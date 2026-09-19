using BoomBust.HealthChecks;
using BoomBust.Logging;
using FantasyProsScrape.Configuration;
using FantasyProsScrape.Jobs;
using FantasyProsScrape.Services;
using FantasyProsScrape.Services.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Quartz;
using Serilog;
using Supabase;

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.UseBoomBustLogging(options =>
    {
        options.ApplicationName = "FantasyProsScrape";
        options.LogFilePath = "logs/fantasy-pros-scrape-.txt";
        options.OverrideToWarning = ["Microsoft", "System", "Quartz"];
    });

    Log.Information("Starting FantasyPros Scrape Service");

    // Configuration: appsettings.json overridden by environment variables (Supabase__Url, Supabase__ServiceRoleKey, ...)
    builder.Services.Configure<SupabaseSettings>(builder.Configuration.GetSection(SupabaseSettings.SectionName));
    builder.Services.Configure<FantasyProsSettings>(builder.Configuration.GetSection(FantasyProsSettings.SectionName));

    // Rankings page client with the same resilience posture as the ESPN and Yahoo scrapers.
    builder.Services.AddHttpClient<IFantasyProsSource, FantasyProsClient>((sp, client) =>
    {
        var settings = sp.GetRequiredService<IOptions<FantasyProsSettings>>().Value;
        client.BaseAddress = new Uri(settings.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", FantasyProsClient.UserAgent);
        client.DefaultRequestHeaders.Add("Accept", "text/html");
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(2);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.OnRetry = args =>
        {
            Log.Warning("Retry attempt {AttemptNumber} for FantasyPros after {Delay}ms delay. Exception: {Exception}",
                args.AttemptNumber, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.Message);
            return default;
        };
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.OnOpened = _ =>
        {
            Log.Error("Circuit breaker opened for FantasyPros. Will retry after 30s");
            return default;
        };
        options.CircuitBreaker.OnClosed = _ =>
        {
            Log.Information("Circuit breaker closed for FantasyPros. Service is healthy again");
            return default;
        };
    });

    // Supabase client (service role so RLS does not block the rank writes).
    // Secrets come only from the environment; refuse to start without them.
    builder.Services.AddSingleton(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SupabaseSettings>>().Value;
        if (string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ServiceRoleKey))
            throw new InvalidOperationException(
                "Supabase configuration is missing: set the Supabase__Url and Supabase__ServiceRoleKey environment variables (see .env.example). Refusing to start.");

        return new Client(settings.Url, settings.ServiceRoleKey, new SupabaseOptions
        {
            AutoConnectRealtime = false,
            AutoRefreshToken = false
        });
    });

    // Repositories PlayerResolver / RankDiffer / FantasyProsRanksJob need to read and write.
    builder.Services.AddScoped<IPlayerRepository, PlayerRepository>();
    builder.Services.AddScoped<IRankRepository, RankRepository>();
    builder.Services.AddScoped<IRunRepository, RunRepository>();

    builder.Services.AddControllers();
    builder.Services.AddHttpClient(); // required by BoomBust.HealthChecks

    // Health checks
    var supabaseUrl = builder.Configuration["Supabase:Url"] ?? string.Empty;
    var supabaseKey = builder.Configuration["Supabase:ServiceRoleKey"] ?? string.Empty;

    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy("Application is running"), tags: ["live"])
        // PostgREST answers GET /rest/v1/ with its OpenAPI document when the apikey is valid.
        .AddApiHealthCheck(
            apiUrl: $"{supabaseUrl.TrimEnd('/')}/rest/v1/",
            configureHttpClient: client =>
            {
                client.DefaultRequestHeaders.Add("apikey", supabaseKey);
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {supabaseKey}");
            },
            name: "supabase",
            healthCheckName: "Supabase REST",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["db", "supabase", "ready"],
            timeout: TimeSpan.FromSeconds(10));

    // Quartz. FantasyProsRanksJob (FEAT-8: resolves, diffs and writes ranks/history/runs) runs
    // hourly Tue-Sat and Sun 00:00-11:00 America/Chicago - the window FantasyPros' week never
    // straddles (it rolls the week after Monday night). SeedWeeklyRanksJob lands in a later ticket.
    var chicago = TimeZoneInfo.FindSystemTimeZoneById(
        builder.Configuration["FantasyPros:TimeZone"] ?? "America/Chicago");

    builder.Services.AddQuartz(q =>
    {
        var ranksJobKey = new JobKey(FantasyProsRanksJob.JobName);
        q.AddJob<FantasyProsRanksJob>(opts => opts
            .WithIdentity(ranksJobKey)
            .DisallowConcurrentExecution()
            .StoreDurably());

        q.AddTrigger(opts => opts
            .ForJob(ranksJobKey)
            .WithIdentity($"{FantasyProsRanksJob.JobName}-tue-sat-trigger")
            .WithCronSchedule("0 0 * ? * TUE-SAT", x => x.InTimeZone(chicago))
            .WithDescription("FantasyPros Ranks - hourly Tue-Sat (America/Chicago)"));

        q.AddTrigger(opts => opts
            .ForJob(ranksJobKey)
            .WithIdentity($"{FantasyProsRanksJob.JobName}-sun-trigger")
            .WithCronSchedule("0 0 0-11 ? * SUN", x => x.InTimeZone(chicago))
            .WithDescription("FantasyPros Ranks - hourly Sun 00:00-11:00 (America/Chicago)"));
    });
    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    var app = builder.Build();

    // Resolving the client here is what makes a missing service-role key fail at startup rather than on first use.
    var supabaseClient = app.Services.GetRequiredService<Client>();
    await supabaseClient.InitializeAsync();
    Log.Information("Supabase client initialised");

    var fantasyProsSettings = app.Services.GetRequiredService<IOptions<FantasyProsSettings>>().Value;

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }

    app.UseRouting();
    app.MapControllers();

    // One JSON shape for every health endpoint, matching the other scrapers.
    static Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            timestamp = DateTime.UtcNow,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = $"{e.Value.Duration.TotalMilliseconds:F2}ms",
                exception = e.Value.Exception?.Message,
                data = e.Value.Data
            }),
            totalDuration = $"{report.TotalDuration.TotalMilliseconds:F2}ms"
        });
    }

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live"),
        AllowCachingResponses = false,
        ResponseWriter = WriteHealthResponse
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        AllowCachingResponses = false,
        ResponseWriter = WriteHealthResponse
    });

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        AllowCachingResponses = false,
        ResponseWriter = WriteHealthResponse
    });

    Log.Information("FantasyPros Scrape Service started. {Scoring} pages {Pages}; cutoffs QB {QB}, RB {RB}, WR {WR}, TE {TE}; schedules in {TimeZone}",
        fantasyProsSettings.Scoring,
        string.Join(", ", fantasyProsSettings.Pages.Select(p => p.Position)),
        fantasyProsSettings.Cutoffs.QB, fantasyProsSettings.Cutoffs.RB, fantasyProsSettings.Cutoffs.WR, fantasyProsSettings.Cutoffs.TE,
        fantasyProsSettings.TimeZone);
    Log.Information("  {Job}: hourly Tue-Sat and Sun 00:00-11:00 America/Chicago (writes ranks/history/runs); POST /api/fantasypros/run to trigger on demand",
        FantasyProsRanksJob.JobName);
    Log.Information("Health check endpoints: /health, /health/live, /health/ready");

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

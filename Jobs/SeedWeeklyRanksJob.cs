using FantasyProsScrape.Configuration;
using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Services;
using Microsoft.Extensions.Options;
using Quartz;

namespace FantasyProsScrape.Jobs;

/// <summary>
/// FEAT-10 (S3): Tuesday-night seed of a <c>WeeklyRankSets</c> row from the current FantasyPros
/// consensus. Algorithm (see docs/plans/fantasy-pros-ranks-scraper.md, "SeedWeeklyRanksJob"):
/// <list type="number">
/// <item>Determine season/week from the most recent <c>FantasyProsRankRuns</c> row, unless overridden
/// via the job data map.</item>
/// <item>If a <c>WeeklyRankSets</c> row already exists for that season/week, log and exit - never
/// touch it.</item>
/// <item>Otherwise insert the set (<c>seeded_at</c> = now, <c>published_at</c> = null).</item>
/// <item>For each configured position, read the top-N current <c>FantasyProsRanks</c> rows ordered by
/// <c>rank_ecr</c> (N = the position's cutoff) and insert <c>WeeklyRanks</c> rows with
/// <c>rank</c> = 1..N in that order; <c>seeded_rank</c> is the same value as <c>rank</c> at seed time,
/// since these rows are created directly from the current ranked order.</item>
/// <item>Log one summary line with the count seeded per position.</item>
/// </list>
/// The admin page in Boom takes it from there (see boom/docs/plans/weekly-ranks.md).
/// </summary>
[DisallowConcurrentExecution]
public class SeedWeeklyRanksJob : IJob
{
    public const string JobName = "SeedWeeklyRanksJob";

    /// <summary>Optional int override in the job data map; forces the season instead of the most recent run's.</summary>
    public const string SeasonDataKey = "season";

    /// <summary>Optional int override in the job data map; forces the week instead of the most recent run's.</summary>
    public const string WeekDataKey = "week";

    private readonly IRunRepository _runs;
    private readonly IRankRepository _ranks;
    private readonly IWeeklyRankRepository _weeklyRanks;
    private readonly IPlayerRepository _players;
    private readonly FantasyProsSettings _settings;
    private readonly ILogger<SeedWeeklyRanksJob> _logger;

    public SeedWeeklyRanksJob(
        IRunRepository runs,
        IRankRepository ranks,
        IWeeklyRankRepository weeklyRanks,
        IPlayerRepository players,
        IOptions<FantasyProsSettings> settings,
        ILogger<SeedWeeklyRanksJob> logger)
    {
        _runs = runs;
        _ranks = ranks;
        _weeklyRanks = weeklyRanks;
        _players = players;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var dataMap = context.MergedJobDataMap;

        var seasonOverride = GetIntOverride(dataMap, SeasonDataKey);
        var weekOverride = GetIntOverride(dataMap, WeekDataKey);

        int season, week;
        if (seasonOverride.HasValue && weekOverride.HasValue)
        {
            season = seasonOverride.Value;
            week = weekOverride.Value;
        }
        else
        {
            var latest = await _runs.GetMostRecentSeasonWeekAsync(cancellationToken);
            if (latest is null)
            {
                _logger.LogWarning(
                    "No FantasyProsRankRuns row exists yet and no season/week override was given; nothing to seed");
                return;
            }

            season = seasonOverride ?? latest.Value.Season;
            week = weekOverride ?? latest.Value.Week;
        }

        if (await _weeklyRanks.SetExistsAsync(season, week, cancellationToken))
        {
            _logger.LogInformation(
                "WeeklyRankSets row already exists for {Season} wk{Week}; leaving it untouched", season, week);
            return;
        }

        var resolution = await _players.GetSkillPlayersAsync(cancellationToken);

        var set = await _weeklyRanks.InsertSetAsync(season, week, cancellationToken);

        var summary = new List<(string Position, int Count)>();
        var hadFailure = false;

        foreach (var page in _settings.Pages)
        {
            try
            {
                var count = await SeedPositionAsync(set, page.Position, resolution, season, week, cancellationToken);
                summary.Add((page.Position, count));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                hadFailure = true;
                _logger.LogError(ex, "Failed seeding WeeklyRanks for {Position} in set {SetId}", page.Position, set.Id);
            }
        }

        var summaryText = string.Join(", ", summary.Select(s => $"{s.Position} {s.Count}"));
        _logger.LogInformation("Seeded week {Week}: {Summary}", week, summaryText);

        if (hadFailure)
        {
            throw new JobExecutionException(
                $"One or more positions failed to seed for set {set.Id}; see preceding errors for {JobName}.")
            {
                RefireImmediately = false
            };
        }
    }

    private async Task<int> SeedPositionAsync(
        WeeklyRankSet set, string position, PlayerResolutionData resolution, int season, int week, CancellationToken cancellationToken)
    {
        if (!resolution.PositionIdByName.TryGetValue(position, out var positionId))
        {
            _logger.LogError("Positions table has no row for {Position}; skipping this position", position);
            return 0;
        }

        var cutoff = _settings.Cutoffs.For(position);
        var topRanks = await _ranks.GetTopRanksAsync(season, week, positionId, _settings.Scoring, cutoff, cancellationToken);

        var now = DateTime.UtcNow;
        var rows = topRanks
            .Select((rank, index) => new WeeklyRank
            {
                SetId = set.Id,
                PositionId = positionId,
                PlayerId = rank.PlayerId,
                Rank = index + 1,
                SeededRank = index + 1,
                UpdatedAt = now
            })
            .ToList();

        await _weeklyRanks.InsertRanksAsync(rows, cancellationToken);
        return rows.Count;
    }

    private static int? GetIntOverride(JobDataMap dataMap, string key) =>
        dataMap.ContainsKey(key) ? dataMap.GetIntValue(key) : null;
}

using Supabase.Postgrest;
using FantasyProsScrape.Models.Supa;
using Client = Supabase.Client;

namespace FantasyProsScrape.Services.Repositories;

/// <summary>
/// Reads and writes <c>WeeklyRankSets</c> and <c>WeeklyRanks</c>. See <see cref="IWeeklyRankRepository"/>.
/// </summary>
public class WeeklyRankRepository : IWeeklyRankRepository
{
    private readonly Client _supabase;
    private readonly ILogger<WeeklyRankRepository> _logger;

    public WeeklyRankRepository(Client supabase, ILogger<WeeklyRankRepository> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<bool> SetExistsAsync(int season, int week, CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<WeeklyRankSet>()
            .Select("id")
            .Filter("season", Constants.Operator.Equals, season)
            .Filter("week", Constants.Operator.Equals, week)
            .Limit(1)
            .Get(cancellationToken);

        return response.Models.Count > 0;
    }

    public async Task<WeeklyRankSet> InsertSetAsync(int season, int week, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var set = new WeeklyRankSet
        {
            Season = season,
            Week = week,
            SeededAt = now,
            PublishedAt = null,
            UpdatedAt = now
        };

        var response = await _supabase.From<WeeklyRankSet>().Insert(set, cancellationToken: cancellationToken);
        var inserted = response.Models.Single();

        _logger.LogDebug("Inserted WeeklyRankSets row {Id} for {Season} wk{Week}", inserted.Id, season, week);
        return inserted;
    }

    public async Task InsertRanksAsync(IReadOnlyList<WeeklyRank> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        await _supabase.From<WeeklyRank>().Insert(rows.ToList(), cancellationToken: cancellationToken);
        _logger.LogDebug("Inserted {Count} WeeklyRanks rows", rows.Count);
    }
}

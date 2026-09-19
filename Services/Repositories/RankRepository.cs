using Supabase.Postgrest;
using FantasyProsScrape.Models.Supa;
using Client = Supabase.Client;

namespace FantasyProsScrape.Services.Repositories;

/// <summary>
/// Reads and writes <c>FantasyProsRanks</c> and <c>FantasyProsRankHistory</c>. See
/// <see cref="IRankRepository"/>.
/// </summary>
public class RankRepository : IRankRepository
{
    /// <summary>The natural key the ranks upsert resolves conflicts on.</summary>
    private const string RanksConflictColumns = "season,week,position_id,scoring,player_id";

    private readonly Client _supabase;
    private readonly ILogger<RankRepository> _logger;

    public RankRepository(Client supabase, ILogger<RankRepository> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<List<FantasyProsRank>> GetExistingRanksAsync(
        int season, int week, long positionId, string scoring, CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<FantasyProsRank>()
            .Filter("season", Constants.Operator.Equals, season)
            .Filter("week", Constants.Operator.Equals, week)
            // Postgrest's Filter(...) only accepts string/int/float/List/Dictionary/FullTextSearchConfig/Range
            // criterion types (not long) - cast down; position ids are always small (1-5).
            .Filter("position_id", Constants.Operator.Equals, (int)positionId)
            .Filter("scoring", Constants.Operator.Equals, scoring)
            .Get(cancellationToken);

        _logger.LogDebug(
            "Loaded {Count} existing FantasyProsRanks rows for {Season} wk{Week} pos {PositionId} {Scoring}",
            response.Models.Count, season, week, positionId, scoring);

        return response.Models;
    }

    public async Task<List<FantasyProsRank>> GetTopRanksAsync(
        int season, int week, long positionId, string scoring, int limit, CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<FantasyProsRank>()
            .Filter("season", Constants.Operator.Equals, season)
            .Filter("week", Constants.Operator.Equals, week)
            // Postgrest's Filter(...) only accepts string/int/float/List/Dictionary/FullTextSearchConfig/Range
            // criterion types (not long) - cast down; position ids are always small (1-5).
            .Filter("position_id", Constants.Operator.Equals, (int)positionId)
            .Filter("scoring", Constants.Operator.Equals, scoring)
            .Order("rank_ecr", Constants.Ordering.Ascending)
            .Limit(limit)
            .Get(cancellationToken);

        _logger.LogDebug(
            "Loaded top {Count} FantasyProsRanks rows (limit {Limit}) for {Season} wk{Week} pos {PositionId} {Scoring}",
            response.Models.Count, limit, season, week, positionId, scoring);

        return response.Models;
    }

    public async Task<List<FantasyProsRank>> UpsertRanksAsync(
        IReadOnlyList<FantasyProsRank> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return [];

        var response = await _supabase
            .From<FantasyProsRank>()
            .Upsert(rows.ToList(), new QueryOptions { OnConflict = RanksConflictColumns }, cancellationToken);

        _logger.LogDebug("Upserted {Count} FantasyProsRanks rows", response.Models.Count);
        return response.Models;
    }

    public async Task InsertHistoryAsync(
        IReadOnlyList<FantasyProsRankHistory> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        await _supabase.From<FantasyProsRankHistory>().Insert(rows.ToList(), cancellationToken: cancellationToken);
        _logger.LogDebug("Inserted {Count} FantasyProsRankHistory rows", rows.Count);
    }
}

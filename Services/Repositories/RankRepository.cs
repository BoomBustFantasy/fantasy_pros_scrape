using System.Text.Json;
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

    /// <summary>Row count per raw-write HTTP request, matching fantasy_calc_scrape's chunk size.</summary>
    private const int WriteBatchSize = 200;

    private readonly Client _supabase;
    private readonly IPostgrestRawWriter _rawWriter;
    private readonly ILogger<RankRepository> _logger;

    public RankRepository(Client supabase, IPostgrestRawWriter rawWriter, ILogger<RankRepository> logger)
    {
        _supabase = supabase;
        _rawWriter = rawWriter;
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

    /// <summary>
    /// Upserts via <see cref="IPostgrestRawWriter"/> rather than the typed client's
    /// <c>.Upsert(List&lt;T&gt;, ...)</c>: that method serializes a literal <c>id: 0</c> for every
    /// brand-new row (<see cref="RankDiffer.Diff"/> sets <c>Id = 0</c> on them), and two or more new
    /// rows in the same batch then collide on the real primary key within a single INSERT statement.
    /// The payload built here omits <c>id</c> entirely so PostgREST assigns a fresh one per row.
    /// <c>return=representation</c> is required: callers need the written rows' real ids for
    /// <see cref="RankDiffer.AttachRankIds"/>.
    /// </summary>
    public async Task<List<FantasyProsRank>> UpsertRanksAsync(
        IReadOnlyList<FantasyProsRank> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return [];

        var upserted = new List<FantasyProsRank>(rows.Count);

        foreach (var batch in rows.Chunk(WriteBatchSize))
        {
            var payload = batch.Select(r => new
            {
                season = r.Season,
                week = r.Week,
                position_id = r.PositionId,
                scoring = r.Scoring,
                player_id = r.PlayerId,
                fantasy_pros_player_id = r.FantasyProsPlayerId,
                rank_ecr = r.RankEcr,
                rank_ave = r.RankAve,
                rank_min = r.RankMin,
                rank_max = r.RankMax,
                rank_std = r.RankStd,
                pos_rank = r.PosRank,
                start_sit_grade = r.StartSitGrade,
                opponent = r.Opponent,
                opponent_abbreviation = r.OpponentAbbreviation,
                first_seen_at = r.FirstSeenAt,
                updated_at = r.UpdatedAt
            });

            var json = JsonSerializer.Serialize(payload);
            var response = await _rawWriter.PostAsync(
                "FantasyProsRanks", RanksConflictColumns, json,
                "resolution=merge-duplicates,return=representation", cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var batchResult = JsonSerializer.Deserialize<List<FantasyProsRank>>(body) ?? [];
            upserted.AddRange(batchResult);
        }

        _logger.LogDebug("Upserted {Count} FantasyProsRanks rows", upserted.Count);
        return upserted;
    }

    /// <summary>
    /// Inserts via <see cref="IPostgrestRawWriter"/> rather than the typed client's
    /// <c>.Insert(List&lt;T&gt;, ...)</c> for the same <c>id: 0</c> serialization reason as
    /// <see cref="UpsertRanksAsync"/> - a plain insert here, so no <c>on_conflict</c>.
    /// </summary>
    public async Task InsertHistoryAsync(
        IReadOnlyList<FantasyProsRankHistory> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        foreach (var batch in rows.Chunk(WriteBatchSize))
        {
            var payload = batch.Select(r => new
            {
                rank_id = r.RankId,
                season = r.Season,
                week = r.Week,
                position_id = r.PositionId,
                player_id = r.PlayerId,
                previous_rank_ecr = r.PreviousRankEcr,
                new_rank_ecr = r.NewRankEcr,
                changed_at = r.ChangedAt
            });

            var json = JsonSerializer.Serialize(payload);
            var response = await _rawWriter.PostAsync(
                "FantasyProsRankHistory", onConflict: null, json, "return=minimal", cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogDebug("Inserted {Count} FantasyProsRankHistory rows", rows.Count);
    }
}

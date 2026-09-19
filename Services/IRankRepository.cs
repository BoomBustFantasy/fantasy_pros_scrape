using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// Reads and writes <c>FantasyProsRanks</c> and <c>FantasyProsRankHistory</c>.
/// <see cref="GetExistingRanksAsync"/> is what <see cref="RankDiffer.Diff"/> diffs against;
/// <see cref="UpsertRanksAsync"/> and <see cref="InsertHistoryAsync"/> write its result. See
/// docs/plans/fantasy-pros-ranks-scraper.md, "FantasyProsRanksJob" step 6 ("Write").
/// </summary>
public interface IRankRepository
{
    /// <summary>Every current row for one season/week/position/scoring.</summary>
    Task<List<FantasyProsRank>> GetExistingRanksAsync(
        int season, int week, long positionId, string scoring, CancellationToken cancellationToken = default);

    /// <summary>
    /// The top <paramref name="limit"/> current rows for one season/week/position/scoring, ordered
    /// by <c>rank_ecr</c> ascending (best consensus rank first). Used by
    /// <see cref="Jobs.SeedWeeklyRanksJob"/> to seed a <c>WeeklyRanks</c> set from the current
    /// consensus order - a different read than <see cref="GetExistingRanksAsync"/>, which loads
    /// every row for diffing rather than the best N for seeding.
    /// </summary>
    Task<List<FantasyProsRank>> GetTopRanksAsync(
        int season, int week, long positionId, string scoring, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts <paramref name="rows"/> in one call on the natural key
    /// (season, week, position_id, scoring, player_id). Returns the written rows, ids included -
    /// new rows need theirs before <see cref="RankDiffer.AttachRankIds"/> can fill in their history
    /// rows' <c>rank_id</c>.
    /// </summary>
    Task<List<FantasyProsRank>> UpsertRanksAsync(
        IReadOnlyList<FantasyProsRank> rows, CancellationToken cancellationToken = default);

    /// <summary>Bulk-inserts <c>FantasyProsRankHistory</c> rows in one call. Append-only.</summary>
    Task InsertHistoryAsync(
        IReadOnlyList<FantasyProsRankHistory> rows, CancellationToken cancellationToken = default);
}

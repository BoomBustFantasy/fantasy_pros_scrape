using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// Reads and writes <c>WeeklyRankSets</c> and <c>WeeklyRanks</c>. See
/// docs/plans/fantasy-pros-ranks-scraper.md, "SeedWeeklyRanksJob", and the companion plan
/// boom/docs/plans/weekly-ranks.md. The seed job checks <see cref="SetExistsAsync"/> before ever
/// calling <see cref="InsertSetAsync"/> or <see cref="InsertRanksAsync"/> - an existing set is never
/// touched, so there is deliberately no update/overwrite method here.
/// </summary>
public interface IWeeklyRankRepository
{
    /// <summary>Whether a <c>WeeklyRankSets</c> row already exists for this season/week. Read-only.</summary>
    Task<bool> SetExistsAsync(int season, int week, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new <c>WeeklyRankSets</c> row (<c>seeded_at</c> = now, <c>published_at</c> = null)
    /// and returns it with its generated id, so the caller can attach <c>WeeklyRanks</c> rows to it.
    /// </summary>
    Task<WeeklyRankSet> InsertSetAsync(int season, int week, CancellationToken cancellationToken = default);

    /// <summary>Bulk-inserts <c>WeeklyRanks</c> rows for one set/position.</summary>
    Task InsertRanksAsync(IReadOnlyList<WeeklyRank> rows, CancellationToken cancellationToken = default);
}

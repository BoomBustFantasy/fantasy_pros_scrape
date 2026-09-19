using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// Reads current <c>FantasyProsRanks</c> rows so <see cref="RankDiffer.Diff"/> has something to
/// diff against. Exists and is registered in DI for FEAT-8 ("enable writes"); the FEAT-7 dry-run
/// job does not call it. It intentionally carries no write method - see the ticket's HITL note:
/// FEAT-7 must not touch the database, and there is no production service-role key in this
/// environment to read with even if it wanted to.
/// </summary>
public interface IRankRepository
{
    /// <summary>Every current row for one season/week/position/scoring.</summary>
    Task<List<FantasyProsRank>> GetExistingRanksAsync(
        int season, int week, long positionId, string scoring, CancellationToken cancellationToken = default);
}

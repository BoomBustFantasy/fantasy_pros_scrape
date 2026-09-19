using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// FantasyProsRankRuns: the raw <c>ecrData</c> payload for one page fetch, stored only when the
/// <c>players</c> array hash (<see cref="PayloadHasher"/>) differs from the latest stored run for
/// the same season/week/position/scoring. See docs/plans/fantasy-pros-ranks-scraper.md,
/// "FantasyProsRanksJob" step 3 ("Run capture").
/// </summary>
public interface IRunRepository
{
    /// <summary>
    /// The <c>payload_hash</c> of the most recently stored run for this season/week/position/scoring,
    /// or null if no run has been stored yet.
    /// </summary>
    Task<string?> GetLatestPayloadHashAsync(
        int season, int week, long positionId, string scoring, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a run row. The unique constraint on (season, week, position_id, scoring,
    /// payload_hash) is the race backstop for two concurrent job runs computing the same new hash:
    /// a duplicate-key failure here means another run already stored this exact payload, not that
    /// this call failed. Returns true when this call's row is the one now stored, false when a
    /// duplicate-key conflict means someone else's row already is.
    /// </summary>
    Task<bool> InsertRunAsync(FantasyProsRankRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// The (season, week) of the single most recent <c>FantasyProsRankRuns</c> row across all
    /// positions - i.e. whatever FantasyPros is currently serving - or null if no run has ever been
    /// stored. Used by <see cref="Jobs.SeedWeeklyRanksJob"/> to default the week it seeds when not
    /// overridden. This is a plain "most recent row" read, unrelated to the payload-hash check
    /// <see cref="GetLatestPayloadHashAsync"/> does for one season/week/position/scoring.
    /// </summary>
    Task<(int Season, int Week)?> GetMostRecentSeasonWeekAsync(CancellationToken cancellationToken = default);
}

using Supabase.Postgrest;
using Supabase.Postgrest.Exceptions;
using FantasyProsScrape.Models.Supa;
using Client = Supabase.Client;

namespace FantasyProsScrape.Services.Repositories;

/// <summary>
/// Reads and writes <c>FantasyProsRankRuns</c>. See <see cref="IRunRepository"/>.
/// </summary>
public class RunRepository : IRunRepository
{
    private readonly Client _supabase;
    private readonly ILogger<RunRepository> _logger;

    public RunRepository(Client supabase, ILogger<RunRepository> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<string?> GetLatestPayloadHashAsync(
        int season, int week, long positionId, string scoring, CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<FantasyProsRankRun>()
            .Select("payload_hash")
            .Filter("season", Constants.Operator.Equals, season)
            .Filter("week", Constants.Operator.Equals, week)
            // Postgrest's Filter(...) only accepts string/int/float/List/Dictionary/FullTextSearchConfig/Range
            // criterion types (not long) - cast down; position ids are always small (1-5).
            .Filter("position_id", Constants.Operator.Equals, (int)positionId)
            .Filter("scoring", Constants.Operator.Equals, scoring)
            .Order("fetched_at", Constants.Ordering.Descending)
            .Limit(1)
            .Get(cancellationToken);

        return response.Models.FirstOrDefault()?.PayloadHash;
    }

    public async Task<bool> InsertRunAsync(FantasyProsRankRun run, CancellationToken cancellationToken = default)
    {
        try
        {
            await _supabase.From<FantasyProsRankRun>().Insert(run, cancellationToken: cancellationToken);
            return true;
        }
        catch (PostgrestException ex) when (ex.Reason == FailureHint.Reason.UniquenessViolation)
        {
            // Someone else's concurrent run already stored this exact (season, week, position_id,
            // scoring, payload_hash) - the unique constraint is the race backstop for the hash
            // check in GetLatestPayloadHashAsync. Not a failure.
            _logger.LogInformation(
                "FantasyProsRankRuns row for {Season} wk{Week} pos {PositionId} {Scoring} hash {Hash} already stored by a concurrent run; skipping",
                run.Season, run.Week, run.PositionId, run.Scoring, run.PayloadHash);
            return false;
        }
    }

    public async Task<(int Season, int Week)?> GetMostRecentSeasonWeekAsync(CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<FantasyProsRankRun>()
            .Select("season,week")
            .Order("fetched_at", Constants.Ordering.Descending)
            .Limit(1)
            .Get(cancellationToken);

        var latest = response.Models.FirstOrDefault();
        return latest is null ? null : (latest.Season, latest.Week);
    }
}

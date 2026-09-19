using FantasyProsScrape.Configuration;
using FantasyProsScrape.Models.Ecr;
using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Services;
using FantasyProsScrape.Utils;
using Microsoft.Extensions.Options;
using Quartz;

namespace FantasyProsScrape.Jobs;

/// <summary>
/// FEAT-8 (S2): fetches the four FantasyPros rankings pages, parses them, and per position runs the
/// full read-diff-write sequence: stores the raw payload only when its players-array hash differs
/// from the latest stored run; loads the current FantasyProsRanks rows for the
/// season/week/position/scoring; resolves every player inside the configured cutoff plus any player
/// already tracked this week even if it has since dropped below the cutoff; upserts new/changed/
/// touched rows in one call; bulk-inserts history rows for new and changed rows; and patches
/// Players.fantasy_pros_player_id when resolution succeeded through a name match rather than the
/// FantasyPros id fast path. Logs one summary line per position. See
/// docs/plans/fantasy-pros-ranks-scraper.md, "FantasyProsRanksJob" and milestone S2.
/// </summary>
[DisallowConcurrentExecution]
public class FantasyProsRanksJob : IJob
{
    public const string JobName = "FantasyProsRanksJob";

    /// <summary>Optional int override in the job data map; forces the season instead of the value FantasyPros reports.</summary>
    public const string SeasonDataKey = "season";

    /// <summary>Optional int override in the job data map; forces the week instead of the value FantasyPros reports.</summary>
    public const string WeekDataKey = "week";

    /// <summary>Optional comma-separated position list in the job data map, e.g. "RB,WR"; restricts which pages run.</summary>
    public const string PositionsDataKey = "positions";

    /// <summary>Spacing between the four page fetches, so the scraper does not hit FantasyPros in a burst.</summary>
    private static readonly TimeSpan PageFetchDelay = TimeSpan.FromMilliseconds(150);

    private readonly IFantasyProsSource _source;
    private readonly IPlayerRepository _players;
    private readonly IRankRepository _ranks;
    private readonly IRunRepository _runs;
    private readonly FantasyProsSettings _settings;
    private readonly ILogger<FantasyProsRanksJob> _logger;

    public FantasyProsRanksJob(
        IFantasyProsSource source,
        IPlayerRepository players,
        IRankRepository ranks,
        IRunRepository runs,
        IOptions<FantasyProsSettings> settings,
        ILogger<FantasyProsRanksJob> logger)
    {
        _source = source;
        _players = players;
        _ranks = ranks;
        _runs = runs;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var dataMap = context.MergedJobDataMap;

        var seasonOverride = GetIntOverride(dataMap, SeasonDataKey);
        var weekOverride = GetIntOverride(dataMap, WeekDataKey);
        var positionsOverride = GetPositionsOverride(dataMap);

        var pages = positionsOverride is null
            ? _settings.Pages
            : _settings.Pages.Where(p => positionsOverride.Contains(p.Position)).ToList();

        if (pages.Count == 0)
        {
            _logger.LogWarning("No FantasyPros pages configured or selected (positions override: {Positions}); nothing to do",
                positionsOverride is null ? "<none>" : string.Join(",", positionsOverride));
            return;
        }

        _logger.LogInformation(
            "Starting FantasyPros ranks run (resolve + diff + write) for {Pages}",
            string.Join(", ", pages.Select(p => p.Position)));

        PlayerResolutionData resolution;
        try
        {
            resolution = await _players.GetSkillPlayersAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Players/Positions/Teams for resolution; aborting the run");
            throw new JobExecutionException(ex, refireImmediately: false);
        }

        var unknownTeamCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hadFailure = false;

        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
                await Task.Delay(PageFetchDelay, cancellationToken);

            var page = pages[i];
            try
            {
                await ProcessPageAsync(page, resolution, seasonOverride, weekOverride, unknownTeamCodes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                hadFailure = true;
                _logger.LogError(ex, "Failed processing the FantasyPros {Position} page", page.Position);
            }
        }

        _logger.LogInformation("Completed FantasyPros ranks run");

        if (hadFailure)
        {
            throw new JobExecutionException(
                $"One or more FantasyPros pages failed; see preceding errors for {JobName}.")
            {
                RefireImmediately = false
            };
        }
    }

    private async Task ProcessPageAsync(
        FantasyProsPage page,
        PlayerResolutionData resolution,
        int? seasonOverride,
        int? weekOverride,
        HashSet<string> unknownTeamCodes,
        CancellationToken cancellationToken)
    {
        if (!resolution.PositionIdByName.TryGetValue(page.Position, out var positionId))
        {
            _logger.LogError("Positions table has no row for {Position}; skipping this page", page.Position);
            return;
        }

        var html = await _source.GetPageAsync(page.Path, cancellationToken);
        var ecrPage = EcrPageParser.Parse(html);

        var season = seasonOverride ?? ecrPage.Year;
        var week = weekOverride ?? ecrPage.Week;
        var cutoff = _settings.Cutoffs.For(page.Position);
        var scoring = _settings.Scoring;

        await CaptureRunAsync(ecrPage, season, week, positionId, scoring, cancellationToken);

        var existingRows = await _ranks.GetExistingRanksAsync(season, week, positionId, scoring, cancellationToken);

        // A player already tracked this season/week/position keeps updating even if their ECR has
        // since dropped below the cutoff, so they don't freeze at their last-seen rank.
        var alreadyTrackedFpIds = new HashSet<int>(existingRows.Select(r => r.FantasyProsPlayerId));

        var playersById = resolution.Players.ToDictionary(p => p.Id);

        var ranked = 0;
        var resolved = new List<ResolvedRank>();
        var unresolved = 0;

        foreach (var fp in ecrPage.Players)
        {
            if (fp.RankEcr > cutoff && !alreadyTrackedFpIds.Contains(fp.PlayerId))
                continue;

            ranked++;

            var teamAbbreviation = TeamAbbreviations.ToSupabaseAbbreviation(fp.PlayerTeamId);
            if (teamAbbreviation is null)
            {
                if (unknownTeamCodes.Add(fp.PlayerTeamId))
                    _logger.LogWarning("Unrecognised FantasyPros team code {Team}; falling back to name-only matching", fp.PlayerTeamId);
            }

            long? supabaseTeamId = teamAbbreviation is not null && resolution.TeamIdByAbbreviation.TryGetValue(teamAbbreviation, out var teamId)
                ? teamId
                : null;

            var playerId = PlayerResolver.Resolve(resolution.Players, fp.PlayerId, fp.PlayerName, supabaseTeamId, positionId);

            if (playerId is null)
            {
                unresolved++;
                _logger.LogWarning(
                    "Unresolved FantasyPros player {Name} ({FpId}, {Team} {Position}, ECR {Rank})",
                    fp.PlayerName, fp.PlayerId, fp.PlayerTeamId, page.Position, fp.RankEcr);
                continue;
            }

            resolved.Add(new ResolvedRank(
                PlayerId: playerId.Value,
                FantasyProsPlayerId: fp.PlayerId,
                RankEcr: fp.RankEcr,
                RankAve: fp.RankAve,
                RankMin: fp.RankMin,
                RankMax: fp.RankMax,
                RankStd: fp.RankStd,
                PosRank: fp.PosRank,
                StartSitGrade: fp.StartSitGrade,
                Opponent: fp.PlayerOpponent,
                OpponentAbbreviation: fp.PlayerOpponentId));

            // The FantasyPros id fast path in PlayerResolver only succeeds when the row's
            // fantasy_pros_player_id already equals fp.PlayerId, so a resolved player whose loaded
            // value is null or different got here through a name match - backfill it. Do not patch
            // players who already had this id: nothing changed for them.
            if (playersById.TryGetValue(playerId.Value, out var matchedPlayer) && matchedPlayer.FantasyProsPlayerId != fp.PlayerId)
                await _players.PatchFantasyProsPlayerIdAsync(playerId.Value, fp.PlayerId, cancellationToken);
        }

        var diff = RankDiffer.Diff(
            season, week, positionId, scoring,
            existing: existingRows,
            scraped: resolved,
            now: DateTime.UtcNow);

        await WriteDiffAsync(diff, cancellationToken);

        _logger.LogInformation(
            "{Position}: {Ranked} ranked, {Resolved} resolved, {New} new, {Moved} moved, {Unresolved} unresolved",
            page.Position, ranked, resolved.Count, diff.NewCount, diff.ChangedCount, unresolved);
    }

    /// <summary>
    /// Stores the raw payload only when its players-array hash differs from the latest stored run
    /// for this season/week/position/scoring. A duplicate-key failure on insert (the unique
    /// constraint backstopping a race between two concurrent runs computing the same new hash) is
    /// handled inside <see cref="IRunRepository.InsertRunAsync"/> and is not a failure here.
    /// </summary>
    private async Task CaptureRunAsync(
        EcrPage ecrPage, int season, int week, long positionId, string scoring, CancellationToken cancellationToken)
    {
        var payloadHash = PayloadHasher.Hash(ecrPage.PlayersRawJson);
        var latestHash = await _runs.GetLatestPayloadHashAsync(season, week, positionId, scoring, cancellationToken);

        if (string.Equals(latestHash, payloadHash, StringComparison.Ordinal))
            return;

        var run = new FantasyProsRankRun
        {
            Season = season,
            Week = week,
            PositionId = positionId,
            Scoring = scoring,
            PayloadHash = payloadHash,
            Payload = ecrPage.RawJson,
            TotalExperts = ecrPage.TotalExperts,
            FpLastUpdated = ecrPage.LastUpdated,
            FetchedAt = DateTime.UtcNow
        };

        await _runs.InsertRunAsync(run, cancellationToken);
    }

    /// <summary>
    /// Upserts new/changed/touched rows, then inserts history for new/changed rows once the upsert
    /// response has filled in the ids of brand-new rows via <see cref="RankDiffer.AttachRankIds"/>.
    /// </summary>
    private async Task WriteDiffAsync(RankDiffResult diff, CancellationToken cancellationToken)
    {
        if (diff.Upserts.Count == 0)
            return;

        var upserted = await _ranks.UpsertRanksAsync(diff.Upserts, cancellationToken);

        if (diff.HistoryRows.Count == 0)
            return;

        RankDiffer.AttachRankIds(diff.HistoryRows, upserted);
        await _ranks.InsertHistoryAsync(diff.HistoryRows, cancellationToken);
    }

    private static int? GetIntOverride(JobDataMap dataMap, string key) =>
        dataMap.ContainsKey(key) ? dataMap.GetIntValue(key) : null;

    private static HashSet<string>? GetPositionsOverride(JobDataMap dataMap)
    {
        if (!dataMap.ContainsKey(PositionsDataKey))
            return null;

        var raw = dataMap.GetString(PositionsDataKey);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var positions = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return positions.Length == 0 ? null : new HashSet<string>(positions, StringComparer.OrdinalIgnoreCase);
    }
}

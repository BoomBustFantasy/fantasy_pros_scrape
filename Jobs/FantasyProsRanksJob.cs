using FantasyProsScrape.Configuration;
using FantasyProsScrape.Models.Ecr;
using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Services;
using FantasyProsScrape.Utils;
using Microsoft.Extensions.Options;
using Quartz;

namespace FantasyProsScrape.Jobs;

/// <summary>
/// FEAT-7 (S1): fetches the four FantasyPros rankings pages, parses them, resolves every player
/// inside the configured cutoff against the Players table, diffs against an empty current set,
/// and logs one summary line per position. Writes nothing to the database - no repository this
/// job depends on exposes a write method. See docs/plans/fantasy-pros-ranks-scraper.md,
/// "FantasyProsRanksJob" and milestone S1.
///
/// Writes (ranks/history/run upserts, the fantasy_pros_player_id backfill) land in FEAT-8 (S2).
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
    private readonly FantasyProsSettings _settings;
    private readonly ILogger<FantasyProsRanksJob> _logger;

    public FantasyProsRanksJob(
        IFantasyProsSource source,
        IPlayerRepository players,
        IOptions<FantasyProsSettings> settings,
        ILogger<FantasyProsRanksJob> logger)
    {
        _source = source;
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
            "Starting FantasyPros ranks dry run (S1: resolve + diff + log, write nothing) for {Pages}",
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

        _logger.LogInformation("Completed FantasyPros ranks dry run");

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

        var ranked = 0;
        var resolved = new List<ResolvedRank>();
        var unresolved = 0;

        foreach (var fp in ecrPage.Players)
        {
            // Dry run: the "existing" set RankDiffer diffs against is always empty (see class docs), so
            // there is no already-tracked player below the cutoff to keep updating this ticket.
            if (fp.RankEcr > cutoff)
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
        }

        var diff = RankDiffer.Diff(
            season, week, positionId, _settings.Scoring,
            existing: [],
            scraped: resolved,
            now: DateTime.UtcNow);

        _logger.LogInformation(
            "{Position}: {Ranked} ranked, {Resolved} resolved, {New} new, {Moved} moved, {Unresolved} unresolved",
            page.Position, ranked, resolved.Count, diff.NewCount, diff.ChangedCount, unresolved);
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

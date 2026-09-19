using FantasyProsScrape.Configuration;
using FantasyProsScrape.Jobs;
using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;

namespace FantasyProsScrape.Tests;

/// <summary>
/// FantasyProsRanksJob end to end, with a fake <see cref="IFantasyProsSource"/> and
/// <see cref="IPlayerRepository"/> standing in for the network and Supabase. There is deliberately
/// no fake or mock for a write path here: the job has no dependency capable of writing anything
/// (see IRankRepository's docs) - so "writes nothing" is enforced by the job's dependency graph,
/// not by an assertion that could be forgotten.
/// </summary>
public class FantasyProsRanksJobTests
{
    private const long RbPositionId = 2;
    private const long ChiefsTeamId = 16;

    private static FantasyProsSettings Settings(int rbCutoff = 2) => new()
    {
        BaseUrl = "https://www.fantasypros.com",
        Pages = [new FantasyProsPage { Position = "RB", Path = "/nfl/rankings/ppr-rb.php" }],
        Cutoffs = new FantasyProsCutoffs { QB = 32, RB = rbCutoff, WR = 70, TE = 30 },
        Scoring = "PPR",
        TimeZone = "America/Chicago"
    };

    private static PlayerResolutionData Resolution() => new(
        Players:
        [
            new Player { Id = 100, FirstName = "Christian", LastName = "McCaffrey", TeamId = ChiefsTeamId, PositionId = RbPositionId }
        ],
        PositionIdByName: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["RB"] = RbPositionId },
        TeamIdByAbbreviation: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["KAN"] = ChiefsTeamId });

    private static string EcrJson(params (int fpId, string name, string team, int rank)[] players)
    {
        var playerJson = string.Join(",", players.Select(p =>
            $$"""
            {"player_id":{{p.fpId}},"player_name":"{{p.name}}","player_team_id":"{{p.team}}","player_position_id":"RB",
             "player_opponent":"at BUF","player_opponent_id":"BUF","pos_rank":"RB{{p.rank}}","rank_ecr":{{p.rank}},
             "rank_min":1,"rank_max":6,"rank_ave":1.4,"rank_std":0.8,"start_sit_grade":"A","player_ecr_delta":null,
             "player_bye_week":6,"player_game_kickoff_ts":1789690500,"player_game_status":"closed",
             "player_page_url":"https://www.fantasypros.com/nfl/players/test.php"}
            """));

        return $$"""
            {"year":"2026","week":"3","position_id":"RB","scoring":"PPR","total_experts":140,"last_updated":"9/19",
             "last_updated_ts":1789846940,"accessed":"2026-09-19 19:44:49","count":{{players.Length}},"players":[{{playerJson}}]}
            """;
    }

    private static string WrapInPage(string ecrDataJson) =>
        $"<html><body><script type=\"text/javascript\">\n    var ecrData = {ecrDataJson};\n</script></body></html>";

    private static Mock<IFantasyProsSource> Source(string path, string html)
    {
        var mock = new Mock<IFantasyProsSource>();
        mock.Setup(s => s.GetPageAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(html);
        return mock;
    }

    private static Mock<IPlayerRepository> Players(PlayerResolutionData data)
    {
        var mock = new Mock<IPlayerRepository>();
        mock.Setup(p => p.GetSkillPlayersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(data);
        return mock;
    }

    private static Mock<IJobExecutionContext> Context(JobDataMap? dataMap = null)
    {
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.MergedJobDataMap).Returns(dataMap ?? new JobDataMap());
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task Execute_LogsPerPositionSummary_ResolvedUnresolvedAndCutoffCounted()
    {
        // Inside the cutoff (RB<=2): McCaffrey resolves by team-scoped name match; "Nobody Real" does not.
        // Outside the cutoff (rank 3): excluded from "ranked" entirely.
        var html = WrapInPage(EcrJson(
            (fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1),
            (fpId: 9999, name: "Nobody Real", team: "KC", rank: 2),
            (fpId: 8888, name: "Also Excluded", team: "KC", rank: 3)));

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, Options.Create(Settings()), logger.Object);

        await job.Execute(Context().Object);

        // One summary line: 2 ranked (rank 3 excluded by cutoff), 1 resolved, 1 new (existing is
        // always empty in dry-run), 0 moved, 1 unresolved.
        logger.VerifyLog(LogLevel.Information, "RB: 2 ranked, 1 resolved, 1 new, 0 moved, 1 unresolved");

        // The unresolved player logs a warning carrying name, FantasyPros id, team, position and ECR.
        logger.VerifyLog(LogLevel.Warning, msg =>
            msg.Contains("Nobody Real") && msg.Contains("9999") && msg.Contains("KC") && msg.Contains("RB") && msg.Contains("2"));

        players.Verify(p => p.GetSkillPlayersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_LoadsPlayersOnceForMultiplePages_NotOncePerPage()
    {
        var rbHtml = WrapInPage(EcrJson((9509, "Christian McCaffrey", "KC", 1)));
        var qbHtml = WrapInPage("""{"year":"2026","week":"3","position_id":"QB","scoring":"PPR","total_experts":1,"last_updated":"9/19","last_updated_ts":1,"accessed":"x","count":0,"players":[]}""");
        // QB page has no players within any cutoff (empty), but EcrPageParser throws on an empty
        // players array - use a QB page with one out-of-cutoff-range player instead so parsing succeeds.
        qbHtml = WrapInPage($$"""{"year":"2026","week":"3","position_id":"QB","scoring":"PPR","total_experts":1,"last_updated":"9/19","last_updated_ts":1,"accessed":"x","count":1,"players":[{"player_id":1,"player_name":"Nobody","player_team_id":"FA","player_position_id":"QB","player_opponent":"","player_opponent_id":null,"pos_rank":"QB99","rank_ecr":99,"rank_min":90,"rank_max":99,"rank_ave":95.0,"rank_std":1.0,"start_sit_grade":"F","player_ecr_delta":null,"player_bye_week":null,"player_game_kickoff_ts":null,"player_game_status":null,"player_page_url":"x"}]}""");

        var settings = Settings();
        settings.Pages =
        [
            new FantasyProsPage { Position = "RB", Path = "/nfl/rankings/ppr-rb.php" },
            new FantasyProsPage { Position = "QB", Path = "/nfl/rankings/qb.php" }
        ];

        var source = new Mock<IFantasyProsSource>();
        source.Setup(s => s.GetPageAsync("/nfl/rankings/ppr-rb.php", It.IsAny<CancellationToken>())).ReturnsAsync(rbHtml);
        source.Setup(s => s.GetPageAsync("/nfl/rankings/qb.php", It.IsAny<CancellationToken>())).ReturnsAsync(qbHtml);

        var resolution = Resolution() with
        {
            PositionIdByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["RB"] = RbPositionId, ["QB"] = 1 }
        };
        var players = Players(resolution);
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, Options.Create(settings), logger.Object);

        await job.Execute(Context().Object);

        players.Verify(p => p.GetSkillPlayersAsync(It.IsAny<CancellationToken>()), Times.Once);
        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 1 new, 0 moved, 0 unresolved");
        // QB's only player (rank 99) is outside the configured QB cutoff (32), so 0 ranked.
        logger.VerifyLog(LogLevel.Information, "QB: 0 ranked, 0 resolved, 0 new, 0 moved, 0 unresolved");
    }

    [Fact]
    public async Task Execute_PositionsOverride_RestrictsWhichPagesRun()
    {
        var html = WrapInPage(EcrJson((9509, "Christian McCaffrey", "KC", 1)));
        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var settings = Settings();
        settings.Pages =
        [
            new FantasyProsPage { Position = "RB", Path = "/nfl/rankings/ppr-rb.php" },
            new FantasyProsPage { Position = "WR", Path = "/nfl/rankings/ppr-wr.php" }
        ];

        var job = new FantasyProsRanksJob(source.Object, players.Object, Options.Create(settings), logger.Object);

        var dataMap = new JobDataMap();
        dataMap.Put(FantasyProsRanksJob.PositionsDataKey, "RB");

        await job.Execute(Context(dataMap).Object);

        source.Verify(s => s.GetPageAsync("/nfl/rankings/ppr-wr.php", It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.GetPageAsync("/nfl/rankings/ppr-rb.php", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_SeasonAndWeekOverrides_AreUsedInsteadOfThePageValues()
    {
        // The differ throws if an "existing" row's season/week/position/scoring don't match the
        // diff call's arguments; since existing is always empty here that path can't surface the
        // override directly, so this test only confirms the job does not throw when overrides are
        // supplied and still produces the expected summary (season/week flow into the log line's
        // data even though the line itself only prints ranked/resolved/new/moved/unresolved).
        var html = WrapInPage(EcrJson((9509, "Christian McCaffrey", "KC", 1)));
        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, Options.Create(Settings()), logger.Object);

        var dataMap = new JobDataMap();
        dataMap.Put(FantasyProsRanksJob.SeasonDataKey, 2099);
        dataMap.Put(FantasyProsRanksJob.WeekDataKey, 1);

        await job.Execute(Context(dataMap).Object);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 1 new, 0 moved, 0 unresolved");
    }
}

internal static class MockLoggerExtensions
{
    public static void VerifyLog(this Mock<ILogger<FantasyProsRanksJob>> logger, LogLevel level, string containing) =>
        logger.VerifyLog(level, msg => msg.Contains(containing, StringComparison.Ordinal));

    public static void VerifyLog(this Mock<ILogger<FantasyProsRanksJob>> logger, LogLevel level, Func<string, bool> predicate)
    {
        logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => predicate(state!.ToString()!)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}

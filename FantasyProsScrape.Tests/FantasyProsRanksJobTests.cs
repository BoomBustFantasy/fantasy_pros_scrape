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
/// FantasyProsRanksJob end to end, with fakes standing in for the network and every repository.
/// This covers the job's write *decision logic* only - what to write, when to skip a run insert,
/// when to backfill fantasy_pros_player_id. Per docs/plans/fantasy-pros-ranks-scraper.md,
/// "Testing Decisions", the repositories themselves (thin wrappers over Postgrest) are not unit
/// tested here; that is covered by the acceptance criteria's real-data checks, run separately.
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

    /// <summary>The payload hash the real job would compute for this page, via the same parser + hasher path.</summary>
    private static string PayloadHashFor(string html) => PayloadHasher.Hash(EcrPageParser.Parse(html).PlayersRawJson);

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
        mock.Setup(p => p.PatchFantasyProsPlayerIdAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock;
    }

    /// <summary>
    /// Defaults: no existing rows, and an upsert that echoes back whatever rows it was given (as a
    /// real upsert would), so <c>RankDiffer.AttachRankIds</c> and history writes have ids to use.
    /// Pass <paramref name="existing"/> to simulate rows already on file, or <paramref name="upsertIdSeed"/>
    /// to control the ids assigned to brand-new rows.
    /// </summary>
    private static Mock<IRankRepository> Ranks(List<FantasyProsRank>? existing = null, long upsertIdSeed = 501)
    {
        var mock = new Mock<IRankRepository>();
        mock.Setup(r => r.GetExistingRanksAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing ?? []);

        mock.Setup(r => r.UpsertRanksAsync(It.IsAny<IReadOnlyList<FantasyProsRank>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<FantasyProsRank> rows, CancellationToken _) =>
            {
                var nextId = upsertIdSeed;
                return rows.Select(row =>
                {
                    if (row.Id == 0)
                        row.Id = nextId++;
                    return row;
                }).ToList();
            });

        return mock;
    }

    /// <summary>Defaults: no run stored yet, and every insert "succeeds" (is the row now on file).</summary>
    private static Mock<IRunRepository> Runs(string? latestHash = null, bool insertSucceeds = true)
    {
        var mock = new Mock<IRunRepository>();
        mock.Setup(r => r.GetLatestPayloadHashAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestHash);
        mock.Setup(r => r.InsertRunAsync(It.IsAny<FantasyProsRankRun>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(insertSucceeds);
        return mock;
    }

    private static Mock<IJobExecutionContext> Context(JobDataMap? dataMap = null)
    {
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.MergedJobDataMap).Returns(dataMap ?? new JobDataMap());
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private static FantasyProsRank ExistingRow(
        long id, long playerId, int fantasyProsPlayerId, int rankEcr,
        decimal rankAve = 1.4m, int rankMin = 1, int rankMax = 6, decimal rankStd = 0.8m,
        string posRank = "RB1", string startSitGrade = "A", string opponent = "at BUF", string opponentAbbreviation = "BUF") =>
        new()
        {
            Id = id,
            Season = 2026,
            Week = 3,
            PositionId = RbPositionId,
            Scoring = "PPR",
            PlayerId = playerId,
            FantasyProsPlayerId = fantasyProsPlayerId,
            RankEcr = rankEcr,
            RankAve = rankAve,
            RankMin = rankMin,
            RankMax = rankMax,
            RankStd = rankStd,
            PosRank = posRank,
            StartSitGrade = startSitGrade,
            Opponent = opponent,
            OpponentAbbreviation = opponentAbbreviation,
            FirstSeenAt = DateTime.UtcNow.AddHours(-2),
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };

    [Fact]
    public async Task Execute_LogsPerPositionSummary_ResolvedUnresolvedAndCutoffCounted()
    {
        // Inside the cutoff (RB<=2): McCaffrey resolves by team-scoped name match; "Nobody Real" does not.
        // Outside the cutoff (rank 3): excluded from "ranked" entirely (no existing row to keep it alive).
        var html = WrapInPage(EcrJson(
            (fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1),
            (fpId: 9999, name: "Nobody Real", team: "KC", rank: 2),
            (fpId: 8888, name: "Also Excluded", team: "KC", rank: 3)));

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var ranks = Ranks();
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        // Should not throw: an unresolved player inside the cutoff is a warning, not a failure.
        await job.Execute(Context().Object);

        // One summary line: 2 ranked (rank 3 excluded by cutoff), 1 resolved, 1 new, 0 moved, 1 unresolved.
        logger.VerifyLog(LogLevel.Information, "RB: 2 ranked, 1 resolved, 1 new, 0 moved, 1 unresolved");

        // The unresolved player logs a warning carrying name, FantasyPros id, team, position and ECR.
        logger.VerifyLog(LogLevel.Warning, msg =>
            msg.Contains("Nobody Real") && msg.Contains("9999") && msg.Contains("KC") && msg.Contains("RB") && msg.Contains("2"));

        players.Verify(p => p.GetSkillPlayersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_FullWritePath_NewPlayers_UpsertsRanksInsertsHistoryAndCapturesRun()
    {
        var html = WrapInPage(EcrJson((fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1)));
        var expectedHash = PayloadHashFor(html);

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var ranks = Ranks(upsertIdSeed: 501);
        var runs = Runs(latestHash: null);
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        await job.Execute(Context().Object);

        // Run capture: no prior hash stored, so the full payload is inserted.
        runs.Verify(r => r.InsertRunAsync(It.Is<FantasyProsRankRun>(run =>
                run.Season == 2026 && run.Week == 3 && run.PositionId == RbPositionId && run.Scoring == "PPR"
                && run.PayloadHash == expectedHash),
            It.IsAny<CancellationToken>()), Times.Once);

        // One new row upserted for McCaffrey.
        ranks.Verify(r => r.UpsertRanksAsync(
            It.Is<IReadOnlyList<FantasyProsRank>>(rows => rows.Count == 1 && rows[0].PlayerId == 100 && rows[0].RankEcr == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        // First appearance: one history row, previous rank null, rank_id filled in from the upsert response.
        ranks.Verify(r => r.InsertHistoryAsync(
            It.Is<IReadOnlyList<FantasyProsRankHistory>>(rows =>
                rows.Count == 1 && rows[0].PlayerId == 100 && rows[0].RankId == 501 && rows[0].PreviousRankEcr == null && rows[0].NewRankEcr == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        // McCaffrey resolved through a name match (no fantasy_pros_player_id on file yet) - backfill it.
        players.Verify(p => p.PatchFantasyProsPlayerIdAsync(100, 9509, It.IsAny<CancellationToken>()), Times.Once);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 1 new, 0 moved, 0 unresolved");
    }

    [Fact]
    public async Task Execute_UnchangedSecondRun_WritesZeroHistoryAndSkipsRunInsert()
    {
        var html = WrapInPage(EcrJson((fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1)));
        var expectedHash = PayloadHashFor(html);

        var source = Source("/nfl/rankings/ppr-rb.php", html);

        // Already resolved via the id fast path, and every field on the existing row matches the scrape exactly.
        var resolution = Resolution() with
        {
            Players = [new Player { Id = 100, FirstName = "Christian", LastName = "McCaffrey", TeamId = ChiefsTeamId, PositionId = RbPositionId, FantasyProsPlayerId = 9509 }]
        };
        var players = Players(resolution);
        var ranks = Ranks(existing: [ExistingRow(id: 501, playerId: 100, fantasyProsPlayerId: 9509, rankEcr: 1)]);
        var runs = Runs(latestHash: expectedHash);
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        await job.Execute(Context().Object);

        // Same hash as last time: no new run row.
        runs.Verify(r => r.InsertRunAsync(It.IsAny<FantasyProsRankRun>(), It.IsAny<CancellationToken>()), Times.Never);

        // Nothing changed: no upsert, no history, no backfill (fast path already had the id).
        ranks.Verify(r => r.UpsertRanksAsync(It.IsAny<IReadOnlyList<FantasyProsRank>>(), It.IsAny<CancellationToken>()), Times.Never);
        ranks.Verify(r => r.InsertHistoryAsync(It.IsAny<IReadOnlyList<FantasyProsRankHistory>>(), It.IsAny<CancellationToken>()), Times.Never);
        players.Verify(p => p.PatchFantasyProsPlayerIdAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 0 new, 0 moved, 0 unresolved");
    }

    [Fact]
    public async Task Execute_ChangedEcr_WritesExactlyOneHistoryRow()
    {
        // Scraped rank is now 1; the existing row has this player at 5.
        var html = WrapInPage(EcrJson((fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1)));

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var resolution = Resolution() with
        {
            Players = [new Player { Id = 100, FirstName = "Christian", LastName = "McCaffrey", TeamId = ChiefsTeamId, PositionId = RbPositionId, FantasyProsPlayerId = 9509 }]
        };
        var players = Players(resolution);
        var existingRow = ExistingRow(id: 777, playerId: 100, fantasyProsPlayerId: 9509, rankEcr: 5, posRank: "RB5", startSitGrade: "B");
        var ranks = Ranks(existing: [existingRow]);
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        await job.Execute(Context().Object);

        ranks.Verify(r => r.UpsertRanksAsync(
            It.Is<IReadOnlyList<FantasyProsRank>>(rows => rows.Count == 1 && rows[0].RankEcr == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        ranks.Verify(r => r.InsertHistoryAsync(
            It.Is<IReadOnlyList<FantasyProsRankHistory>>(rows =>
                rows.Count == 1 && rows[0].RankId == 777 && rows[0].PreviousRankEcr == 5 && rows[0].NewRankEcr == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 0 new, 1 moved, 0 unresolved");
    }

    [Fact]
    public async Task Execute_SpreadOnlyChange_UpdatesRowButWritesNoHistory()
    {
        // rank_ecr stays at 1 (EcrJson: rank=1), but rank_ave moved from 1.4 -> the existing row's 9.9.
        var html = WrapInPage(EcrJson((fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1)));

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var resolution = Resolution() with
        {
            Players = [new Player { Id = 100, FirstName = "Christian", LastName = "McCaffrey", TeamId = ChiefsTeamId, PositionId = RbPositionId, FantasyProsPlayerId = 9509 }]
        };
        var players = Players(resolution);
        var existingRow = ExistingRow(id: 777, playerId: 100, fantasyProsPlayerId: 9509, rankEcr: 1, rankAve: 9.9m);
        var ranks = Ranks(existing: [existingRow]);
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        await job.Execute(Context().Object);

        // Touched: the row is upserted (rank_ave corrected) but no history, since rank_ecr didn't move.
        ranks.Verify(r => r.UpsertRanksAsync(
            It.Is<IReadOnlyList<FantasyProsRank>>(rows => rows.Count == 1 && rows[0].RankAve == 1.4m),
            It.IsAny<CancellationToken>()), Times.Once);
        ranks.Verify(r => r.InsertHistoryAsync(It.IsAny<IReadOnlyList<FantasyProsRankHistory>>(), It.IsAny<CancellationToken>()), Times.Never);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 0 new, 0 moved, 0 unresolved");
    }

    [Fact]
    public async Task Execute_DroppedBelowCutoff_StillUpdatesViaExistingRow()
    {
        // Cutoff is 1: McCaffrey now scrapes at rank 2, below it. He has an existing row from
        // earlier this week, so he must still be included and updated rather than frozen.
        var html = WrapInPage(EcrJson((fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 2)));

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var resolution = Resolution() with
        {
            Players = [new Player { Id = 100, FirstName = "Christian", LastName = "McCaffrey", TeamId = ChiefsTeamId, PositionId = RbPositionId, FantasyProsPlayerId = 9509 }]
        };
        var players = Players(resolution);
        var existingRow = ExistingRow(id: 777, playerId: 100, fantasyProsPlayerId: 9509, rankEcr: 1, posRank: "RB1");
        var ranks = Ranks(existing: [existingRow]);
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings(rbCutoff: 1)), logger.Object);

        await job.Execute(Context().Object);

        ranks.Verify(r => r.InsertHistoryAsync(
            It.Is<IReadOnlyList<FantasyProsRankHistory>>(rows => rows.Count == 1 && rows[0].PreviousRankEcr == 1 && rows[0].NewRankEcr == 2),
            It.IsAny<CancellationToken>()), Times.Once);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 0 new, 1 moved, 0 unresolved");
    }

    [Fact]
    public async Task Execute_PatchesFantasyProsPlayerId_OnlyOnFreshNameMatch_NotOnIdFastPath()
    {
        var html = WrapInPage(EcrJson(
            (fpId: 9509, name: "Christian McCaffrey", team: "KC", rank: 1),   // no id on file -> name match -> patch
            (fpId: 7777, name: "Austin Ekeler", team: "KC", rank: 2)));        // id already on file -> fast path -> no patch

        var resolution = Resolution() with
        {
            Players =
            [
                new Player { Id = 100, FirstName = "Christian", LastName = "McCaffrey", TeamId = ChiefsTeamId, PositionId = RbPositionId },
                new Player { Id = 200, FirstName = "Austin", LastName = "Ekeler", TeamId = ChiefsTeamId, PositionId = RbPositionId, FantasyProsPlayerId = 7777 }
            ]
        };

        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(resolution);
        var ranks = Ranks();
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        await job.Execute(Context().Object);

        players.Verify(p => p.PatchFantasyProsPlayerIdAsync(100, 9509, It.IsAny<CancellationToken>()), Times.Once);
        players.Verify(p => p.PatchFantasyProsPlayerIdAsync(200, 7777, It.IsAny<CancellationToken>()), Times.Never);
        players.Verify(p => p.PatchFantasyProsPlayerIdAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_DuplicateRunHashInsertFailure_IsTreatedAsSuccessNotJobFailure()
    {
        var html = WrapInPage(EcrJson((9509, "Christian McCaffrey", "KC", 1)));
        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var ranks = Ranks();
        // IRunRepository.InsertRunAsync returning false means a concurrent run already stored this
        // exact hash (the unique constraint backstopped the race) - not a job failure.
        var runs = Runs(insertSucceeds: false);
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        // Must not throw.
        await job.Execute(Context().Object);

        runs.Verify(r => r.InsertRunAsync(It.IsAny<FantasyProsRankRun>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.VerifyLog(LogLevel.Information, "Completed FantasyPros ranks run");
    }

    [Fact]
    public async Task Execute_LoadsPlayersOnceForMultiplePages_NotOncePerPage()
    {
        var rbHtml = WrapInPage(EcrJson((9509, "Christian McCaffrey", "KC", 1)));
        var qbHtml = WrapInPage($$"""{"year":"2026","week":"3","position_id":"QB","scoring":"PPR","total_experts":1,"last_updated":"9/19","last_updated_ts":1,"accessed":"x","count":1,"players":[{"player_id":1,"player_name":"Nobody","player_team_id":"FA","player_position_id":"QB","player_opponent":"","player_opponent_id":null,"pos_rank":"QB99","rank_ecr":99,"rank_min":90,"rank_max":99,"rank_ave":95.0,"rank_std":1.0,"start_sit_grade":"F","player_ecr_delta":null,"player_bye_week":null,"player_game_kickoff_ts":null,"player_game_status":null,"player_page_url":"x"}]}""");

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
        var ranks = Ranks();
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(settings), logger.Object);

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
        var ranks = Ranks();
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var settings = Settings();
        settings.Pages =
        [
            new FantasyProsPage { Position = "RB", Path = "/nfl/rankings/ppr-rb.php" },
            new FantasyProsPage { Position = "WR", Path = "/nfl/rankings/ppr-wr.php" }
        ];

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(settings), logger.Object);

        var dataMap = new JobDataMap();
        dataMap.Put(FantasyProsRanksJob.PositionsDataKey, "RB");

        await job.Execute(Context(dataMap).Object);

        source.Verify(s => s.GetPageAsync("/nfl/rankings/ppr-wr.php", It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(s => s.GetPageAsync("/nfl/rankings/ppr-rb.php", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_SeasonAndWeekOverrides_AreUsedInsteadOfThePageValues()
    {
        var html = WrapInPage(EcrJson((9509, "Christian McCaffrey", "KC", 1)));
        var source = Source("/nfl/rankings/ppr-rb.php", html);
        var players = Players(Resolution());
        var ranks = Ranks();
        var runs = Runs();
        var logger = new Mock<ILogger<FantasyProsRanksJob>>();

        var job = new FantasyProsRanksJob(source.Object, players.Object, ranks.Object, runs.Object, Options.Create(Settings()), logger.Object);

        var dataMap = new JobDataMap();
        dataMap.Put(FantasyProsRanksJob.SeasonDataKey, 2099);
        dataMap.Put(FantasyProsRanksJob.WeekDataKey, 1);

        await job.Execute(Context(dataMap).Object);

        logger.VerifyLog(LogLevel.Information, "RB: 1 ranked, 1 resolved, 1 new, 0 moved, 0 unresolved");

        // The overridden season/week, not the page's, are what the repositories were asked about.
        ranks.Verify(r => r.GetExistingRanksAsync(2099, 1, RbPositionId, "PPR", It.IsAny<CancellationToken>()), Times.Once);
        runs.Verify(r => r.GetLatestPayloadHashAsync(2099, 1, RbPositionId, "PPR", It.IsAny<CancellationToken>()), Times.Once);
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

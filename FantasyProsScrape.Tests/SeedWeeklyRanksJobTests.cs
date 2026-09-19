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
/// SeedWeeklyRanksJob's write *decision logic*, with fakes standing in for every repository. Per
/// docs/plans/fantasy-pros-ranks-scraper.md, "Testing Decisions", the repositories themselves (thin
/// wrappers over Postgrest) are not unit tested here.
/// </summary>
public class SeedWeeklyRanksJobTests
{
    private const long QbPositionId = 1;
    private const long RbPositionId = 2;
    private const long WrPositionId = 3;
    private const long TePositionId = 4;

    private static FantasyProsSettings Settings() => new()
    {
        BaseUrl = "https://www.fantasypros.com",
        Pages =
        [
            new FantasyProsPage { Position = "QB", Path = "/nfl/rankings/qb.php" },
            new FantasyProsPage { Position = "RB", Path = "/nfl/rankings/ppr-rb.php" },
            new FantasyProsPage { Position = "WR", Path = "/nfl/rankings/ppr-wr.php" },
            new FantasyProsPage { Position = "TE", Path = "/nfl/rankings/ppr-te.php" }
        ],
        Cutoffs = new FantasyProsCutoffs { QB = 2, RB = 3, WR = 3, TE = 1 },
        Scoring = "PPR",
        TimeZone = "America/Chicago"
    };

    private static PlayerResolutionData Resolution() => new(
        Players: [],
        PositionIdByName: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["QB"] = QbPositionId,
            ["RB"] = RbPositionId,
            ["WR"] = WrPositionId,
            ["TE"] = TePositionId
        },
        TeamIdByAbbreviation: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

    private static Mock<IPlayerRepository> Players()
    {
        var mock = new Mock<IPlayerRepository>();
        mock.Setup(p => p.GetSkillPlayersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Resolution());
        return mock;
    }

    private static List<FantasyProsRank> RanksFor(long positionId, int count, long firstPlayerId) =>
        Enumerable.Range(0, count)
            .Select(i => new FantasyProsRank
            {
                Id = 1000 + i,
                Season = 2026,
                Week = 3,
                PositionId = positionId,
                Scoring = "PPR",
                PlayerId = firstPlayerId + i,
                FantasyProsPlayerId = (int)(firstPlayerId + i),
                RankEcr = i + 1
            })
            .ToList();

    /// <summary>Returns however many rows the settings' cutoff calls for, in ascending rank_ecr order.</summary>
    private static Mock<IRankRepository> Ranks()
    {
        var mock = new Mock<IRankRepository>();
        mock.Setup(r => r.GetTopRanksAsync(2026, 3, QbPositionId, "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, long _, string _, int limit, CancellationToken _) => RanksFor(QbPositionId, limit, 100));
        mock.Setup(r => r.GetTopRanksAsync(2026, 3, RbPositionId, "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, long _, string _, int limit, CancellationToken _) => RanksFor(RbPositionId, limit, 200));
        mock.Setup(r => r.GetTopRanksAsync(2026, 3, WrPositionId, "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, long _, string _, int limit, CancellationToken _) => RanksFor(WrPositionId, limit, 300));
        mock.Setup(r => r.GetTopRanksAsync(2026, 3, TePositionId, "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, long _, string _, int limit, CancellationToken _) => RanksFor(TePositionId, limit, 400));
        return mock;
    }

    private static Mock<IRunRepository> Runs((int Season, int Week)? latest = null)
    {
        var mock = new Mock<IRunRepository>();
        mock.Setup(r => r.GetMostRecentSeasonWeekAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(latest ?? (2026, 3));
        return mock;
    }

    private static Mock<IWeeklyRankRepository> WeeklyRanks(bool setExists = false, long insertedSetId = 900)
    {
        var mock = new Mock<IWeeklyRankRepository>();
        mock.Setup(w => w.SetExistsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(setExists);
        mock.Setup(w => w.InsertSetAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int season, int week, CancellationToken _) => new WeeklyRankSet
            {
                Id = insertedSetId,
                Season = season,
                Week = week,
                SeededAt = DateTime.UtcNow,
                PublishedAt = null,
                UpdatedAt = DateTime.UtcNow
            });
        return mock;
    }

    private static Mock<IJobExecutionContext> Context(JobDataMap? dataMap = null)
    {
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.MergedJobDataMap).Returns(dataMap ?? new JobDataMap());
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private static SeedWeeklyRanksJob BuildJob(
        Mock<IRunRepository> runs, Mock<IRankRepository> ranks, Mock<IWeeklyRankRepository> weeklyRanks,
        Mock<IPlayerRepository> players, Mock<ILogger<SeedWeeklyRanksJob>> logger) =>
        new(runs.Object, ranks.Object, weeklyRanks.Object, players.Object, Options.Create(Settings()), logger.Object);

    [Fact]
    public async Task Execute_FreshWeek_CreatesSetAndSeedsEachPositionWithRankAndSeededRank1ToN()
    {
        var runs = Runs();
        var ranks = Ranks();
        var weeklyRanks = WeeklyRanks(setExists: false, insertedSetId: 900);
        var players = Players();
        var logger = new Mock<ILogger<SeedWeeklyRanksJob>>();

        var job = BuildJob(runs, ranks, weeklyRanks, players, logger);

        await job.Execute(Context().Object);

        weeklyRanks.Verify(w => w.InsertSetAsync(2026, 3, It.IsAny<CancellationToken>()), Times.Once);

        weeklyRanks.Verify(w => w.InsertRanksAsync(
            It.Is<IReadOnlyList<WeeklyRank>>(rows =>
                rows.Count == 2
                && rows.All(r => r.SetId == 900 && r.PositionId == QbPositionId)
                && rows[0].PlayerId == 100 && rows[0].Rank == 1 && rows[0].SeededRank == 1
                && rows[1].PlayerId == 101 && rows[1].Rank == 2 && rows[1].SeededRank == 2),
            It.IsAny<CancellationToken>()), Times.Once);

        weeklyRanks.Verify(w => w.InsertRanksAsync(
            It.Is<IReadOnlyList<WeeklyRank>>(rows =>
                rows.Count == 3 && rows.All(r => r.PositionId == RbPositionId)
                && rows[0].PlayerId == 200 && rows[2].PlayerId == 202 && rows[2].Rank == 3 && rows[2].SeededRank == 3),
            It.IsAny<CancellationToken>()), Times.Once);

        weeklyRanks.Verify(w => w.InsertRanksAsync(
            It.Is<IReadOnlyList<WeeklyRank>>(rows => rows.Count == 3 && rows.All(r => r.PositionId == WrPositionId)),
            It.IsAny<CancellationToken>()), Times.Once);

        weeklyRanks.Verify(w => w.InsertRanksAsync(
            It.Is<IReadOnlyList<WeeklyRank>>(rows =>
                rows.Count == 1 && rows[0].PositionId == TePositionId && rows[0].PlayerId == 400
                && rows[0].Rank == 1 && rows[0].SeededRank == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        logger.VerifyLog(LogLevel.Information, "Seeded week 3: QB 2, RB 3, WR 3, TE 1");
    }

    [Fact]
    public async Task Execute_SeededRankUsesTheRowsActualRankEcr_NotARecomputedSequentialIndex()
    {
        // RB's top-3 slice has gaps in rank_ecr (2, 5, 9) - not a clean 1..N sequence. seeded_rank
        // must freeze the real FantasyPros value, while `Rank` (the set's own ordering, later
        // drag-reordered from the admin page) stays the sequential 1..N position within the slice.
        var gappedRb = new List<FantasyProsRank>
        {
            new() { Id = 2001, Season = 2026, Week = 3, PositionId = RbPositionId, Scoring = "PPR", PlayerId = 200, FantasyProsPlayerId = 200, RankEcr = 2 },
            new() { Id = 2002, Season = 2026, Week = 3, PositionId = RbPositionId, Scoring = "PPR", PlayerId = 201, FantasyProsPlayerId = 201, RankEcr = 5 },
            new() { Id = 2003, Season = 2026, Week = 3, PositionId = RbPositionId, Scoring = "PPR", PlayerId = 202, FantasyProsPlayerId = 202, RankEcr = 9 }
        };

        var runs = Runs();
        var ranks = Ranks();
        ranks.Setup(r => r.GetTopRanksAsync(2026, 3, RbPositionId, "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(gappedRb);
        var weeklyRanks = WeeklyRanks(setExists: false, insertedSetId: 900);
        var players = Players();
        var logger = new Mock<ILogger<SeedWeeklyRanksJob>>();

        var job = BuildJob(runs, ranks, weeklyRanks, players, logger);

        await job.Execute(Context().Object);

        weeklyRanks.Verify(w => w.InsertRanksAsync(
            It.Is<IReadOnlyList<WeeklyRank>>(rows =>
                rows.Count == 3 && rows.All(r => r.PositionId == RbPositionId)
                && rows[0].PlayerId == 200 && rows[0].Rank == 1 && rows[0].SeededRank == 2
                && rows[1].PlayerId == 201 && rows[1].Rank == 2 && rows[1].SeededRank == 5
                && rows[2].PlayerId == 202 && rows[2].Rank == 3 && rows[2].SeededRank == 9),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_AlreadySeededWeek_IsNoOp_NeverCallsAnyWriteMethod()
    {
        var runs = Runs();
        var ranks = Ranks();
        var weeklyRanks = WeeklyRanks(setExists: true);
        var players = Players();
        var logger = new Mock<ILogger<SeedWeeklyRanksJob>>();

        var job = BuildJob(runs, ranks, weeklyRanks, players, logger);

        await job.Execute(Context().Object);

        weeklyRanks.Verify(w => w.InsertSetAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        weeklyRanks.Verify(w => w.InsertRanksAsync(It.IsAny<IReadOnlyList<WeeklyRank>>(), It.IsAny<CancellationToken>()), Times.Never);
        ranks.Verify(r => r.GetTopRanksAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        players.Verify(p => p.GetSkillPlayersAsync(It.IsAny<CancellationToken>()), Times.Never);

        logger.VerifyLog(LogLevel.Information, "already exists");
    }

    [Fact]
    public async Task Execute_NoOverride_ResolvesSeasonAndWeekFromMostRecentRun()
    {
        var runs = Runs(latest: (2027, 9));
        var ranks = Ranks();
        var weeklyRanks = WeeklyRanks(setExists: false);
        var players = Players();
        var logger = new Mock<ILogger<SeedWeeklyRanksJob>>();

        // GetTopRanksAsync/SetExistsAsync mocks above are wired for (2026, 3); re-wire for (2027, 9)
        // so the assertions below can tell resolution actually used the run's season/week.
        weeklyRanks.Setup(w => w.SetExistsAsync(2027, 9, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        weeklyRanks.Setup(w => w.InsertSetAsync(2027, 9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeeklyRankSet { Id = 901, Season = 2027, Week = 9, SeededAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        ranks.Setup(r => r.GetTopRanksAsync(2027, 9, It.IsAny<long>(), "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, long positionId, string _, int limit, CancellationToken _) => RanksFor(positionId, limit, 100));

        var job = BuildJob(runs, ranks, weeklyRanks, players, logger);

        await job.Execute(Context().Object);

        runs.Verify(r => r.GetMostRecentSeasonWeekAsync(It.IsAny<CancellationToken>()), Times.Once);
        weeklyRanks.Verify(w => w.InsertSetAsync(2027, 9, It.IsAny<CancellationToken>()), Times.Once);
        logger.VerifyLog(LogLevel.Information, "Seeded week 9: QB 2, RB 3, WR 3, TE 1");
    }

    [Fact]
    public async Task Execute_SeasonAndWeekOverride_SkipsMostRecentRunLookup()
    {
        var runs = Runs();
        var ranks = Ranks();
        var weeklyRanks = WeeklyRanks(setExists: false);
        var players = Players();
        var logger = new Mock<ILogger<SeedWeeklyRanksJob>>();

        weeklyRanks.Setup(w => w.SetExistsAsync(2099, 15, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        weeklyRanks.Setup(w => w.InsertSetAsync(2099, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeeklyRankSet { Id = 902, Season = 2099, Week = 15, SeededAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        ranks.Setup(r => r.GetTopRanksAsync(2099, 15, It.IsAny<long>(), "PPR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, long positionId, string _, int limit, CancellationToken _) => RanksFor(positionId, limit, 100));

        var job = BuildJob(runs, ranks, weeklyRanks, players, logger);

        var dataMap = new JobDataMap();
        dataMap.Put(SeedWeeklyRanksJob.SeasonDataKey, 2099);
        dataMap.Put(SeedWeeklyRanksJob.WeekDataKey, 15);

        await job.Execute(Context(dataMap).Object);

        runs.Verify(r => r.GetMostRecentSeasonWeekAsync(It.IsAny<CancellationToken>()), Times.Never);
        weeklyRanks.Verify(w => w.InsertSetAsync(2099, 15, It.IsAny<CancellationToken>()), Times.Once);
        logger.VerifyLog(LogLevel.Information, "Seeded week 15: QB 2, RB 3, WR 3, TE 1");
    }

    [Fact]
    public async Task Execute_NoRunsStoredAndNoOverride_LogsWarningAndDoesNothing()
    {
        var runs = new Mock<IRunRepository>();
        runs.Setup(r => r.GetMostRecentSeasonWeekAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((int, int)?)null);
        var ranks = Ranks();
        var weeklyRanks = WeeklyRanks();
        var players = Players();
        var logger = new Mock<ILogger<SeedWeeklyRanksJob>>();

        var job = BuildJob(runs, ranks, weeklyRanks, players, logger);

        await job.Execute(Context().Object);

        weeklyRanks.Verify(w => w.SetExistsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        weeklyRanks.Verify(w => w.InsertSetAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        logger.VerifyLog(LogLevel.Warning, "nothing to seed");
    }
}

internal static class SeedWeeklyRanksJobMockLoggerExtensions
{
    public static void VerifyLog(this Mock<ILogger<SeedWeeklyRanksJob>> logger, LogLevel level, string containing) =>
        logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state!.ToString()!.Contains(containing, StringComparison.Ordinal)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
}

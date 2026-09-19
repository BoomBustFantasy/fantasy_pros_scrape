using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Services;

namespace FantasyProsScrape.Tests;

/// <summary>
/// RankDiffer is pure and static: everything here is built from small in-memory lists, no
/// network or database. See docs/plans/fantasy-pros-ranks-scraper.md, "FantasyProsRanksJob"
/// step 5, for the new/changed/touched/unchanged contract.
/// </summary>
public class RankDifferTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private const int Season = 2026;
    private const int Week = 3;
    private const long PositionId = 2; // RB
    private const string Scoring = "PPR";

    private static FantasyProsRank ExistingRow(
        long id,
        long playerId,
        int fpPlayerId,
        int rankEcr,
        decimal? rankAve = 5.0m,
        int? rankMin = 1,
        int? rankMax = 10,
        decimal? rankStd = 1.5m,
        string? posRank = "RB1",
        string? startSitGrade = "A",
        string? opponent = "at BUF",
        string? opponentAbbreviation = "BUF",
        DateTime? firstSeenAt = null) =>
        new()
        {
            Id = id,
            Season = Season,
            Week = Week,
            PositionId = PositionId,
            Scoring = Scoring,
            PlayerId = playerId,
            FantasyProsPlayerId = fpPlayerId,
            RankEcr = rankEcr,
            RankAve = rankAve,
            RankMin = rankMin,
            RankMax = rankMax,
            RankStd = rankStd,
            PosRank = posRank,
            StartSitGrade = startSitGrade,
            Opponent = opponent,
            OpponentAbbreviation = opponentAbbreviation,
            FirstSeenAt = firstSeenAt ?? Now.AddDays(-7),
            UpdatedAt = firstSeenAt ?? Now.AddDays(-7)
        };

    private static ResolvedRank ScrapedRow(
        long playerId,
        int fpPlayerId,
        int rankEcr,
        decimal? rankAve = 5.0m,
        int? rankMin = 1,
        int? rankMax = 10,
        decimal? rankStd = 1.5m,
        string? posRank = "RB1",
        string? startSitGrade = "A",
        string? opponent = "at BUF",
        string? opponentAbbreviation = "BUF") =>
        new(playerId, fpPlayerId, rankEcr, rankAve, rankMin, rankMax, rankStd, posRank, startSitGrade, opponent, opponentAbbreviation);

    [Fact]
    public void NoExistingRow_LandsInNew_WithHistoryRowPreviousNull()
    {
        var scraped = new[] { ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 12) };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing: [], scraped, Now);

        var inserted = Assert.Single(result.New);
        Assert.Equal(100, inserted.PlayerId);
        Assert.Equal(12, inserted.RankEcr);
        Assert.Equal(Now, inserted.FirstSeenAt);
        Assert.Equal(Now, inserted.UpdatedAt);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Touched);
        Assert.Empty(result.Unchanged);
        Assert.Same(inserted, Assert.Single(result.Upserts));

        var history = Assert.Single(result.HistoryRows);
        Assert.Null(history.PreviousRankEcr);
        Assert.Equal(12, history.NewRankEcr);
        Assert.Null(history.RankId);
        Assert.Equal(100, history.PlayerId);
        Assert.Equal(Now, history.ChangedAt);
    }

    [Fact]
    public void RankEcrChanged_LandsInChanged_WithHistoryRowCarryingPreviousAndNew()
    {
        var existing = new[] { ExistingRow(id: 7, playerId: 100, fpPlayerId: 5000, rankEcr: 15) };
        var scraped = new[] { ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 12) };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing, scraped, Now);

        var updated = Assert.Single(result.Changed);
        Assert.Equal(7, updated.Id);
        Assert.Equal(12, updated.RankEcr);
        Assert.Equal(existing[0].FirstSeenAt, updated.FirstSeenAt);
        Assert.Equal(Now, updated.UpdatedAt);
        Assert.Empty(result.New);
        Assert.Empty(result.Touched);
        Assert.Empty(result.Unchanged);
        Assert.Same(updated, Assert.Single(result.Upserts));

        var history = Assert.Single(result.HistoryRows);
        Assert.Equal(15, history.PreviousRankEcr);
        Assert.Equal(12, history.NewRankEcr);
        Assert.Equal(7, history.RankId);
    }

    [Theory]
    [InlineData("rank_ave")]
    [InlineData("rank_min")]
    [InlineData("rank_max")]
    [InlineData("rank_std")]
    [InlineData("start_sit_grade")]
    [InlineData("opponent")]
    public void OnlyDetailFieldMoved_LandsInTouched_NoHistoryRow(string movedField)
    {
        var existing = new[] { ExistingRow(id: 7, playerId: 100, fpPlayerId: 5000, rankEcr: 12) };

        var scraped = new[]
        {
            movedField switch
            {
                "rank_ave" => ScrapedRow(100, 5000, 12, rankAve: 6.5m),
                "rank_min" => ScrapedRow(100, 5000, 12, rankMin: 2),
                "rank_max" => ScrapedRow(100, 5000, 12, rankMax: 14),
                "rank_std" => ScrapedRow(100, 5000, 12, rankStd: 2.1m),
                "start_sit_grade" => ScrapedRow(100, 5000, 12, startSitGrade: "B+"),
                "opponent" => ScrapedRow(100, 5000, 12, opponent: "vs. CAR", opponentAbbreviation: "CAR"),
                _ => throw new ArgumentOutOfRangeException(nameof(movedField))
            }
        };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing, scraped, Now);

        var updated = Assert.Single(result.Touched);
        Assert.Equal(7, updated.Id);
        Assert.Equal(12, updated.RankEcr);
        Assert.Equal(Now, updated.UpdatedAt);
        Assert.Empty(result.New);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Unchanged);
        Assert.Empty(result.HistoryRows);
        Assert.Same(updated, Assert.Single(result.Upserts));
    }

    [Fact]
    public void IdenticalRow_LandsInUnchanged_ReturnedAsExistingInstance()
    {
        var existing = new[] { ExistingRow(id: 7, playerId: 100, fpPlayerId: 5000, rankEcr: 12) };
        var scraped = new[] { ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 12) };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing, scraped, Now);

        Assert.Same(existing[0], Assert.Single(result.Unchanged));
        Assert.Empty(result.New);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Touched);
        Assert.Empty(result.Upserts);
        Assert.Empty(result.HistoryRows);
    }

    [Fact]
    public void ExistingRowStillInScrapedList_UpdatesRegardlessOfBeingBelowCutoff()
    {
        // RankDiffer knows nothing about cutoffs; the caller decides who is in `scraped`. A
        // player who dropped below the cutoff but is still present (because they already have
        // a row) must still be classified normally rather than silently dropped.
        var existing = new[] { ExistingRow(id: 7, playerId: 100, fpPlayerId: 5000, rankEcr: 65) };
        var scraped = new[] { ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 74) };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing, scraped, Now);

        var updated = Assert.Single(result.Changed);
        Assert.Equal(74, updated.RankEcr);
        Assert.Empty(result.Unchanged);
    }

    [Fact]
    public void ExistingRowAbsentFromScrape_IsLeftAlone_NeverDeleted()
    {
        var existing = new[]
        {
            ExistingRow(id: 7, playerId: 100, fpPlayerId: 5000, rankEcr: 12),
            ExistingRow(id: 8, playerId: 200, fpPlayerId: 5001, rankEcr: 20)
        };
        // Only player 100 comes back from the scrape; player 200 dropped off entirely.
        var scraped = new[] { ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 12) };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing, scraped, Now);

        Assert.Same(existing[0], Assert.Single(result.Unchanged));
        Assert.Empty(result.New);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Touched);
        Assert.Empty(result.Upserts);
        Assert.Empty(result.HistoryRows);
        // Player 200 appears nowhere in the result; it was never touched or deleted.
        Assert.DoesNotContain(result.Upserts, r => r.PlayerId == 200);
    }

    [Fact]
    public void DuplicatePlayerIdInScrape_KeepsBestLowestRankEcr()
    {
        var scraped = new[]
        {
            ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 20),
            ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 12),
            ScrapedRow(playerId: 100, fpPlayerId: 5000, rankEcr: 18)
        };

        var result = RankDiffer.Diff(Season, Week, PositionId, Scoring, existing: [], scraped, Now);

        var inserted = Assert.Single(result.New);
        Assert.Equal(12, inserted.RankEcr);
        Assert.Single(result.Upserts);
        Assert.Single(result.HistoryRows);
    }

    [Fact]
    public void ExistingRowFromDifferentSeasonWeekPositionScoring_Throws()
    {
        var mismatched = ExistingRow(id: 7, playerId: 100, fpPlayerId: 5000, rankEcr: 12);
        mismatched.Week = Week + 1;
        var existing = new[] { mismatched };

        Assert.Throws<ArgumentException>(() =>
            RankDiffer.Diff(Season, Week, PositionId, Scoring, existing, scraped: [], Now));
    }

    [Fact]
    public void AttachRankIds_FillsNullRankIdFromUpsertResponse_LeavesExistingIdsAlone()
    {
        var newHistory = new FantasyProsRankHistory
        {
            RankId = null,
            Season = Season,
            Week = Week,
            PositionId = PositionId,
            PlayerId = 100,
            PreviousRankEcr = null,
            NewRankEcr = 12,
            ChangedAt = Now
        };
        var changedHistory = new FantasyProsRankHistory
        {
            RankId = 7,
            Season = Season,
            Week = Week,
            PositionId = PositionId,
            PlayerId = 200,
            PreviousRankEcr = 15,
            NewRankEcr = 12,
            ChangedAt = Now
        };
        var upserted = new[]
        {
            ExistingRow(id: 42, playerId: 100, fpPlayerId: 5000, rankEcr: 12),
            ExistingRow(id: 7, playerId: 200, fpPlayerId: 5001, rankEcr: 12)
        };

        RankDiffer.AttachRankIds([newHistory, changedHistory], upserted);

        Assert.Equal(42, newHistory.RankId);
        Assert.Equal(7, changedHistory.RankId); // untouched, already had an id
    }

    [Fact]
    public void AttachRankIds_MissingUpsertedRowForPlayer_Throws()
    {
        var newHistory = new FantasyProsRankHistory
        {
            RankId = null,
            Season = Season,
            Week = Week,
            PositionId = PositionId,
            PlayerId = 100,
            PreviousRankEcr = null,
            NewRankEcr = 12,
            ChangedAt = Now
        };

        Assert.Throws<InvalidOperationException>(() =>
            RankDiffer.AttachRankIds([newHistory], upserted: []));
    }
}

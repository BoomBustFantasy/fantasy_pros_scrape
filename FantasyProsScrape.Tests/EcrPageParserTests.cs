using System.Text.Json;
using FantasyProsScrape.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FantasyProsScrape.Tests;

/// <summary>
/// Parses fixtures/ppr-rb.html, a real PPR RB page stripped to its ecrData script, plus a few hand-built
/// pages for the failure modes. If FantasyPros changes the blob's shape, the fixture test is what breaks.
/// </summary>
public class EcrPageParserTests(ITestOutputHelper output)
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "ppr-rb.html");

    private static string ReadFixture() => File.ReadAllText(FixturePath);

    private static string WrapInPage(string ecrDataJson) =>
        $"<html><body><script type=\"text/javascript\">\n    var ecrData = {ecrDataJson};\n    var sosData = {{}};\n</script></body></html>";

    [Fact]
    public void Fixture_ParsesTopLevelFields()
    {
        var page = EcrPageParser.Parse(ReadFixture());

        Assert.Equal(2026, page.Year);
        Assert.Equal(2, page.Week);
        Assert.Equal("RB", page.PositionId);
        Assert.Equal("PPR", page.Scoring);
        Assert.Equal(140, page.TotalExperts);
        Assert.Equal("9/19", page.LastUpdated);
        Assert.Equal(1789846940L, page.LastUpdatedTs);
        Assert.Equal("2026-09-19 19:44:49", page.Accessed);
        Assert.Equal(131, page.Count);
        Assert.Equal(131, page.Players.Count);
    }

    [Fact]
    public void Fixture_ParsesFirstPlayer()
    {
        var first = EcrPageParser.Parse(ReadFixture()).Players[0];

        Assert.Equal(22968, first.PlayerId);
        Assert.Equal("Jahmyr Gibbs", first.PlayerName);
        Assert.Equal("DET", first.PlayerTeamId);
        Assert.Equal("RB", first.PlayerPositionId);
        Assert.Equal("at BUF", first.PlayerOpponent);
        Assert.Equal("BUF", first.PlayerOpponentId);
        Assert.Equal("RB1", first.PosRank);
        Assert.Equal(1, first.RankEcr);
        Assert.Equal(1, first.RankMin);
        Assert.Equal(6, first.RankMax);
        Assert.Equal(1.42m, first.RankAve);
        Assert.Equal(0.83m, first.RankStd);
        Assert.Equal("A+", first.StartSitGrade);
        Assert.Null(first.PlayerEcrDelta);
        Assert.Equal(6, first.PlayerByeWeek);
        Assert.Equal(1789690500L, first.PlayerGameKickoffTs);
        Assert.Equal("closed", first.PlayerGameStatus);
        Assert.Equal("https://www.fantasypros.com/nfl/players/jahmyr-gibbs.php", first.PlayerPageUrl);
    }

    [Fact]
    public void Fixture_RanksAreContiguousAndPlayersWithNoScheduledGameHaveNullOpponentFields()
    {
        var page = EcrPageParser.Parse(ReadFixture());

        Assert.Equal(Enumerable.Range(1, page.Players.Count), page.Players.Select(p => p.RankEcr));
        Assert.All(page.Players, p => Assert.Equal("RB", p.PlayerPositionId));

        // In this fixture (week 2, too early for real byes) these are players FantasyPros has no
        // scheduled game for at all (e.g. unrostered free agents), not literal bye-week starters -
        // player_opponent comes back "", not "BYE", for them.
        var noOpponent = page.Players.Where(p => p.PlayerOpponentId is null).ToList();
        Assert.NotEmpty(noOpponent);
        Assert.All(noOpponent, p =>
        {
            Assert.Equal(string.Empty, p.PlayerOpponent);
            Assert.Null(p.PlayerGameKickoffTs);
            Assert.Null(p.PlayerGameStatus);
        });
    }

    [Fact]
    public void Fixture_ExposesPlayersArrayAndWholeBlobExactlyAsEmbedded()
    {
        var html = ReadFixture();
        var page = EcrPageParser.Parse(html);

        // Verbatim slices of the page, not a reserialisation: the run-capture hash depends on it.
        Assert.StartsWith("[{\"player_id\":22968,", page.PlayersRawJson);
        Assert.Contains("\"players\":" + page.PlayersRawJson, html);
        Assert.Contains("var ecrData = " + page.RawJson + ";", html);

        using var doc = JsonDocument.Parse(page.PlayersRawJson);
        Assert.Equal(131, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void StringNumerics_AreCoercedToNumbers()
    {
        const string json = """
            {"year":"2026","week":"7","position_id":"WR","scoring":"PPR","total_experts":"12","last_updated":"10/14","last_updated_ts":"1792000000","accessed":"2026-10-14 08:00:00","count":"1",
             "players":[{"player_id":1,"player_name":"Test Player","player_team_id":"KC","player_position_id":"WR","player_opponent":"vs. CAR","player_opponent_id":"CAR","pos_rank":"WR1",
                         "rank_ecr":1,"rank_min":"1","rank_max":"3","rank_ave":"1.75","rank_std":"0.50","start_sit_grade":"A","player_ecr_delta":"2","player_bye_week":"10",
                         "player_game_kickoff_ts":"1792100000","player_game_status":"open","player_page_url":"https://www.fantasypros.com/nfl/players/test.php"}]}
            """;

        var page = EcrPageParser.Parse(WrapInPage(json));
        var player = Assert.Single(page.Players);

        Assert.Equal(2026, page.Year);
        Assert.Equal(7, page.Week);
        Assert.Equal(12, page.TotalExperts);
        Assert.Equal(1792000000L, page.LastUpdatedTs);
        Assert.Equal(1, page.Count);
        Assert.Equal(1, player.RankMin);
        Assert.Equal(3, player.RankMax);
        Assert.Equal(1.75m, player.RankAve);
        Assert.Equal(0.50m, player.RankStd);
        Assert.Equal(2d, player.PlayerEcrDelta);
        Assert.Equal(10, player.PlayerByeWeek);
        Assert.Equal(1792100000L, player.PlayerGameKickoffTs);
    }

    [Fact]
    public void PageWithoutEcrData_Throws()
    {
        const string html = "<html><body><script>var sosData = {\"ARI\":{\"rb_stars\":1}};\n</script></body></html>";

        var ex = Assert.Throws<EcrPageParseException>(() => EcrPageParser.Parse(html));

        Assert.Contains("ecrData", ex.Message);
    }

    [Fact]
    public void EmptyPlayersArray_Throws()
    {
        const string json = """{"year":"2026","week":"2","position_id":"RB","scoring":"PPR","total_experts":140,"last_updated":"9/19","last_updated_ts":1789846940,"count":0,"players":[]}""";

        var ex = Assert.Throws<EcrPageParseException>(() => EcrPageParser.Parse(WrapInPage(json)));

        Assert.Contains("no players", ex.Message);
        Assert.Contains("RB", ex.Message);
    }

    [Fact]
    public void MissingPlayersProperty_Throws()
    {
        const string json = """{"year":"2026","week":"2","position_id":"RB","scoring":"PPR","count":0}""";

        Assert.Throws<EcrPageParseException>(() => EcrPageParser.Parse(WrapInPage(json)));
    }

    [Fact]
    public void MalformedBlob_Throws()
    {
        var ex = Assert.Throws<EcrPageParseException>(() => EcrPageParser.Parse(WrapInPage("{\"year\":\"2026\",\"players\":[")));

        // System.Text.Json throws the internal JsonReaderException subtype for malformed JSON, not
        // JsonException itself, so assert assignability rather than the exact type.
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    /// <summary>
    /// Fetches ppr-rb.php from fantasypros.com through <see cref="FantasyProsClient"/> and prints season, week
    /// and the top 5. Off by default because it needs the network; set FANTASYPROS_LIVE=1 to run it.
    /// </summary>
    [Fact]
    public async Task LiveSmoke_FetchesPprRbAndPrintsTopFive()
    {
        if (Environment.GetEnvironmentVariable("FANTASYPROS_LIVE") != "1")
        {
            output.WriteLine("Skipped: set FANTASYPROS_LIVE=1 to fetch the live page.");
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri("https://www.fantasypros.com"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", FantasyProsClient.UserAgent);
        http.DefaultRequestHeaders.Add("Accept", "text/html");
        var client = new FantasyProsClient(http, NullLogger<FantasyProsClient>.Instance);

        var html = await client.GetPageAsync("/nfl/rankings/ppr-rb.php");
        var page = EcrPageParser.Parse(html);

        output.WriteLine($"Season {page.Year} week {page.Week} {page.Scoring} {page.PositionId}: {page.Players.Count} players, {page.TotalExperts} experts, updated {page.LastUpdated}");
        foreach (var p in page.Players.Take(5))
            output.WriteLine($"  {p.RankEcr,3} {p.PosRank,-5} {p.PlayerName,-24} {p.PlayerTeamId,-4} {p.PlayerOpponent,-8} ave {p.RankAve} ({p.RankMin}-{p.RankMax}) {p.StartSitGrade}");

        Assert.True(page.Year >= 2026);
        Assert.InRange(page.Week, 1, 18);
        Assert.Equal("RB", page.PositionId);
        Assert.True(page.Players.Count >= 70, "expected at least the RB cutoff worth of players");
    }
}

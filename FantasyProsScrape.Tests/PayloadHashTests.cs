using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FantasyProsScrape.Services;

namespace FantasyProsScrape.Tests;

/// <summary>
/// PayloadHasher is pure and static: it only ever sees in-memory JSON text, no network or
/// database. See docs/plans/fantasy-pros-ranks-scraper.md, "FantasyProsRanksJob" step 3.
/// </summary>
public class PayloadHashTests
{
    private const string PlayersArray = """[{"player_id":1,"player_name":"A Player","rank_ecr":1},{"player_id":2,"player_name":"B Player","rank_ecr":2}]""";

    private static string EcrData(string accessed, string lastUpdatedTs, string playersJson) =>
        $$"""{"year":2026,"week":3,"position_id":2,"scoring":"PPR","total_experts":50,"last_updated":"9/19","last_updated_ts":{{lastUpdatedTs}},"accessed":"{{accessed}}","count":2,"players":{{playersJson}}}""";

    [Fact]
    public void Hash_IsLowercaseHexSha256_OfUtf8BytesExactlyAsGiven()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PlayersArray)));

        var actual = PayloadHasher.Hash(PlayersArray);

        Assert.Equal(expected, actual);
        Assert.Equal(64, actual.Length);
        Assert.Equal(actual, actual.ToLowerInvariant());
    }

    [Fact]
    public void Hash_IsDeterministic_SameInputSameOutput()
    {
        Assert.Equal(PayloadHasher.Hash(PlayersArray), PayloadHasher.Hash(PlayersArray));
    }

    [Fact]
    public void TwoPayloads_DifferingOnlyInAccessedAndLastUpdatedTs_HashIdentically()
    {
        var first = EcrData(accessed: "2026-09-19T12:00:00Z", lastUpdatedTs: "1758283200", PlayersArray);
        var second = EcrData(accessed: "2026-09-19T18:30:00Z", lastUpdatedTs: "1758304200", PlayersArray);

        var hash1 = PayloadHasher.HashPlayersOf(first);
        var hash2 = PayloadHasher.HashPlayersOf(second);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void PayloadWithOneRankMoved_HashesDifferently()
    {
        const string movedPlayers = """[{"player_id":1,"player_name":"A Player","rank_ecr":1},{"player_id":2,"player_name":"B Player","rank_ecr":3}]""";
        var original = EcrData(accessed: "2026-09-19T12:00:00Z", lastUpdatedTs: "1758283200", PlayersArray);
        var moved = EcrData(accessed: "2026-09-19T12:00:00Z", lastUpdatedTs: "1758283200", movedPlayers);

        var hashOriginal = PayloadHasher.HashPlayersOf(original);
        var hashMoved = PayloadHasher.HashPlayersOf(moved);

        Assert.NotEqual(hashOriginal, hashMoved);
    }

    [Fact]
    public void OneCharacterChangeInsidePlayersArray_HashesDifferently()
    {
        const string original = """[{"player_id":1,"player_name":"A Player","rank_ecr":1}]""";
        const string oneCharChanged = """[{"player_id":1,"player_name":"A Player","rank_ecr":2}]""";

        Assert.NotEqual(PayloadHasher.Hash(original), PayloadHasher.Hash(oneCharChanged));
    }

    [Fact]
    public void HashOfJsonElement_MatchesHashOfStringItWasParsedFrom()
    {
        using var doc = JsonDocument.Parse(PlayersArray);

        var fromElement = PayloadHasher.Hash(doc.RootElement);
        var fromString = PayloadHasher.Hash(PlayersArray);

        Assert.Equal(fromString, fromElement);
    }

    [Fact]
    public void HashOfJsonElement_NotAnArray_Throws()
    {
        using var doc = JsonDocument.Parse("""{"not":"an array"}""");

        Assert.Throws<ArgumentException>(() => PayloadHasher.Hash(doc.RootElement));
    }

    [Fact]
    public void HashPlayersOf_MatchesDirectHashOfPlayersArrayText()
    {
        var ecrData = EcrData(accessed: "2026-09-19T12:00:00Z", lastUpdatedTs: "1758283200", PlayersArray);

        var viaEcrData = PayloadHasher.HashPlayersOf(ecrData);
        var direct = PayloadHasher.Hash(PlayersArray);

        Assert.Equal(direct, viaEcrData);
    }

    [Fact]
    public void HashPlayersOf_MissingPlayersArray_Throws()
    {
        const string noPlayers = """{"year":2026,"week":3}""";

        Assert.Throws<ArgumentException>(() => PayloadHasher.HashPlayersOf(noPlayers));
    }

    [Fact]
    public void HashPlayersOf_PlayersIsNotAnArray_Throws()
    {
        const string playersNotArray = """{"players":"oops"}""";

        Assert.Throws<ArgumentException>(() => PayloadHasher.HashPlayersOf(playersNotArray));
    }

    [Fact]
    public void Hash_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PayloadHasher.Hash((string)null!));
    }
}

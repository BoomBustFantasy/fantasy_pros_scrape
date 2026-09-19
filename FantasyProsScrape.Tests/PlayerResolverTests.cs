using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Services;

namespace FantasyProsScrape.Tests;

public class PlayerResolverTests
{
    private const long Chiefs = 16;
    private const long Niners = 28;
    private const long FreeAgents = PlayerResolver.FreeAgentTeamId;
    private const long QB = 1;
    private const long RB = 2;
    private const long WR = 3;
    private const long TE = 4;

    private static Player Db(long id, string first, string last, long team, long position = RB, int? fpId = null) =>
        new() { Id = id, FirstName = first, LastName = last, TeamId = team, PositionId = position, FantasyProsPlayerId = fpId };

    [Fact]
    public void Existing_fantasy_pros_id_wins_regardless_of_name_or_team()
    {
        var players = new List<Player>
        {
            Db(1, "Christian", "McCaffrey", Niners, fpId: 9509),
            Db(2, "Christian", "McCaffrey", FreeAgents),
        };

        var resolved = PlayerResolver.Resolve(players, 9509, "C. McCaffrey", Chiefs, QB);

        Assert.Equal(1, resolved);
    }

    [Fact]
    public void Matches_by_name_within_the_fantasy_pros_team()
    {
        var players = new List<Player>
        {
            Db(1, "Christian", "McCaffrey", Niners),
            Db(2, "Christian", "McCaffrey", Chiefs),
        };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, "Christian McCaffrey", Niners, RB));
        Assert.Equal(2, PlayerResolver.Resolve(players, 0, "Christian McCaffrey", Chiefs, RB));
    }

    [Fact]
    public void Ambiguous_team_scoped_exact_matches_return_null_and_do_not_fall_through_to_prefix()
    {
        var players = new List<Player>
        {
            Db(1, "Josh", "Allen", Niners, QB),
            Db(2, "Josh", "Allen", Niners, QB),
            Db(3, "Joshua", "Allen", Niners, QB), // would win a prefix fallback if the ambiguity guard didn't stop the search
        };

        Assert.Null(PlayerResolver.Resolve(players, 0, "Josh Allen", Niners, QB));
    }

    [Fact]
    public void Falls_back_to_the_free_agent_bucket_when_fantasy_pros_still_lists_a_stale_team()
    {
        var players = new List<Player>
        {
            Db(1, "Someone", "Else", Niners),
            Db(2, "Christian", "McCaffrey", FreeAgents),
        };

        Assert.Equal(2, PlayerResolver.Resolve(players, 0, "Christian McCaffrey", Niners, RB));
    }

    [Fact]
    public void Falls_back_to_a_unique_exact_name_anywhere_across_a_team_abbreviation_mismatch()
    {
        var players = new List<Player>
        {
            Db(1, "Christian", "McCaffrey", Chiefs), // e.g. FantasyPros team code didn't map to our stored team
        };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, "Christian McCaffrey", Niners, RB));
    }

    [Fact]
    public void Global_exact_match_is_ambiguous_when_not_unique()
    {
        var players = new List<Player>
        {
            Db(1, "Chris", "Godwin", Chiefs, WR),
            Db(2, "Chris", "Godwin", Niners, WR),
        };

        Assert.Null(PlayerResolver.Resolve(players, 0, "Chris Godwin", FreeAgents, WR));
    }

    [Fact]
    public void Prefix_fallback_resolves_josh_joshua_position_scoped_and_unique()
    {
        var players = new List<Player> { Db(1, "Joshua", "Palmer", Chiefs, WR) };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, "Josh Palmer", FreeAgents, WR));
    }

    [Fact]
    public void Prefix_fallback_returns_null_when_still_ambiguous()
    {
        var players = new List<Player>
        {
            Db(1, "Joshua", "Palmer", Chiefs, WR),
            Db(2, "Joshua", "Palmer", Niners, WR),
        };

        Assert.Null(PlayerResolver.Resolve(players, 0, "Josh Palmer", FreeAgents, WR));
    }

    [Fact]
    public void Prefix_fallback_never_matches_an_unrelated_first_name()
    {
        // "Marcus" is neither a prefix of "Christian" nor vice versa (unlike Josh/Joshua), so an
        // exact last-name match alone must not be enough.
        var players = new List<Player> { Db(1, "Marcus", "McCaffrey", Chiefs, RB) };

        Assert.Null(PlayerResolver.Resolve(players, 0, "Christian McCaffrey", FreeAgents, RB));
    }

    [Theory]
    [InlineData("Aaron Jones Sr.", "Aaron", "Jones")]
    [InlineData("Kenneth Walker III", "Kenneth", "Walker")]
    [InlineData("D'Andre Swift", "DAndre", "Swift")]
    [InlineData("C.J. Stroud", "CJ", "Stroud")]
    [InlineData("Amon-Ra St. Brown", "Amon-Ra", "St Brown")]
    public void Normalises_suffixes_periods_and_apostrophes(string fpName, string dbFirst, string dbLast)
    {
        var players = new List<Player> { Db(1, dbFirst, dbLast, Chiefs, RB) };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, fpName, Chiefs, RB));
    }

    [Theory]
    [InlineData("Hollywood Brown", "Marquise", "Brown")]
    [InlineData("Bam Knight", "Zonovan", "Knight")]
    [InlineData("Chig Okonkwo", "Chigoziem", "Okonkwo")]
    [InlineData("Cam Ward", "Cameron", "Ward")]
    [InlineData("Gabe Davis", "Gabriel", "Davis")]
    public void Resolves_known_fantasy_pros_nicknames_via_the_synonym_map(string fpName, string dbFirst, string dbLast)
    {
        var players = new List<Player> { Db(1, dbFirst, dbLast, Chiefs, WR) };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, fpName, Chiefs, WR));
    }

    [Fact]
    public void Synonym_map_also_works_in_reverse()
    {
        // Players row already carries the nickname; FantasyPros happens to use the formal name.
        var players = new List<Player> { Db(1, "Hollywood", "Brown", Chiefs, WR) };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, "Marquise Brown", Chiefs, WR));
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        var players = new List<Player> { Db(1, "Someone", "Else", Niners) };

        Assert.Null(PlayerResolver.Resolve(players, 0, "Christian McCaffrey", Niners, RB));
    }

    [Fact]
    public void Returns_null_for_a_blank_name_with_no_fantasy_pros_id()
    {
        var players = new List<Player> { Db(1, "Christian", "McCaffrey", Niners) };

        Assert.Null(PlayerResolver.Resolve(players, 0, "", Niners, RB));
    }

    [Fact]
    public void Unknown_team_still_matches_through_free_agents_and_global()
    {
        var players = new List<Player> { Db(1, "Christian", "McCaffrey", Niners) };

        Assert.Equal(1, PlayerResolver.Resolve(players, 0, "Christian McCaffrey", null, RB));
    }
}

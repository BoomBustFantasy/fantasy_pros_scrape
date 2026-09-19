namespace FantasyProsScrape.Utils;

/// <summary>
/// Maps FantasyPros' <c>player_team_id</c> codes to <c>Teams.abbreviation</c>.
///
/// Verified Sep 19, 2026 by pulling all four live rankings pages (qb.php, ppr-rb.php,
/// ppr-wr.php, ppr-te.php) and comparing the observed player_team_id values against the Teams
/// table in Supabase. FantasyPros currently emits the common short codes (GB, KC, LV, NE, NO,
/// SF, TB, WAS, FA); the Teams table uses Pro-Football-Reference style codes for four teams
/// (GNB, KAN, LVR, NWE, NOR, SFO, TAM) and otherwise matches exactly. FantasyPros' Jacksonville
/// and Washington codes were not observed as anything other than JAC/WAS on today's pages, but
/// JAX and WSH are accepted too (documented divergences per the project plan and the sibling
/// scrapers' team mappers) in case FantasyPros changes them or a different page uses them.
/// Assumption: unverified alternates (SD, STL, OAK, LA) are carried over from yahoo_scrape's
/// mapper for the same historical-relocation safety net; FantasyPros has not been observed
/// emitting them.
/// </summary>
public static class TeamAbbreviations
{
    /// <summary>Teams.id of the free-agent bucket (abbreviation FA).</summary>
    public const long FreeAgentTeamId = 33;

    public const string FreeAgent = "FA";

    private static readonly Dictionary<string, string> FantasyProsToSupabase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ARI"] = "ARI",
        ["ATL"] = "ATL",
        ["BAL"] = "BAL",
        ["BUF"] = "BUF",
        ["CAR"] = "CAR",
        ["CHI"] = "CHI",
        ["CIN"] = "CIN",
        ["CLE"] = "CLE",
        ["DAL"] = "DAL",
        ["DEN"] = "DEN",
        ["DET"] = "DET",
        ["GB"] = "GNB", ["GNB"] = "GNB",
        ["HOU"] = "HOU",
        ["IND"] = "IND",
        ["JAC"] = "JAX", ["JAX"] = "JAX",
        ["KC"] = "KAN", ["KAN"] = "KAN",
        ["LAC"] = "LAC", ["SD"] = "LAC", ["SDG"] = "LAC",
        ["LAR"] = "LAR", ["LA"] = "LAR", ["STL"] = "LAR",
        ["LV"] = "LVR", ["LVR"] = "LVR", ["OAK"] = "LVR",
        ["MIA"] = "MIA",
        ["MIN"] = "MIN",
        ["NE"] = "NWE", ["NWE"] = "NWE",
        ["NO"] = "NOR", ["NOR"] = "NOR",
        ["NYG"] = "NYG",
        ["NYJ"] = "NYJ",
        ["PHI"] = "PHI",
        ["PIT"] = "PIT",
        ["SF"] = "SFO", ["SFO"] = "SFO",
        ["SEA"] = "SEA",
        ["TB"] = "TAM", ["TAM"] = "TAM",
        ["TEN"] = "TEN",
        ["WAS"] = "WAS", ["WSH"] = "WAS",
        [FreeAgent] = FreeAgent,
    };

    /// <summary>
    /// The Teams.abbreviation for a FantasyPros team code. Blank maps to the free-agent bucket;
    /// an unrecognised code returns null so the caller can log it rather than guess.
    /// </summary>
    public static string? ToSupabaseAbbreviation(string? fantasyProsCode)
    {
        if (string.IsNullOrWhiteSpace(fantasyProsCode))
            return FreeAgent;

        return FantasyProsToSupabase.TryGetValue(fantasyProsCode.Trim(), out var abbreviation) ? abbreviation : null;
    }
}

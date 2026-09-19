using System.Text.RegularExpressions;
using FantasyProsScrape.Models.Supa;
using FantasyProsScrape.Utils;

namespace FantasyProsScrape.Services;

/// <summary>
/// Resolves a FantasyPros player to a row in our Players table. Same shape as the Yahoo and
/// ESPN scrapers' resolvers so behaviour is predictable across platforms. Deliberately takes
/// primitives rather than an EcrPlayer (owned by FEAT-4/FEAT-7) so this stays independent of the
/// parsing model.
///
/// Resolution order:
///   1. FantasyPros id fast path: a row already carrying this fantasy_pros_player_id.
///   2. Team-scoped exact normalized-name match on the team FantasyPros says the player is on.
///   3. Free-agent fallback: the same exact match against team 33, because a player FantasyPros
///      still lists on a team may have been released since our last roster sync.
///   4. Global exact-name match, only when it is unique (catches team-abbreviation mismatches).
///   5. Prefix fallback: exact last name plus a bidirectional first-name prefix (Josh/Joshua),
///      only when it is unique.
/// Every name step is scoped to the position of the page the player was scraped from. More than
/// one exact hit at any step is ambiguous and returns null without falling through to a fuzzier
/// step. A small synonym map covers FantasyPros nicknames (Hollywood Brown, Bam Knight, ...),
/// same list as the KTC scraper. Never writes to the database.
/// </summary>
public static class PlayerResolver
{
    public const long FreeAgentTeamId = TeamAbbreviations.FreeAgentTeamId;

    /// <summary>
    /// Normalized full-name synonyms, bidirectional. Whichever spelling FantasyPros uses and
    /// whichever spelling the Players row carries, both are tried.
    /// </summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["CAM WARD"] = "CAMERON WARD",
        ["CAMERON WARD"] = "CAM WARD",
        ["GABRIEL DAVIS"] = "GABE DAVIS",
        ["GABE DAVIS"] = "GABRIEL DAVIS",
        ["MARQUISE BROWN"] = "HOLLYWOOD BROWN",
        ["HOLLYWOOD BROWN"] = "MARQUISE BROWN",
        ["CHIGOZIEM OKONKWO"] = "CHIG OKONKWO",
        ["CHIG OKONKWO"] = "CHIGOZIEM OKONKWO",
        ["ZONOVAN KNIGHT"] = "BAM KNIGHT",
        ["BAM KNIGHT"] = "ZONOVAN KNIGHT",
    };

    public static long? Resolve(IReadOnlyList<Player> allPlayers, int fantasyProsPlayerId, string playerName, long? supabaseTeamId, long positionId)
    {
        // Step 1: already linked
        if (fantasyProsPlayerId > 0)
        {
            var byId = allPlayers.FirstOrDefault(p => p.FantasyProsPlayerId == fantasyProsPlayerId);
            if (byId is not null)
                return byId.Id;
        }

        var names = NameVariants(playerName);
        if (names.Count == 0)
            return null;

        var byPosition = allPlayers.Where(p => p.PositionId == positionId).ToList();

        // Step 2: team scoped
        if (supabaseTeamId.HasValue && supabaseTeamId.Value != FreeAgentTeamId)
        {
            var (teamMatch, ambiguous) = ExactMatch(byPosition.Where(p => p.TeamId == supabaseTeamId.Value), names);
            if (teamMatch.HasValue || ambiguous)
                return teamMatch;
        }

        // Step 3: free agents
        var (freeAgentMatch, freeAgentAmbiguous) = ExactMatch(byPosition.Where(p => p.TeamId == FreeAgentTeamId), names);
        if (freeAgentMatch.HasValue || freeAgentAmbiguous)
            return freeAgentMatch;

        // Step 4: anywhere, but only an exact and unique hit
        var (globalMatch, globalAmbiguous) = ExactMatch(byPosition, names);
        if (globalMatch.HasValue || globalAmbiguous)
            return globalMatch;

        // Step 5: exact last name, first-name prefix either way, still unique
        var fuzzy = byPosition.Where(p => IsPrefixMatch(p, names)).ToList();
        return fuzzy.Count == 1 ? fuzzy[0].Id : null;
    }

    /// <summary>One exact hit wins; several are ambiguous and stop the search (do not fall through).</summary>
    private static (long? Id, bool Ambiguous) ExactMatch(IEnumerable<Player> candidates, HashSet<string> names)
    {
        var exact = candidates.Where(p => names.Contains(FullName(p))).ToList();
        return exact.Count switch
        {
            0 => (null, false),
            1 => (exact[0].Id, false),
            _ => (null, true),
        };
    }

    /// <summary>
    /// "JOSH ALLEN" matches "JOSHUA ALLEN" and vice versa: the scraped name must end with the row's
    /// last name, and whichever first name is shorter must be a prefix of the other.
    /// </summary>
    private static bool IsPrefixMatch(Player player, HashSet<string> names)
    {
        var dbFirst = NormalizeName(player.FirstName);
        var dbLast = NormalizeName(player.LastName);
        if (dbFirst.Length == 0 || dbLast.Length == 0)
            return false;

        foreach (var name in names)
        {
            if (!name.EndsWith(" " + dbLast, StringComparison.Ordinal))
                continue;

            var fpFirst = name[..^(dbLast.Length + 1)].TrimEnd();
            if (fpFirst.Length == 0)
                continue;

            if (dbFirst.StartsWith(fpFirst, StringComparison.Ordinal) || fpFirst.StartsWith(dbFirst, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string FullName(Player player) => NormalizeName(player.FirstName + " " + player.LastName);

    /// <summary>The normalized scraped name plus any synonym for it.</summary>
    internal static HashSet<string> NameVariants(string? playerName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var normalized = NormalizeName(playerName);
        if (normalized.Length == 0)
            return names;

        names.Add(normalized);
        if (Synonyms.TryGetValue(normalized, out var synonym))
            names.Add(synonym);

        return names;
    }

    internal static string NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var normalized = name.Trim().ToUpperInvariant();
        normalized = normalized.Replace(".", string.Empty).Replace("'", string.Empty).Replace("’", string.Empty);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = Regex.Replace(normalized, @"\s+(JR|SR|III|IV|II|V)$", string.Empty);

        return normalized.Trim();
    }
}

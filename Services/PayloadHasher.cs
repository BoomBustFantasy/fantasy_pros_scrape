using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FantasyProsScrape.Services;

/// <summary>
/// Stable hash of a FantasyPros <c>ecrData.players</c> array, used to decide whether a run's raw
/// payload is worth storing. Only the players array is hashed: the top-level object carries
/// <c>accessed</c> and <c>last_updated_ts</c>, which change on every request.
/// </summary>
/// <remarks>
/// <see cref="Hash(string)"/> over the players array text exactly as embedded in the page is the
/// canonical form the job must use. The <see cref="Hash(JsonElement)"/> overload matches it only
/// when the element came from parsing that same text (<see cref="JsonElement.GetRawText"/> returns
/// the original input, not a re-serialization); an element built any other way hashes its own text.
/// </remarks>
public static class PayloadHasher
{
    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="playersJson"/>, byte for byte.</summary>
    public static string Hash(string playersJson)
    {
        ArgumentNullException.ThrowIfNull(playersJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(playersJson)));
    }

    /// <summary>
    /// Hash of a parsed players array. Equivalent to <see cref="Hash(string)"/> on the embedded
    /// text when <paramref name="players"/> was parsed from it; see the class remarks.
    /// </summary>
    public static string Hash(JsonElement players)
    {
        if (players.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Expected the players array, got {players.ValueKind}.", nameof(players));

        return Hash(players.GetRawText());
    }

    /// <summary>
    /// Hash the <c>players</c> array inside a full <c>ecrData</c> object, ignoring every other
    /// top-level field. The array text is taken as embedded, so this equals
    /// <see cref="Hash(string)"/> on that substring.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="ecrDataJson"/> is not an object with a <c>players</c> array.</exception>
    public static string HashPlayersOf(string ecrDataJson)
    {
        ArgumentNullException.ThrowIfNull(ecrDataJson);

        using var doc = JsonDocument.Parse(ecrDataJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("players", out var players)
            || players.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("ecrData has no players array.", nameof(ecrDataJson));
        }

        return Hash(players);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FantasyProsScrape.Models.Ecr;

namespace FantasyProsScrape.Services;

/// <summary>
/// Turns a FantasyPros rankings page into an <see cref="EcrPage"/> by lifting the inline
/// <c>var ecrData = {...};</c> assignment out of the HTML and deserialising it.
/// No HTML parser: the blob is the only thing on the page the scraper needs.
/// </summary>
public static partial class EcrPageParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // rank_min, rank_max, rank_ave, rank_std, week and year are served as JSON strings.
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Parses the page. Throws <see cref="EcrPageParseException"/> when the blob is missing, is not
    /// valid JSON, or ranks nobody, so a FantasyPros layout change fails the job loudly instead of
    /// silently writing zeros.
    /// </summary>
    public static EcrPage Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var match = EcrDataRegex().Match(html);
        if (!match.Success)
            throw new EcrPageParseException(
                "No `var ecrData = {...};` script assignment found in the page. FantasyPros may have changed the rankings page layout.");

        var json = match.Groups[1].Value;

        EcrPage? page;
        string playersRaw;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            page = root.Deserialize<EcrPage>(SerializerOptions);

            // GetRawText returns the element's bytes as they appeared in the input, which is what the
            // run-capture hash must be computed over.
            playersRaw = root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array
                ? players.GetRawText()
                : "[]";
        }
        catch (JsonException ex)
        {
            throw new EcrPageParseException("The ecrData blob is not valid JSON or does not match the expected shape.", ex);
        }

        if (page is null)
            throw new EcrPageParseException("The ecrData blob deserialised to null.");

        if (page.Players.Count == 0)
            throw new EcrPageParseException(
                $"The ecrData blob for {page.Scoring} {page.PositionId} week {page.Week} ({page.Year}) contains no players.");

        page.RawJson = json;
        page.PlayersRawJson = playersRaw;
        return page;
    }

    // Singleline so `.` spans the (single, very long) line the blob is served on; lazy so it stops at the
    // first `};` + newline, which is the end of the assignment because the JSON itself contains no newline.
    [GeneratedRegex(@"var ecrData = (\{.*?\});\s*\n", RegexOptions.Singleline)]
    private static partial Regex EcrDataRegex();
}

/// <summary>Raised when a rankings page cannot be turned into an <see cref="EcrPage"/>.</summary>
public sealed class EcrPageParseException : Exception
{
    public EcrPageParseException(string message) : base(message) { }

    public EcrPageParseException(string message, Exception innerException) : base(message, innerException) { }
}

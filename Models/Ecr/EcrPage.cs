using System.Text.Json.Serialization;

namespace FantasyProsScrape.Models.Ecr;

/// <summary>
/// The <c>var ecrData = {...};</c> blob embedded in a FantasyPros rankings page, typed.
/// Season and week come from here and only here: FantasyPros rolls the week after Monday night,
/// so the scrape window never straddles two weeks and no calendar maths is needed.
/// </summary>
public sealed class EcrPage
{
    /// <summary>Season, e.g. 2026. Served as a string; coerced on read.</summary>
    [JsonPropertyName("year")]
    public int Year { get; set; }

    /// <summary>NFL week the page ranks. Served as a string; coerced on read.</summary>
    [JsonPropertyName("week")]
    public int Week { get; set; }

    /// <summary>"QB", "RB", "WR" or "TE".</summary>
    [JsonPropertyName("position_id")]
    public string PositionId { get; set; } = string.Empty;

    /// <summary>"PPR" on the pages this scraper reads.</summary>
    [JsonPropertyName("scoring")]
    public string Scoring { get; set; } = string.Empty;

    /// <summary>Number of experts included in the consensus.</summary>
    [JsonPropertyName("total_experts")]
    public int TotalExperts { get; set; }

    /// <summary>Display date of the last consensus update, e.g. "9/19".</summary>
    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = string.Empty;

    /// <summary>Unix seconds of the last consensus update.</summary>
    [JsonPropertyName("last_updated_ts")]
    public long LastUpdatedTs { get; set; }

    /// <summary>Server time the page was rendered, e.g. "2026-09-19 19:44:49". Changes on every request.</summary>
    [JsonPropertyName("accessed")]
    public string Accessed { get; set; } = string.Empty;

    /// <summary>Number of ranked players FantasyPros reports; equals <see cref="Players"/>.Count on a healthy page.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("players")]
    public List<EcrPlayer> Players { get; set; } = [];

    /// <summary>
    /// The complete <c>ecrData</c> object exactly as embedded in the page (no reserialisation),
    /// for storing the raw payload as jsonb. Set by the parser, never deserialised.
    /// </summary>
    [JsonIgnore]
    public string RawJson { get; set; } = string.Empty;

    /// <summary>
    /// The <c>players</c> array exactly as embedded in the page (no reserialisation). Hash this, not
    /// <see cref="RawJson"/>: the top level carries <c>accessed</c> and <c>last_updated_ts</c>, which
    /// change between otherwise identical responses. Set by the parser, never deserialised.
    /// </summary>
    [JsonIgnore]
    public string PlayersRawJson { get; set; } = string.Empty;
}

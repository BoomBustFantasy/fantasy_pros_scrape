using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

/// <summary>
/// One row per (season, week): the container <see cref="SeedWeeklyRanksJob"/> creates on Tuesday
/// night from consensus, that Jack reorders and publishes from the Boom admin page. Unique on
/// (season, week) - the seed job checks for an existing row first and never overwrites one. See
/// the companion plan boom/docs/plans/weekly-ranks.md, "WeeklyRankSets / WeeklyRanks".
/// </summary>
[Table("WeeklyRankSets")]
public class WeeklyRankSet : BaseModel
{
    [PrimaryKey("id", false)]
    [JsonPropertyName("id")]
    [Column("id")]
    public long Id { get; set; }

    [JsonPropertyName("season")]
    [Column("season")]
    public int Season { get; set; }

    [JsonPropertyName("week")]
    [Column("week")]
    public int Week { get; set; }

    [JsonPropertyName("seeded_at")]
    [Column("seeded_at")]
    public DateTime SeededAt { get; set; }

    /// <summary>Null until Jack publishes from the Boom admin page. This job never sets it.</summary>
    [JsonPropertyName("published_at")]
    [Column("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

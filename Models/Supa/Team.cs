using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

[Table("Teams")]
public class Team : BaseModel
{
    [PrimaryKey("id")]
    [JsonPropertyName("id")]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>Pro-Football-Reference style code (GNB, JAX, KAN, ...). See <see cref="FantasyProsScrape.Utils.TeamAbbreviations"/>.</summary>
    [JsonPropertyName("abbreviation")]
    [Column("abbreviation")]
    public string Abbreviation { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    [Column("full_name")]
    public string FullName { get; set; } = string.Empty;
}

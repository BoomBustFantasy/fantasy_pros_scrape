using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

[Table("Positions")]
public class Position : BaseModel
{
    [PrimaryKey("id")]
    [JsonPropertyName("id")]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>QB, RB, WR, TE or PICK.</summary>
    [JsonPropertyName("name")]
    [Column("name")]
    public string Name { get; set; } = string.Empty;
}

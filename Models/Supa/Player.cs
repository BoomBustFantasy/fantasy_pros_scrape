using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

/// <summary>
/// The slice of the Players table this service reads. Writes go through a targeted
/// column update (see PlayerRepository, FEAT-8) so fields owned by other scrapers are never
/// touched.
/// </summary>
[Table("Players")]
public class Player : BaseModel
{
    [PrimaryKey("id")]
    [JsonPropertyName("id")]
    [Column("id")]
    public long Id { get; set; }

    [JsonPropertyName("first_name")]
    [Column("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last_name")]
    [Column("last_name")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("team_id")]
    [Column("team_id")]
    public long TeamId { get; set; }

    [JsonPropertyName("position_id")]
    [Column("position_id")]
    public long? PositionId { get; set; }

    /// <summary>FantasyPros' integer player id (ecrData.players[].player_id). Backfilled by name resolution.</summary>
    [JsonPropertyName("fantasy_pros_player_id")]
    [Column("fantasy_pros_player_id")]
    public int? FantasyProsPlayerId { get; set; }

    [JsonPropertyName("sleeper_id")]
    [Column("sleeper_id")]
    public long? SleeperId { get; set; }

    [JsonPropertyName("espn_player_id")]
    [Column("espn_player_id")]
    public string? EspnPlayerId { get; set; }

    [JsonPropertyName("yahoo_player_id")]
    [Column("yahoo_player_id")]
    public string? YahooPlayerId { get; set; }

    [JsonPropertyName("active")]
    [Column("active")]
    public bool? Active { get; set; }

    [JsonPropertyName("created_at")]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

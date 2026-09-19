using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

/// <summary>
/// Raw <c>ecrData</c> payload for one page fetch, stored only when
/// <see cref="PayloadHash"/> (SHA-256 of the <c>players</c> array, see <c>PayloadHasher</c>)
/// differs from the previous run for the same season/week/position/scoring.
/// </summary>
[Table("FantasyProsRankRuns")]
public class FantasyProsRankRun : BaseModel
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

    [JsonPropertyName("position_id")]
    [Column("position_id")]
    public long PositionId { get; set; }

    [JsonPropertyName("scoring")]
    [Column("scoring")]
    public string Scoring { get; set; } = "PPR";

    /// <summary>Lowercase hex SHA-256 of the <c>players</c> array text exactly as embedded.</summary>
    [JsonPropertyName("payload_hash")]
    [Column("payload_hash")]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>The full <c>ecrData</c> object as JSON text; PostgREST stores it as jsonb.</summary>
    [JsonPropertyName("payload")]
    [Column("payload")]
    public string Payload { get; set; } = string.Empty;

    [JsonPropertyName("total_experts")]
    [Column("total_experts")]
    public int? TotalExperts { get; set; }

    /// <summary><c>ecrData.last_updated</c>, e.g. "9/19".</summary>
    [JsonPropertyName("fp_last_updated")]
    [Column("fp_last_updated")]
    public string? FpLastUpdated { get; set; }

    [JsonPropertyName("fetched_at")]
    [Column("fetched_at")]
    public DateTime FetchedAt { get; set; }
}

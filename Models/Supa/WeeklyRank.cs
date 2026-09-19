using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

/// <summary>
/// One player's slot within a <see cref="WeeklyRankSet"/>. At seed time <see cref="Rank"/> and
/// <see cref="SeededRank"/> are the same value - the consensus order the row was created from - and
/// only <see cref="Rank"/> ever changes afterward, when Jack reorders from the Boom admin page.
/// Unique on (set_id, position_id, player_id); a deferrable unique on (set_id, position_id, rank)
/// backs a reorder written as one transaction, but is not represented on this model. See the
/// companion plan boom/docs/plans/weekly-ranks.md, "WeeklyRankSets / WeeklyRanks".
/// </summary>
[Table("WeeklyRanks")]
public class WeeklyRank : BaseModel
{
    [PrimaryKey("id", false)]
    [JsonPropertyName("id")]
    [Column("id")]
    public long Id { get; set; }

    [JsonPropertyName("set_id")]
    [Column("set_id")]
    public long SetId { get; set; }

    [JsonPropertyName("position_id")]
    [Column("position_id")]
    public long PositionId { get; set; }

    [JsonPropertyName("player_id")]
    [Column("player_id")]
    public long PlayerId { get; set; }

    [JsonPropertyName("rank")]
    [Column("rank")]
    public int Rank { get; set; }

    /// <summary>Consensus rank at seed time; never changes after the seed.</summary>
    [JsonPropertyName("seeded_rank")]
    [Column("seeded_rank")]
    public int SeededRank { get; set; }

    [JsonPropertyName("updated_at")]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

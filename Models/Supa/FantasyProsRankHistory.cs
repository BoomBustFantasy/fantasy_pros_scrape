using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

/// <summary>
/// Append-only log of <c>rank_ecr</c> movement. One row on first appearance
/// (<see cref="PreviousRankEcr"/> null) and one per subsequent change. Movement in
/// ave/min/max/std/grade/opponent alone is never recorded.
/// </summary>
/// <remarks>
/// <see cref="RankId"/> is nullable on the model only: the column is NOT NULL. RankDiffer cannot
/// know the id of a brand-new FantasyProsRanks row until the upsert has returned, so it leaves
/// <see cref="RankId"/> null on first-appearance rows and the job fills it in from the upsert
/// response (see <c>RankDiffer.AttachRankIds</c>) before inserting.
/// </remarks>
[Table("FantasyProsRankHistory")]
public class FantasyProsRankHistory : BaseModel
{
    [PrimaryKey("id", false)]
    [JsonPropertyName("id")]
    [Column("id")]
    public long Id { get; set; }

    [JsonPropertyName("rank_id")]
    [Column("rank_id")]
    public long? RankId { get; set; }

    [JsonPropertyName("season")]
    [Column("season")]
    public int Season { get; set; }

    [JsonPropertyName("week")]
    [Column("week")]
    public int Week { get; set; }

    [JsonPropertyName("position_id")]
    [Column("position_id")]
    public long PositionId { get; set; }

    [JsonPropertyName("player_id")]
    [Column("player_id")]
    public long PlayerId { get; set; }

    /// <summary>Null on first appearance.</summary>
    [JsonPropertyName("previous_rank_ecr")]
    [Column("previous_rank_ecr")]
    public int? PreviousRankEcr { get; set; }

    [JsonPropertyName("new_rank_ecr")]
    [Column("new_rank_ecr")]
    public int NewRankEcr { get; set; }

    [JsonPropertyName("changed_at")]
    [Column("changed_at")]
    public DateTime ChangedAt { get; set; }
}

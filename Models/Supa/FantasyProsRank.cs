using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace FantasyProsScrape.Models.Supa;

/// <summary>
/// One current-consensus row per (season, week, position_id, scoring, player_id). Upserted by the
/// ranks job on that natural key; never deleted mid-week once a player appears.
/// </summary>
/// <remarks>
/// <c>id</c> is an identity column, so the primary key is marked <c>shouldInsert: false</c>: the
/// upsert payload carries the natural key and PostgREST resolves the conflict on it. New rows get
/// a fresh id; existing rows keep theirs. Read <see cref="Id"/> back from the upsert response.
/// </remarks>
[Table("FantasyProsRanks")]
public class FantasyProsRank : BaseModel
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

    [JsonPropertyName("player_id")]
    [Column("player_id")]
    public long PlayerId { get; set; }

    [JsonPropertyName("fantasy_pros_player_id")]
    [Column("fantasy_pros_player_id")]
    public int FantasyProsPlayerId { get; set; }

    [JsonPropertyName("rank_ecr")]
    [Column("rank_ecr")]
    public int RankEcr { get; set; }

    [JsonPropertyName("rank_ave")]
    [Column("rank_ave")]
    public decimal? RankAve { get; set; }

    [JsonPropertyName("rank_min")]
    [Column("rank_min")]
    public int? RankMin { get; set; }

    [JsonPropertyName("rank_max")]
    [Column("rank_max")]
    public int? RankMax { get; set; }

    [JsonPropertyName("rank_std")]
    [Column("rank_std")]
    public decimal? RankStd { get; set; }

    /// <summary>Positional rank label, e.g. "RB1".</summary>
    [JsonPropertyName("pos_rank")]
    [Column("pos_rank")]
    public string? PosRank { get; set; }

    /// <summary>Start/sit grade, e.g. "A+".</summary>
    [JsonPropertyName("start_sit_grade")]
    [Column("start_sit_grade")]
    public string? StartSitGrade { get; set; }

    /// <summary>Opponent as FantasyPros prints it, e.g. "at BUF" / "vs. CAR".</summary>
    [JsonPropertyName("opponent")]
    [Column("opponent")]
    public string? Opponent { get; set; }

    /// <summary>Opponent abbreviation, e.g. "BUF".</summary>
    [JsonPropertyName("opponent_abbreviation")]
    [Column("opponent_abbreviation")]
    public string? OpponentAbbreviation { get; set; }

    [JsonPropertyName("first_seen_at")]
    [Column("first_seen_at")]
    public DateTime FirstSeenAt { get; set; }

    [JsonPropertyName("updated_at")]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

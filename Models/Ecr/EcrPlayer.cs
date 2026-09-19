using System.Text.Json.Serialization;

namespace FantasyProsScrape.Models.Ecr;

/// <summary>
/// One entry of <c>ecrData.players[]</c> on a FantasyPros rankings page.
/// Property names mirror the JSON keys. FantasyPros serves rank_min/max/ave/std and player_bye_week
/// as strings; the parser reads them with <see cref="System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString"/>.
/// Fields that are null on the page for players on bye are nullable here.
/// </summary>
public sealed class EcrPlayer
{
    /// <summary>FantasyPros' own integer player id. Stored in Players.fantasy_pros_player_id once resolved.</summary>
    [JsonPropertyName("player_id")]
    public int PlayerId { get; set; }

    [JsonPropertyName("player_name")]
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>FantasyPros team abbreviation, e.g. "DET". Not necessarily identical to Teams.abbreviation.</summary>
    [JsonPropertyName("player_team_id")]
    public string PlayerTeamId { get; set; } = string.Empty;

    /// <summary>"QB", "RB", "WR" or "TE" on the pages this scraper reads.</summary>
    [JsonPropertyName("player_position_id")]
    public string PlayerPositionId { get; set; } = string.Empty;

    /// <summary>Display opponent, e.g. "at BUF" or "vs. CAR". Empty when FantasyPros has no scheduled
    /// game for the player (bye week or no active roster spot).</summary>
    [JsonPropertyName("player_opponent")]
    public string PlayerOpponent { get; set; } = string.Empty;

    /// <summary>Opponent team abbreviation; null when there is no scheduled game.</summary>
    [JsonPropertyName("player_opponent_id")]
    public string? PlayerOpponentId { get; set; }

    /// <summary>Position rank label, e.g. "RB1".</summary>
    [JsonPropertyName("pos_rank")]
    public string PosRank { get; set; } = string.Empty;

    /// <summary>Expert consensus rank within the position. The only field whose change writes history.</summary>
    [JsonPropertyName("rank_ecr")]
    public int RankEcr { get; set; }

    [JsonPropertyName("rank_min")]
    public int RankMin { get; set; }

    [JsonPropertyName("rank_max")]
    public int RankMax { get; set; }

    [JsonPropertyName("rank_ave")]
    public decimal RankAve { get; set; }

    [JsonPropertyName("rank_std")]
    public decimal RankStd { get; set; }

    /// <summary>Letter grade such as "A+" or "C-".</summary>
    [JsonPropertyName("start_sit_grade")]
    public string StartSitGrade { get; set; } = string.Empty;

    /// <summary>Rank movement since the previous update. Null when FantasyPros has none to report.</summary>
    [JsonPropertyName("player_ecr_delta")]
    public double? PlayerEcrDelta { get; set; }

    /// <summary>Bye week number; null when FantasyPros does not report one.</summary>
    [JsonPropertyName("player_bye_week")]
    public int? PlayerByeWeek { get; set; }

    /// <summary>Unix seconds of the player's game kickoff; null on a bye week.</summary>
    [JsonPropertyName("player_game_kickoff_ts")]
    public long? PlayerGameKickoffTs { get; set; }

    /// <summary>E.g. "closed"; null on a bye week.</summary>
    [JsonPropertyName("player_game_status")]
    public string? PlayerGameStatus { get; set; }

    [JsonPropertyName("player_page_url")]
    public string PlayerPageUrl { get; set; } = string.Empty;
}

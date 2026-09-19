using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// Everything <see cref="PlayerResolver"/> needs, loaded once per job run (not once per page).
/// Read-only for FEAT-7 (S1, dry run): the <c>fantasy_pros_player_id</c> backfill PATCH is
/// FEAT-8's "enable writes" and does not exist on this interface yet.
/// </summary>
public interface IPlayerRepository
{
    /// <summary>
    /// Players at QB/RB/WR/TE, plus Positions.id by name and Teams.id by Teams.abbreviation so the
    /// job can resolve a FantasyPros team code (via <see cref="Utils.TeamAbbreviations"/>) to a
    /// Supabase team id before calling <see cref="PlayerResolver.Resolve"/>.
    /// </summary>
    Task<PlayerResolutionData> GetSkillPlayersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Players/Positions/Teams data <see cref="PlayerResolver.Resolve"/> needs for one job run.
/// </summary>
/// <param name="Players">Every row in Players whose position_id is QB, RB, WR or TE.</param>
/// <param name="PositionIdByName">Positions.id keyed by name ("QB", "RB", "WR", "TE"), case-insensitive.</param>
/// <param name="TeamIdByAbbreviation">Teams.id keyed by Teams.abbreviation, case-insensitive.</param>
public sealed record PlayerResolutionData(
    List<Player> Players,
    IReadOnlyDictionary<string, long> PositionIdByName,
    IReadOnlyDictionary<string, long> TeamIdByAbbreviation);

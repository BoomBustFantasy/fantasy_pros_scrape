using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// Everything <see cref="PlayerResolver"/> needs, loaded once per job run (not once per page), plus
/// the <c>fantasy_pros_player_id</c> backfill PATCH.
/// </summary>
public interface IPlayerRepository
{
    /// <summary>
    /// Players at QB/RB/WR/TE, plus Positions.id by name and Teams.id by Teams.abbreviation so the
    /// job can resolve a FantasyPros team code (via <see cref="Utils.TeamAbbreviations"/>) to a
    /// Supabase team id before calling <see cref="PlayerResolver.Resolve"/>.
    /// </summary>
    Task<PlayerResolutionData> GetSkillPlayersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Column-level PATCH of <c>fantasy_pros_player_id</c> and <c>updated_at</c> for one player -
    /// never a full-row update, so a concurrent Sleeper/ESPN sync writing sleeper_id/espn_player_id
    /// on the same row can't be clobbered. Called after <see cref="PlayerResolver.Resolve"/>
    /// succeeds through a name match rather than the FantasyPros id fast path. Returns false (not
    /// thrown) on failure; the job logs and moves on rather than failing the run over a backfill.
    /// </summary>
    Task<bool> PatchFantasyProsPlayerIdAsync(
        long playerId, int fantasyProsPlayerId, CancellationToken cancellationToken = default);
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

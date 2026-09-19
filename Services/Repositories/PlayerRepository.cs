using Supabase.Postgrest;
using FantasyProsScrape.Models.Supa;
using Client = Supabase.Client;

namespace FantasyProsScrape.Services.Repositories;

/// <summary>
/// Reads the Players/Positions/Teams rows <see cref="PlayerResolver"/> needs, once per job run.
/// Read-only: see <see cref="IPlayerRepository"/> for why there is no write method yet.
/// </summary>
public class PlayerRepository : IPlayerRepository
{
    private static readonly string[] SkillPositions = ["QB", "RB", "WR", "TE"];

    /// <summary>Supabase caps a single PostgREST response at 1000 rows (its max-rows setting). Stay under it.</summary>
    private const int PageSize = 1000;

    private const string PlayerColumns = "id,first_name,last_name,team_id,position_id,fantasy_pros_player_id,active,created_at,updated_at";

    private readonly Client _supabase;
    private readonly ILogger<PlayerRepository> _logger;

    public PlayerRepository(Client supabase, ILogger<PlayerRepository> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<PlayerResolutionData> GetSkillPlayersAsync(CancellationToken cancellationToken = default)
    {
        var positionIdByName = await LoadSkillPositionIdsAsync(cancellationToken);
        var teamIdByAbbreviation = await LoadTeamIdsAsync(cancellationToken);

        var players = new List<Player>();
        if (positionIdByName.Count > 0)
        {
            var positionIds = positionIdByName.Values.Cast<object>().ToList();
            var offset = 0;

            while (true)
            {
                // Ordered by id so the range windows are stable while other scrapers write concurrently.
                var response = await _supabase
                    .From<Player>()
                    .Select(PlayerColumns)
                    .Filter("position_id", Constants.Operator.In, positionIds)
                    .Order("id", Constants.Ordering.Ascending)
                    .Range(offset, offset + PageSize - 1)
                    .Get(cancellationToken);

                players.AddRange(response.Models);

                if (response.Models.Count < PageSize)
                    break;

                offset += PageSize;
            }
        }

        _logger.LogInformation("Loaded {Count} QB/RB/WR/TE players for resolution", players.Count);

        return new PlayerResolutionData(players, positionIdByName, teamIdByAbbreviation);
    }

    private async Task<Dictionary<string, long>> LoadSkillPositionIdsAsync(CancellationToken cancellationToken)
    {
        var response = await _supabase.From<Position>().Select("id,name").Get(cancellationToken);

        var positionIdByName = response.Models
            .Where(p => SkillPositions.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);

        var missing = SkillPositions.Where(name => !positionIdByName.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Positions table has no row for {Missing}; players at that position cannot resolve this run",
                string.Join(", ", missing));
        }

        return positionIdByName;
    }

    private async Task<Dictionary<string, long>> LoadTeamIdsAsync(CancellationToken cancellationToken)
    {
        var response = await _supabase.From<Team>().Select("id,abbreviation").Get(cancellationToken);

        // First row wins on a duplicate abbreviation; Teams.abbreviation is expected unique.
        var teamIdByAbbreviation = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in response.Models)
            teamIdByAbbreviation.TryAdd(team.Abbreviation, team.Id);

        return teamIdByAbbreviation;
    }
}

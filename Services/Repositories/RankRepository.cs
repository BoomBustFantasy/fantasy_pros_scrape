using Supabase.Postgrest;
using FantasyProsScrape.Models.Supa;
using Client = Supabase.Client;

namespace FantasyProsScrape.Services.Repositories;

/// <summary>
/// Reads current <c>FantasyProsRanks</c> rows. Registered in DI and ready for FEAT-8; the FEAT-7
/// dry-run job does not call it (see <see cref="IRankRepository"/>).
/// </summary>
public class RankRepository : IRankRepository
{
    private readonly Client _supabase;
    private readonly ILogger<RankRepository> _logger;

    public RankRepository(Client supabase, ILogger<RankRepository> logger)
    {
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<List<FantasyProsRank>> GetExistingRanksAsync(
        int season, int week, long positionId, string scoring, CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<FantasyProsRank>()
            .Filter("season", Constants.Operator.Equals, season)
            .Filter("week", Constants.Operator.Equals, week)
            .Filter("position_id", Constants.Operator.Equals, positionId)
            .Filter("scoring", Constants.Operator.Equals, scoring)
            .Get(cancellationToken);

        _logger.LogDebug(
            "Loaded {Count} existing FantasyProsRanks rows for {Season} wk{Week} pos {PositionId} {Scoring}",
            response.Models.Count, season, week, positionId, scoring);

        return response.Models;
    }
}

using System.Text.Json;
using Supabase.Postgrest;
using FantasyProsScrape.Models.Supa;
using Client = Supabase.Client;

namespace FantasyProsScrape.Services.Repositories;

/// <summary>
/// Reads and writes <c>WeeklyRankSets</c> and <c>WeeklyRanks</c>. See <see cref="IWeeklyRankRepository"/>.
/// </summary>
public class WeeklyRankRepository : IWeeklyRankRepository
{
    /// <summary>Row count per raw-write HTTP request, matching fantasy_calc_scrape's chunk size.</summary>
    private const int WriteBatchSize = 200;

    private readonly Client _supabase;
    private readonly IPostgrestRawWriter _rawWriter;
    private readonly ILogger<WeeklyRankRepository> _logger;

    public WeeklyRankRepository(Client supabase, IPostgrestRawWriter rawWriter, ILogger<WeeklyRankRepository> logger)
    {
        _supabase = supabase;
        _rawWriter = rawWriter;
        _logger = logger;
    }

    public async Task<bool> SetExistsAsync(int season, int week, CancellationToken cancellationToken = default)
    {
        var response = await _supabase
            .From<WeeklyRankSet>()
            .Select("id")
            .Filter("season", Constants.Operator.Equals, season)
            .Filter("week", Constants.Operator.Equals, week)
            .Limit(1)
            .Get(cancellationToken);

        return response.Models.Count > 0;
    }

    /// <summary>
    /// Inserts via <see cref="IPostgrestRawWriter"/> rather than the typed client's <c>.Insert(...)</c>
    /// for the same <c>id: 0</c> serialization reason documented on <see cref="RunRepository.InsertRunAsync"/>.
    /// <c>return=representation</c> is required: the caller needs the generated id to attach
    /// <c>WeeklyRanks</c> rows to this set.
    /// </summary>
    public async Task<WeeklyRankSet> InsertSetAsync(int season, int week, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var payload = new[]
        {
            new
            {
                season,
                week,
                seeded_at = now,
                published_at = (DateTime?)null,
                updated_at = now
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var response = await _rawWriter.PostAsync(
            "WeeklyRankSets", onConflict: null, json, "return=representation", cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var inserted = JsonSerializer.Deserialize<List<WeeklyRankSet>>(body)?.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"WeeklyRankSets insert for {season} wk{week} returned no row");

        _logger.LogDebug("Inserted WeeklyRankSets row {Id} for {Season} wk{Week}", inserted.Id, season, week);
        return inserted;
    }

    /// <summary>
    /// Inserts via <see cref="IPostgrestRawWriter"/> rather than the typed client's
    /// <c>.Insert(List&lt;T&gt;, ...)</c> for the same <c>id: 0</c> serialization reason documented
    /// on <see cref="RunRepository.InsertRunAsync"/>.
    /// </summary>
    public async Task InsertRanksAsync(IReadOnlyList<WeeklyRank> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        foreach (var batch in rows.Chunk(WriteBatchSize))
        {
            var payload = batch.Select(r => new
            {
                set_id = r.SetId,
                position_id = r.PositionId,
                player_id = r.PlayerId,
                rank = r.Rank,
                seeded_rank = r.SeededRank,
                updated_at = r.UpdatedAt
            });

            var json = JsonSerializer.Serialize(payload);
            var response = await _rawWriter.PostAsync(
                "WeeklyRanks", onConflict: null, json, "return=minimal", cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogDebug("Inserted {Count} WeeklyRanks rows", rows.Count);
    }
}

using FantasyProsScrape.Models.Supa;

namespace FantasyProsScrape.Services;

/// <summary>
/// One scraped player after resolution: the FantasyPros fields the ranks table stores plus the
/// Supabase <c>Players.id</c> it resolved to. FEAT-7 maps <c>EcrPlayer</c> + resolved id into this;
/// the differ deliberately does not depend on the parser's shapes.
/// </summary>
public sealed record ResolvedRank(
    long PlayerId,
    int FantasyProsPlayerId,
    int RankEcr,
    decimal? RankAve,
    int? RankMin,
    int? RankMax,
    decimal? RankStd,
    string? PosRank,
    string? StartSitGrade,
    string? Opponent,
    string? OpponentAbbreviation);

/// <summary>
/// What one page's diff produced. The job writes <see cref="Upserts"/> in one upsert on
/// (season, week, position_id, scoring, player_id), then <see cref="HistoryRows"/> in one insert
/// after <see cref="RankDiffer.AttachRankIds"/> has filled in the ids of brand-new rows.
/// </summary>
public sealed class RankDiffResult
{
    /// <summary>No existing row. Insert; history row with <c>previous_rank_ecr</c> null.</summary>
    public required IReadOnlyList<FantasyProsRank> New { get; init; }

    /// <summary><c>rank_ecr</c> moved. Update; history row with previous and new rank.</summary>
    public required IReadOnlyList<FantasyProsRank> Changed { get; init; }

    /// <summary>Only ave/min/max/std/pos_rank/grade/opponent moved. Update; no history.</summary>
    public required IReadOnlyList<FantasyProsRank> Touched { get; init; }

    /// <summary>Identical to the existing row. Nothing to write; returned as the existing instance.</summary>
    public required IReadOnlyList<FantasyProsRank> Unchanged { get; init; }

    /// <summary>New + Changed + Touched, in scraped order: the rows the job upserts.</summary>
    public required IReadOnlyList<FantasyProsRank> Upserts { get; init; }

    /// <summary>
    /// One row per New and Changed entry. Changed rows carry <c>rank_id</c>; New rows have it null
    /// until <see cref="RankDiffer.AttachRankIds"/> is applied with the upsert response.
    /// </summary>
    public required IReadOnlyList<FantasyProsRankHistory> HistoryRows { get; init; }

    public int NewCount => New.Count;
    public int ChangedCount => Changed.Count;
    public int TouchedCount => Touched.Count;
    public int UnchangedCount => Unchanged.Count;
}

/// <summary>
/// Pure, static: classifies the resolved scrape against the current FantasyProsRanks rows for the
/// same season/week/position/scoring. Knows nothing about cutoffs, the network or the database.
/// </summary>
public static class RankDiffer
{
    /// <summary>
    /// Diff <paramref name="scraped"/> against <paramref name="existing"/>, keyed by <c>player_id</c>.
    /// </summary>
    /// <param name="existing">
    /// Every current row for this season/week/position/scoring. Rows whose player is absent from
    /// <paramref name="scraped"/> are left alone; the differ never deletes.
    /// </param>
    /// <param name="scraped">
    /// The resolved players to write. Callers decide who is in this list (inside the cutoff, or
    /// already present in <paramref name="existing"/>); anyone here with an existing row is
    /// updated regardless of rank. Duplicate <c>PlayerId</c>s keep the best (lowest) rank_ecr so
    /// the upsert never touches the same row twice.
    /// </param>
    /// <param name="now">Stamped on <c>updated_at</c> of every written row, <c>first_seen_at</c> of new rows and <c>changed_at</c> of history rows.</param>
    /// <exception cref="ArgumentException">An existing row belongs to a different season/week/position/scoring.</exception>
    public static RankDiffResult Diff(
        int season,
        int week,
        long positionId,
        string scoring,
        IReadOnlyList<FantasyProsRank> existing,
        IReadOnlyList<ResolvedRank> scraped,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(scraped);
        ArgumentException.ThrowIfNullOrWhiteSpace(scoring);

        var existingByPlayer = new Dictionary<long, FantasyProsRank>(existing.Count);
        foreach (var row in existing)
        {
            if (row.Season != season || row.Week != week || row.PositionId != positionId
                || !string.Equals(row.Scoring, scoring, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Existing row for player {row.PlayerId} is {row.Season} wk{row.Week} pos {row.PositionId} {row.Scoring}; " +
                    $"expected {season} wk{week} pos {positionId} {scoring}.", nameof(existing));
            }

            existingByPlayer[row.PlayerId] = row;
        }

        var @new = new List<FantasyProsRank>();
        var changed = new List<FantasyProsRank>();
        var touched = new List<FantasyProsRank>();
        var unchanged = new List<FantasyProsRank>();
        var upserts = new List<FantasyProsRank>();
        var history = new List<FantasyProsRankHistory>();

        foreach (var fp in Dedupe(scraped))
        {
            if (!existingByPlayer.TryGetValue(fp.PlayerId, out var current))
            {
                var inserted = ToRow(fp, season, week, positionId, scoring, id: 0, firstSeenAt: now, updatedAt: now);
                @new.Add(inserted);
                upserts.Add(inserted);
                history.Add(ToHistory(inserted, rankId: null, previousRankEcr: null, changedAt: now));
                continue;
            }

            if (fp.RankEcr != current.RankEcr)
            {
                var updated = ToRow(fp, season, week, positionId, scoring, current.Id, current.FirstSeenAt, updatedAt: now);
                changed.Add(updated);
                upserts.Add(updated);
                history.Add(ToHistory(updated, rankId: current.Id, previousRankEcr: current.RankEcr, changedAt: now));
                continue;
            }

            if (!DetailsMatch(fp, current))
            {
                var updated = ToRow(fp, season, week, positionId, scoring, current.Id, current.FirstSeenAt, updatedAt: now);
                touched.Add(updated);
                upserts.Add(updated);
                continue;
            }

            unchanged.Add(current);
        }

        return new RankDiffResult
        {
            New = @new,
            Changed = changed,
            Touched = touched,
            Unchanged = unchanged,
            Upserts = upserts,
            HistoryRows = history
        };
    }

    /// <summary>
    /// Fill in <c>rank_id</c> on history rows that are still null, using the rows the upsert
    /// returned (PostgREST echoes the written rows, ids included). Rows that already carry an id
    /// are left as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">A history row's player has no id in <paramref name="upserted"/>.</exception>
    public static void AttachRankIds(IReadOnlyList<FantasyProsRankHistory> historyRows, IReadOnlyList<FantasyProsRank> upserted)
    {
        ArgumentNullException.ThrowIfNull(historyRows);
        ArgumentNullException.ThrowIfNull(upserted);

        var idByPlayer = new Dictionary<long, long>(upserted.Count);
        foreach (var row in upserted)
        {
            if (row.Id != 0)
                idByPlayer[row.PlayerId] = row.Id;
        }

        foreach (var h in historyRows)
        {
            if (h.RankId is not null)
                continue;

            if (!idByPlayer.TryGetValue(h.PlayerId, out var rankId))
            {
                throw new InvalidOperationException(
                    $"No FantasyProsRanks id returned for player {h.PlayerId} ({h.Season} wk{h.Week} pos {h.PositionId}); cannot write its history row.");
            }

            h.RankId = rankId;
        }
    }

    /// <summary>First occurrence wins unless a later duplicate has a better (lower) rank_ecr.</summary>
    private static IEnumerable<ResolvedRank> Dedupe(IReadOnlyList<ResolvedRank> scraped)
    {
        var best = new Dictionary<long, ResolvedRank>(scraped.Count);
        var order = new List<long>(scraped.Count);

        foreach (var fp in scraped)
        {
            if (!best.TryGetValue(fp.PlayerId, out var seen))
            {
                best[fp.PlayerId] = fp;
                order.Add(fp.PlayerId);
            }
            else if (fp.RankEcr < seen.RankEcr)
            {
                best[fp.PlayerId] = fp;
            }
        }

        foreach (var playerId in order)
            yield return best[playerId];
    }

    private static bool DetailsMatch(ResolvedRank fp, FantasyProsRank row) =>
        fp.FantasyProsPlayerId == row.FantasyProsPlayerId
        && fp.RankAve == row.RankAve
        && fp.RankMin == row.RankMin
        && fp.RankMax == row.RankMax
        && fp.RankStd == row.RankStd
        && string.Equals(fp.PosRank, row.PosRank, StringComparison.Ordinal)
        && string.Equals(fp.StartSitGrade, row.StartSitGrade, StringComparison.Ordinal)
        && string.Equals(fp.Opponent, row.Opponent, StringComparison.Ordinal)
        && string.Equals(fp.OpponentAbbreviation, row.OpponentAbbreviation, StringComparison.Ordinal);

    private static FantasyProsRank ToRow(
        ResolvedRank fp, int season, int week, long positionId, string scoring,
        long id, DateTime firstSeenAt, DateTime updatedAt) =>
        new()
        {
            Id = id,
            Season = season,
            Week = week,
            PositionId = positionId,
            Scoring = scoring,
            PlayerId = fp.PlayerId,
            FantasyProsPlayerId = fp.FantasyProsPlayerId,
            RankEcr = fp.RankEcr,
            RankAve = fp.RankAve,
            RankMin = fp.RankMin,
            RankMax = fp.RankMax,
            RankStd = fp.RankStd,
            PosRank = fp.PosRank,
            StartSitGrade = fp.StartSitGrade,
            Opponent = fp.Opponent,
            OpponentAbbreviation = fp.OpponentAbbreviation,
            FirstSeenAt = firstSeenAt,
            UpdatedAt = updatedAt
        };

    private static FantasyProsRankHistory ToHistory(FantasyProsRank row, long? rankId, int? previousRankEcr, DateTime changedAt) =>
        new()
        {
            RankId = rankId,
            Season = row.Season,
            Week = row.Week,
            PositionId = row.PositionId,
            PlayerId = row.PlayerId,
            PreviousRankEcr = previousRankEcr,
            NewRankEcr = row.RankEcr,
            ChangedAt = changedAt
        };
}

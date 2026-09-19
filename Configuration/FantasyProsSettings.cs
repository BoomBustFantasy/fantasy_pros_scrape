namespace FantasyProsScrape.Configuration;

/// <summary>
/// Which FantasyPros pages to scrape and how deep to go. Bound from the "FantasyPros" section
/// of appsettings.json; nothing here is secret.
/// </summary>
public class FantasyProsSettings
{
    public const string SectionName = "FantasyPros";

    public string BaseUrl { get; set; } = "https://www.fantasypros.com";

    /// <summary>One rankings page per position, in scrape order.</summary>
    public List<FantasyProsPage> Pages { get; set; } = [];

    /// <summary>How many ranked players per position are stored (rank_ecr at or under the cutoff).</summary>
    public FantasyProsCutoffs Cutoffs { get; set; } = new();

    /// <summary>Scoring label the pages are pulled for. PPR only for now.</summary>
    public string Scoring { get; set; } = "PPR";

    /// <summary>IANA time zone the Quartz schedules run in.</summary>
    public string TimeZone { get; set; } = "America/Chicago";
}

public class FantasyProsPage
{
    /// <summary>QB, RB, WR or TE.</summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>Path relative to <see cref="FantasyProsSettings.BaseUrl"/>, e.g. /nfl/rankings/ppr-rb.php.</summary>
    public string Path { get; set; } = string.Empty;
}

public class FantasyProsCutoffs
{
    public int QB { get; set; }
    public int RB { get; set; }
    public int WR { get; set; }
    public int TE { get; set; }

    public int For(string position) => position.ToUpperInvariant() switch
    {
        "QB" => QB,
        "RB" => RB,
        "WR" => WR,
        "TE" => TE,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "No cutoff configured for this position")
    };
}

using FantasyProsScrape.Configuration;
using Microsoft.Extensions.Configuration;

namespace FantasyProsScrape.Tests;

/// <summary>Pins the committed appsettings.json: the pages and cutoffs ship, the secrets do not.</summary>
public class AppSettingsTests
{
    private static IConfiguration Load() =>
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    [Fact]
    public void FantasyProsSection_BindsPagesCutoffsScoringAndTimeZone()
    {
        var settings = Load().GetSection(FantasyProsSettings.SectionName).Get<FantasyProsSettings>();

        Assert.NotNull(settings);
        Assert.Equal("https://www.fantasypros.com", settings.BaseUrl);
        Assert.Equal(["QB", "RB", "WR", "TE"], settings.Pages.Select(p => p.Position));
        Assert.Equal(
            ["/nfl/rankings/qb.php", "/nfl/rankings/ppr-rb.php", "/nfl/rankings/ppr-wr.php", "/nfl/rankings/ppr-te.php"],
            settings.Pages.Select(p => p.Path));
        Assert.Equal(32, settings.Cutoffs.QB);
        Assert.Equal(70, settings.Cutoffs.RB);
        Assert.Equal(70, settings.Cutoffs.WR);
        Assert.Equal(30, settings.Cutoffs.TE);
        Assert.Equal(70, settings.Cutoffs.For("rb"));
        Assert.Equal("PPR", settings.Scoring);
        Assert.Equal("America/Chicago", settings.TimeZone);
    }

    [Fact]
    public void CommittedSettings_CarryNoSecrets()
    {
        var config = Load();
        var supabase = config.GetSection(SupabaseSettings.SectionName).Get<SupabaseSettings>();

        Assert.NotNull(supabase);
        Assert.Equal(string.Empty, supabase.Url);
        Assert.Equal(string.Empty, supabase.ServiceRoleKey);
        Assert.Equal(string.Empty, config["BetterStack:SourceToken"]);
    }
}

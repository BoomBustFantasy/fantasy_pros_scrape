namespace FantasyProsScrape.Configuration;

/// <summary>
/// Supabase connection. Bound from the "Supabase" section, which in Docker / Portainer comes
/// from the Supabase__Url and Supabase__ServiceRoleKey environment variables. Never committed.
/// </summary>
public class SupabaseSettings
{
    public const string SectionName = "Supabase";

    public string Url { get; set; } = string.Empty;

    /// <summary>Service role key. Required: it bypasses RLS so the jobs can write the rank tables.</summary>
    public string ServiceRoleKey { get; set; } = string.Empty;
}

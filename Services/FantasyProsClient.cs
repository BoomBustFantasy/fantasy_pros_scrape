namespace FantasyProsScrape.Services;

/// <summary>
/// Typed HttpClient for www.fantasypros.com. Base address, user agent and the standard
/// resilience handler are configured in Program.cs.
/// </summary>
public sealed class FantasyProsClient(HttpClient httpClient, ILogger<FantasyProsClient> logger) : IFantasyProsSource
{
    public const string UserAgent = "FantasyProsScraper/1.0 (+boombustfantasy.com)";

    public async Task<string> GetPageAsync(string path, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Fetching FantasyPros page {Path}", path);

        using var response = await httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

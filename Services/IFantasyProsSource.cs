namespace FantasyProsScrape.Services;

/// <summary>Fetches one FantasyPros rankings page as raw HTML. Parsing is a separate concern.</summary>
public interface IFantasyProsSource
{
    Task<string> GetPageAsync(string path, CancellationToken cancellationToken = default);
}

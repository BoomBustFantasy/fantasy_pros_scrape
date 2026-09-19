using System.Text;

namespace FantasyProsScrape.Services;

/// <summary>
/// Typed HttpClient against the PostgREST REST endpoint. Base address (<c>{SupabaseUrl}/rest/v1/</c>)
/// and the <c>apikey</c>/<c>Authorization</c> headers are configured in Program.cs, the same way
/// <see cref="FantasyProsClient"/> configures its own typed client. See
/// <see cref="IPostgrestRawWriter"/> for why this exists.
/// </summary>
public sealed class PostgrestRawWriter(HttpClient httpClient) : IPostgrestRawWriter
{
    public async Task<HttpResponseMessage> PostAsync(
        string table, string? onConflict, string jsonBody, string prefer, CancellationToken cancellationToken = default)
    {
        var path = onConflict is null
            ? table
            : $"{table}?on_conflict={Uri.EscapeDataString(onConflict)}";

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Prefer", prefer);

        return await httpClient.SendAsync(request, cancellationToken);
    }
}

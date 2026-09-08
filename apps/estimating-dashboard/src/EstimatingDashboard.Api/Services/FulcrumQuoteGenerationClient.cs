using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

// Paths and fields verified against the ITAR OpenAPI schema, 2026-09-08.
internal sealed class FulcrumQuoteGenerationClient(HttpClient http,
    IOptions<FulcrumQuoteSyncOptions> options, IIntegrationCredentialReader credentials)
{
    private const int MaxListRows = 100000;
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private string? token;
    private Uri? baseUri;
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        baseUri = FulcrumApiEndpoint.ResolveItarBaseUri(options.Value.BaseUrl, "FulcrumQuoteSync:BaseUrl");
        token = (await credentials.GetSecretAsync(IntegrationCredentialNames.FulcrumPublicApi, cancellationToken))?.Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException(
            "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, cancellationToken);

    public async Task<IReadOnlyList<JsonElement>> ListAsync(string path, bool paged, CancellationToken cancellationToken)
    {
        const int pageSize = 5000;
        var rows = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await SendAsync(HttpMethod.Post,
                path + (paged ? $"?skip={skip}&take={pageSize}" : ""), cancellationToken);
            if (page.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Fulcrum returned an invalid list response.");
            foreach (var row in page.EnumerateArray())
            {
                if (row.TryGetProperty("id", out var id) && !ids.Add(id.ToString()))
                    throw new InvalidOperationException("Fulcrum returned repeated list records. Generation stopped to avoid duplicate costs.");
                rows.Add(row.Clone());
            }
            if (rows.Count > MaxListRows)
                throw new InvalidOperationException("Fulcrum returned more records than the safe generation limit.");
            if (!paged || page.GetArrayLength() < pageSize) return rows;
            if (rows.Count >= MaxListRows)
                throw new InvalidOperationException("Fulcrum returned more records than the safe generation limit.");
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        if (baseUri is null || token is null) throw new InvalidOperationException("Fulcrum connection has not been initialized.");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post) request.Content = JsonContent.Create(new { });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(
            $"Fulcrum returned HTTP {(int)response.StatusCode}. Check API permissions for quotes, items, routing, and inventory.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("Fulcrum response exceeds the safe generation size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (bounded.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("Fulcrum response exceeds the safe generation size.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<JsonElement>(bounded, cancellationToken: cancellationToken);
    }

    public static string Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100
        ? Uri.EscapeDataString(value) : throw new InvalidOperationException("Fulcrum returned a missing or invalid identifier.");
}

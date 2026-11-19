using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Api.Services;

// Official ITAR CustomFieldDto exposes the friendly name and API label separately.
// Failed or incomplete metadata never authorizes guessing a semantic alias.
internal static class FulcrumQuoteDateDefinitionReader
{
    private const int PageSize = 100;
    private const int MaximumPages = 4;
    private const int MaximumBytes = 128 * 1024;

    public static async Task<FulcrumQuoteDateDefinitions> ReadAsync(HttpClient http, Uri baseUri,
        string token, bool fresh, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var readToken = deadline.Token;
        try
        {
            token = token.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();
            var fields = new List<FulcrumQuoteDateDefinition>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < MaximumPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri,
                    $"api/custom-fields/list?Skip={page * PageSize}&Take={PageSize}"))
                { Content = JsonContent.Create(new { module = "quote", type = "date" }) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Options.Set(FulcrumReadSnapshotHandler.RequireFresh, fresh);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, readToken);
                ct.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode) return new("UpstreamRejected", [], (int)response.StatusCode);
                if (response.Content.Headers.ContentLength > MaximumBytes) return new("ResponseTooLarge", []);
                await using var stream = await response.Content.ReadAsStreamAsync(readToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, readToken)) > 0)
                {
                    if (buffer.Length + count > MaximumBytes) return new("ResponseTooLarge", []);
                    await buffer.WriteAsync(chunk.AsMemory(0, count), readToken);
                }
                using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
                var rows = document.RootElement;
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > PageSize) return new("InvalidResponse", []);
                foreach (var row in rows.EnumerateArray())
                {
                    var id = Text(row, "id");
                    var name = Text(row, "name");
                    var label = Text(row, "label");
                    if (id.Length != 24 || !id.All(Uri.IsHexDigit) || !ids.Add(id)
                        || name.Length is < 1 or > 200 || label.Length is < 1 or > 200
                        || name.Any(char.IsControl) || label.Any(char.IsControl)
                        || !string.Equals(Text(row, "module"), "quote", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(Text(row, "type"), "date", StringComparison.OrdinalIgnoreCase))
                        return new("InvalidResponse", []);
                    fields.Add(new(name, label));
                }
                if (rows.GetArrayLength() < PageSize) return new("Succeeded", fields, 200);
            }
            return new("Truncated", []);
        }
        catch (FulcrumCoordinationUnavailableException) { ct.ThrowIfCancellationRequested(); return new("CoordinationUnavailable", []); }
        catch (FulcrumReadDeferredException error) { ct.ThrowIfCancellationRequested(); return new(error.Reason.ToString(), [], RetryAt: error.RetryAt); }
        catch (HttpRequestException error) { ct.ThrowIfCancellationRequested(); return new("ConnectionFailed", [], error.StatusCode is { } status ? (int)status : null); }
        catch (IOException) { ct.ThrowIfCancellationRequested(); return new("ConnectionFailed", []); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("TimedOut", []); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { ct.ThrowIfCancellationRequested(); return new("InvalidResponse", []); }
    }

    private static string Text(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

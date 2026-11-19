using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

internal sealed class QuoteTimingDiagnosticClient(HttpClient http, IOptions<FulcrumQuoteSyncOptions> options,
    IIntegrationCredentialReader credentials)
{
    private const int MaximumBytes = 256 * 1024;

    public async Task<(QuoteTimingReadDto List, QuoteTimingReadDto Detail)> ReadAsync(
        EstimatingQuoteHistoryRecord stored, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (stored.QuoteNumber <= 0 || !Regex.IsMatch(stored.SourceId, "\\A[0-9a-fA-F]{24}\\z"))
            return (new("InvalidStoredIdentity"), new("InvalidStoredIdentity"));
        Uri baseUri;
        string? token;
        try
        {
            baseUri = FulcrumApiEndpoint.ResolveItarBaseUri(options.Value.BaseUrl, "FulcrumQuoteSync:BaseUrl");
            token = (await credentials.GetSecretAsync(IntegrationCredentialNames.FulcrumPublicApi, ct))?.Trim();
            if (token?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) token = token[7..].Trim();
            if (string.IsNullOrWhiteSpace(token)) return (new("CredentialUnavailable"), new("CredentialUnavailable"));
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or CryptographicException)
        {
            ct.ThrowIfCancellationRequested();
            return (new("ConfigurationUnavailable"), new("ConfigurationUnavailable"));
        }

        var list = await Read(HttpMethod.Post, $"api/quotes/list?Skip=0&Take=2&Sort.Field=Number&Sort.Dir={FulcrumApiEndpoint.AscendingSortDirection}", true);
        var detail = await Read(HttpMethod.Get, "api/quotes/" + stored.SourceId, false);
        ct.ThrowIfCancellationRequested();
        return (list, detail);

        async Task<QuoteTimingReadDto> Read(HttpMethod method, string path, bool isList)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var readToken = deadline.Token;
            try
            {
                using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
                request.Options.Set(FulcrumReadSnapshotHandler.RequireFresh, true);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (isList) request.Content = JsonContent.Create(new { numbers = new[] { stored.QuoteNumber } });
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, readToken);
                ct.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode) return new("UpstreamRejected", (int)response.StatusCode);
                if (response.Content.Headers.ContentLength > MaximumBytes) return new("ResponseTooLarge");
                await using var source = await response.Content.ReadAsStreamAsync(readToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await source.ReadAsync(chunk, readToken)) > 0)
                {
                    if (buffer.Length + read > MaximumBytes) return new("ResponseTooLarge");
                    await buffer.WriteAsync(chunk.AsMemory(0, read), readToken);
                }
                using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                var data = document.RootElement;
                int? count = null;
                if (isList)
                {
                    if (data.ValueKind != JsonValueKind.Array) return new("InvalidResponse");
                    count = data.GetArrayLength();
                    if (count == 0) return new("NotReturned", ReturnedCount: 0);
                    if (count >= 2) return new("AmbiguousList", ReturnedCount: count, PossiblyTruncated: true);
                    data = data[0];
                }
                return QuoteTimingDiagnosticProjection.Project(data, stored, options.Value, count);
            }
            catch (FulcrumCoordinationUnavailableException) { ct.ThrowIfCancellationRequested(); return new("CoordinationUnavailable"); }
            catch (FulcrumReadDeferredException error) { ct.ThrowIfCancellationRequested(); return new(error.Reason.ToString(), RetryAt: error.RetryAt); }
            catch (HttpRequestException error) { ct.ThrowIfCancellationRequested(); return new("ConnectionFailed", error.StatusCode is { } status ? (int)status : null); }
            catch (IOException) { ct.ThrowIfCancellationRequested(); return new("ConnectionFailed"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("TimedOut"); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            { ct.ThrowIfCancellationRequested(); return new("InvalidResponse"); }
        }
    }
}

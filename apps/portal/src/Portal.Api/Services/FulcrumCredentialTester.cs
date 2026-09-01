using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Portal.Api.Services;

public sealed class IntegrationCredentialTestOptions
{
    public const string SectionName = "IntegrationCredentials";

    public string FulcrumBaseUrl { get; set; } = "https://api.fulcrumpro.com/";
}

public sealed record IntegrationCredentialTestResult(
    bool Succeeded,
    string Message,
    int? HttpStatusCode);

public sealed class FulcrumCredentialTester(
    HttpClient httpClient,
    IOptions<IntegrationCredentialTestOptions> options)
{
    public async Task<IntegrationCredentialTestResult> TestAsync(
        string configuredToken,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Uri.TryCreate(settings.FulcrumBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
            return new(false, "The Fulcrum API address is not configured as a secure HTTPS URL.", null);

        var token = configuredToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        httpClient.BaseAddress = baseUri;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/reporting/quote/list?Skip=0&Take=1&Sort.Field=Number&Sort.Dir=asc")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SonAero-AdminCredentialTest/1.0");

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return new(
                    true,
                    "Fulcrum accepted the token and allowed read access to quotes.",
                    statusCode);

            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    "Fulcrum rejected the token. Replace it with a current Fulcrum Public API token.",
                HttpStatusCode.Forbidden =>
                    "Fulcrum accepted the identity but the token does not have permission to view quotes.",
                _ => $"Fulcrum returned HTTP {statusCode}. The token could not be verified."
            };
            return new(false, message, statusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "The Fulcrum connection test timed out.", null);
        }
        catch (HttpRequestException)
        {
            return new(false, "The application server could not reach the Fulcrum API.", null);
        }
    }
}

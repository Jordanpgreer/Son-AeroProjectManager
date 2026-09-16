using System.Text.Json;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Api.Services;

public interface IQuoteSourceLinkResolver
{
    Task<string?> ResolveAsync(string sourceId, int quoteNumber, CancellationToken cancellationToken);
}

internal sealed class FulcrumQuoteLinkResolver(
    FulcrumQuoteGenerationClient client,
    IEnterpriseProviderSource providerSource,
    IOptions<FulcrumQuoteSyncOptions> options,
    ILogger<FulcrumQuoteLinkResolver> logger) : IQuoteSourceLinkResolver
{
    public async Task<string?> ResolveAsync(string sourceId, int quoteNumber, CancellationToken cancellationToken)
    {
        var template = options.Value.QuoteUrlTemplate;
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(sourceId) || quoteNumber <= 0)
            return null;

        var url = BuildRecordUrl(template, sourceId, quoteNumber);
        if (url is null)
        {
            logger.LogWarning("FulcrumQuoteSync:QuoteUrlTemplate must be an absolute HTTPS URL containing {{id}} or {{quoteNumber}}.");
            return null;
        }

        try
        {
            if (!string.Equals(await providerSource.GetActiveProviderAsync(cancellationToken),
                    EnterpriseProviderNames.Fulcrum, StringComparison.OrdinalIgnoreCase))
                return null;

            // A link is shown only after Fulcrum confirms that this ID still has the same quote number.
            // Keep the status page usable if the external system is slow or unavailable.
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.InitializeAsync(checkTimeout.Token);
            var quote = await client.GetAsync(
                $"api/quotes/{FulcrumQuoteGenerationClient.Identifier(sourceId)}", checkTimeout.Token);
            if (quote.ValueKind != JsonValueKind.Object
                || !quote.TryGetProperty("number", out var number)
                || number.ValueKind != JsonValueKind.Number
                || !number.TryGetInt32(out var actualNumber)
                || actualNumber != quoteNumber)
                return null;

            if (quote.TryGetProperty("id", out var id)
                && (id.ValueKind != JsonValueKind.String
                    || !string.Equals(id.GetString(), sourceId, StringComparison.OrdinalIgnoreCase)))
                return null;

            return url;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Timed out verifying the Fulcrum link for quote {QuoteNumber}.", quoteNumber);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            logger.LogDebug(exception, "Could not verify the Fulcrum link for quote {QuoteNumber}.", quoteNumber);
            return null;
        }
    }

    internal static string? BuildRecordUrl(string template, string sourceId, int quoteNumber)
    {
        if (string.IsNullOrWhiteSpace(template)
            || (!template.Contains("{id}", StringComparison.OrdinalIgnoreCase)
                && !template.Contains("{quoteNumber}", StringComparison.OrdinalIgnoreCase)))
            return null;

        var value = template.Trim()
            .Replace("{id}", Uri.EscapeDataString(sourceId), StringComparison.OrdinalIgnoreCase)
            .Replace("{quoteNumber}", Uri.EscapeDataString(quoteNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                StringComparison.OrdinalIgnoreCase);
        if (value.Contains('{') || value.Contains('}')) return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri
            : null;
    }
}

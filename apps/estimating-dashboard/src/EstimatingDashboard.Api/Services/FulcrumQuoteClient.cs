using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

public sealed class FulcrumQuoteSyncOptions
{
    public const string SectionName = "FulcrumQuoteSync";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.fulcrumpro.com/";
    public string TimeZoneId { get; set; } = "Mountain Standard Time";
    public int PageSize { get; set; } = 5000;
    public FulcrumQuoteCustomFieldOptions CustomFields { get; set; } = new();
}

public sealed class FulcrumQuoteCustomFieldOptions
{
    public string CustomerContact { get; set; } = "CustomerContact";
    public string RfqReferenceNumber { get; set; } = "RFQ/REF No";
    public string RfqDueDate { get; set; } = "RFQ Due Date";
    public string DateToEstimating { get; set; } = "Date to Estimating";
    public string Issues { get; set; } = "Issues?";
    public string QuoteOnTrack { get; set; } = "Quote On Track?";
    public string QuoteComplexity { get; set; } = "Quote Complexity";
    public string NumberOfParts { get; set; } = "Number of Parts in Quote";
    public string EstimatingStatus { get; set; } = "Estimating Status";
    public string EstimatingRep { get; set; } = "Estimating Rep";
    public string EstimatingCompletionDate { get; set; } = "Estimating Completion Date";
}

internal sealed class FulcrumQuoteClient(
    HttpClient httpClient,
    IOptions<FulcrumQuoteSyncOptions> options,
    IIntegrationCredentialReader credentials,
    ILogger<FulcrumQuoteClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<FulcrumQuoteSnapshot>> GetQuotesAsync(
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("FulcrumQuoteSync:BaseUrl must be an absolute HTTPS URL.");
        var token = await credentials.GetSecretAsync(
            SonAero.Platform.Security.IntegrationCredentialNames.FulcrumPublicApi,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");

        httpClient.BaseAddress = baseUri;
        var pageSize = Math.Clamp(settings.PageSize, 1, 5000);
        var reports = await GetQuoteReportsAsync(pageSize, token, cancellationToken);
        var quotes = await GetQuoteDetailsAsync(pageSize, token, cancellationToken);
        var reportsById = reports
            .GroupBy(report => report.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var missingReports = 0;
        var snapshots = quotes.Select(quote =>
        {
            reportsById.TryGetValue(quote.Id, out var report);
            if (report is null) missingReports++;
            return new FulcrumQuoteSnapshot(quote, report);
        }).ToList();
        if (missingReports > 0)
            logger.LogWarning(
                "Fulcrum returned {MissingReportCount} quote records without matching reporting rows; existing customer and salesperson values will be retained when possible.",
                missingReports);
        return snapshots;
    }

    private async Task<IReadOnlyList<FulcrumQuoteReportDto>> GetQuoteReportsAsync(
        int pageSize,
        string token,
        CancellationToken cancellationToken)
    {
        var rows = new List<FulcrumQuoteReportDto>();
        var skip = 0;
        while (true)
        {
            var page = await PostAsync<FulcrumQuoteReportPageDto>(
                $"api/reporting/quote/list?Skip={skip}&Take={pageSize}&Sort.Field=Number&Sort.Dir=asc",
                token,
                cancellationToken);
            rows.AddRange(page.Data);
            if (!page.HasNextPage || page.Data.Count == 0) break;
            skip += page.Data.Count;
        }
        return rows;
    }

    private async Task<IReadOnlyList<FulcrumQuoteDto>> GetQuoteDetailsAsync(
        int pageSize,
        string token,
        CancellationToken cancellationToken)
    {
        var rows = new List<FulcrumQuoteDto>();
        var skip = 0;
        while (true)
        {
            var page = await PostAsync<List<FulcrumQuoteDto>>(
                $"api/quotes/list?Skip={skip}&Take={pageSize}&Sort.Field=Number&Sort.Dir=asc",
                token,
                cancellationToken);
            rows.AddRange(page);
            if (page.Count < pageSize || page.Count == 0) break;
            skip += page.Count;
        }
        return rows;
    }

    private async Task<T> PostAsync<T>(
        string relativeUrl,
        string configuredToken,
        CancellationToken cancellationToken)
    {
        var token = configuredToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SonAero-EstimatingQuoteSync/1.0");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Fulcrum returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for {relativeUrl}.",
                null,
                response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Fulcrum returned an empty response for {relativeUrl}.");
    }
}

internal sealed record FulcrumQuoteSnapshot(
    FulcrumQuoteDto Quote,
    FulcrumQuoteReportDto? Report);

internal sealed record FulcrumQuoteDto(
    string Id,
    int Number,
    string CustomerId,
    string Status,
    decimal? TotalInPrimaryCurrency,
    Dictionary<string, JsonElement>? CustomFields,
    Dictionary<string, FulcrumExternalReferenceDto>? ExternalReferences);

internal sealed record FulcrumExternalReferenceDto(
    string? Type,
    string ExternalId,
    string? DisplayId);

internal sealed record FulcrumQuoteReportDto(
    string Id,
    int Number,
    string? CustomerName,
    string? SalesPersonName,
    string? Status,
    decimal? TotalInPrimaryCurrency);

internal sealed record FulcrumQuoteReportPageDto(
    IReadOnlyList<FulcrumQuoteReportDto> Data,
    int TotalCount,
    bool HasNextPage);

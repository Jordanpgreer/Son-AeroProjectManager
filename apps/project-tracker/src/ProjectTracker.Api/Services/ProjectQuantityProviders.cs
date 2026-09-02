using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Models;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class ProjectQuantitySyncOptions
{
    public const string SectionName = "ProjectQuantitySync";

    public string FulcrumBaseUrl { get; set; } = FulcrumApiEndpoint.ItarBaseUrl;
}

public sealed record ProjectQuantitySnapshot(
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    IReadOnlyList<string> Warnings,
    bool MatchConfirmed = false);

public enum ProjectQuantityLookupKind
{
    SalesOrder,
    Job
}

public sealed record ProjectQuantityLookupOption(
    string ExternalId,
    string Number,
    string? Name,
    string Status);

public interface IProjectQuantityProvider : IEnterpriseIntegrationAdapter
{
    Task<ProjectQuantitySnapshot> PullAsync(Project project, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(
        ProjectQuantityLookupKind kind,
        string query,
        CancellationToken cancellationToken);
}

public sealed class FulcrumProjectQuantityProvider(
    HttpClient httpClient,
    IOptions<ProjectQuantitySyncOptions> options,
    IProjectTrackerIntegrationCredentialReader credentials) : IProjectQuantityProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string ProviderName => EnterpriseProviderNames.Fulcrum;
    public string RouteName => EnterpriseDataRoutes.ProjectQuantities;

    public async Task<ProjectQuantitySnapshot> PullAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        var partNumber = project.ProgramName.Trim();
        var warnings = new List<string>();
        if (partNumber.Length == 0)
            warnings.Add("Enter a part number before pulling quantities.");
        if (!int.TryParse(project.SalesOrderNumber, out var salesOrderNumber) || salesOrderNumber <= 0)
            warnings.Add("Enter a numeric Fulcrum sales order number before pulling quantities.");
        if (!int.TryParse(project.JobNumber, out var jobNumber) || jobNumber <= 0)
            warnings.Add("Enter a numeric Fulcrum job number before pulling quantities.");
        if (warnings.Count > 0)
            return new ProjectQuantitySnapshot(null, null, warnings);

        var baseUri = ReadBaseUri();
        var token = await credentials.GetSecretAsync(
            IntegrationCredentialNames.FulcrumPublicApi,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");

        var jobs = await SendAsync<List<FulcrumJobDto>>(
            baseUri,
            HttpMethod.Post,
            "api/jobs/list?Skip=0&Take=10",
            token,
            new { numbers = new[] { jobNumber } },
            cancellationToken);
        var matchingJobs = jobs.Where(candidate => candidate.Number == jobNumber).ToList();
        if (matchingJobs.Count != 1)
            return NoMatch(matchingJobs.Count == 0
                ? $"Fulcrum did not return Job {jobNumber}."
                : $"Fulcrum returned more than one Job {jobNumber}; no quantities were changed.");
        var job = matchingJobs[0];
        if (!IsActiveJob(job.Status))
            return NoMatch($"Job {jobNumber} is not active in Fulcrum; no quantities were changed.");

        var salesOrders = await SendAsync<List<FulcrumSalesOrderDto>>(
            baseUri,
            HttpMethod.Post,
            "api/sales-orders/list?Skip=0&Take=10",
            token,
            new { numbers = new[] { salesOrderNumber } },
            cancellationToken);
        var matchingSalesOrders = salesOrders.Where(candidate => candidate.Number == salesOrderNumber).ToList();
        if (matchingSalesOrders.Count != 1)
            return NoMatch(matchingSalesOrders.Count == 0
                ? $"Fulcrum did not return Sales Order {salesOrderNumber}."
                : $"Fulcrum returned more than one Sales Order {salesOrderNumber}; no quantities were changed.");
        var salesOrder = matchingSalesOrders[0];
        if (!IsActiveSalesOrder(salesOrder.Status))
            return NoMatch($"Sales Order {salesOrderNumber} is not active in Fulcrum; no quantities were changed.");

        if (string.IsNullOrWhiteSpace(job.SalesOrderId)
            || !string.Equals(job.SalesOrderId, salesOrder.Id, StringComparison.OrdinalIgnoreCase))
            return NoMatch($"Job {jobNumber} is not linked to Sales Order {salesOrderNumber} in Fulcrum; no quantities were changed.");
        if (string.IsNullOrWhiteSpace(job.SalesOrderLineItemId))
            return NoMatch($"Job {jobNumber} is not linked to a part line on Sales Order {salesOrderNumber}; no quantities were changed.");

        var salesOrderLine = await TryGetSalesOrderLineAsync(
            baseUri,
            salesOrder.Id,
            job.SalesOrderLineItemId,
            token,
            cancellationToken);
        if (salesOrderLine is null)
            return NoMatch($"The part line linked to Job {jobNumber} was not found on Sales Order {salesOrderNumber}; no quantities were changed.");
        if (!PartNumberMatches(salesOrderLine, partNumber))
            return NoMatch($"Part {partNumber} does not match the part line linked to Job {jobNumber} and Sales Order {salesOrderNumber}; no quantities were changed.");

        var jobQuantity = PositiveOrNull(job?.QuantityToMake);
        var requiredQuantity = PositiveOrNull(salesOrderLine?.Quantity);
        if (jobQuantity is null)
            warnings.Add("The matched Fulcrum job did not contain a positive quantity to make.");
        if (requiredQuantity is null)
            warnings.Add("The matched Fulcrum sales-order line did not contain a positive quantity.");

        return new ProjectQuantitySnapshot(
            requiredQuantity,
            jobQuantity,
            warnings.Distinct().ToList(),
            MatchConfirmed: true);
    }

    public async Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(
        ProjectQuantityLookupKind kind,
        string query,
        CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0) return [];

        var baseUri = ReadBaseUri();
        var token = await credentials.GetSecretAsync(
            IntegrationCredentialNames.FulcrumPublicApi,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");

        if (kind == ProjectQuantityLookupKind.SalesOrder)
        {
            if (!int.TryParse(query, out var salesOrderNumber) || salesOrderNumber <= 0) return [];
            var salesOrders = await SendAsync<List<FulcrumSalesOrderDto>>(
                baseUri,
                HttpMethod.Post,
                "api/sales-orders/list?Skip=0&Take=20",
                token,
                new { numbers = new[] { salesOrderNumber } },
                cancellationToken);
            return salesOrders
                .Where(order => order.Number == salesOrderNumber && IsActiveSalesOrder(order.Status))
                .OrderByDescending(order => order.Number)
                .Select(order => new ProjectQuantityLookupOption(
                    order.Id,
                    order.Number.ToString(),
                    null,
                    order.Status!))
                .ToList();
        }

        object filters;
        if (int.TryParse(query, out var jobNumber) && jobNumber > 0)
            filters = new { numbers = new[] { jobNumber }, statuses = ActiveJobStatuses };
        else
            filters = new { jobNames = new[] { query }, statuses = ActiveJobStatuses };
        var jobs = await SendAsync<List<FulcrumJobDto>>(
            baseUri,
            HttpMethod.Post,
            "api/jobs/list?Skip=0&Take=20",
            token,
            filters,
            cancellationToken);
        return jobs
            .Where(job => IsActiveJob(job.Status)
                && (jobNumber > 0
                    ? job.Number == jobNumber
                    : string.Equals(job.Name?.Trim(), query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(job => job.Number)
            .Select(job => new ProjectQuantityLookupOption(
                job.Id,
                job.Number.ToString(),
                job.Name,
                job.Status!))
            .ToList();
    }

    private async Task<FulcrumSalesOrderPartLineItemDto?> TryGetSalesOrderLineAsync(
        Uri baseUri,
        string salesOrderId,
        string lineItemId,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            baseUri,
            HttpMethod.Get,
            $"api/sales-orders/{Uri.EscapeDataString(salesOrderId)}/part-line-items/{Uri.EscapeDataString(lineItemId)}",
            token,
            null);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<FulcrumSalesOrderPartLineItemDto>(JsonOptions, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        Uri baseUri,
        HttpMethod method,
        string relativeUrl,
        string token,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(baseUri, method, relativeUrl, token, body);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Fulcrum returned an empty response for {relativeUrl}.");
    }

    private static HttpRequestMessage CreateRequest(
        Uri baseUri,
        HttpMethod method,
        string relativeUrl,
        string configuredToken,
        object? body)
    {
        var token = configuredToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativeUrl));
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SonAero-ProjectQuantitySync/1.0");
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Fulcrum rejected the saved token. Replace it in Admin Hub and try again.",
            HttpStatusCode.Forbidden => "The Fulcrum token does not have permission to view jobs and sales orders.",
            _ => $"Fulcrum returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private Uri ReadBaseUri()
        => FulcrumApiEndpoint.ResolveItarBaseUri(
            options.Value.FulcrumBaseUrl,
            "ProjectQuantitySync:FulcrumBaseUrl");

    private static decimal? PositiveOrNull(double? value) =>
        value is > 0 and <= 1_000_000_000 ? Convert.ToDecimal(value.Value) : null;

    private static ProjectQuantitySnapshot NoMatch(string warning) =>
        new(null, null, [warning], MatchConfirmed: false);

    private static bool PartNumberMatches(
        FulcrumSalesOrderPartLineItemDto line,
        string partNumber) =>
        string.Equals(line.Name?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase)
        || string.Equals(line.CustomerPartNumber?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveJob(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveSalesOrder(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ActiveJobStatuses =
    [
        "draft",
        "needsReview",
        "approved",
        "engineering",
        "scheduled",
        "inProgress",
        "hold"
    ];
}

public sealed class AcumaticaProjectQuantityProvider : IProjectQuantityProvider
{
    public string ProviderName => EnterpriseProviderNames.Acumatica;
    public string RouteName => EnterpriseDataRoutes.ProjectQuantities;

    public Task<ProjectQuantitySnapshot> PullAsync(
        Project project,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "The Acumatica project-quantity adapter is installed but not configured. Add the tenant endpoint, authentication, and job/sales-order field mappings before activating Acumatica.");

    public Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(
        ProjectQuantityLookupKind kind,
        string query,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "The Acumatica project lookup adapter is installed but not configured. Add the tenant endpoint, authentication, and job/sales-order field mappings before activating Acumatica.");
}

public sealed record FulcrumJobDto(
    string Id,
    int Number,
    string? Name,
    double? QuantityToMake,
    string? SalesOrderId,
    string? SalesOrderLineItemId,
    string? Status);

public sealed record FulcrumSalesOrderDto(string Id, int Number, string? Status);

public sealed record FulcrumSalesOrderPartLineItemDto(
    string Id,
    string? Name,
    double? Quantity,
    string? CustomerPartNumber);

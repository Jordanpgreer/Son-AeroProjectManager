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
    IReadOnlyList<string> Warnings);

public interface IProjectQuantityProvider : IEnterpriseIntegrationAdapter
{
    Task<ProjectQuantitySnapshot> PullAsync(Project project, CancellationToken cancellationToken);
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
        var baseUri = ReadBaseUri();
        var token = await credentials.GetSecretAsync(
            IntegrationCredentialNames.FulcrumPublicApi,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");

        var warnings = new List<string>();
        FulcrumJobDto? job = null;
        if (int.TryParse(project.JobNumber, out var jobNumber) && jobNumber > 0)
        {
            var jobs = await SendAsync<List<FulcrumJobDto>>(
                baseUri,
                HttpMethod.Post,
                "api/jobs/list?Skip=0&Take=10&Sort.Field=Number&Sort.Dir=asc",
                token,
                new { numbers = new[] { jobNumber } },
                cancellationToken);
            job = jobs.FirstOrDefault(candidate => candidate.Number == jobNumber);
            if (job is null)
                warnings.Add($"Fulcrum did not return Job {jobNumber}.");
        }
        else
        {
            warnings.Add("Enter a numeric Fulcrum job number before pulling job quantity.");
        }

        FulcrumSalesOrderPartLineItemDto? salesOrderLine = null;
        if (job is { SalesOrderId.Length: > 0, SalesOrderLineItemId.Length: > 0 })
        {
            salesOrderLine = await TryGetSalesOrderLineAsync(
                baseUri,
                job.SalesOrderId,
                job.SalesOrderLineItemId,
                token,
                cancellationToken);
        }

        if (salesOrderLine is null)
        {
            salesOrderLine = await FindSalesOrderLineAsync(
                baseUri,
                project,
                token,
                warnings,
                cancellationToken);
        }

        var jobQuantity = PositiveOrNull(job?.QuantityToMake);
        var requiredQuantity = PositiveOrNull(salesOrderLine?.Quantity);
        if (job is not null && jobQuantity is null)
            warnings.Add("The matched Fulcrum job did not contain a positive quantity to make.");
        if (salesOrderLine is null)
            warnings.Add("No linked Fulcrum sales-order part line was found for required quantity.");
        else if (requiredQuantity is null)
            warnings.Add("The matched Fulcrum sales-order line did not contain a positive quantity.");

        return new ProjectQuantitySnapshot(requiredQuantity, jobQuantity, warnings.Distinct().ToList());
    }

    private async Task<FulcrumSalesOrderPartLineItemDto?> FindSalesOrderLineAsync(
        Uri baseUri,
        Project project,
        string token,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(project.SalesOrderNumber, out var salesOrderNumber) || salesOrderNumber <= 0)
        {
            warnings.Add("Enter a numeric Fulcrum sales order number before pulling required quantity.");
            return null;
        }

        var salesOrders = await SendAsync<List<FulcrumSalesOrderDto>>(
            baseUri,
            HttpMethod.Post,
            "api/sales-orders/list?Skip=0&Take=10&Sort.Field=Number&Sort.Dir=asc",
            token,
            new { numbers = new[] { salesOrderNumber } },
            cancellationToken);
        var salesOrder = salesOrders.FirstOrDefault(candidate => candidate.Number == salesOrderNumber);
        if (salesOrder is null)
        {
            warnings.Add($"Fulcrum did not return Sales Order {salesOrderNumber}.");
            return null;
        }

        var lines = await SendAsync<List<FulcrumSalesOrderPartLineItemDto>>(
            baseUri,
            HttpMethod.Post,
            $"api/sales-orders/{Uri.EscapeDataString(salesOrder.Id)}/part-line-items/list",
            token,
            null,
            cancellationToken);
        var exactMatches = lines.Where(line =>
                string.Equals(line.Name?.Trim(), project.ProgramName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line.CustomerPartNumber?.Trim(), project.ProgramName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count == 1) return exactMatches[0];
        if (exactMatches.Count > 1)
        {
            warnings.Add($"Sales Order {salesOrderNumber} contains more than one line matching part number {project.ProgramName}.");
            return null;
        }
        if (lines.Count == 1) return lines[0];

        warnings.Add($"Sales Order {salesOrderNumber} has no unique part line matching {project.ProgramName}.");
        return null;
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
}

public sealed record FulcrumJobDto(
    string Id,
    int Number,
    string? Name,
    double? QuantityToMake,
    string? SalesOrderId,
    string? SalesOrderLineItemId);

public sealed record FulcrumSalesOrderDto(string Id, int Number);

public sealed record FulcrumSalesOrderPartLineItemDto(
    string Id,
    string? Name,
    double? Quantity,
    string? CustomerPartNumber);

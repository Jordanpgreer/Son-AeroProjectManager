using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Models;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class ProjectQuantitySyncOptions
{
    public const string SectionName = "ProjectQuantitySync";

    public string FulcrumBaseUrl { get; set; } = FulcrumApiEndpoint.ItarBaseUrl;
    public int LookupCatalogMaxAgeHours { get; set; } = 24;
    public int[] LookupCatalogRefreshHours { get; set; } = [5, 8, 11, 14, 17];
    public string LookupCatalogTimeZoneId { get; set; } = "Mountain Standard Time";
}

public sealed record ProjectRoutingStepSnapshot(
    string ExternalId,
    int Sequence,
    string Name,
    DateOnly? ActualStartDate = null,
    DateOnly? ActualCompletionDate = null,
    bool IsComplete = false);

public sealed record ProjectQuantitySnapshot(
    decimal? RequiredQuantity,
    decimal? JobQuantity,
    IReadOnlyList<string> Warnings,
    bool MatchConfirmed = false,
    IReadOnlyList<ProjectRoutingStepSnapshot>? RoutingSteps = null)
{
    public IReadOnlyList<ProjectRoutingStepSnapshot> ConfirmedRoutingSteps => RoutingSteps ?? [];
}

public enum ProjectQuantityLookupKind
{
    Item,
    SalesOrder,
    Job
}

public sealed record ProjectQuantityLookupOption(
    string ExternalId,
    string Number,
    string? Name,
    string Status,
    string? PartNumber = null,
    string? SalesOrderNumber = null,
    string? JobNumber = null,
    decimal? JobQuantity = null);

public sealed class FulcrumProjectLookupCatalog
{
    private readonly SemaphoreSlim itemsGate = new(1, 1);
    private readonly SemaphoreSlim jobsGate = new(1, 1);
    private readonly SemaphoreSlim salesOrdersGate = new(1, 1);
    private CatalogEntry<IReadOnlyList<FulcrumItemDto>>? items;
    private CatalogEntry<IReadOnlyList<FulcrumJobDto>>? jobs;
    private CatalogEntry<IReadOnlyList<FulcrumSalesOrderDto>>? salesOrders;

    public Task<IReadOnlyList<FulcrumItemDto>> GetItemsAsync(
        Func<CancellationToken, Task<IReadOnlyList<FulcrumItemDto>>> load,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken,
        bool forceRefresh = false) =>
        GetAsync(() => items, value => items = value, itemsGate, load, refreshInterval, cancellationToken, forceRefresh);

    public Task<IReadOnlyList<FulcrumJobDto>> GetJobsAsync(
        Func<CancellationToken, Task<IReadOnlyList<FulcrumJobDto>>> load,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken,
        bool forceRefresh = false) =>
        GetAsync(() => jobs, value => jobs = value, jobsGate, load, refreshInterval, cancellationToken, forceRefresh);

    public Task<IReadOnlyList<FulcrumSalesOrderDto>> GetSalesOrdersAsync(
        Func<CancellationToken, Task<IReadOnlyList<FulcrumSalesOrderDto>>> load,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken,
        bool forceRefresh = false) =>
        GetAsync(() => salesOrders, value => salesOrders = value, salesOrdersGate, load, refreshInterval, cancellationToken, forceRefresh);

    private static async Task<IReadOnlyList<T>> GetAsync<T>(
        Func<CatalogEntry<IReadOnlyList<T>>?> read,
        Action<CatalogEntry<IReadOnlyList<T>>> update,
        SemaphoreSlim gate,
        Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var current = read();
        if (!forceRefresh
            && current is not null
            && DateTimeOffset.UtcNow - current.LoadedAt < refreshInterval)
            return current.Value;

        await gate.WaitAsync(cancellationToken);
        try
        {
            current = read();
            if (!forceRefresh
                && current is not null
                && DateTimeOffset.UtcNow - current.LoadedAt < refreshInterval)
                return current.Value;

            var loaded = await load(cancellationToken);
            update(new CatalogEntry<IReadOnlyList<T>>(loaded, DateTimeOffset.UtcNow));
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record CatalogEntry<T>(T Value, DateTimeOffset LoadedAt);
}

public interface IProjectQuantityProvider : IEnterpriseIntegrationAdapter
{
    Task<ProjectQuantitySnapshot> PullAsync(Project project, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(
        ProjectQuantityLookupKind kind,
        string query,
        CancellationToken cancellationToken,
        string? partNumber = null);
}

public sealed class FulcrumProjectQuantityProvider(
    HttpClient httpClient,
    IOptions<ProjectQuantitySyncOptions> options,
    IProjectTrackerIntegrationCredentialReader credentials,
    FulcrumProjectLookupCatalog catalog) : IProjectQuantityProvider
{
    private const int CatalogPageSize = 5000;
    private const decimal MaximumQuantity = 1_000_000_000m;
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

        var itemQuery = NormalizePartNumber(partNumber);
        var items = await SendAsync<List<FulcrumItemDto>>(
            baseUri,
            HttpMethod.Post,
            "api/items/list/v2?Skip=0&Take=50",
            token,
            new
            {
                numbers = new[]
                {
                    new { query = itemQuery, mode = "startsWith", casingOption = "caseInsensitive" }
                },
                latestRevision = true,
                isArchived = false
            },
            cancellationToken);
        var matchingItems = FindMatchingItems(items, partNumber);
        if (matchingItems.Count != 1)
            return NoMatch(matchingItems.Count == 0
                ? $"Fulcrum did not return active Item {partNumber}."
                : $"Fulcrum returned more than one active Item {partNumber}; no quantities were changed.");
        var item = matchingItems[0];

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
        if (!string.IsNullOrWhiteSpace(job.ParentItemId)
            && !string.Equals(job.ParentItemId, item.Id, StringComparison.OrdinalIgnoreCase))
            return NoMatch($"Job {jobNumber} is not producing Item {partNumber} in Fulcrum; no quantities were changed.");

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
        if (salesOrder.Deleted || !IsActiveSalesOrder(salesOrder.Status))
            return NoMatch($"Sales Order {salesOrderNumber} is not active in Fulcrum; no quantities were changed.");

        if (string.IsNullOrWhiteSpace(job.SalesOrderId)
            || !string.Equals(job.SalesOrderId, salesOrder.Id, StringComparison.OrdinalIgnoreCase))
            return NoMatch($"Job {jobNumber} is not linked to Sales Order {salesOrderNumber} in Fulcrum; no quantities were changed.");
        if (string.IsNullOrWhiteSpace(job.SalesOrderLineItemId))
            return NoMatch($"Job {jobNumber} is not linked to a part line on Sales Order {salesOrderNumber}; no quantities were changed.");

        var salesOrderLines = await SendAsync<List<FulcrumSalesOrderPartLineItemDto>>(
            baseUri,
            HttpMethod.Post,
            $"api/sales-orders/{Uri.EscapeDataString(salesOrder.Id)}/part-line-items/list",
            token,
            null,
            cancellationToken);
        var salesOrderLine = salesOrderLines.SingleOrDefault(line =>
            string.Equals(line.Id, job.SalesOrderLineItemId, StringComparison.OrdinalIgnoreCase));
        if (salesOrderLine is null)
            return NoMatch($"The part line linked to Job {jobNumber} was not found on Sales Order {salesOrderNumber}; no quantities were changed.");
        if (!SalesOrderLineMatchesItem(salesOrderLine, item, partNumber))
            return NoMatch($"Part {partNumber} does not match the part line linked to Job {jobNumber} and Sales Order {salesOrderNumber}; no quantities were changed.");

        var matchingSalesOrderLines = salesOrderLines
            .Where(line => SalesOrderLineMatchesItem(line, item, partNumber))
            .ToList();
        var matchingRequiredQuantities = matchingSalesOrderLines
            .Select(line => PositiveOrNull(line.Quantity))
            .Where(quantity => quantity is not null)
            .Select(quantity => quantity!.Value)
            .ToList();

        var jobQuantity = PositiveOrNull(job.QuantityToMake);
        var requiredQuantityTotal = matchingRequiredQuantities.Sum();
        decimal? requiredQuantity = requiredQuantityTotal is > 0 and <= MaximumQuantity
            ? requiredQuantityTotal
            : null;
        if (jobQuantity is null)
            warnings.Add("The matched Fulcrum job did not contain a positive quantity to make.");
        if (requiredQuantity is null)
            warnings.Add(requiredQuantityTotal > MaximumQuantity
                ? $"The total required quantity exceeds the supported maximum of {MaximumQuantity:N0}; the existing value was retained."
                : "The matching Fulcrum sales-order lines did not contain a positive quantity.");
        else if (matchingRequiredQuantities.Count < matchingSalesOrderLines.Count)
            warnings.Add("Required quantity excludes matching Fulcrum sales-order lines without a positive quantity.");

        var routingDetails = await SendAsync<List<FulcrumJobOperationDetailsDto>>(
            baseUri,
            HttpMethod.Post,
            $"api/jobs/{Uri.EscapeDataString(job.Id)}/operations/list",
            token,
            null,
            cancellationToken);
        var topLevelRoutingDetails = routingDetails
            .Select((detail, index) => new { Detail = detail, Index = index })
            .Where(record => record.Detail.ItemToMake.Depth == 0
                && string.Equals(record.Detail.ItemToMake.ItemId, item.Id, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.Detail.Operation.Id)
                && !string.IsNullOrWhiteSpace(record.Detail.Operation.Name))
            .OrderBy(record => record.Detail.Operation.Order)
            .ThenBy(record => record.Index)
            .ToList();
        var timers = topLevelRoutingDetails.Count == 0
            ? []
            : await LoadJobTimersAsync(baseUri, job.Id, token, cancellationToken);
        var actualStartByOperationId = timers
            .Where(timer => !string.IsNullOrWhiteSpace(timer.JobOperationId)
                && timer.StartedOnUtc is not null)
            .GroupBy(timer => timer.JobOperationId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Min(timer => timer.StartedOnUtc!.Value),
                StringComparer.OrdinalIgnoreCase);
        var timeZone = ResolveLookupTimeZone();
        var routingSteps = topLevelRoutingDetails
            .Select(record => new ProjectRoutingStepSnapshot(
                record.Detail.Operation.Id.Trim(),
                record.Detail.Operation.Order,
                record.Detail.Operation.Name!.Trim(),
                actualStartByOperationId.TryGetValue(record.Detail.Operation.Id, out var actualStart)
                    ? ToLocalDate(actualStart, timeZone)
                    : null,
                ToLocalDate(record.Detail.Operation.CompletedOnUtc, timeZone),
                IsCompleteOperation(record.Detail.Operation)))
            .DistinctBy(step => step.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (routingSteps.Count == 0)
            warnings.Add($"Job {jobNumber} did not return any top-level routing operations for Item {partNumber}.");

        return new ProjectQuantitySnapshot(
            requiredQuantity,
            jobQuantity,
            warnings.Distinct().ToList(),
            MatchConfirmed: true,
            RoutingSteps: routingSteps);
    }

    public async Task RefreshLookupCatalogAsync(CancellationToken cancellationToken)
    {
        var baseUri = ReadBaseUri();
        var token = await credentials.GetSecretAsync(
            IntegrationCredentialNames.FulcrumPublicApi,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");

        var maxAge = LookupCatalogMaxAge();
        await Task.WhenAll(
            catalog.GetItemsAsync(
                ct => LoadAllAsync<FulcrumItemDto>(
                    baseUri,
                    "api/items/list/v2",
                    token,
                    new { latestRevision = true, isArchived = false },
                    ct),
                maxAge,
                cancellationToken,
                forceRefresh: true),
            catalog.GetJobsAsync(
                ct => LoadAllAsync<FulcrumJobDto>(
                    baseUri,
                    "api/jobs/list",
                    token,
                    new { statuses = ActiveJobStatuses },
                    ct),
                maxAge,
                cancellationToken,
                forceRefresh: true),
            catalog.GetSalesOrdersAsync(
                ct => LoadAllAsync<FulcrumSalesOrderDto>(baseUri, "api/sales-orders/list", token, new { }, ct),
                maxAge,
                cancellationToken,
                forceRefresh: true));
    }

    public async Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(
        ProjectQuantityLookupKind kind,
        string query,
        CancellationToken cancellationToken,
        string? partNumber = null)
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

        var refreshInterval = LookupCatalogMaxAge();

        if (kind == ProjectQuantityLookupKind.Item)
        {
            var items = await catalog.GetItemsAsync(
                ct => LoadAllAsync<FulcrumItemDto>(
                    baseUri,
                    "api/items/list/v2",
                    token,
                    new { latestRevision = true, isArchived = false },
                    ct),
                refreshInterval,
                cancellationToken);
            return items
                .Where(item => !item.IsArchived
                    && (Contains(item.Number, query) || Contains(item.Description, query)))
                .OrderBy(item => MatchRank(item.Number, query))
                .ThenBy(item => item.Number, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .Select(item => new ProjectQuantityLookupOption(
                    item.Id,
                    item.Number,
                    item.Description,
                    "active",
                    PartNumber: item.Number))
                .ToList();
        }

        if (kind == ProjectQuantityLookupKind.SalesOrder)
        {
            var salesOrders = await catalog.GetSalesOrdersAsync(
                ct => LoadAllAsync<FulcrumSalesOrderDto>(baseUri, "api/sales-orders/list", token, new { }, ct),
                refreshInterval,
                cancellationToken);
            var matchingOrders = salesOrders
                .Where(order => !order.Deleted && IsActiveSalesOrder(order.Status)
                    && order.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(order => MatchRank(order.Number.ToString(), query))
                .ThenByDescending(order => order.Number)
                .Take(500)
                .ToList();
            var normalizedPartNumber = NormalizePartNumber(partNumber);
            if (normalizedPartNumber.Length > 0 && matchingOrders.Count > 0)
            {
                var lines = await LoadSalesOrderLineReportsAsync(
                    baseUri,
                    matchingOrders.Select(order => order.Number).ToArray(),
                    token,
                    cancellationToken);
                var matchingOrderNumbers = lines
                    .Where(line => line.SalesOrderNumber is not null
                        && (PartNumberValuesMatch(line.LineItem, normalizedPartNumber)
                            || PartNumberValuesMatch(line.CustomerPartNumber, normalizedPartNumber)))
                    .Select(line => line.SalesOrderNumber!.Value)
                    .ToHashSet();
                matchingOrders = matchingOrders
                    .Where(order => matchingOrderNumbers.Contains(order.Number))
                    .ToList();
            }

            return matchingOrders
                .Take(20)
                .Select(order => new ProjectQuantityLookupOption(
                    order.Id,
                    order.Number.ToString(),
                    null,
                    order.Status!,
                    PartNumber: normalizedPartNumber.Length == 0 ? null : partNumber!.Trim(),
                    SalesOrderNumber: order.Number.ToString()))
                .ToList();
        }

        var jobsTask = catalog.GetJobsAsync(
            ct => LoadAllAsync<FulcrumJobDto>(
                baseUri,
                "api/jobs/list",
                token,
                new { statuses = ActiveJobStatuses },
                ct),
            refreshInterval,
            cancellationToken);
        var itemsTask = catalog.GetItemsAsync(
            ct => LoadAllAsync<FulcrumItemDto>(
                baseUri,
                "api/items/list/v2",
                token,
                new { latestRevision = true, isArchived = false },
                ct),
            refreshInterval,
            cancellationToken);
        var salesOrdersTask = catalog.GetSalesOrdersAsync(
            ct => LoadAllAsync<FulcrumSalesOrderDto>(baseUri, "api/sales-orders/list", token, new { }, ct),
            refreshInterval,
            cancellationToken);
        await Task.WhenAll(jobsTask, itemsTask, salesOrdersTask);
        var jobs = await jobsTask;
        var itemById = (await itemsTask).ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var salesOrderById = (await salesOrdersTask).ToDictionary(order => order.Id, StringComparer.OrdinalIgnoreCase);
        return jobs
            .Where(job => IsActiveJob(job.Status))
            .Select(job =>
            {
                itemById.TryGetValue(job.ParentItemId ?? string.Empty, out var item);
                salesOrderById.TryGetValue(job.SalesOrderId ?? string.Empty, out var salesOrder);
                if (salesOrder is not null
                    && (salesOrder.Deleted || !IsActiveSalesOrder(salesOrder.Status)))
                    salesOrder = null;
                return new { Job = job, Item = item, SalesOrder = salesOrder };
            })
            .Where(record =>
                Contains(record.Job.Number.ToString(), query)
                || Contains(record.Job.Name, query)
                || Contains(record.Item?.Number, query)
                || (record.SalesOrder is not null
                    && record.SalesOrder.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(record => MatchRank(record.Job.Number.ToString(), query))
            .ThenByDescending(record => record.Job.Number)
            .Take(20)
            .Select(record => new ProjectQuantityLookupOption(
                record.Job.Id,
                record.Job.Number.ToString(),
                record.Job.Name,
                record.Job.Status!,
                record.Item?.Number,
                record.SalesOrder?.Number.ToString(),
                record.Job.Number.ToString(),
                PositiveOrNull(record.Job.QuantityToMake)))
            .ToList();
    }

    private async Task<IReadOnlyList<T>> LoadAllAsync<T>(
        Uri baseUri,
        string relativeUrl,
        string token,
        object body,
        CancellationToken cancellationToken)
    {
        var records = new List<T>();
        for (var skip = 0; skip < 100_000; skip += CatalogPageSize)
        {
            var page = await SendAsync<List<T>>(
                baseUri,
                HttpMethod.Post,
                $"{relativeUrl}?Skip={skip}&Take={CatalogPageSize}",
                token,
                body,
                cancellationToken);
            records.AddRange(page);
            if (page.Count < CatalogPageSize) return records;
        }

        throw new InvalidOperationException(
            "The Fulcrum lookup catalogue exceeded 100,000 records. Narrow the configured catalogue scope before using live search.");
    }

    private async Task<IReadOnlyList<FulcrumJobTrackingTimerDto>> LoadJobTimersAsync(
        Uri baseUri,
        string jobId,
        string token,
        CancellationToken cancellationToken)
    {
        var records = new List<FulcrumJobTrackingTimerDto>();
        for (var skip = 0; skip < 100_000; skip += CatalogPageSize)
        {
            var page = await SendAsync<FulcrumPagedResultDto<FulcrumJobTrackingTimerDto>>(
                baseUri,
                HttpMethod.Post,
                $"api/job-tracking-timers/list?Skip={skip}&Take={CatalogPageSize}",
                token,
                new { jobId },
                cancellationToken);
            records.AddRange(page.Data);
            if (!page.HasNextPage || page.Data.Count == 0) return records;
        }

        throw new InvalidOperationException(
            $"Fulcrum returned more than 100,000 time records for Job {jobId}; operation start dates were not synchronized.");
    }

    private async Task<IReadOnlyList<FulcrumSalesOrderLineReportDto>> LoadSalesOrderLineReportsAsync(
        Uri baseUri,
        IReadOnlyCollection<int> salesOrderNumbers,
        string token,
        CancellationToken cancellationToken)
    {
        var records = new List<FulcrumSalesOrderLineReportDto>();
        for (var skip = 0; skip < 100_000; skip += CatalogPageSize)
        {
            var page = await SendAsync<FulcrumPagedResultDto<FulcrumSalesOrderLineReportDto>>(
                baseUri,
                HttpMethod.Post,
                $"api/reporting/sales-order-lines/list?Skip={skip}&Take={CatalogPageSize}",
                token,
                new { salesOrderNumbers },
                cancellationToken);
            records.AddRange(page.Data);
            if (!page.HasNextPage || page.Data.Count == 0) return records;
        }

        throw new InvalidOperationException(
            "Fulcrum returned more than 100,000 sales-order lines for the lookup; narrow the sales-order search and try again.");
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
            HttpStatusCode.Forbidden => "The Fulcrum token does not have permission to view the jobs, sales orders, operations, or time tracking records required for this pull.",
            _ => $"Fulcrum returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private Uri ReadBaseUri()
        => FulcrumApiEndpoint.ResolveItarBaseUri(
            options.Value.FulcrumBaseUrl,
            "ProjectQuantitySync:FulcrumBaseUrl");

    private TimeSpan LookupCatalogMaxAge() => TimeSpan.FromHours(Math.Clamp(
        options.Value.LookupCatalogMaxAgeHours,
        1,
        7 * 24));

    private TimeZoneInfo ResolveLookupTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(options.Value.LookupCatalogTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DateOnly? ToLocalDate(DateTimeOffset? value, TimeZoneInfo timeZone) =>
        value is null
            ? null
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Value, timeZone).DateTime);

    private static decimal? PositiveOrNull(double? value) =>
        value is > 0 and <= (double)MaximumQuantity ? Convert.ToDecimal(value.Value) : null;

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int MatchRank(string? value, string query)
    {
        if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (value?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true) return 1;
        return 2;
    }

    private static ProjectQuantitySnapshot NoMatch(string warning) =>
        new(null, null, [warning], MatchConfirmed: false);

    private static List<FulcrumItemDto> FindMatchingItems(
        IEnumerable<FulcrumItemDto> items,
        string projectPartNumber)
    {
        var activeItems = items.Where(item => !item.IsArchived).ToList();
        var normalizedProjectNumber = NormalizePartNumber(projectPartNumber);
        var exactMatches = activeItems
            .Where(item => ItemPartNumberMatchesExactly(item, normalizedProjectNumber))
            .ToList();
        if (exactMatches.Count > 0) return exactMatches;

        // A supplied revision must still match exactly. Fuzzy matching is only for
        // a project number that omits the trailing Fulcrum revision.
        if (TryStripRevision(normalizedProjectNumber, out _)) return [];

        return activeItems
            .Where(item =>
            {
                var itemNumber = NormalizePartNumber(item.Number);
                return item.Revision?.Revision is not null
                    ? string.Equals(itemNumber, normalizedProjectNumber, StringComparison.Ordinal)
                    : TryStripRevision(itemNumber, out var baseNumber)
                        && string.Equals(baseNumber, normalizedProjectNumber, StringComparison.Ordinal);
            })
            .ToList();
    }

    private static bool ItemPartNumberMatchesExactly(FulcrumItemDto item, string normalizedProjectNumber)
    {
        var itemNumber = NormalizePartNumber(item.Number);
        if (string.Equals(itemNumber, normalizedProjectNumber, StringComparison.Ordinal)) return true;
        var revision = NormalizePartNumber(item.Revision?.Revision);
        if (revision.Length == 0) return false;
        return string.Equals($"{itemNumber.TrimEnd('-')}-{revision}", normalizedProjectNumber, StringComparison.Ordinal);
    }

    private static bool SalesOrderLineMatchesItem(
        FulcrumSalesOrderPartLineItemDto line,
        FulcrumItemDto item,
        string projectPartNumber)
    {
        return string.Equals(line.ItemId, item.Id, StringComparison.OrdinalIgnoreCase)
            || PartNumberMatches(line, item.Number)
            || PartNumberMatches(line, projectPartNumber);
    }

    private static bool PartNumberMatches(
        FulcrumSalesOrderPartLineItemDto line,
        string partNumber) =>
        PartNumberValuesMatch(line.Name, partNumber)
        || PartNumberValuesMatch(line.CustomerPartNumber, partNumber);

    private static bool PartNumberValuesMatch(string? left, string? right)
    {
        var normalizedLeft = NormalizePartNumber(left);
        var normalizedRight = NormalizePartNumber(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0) return false;
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)) return true;

        var leftHasRevision = TryStripRevision(normalizedLeft, out var leftBase);
        var rightHasRevision = TryStripRevision(normalizedRight, out var rightBase);
        if (leftHasRevision == rightHasRevision) return false;
        return leftHasRevision
            ? string.Equals(leftBase, normalizedRight, StringComparison.Ordinal)
            : string.Equals(normalizedLeft, rightBase, StringComparison.Ordinal);
    }

    private static string NormalizePartNumber(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)))
            .Replace('_', '-')
            .ToUpperInvariant();

    private static bool TryStripRevision(string value, out string baseNumber)
    {
        foreach (var pattern in RevisionSuffixPatterns)
        {
            var match = pattern.Match(value);
            if (!match.Success) continue;
            baseNumber = match.Groups["base"].Value.TrimEnd('-');
            return baseNumber.Length > 0;
        }

        baseNumber = value;
        return false;
    }

    private static bool IsActiveJob(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveSalesOrder(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleteOperation(FulcrumJobOperationDto operation) =>
        operation.CompletedOnUtc is not null
        || string.Equals(operation.Status, "complete", StringComparison.OrdinalIgnoreCase);

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

    private static readonly Regex[] RevisionSuffixPatterns =
    [
        new(@"^(?<base>.+?)-(?<revision>[A-Z]|N/?C)$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"^(?<base>.+?)-?REV(?:ISION)?-?(?<revision>[A-Z0-9]{1,3}|N/?C)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)
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
        CancellationToken cancellationToken,
        string? partNumber = null) =>
        throw new InvalidOperationException(
            "The Acumatica project lookup adapter is installed but not configured. Add the tenant endpoint, authentication, and job/sales-order field mappings before activating Acumatica.");
}

public sealed record FulcrumItemDto(
    string Id,
    string Number,
    string? Description,
    bool IsArchived,
    FulcrumItemRevisionDto? Revision = null);

public sealed record FulcrumItemRevisionDto(bool IsLatestRevision, string? Revision);

public sealed record FulcrumJobDto(
    string Id,
    int Number,
    string? Name,
    double? QuantityToMake,
    string? ParentItemId,
    string? SalesOrderId,
    string? SalesOrderLineItemId,
    string? Status);

public sealed record FulcrumSalesOrderDto(string Id, int Number, string? Status, bool Deleted);

public sealed record FulcrumSalesOrderPartLineItemDto(
    string Id,
    string? Name,
    double? Quantity,
    string? CustomerPartNumber,
    string? ItemId);

public sealed record FulcrumSalesOrderLineReportDto(
    int? SalesOrderNumber,
    string? LineItem,
    string? CustomerPartNumber);

public sealed record FulcrumJobOperationDetailsDto(
    FulcrumJobItemToMakeDto ItemToMake,
    FulcrumJobOperationDto Operation);

public sealed record FulcrumJobItemToMakeDto(
    string Id,
    string ItemId,
    int Depth);

public sealed record FulcrumJobOperationDto(
    string Id,
    int Order,
    string? Name,
    string? Status,
    DateTimeOffset? CompletedOnUtc);

public sealed record FulcrumJobTrackingTimerDto(
    string? JobOperationId,
    DateTimeOffset? StartedOnUtc);

public sealed record FulcrumPagedResultDto<T>(
    IReadOnlyList<T> Data,
    bool HasNextPage);

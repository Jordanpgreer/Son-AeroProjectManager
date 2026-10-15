using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityIntegrationOptions
{
    public const string SectionName = "QualityIntegration";

    public bool Enabled { get; set; } = true;
    public string FulcrumBaseUrl { get; set; } = FulcrumApiEndpoint.ItarBaseUrl;
    public string FulcrumShipmentUrlTemplate { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 60;
    public int PageSize { get; set; } = 5000;
}

public sealed record QualityExternalShipmentPart(
    string PartNumber,
    decimal? Quantity,
    decimal? UnitPrice,
    string? ExternalItemId,
    string? LineItemNumber = null);

public sealed record QualityExternalShipment(
    string ExternalId,
    string ShipperNumber,
    string Status,
    DateOnly? ShipByDate,
    DateTimeOffset? ShippedAt,
    string? Customer,
    string? PurchaseOrderNumber,
    string? RecordUrl,
    IReadOnlyList<QualityExternalShipmentPart> Parts);

public interface IQualityShipmentProvider : IEnterpriseIntegrationAdapter
{
    Task<IReadOnlyDictionary<string, QualityExternalShipment>> FindByShipperNumbersAsync(
        IReadOnlyCollection<string> shipperNumbers,
        CancellationToken cancellationToken);
}

public sealed class FulcrumQualityShipmentProvider(
    HttpClient httpClient,
    IOptions<QualityIntegrationOptions> options,
    IQualityIntegrationCredentialReader credentials) : IQualityShipmentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string ProviderName => EnterpriseProviderNames.Fulcrum;
    public string RouteName => EnterpriseDataRoutes.QualityRecords;

    public async Task<IReadOnlyDictionary<string, QualityExternalShipment>> FindByShipperNumbersAsync(
        IReadOnlyCollection<string> shipperNumbers,
        CancellationToken cancellationToken)
    {
        var requested = shipperNumbers
            .Select(number => number.Trim())
            .Where(number => number.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<string, QualityExternalShipment>(StringComparer.OrdinalIgnoreCase);

        var settings = options.Value;
        httpClient.BaseAddress = FulcrumApiEndpoint.ResolveItarBaseUri(
            settings.FulcrumBaseUrl,
            "QualityIntegration:FulcrumBaseUrl");
        var token = await credentials.GetSecretAsync(
            IntegrationCredentialNames.FulcrumPublicApi,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");

        var shipments = new List<FulcrumShipmentDto>();
        foreach (var batch in requested.Chunk(500))
        {
            var page = await PostAsync<FulcrumPage<FulcrumShipmentDto>>(
                $"api/shipments/list?Skip=0&Take=5000&Sort.Field=Name&Sort.Dir={FulcrumApiEndpoint.AscendingSortDirection}",
                new { names = batch },
                token,
                cancellationToken);
            shipments.AddRange(page.Data);
        }

        var exactShipments = shipments
            .Where(shipment => requested.Contains(shipment.Name, StringComparer.OrdinalIgnoreCase))
            .GroupBy(shipment => shipment.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (exactShipments.Count == 0)
            return new Dictionary<string, QualityExternalShipment>(StringComparer.OrdinalIgnoreCase);

        var reportRows = await GetShippingReportAsync(token, cancellationToken);
        var rowsByShipment = reportRows
            .Where(row => !string.IsNullOrWhiteSpace(row.ShipmentName)
                && exactShipments.ContainsKey(row.ShipmentName))
            .GroupBy(row => row.ShipmentName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var shipmentLines = await GetShipmentLinesAsync(
            exactShipments.Values.Select(shipment => shipment.Id).ToArray(),
            token,
            cancellationToken);
        var salesOrderLines = new Dictionary<string, IReadOnlyList<FulcrumSalesOrderPartLineItemDto>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var salesOrderId in shipmentLines
            .Select(line => line.SalesOrderId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            salesOrderLines[salesOrderId] = await PostAsync<IReadOnlyList<FulcrumSalesOrderPartLineItemDto>>(
                $"api/sales-orders/{Uri.EscapeDataString(salesOrderId)}/part-line-items/list",
                new { },
                token,
                cancellationToken);
        }
        var linesByShipmentId = shipmentLines
            .Where(line => !string.IsNullOrWhiteSpace(line.ShipmentId))
            .GroupBy(line => line.ShipmentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return exactShipments.ToDictionary(
            pair => pair.Key,
            pair => Map(
                pair.Value,
                rowsByShipment.GetValueOrDefault(pair.Key),
                linesByShipmentId.GetValueOrDefault(pair.Value.Id),
                salesOrderLines,
                settings.FulcrumShipmentUrlTemplate),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<FulcrumShipmentLineItemDto>> GetShipmentLinesAsync(
        IReadOnlyCollection<string> shipmentIds,
        string token,
        CancellationToken cancellationToken)
    {
        if (shipmentIds.Count == 0) return [];
        var rows = new List<FulcrumShipmentLineItemDto>();
        foreach (var batch in shipmentIds.Chunk(500))
        {
            var skip = 0;
            while (true)
            {
                var page = await PostAsync<FulcrumPage<FulcrumShipmentLineItemDto>>(
                    $"api/shipment-line-items/list?Skip={skip}&Take=5000&Sort.Field=ShipmentName&Sort.Dir={FulcrumApiEndpoint.AscendingSortDirection}",
                    new { shipmentIds = batch },
                    token,
                    cancellationToken);
                rows.AddRange(page.Data);
                if (!page.HasNextPage || page.Data.Count == 0) break;
                skip += page.Data.Count;
            }
        }
        return rows;
    }

    private async Task<IReadOnlyList<FulcrumShippingReportRowDto>> GetShippingReportAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var rows = new List<FulcrumShippingReportRowDto>();
        var pageSize = Math.Clamp(options.Value.PageSize, 1, 5000);
        var skip = 0;
        while (true)
        {
            var page = await PostAsync<FulcrumPage<FulcrumShippingReportRowDto>>(
                $"api/reporting/shipping/list?Skip={skip}&Take={pageSize}&Sort.Field=ShipmentName&Sort.Dir={FulcrumApiEndpoint.AscendingSortDirection}",
                new { },
                token,
                cancellationToken);
            rows.AddRange(page.Data);
            if (!page.HasNextPage || page.Data.Count == 0) break;
            skip += page.Data.Count;
        }
        return rows;
    }

    private async Task<T> PostAsync<T>(
        string relativeUrl,
        object body,
        string configuredToken,
        CancellationToken cancellationToken)
    {
        var token = configuredToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SonAero-QualityShipmentSync/1.0");
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

    private static QualityExternalShipment Map(
        FulcrumShipmentDto shipment,
        IReadOnlyCollection<FulcrumShippingReportRowDto>? rows,
        IReadOnlyCollection<FulcrumShipmentLineItemDto>? shipmentLines,
        IReadOnlyDictionary<string, IReadOnlyList<FulcrumSalesOrderPartLineItemDto>> salesOrderLines,
        string urlTemplate)
    {
        var reportRows = rows ?? [];
        var exactLines = shipmentLines ?? [];
        var allSalesOrderLines = salesOrderLines.Values.SelectMany(lines => lines).ToList();
        var parts = exactLines.Select((line, index) =>
        {
            FulcrumSalesOrderPartLineItemDto? salesOrderLine = null;
            IReadOnlyList<FulcrumSalesOrderPartLineItemDto> candidates = [];
            if (!string.IsNullOrWhiteSpace(line.SalesOrderId)
                && salesOrderLines.TryGetValue(line.SalesOrderId, out var matchingCandidates))
                candidates = matchingCandidates;
            if (!string.IsNullOrWhiteSpace(line.SalesOrderLineItemId))
            {
                salesOrderLine = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, line.SalesOrderLineItemId, StringComparison.OrdinalIgnoreCase));
                salesOrderLine ??= allSalesOrderLines.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, line.SalesOrderLineItemId, StringComparison.OrdinalIgnoreCase));
            }
            if (salesOrderLine is null && !string.IsNullOrWhiteSpace(line.ItemId))
            {
                var itemMatches = candidates
                    .Where(candidate => string.Equals(candidate.ItemId, line.ItemId, StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();
                if (itemMatches.Count == 1) salesOrderLine = itemMatches[0];
            }
            var report = reportRows.FirstOrDefault(row =>
                salesOrderLine is not null
                && string.Equals(row.ItemName?.Trim(), salesOrderLine.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            var partNumber = salesOrderLine?.Name?.Trim()
                ?? report?.ItemName?.Trim()
                ?? line.ItemId?.Trim()
                ?? $"Shipment line {index + 1}";
            return new QualityExternalShipmentPart(
                partNumber,
                line.QuantityToShip,
                salesOrderLine?.DiscountedPrice ?? salesOrderLine?.Price ?? report?.UnitPrice,
                line.Id,
                salesOrderLine?.Number?.ToString(CultureInfo.InvariantCulture));
        }).ToList();
        if (parts.Count == 0)
        {
            parts = reportRows
                .Where(row => !string.IsNullOrWhiteSpace(row.ItemName))
                .GroupBy(row => row.ItemName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new QualityExternalShipmentPart(
                    group.Key,
                    group.Sum(row => row.QuantityToShip ?? row.QuantityPacked ?? row.QuantityShipped ?? 0),
                    group.Select(row => row.UnitPrice).FirstOrDefault(value => value.HasValue),
                    null))
                .OrderBy(part => part.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        var first = reportRows.FirstOrDefault();
        return new QualityExternalShipment(
            shipment.Id,
            shipment.Name,
            shipment.Status,
            shipment.ShipByDate.HasValue ? DateOnly.FromDateTime(shipment.ShipByDate.Value.UtcDateTime) : null,
            shipment.ShippedDateOverride ?? shipment.ShippedDate,
            first?.RecipientCustomerCode ?? first?.RecipientName,
            first?.CustomerPoNumber,
            BuildRecordUrl(urlTemplate, shipment.Id, shipment.Name),
            parts);
    }

    private static string? BuildRecordUrl(string template, string id, string shipperNumber)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var value = template.Trim()
            .Replace("{id}", Uri.EscapeDataString(id), StringComparison.OrdinalIgnoreCase)
            .Replace("{shipperNumber}", Uri.EscapeDataString(shipperNumber), StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : throw new InvalidOperationException(
                "QualityIntegration:FulcrumShipmentUrlTemplate must produce an absolute HTTPS URL.");
    }

    private sealed record FulcrumPage<T>(IReadOnlyList<T> Data, bool HasNextPage);

    private sealed record FulcrumShipmentDto(
        string Id,
        string Name,
        string Status,
        DateTimeOffset? ShipByDate,
        DateTimeOffset? ShippedDate,
        DateTimeOffset? ShippedDateOverride);

    private sealed record FulcrumShippingReportRowDto(
        string? ShipmentName,
        string? RecipientName,
        string? RecipientCustomerCode,
        string? CustomerPoNumber,
        string? ItemName,
        decimal? QuantityShipped,
        decimal? QuantityPacked,
        decimal? QuantityToShip,
        decimal? UnitPrice);

    private sealed record FulcrumShipmentLineItemDto(
        string Id,
        decimal QuantityToShip,
        string? ItemId,
        string? ShipmentId,
        string? SalesOrderId,
        string? SalesOrderLineItemId);

    private sealed record FulcrumSalesOrderPartLineItemDto(
        string Id,
        string Name,
        decimal Price,
        decimal DiscountedPrice,
        int? Number,
        string? ItemId);
}

public sealed class AcumaticaQualityShipmentProvider : IQualityShipmentProvider
{
    public string ProviderName => EnterpriseProviderNames.Acumatica;
    public string RouteName => EnterpriseDataRoutes.QualityRecords;

    public Task<IReadOnlyDictionary<string, QualityExternalShipment>> FindByShipperNumbersAsync(
        IReadOnlyCollection<string> shipperNumbers,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Acumatica Quality shipment synchronization is not configured. Complete and validate the Acumatica quality-records adapter before selecting Acumatica.");
}

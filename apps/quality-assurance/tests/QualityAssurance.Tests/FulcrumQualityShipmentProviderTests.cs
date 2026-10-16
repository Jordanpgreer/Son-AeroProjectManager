using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QualityAssurance.Api.Services;

namespace QualityAssurance.Tests;

public sealed class FulcrumQualityShipmentProviderTests
{
    [Fact]
    public void Integration_defaults_to_twenty_minute_sync()
    {
        Assert.Equal(20, new QualityIntegrationOptions().SyncIntervalMinutes);
    }

    [Fact]
    public async Task Provider_paginates_shipment_lines_and_uses_unique_item_fallback_for_so_line_number()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            return path switch
            {
                "/api/shipments/list" => Json(
                    """{"data":[{"id":"shipment-id","name":"SHIP-300","status":"open","shipByDate":"2026-10-31T00:00:00Z"}],"hasNextPage":false}"""),
                "/api/reporting/shipping/list" => Json(
                    """{"data":[{"shipmentName":"SHIP-300","recipientName":"Customer","customerPoNumber":"PO-300","itemName":"PART-A","quantityToShip":2,"unitPrice":12.5},{"shipmentName":"SHIP-300","recipientName":"Customer","customerPoNumber":"PO-300","itemName":"PART-B","quantityToShip":3,"unitPrice":20}],"hasNextPage":false}"""),
                "/api/shipment-line-items/list" when query.Contains("Skip=0", StringComparison.Ordinal) => Json(
                    """{"data":[{"id":"shipment-line-a","quantityToShip":2,"itemId":"item-a","shipmentId":"shipment-id","salesOrderId":"sales-order-id","salesOrderLineItemId":"so-line-a"}],"hasNextPage":true}"""),
                "/api/shipment-line-items/list" when query.Contains("Skip=1", StringComparison.Ordinal) => Json(
                    """{"data":[{"id":"shipment-line-b","quantityToShip":3,"itemId":"item-b","shipmentId":"shipment-id","salesOrderId":"sales-order-id","salesOrderLineItemId":"missing-link"}],"hasNextPage":false}"""),
                "/api/sales-orders/sales-order-id/part-line-items/list" => Json(
                    """[{"id":"so-line-a","name":"PART-A","price":12.5,"discountedPrice":12.5,"number":10,"itemId":"item-a"},{"id":"so-line-b","name":"PART-B","price":20,"discountedPrice":20,"number":20,"itemId":"item-b"}]"""),
                _ => throw new InvalidOperationException($"Unexpected Fulcrum request: {request.Method} {request.RequestUri}")
            };
        });
        using var client = new HttpClient(handler);
        var provider = new FulcrumQualityShipmentProvider(
            client,
            Options.Create(new QualityIntegrationOptions
            {
                FulcrumBaseUrl = "https://api.fulcrumpro.us/",
                FulcrumShipmentUrlTemplate = "https://son-aero.fulcrumpro.com/ui/ship/{id}/details"
            }),
            new StubCredentials());

        var results = await provider.FindByShipperNumbersAsync(["SHIP-300"], default);

        var shipment = Assert.Single(results).Value;
        Assert.Equal(new DateOnly(2026, 10, 31), shipment.ShipByDate);
        Assert.Collection(
            shipment.Parts,
            part => Assert.Equal("10", part.LineItemNumber),
            part => Assert.Equal("20", part.LineItemNumber));
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath == "/api/shipment-line-items/list"));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData("SO2877-1")]
    [InlineData("2877-1")]
    [InlineData("shp-so2877-1")]
    public async Task Provider_matches_imported_alias_to_the_exact_Fulcrum_shipper_and_record_link(string alias)
    {
        var handler = ShipmentHandler();
        using var client = new HttpClient(handler);
        var provider = new FulcrumQualityShipmentProvider(client, Options.Create(new QualityIntegrationOptions()), new StubCredentials());

        var results = await provider.FindByShipperNumbersAsync([alias], default);

        var result = Assert.Single(results);
        Assert.Equal(alias, result.Key);
        Assert.Equal("SHP-SO2877-1", result.Value.ShipperNumber);
        Assert.Equal("shipment-id", result.Value.ExternalId);
        Assert.Equal("https://son-aero.fulcrumpro.com/ui/ship/shipment-id/details", result.Value.RecordUrl);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("SHP-SO2877-1", Assert.Single(body.RootElement.GetProperty("names").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Provider_deduplicates_aliases_without_matching_another_shipment_suffix()
    {
        var handler = ShipmentHandler();
        using var client = new HttpClient(handler);
        var provider = new FulcrumQualityShipmentProvider(client, Options.Create(new QualityIntegrationOptions()), new StubCredentials());

        var results = await provider.FindByShipperNumbersAsync(["SO2877-1", "SHP-SO2877-1", "SO2877-2"], default);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain("SO2877-2", results.Keys);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(2, body.RootElement.GetProperty("names").GetArrayLength());
    }

    [Theory]
    [InlineData("http://son-aero.fulcrumpro.com/ui/ship/{id}/details")]
    [InlineData("https://user@son-aero.fulcrumpro.com/ui/ship/{id}/details")]
    [InlineData("https://son-aero.fulcrumpro.com/ui/ship")]
    [InlineData("https://son-aero.fulcrumpro.com/ui/ship/{id}/{unknown}")]
    public async Task Provider_rejects_unsafe_or_non_record_URL_templates(string template)
    {
        using var client = new HttpClient(ShipmentHandler());
        var provider = new FulcrumQualityShipmentProvider(client,
            Options.Create(new QualityIntegrationOptions { FulcrumShipmentUrlTemplate = template }), new StubCredentials());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FindByShipperNumbersAsync(["SO2877-1"], default));
    }

    private static StubHandler ShipmentHandler() => new(request => request.RequestUri!.AbsolutePath switch
    {
        "/api/shipments/list" => Json("""{"data":[{"id":"shipment-id","name":"SHP-SO2877-1","status":"open"}],"hasNextPage":false}"""),
        "/api/reporting/shipping/list" or "/api/shipment-line-items/list" => Json("""{"data":[],"hasNextPage":false}"""),
        _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
    });

    private sealed class StubCredentials : IQualityIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}

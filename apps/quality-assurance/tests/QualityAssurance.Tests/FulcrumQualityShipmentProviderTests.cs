using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using QualityAssurance.Api.Services;

namespace QualityAssurance.Tests;

public sealed class FulcrumQualityShipmentProviderTests
{
    [Fact]
    public void Integration_defaults_to_hourly_sync()
    {
        Assert.Equal(60, new QualityIntegrationOptions().SyncIntervalMinutes);
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
                FulcrumShipmentUrlTemplate = "https://son-aero.fulcrumpro.com/ui/shipments/{id}"
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

    private sealed class StubCredentials : IQualityIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}

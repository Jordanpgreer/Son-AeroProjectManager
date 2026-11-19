using System.Net;
using System.Net.Http.Json;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumZeroPlanningQuantityTests
{
    [Fact]
    public async Task Outside_processing_ignores_zero_branches_and_keeps_fixed_and_unknown_demand()
    {
        using var http = new HttpClient(new PlanningHandler());
        var service = new FulcrumOutsideProcessingService(Client(http), TimeProvider.System);
        var review = await service.InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [10, 20] }, default);

        Assert.Equal(2, review.Operations.Count);
        var fixedRequirement = Assert.Single(review.Operations, operation => operation.ItemId == "active-sub");
        Assert.Equal(2m, fixedRequirement.RequiredMaximum);
        Assert.Equal("needs-rfq", fixedRequirement.Status);
        var unknown = Assert.Single(review.Operations, operation => operation.ItemId == "unknown-sub");
        Assert.Null(unknown.RequiredMaximum);
        Assert.Equal("review-required", unknown.Status);
    }

    [Fact]
    public async Task Material_purchasing_uses_quote_planning_and_counts_only_active_material_demand()
    {
        using var http = new HttpClient(new PlanningHandler());
        var service = new FulcrumMaterialRequirementsService(Client(http), TimeProvider.System);
        var review = await service.InspectAsync(7, 123, "quote", default,
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [10, 20] });

        Assert.Equal(2, review.Materials.Count);
        var fixedRequirement = Assert.Single(review.Materials, material => material.ItemId == "active-material");
        Assert.Equal(7m, fixedRequirement.RequiredMaximum);
        Assert.Equal("needs-rfq", fixedRequirement.Status);
        Assert.Single(fixedRequirement.Sources);
        var unknown = Assert.Single(review.Materials, material => material.ItemId == "unknown-material");
        Assert.Null(unknown.RequiredMaximum);
        Assert.Equal("review-required", unknown.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Material_purchasing_reconciles_the_planning_for_all_quote_quantity_alternatives(bool sameDemand)
    {
        using var http = new HttpClient(new PlanningHandler { SameAlternativeDemand = sameDemand });
        var service = new FulcrumMaterialRequirementsService(Client(http), TimeProvider.System);

        var review = await service.InspectAsync(7, 123, "quote", default);

        Assert.Equal(new[] { 10m, 20m }, Assert.Single(review.Parts).Quantities);
        var material = Assert.Single(review.Materials);
        Assert.Equal("active-material", material.ItemId);
        if (sameDemand)
        {
            Assert.Equal("needs-rfq", material.Status);
            Assert.Equal(new[] { 20m, 40m }, material.QuoteQuantities);
            Assert.Single(material.Sources);
        }
        else
        {
            Assert.Equal("review-required", material.Status);
            Assert.Empty(material.QuoteQuantities);
            Assert.Null(material.RequiredMaximum);
            Assert.Contains(material.Issues, issue => issue.Contains("differs across quote-line routings"));
        }
    }

    [Fact]
    public async Task Synced_counts_and_op_flags_exclude_zero_planning_demand()
    {
        using var http = new HttpClient(new PlanningHandler());
        var service = new FulcrumQuoteInspectionService(Client(http), TimeProvider.System,
            NullLogger<FulcrumQuoteInspectionService>.Instance);
        var snapshot = new FulcrumQuoteSnapshot(new("quote", 123, "customer", "draft", null, null, null), null);

        var review = await service.InspectAsync([snapshot], default);

        Assert.Empty(review.Warnings);
        var quote = review.Results[123];
        Assert.Equal(2, quote.BuyItemCount);
        Assert.Equal(2, quote.MakeItemCount);
        Assert.Equal(new[] { "ACTIVE-SUB: OP Heat Treat", "UNKNOWN-SUB: OP Heat Treat" }, quote.OpOperations);
    }

    private static FulcrumQuoteGenerationClient Client(HttpClient http) =>
        new(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials());

    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken ct) =>
            Task.FromResult<string?>("fixture-key");
    }

    private sealed class PlanningHandler : HttpMessageHandler
    {
        public bool? SameAlternativeDemand { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string quote = "/api/quotes/quote";
            const string line = quote + "/part-line-items/line";
            const string alternate = quote + "/part-line-items/alternate";
            const string routing = line + "/routing/";
            var uri = request.RequestUri!;
            object body = uri.AbsolutePath switch
            {
                quote => new { number = 123 },
                quote + "/part-line-items/list" when SameAlternativeDemand.HasValue => new[]
                {
                    new { id = "line", itemId = "top", quantity = 10 },
                    new { id = "alternate", itemId = "top", quantity = 20 }
                },
                quote + "/part-line-items/list" => new[] { new { id = "line", itemId = "top", quantity = 15 } },
                alternate + "/make-summary" => new[] { new { itemId = "top", routingId = "alternate-top" } },
                alternate + "/routing/alternate-top/operations/list" => Array.Empty<object>(),
                alternate + "/routing/alternate-top/input-items/list" => new[] { Input("alternate-input", "active-material", 2) },
                line + "/make-summary" when SameAlternativeDemand.HasValue => new[] { new { itemId = "top", routingId = "top" } },
                line + "/make-summary" => new[] { "top", "zero-sub", "active-sub", "unknown-sub" }
                    .Select(id => new { itemId = id, routingId = id }).ToArray(),
                routing + "top/input-items/list" when SameAlternativeDemand.HasValue =>
                    new[] { Input("first-input", "active-material", SameAlternativeDemand == true ? 2 : 0) },
                routing + "top/input-items/list" => new[]
                {
                    Input("zero-sub", "zero-sub", 0), Input("zero-material", "zero-material", 0),
                    Input("zero-shared", "active-material", 0), Input("active-sub", "active-sub", 0, 2),
                    Input("unknown-sub", "unknown-sub", null)
                },
                routing + "active-sub/input-items/list" => new[] { Input("active", "active-material", 3, 1) },
                routing + "unknown-sub/input-items/list" => new[]
                {
                    Input("unknown", "unknown-material", 1), Input("zero-under-unknown", "zero-material", 0)
                },
                routing + "top/operations/list" or "/api/items/top/routing/operations/list" => Array.Empty<object>(),
                routing + "active-sub/operations/list" or routing + "unknown-sub/operations/list" =>
                    new[] { new { id = "heat-treat", name = "OP Heat Treat", isOutsideProcessing = true,
                        outsideProcessingOperation = new { selectedVendorId = "vendor" } } },
                "/api/items/top" or "/api/items/active-sub" or "/api/items/unknown-sub" =>
                    Item(uri.Segments[^1], "make"),
                "/api/items/active-material" or "/api/items/unknown-material" => Item(uri.Segments[^1], "buy"),
                "/api/inventory/availableByItem" => new { },
                "/api/items/active-material/purchase-history" or "/api/items/unknown-material/purchase-history" =>
                    new { windowMonths = uri.Query == "?months=60" ? 60 : 3, totalPurchaseCount = 0,
                        purchases = Array.Empty<object>() },
                "/api/items/active-material/vendors/list" or "/api/items/unknown-material/vendors/list" =>
                    new[] { new { isPrimary = true, vendorId = "vendor" } },
                "/api/vendors/vendor" => new { name = "Fixture Vendor", active = true },
                "/api/vendors/vendor/contacts/list" => new[] { new { email = "quotes@example.test" } },
                _ => throw new InvalidOperationException("Unexpected request: " + uri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }

        private static object Input(string id, string itemId, decimal? perUnit, decimal? fixedUnits = null) =>
            new { id, itemId, valueType = "requires", valueTypeUnits = perUnit, fixedUnits };

        private static object Item(string id, string origin) => new { id, number = id.ToUpperInvariant(),
            itemOrigin = origin, description = "Planning fixture part", unitOfMeasureName = "Piece" };
    }
}

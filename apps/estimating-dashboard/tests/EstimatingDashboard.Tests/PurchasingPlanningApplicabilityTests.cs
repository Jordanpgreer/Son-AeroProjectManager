using System.Net;
using System.Net.Http.Json;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Tests;

public sealed class PurchasingPlanningApplicabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_industrios_scope_skips_all_downstream_purchasing_reads(bool emptyRouting)
    {
        using var handler = new PlanningHandler("industrios") { EmptyRouting = emptyRouting };
        using var http = new HttpClient(handler);
        var client = Client(http);

        var material = await new FulcrumMaterialRequirementsService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", default);
        var op = await new FulcrumOutsideProcessingService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", new Dictionary<string, IReadOnlyList<decimal>>(), default);

        Assert.Equal(PurchasingPlanningSources.Industrios, material.PlanningSource);
        Assert.True(Assert.Single(material.Parts).NoFulcrumOperations);
        Assert.Empty(material.Materials);
        Assert.Empty(material.Warnings);
        Assert.Equal(PurchasingPlanningSources.Industrios, op.PlanningSource);
        Assert.Empty(op.Operations);
        Assert.Empty(op.Warnings);
        AssertNoPurchasingReads(handler);
    }

    [Fact]
    public async Task Mixed_scope_inspects_only_fulcrum_parts_and_does_not_require_industrios_quantity_confirmation()
    {
        using var handler = new PlanningHandler("mixed");
        using var http = new HttpClient(handler);
        var client = Client(http);
        var quantities = new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [100] };

        var material = await new FulcrumMaterialRequirementsService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", default, quantities);
        var op = await new FulcrumOutsideProcessingService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", quantities, default);

        Assert.Equal(PurchasingPlanningSources.Mixed, material.PlanningSource);
        Assert.True(material.Parts.Single(part => part.ItemId == "industry").NoFulcrumOperations);
        Assert.False(material.Parts.Single(part => part.ItemId == "top").NoFulcrumOperations);
        Assert.Equal(new decimal[] { 10, 20, 30 }, material.Parts.Single(part => part.ItemId == "industry").Quantities);
        var demand = Assert.Single(material.Materials);
        Assert.Equal("material", demand.ItemId);
        Assert.Equal(100m, demand.RequiredMaximum);
        Assert.Equal("needs-rfq", demand.Status);
        Assert.Empty(material.Warnings);
        Assert.Equal(PurchasingPlanningSources.Mixed, op.PlanningSource);
        Assert.Equal("top", Assert.Single(op.Operations).ItemId);
        Assert.Equal(100m, op.Operations[0].RequiredMaximum);
        Assert.Empty(op.Warnings);
        Assert.DoesNotContain(handler.Paths, path => path == "/api/items/industry/routing/input-items/list");
        Assert.DoesNotContain(handler.Paths, path => path == "/api/items/top/routing/operations/list");
    }

    [Fact]
    public async Task Quote_specific_plan_wins_without_reading_empty_catalog_operations()
    {
        using var handler = new PlanningHandler("fulcrum");
        using var http = new HttpClient(handler);
        var client = Client(http);
        var quantities = new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [100] };
        var material = await new FulcrumMaterialRequirementsService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", default, quantities);
        var op = await new FulcrumOutsideProcessingService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", quantities, default);

        Assert.Equal(PurchasingPlanningSources.Fulcrum, material.PlanningSource);
        Assert.False(Assert.Single(material.Parts).NoFulcrumOperations);
        Assert.Single(material.Materials);
        Assert.Equal(PurchasingPlanningSources.Fulcrum, op.PlanningSource);
        Assert.Single(op.Operations);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/api/items/top/routing/"));
    }

    [Fact]
    public async Task Direct_buy_part_remains_purchasable_without_manufacturing_planning()
    {
        using var handler = new PlanningHandler("buy");
        using var http = new HttpClient(handler);
        var client = Client(http);
        var quantities = new Dictionary<string, IReadOnlyList<decimal>> { ["buy"] = [100] };
        var material = await new FulcrumMaterialRequirementsService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", default, quantities);
        var op = await new FulcrumOutsideProcessingService(client, TimeProvider.System)
            .InspectAsync(7, 4445, "quote", quantities, default);

        Assert.Equal(PurchasingPlanningSources.Fulcrum, material.PlanningSource);
        Assert.False(Assert.Single(material.Parts).NoFulcrumOperations);
        Assert.Equal("buy", Assert.Single(material.Materials).ItemId);
        Assert.Equal("needs-rfq", material.Materials[0].Status);
        Assert.Equal(PurchasingPlanningSources.Fulcrum, op.PlanningSource);
        Assert.Empty(op.Operations);
        Assert.DoesNotContain(handler.Paths, path => path.Contains("make-summary") || path.Contains("/routing/"));
    }

    [Theory]
    [InlineData("403")]
    [InlineData("503")]
    [InlineData("404")]
    [InlineData("malformed")]
    [InlineData("wrong-root")]
    [InlineData("empty-quote")]
    public async Task Unavailable_or_invalid_planning_never_becomes_industrios(string failure)
    {
        using var handler = new PlanningHandler("industrios") { Failure = failure };
        using var http = new HttpClient(handler);
        var client = Client(http);
        var materialError = await Record.ExceptionAsync(() =>
            new FulcrumMaterialRequirementsService(client, TimeProvider.System).InspectAsync(7, 4445, "quote", default));
        var opError = await Record.ExceptionAsync(() =>
            new FulcrumOutsideProcessingService(client, TimeProvider.System).InspectAsync(7, 4445, "quote",
                new Dictionary<string, IReadOnlyList<decimal>>(), default));

        Assert.NotNull(materialError);
        Assert.NotNull(opError);
        if (failure is "403" or "503")
        {
            Assert.Equal((HttpStatusCode)int.Parse(failure), Assert.IsType<FulcrumReadFailureException>(materialError).StatusCode);
            Assert.Equal((HttpStatusCode)int.Parse(failure), Assert.IsType<FulcrumReadFailureException>(opError).StatusCode);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(materialError);
            Assert.IsType<InvalidOperationException>(opError);
        }
        AssertNoPurchasingReads(handler);
    }

    private static void AssertNoPurchasingReads(PlanningHandler handler) => Assert.DoesNotContain(handler.Paths,
        path => path.StartsWith("/api/inventory/") || path.StartsWith("/api/vendors/")
            || path.Contains("purchase-history") || path.EndsWith("/vendors/list")
            || path.StartsWith("/api/items/") && path.EndsWith("/input-items/list"));

    private static FulcrumQuoteGenerationClient Client(HttpClient http) =>
        new(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials());
    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken ct) => Task.FromResult<string?>("fixture-key");
    }

    private sealed class PlanningHandler(string mode) : HttpMessageHandler
    {
        public bool EmptyRouting { get; init; }
        public string? Failure { get; init; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/make-summary") && int.TryParse(Failure, out var status))
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
            object body;
            if (path == "/api/quotes/quote") body = new { number = 4445 };
            else if (path == "/api/quotes/quote/part-line-items/list")
            {
                var rows = new List<object>();
                if (mode is "industrios" or "mixed")
                {
                    rows.Add(new { id = "industry-line", itemId = "industry", quantity = 10 });
                    if (mode == "mixed")
                    {
                        rows.Add(new { id = "industry-line2", itemId = "industry", quantity = 20 });
                        rows.Add(new { id = "industry-line3", itemId = "industry", quantity = 30 });
                    }
                }
                if (mode is "fulcrum" or "mixed") rows.Add(new { id = "top-line", itemId = "top", quantity = 100 });
                if (mode == "buy") rows.Add(new { id = "buy-line", itemId = "buy", quantity = 100 });
                body = Failure == "empty-quote" ? Array.Empty<object>() : rows.ToArray();
            }
            else if (path.EndsWith("/make-summary"))
            {
                var industry = path.Contains("industry-line");
                body = Failure switch
                {
                    "malformed" => new object[] { new { itemId = "industry" } },
                    "wrong-root" => new object[] { new { itemId = "unrelated", routingId = "route" } },
                    _ => industry && !EmptyRouting ? Array.Empty<object>()
                        : new object[] { new { itemId = industry ? "industry" : "top", routingId = "route" } }
                };
            }
            else if (path.StartsWith("/api/quotes/") && path.EndsWith("/operations/list"))
                body = path.Contains("industry-line") ? Array.Empty<object>()
                    : new object[] { new { id = "op", name = "Heat treat", isOutsideProcessing = true,
                        outsideProcessingOperation = new { selectedVendorId = "vendor" } } };
            else if (path.StartsWith("/api/quotes/") && path.EndsWith("/input-items/list"))
                body = path.Contains("industry-line") ? Array.Empty<object>()
                    : new object[] { new { id = "input", itemId = "material", valueType = "requires", valueTypeUnits = 1, fixedUnits = 0 } };
            else body = path switch
            {
                "/api/items/industry" => new { number = "INDUSTRY", itemOrigin = "make" },
                "/api/items/industry/routing/operations/list" => Array.Empty<object>(),
                "/api/items/top" => new { number = "TOP", itemOrigin = "make", unitOfMeasureName = "Each" },
                "/api/items/buy" => new { number = "BUY", itemOrigin = "buy", unitOfMeasureName = "Each", description = "Purchased part" },
                "/api/items/material" => new { number = "RAW", itemOrigin = "buy", unitOfMeasureName = "Each", description = "Raw material" },
                "/api/inventory/availableByItem" => new { material = 0, buy = 0 },
                "/api/items/material/purchase-history" or "/api/items/buy/purchase-history" => new
                    { windowMonths = request.RequestUri.Query == "?months=60" ? 60 : 3, totalPurchaseCount = 0, purchases = Array.Empty<object>() },
                "/api/items/material/vendors/list" or "/api/items/buy/vendors/list" => new[] { new { vendorId = "vendor", isPrimary = true } },
                "/api/vendors/vendor" => new { name = "Synthetic Vendor", active = true },
                "/api/vendors/vendor/contacts/list" => new[] { new { email = "quotes@example.test" } },
                _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}

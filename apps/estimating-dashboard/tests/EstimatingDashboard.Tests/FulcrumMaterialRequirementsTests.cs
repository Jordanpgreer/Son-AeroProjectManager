using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumMaterialRequirementsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fixed_plus_per_unit_requirement_uses_available_and_skips_purchasing_when_covered()
    {
        using var fixture = new Fixture();
        fixture.Handler.Available = 58.2m;
        var result = await fixture.Service.InspectAsync(7, 123, "quote", default);
        var material = Assert.Single(result.Materials);
        Assert.Equal(15m, material.RequiredMaximum);
        Assert.Equal(58.2m, material.AvailableQuantity);
        Assert.Equal("stock-covered", material.Status);
        Assert.Equal(new[] { 15m, 22.5m }, material.QuoteQuantities);
        var source = Assert.Single(material.Sources);
        Assert.Equal(10m, source.FixedQuantity);
        Assert.Equal(.005m, source.PerUnitQuantity);
        Assert.DoesNotContain(fixture.Handler.Paths, path => path.Contains("purchase-history") || path.Contains("byItem"));
    }

    [Fact]
    public async Task Short_stock_quotes_full_requirement_to_all_unique_primary_vendor_contacts()
    {
        using var fixture = new Fixture();
        fixture.Handler.Available = 5;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("needs-rfq", material.Status);
        Assert.Equal(new[] { 15m, 22.5m }, material.QuoteQuantities);
        Assert.Equal("vendor", material.VendorId);
        Assert.Equal("Nexeo Plastics LLC", material.VendorName);
        Assert.Equal(new[] { "one@example.com", "two@example.com" }, material.VendorEmails);
        Assert.Equal("Complete item description\nSecond line", material.Description);
    }

    [Fact]
    public async Task Alternate_quantities_use_lowest_and_largest_without_adding_or_multiplying_them()
    {
        using var fixture = new Fixture();
        fixture.Handler.Lines = [Line("one", "top", 500), Line("two", "top", 1000), Line("three", "top", 2000)];
        var result = await fixture.Service.InspectAsync(7, 123, "quote", default);
        Assert.Equal(new decimal[] { 500, 1000, 2000 }, Assert.Single(result.Parts).Quantities);
        var material = Assert.Single(result.Materials);
        Assert.Equal(12.5m, material.RequiredMinimum);
        Assert.Equal(20m, material.RequiredMaximum);
        Assert.Equal(new[] { 12.5m, 20m }, material.QuoteQuantities);
        Assert.Single(material.Sources);
    }

    [Fact]
    public async Task Shared_material_across_distinct_parts_is_aggregated_before_stock_comparison()
    {
        using var fixture = new Fixture();
        fixture.Handler.Lines = [Line("one", "top", 1000), Line("two", "other", 1000)];
        fixture.Handler.Available = 20;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal(30m, material.RequiredMaximum);
        Assert.Equal(new[] { 30m, 45m }, material.QuoteQuantities);
        Assert.Equal("needs-rfq", material.Status);
        Assert.Equal(2, material.Sources.Count);
    }

    [Fact]
    public async Task Reviewed_quantity_correction_replaces_public_line_quantities_and_marks_the_source()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.InspectAsync(7, 123, "quote", default,
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [2000, 500] });
        Assert.Equal(new decimal[] { 500, 2000 }, Assert.Single(result.Parts).Quantities);
        Assert.True(result.Parts[0].QuantitiesOverridden);
        Assert.Equal(new[] { 12.5m, 20m }, Assert.Single(result.Materials).QuoteQuantities);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Quotes_without_purchased_materials_do_not_show_quantity_break_caution()
    {
        using var fixture = new Fixture();
        fixture.Handler.NoMaterials = true;
        var result = await fixture.Service.InspectAsync(7, 123, "quote", default);
        Assert.Empty(result.Materials);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Unconfirmed_material_quantities_keep_the_pricing_panel_caution()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.InspectAsync(7, 123, "quote", default);
        Assert.Contains("Pricing-panel", Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("Vendor\nInjected")]
    public async Task Missing_or_invalid_vendor_name_requires_review(string name)
    {
        using var fixture = new Fixture();
        fixture.Handler.VendorName = name;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("review-required", material.Status);
        Assert.Contains(material.Issues, issue => issue.Contains("vendor's name"));
    }

    [Fact]
    public async Task Description_preserves_normal_newlines_but_rejects_embedded_control_characters()
    {
        using var fixture = new Fixture();
        fixture.Handler.Description = "Description\nSecond line\0hidden";
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("review-required", material.Status);
        Assert.Contains(material.Issues, issue => issue.Contains("description"));
    }

    [Fact]
    public async Task Part_number_control_characters_fail_with_an_actionable_error()
    {
        using var fixture = new Fixture();
        fixture.Handler.PartNumber = "RM004420\nInjected";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(7, 123, "quote", default));
        Assert.Contains("invalid part number", error.Message);
    }

    [Theory]
    [InlineData("multiply")]
    [InlineData("accumulate")]
    [InlineData("uplift")]
    public async Task Extreme_quantities_fail_with_an_actionable_error_instead_of_overflow(string operation)
    {
        using var fixture = new Fixture();
        fixture.Handler.PerUnit = operation == "multiply" ? 2 : 0;
        fixture.Handler.FixedUnits = operation == "accumulate" ? decimal.MaxValue / 2 + 1
            : operation == "uplift" ? decimal.MaxValue : 0;
        fixture.Handler.Lines = operation == "accumulate"
            ? [Line("one", "top", 1000), Line("two", "other", 1000)]
            : [Line("one", "top", decimal.MaxValue)];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(7, 123, "quote", default));
        Assert.Contains("supported calculation range", error.Message);
        Assert.IsType<OverflowException>(error.InnerException);
    }

    [Theory]
    [InlineData("top", 0, 2000)]
    [InlineData("top", -1, 2000)]
    public async Task Quantity_corrections_reject_nonpositive_quantities(string item, decimal low, decimal high)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(7, 123, "quote", default,
            new Dictionary<string, IReadOnlyList<decimal>> { [item] = [low, high] }));
    }

    [Fact]
    public async Task Saved_quantities_for_removed_parts_allow_current_parts_to_load_but_require_reconfirmation()
    {
        using var fixture = new Fixture();
        fixture.Handler.Available = 1000;
        var result = await fixture.Service.InspectAsync(7, 123, "quote", default,
            new Dictionary<string, IReadOnlyList<decimal>> { ["removed"] = [2000, 5000] });
        var part = Assert.Single(result.Parts);
        Assert.Equal("top", part.ItemId);
        Assert.Equal(new decimal[] { 1000 }, part.Quantities);
        Assert.False(part.QuantitiesOverridden);
        Assert.Contains(result.Warnings, warning => warning.Contains("removed parts"));
        var material = Assert.Single(result.Materials);
        Assert.Equal(15m, material.RequiredMaximum);
        Assert.Equal("review-required", material.Status);
        Assert.Contains(material.Issues, issue => issue.Contains("reset or confirm"));
    }

    [Fact]
    public async Task Nested_make_fixed_quantities_are_applied_once_per_parent_lot()
    {
        using var fixture = new Fixture();
        fixture.Handler.Nested = true;
        // 2 + (3 * 1000) intermediate items, then 10 + (.005 * 3002) pounds.
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal(25.01m, material.RequiredMaximum);
        Assert.Equal("SUB", Assert.Single(material.Sources).ParentPartNumber);
    }

    [Theory]
    [InlineData("2026-07-12T12:00:00Z", "Ordered", 15, true)]
    [InlineData("2026-10-12T11:00:00Z", "Paid", 8, true)]
    [InlineData("2026-07-11T23:59:59Z", "Ordered", 15, false)]
    [InlineData("2026-10-13T00:00:00Z", "Ordered", 15, false)]
    [InlineData("2026-10-01T00:00:00Z", "Draft", 15, false)]
    [InlineData("2026-10-01T00:00:00Z", "Cancelled", 15, false)]
    [InlineData("2026-10-01T00:00:00Z", "Ordered", 0, false)]
    public async Task Only_recent_ordered_or_paid_purchase_with_positive_price_suppresses_rfq(
        string orderedAt, string status, decimal price, bool reusable)
    {
        using var fixture = new Fixture();
        fixture.Handler.Purchases = [new { purchaseOrderNumber = 456, orderDateUtc = orderedAt,
            purchaseOrderStatus = status, effectiveUnitPrice = price, quantityOrdered = 100,
            vendor = new { name = "Previous vendor" } }];
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal(reusable ? "recent-po" : "needs-rfq", material.Status);
        if (reusable)
        {
            Assert.Equal("456", material.RecentPurchaseOrder!.Number);
            Assert.Equal(price, material.RecentPurchaseOrder.UnitPrice);
            Assert.DoesNotContain(fixture.Handler.Paths, path => path.Contains("/vendors"));
        }
        else Assert.Null(material.RecentPurchaseOrder);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("history")]
    [InlineData("vendor")]
    [InlineData("contacts")]
    public async Task Missing_upstream_evidence_requires_review(string failure)
    {
        using var fixture = new Fixture();
        fixture.Handler.Failure = failure;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("review-required", material.Status);
        Assert.NotEmpty(material.Issues);
    }

    [Fact]
    public async Task Invalid_usage_never_looks_stock_covered()
    {
        using var fixture = new Fixture();
        fixture.Handler.InvalidUsage = true;
        fixture.Handler.Available = 10000;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("review-required", material.Status);
        Assert.Null(material.RequiredMaximum);
        Assert.Empty(material.QuoteQuantities);
    }

    [Fact]
    public async Task Invalid_history_shape_cannot_be_treated_as_no_recent_orders()
    {
        using var fixture = new Fixture();
        fixture.Handler.BadHistory = true;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("review-required", material.Status);
        Assert.Contains(material.Issues, issue => issue.Contains("incomplete"));
    }

    [Fact]
    public async Task Malformed_inventory_is_unknown_instead_of_zero()
    {
        using var fixture = new Fixture();
        fixture.Handler.BadInventory = true;
        var material = Assert.Single((await fixture.Service.InspectAsync(7, 123, "quote", default)).Materials);
        Assert.Equal("review-required", material.Status);
        Assert.Null(material.AvailableQuantity);
    }

    [Fact]
    public async Task Zero_top_level_quantity_fails_before_a_partial_result()
    {
        using var fixture = new Fixture();
        fixture.Handler.Lines = [Line("one", "top", 0)];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectAsync(7, 123, "quote", default));
        Assert.Contains("positive quantity", error.Message);
    }

    [Theory]
    [InlineData("{\"valueType\":\"requires\",\"valueTypeUnits\":0.005,\"fixedUnits\":null}", 0, .005)]
    [InlineData("{\"valueType\":\"requires\",\"valueTypeUnits\":0.005}", 0, .005)]
    [InlineData("{\"valueType\":\"creates\",\"valueTypeUnits\":4,\"fixedUnits\":10}", 10, .25)]
    [InlineData("{\"valueType\":\"requires\",\"valueTypeUnits\":0,\"fixedUnits\":10}", 10, 0)]
    public void Verified_quantity_bases_support_nullable_fixed_and_inverse_yield(string json, decimal expectedFixed, decimal expectedPerUnit)
    {
        var usage = FulcrumMaterialRequirementsPolicy.Usage(JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Equal(expectedFixed, usage.Fixed);
        Assert.Equal(expectedPerUnit, usage.PerUnit);
    }

    [Theory]
    [InlineData("{\"valueType\":\"requires\",\"valueTypeUnits\":-1,\"fixedUnits\":10}")]
    [InlineData("{\"valueType\":\"requires\",\"valueTypeUnits\":0.005,\"fixedUnits\":-10}")]
    [InlineData("{\"valueType\":\"creates\",\"valueTypeUnits\":0,\"fixedUnits\":10}")]
    [InlineData("{\"valueType\":\"requires\",\"valueTypeUnits\":0.005,\"fixedUnits\":\"unknown\"}")]
    public void Invalid_quantity_fields_never_default_to_a_usable_requirement(string json)
    {
        var usage = FulcrumMaterialRequirementsPolicy.Usage(JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Null(FulcrumMaterialRequirementsPolicy.Required(usage.Fixed, usage.PerUnit, 1000));
    }

    private static object Line(string id, string item, decimal quantity) => new { id, itemId = item, quantity };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient http;
        public Handler Handler { get; } = new();
        public FulcrumMaterialRequirementsService Service { get; }
        public Fixture()
        {
            http = new(Handler);
            Service = new(new(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials()), new Clock());
        }
        public void Dispose() => http.Dispose();
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-key");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public object[] Lines { get; set; } = [Line("one", "top", 1000)];
        public object[] Purchases { get; set; } = [];
        public decimal Available { get; set; }
        public bool Nested { get; set; }
        public bool NoMaterials { get; set; }
        public string VendorName { get; set; } = "Nexeo Plastics LLC";
        public string PartNumber { get; set; } = "RM004420";
        public string Description { get; set; } = "Complete item description\nSecond line";
        public decimal FixedUnits { get; set; } = 10;
        public decimal PerUnit { get; set; } = .005m;
        public bool InvalidUsage { get; set; }
        public bool BadHistory { get; set; }
        public bool BadInventory { get; set; }
        public string? Failure { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (Failure switch { "inventory" => path.Contains("availableByItem"), "history" => path.Contains("purchase-history"),
                "vendor" => path.EndsWith("vendors/list"), "contacts" => path.Contains("contacts"), _ => false })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            object body = path switch
            {
                "/api/quotes/quote" => new { number = 123 },
                "/api/quotes/quote/part-line-items/list" => Lines,
                "/api/items/top" => new { id = "top", number = "TOP", itemOrigin = "make", revision = new { revision = "E" } },
                "/api/items/other" => new { id = "other", number = "OTHER", itemOrigin = "make" },
                "/api/items/sub" => new { id = "sub", number = "SUB", itemOrigin = "make" },
                "/api/items/material" => new { id = "material", number = PartNumber, itemOrigin = "buy", unitOfMeasureName = "Pound", description = Description },
                "/api/items/top/routing/input-items/list" when NoMaterials => System.Array.Empty<object>(),
                "/api/items/top/routing/input-items/list" when Nested => new[] { new { id = "subInput", itemId = "sub", valueType = "requires", valueTypeUnits = 3, fixedUnits = 2 } },
                "/api/items/top/routing/input-items/list" or "/api/items/other/routing/input-items/list" or "/api/items/sub/routing/input-items/list" =>
                    new[] { new { id = "input", itemId = "material", valueType = InvalidUsage ? "unknown" : "requires", valueTypeUnits = PerUnit, fixedUnits = FixedUnits } },
                "/api/inventory/availableByItem" when BadInventory => new { material = "unknown" },
                "/api/inventory/availableByItem" => new { material = Available },
                "/api/items/material/purchase-history" when BadHistory => new { purchases = "unknown" },
                "/api/items/material/purchase-history" => new { windowMonths = 3, totalPurchaseCount = Purchases.Length, purchases = Purchases },
                "/api/items/material/vendors/list" => new[] { new { id = "vendor-link", vendorId = "vendor", isPrimary = true } },
                "/api/vendors/vendor" => new { id = "vendor", name = VendorName, active = true },
                "/api/vendors/vendor/contacts/list" => new[] { new { id = "one", email = "one@example.com" }, new { id = "two", email = "two@example.com" },
                    new { id = "duplicate", email = "ONE@example.com" }, new { id = "bad", email = "bad@\r\nexample.com" } },
                _ => throw new InvalidOperationException($"Unexpected path: {path}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}

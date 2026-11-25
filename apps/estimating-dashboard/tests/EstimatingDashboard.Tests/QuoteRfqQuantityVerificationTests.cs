using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Tests;

public sealed class QuoteRfqQuantityVerificationTests
{
    [Fact]
    public async Task Sent_op_does_not_prove_material_coverage_until_all_quoted_tiers_are_confirmed_and_fulfilled()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        using var http = new HttpClient(new Handler());
        var client = new FulcrumQuoteGenerationClient(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials());
        var materials = new FulcrumMaterialRequirementsService(client, TimeProvider.System);
        var operations = new FulcrumOutsideProcessingService(client, TimeProvider.System);
        var quoteId = fixture.QuoteId();
        var quote = await fixture.Db.QuoteHistory.SingleAsync(row => row.Id == quoteId);
        var state = new MaterialPurchasingState { QuoteHistoryId = quote.Id };
        fixture.Db.Add(state);
        var initial = await materials.InspectAsync(quote.Id, quote.QuoteNumber, quote.SourceId, default);
        var material = Assert.Single(initial.Materials);
        Assert.Equal(new decimal[] { 100, 200, 500 }, Assert.Single(initial.Parts).Quantities);
        Assert.Equal(100m, material.RequiredMaximum);
        Assert.Equal(150m, material.AvailableQuantity);
        Assert.Equal("stock-covered", material.Status);
        Assert.NotEmpty(initial.Warnings);
        var operation = Assert.Single((await operations.InspectAsync(quote.Id, quote.QuoteNumber,
            quote.SourceId, new Dictionary<string, IReadOnlyList<decimal>>(), default)).Operations);
        Assert.Equal("needs-rfq", operation.Status);
        Assert.Equal(new decimal[] { 100, 200, 500 }, operation.QuoteQuantities);
        state.DraftsJson = JsonSerializer.Serialize(new[] { SentOp(operation, state) });
        await fixture.Db.SaveChangesAsync();
        var version = quote.Version;
        var progression = new QuoteRfqProgressionService(fixture.Db, materials, operations, TimeProvider.System);

        await progression.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);

        Assert.Null(quote.ArdaStatus);
        Assert.Equal(version, quote.Version);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Empty(await fixture.Db.QuoteHistoryAudits.ToListAsync());

        var purchasing = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, materials, TimeProvider.System);
        var confirmed = await purchasing.SaveQuantitiesAsync(quote.Id,
            new(state.Version, [new("top", 100, 500, [100, 200, 500])]), VendorQuoteTestFixture.Editor, default);
        material = Assert.Single(confirmed.Materials);
        Assert.Equal(500m, material.RequiredMaximum);
        Assert.Equal("needs-rfq", material.Status);
        Assert.Empty(confirmed.Warnings);
        state.DraftsJson = JsonSerializer.Serialize(new[] { SentOp(operation, state) });
        await fixture.Db.SaveChangesAsync();
        await progression.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        Assert.Null(quote.ArdaStatus);

        state.DraftsJson = JsonSerializer.Serialize(new[] { SentOp(operation, state),
            Sent(material.ItemId, QuoteMaterialPurchasingService.Fingerprint(material), "material", state) });
        await fixture.Db.SaveChangesAsync();
        await progression.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(EstimatingArdaStatuses.RfqSent, quote.ArdaStatus);
        Assert.Single(await fixture.Db.QuoteHistoryAudits.Where(row => row.QuoteHistoryId == quote.Id
            && row.NewValue == EstimatingArdaStatuses.RfqSent).ToListAsync());
    }

    [Fact]
    public async Task Op_only_quote_with_verified_pricing_tiers_can_advance_without_material_quantity_confirmation()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        using var http = new HttpClient(new Handler { NoMaterials = true });
        var client = new FulcrumQuoteGenerationClient(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials());
        var materials = new FulcrumMaterialRequirementsService(client, TimeProvider.System);
        var operations = new FulcrumOutsideProcessingService(client, TimeProvider.System);
        var quoteId = fixture.QuoteId();
        var quote = await fixture.Db.QuoteHistory.SingleAsync(row => row.Id == quoteId);
        var review = await materials.InspectAsync(quote.Id, quote.QuoteNumber, quote.SourceId, default);
        Assert.Empty(review.Materials);
        Assert.False(Assert.Single(review.Parts).QuantitiesOverridden);
        var operation = Assert.Single((await operations.InspectAsync(quote.Id, quote.QuoteNumber,
            quote.SourceId, new Dictionary<string, IReadOnlyList<decimal>>(), default)).Operations);
        var state = new MaterialPurchasingState { QuoteHistoryId = quote.Id };
        state.DraftsJson = JsonSerializer.Serialize(new[] { SentOp(operation, state) });
        fixture.Db.Add(state);
        await fixture.Db.SaveChangesAsync();

        await new QuoteRfqProgressionService(fixture.Db, materials, operations, TimeProvider.System)
            .TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);

        Assert.Equal(EstimatingArdaStatuses.RfqSent, quote.ArdaStatus);
    }

    private static MaterialDraftLink SentOp(OutsideProcessingOperationDto operation, MaterialPurchasingState state) =>
        Sent(operation.OperationKey, QuoteOutsideProcessingService.Fingerprint(operation), "op", state);

    private static MaterialDraftLink Sent(string id, string fingerprint, string kind, MaterialPurchasingState state) =>
        new(id, 1, fingerprint, Guid.NewGuid().ToString("N"), VendorQuoteTestFixture.Now,
            "TOP", "Quote 4445 - TOP", "Please quote", ["vendor@example.test"],
            MaterialDraftTracking.QuantitiesFingerprint(state), true, VendorQuoteTestFixture.Now,
            "sent-" + kind, Kind: kind);

    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("synthetic-test-key");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool NoMaterials { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string line = "/api/quotes/test-4445/part-line-items/line";
            object response = request.RequestUri!.AbsolutePath switch
            {
                "/api/quotes/test-4445" => new { number = 4445 },
                "/api/quotes/test-4445/part-line-items/list" => new[] { new { id = "line", itemId = "top", quantity = 100,
                    priceBreaks = new[] { new { quantity = 200 }, new { quantity = 500 } } } },
                "/api/items/top" => new { number = "TOP", itemOrigin = "make", unitOfMeasureName = "Each" },
                "/api/items/material" => new { number = "MATERIAL", itemOrigin = "buy", unitOfMeasureName = "Each", description = "Material" },
                line + "/make-summary" => new[] { new { itemId = "top", routingId = "route" } },
                line + "/routing/route/operations/list" => new[] { new { id = "op", name = "OP Finish", isOutsideProcessing = true,
                    outsideProcessingOperation = new { selectedVendorId = "vendor" } } },
                line + "/routing/route/input-items/list" => NoMaterials ? Array.Empty<object>()
                    : [new { itemId = "material", valueType = "requires", valueTypeUnits = 1 }],
                "/api/inventory/availableByItem" => new { material = 150 },
                "/api/items/material/purchase-history" => new { windowMonths = request.RequestUri.Query.Contains("months=60") ? 60 : 3,
                    totalPurchaseCount = 0, purchases = Array.Empty<object>() },
                "/api/items/material/vendors/list" => new[] { new { vendorId = "vendor", isPrimary = true } },
                "/api/vendors/vendor" => new { name = "Vendor", active = true },
                "/api/vendors/vendor/contacts/list" => new[] { new { email = "vendor@example.test" } },
                _ => throw new InvalidOperationException("Unexpected synthetic API path: " + request.RequestUri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
    }
}

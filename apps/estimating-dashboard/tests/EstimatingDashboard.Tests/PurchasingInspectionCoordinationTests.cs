using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class PurchasingInspectionCoordinationTests
{
    [Theory]
    [InlineData("op", "/api/vendors/vendor")]
    [InlineData("op", "/api/vendors/vendor/contacts/list")]
    [InlineData("material", "/api/items/top/routing/operations/list")]
    [InlineData("material", "/api/inventory/availableByItem")]
    [InlineData("material", "/api/items/material/purchase-history?months=3")]
    [InlineData("material", "/api/items/material/purchase-history?months=60")]
    [InlineData("material", "/api/items/material/vendors/list")]
    [InlineData("material", "/api/vendors/vendor")]
    [InlineData("material", "/api/vendors/vendor/contacts/list")]
    public async Task Coordination_failure_propagates_without_invalidating_existing_draft(string kind, string failurePath)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var id = fixture.QuoteId();
        fixture.Db.Add(new MaterialPurchasingState
        {
            QuoteHistoryId = id, QuantitiesJson = """[{"ItemId":"top","Minimum":100,"Maximum":null}]""",
            Version = 1, UpdatedAt = VendorQuoteTestFixture.Now, UpdatedBy = "TEST"
        });
        await fixture.Db.SaveChangesAsync();
        using var handler = new InspectionHandler();
        using var http = new HttpClient(handler);
        var client = new FulcrumQuoteGenerationClient(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials());
        var opService = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors,
            new FulcrumOutsideProcessingService(client, TimeProvider.System), TimeProvider.System);
        var materialService = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors,
            new FulcrumMaterialRequirementsService(client, TimeProvider.System), TimeProvider.System);
        if (kind == "op")
        {
            var review = await opService.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
            var operation = Assert.Single(review.Operations);
            Assert.Equal("needs-rfq", operation.Status);
            await opService.DraftAsync(id, operation.OperationKey, VendorQuoteTestFixture.Editor, default);
        }
        else
        {
            var review = await materialService.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
            var material = Assert.Single(review.Materials);
            Assert.Equal("needs-rfq", material.Status);
            await materialService.DraftAsync(id, material.ItemId, VendorQuoteTestFixture.Editor, default);
        }

        var state = await fixture.Db.Set<MaterialPurchasingState>().SingleAsync();
        var originalJson = state.DraftsJson;
        var originalVersion = state.Version;
        var originalUpdated = state.UpdatedAt;
        var originalLink = Assert.Single(MaterialDraftTracking.Read(state));
        Assert.True(originalLink.IsCurrent);
        var request = await fixture.Db.Set<VendorQuoteRequest>().AsNoTracking().SingleAsync();
        handler.FailurePath = failurePath;

        var error = await Assert.ThrowsAsync<FulcrumCoordinationUnavailableException>(async () =>
        {
            if (kind == "op") await opService.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
            else await materialService.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
        });

        Assert.Same(handler.Failure, error);
        Assert.Equal(1, handler.Failures);
        Assert.Equal(originalJson, state.DraftsJson);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        await using var persisted = fixture.AnotherContext();
        var saved = await persisted.Set<MaterialPurchasingState>().SingleAsync();
        Assert.Equal(originalJson, saved.DraftsJson);
        Assert.Equal(originalVersion, saved.Version);
        Assert.Equal(originalUpdated, saved.UpdatedAt);
        Assert.True(Assert.Single(MaterialDraftTracking.Read(saved)).IsCurrent);
        var savedRequest = await persisted.Set<VendorQuoteRequest>().SingleAsync();
        Assert.Equal(request.Id, savedRequest.Id);
        Assert.Equal(request.Status, savedRequest.Status);
        Assert.Equal(request.Version, savedRequest.Version);

        // A successful retry must still associate the same prepared draft with its request.
        handler.FailurePath = null;
        var requestId = kind == "op"
            ? Assert.Single((await opService.ReadAsync(id, VendorQuoteTestFixture.Editor, default)).Operations).RequestId
            : Assert.Single((await materialService.ReadAsync(id, VendorQuoteTestFixture.Editor, default)).Materials).RequestId;
        Assert.Equal(originalLink.RequestId, requestId);
        Assert.Equal(originalJson, state.DraftsJson);
    }

    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("synthetic-token");
    }

    private sealed class InspectionDbException() : DbException("Synthetic coordination database failure");

    private sealed class InspectionHandler : HttpMessageHandler
    {
        public string? FailurePath { get; set; }
        public int Failures { get; private set; }
        public FulcrumCoordinationUnavailableException Failure { get; } = new(new InspectionDbException());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var matchPath = path.Contains("purchase-history", StringComparison.Ordinal)
                ? path + request.RequestUri.Query : path;
            if (matchPath == FailurePath)
            {
                Failures++;
                throw Failure;
            }
            const string routing = "/api/quotes/test-4445/part-line-items/line/routing/route";
            object body = path switch
            {
                "/api/quotes/test-4445" => new { number = 4445 },
                "/api/quotes/test-4445/part-line-items/list" => new[] { new { id = "line", itemId = "top", quantity = 100 } },
                "/api/items/top" => new { id = "top", number = "TOP", itemOrigin = "make" },
                "/api/items/material" => new { id = "material", number = "MATERIAL", itemOrigin = "buy",
                    unitOfMeasureName = "Each", description = "Synthetic purchased material" },
                "/api/quotes/test-4445/part-line-items/line/make-summary" => new[] { new { itemId = "top", routingId = "route" } },
                routing + "/operations/list" => new[] { new { id = "op", name = "Heat Treat", isOutsideProcessing = true,
                    outsideProcessingOperation = new { selectedVendorId = "vendor" } } },
                routing + "/input-items/list" => Array.Empty<object>(),
                "/api/items/top/routing/operations/list" => new[] { new { id = "operation", name = "Cut" } },
                "/api/items/top/routing/input-items/list" => new[] { new { id = "input", itemId = "material",
                    valueType = "requires", valueTypeUnits = 1, fixedUnits = 0 } },
                "/api/inventory/availableByItem" => new { material = 0 },
                "/api/items/material/purchase-history" => new { windowMonths = request.RequestUri.Query == "?months=60" ? 60 : 3,
                    totalPurchaseCount = 0, purchases = Array.Empty<object>() },
                "/api/items/material/vendors/list" => new[] { new { vendorId = "vendor", isPrimary = true } },
                "/api/vendors/vendor" => new { id = "vendor", name = "Synthetic Vendor", active = true },
                "/api/vendors/vendor/contacts/list" => new[] { new { email = "quotes@example.test" } },
                _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}

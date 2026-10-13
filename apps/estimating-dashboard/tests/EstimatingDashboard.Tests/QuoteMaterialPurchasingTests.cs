using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed class QuoteMaterialPurchasingTests
{
    [Fact]
    public async Task Read_and_draft_check_quote_assignment_before_calling_fulcrum()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        var id = fixture.QuoteId(44450);
        var read = await Assert.ThrowsAsync<VendorQuoteException>(() => service.ReadAsync(id, VendorQuoteTestFixture.Editor, default));
        var draft = await Assert.ThrowsAsync<VendorQuoteException>(() => service.DraftAsync(id, "material-1", VendorQuoteTestFixture.Editor, default));
        Assert.Equal(403, read.StatusCode); Assert.Equal(403, draft.StatusCode); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Draft_rechecks_requirements_and_creates_only_one_untouched_rfq()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        Assert.Equal(2, provider.Calls);
        var request = Assert.Single(await fixture.Db.Set<VendorQuoteRequest>().ToListAsync());
        Assert.Equal("RM004420", request.PartNumber);
        Assert.Equal("Untouched", request.Status);
        Assert.Empty(await fixture.Db.Set<VendorQuoteMessage>().ToListAsync());
        var review = await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.Equal(request.Id, review.Materials.Single().RequestId);
        Assert.Equal("Untouched", review.Materials.Single().RequestStatus);
    }

    [Fact]
    public async Task Stock_change_blocks_stale_draft_without_creating_rfq()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader { Material = MaterialRfqDraftTests.Material with { Status = "stock-covered", AvailableQuantity = 58.2m } };
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        var error = await Assert.ThrowsAsync<VendorQuoteException>(() => service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(await fixture.Db.Set<VendorQuoteRequest>().ToListAsync());
    }

    [Fact]
    public async Task Draft_cannot_select_material_outside_current_quote()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, new Reader(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<VendorQuoteException>(() => service.DraftAsync(fixture.QuoteId(), "not-on-quote", VendorQuoteTestFixture.Editor, default));
        Assert.Equal(404, error.StatusCode);
        Assert.Empty(await fixture.Db.Set<VendorQuoteRequest>().ToListAsync());
    }

    private sealed class Reader : IFulcrumMaterialRequirementsReader
    {
        public int Calls { get; private set; }
        public MaterialRequirementDto Material { get; set; } = MaterialRfqDraftTests.Material;
        public IReadOnlyDictionary<string, IReadOnlyList<decimal>>? Overrides { get; private set; }
        public Task<MaterialRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId, CancellationToken ct, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null)
        {
            Calls++;
            Overrides = quantityOverrides;
            return Task.FromResult(new MaterialRequirementsDto(historyId, quoteNumber, VendorQuoteTestFixture.Now,
                [new("line", "top-item", "11488049", "E", quantityOverrides?.GetValueOrDefault("top-item") ?? [1000],
                    quantityOverrides?.ContainsKey("top-item") == true)], [Material], []));
        }
    }

    [Fact]
    public async Task Changed_requirements_do_not_inherit_sent_status_and_new_draft_reopens_existing_rfq()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        var request = await fixture.Db.Set<VendorQuoteRequest>().SingleAsync();
        request.Status = VendorQuoteStatuses.WaitingOnVendor;
        await fixture.Db.SaveChangesAsync();
        var sent = await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, sent.Materials[0].RequestStatus);
        provider.Material = provider.Material with { RequiredMinimum = 20, RequiredMaximum = 20, QuoteQuantities = [20, 30] };
        var changed = await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.Null(changed.Materials[0].RequestStatus);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        Assert.Equal(VendorQuoteStatuses.Untouched, request.Status);
        Assert.Single(await fixture.Db.Set<VendorQuoteRequest>().ToListAsync());
    }

    [Fact]
    public async Task Multiple_contact_threads_do_not_block_draft_or_change_unrelated_thread()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var primary = await fixture.ThreadAsync("RM004420", "cheri@example.test", "Quote received");
        var secondary = await fixture.ThreadAsync("RM004420", "mark@example.test", "Quote received");
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, new Reader(), TimeProvider.System);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        Assert.Equal("Untouched", (await fixture.Vendors.DetailAsync(primary.Request.Id, VendorQuoteTestFixture.Editor, default)).Request.Status);
        Assert.Equal("Quote received", (await fixture.Vendors.DetailAsync(secondary.Request.Id, VendorQuoteTestFixture.Editor, default)).Request.Status);
        Assert.Equal(2, await fixture.Db.Set<VendorQuoteRequest>().CountAsync());
    }

    [Fact]
    public async Task Quantity_corrections_persist_recalculate_and_reject_stale_saves()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        var saved = await service.SaveQuantitiesAsync(fixture.QuoteId(), new(0, [new("top-item", 500, 2000)]), VendorQuoteTestFixture.Editor, default);
        Assert.Equal(1, saved.QuantityVersion);
        await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.Equal(new decimal[] { 500, 2000 }, provider.Overrides!["top-item"]);
        var stale = await Assert.ThrowsAsync<VendorQuoteException>(() => service.SaveQuantitiesAsync(fixture.QuoteId(), new(0, []), VendorQuoteTestFixture.Editor, default));
        Assert.Equal(409, stale.StatusCode);
        await service.SaveQuantitiesAsync(fixture.QuoteId(), new(1, []), VendorQuoteTestFixture.Editor, default);
        Assert.Empty(provider.Overrides!);
        Assert.Equal(2, await fixture.Db.Set<QuoteStatusActivity>().CountAsync());
    }

    [Fact]
    public async Task Preview_and_invalid_quantities_cannot_write_or_call_fulcrum()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        var preview = VendorQuoteTestFixture.Editor with { IsPreview = true };
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => service.DraftAsync(fixture.QuoteId(), "material-1", preview, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => service.SaveQuantitiesAsync(fixture.QuoteId(), new(0, []), preview, default))).StatusCode);
        foreach (var values in new IReadOnlyList<MaterialQuantitySelectionDto>[] {
            [new("top-item", 0)], [new("top-item", 2, 1)], [new("top-item", 1), new("top-item", 2)] })
            Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => service.SaveQuantitiesAsync(fixture.QuoteId(), new(0, values), VendorQuoteTestFixture.Editor, default))).StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Schema_upgrade_is_idempotent_and_does_not_change_existing_data()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var initializer = new VendorQuoteSchemaInitializer(fixture.Db);
        await initializer.InitializeAsync(); await initializer.InitializeAsync();
        Assert.Equal(3, await fixture.Db.QuoteHistory.CountAsync());
        Assert.Empty(await fixture.Db.Set<MaterialPurchasingState>().ToListAsync());
        Assert.Contains("IF OBJECT_ID", MaterialPurchasingSchema.CreateSql(false));
    }

    [Fact]
    public async Task Manually_reassigned_rfq_does_not_hide_material_needing_email()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, new Reader(), TimeProvider.System);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        var request = await fixture.Db.Set<VendorQuoteRequest>().SingleAsync();
        request.PartNumber = "OTHER-PART";
        request.Status = VendorQuoteStatuses.WaitingOnVendor;
        await fixture.Db.SaveChangesAsync();
        var review = await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.Null(review.Materials[0].RequestId);
        Assert.Null(review.Materials[0].RequestStatus);
    }
}

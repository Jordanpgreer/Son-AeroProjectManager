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
    public async Task Different_material_items_with_same_part_and_vendor_have_separate_threads()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var provider = new Reader { SecondMaterial = MaterialRfqDraftTests.Material with { ItemId = "material-2" } };
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        await service.DraftAsync(fixture.QuoteId(), "material-2", VendorQuoteTestFixture.Editor, default);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        var requests = await fixture.Db.Set<VendorQuoteRequest>().OrderBy(request => request.Id).ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].ThreadKey, requests[1].ThreadKey);
        var materials = (await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default)).Materials;
        Assert.NotEqual(materials[0].RequestId, materials[1].RequestId);
        Assert.All(materials, material => Assert.NotNull(material.RequestId));
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
        public MaterialRequirementDto? SecondMaterial { get; set; }
        public IReadOnlyDictionary<string, IReadOnlyList<decimal>>? Overrides { get; private set; }
        public Task<MaterialRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId, CancellationToken ct, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null)
        {
            Calls++;
            Overrides = quantityOverrides;
            return Task.FromResult(new MaterialRequirementsDto(historyId, quoteNumber, VendorQuoteTestFixture.Now,
                [new("line", "top-item", "11488049", "E", quantityOverrides?.GetValueOrDefault("top-item") ?? [1000],
                    quantityOverrides?.ContainsKey("top-item") == true)],
                SecondMaterial is null ? [Material] : [Material, SecondMaterial], [], PlanningSource: PurchasingPlanningSources.Fulcrum));
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
    public async Task Multiple_contact_threads_do_not_block_draft_or_reuse_unrelated_thread()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var primary = await fixture.ThreadAsync("RM004420", "cheri@example.test", "Quote received");
        var secondary = await fixture.ThreadAsync("RM004420", "mark@example.test", "Quote received");
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, new Reader(), TimeProvider.System);
        await service.DraftAsync(fixture.QuoteId(), "material-1", VendorQuoteTestFixture.Editor, default);
        Assert.Equal("Quote received", (await fixture.Vendors.DetailAsync(primary.Request.Id, VendorQuoteTestFixture.Editor, default)).Request.Status);
        Assert.Equal("Quote received", (await fixture.Vendors.DetailAsync(secondary.Request.Id, VendorQuoteTestFixture.Editor, default)).Request.Status);
        Assert.Equal(3, await fixture.Db.Set<VendorQuoteRequest>().CountAsync());
        Assert.Single(await fixture.Db.Set<VendorQuoteRequest>().Where(request => request.Title.StartsWith("Material pricing:"))
            .Where(request => request.Status == VendorQuoteStatuses.Untouched).ToListAsync());
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
    public async Task Confirmed_tiers_round_trip_through_shared_storage_and_reach_the_op_draft()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var id = fixture.QuoteId();
        var provider = new Reader();
        var service = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, provider, TimeProvider.System);
        var saved = await service.SaveQuantitiesAsync(id,
            new(0, [new("top-item", 100, 500, [500, 200, 100, 200])]), VendorQuoteTestFixture.Editor, default);
        Assert.Equal(new decimal[] { 100, 200, 500 }, Assert.Single(saved.Parts).Quantities);

        fixture.Db.ChangeTracker.Clear();
        var reloaded = await service.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
        Assert.Equal(new decimal[] { 100, 200, 500 }, Assert.Single(reloaded.Parts).Quantities);

        var op = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors, new SubassemblyOpReader(), TimeProvider.System);
        var review = await op.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
        var operation = Assert.Single(review.Operations);
        Assert.Equal(new decimal[] { 50, 100, 250 }, operation.QuoteQuantities);
        var draft = await op.DraftAsync(id, operation.OperationKey, VendorQuoteTestFixture.Editor, default);
        Assert.Contains("Quantity: 50 / 100 / 250", draft.PlainText);
    }

    private sealed class SubassemblyOpReader : IFulcrumOutsideProcessingReader
    {
        public Task<OutsideProcessingRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct)
        {
            var tiers = quantities["top-item"].Select(quantity => quantity * .5m).ToArray();
            var operation = new OutsideProcessingOperationDto(new string('a', 64), "child", "op-1",
                "SUBASSEMBLY", "Finish", null, tiers[0], tiers[^1], tiers, "needs-rfq", "vendor-1",
                "Finish Shop", ["op@example.test"], []);
            return Task.FromResult(new OutsideProcessingRequirementsDto(historyId, quoteNumber, VendorQuoteTestFixture.Now,
                [operation], [], PurchasingPlanningSources.Fulcrum));
        }
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
            [new("top-item", 0)], [new("top-item", 2, 1)], [new("top-item", 1), new("top-item", 2)],
            [new("top-item", 1, 10, [])], [new("top-item", 1, 10, [1, 0, 10])],
            [new("top-item", 1, 10, [1, 3, 11])],
            [new("top-item", 1, 201, Enumerable.Range(1, 201).Select(quantity => (decimal)quantity).ToArray())],
            [new("top-item", 1, 10, [1, 10, 1_000_000_001m])] })
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

using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed class QuoteRfqProgressionTests
{
    [Fact]
    public async Task Advances_only_when_every_current_material_and_op_has_confirmed_sent_evidence()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var quoteId = fixture.QuoteId();
        var quote = await fixture.Db.QuoteHistory.SingleAsync(x => x.Id == quoteId);
        var material = MaterialRfqDraftTests.Material;
        var operation = Operation();
        var reader = new Reader(material, operation);
        var service = new QuoteRfqProgressionService(fixture.Db, reader, reader, new FrozenClock());
        var state = new MaterialPurchasingState { QuoteHistoryId = quote.Id, QuantitiesJson = "[]" };
        fixture.Db.Add(state);
        var materialLink = Link(material.ItemId, QuoteMaterialPurchasingService.Fingerprint(material), "material");
        var opLink = Link(operation.OperationKey, QuoteOutsideProcessingService.Fingerprint(operation), "op")
            with { SentAt = null, SentSourceId = null };
        state.DraftsJson = JsonSerializer.Serialize(new[] { materialLink, opLink });
        await fixture.Db.SaveChangesAsync();

        await service.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        Assert.Null(quote.ArdaStatus);

        state.DraftsJson = JsonSerializer.Serialize(new[] { materialLink, opLink with {
            SentAt = VendorQuoteTestFixture.Now, SentSourceId = "sent-op" } });
        await service.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(EstimatingArdaStatuses.RfqSent, quote.ArdaStatus);
        Assert.Single(await fixture.Db.QuoteHistoryAudits.Where(audit => audit.QuoteHistoryId == quote.Id
            && audit.FieldName == "Arda status" && audit.NewValue == EstimatingArdaStatuses.RfqSent).ToListAsync());
    }

    [Fact]
    public async Task Unverified_or_stale_requirements_do_not_advance_or_overwrite_a_later_status()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var quoteId = fixture.QuoteId();
        var quote = await fixture.Db.QuoteHistory.SingleAsync(x => x.Id == quoteId);
        var material = MaterialRfqDraftTests.Material;
        var reader = new Reader(material, null);
        var service = new QuoteRfqProgressionService(fixture.Db, reader, reader, new FrozenClock());
        var state = new MaterialPurchasingState { QuoteHistoryId = quote.Id, QuantitiesJson = "[]",
            DraftsJson = JsonSerializer.Serialize(new[] { Link(material.ItemId, "obsolete-fingerprint", "material") }) };
        fixture.Db.Add(state);
        await fixture.Db.SaveChangesAsync();

        await service.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        Assert.Null(quote.ArdaStatus);
        reader.Material = material with { Status = "review-required" };
        state.DraftsJson = JsonSerializer.Serialize(new[] { Link(material.ItemId,
            QuoteMaterialPurchasingService.Fingerprint(reader.Material), "material") });
        await service.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        Assert.Null(quote.ArdaStatus);

        reader.Material = material;
        quote.ArdaStatus = EstimatingArdaStatuses.ReadyForReview;
        await service.TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);
        Assert.Equal(EstimatingArdaStatuses.ReadyForReview, quote.ArdaStatus);
    }

    [Theory]
    [InlineData("material", "industrios")]
    [InlineData("material", "mixed")]
    [InlineData("material", "unknown")]
    [InlineData("op", "industrios")]
    [InlineData("op", "mixed")]
    [InlineData("op", "unknown")]
    public async Task Incomplete_planning_scope_does_not_auto_advance_even_when_visible_requirements_are_sent(
        string kind, string planningSource)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var quoteId = fixture.QuoteId();
        var quote = await fixture.Db.QuoteHistory.SingleAsync(row => row.Id == quoteId);
        var material = MaterialRfqDraftTests.Material;
        var operation = Operation();
        var reader = new Reader(material, operation);
        if (kind == "material") reader.MaterialSource = planningSource;
        else reader.OpSource = planningSource;
        var state = new MaterialPurchasingState { QuoteHistoryId = quote.Id, QuantitiesJson = "[]",
            DraftsJson = JsonSerializer.Serialize(new[] {
                Link(material.ItemId, QuoteMaterialPurchasingService.Fingerprint(material), "material"),
                Link(operation.OperationKey, QuoteOutsideProcessingService.Fingerprint(operation), "op") }) };
        fixture.Db.Add(state);
        await fixture.Db.SaveChangesAsync();
        var version = quote.Version;
        var auditCount = await fixture.Db.QuoteHistoryAudits.CountAsync();
        await new QuoteRfqProgressionService(fixture.Db, reader, reader, new FrozenClock())
            .TryAdvanceAsync(quote, VendorQuoteTestFixture.Editor, default);

        Assert.Null(quote.ArdaStatus);
        Assert.Equal(version, quote.Version);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Equal(auditCount, await fixture.Db.QuoteHistoryAudits.CountAsync());
        Assert.Equal(kind == "material" ? 0 : 1, reader.OpCalls);
    }

    private static MaterialDraftLink Link(string id, string fingerprint, string kind) =>
        new(id, 1, fingerprint, Guid.NewGuid().ToString("N"), VendorQuoteTestFixture.Now,
            "PART", "Quote 4445 - PART", "Please quote", ["vendor@example.test"],
            MaterialDraftTracking.QuantitiesFingerprint(null), true, VendorQuoteTestFixture.Now,
            "sent-material", Kind: kind);

    private static OutsideProcessingOperationDto Operation() => new("op-key", "top", "operation-1",
        "TOP", "OP Anodize", "Clear anodize", 1000, 1000, [1000], "needs-rfq",
        "vendor-1", "Finish Shop", ["vendor@example.test"], []);

    private sealed class Reader(MaterialRequirementDto material, OutsideProcessingOperationDto? operation)
        : IFulcrumMaterialRequirementsReader, IFulcrumOutsideProcessingReader
    {
        public MaterialRequirementDto Material { get; set; } = material;
        public string MaterialSource { get; set; } = PurchasingPlanningSources.Fulcrum;
        public string OpSource { get; set; } = PurchasingPlanningSources.Fulcrum;
        public int OpCalls { get; private set; }
        public Task<MaterialRequirementsDto> InspectAsync(int id, int quoteNumber, string sourceId,
            CancellationToken ct, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null) =>
            Task.FromResult(new MaterialRequirementsDto(id, quoteNumber, VendorQuoteTestFixture.Now,
                [new("line", "top", "TOP", null, [1000])], [Material], [], PlanningSource: MaterialSource));
        public Task<OutsideProcessingRequirementsDto> InspectAsync(int id, int quoteNumber, string sourceId,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct)
        {
            OpCalls++;
            return Task.FromResult(new OutsideProcessingRequirementsDto(id, quoteNumber, VendorQuoteTestFixture.Now,
                operation is null ? [] : [operation], [], OpSource));
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => VendorQuoteTestFixture.Now;
    }
}

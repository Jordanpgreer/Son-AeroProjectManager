using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed class PurchasingPlanningStateTests
{
    [Theory]
    [InlineData("industrios", false)]
    [InlineData("mixed", false)]
    [InlineData("unknown", false)]
    [InlineData("mixed", true)]
    [InlineData("unknown", true)]
    public async Task Partial_scope_read_preserves_saved_drafts_sent_evidence_and_rfq_state(string source, bool changed)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var id = fixture.QuoteId();
        var reader = new Reader();
        var materialService = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors, reader, TimeProvider.System);
        var opService = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors, reader, TimeProvider.System);
        await materialService.DraftAsync(id, reader.Material.ItemId, VendorQuoteTestFixture.Editor, default);
        await opService.DraftAsync(id, reader.Operation.OperationKey, VendorQuoteTestFixture.Editor, default);
        var state = await fixture.Db.Set<MaterialPurchasingState>().SingleAsync();
        state.DraftsJson = JsonSerializer.Serialize(MaterialDraftTracking.Read(state).Select(link => link with
            { SentAt = VendorQuoteTestFixture.Now, SentSourceId = "sent-" + link.Kind }).ToArray());
        var requests = await fixture.Db.Set<VendorQuoteRequest>().ToListAsync();
        foreach (var request in requests) request.Status = VendorQuoteStatuses.WaitingOnVendor;
        await fixture.Db.SaveChangesAsync();
        var json = state.DraftsJson;
        var version = state.Version;
        var updatedAt = state.UpdatedAt;
        var requestVersions = requests.ToDictionary(request => request.Id, request => request.Version);

        reader.Source = source;
        reader.Empty = !changed;
        if (changed)
        {
            reader.Material = reader.Material with { RequiredMinimum = 999, RequiredMaximum = 999 };
            reader.Operation = reader.Operation with { RequiredMinimum = 999, RequiredMaximum = 999 };
        }
        var material = await materialService.ReadAsync(id, VendorQuoteTestFixture.Editor, default);
        var op = await opService.ReadAsync(id, VendorQuoteTestFixture.Editor, default);

        Assert.Equal(source, material.PlanningSource);
        Assert.Equal(source, op.PlanningSource);
        Assert.Equal(json, state.DraftsJson);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        await using var persisted = fixture.AnotherContext();
        var saved = await persisted.Set<MaterialPurchasingState>().SingleAsync();
        Assert.Equal(json, saved.DraftsJson);
        Assert.Equal(version, saved.Version);
        Assert.Equal(updatedAt, saved.UpdatedAt);
        Assert.All(MaterialDraftTracking.Read(saved), link => { Assert.True(link.IsCurrent); Assert.NotNull(link.SentAt); });
        Assert.All(await persisted.Set<VendorQuoteRequest>().ToListAsync(), request =>
        {
            Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, request.Status);
            Assert.Equal(requestVersions[request.Id], request.Version);
        });
        Assert.All(material.Materials, item => Assert.Null(item.RequestId));
        Assert.All(op.Operations, item => Assert.Null(item.RequestId));
    }

    private sealed class Reader : IFulcrumMaterialRequirementsReader, IFulcrumOutsideProcessingReader
    {
        public string Source { get; set; } = PurchasingPlanningSources.Fulcrum;
        public bool Empty { get; set; }
        public MaterialRequirementDto Material { get; set; } = MaterialRfqDraftTests.Material;
        public OutsideProcessingOperationDto Operation { get; set; } = new(new string('a', 64), "top", "operation",
            "TOP", "Heat treat", null, 100, 100, [100], "needs-rfq", "vendor", "Synthetic Vendor", ["op@example.test"], []);
        public Task<MaterialRequirementsDto> InspectAsync(int id, int number, string sourceId, CancellationToken ct,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null) =>
            Task.FromResult(new MaterialRequirementsDto(id, number, VendorQuoteTestFixture.Now, [],
                Empty ? [] : [Material], [], PlanningSource: Source));
        public Task<OutsideProcessingRequirementsDto> InspectAsync(int id, int number, string sourceId,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct) =>
            Task.FromResult(new OutsideProcessingRequirementsDto(id, number, VendorQuoteTestFixture.Now,
                Empty ? [] : [Operation], [], Source));
    }
}

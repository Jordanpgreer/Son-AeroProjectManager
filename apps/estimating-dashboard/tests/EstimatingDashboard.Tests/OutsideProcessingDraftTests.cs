using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace EstimatingDashboard.Tests;

public sealed class OutsideProcessingDraftTests
{
    private static OutsideProcessingOperationDto Operation(string key, string operationId, string name) =>
        new(key, "item-1", operationId, "PART-01", name, "Handle carefully <&>",
            100, 200, [100, 200], "needs-rfq", "vendor-1", "OP Vendor",
            ["op@example.test"], []);

    [Fact]
    public void Draft_has_unique_op_details_safe_html_and_unsent_tracking_header()
    {
        var token = new string('a', 32);
        var file = OutsideProcessingRfqDraft.Create(4445, Operation(new string('b', 64), "op-1", "Heat Treat"), token);
        using var message = MimeMessage.Load(new MemoryStream(file.Content));
        Assert.Equal("Quote 4445 - PART-01", message.Subject);
        Assert.Equal("1", message.Headers["X-Unsent"]);
        Assert.Equal(token, message.Headers[MaterialDraftTracking.Header]);
        Assert.Contains("Operation: Heat Treat", message.TextBody);
        Assert.Contains("Quantity: 100 / 200", message.TextBody);
        Assert.Contains("&lt;&amp;&gt;", message.HtmlBody);
        Assert.DoesNotContain("<&>", message.HtmlBody);
    }

    [Fact]
    public async Task Each_op_gets_a_distinct_untouched_thread_and_persisted_tracking_link()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var first = Operation(new string('a', 64), "op-1", "Heat Treat");
        var second = Operation(new string('b', 64), "op-2", "Anodize");
        var reader = new Reader([first, second]);
        var service = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors, reader, new Clock());
        await service.DraftAsync(fixture.QuoteId(), first.OperationKey, VendorQuoteTestFixture.Editor, default);
        await service.DraftAsync(fixture.QuoteId(), second.OperationKey, VendorQuoteTestFixture.Editor, default);
        var requests = await fixture.Db.Set<VendorQuoteRequest>().ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].ThreadKey, requests[1].ThreadKey);
        Assert.All(requests, request => Assert.Equal(VendorQuoteStatuses.Untouched, request.Status));
        var state = await fixture.Db.Set<MaterialPurchasingState>().SingleAsync();
        var links = MaterialDraftTracking.Read(state);
        Assert.Equal(2, links.Count);
        Assert.All(links, link => Assert.Equal("op", link.Kind));
        var read = await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.All(read.Operations, op => Assert.NotNull(op.RequestId));
        Assert.All(read.Operations, op => Assert.Null(op.SentAt));
        Assert.Empty(await fixture.Db.Set<VendorQuoteMessage>().ToListAsync());
    }

    [Fact]
    public async Task Missing_vendor_or_unconfirmed_quantities_blocks_op_draft()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var operation = Operation(new string('c', 64), "op-3", "Coat");
        var reader = new Reader([operation with { Status = "review-required", VendorEmails = [], Issues = ["Select a vendor"] }]);
        var service = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors, reader, new Clock());
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => service.DraftAsync(
            fixture.QuoteId(), operation.OperationKey, VendorQuoteTestFixture.Editor, default))).StatusCode);
        reader.Operations = [operation]; reader.Warnings = ["Confirm quantity"];
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => service.DraftAsync(
            fixture.QuoteId(), operation.OperationKey, VendorQuoteTestFixture.Editor, default))).StatusCode);
        Assert.Empty(await fixture.Db.Set<VendorQuoteRequest>().ToListAsync());
    }

    [Fact]
    public async Task Op_link_is_preserved_by_material_read_and_does_not_become_a_material()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var op = Operation(new string('d', 64), "op-4", "Plate");
        var opService = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors, new Reader([op]), new Clock());
        await opService.DraftAsync(fixture.QuoteId(), op.OperationKey, VendorQuoteTestFixture.Editor, default);
        var materialService = new QuoteMaterialPurchasingService(fixture.Db, fixture.Vendors,
            new MaterialReader(), TimeProvider.System);
        await materialService.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.True(MaterialDraftTracking.Read(await fixture.Db.Set<MaterialPurchasingState>().SingleAsync()).Single().IsCurrent);
    }

    private sealed class Reader(IReadOnlyList<OutsideProcessingOperationDto> operations) : IFulcrumOutsideProcessingReader
    {
        public IReadOnlyList<OutsideProcessingOperationDto> Operations { get; set; } = operations;
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public Task<OutsideProcessingRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct) =>
            Task.FromResult(new OutsideProcessingRequirementsDto(historyId, quoteNumber,
                VendorQuoteTestFixture.Now, Operations, Warnings, PurchasingPlanningSources.Fulcrum));
    }

    private sealed class MaterialReader : IFulcrumMaterialRequirementsReader
    {
        public Task<MaterialRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId, CancellationToken ct,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null) =>
            Task.FromResult(new MaterialRequirementsDto(historyId, quoteNumber, VendorQuoteTestFixture.Now, [], [], [],
                PlanningSource: PurchasingPlanningSources.Fulcrum));
    }

    [Fact]
    public async Task Edited_sent_op_email_with_tracking_token_confirms_only_its_thread()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var first = Operation(new string('e', 64), "op-5", "Heat Treat");
        var second = Operation(new string('f', 64), "op-6", "Anodize");
        var service = new QuoteOutsideProcessingService(fixture.Db, fixture.Vendors,
            new Reader([first, second]), new Clock());
        var draft = await service.DraftAsync(fixture.QuoteId(), first.OperationKey, VendorQuoteTestFixture.Editor, default);
        await service.DraftAsync(fixture.QuoteId(), second.OperationKey, VendorQuoteTestFixture.Editor, default);
        using var mime = MimeMessage.Load(new MemoryStream(draft.Content));
        var token = mime.Headers[MaterialDraftTracking.Header];
        var imported = await fixture.Vendors.ImportAsync(new("op-sent-1", "casey@company.example", "outgoing",
            "Quote 4445 - revised outside processing details", "casey@company.example", "Casey",
            ["op@example.test"], "op@example.test", "OP Vendor", VendorQuoteTestFixture.Now.AddMinutes(1), null,
            "Please price the revised heat treat process and lead time.", [], "op-conversation", token),
            VendorQuoteTestFixture.Editor, default);
        Assert.Equal("imported", imported.Outcome);
        var read = await service.ReadAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);
        Assert.NotNull(read.Operations.Single(x => x.OperationKey == first.OperationKey).SentAt);
        Assert.Null(read.Operations.Single(x => x.OperationKey == second.OperationKey).SentAt);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor,
            read.Operations.Single(x => x.OperationKey == first.OperationKey).RequestStatus);
        Assert.Equal(VendorQuoteStatuses.Untouched,
            read.Operations.Single(x => x.OperationKey == second.OperationKey).RequestStatus);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => VendorQuoteTestFixture.Now; }
}

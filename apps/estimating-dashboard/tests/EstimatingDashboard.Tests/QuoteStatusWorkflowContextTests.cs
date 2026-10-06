using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class QuoteStatusWorkflowContextTests
{
    [Fact]
    public async Task Detail_passes_source_identity_to_link_resolver_and_exposes_result()
    {
        var links = new CapturingQuoteLinks();
        await using var f = await CreateAsync(links);

        var detail = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);

        Assert.Equal("test-4445", links.SourceId);
        Assert.Equal(4445, links.QuoteNumber);
        Assert.Equal("https://tenant.fulcrumpro.us/quotes/test-4445", detail.FulcrumQuoteUrl);
        var json = System.Text.Json.JsonSerializer.Serialize(detail,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"fulcrumQuoteUrl\":\"https://tenant.fulcrumpro.us/quotes/test-4445\"", json);
    }

    [Fact]
    public async Task Quote_details_use_sales_person_from_source_instead_of_estimating_rep()
    {
        await using var f = await CreateAsync();
        var quoteId = f.QuoteId();
        f.Db.QuoteHistory.Single(x => x.Id == quoteId).SalesPerson = "Morgan Sales";
        await f.Db.SaveChangesAsync();

        var detail = await f.Quotes.DetailAsync(quoteId, Editor, default);
        var page = await f.Quotes.ListAsync(Editor, null, null, 4445, 1, 50, default);
        Assert.Equal("Morgan Sales", detail.Quote.SalesPerson);
        Assert.Equal("Casey Lee", detail.Quote.EstimatingRep);
        Assert.Equal("Morgan Sales", page.Items.Single().SalesPerson);
        var json = System.Text.Json.JsonSerializer.Serialize(detail,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"salesPerson\":\"Morgan Sales\"", json);
    }

    [Fact]
    public async Task Detail_exposes_existing_notes_without_audit_and_uses_canonical_legacy_status_dates_and_author()
    {
        await using var f = await CreateAsync();
        var quoteId = f.QuoteId();
        var quote = f.Db.QuoteHistory.Single(x => x.Id == quoteId);
        quote.ArdaStatus = "Waiting on information";
        quote.ArdaStatusNotes = "Legacy current-status note retained without audit";
        quote.ArdaStatusChangedAt = Now.AddDays(-1);
        quote.ArdaStatusChangedBy = "SONAERO\\casey";
        quote.RfqDueDate = new DateTime(2026, 9, 14);
        quote.QuoteStatus = "Needs Approval";
        quote.QuoteFolderPath = @"S:\Estimating\Quotes\Customer 4445";
        quote.TotalValue = 12500m;
        f.Db.Users.Add(new EstimatingUserRecord { AccountName = "SONAERO\\casey", DisplayName = "Casey Lee", IsActive = true });
        await f.Db.SaveChangesAsync();

        var detail = await f.Quotes.DetailAsync(quoteId, Editor, default);
        var canonical = (await f.Workflow.GetMineAsync(Editor, default)).Single(x => x.Id == quoteId);
        Assert.Equal(canonical, detail.Workflow);
        Assert.Equal("On Hold", detail.Workflow.ArdaStatus);
        Assert.Equal("On Hold", detail.Quote.Status);
        var heldQuotes = await f.Quotes.ListAsync(Editor, null, "On Hold", null, 1, 50, default);
        Assert.Equal(quoteId, heldQuotes.Items.Single().QuoteHistoryId);
        Assert.Equal("Casey Lee", detail.Workflow.ArdaStatusChangedBy);
        Assert.Equal("Casey Lee", detail.Quote.StatusChangedBy);
        Assert.Equal("Legacy current-status note retained without audit", detail.Workflow.ArdaStatusNotes);
        Assert.Equal(new DateTime(2026, 9, 10), detail.Workflow.AutomaticEstimatingDueDate);
        Assert.Equal(12500m, detail.Workflow.TotalValue);
        Assert.Equal("Needs Approval", detail.Workflow.FulcrumQuoteStatus);
        Assert.Equal(@"S:\Estimating\Quotes\Customer 4445", detail.QuoteFolderPath);
        Assert.Empty(detail.Activity);
        var json = System.Text.Json.JsonSerializer.Serialize(detail, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"workflow\":", json);
        Assert.Contains("\"ardaStatusNotes\":\"Legacy current-status note retained without audit\"", json);
        Assert.Contains("\"quoteFolderPath\":\"S:\\\\Estimating\\\\Quotes\\\\Customer 4445\"", json);
    }

    [Fact]
    public async Task Existing_workflow_edit_persists_notes_due_override_and_audits_in_status_detail_then_restores_automatic_date()
    {
        await using var f = await CreateAsync();
        var quoteId = f.QuoteId();
        f.Db.QuoteHistory.Single(x => x.Id == quoteId).RfqDueDate = new DateTime(2026, 9, 14);
        await f.Db.SaveChangesAsync();
        var initial = await f.Quotes.DetailAsync(quoteId, Editor, default);

        await f.Workflow.UpdateAsync(quoteId, new("In progress", "Engineering review in progress", new DateTime(2026, 9, 9), initial.Workflow.Version), Editor, default);
        var edited = await f.Quotes.DetailAsync(quoteId, Editor, default);
        Assert.Equal(1, edited.Workflow.Version);
        Assert.Equal(edited.Workflow.Version, edited.Quote.Version);
        Assert.Equal(Now, edited.Quote.UpdatedAt);
        var page = await f.Quotes.ListAsync(Editor, null, null, 4445, 1, 50, default);
        Assert.Equal(Now, page.Items.Single().UpdatedAt);
        Assert.Equal(Now.AddDays(-1), f.Db.QuoteHistory.Single(x => x.Id == quoteId).UpdatedAt);
        Assert.Equal("Engineering review in progress", edited.Workflow.ArdaStatusNotes);
        Assert.True(edited.Workflow.EstimatingDueDateIsOverride);
        Assert.Equal(new DateTime(2026, 9, 9), edited.Workflow.EstimatingDueDate);
        Assert.Equal(new DateTime(2026, 9, 10), edited.Workflow.AutomaticEstimatingDueDate);
        Assert.Contains(edited.Activity, x => x.Text == "Overall status updated" && x.NewValue == "In progress");
        Assert.Contains(edited.Activity, x => x.Text == "Arda status notes updated" && x.NewValue == "Engineering review in progress");
        Assert.Contains(edited.Activity, x => x.Text == "Estimating due date override updated" && x.NewValue == "2026-09-09");

        await f.Workflow.UpdateAsync(quoteId, new("In progress", "Engineering review in progress", null, edited.Workflow.Version), Editor, default);
        var restored = await f.Quotes.DetailAsync(quoteId, Editor, default);
        Assert.Equal(2, restored.Workflow.Version);
        Assert.False(restored.Workflow.EstimatingDueDateIsOverride);
        Assert.Equal(restored.Workflow.AutomaticEstimatingDueDate, restored.Workflow.EstimatingDueDate);
        Assert.Equal("Engineering review in progress", restored.Workflow.ArdaStatusNotes);
        Assert.Contains(restored.Activity, x => x.Text == "Estimating due date override updated" && x.OldValue == "2026-09-09" && x.NewValue is null);

        await f.Workflow.UpdateAsync(
            quoteId,
            new("In progress", null, null, restored.Workflow.Version),
            Editor,
            default);
        var notesCleared = await f.Quotes.DetailAsync(quoteId, Editor, default);
        Assert.Null(notesCleared.Workflow.ArdaStatusNotes);
        Assert.Contains(notesCleared.Activity, x => x.Text == "Arda status notes updated"
            && x.OldValue == "Engineering review in progress"
            && x.NewValue is null);

        await Assert.ThrowsAsync<EstimatingQuoteWorkflowConflictException>(() => f.Workflow.UpdateAsync(quoteId, new("Complete", "Stale editor", null, edited.Workflow.Version), Editor, default));
    }

    [Fact]
    public async Task Workflow_context_remains_protected_by_quote_detail_ownership()
    {
        await using var f = await CreateAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.DetailAsync(f.QuoteId(44450), Editor, default))).StatusCode);
        var completeQuote = await f.Quotes.DetailAsync(f.QuoteId(4451), Editor, default);
        Assert.Equal(4451, completeQuote.Workflow.QuoteNumber);
        Assert.Equal(completeQuote.Quote.QuoteHistoryId, completeQuote.Workflow.Id);
    }

    [Fact]
    public async Task Detail_combines_fulcrum_and_arda_locations_and_exposes_cached_production_review()
    {
        await using var f = await CreateAsync();
        var quoteId = f.QuoteId();
        var quote = f.Db.QuoteHistory.Single(x => x.Id == quoteId);
        quote.FulcrumFilePathsJson = "[\"S:\\\\Quotes\\\\Q4445\",\"S:\\\\Quotes\\\\Q4445 Support\"]";
        quote.ArdaFilePathOverridesJson = "[\"S:\\\\Quotes\\\\Q4445 Local\"]";
        quote.ArdaSuppressedFilePathsJson = "[\"S:\\\\Quotes\\\\Q4445 Support\"]";
        quote.FulcrumQuoteItemsJson = "[{\"itemId\":\"item-1\",\"partNumber\":\"74A231660-2011\",\"revision\":\"C\"}]";
        quote.FulcrumOpWarningsJson = "[\"74A231660-2011: OP Testing\"]";
        quote.FulcrumBuyItemCount = 3;
        quote.FulcrumMakeItemCount = 1;
        quote.FulcrumInspectionUpdatedAt = Now;
        await f.Db.SaveChangesAsync();

        var detail = await f.Quotes.DetailAsync(quote.Id, Editor, default);
        var page = await f.Quotes.ListAsync(Editor, null, null, quote.QuoteNumber, 1, 50, default);
        var personal = (await f.Workflow.GetMineAsync(Editor, default)).Single(x => x.Id == quote.Id);

        Assert.Equal([@"S:\Quotes\Q4445", @"S:\Quotes\Q4445 Local"], detail.FileLocations!.Select(location => location.Path));
        Assert.Equal(["Fulcrum", "Arda"], detail.FileLocations!.Select(location => location.Source));
        Assert.Equal("74A231660-2011", detail.ProductionWarnings!.Items.Single().PartNumber);
        Assert.Equal("C", detail.ProductionWarnings.Items.Single().Revision);
        Assert.Equal(3, detail.ProductionWarnings.BuyItemCount);
        Assert.Equal(1, detail.ProductionWarnings.MakeItemCount);
        Assert.True(detail.Quote.HasFulcrumWarnings);
        Assert.True(page.Items.Single().HasFulcrumWarnings);
        Assert.True(personal.HasFulcrumWarnings);
    }

    [Fact]
    public async Task File_location_changes_are_versioned_audited_and_can_suppress_fulcrum_paths()
    {
        await using var f = await CreateAsync();
        var quoteId = f.QuoteId();
        var quote = f.Db.QuoteHistory.Single(x => x.Id == quoteId);
        quote.FulcrumFilePathsJson = "[\"S:\\\\Quotes\\\\Q4445\"]";
        await f.Db.SaveChangesAsync();

        var added = await f.Quotes.UpsertFileLocationAsync(quote.Id, new(0, @"S:\Quotes\Q4445 Support"), Editor, default);
        Assert.Equal(2, added.FileLocations!.Count);
        var edited = await f.Quotes.UpsertFileLocationAsync(quote.Id, new(1, @"S:\Quotes\Q4445 Working", @"S:\Quotes\Q4445 Support"), Editor, default);
        Assert.Contains(edited.FileLocations!, location => location.Path == @"S:\Quotes\Q4445 Working" && location.Source == "Arda");
        var removed = await f.Quotes.RemoveFileLocationAsync(quote.Id, new(2, @"S:\Quotes\Q4445"), Editor, default);
        Assert.Equal(@"S:\Quotes\Q4445 Working", Assert.Single(removed.FileLocations!).Path);
        Assert.Equal(3, await f.Db.Set<EstimatingDashboard.Api.Models.QuoteStatusActivity>().CountAsync(activity => activity.Kind == "file-location"));

        var viewer = Editor with { Role = EstimatingRoles.Viewer };
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() =>
            f.Quotes.UpsertFileLocationAsync(quote.Id, new(3, @"S:\Quotes\Denied"), viewer, default))).StatusCode);
    }

    private sealed class CapturingQuoteLinks : IQuoteSourceLinkResolver
    {
        public string? SourceId { get; private set; }
        public int QuoteNumber { get; private set; }

        public Task<string?> ResolveAsync(string sourceId, int quoteNumber, CancellationToken cancellationToken)
        {
            SourceId = sourceId;
            QuoteNumber = quoteNumber;
            return Task.FromResult<string?>("https://tenant.fulcrumpro.us/quotes/test-4445");
        }
    }
}

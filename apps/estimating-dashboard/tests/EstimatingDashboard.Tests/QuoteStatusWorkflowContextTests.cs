using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Services;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class QuoteStatusWorkflowContextTests
{
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
        Assert.Empty(detail.Activity);
        var json = System.Text.Json.JsonSerializer.Serialize(detail, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"workflow\":", json);
        Assert.Contains("\"ardaStatusNotes\":\"Legacy current-status note retained without audit\"", json);
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
}

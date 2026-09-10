using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class VendorQuoteServiceTests
{
    [Theory]
    [InlineData("Quote 4445", 4445)]
    [InlineData("RE: FW: FWD: quote 44450", 44450)]
    [InlineData("  Quote 4445  ", 4445)]
    [InlineData("Quote 4445 extra", null)]
    [InlineData("Quote #4445", null)]
    [InlineData("Quote 4445 / Quote 44450", null)]
    [InlineData("Quote 0", null)]
    [InlineData("Quote 999999999999", null)]
    [InlineData("A Quote 4445", null)]
    public void Matches_only_an_exact_quote_subject(string subject, int? expected) => Assert.Equal(expected, VendorQuoteService.MatchQuoteNumber(subject));

    [Fact]
    public async Task Accessible_quote_list_includes_completed_and_quotes_without_threads()
    {
        await using var f = await CreateAsync();
        var page = await f.Quotes.ListAsync(Editor, null, null, null, 1, 50, default);
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, x => Assert.Equal(0, x.ThreadCount));
        Assert.Contains(page.Items, x => x.QuoteNumber == 4451);
        Assert.Equal(3, (await f.Quotes.ListAsync(Admin, null, null, null, 1, 50, default)).TotalCount);
    }

    [Fact]
    public async Task Quote_notes_and_status_are_persistent_append_only_and_share_dashboard_version()
    {
        await using var f = await CreateAsync();
        var result = await f.Quotes.UpdateAsync(f.QuoteId(), new(0, "In progress", Now.Date.AddDays(3), "Waiting on material rates"), Editor, default);
        Assert.Equal("In progress", result.Quote.Status);
        Assert.Equal(1, result.Quote.Version);
        var quoteId = f.QuoteId();
        Assert.Equal("In progress", f.Db.QuoteHistory.Single(x => x.Id == quoteId).ArdaStatus);
        Assert.Contains(result.Activity, x => x.Kind == "status" && x.NewValue == "In progress");
        result = await f.Quotes.AddNoteAsync(f.QuoteId(), new(1, "Customer confirmed quantity"), Editor, default);
        Assert.Equal(2, result.Activity.Count(x => x.Kind == "note"));
        Assert.All(result.Activity, x => Assert.Equal(Now, x.OccurredAt));
        var conflict = await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.AddNoteAsync(f.QuoteId(), new(1, "stale"), Editor, default));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(2, await f.Db.Set<QuoteStatusActivity>().CountAsync(x => x.Kind == "note"));
    }

    [Fact]
    public async Task Separate_part_threads_keep_statuses_notes_and_combined_history()
    {
        await using var f = await CreateAsync();
        var first = await f.ThreadAsync("P-100");
        var second = await f.ThreadAsync("P-200");
        await f.Vendors.UpdateAsync(first.Request.Id, new(first.Request.Version, "Silicone Prime", "Rates for seals", "Rates requested", null, "RFQ sent to Silicone Prime"), Editor, default);
        var detail = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        Assert.Equal(2, detail.Threads.Count);
        Assert.Contains(detail.Activity, x => x.Text == "RFQ sent to Silicone Prime" && x.PartNumber == "P-100" && x.RequestId == first.Request.Id);
        Assert.Equal("Untouched", detail.Threads.Single(x => x.Request.Id == second.Request.Id).Request.Status);
        Assert.Equal("Untouched", detail.Quote.Status);
    }

    [Fact]
    public async Task Duplicate_threads_are_rejected_but_same_part_different_vendors_are_allowed()
    {
        await using var f = await CreateAsync();
        await f.ThreadAsync("P-100");
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.ThreadAsync("p-100"))).StatusCode);
        await f.ThreadAsync("P-100", "other@vendor.example");
        Assert.Equal(2, await f.Db.Set<VendorQuoteRequest>().CountAsync());
    }

    [Fact]
    public async Task Quote_search_includes_accessible_part_and_vendor_and_creation_captures_initial_status()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync("SPECIAL-PART", status: "Rates requested");
        Assert.Equal("Rates requested", thread.Activity.Single(x => x.Kind == "created").NewValue);
        Assert.Equal(4445, (await f.Quotes.ListAsync(Editor, "special-part", null, null, 1, 50, default)).Items.Single().QuoteNumber);
        Assert.Equal(4445, (await f.Quotes.ListAsync(Editor, "silicone prime", null, null, 1, 50, default)).Items.Single().QuoteNumber);
    }

    [Fact]
    public async Task Thread_metadata_changes_are_audited_and_rekeyed()
    {
        await using var f = await CreateAsync();
        var created = await f.ThreadAsync();
        var updated = await f.Vendors.UpdateAsync(created.Request.Id, new(0, "Silicone Prime", "Rates", "Rates requested", null, null, "P-101", "sales@vendor.example"), Editor, default);
        Assert.Equal("P-101", updated.Request.PartNumber);
        Assert.Equal("sales@vendor.example", updated.Request.VendorEmail);
        Assert.Contains(updated.Activity, x => x.Text == "Part number updated" && x.NewValue == "P-101");
        Assert.Contains(updated.Activity, x => x.Text == "Vendor email updated");
    }

    [Fact]
    public async Task Notes_reject_blank_and_oversized_inputs_without_changes()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync();
        await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.AddNoteAsync(thread.Request.Id, new(0, "   "), Editor, default));
        await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.AddNoteAsync(thread.Request.Id, new(0, new string('x', 4001)), Editor, default));
        Assert.Equal(0, (await f.Vendors.DetailAsync(thread.Request.Id, Editor, default)).Request.NoteCount);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("preview")]
    [InlineData("disabled")]
    [InlineData("without-history")]
    public async Task Writes_deny_insufficient_access(string scenario)
    {
        await using var f = await CreateAsync();
        var access = scenario switch { "viewer" => Editor with { Role = EstimatingRoles.Viewer },
            "preview" => Editor with { IsPreview = true }, "disabled" => Editor with { IsEnabled = false },
            _ => Editor with { GrantedPermissions = [EstimatingPermissions.ManageQuotes] } };
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.AddNoteAsync(f.QuoteId(), new(0, "No"), access, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportAsync(Message(), access, default))).StatusCode);
        Assert.Empty(f.Db.Set<VendorQuoteMessage>());
    }

    [Fact]
    public async Task Cross_estimator_reads_writes_and_imports_are_denied_but_manager_can_access()
    {
        await using var f = await CreateAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.DetailAsync(f.QuoteId(44450), Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportAsync(Message(subject: "Quote 44450"), Editor, default))).StatusCode);
        Assert.Equal(44450, (await f.Quotes.DetailAsync(f.QuoteId(44450), Admin, default)).Quote.QuoteNumber);
    }

    [Fact]
    public async Task Sync_heartbeat_is_caller_scoped_and_reports_deferred_without_false_success()
    {
        await using var f = await CreateAsync();
        var status = await f.Vendors.HeartbeatAsync(new("casey@company.example", "laptop", 2, 1, 1), Editor, default);
        Assert.Null(status.LastSuccessAt);
        Assert.Empty(await f.Vendors.SyncStatusAsync(Admin, default));
        status = await f.Vendors.HeartbeatAsync(new("casey@company.example", "laptop", 2, 1, 0), Editor, default);
        Assert.Equal(Now, status.LastSuccessAt);
        Assert.Single(await f.Vendors.SyncStatusAsync(Editor, default));
    }
}

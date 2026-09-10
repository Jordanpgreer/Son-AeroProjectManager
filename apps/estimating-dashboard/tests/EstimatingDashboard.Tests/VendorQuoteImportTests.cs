using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class VendorQuoteImportTests
{
    [Fact]
    public async Task Import_is_idempotent_across_mailboxes_and_does_not_reset_notes_or_status()
    {
        await using var f = await CreateAsync();
        var result = await f.Vendors.ImportAsync(Message(), Editor, default);
        Assert.Equal("imported", result.Outcome);
        var detail = await f.Vendors.DetailAsync(result.RequestId!.Value, Editor, default);
        Assert.Equal("Reply received", detail.Request.Status);
        await f.Vendors.UpdateAsync(result.RequestId.Value, new(detail.Request.Version, "Silicone Prime", "Seal pricing", "Accepted", null, "Approved pricing"), Editor, default);
        result = await f.Vendors.ImportAsync(Message() with { Mailbox = "other@company.example" }, Editor, default);
        Assert.Equal("duplicate", result.Outcome);
        detail = await f.Vendors.DetailAsync(result.RequestId!.Value, Editor, default);
        Assert.Equal("Accepted", detail.Request.Status);
        Assert.Single(detail.Messages);
        Assert.Contains(detail.Activity, x => x.Text == "Approved pricing");
    }

    [Fact]
    public async Task Same_message_sent_to_multiple_vendors_creates_each_thread_once()
    {
        await using var f = await CreateAsync();
        var message = Message() with { Direction = "outgoing", FromAddress = "casey@company.example", ToAddresses = ["quotes@vendor.example", "second@vendor.example"] };
        await f.Vendors.ImportAsync(message, Editor, default);
        await f.Vendors.ImportAsync(message with { VendorEmail = "second@vendor.example", VendorName = "Second vendor" }, Editor, default);
        Assert.Equal(2, await f.Db.Set<VendorQuoteMessage>().CountAsync());
        Assert.Equal(2, await f.Db.Set<VendorQuoteRequest>().CountAsync());
        Assert.All(await f.Db.Set<VendorQuoteRequest>().ToListAsync(), x => Assert.Equal("Untouched", x.Status));
        var details = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        Assert.All(details.Threads, thread => Assert.Equal(thread.Request.VendorEmail, thread.Messages.Single().VendorEmail));
        var json = System.Text.Json.JsonSerializer.Serialize(details, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"vendorEmail\":\"second@vendor.example\"", json);
    }

    [Fact]
    public async Task Ambiguous_vendor_parts_save_unassigned_then_manual_assignment_guides_conversation()
    {
        await using var f = await CreateAsync();
        var first = await f.ThreadAsync("PART-A");
        await f.ThreadAsync("PART-B");
        var result = await f.Vendors.ImportAsync(Message(), Editor, default);
        Assert.Equal("unassigned", result.Outcome); Assert.Null(result.RequestId);
        var detail = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        var message = Assert.Single(detail.UnassignedMessages);
        detail = await f.Quotes.AssignMessageAsync(f.QuoteId(), message.Id, new(detail.Quote.Version, first.Request.Id), Editor, default);
        Assert.Empty(detail.UnassignedMessages);
        result = await f.Vendors.ImportAsync(Message("<followup@vendor.example>"), Editor, default);
        Assert.Equal(first.Request.Id, result.RequestId);
        Assert.Equal(2, (await f.Vendors.DetailAsync(first.Request.Id, Editor, default)).Messages.Count);
    }

    [Fact]
    public async Task Manual_assignment_cannot_cross_quotes_or_vendors_or_overwrite_existing_assignment()
    {
        await using var f = await CreateAsync();
        var first = await f.ThreadAsync("A"); await f.ThreadAsync("B");
        var other = await f.ThreadAsync("C", "other@vendor.example");
        await f.Vendors.ImportAsync(Message(), Editor, default);
        var message = (await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).UnassignedMessages.Single();
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.AssignMessageAsync(f.QuoteId(), message.Id, new(0, other.Request.Id), Editor, default))).StatusCode);
        var assigned = await f.Quotes.AssignMessageAsync(f.QuoteId(), message.Id, new(0, first.Request.Id), Editor, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.AssignMessageAsync(f.QuoteId(), message.Id, new(assigned.Quote.Version, first.Request.Id), Editor, default))).StatusCode);
    }

    [Theory]
    [InlineData("Waiting on vendor")]
    [InlineData("Rates requested")]
    [InlineData("Accepted")]
    [InlineData("Quote received")]
    public async Task Historical_backfill_preserves_newer_manual_status(string status)
    {
        await using var f = await CreateAsync();
        var created = await f.ThreadAsync(status: status);
        await f.Vendors.ImportAsync(Message() with { SentAt = Now.AddDays(-1), ReceivedAt = Now.AddDays(-1) }, Editor, default);
        Assert.Equal(status, (await f.Vendors.DetailAsync(created.Request.Id, Editor, default)).Request.Status);
    }

    [Fact]
    public async Task Newer_reply_only_advances_eligible_thread_and_never_overall_status()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync(status: "Waiting on vendor");
        await f.Quotes.UpdateAsync(f.QuoteId(), new(0, "Ready for Review"), Editor, default);
        await f.Vendors.ImportAsync(Message(), Editor, default);
        Assert.Equal("Reply received", (await f.Vendors.DetailAsync(thread.Request.Id, Editor, default)).Request.Status);
        Assert.Equal("Ready for Review", (await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).Quote.Status);
    }

    [Fact]
    public async Task Invalid_import_is_atomic_and_unknown_quote_remains_retryable()
    {
        await using var f = await CreateAsync();
        var bad = Message() with { Attachments = [new("rates.pdf", "application/pdf", "invalid-base64")] };
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportAsync(bad, Editor, default))).StatusCode);
        Assert.Empty(await f.Db.Set<VendorQuoteMessage>().ToListAsync());
        Assert.Empty(await f.Db.Set<VendorQuoteRequest>().ToListAsync());
        Assert.Equal("unmatched", (await f.Vendors.ImportAsync(Message(subject: "Quote 7777"), Editor, default)).Outcome);
        Assert.Equal("ambiguous", (await f.Vendors.ImportAsync(Message(subject: "Quote 4445 and Quote 44450"), Editor, default)).Outcome);
        Assert.Empty(await f.Db.Set<VendorQuoteMessage>().ToListAsync());
    }

    [Fact]
    public async Task Attachment_download_requires_quote_access_and_has_sanitized_metadata()
    {
        await using var f = await CreateAsync();
        var result = await f.Vendors.ImportAsync(Message() with { Attachments = [new("../../<rates>.html", "text/html", Convert.ToBase64String("test"u8.ToArray()))] }, Editor, default);
        var attachment = (await f.Vendors.DetailAsync(result.RequestId!.Value, Editor, default)).Messages.Single().Attachments.Single();
        Assert.Equal("rates.html", attachment.FileName);
        Assert.Equal("application/octet-stream", attachment.ContentType);
        Assert.Equal("test"u8.ToArray(), (await f.Vendors.AttachmentAsync(attachment.Id, Editor, default)).Content);
        var other = Editor with { AccountName = "SONAERO\\other", DisplayName = "Someone Else" };
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.AttachmentAsync(attachment.Id, other, default))).StatusCode);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("vendor")]
    [InlineData("body")]
    [InlineData("date")]
    [InlineData("attachment-count")]
    public async Task Boundary_validation_rejects_invalid_envelopes_before_persistence(string scenario)
    {
        await using var f = await CreateAsync();
        var message = scenario switch {
            "direction" => Message() with { Direction = "unknown" },
            "vendor" => Message() with { VendorEmail = "somebody@vendor.example" },
            "body" => Message() with { BodyText = new string('x', 200001) },
            "date" => Message() with { SentAt = Now.AddYears(2) },
            _ => Message() with { Attachments = Enumerable.Repeat(new ImportVendorQuoteAttachmentDto("x", null, ""), 26).ToList() }
        };
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportAsync(message, Editor, default))).StatusCode);
        Assert.Empty(f.Db.Set<VendorQuoteRequest>());
    }

    [Fact]
    public async Task Oversized_attachment_rejected_without_a_partial_message()
    {
        await using var f = await CreateAsync();
        var message = Message() with { Attachments = [new("oversized.pdf", null, Convert.ToBase64String(new byte[10 * 1024 * 1024 + 1]))] };
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportAsync(message, Editor, default))).StatusCode);
        Assert.Empty(f.Db.Set<VendorQuoteMessage>());
    }
}

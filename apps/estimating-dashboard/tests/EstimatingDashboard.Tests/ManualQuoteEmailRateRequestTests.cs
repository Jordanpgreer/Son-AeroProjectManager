using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class ManualQuoteEmailRateRequestTests
{
    [Fact]
    public async Task Manual_rate_request_saves_email_attachment_note_and_rfq_status_together()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync("PART-A");
        await f.Quotes.UpdateAsync(f.QuoteId(), new(0, "In progress"), Editor, default);
        var file = ManualEmailFixtures.Eml("Original supplier request", outgoing: true);
        var options = new ManualQuoteEmailImportOptions(1, thread.Request.Id, "outgoing", null, "Request covers 25 pieces.", true);

        var result = await f.Vendors.ImportEmailAsync(f.QuoteId(), "sent.eml", file, options, Editor, default);

        Assert.Equal("imported", result.Outcome);
        var quote = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        var saved = quote.Threads.Single();
        var email = Assert.Single(saved.Messages);
        Assert.True(email.IsRateRequest);
        Assert.Equal("Original supplier request", email.Subject);
        Assert.Equal("Synthetic vendor pricing details", email.BodyText.Trim());
        Assert.Equal("rates.txt", email.Attachments.Single().FileName);
        Assert.Equal("Waiting on vendor", saved.Request.Status);
        Assert.Equal(1, saved.Request.Version);
        Assert.Equal(2, quote.Quote.Version);
        Assert.Equal("In progress", quote.Quote.Status);
        Assert.Contains(saved.Activity, x => x.Kind == "note" && x.Text == "Request covers 25 pieces.");
        Assert.Contains(saved.Activity, x => x.Kind == "status" && x.NewValue == "Waiting on vendor");
        await using var persisted = f.AnotherContext();
        Assert.True((await persisted.Set<VendorQuoteMessage>().SingleAsync()).IsRateRequest);
        Assert.Equal(1, await persisted.Set<VendorQuoteAttachment>().CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_import_does_not_reapply_label_status_or_note(bool originalLabel)
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync();
        var file = ManualEmailFixtures.Eml(outgoing: true);
        var original = new ManualQuoteEmailImportOptions(0, thread.Request.Id, "outgoing", null, "One original note", originalLabel);
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "sent.eml", file, original, Editor, default);
        var afterImport = await f.Vendors.DetailAsync(thread.Request.Id, Editor, default);
        await f.Vendors.UpdateAsync(thread.Request.Id,
            new(afterImport.Request.Version, "Silicone Prime", "Material pricing", "Quote received"), Editor, default);
        var beforeRetry = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);

        // A stale duplicate upload is safe: it cannot relabel existing evidence or reapply a workflow transition.
        var duplicate = await f.Vendors.ImportEmailAsync(f.QuoteId(), "same-file.eml", file,
            original with { IsRateRequest = !originalLabel, Note = "Must not be appended" }, Editor, default);

        Assert.Equal("duplicate", duplicate.Outcome);
        var repeated = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        Assert.Equal(originalLabel, repeated.Threads.Single().Messages.Single().IsRateRequest);
        Assert.Equal("Quote received", repeated.Threads.Single().Request.Status);
        Assert.Equal(beforeRetry.Quote.Version, repeated.Quote.Version);
        Assert.Equal(beforeRetry.Threads.Single().Request.Version, repeated.Threads.Single().Request.Version);
        Assert.Equal(beforeRetry.Activity.Count, repeated.Activity.Count);
        Assert.Equal(1, repeated.Threads.Single().Request.NoteCount);
    }

    [Theory]
    [InlineData("incoming", 400)]
    [InlineData("unassigned", 400)]
    [InlineData("wrong-quote", 400)]
    [InlineData("stale", 409)]
    [InlineData("preview", 403)]
    public async Task Invalid_rate_request_import_is_rejected_before_email_status_or_note_are_persisted(string scenario, int expectedCode)
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync();
        var incoming = scenario == "incoming";
        var file = ManualEmailFixtures.Eml(outgoing: !incoming);
        var quoteId = scenario == "wrong-quote" ? f.QuoteId(4451) : f.QuoteId();
        var options = new ManualQuoteEmailImportOptions(scenario == "stale" ? 9 : 0,
            scenario == "unassigned" ? null : thread.Request.Id, incoming ? "incoming" : "outgoing",
            "quotes@vendor.example", "Must not be appended", true);
        var access = scenario == "preview" ? Editor with { IsPreview = true } : Editor;

        var failure = await Assert.ThrowsAsync<VendorQuoteException>(() =>
            f.Vendors.ImportEmailAsync(quoteId, "email.eml", file, options, access, default));

        Assert.Equal(expectedCode, failure.StatusCode);
        await using var persisted = f.AnotherContext();
        Assert.Empty(await persisted.Set<VendorQuoteMessage>().ToListAsync());
        Assert.Empty(await persisted.Set<VendorQuoteAttachment>().ToListAsync());
        Assert.Equal("Untouched", (await persisted.Set<VendorQuoteRequest>().SingleAsync()).Status);
        Assert.All(await persisted.QuoteHistory.ToListAsync(), x => Assert.Equal(0, x.Version));
        Assert.DoesNotContain(await persisted.Set<VendorQuoteActivity>().ToListAsync(), x => x.Kind == "note");
    }

    [Fact]
    public async Task Invalid_note_rejects_labeled_manual_import_before_email_and_status_are_saved()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync();
        var file = ManualEmailFixtures.Eml(outgoing: true);
        var options = new ManualQuoteEmailImportOptions(0, thread.Request.Id, "outgoing", null, new string('x', 4001), true);

        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() =>
            f.Vendors.ImportEmailAsync(f.QuoteId(), "sent.eml", file, options, Editor, default))).StatusCode);
        await using var persisted = f.AnotherContext();
        Assert.Empty(await persisted.Set<VendorQuoteMessage>().ToListAsync());
        Assert.Empty(await persisted.Set<VendorQuoteAttachment>().ToListAsync());
        Assert.Equal("Untouched", (await persisted.Set<VendorQuoteRequest>().SingleAsync()).Status);
        var quoteId = f.QuoteId();
        Assert.Equal(0, (await persisted.QuoteHistory.SingleAsync(x => x.Id == quoteId)).Version);
    }
}

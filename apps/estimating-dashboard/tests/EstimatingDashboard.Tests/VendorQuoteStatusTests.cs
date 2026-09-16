using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class VendorQuoteStatusTests
{
    [Fact]
    public async Task Rfq_options_offer_only_the_three_current_statuses()
    {
        await using var f = await CreateAsync();
        var options = await f.Vendors.OptionsAsync(Editor, null, default);
        Assert.Equal(["Untouched", "Waiting on vendor", "Quote received"], options.Statuses);
    }

    [Theory]
    [InlineData("Rates requested", "Waiting on vendor")]
    [InlineData("Reply received", "Waiting on vendor")]
    [InlineData("Under review", "Quote received")]
    [InlineData("Accepted", "Quote received")]
    [InlineData("Declined", "Untouched")]
    [InlineData("Cancelled", "Untouched")]
    public async Task Legacy_statuses_are_normalized_in_details_and_filters_without_rewriting_history(string stored, string displayed)
    {
        await using var f = await CreateAsync();
        var created = await f.ThreadAsync();
        var record = await f.Db.Set<VendorQuoteRequest>().SingleAsync(x => x.Id == created.Request.Id);
        record.Status = stored;
        record.Activity.Single(x => x.Kind == "created").NewValue = stored;
        await f.Db.SaveChangesAsync();

        var detail = await f.Vendors.DetailAsync(record.Id, Editor, default);
        var matching = await f.Vendors.ListAsync(Editor, null, displayed, null, 1, 50, default);
        var other = displayed == "Untouched" ? "Quote received" : "Untouched";
        var nonMatching = await f.Vendors.ListAsync(Editor, null, other, null, 1, 50, default);
        var quote = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);

        Assert.Equal(displayed, detail.Request.Status);
        Assert.Equal(displayed, matching.Items.Single().Status);
        Assert.Empty(nonMatching.Items);
        Assert.Equal(displayed, quote.Threads.Single().Request.Status);
        Assert.Equal(stored, detail.Activity.Single(x => x.Kind == "created").NewValue);
        Assert.Equal(stored, (await f.Db.Set<VendorQuoteRequest>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, record.Version);
    }

    [Theory]
    [InlineData("Rates requested")]
    [InlineData("Reply received")]
    [InlineData("Under review")]
    [InlineData("Accepted")]
    [InlineData("Declined")]
    [InlineData("Cancelled")]
    public async Task New_requests_and_updates_reject_retired_statuses_without_changes(string retired)
    {
        await using var f = await CreateAsync();
        var created = await f.ThreadAsync("EXISTING");
        var createError = await Assert.ThrowsAsync<VendorQuoteException>(() => f.ThreadAsync("NEW", status: retired));
        var updateError = await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.UpdateAsync(created.Request.Id,
            new(created.Request.Version, "Silicone Prime", "Material pricing", retired, null, "Must not be added"), Editor, default));

        Assert.Equal(400, createError.StatusCode);
        Assert.Equal(400, updateError.StatusCode);
        Assert.Equal(1, await f.Db.Set<VendorQuoteRequest>().CountAsync());
        var unchanged = await f.Vendors.DetailAsync(created.Request.Id, Editor, default);
        Assert.Equal("Untouched", unchanged.Request.Status);
        Assert.Equal(0, unchanged.Request.Version);
        Assert.Single(unchanged.Activity);
    }

    [Fact]
    public async Task Note_only_save_preserves_legacy_status_then_a_real_status_change_audits_original_value()
    {
        await using var f = await CreateAsync();
        var created = await f.ThreadAsync();
        var record = await f.Db.Set<VendorQuoteRequest>().SingleAsync(x => x.Id == created.Request.Id);
        record.Status = "Rates requested";
        await f.Db.SaveChangesAsync();

        var noted = await f.Vendors.UpdateAsync(record.Id,
            new(created.Request.Version, "Silicone Prime", "Material pricing", "Waiting on vendor", null, "Followed up with vendor"), Editor, default);
        Assert.Equal("Waiting on vendor", noted.Request.Status);
        Assert.Equal("Rates requested", record.Status);
        Assert.DoesNotContain(noted.Activity, x => x.Kind == "status");

        var received = await f.Vendors.UpdateAsync(record.Id,
            new(noted.Request.Version, "Silicone Prime", "Material pricing", "Quote received"), Editor, default);
        Assert.Equal("Quote received", record.Status);
        Assert.Contains(received.Activity, x => x.Kind == "status" && x.OldValue == "Rates requested" && x.NewValue == "Quote received");
    }

    [Theory]
    [InlineData(" untouched ", "Untouched")]
    [InlineData("WAITING ON VENDOR", "Waiting on vendor")]
    [InlineData("quote received", "Quote received")]
    public async Task Current_status_writes_use_canonical_spelling(string input, string expected)
    {
        await using var f = await CreateAsync();
        var created = await f.ThreadAsync(status: input);
        Assert.Equal(expected, created.Request.Status);
        Assert.Equal(expected, created.Activity.Single(x => x.Kind == "created").NewValue);
    }
}

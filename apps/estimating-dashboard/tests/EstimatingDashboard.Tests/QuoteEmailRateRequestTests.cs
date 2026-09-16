using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class QuoteEmailRateRequestTests
{
    [Fact]
    public async Task Marking_sent_email_updates_only_its_rfq_and_metadata_and_preserves_original_evidence()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync("PART-A");
        var otherThread = await f.ThreadAsync("PART-B", "other@vendor.example");
        await f.Quotes.UpdateAsync(f.QuoteId(), new(0, "Ready for Review"), Editor, default);
        var original = Outgoing() with { BodyText = "Please quote this original specification." };
        await f.Vendors.ImportAsync(original, Editor, default);
        var before = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        var initialThread = before.Threads.Single(x => x.Request.Id == thread.Request.Id);
        var email = Assert.Single(initialThread.Messages);
        Assert.False(email.IsRateRequest);

        var marked = await f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id,
            new(before.Quote.Version, true), Editor, default);

        var changed = marked.Threads.Single(x => x.Request.Id == thread.Request.Id);
        Assert.True(changed.Messages.Single().IsRateRequest);
        Assert.Equal("Waiting on vendor", changed.Request.Status);
        Assert.Equal(initialThread.Request.Version + 1, changed.Request.Version);
        Assert.Equal(before.Quote.Version + 1, marked.Quote.Version);
        Assert.Equal("Ready for Review", marked.Quote.Status);
        Assert.Equal("Untouched", marked.Threads.Single(x => x.Request.Id == otherThread.Request.Id).Request.Status);
        Assert.Equal(original.Subject, changed.Messages.Single().Subject);
        Assert.Equal(original.BodyText, changed.Messages.Single().BodyText);
        Assert.Equal(original.SentAt, changed.Messages.Single().SentAt);
        Assert.Contains(changed.Activity, x => x.Kind == "status" && x.NewValue == "Waiting on vendor");
        await using var persisted = f.AnotherContext();
        Assert.True((await persisted.Set<VendorQuoteMessage>().SingleAsync()).IsRateRequest);
    }

    [Fact]
    public async Task Retrying_same_label_is_noop_and_clearing_does_not_reset_current_rfq_status()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync();
        await f.Vendors.ImportAsync(Outgoing(), Editor, default);
        var email = await f.Db.Set<VendorQuoteMessage>().SingleAsync();
        var marked = await f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id, new(0, true), Editor, default);
        var markedThread = marked.Threads.Single();
        await f.Vendors.UpdateAsync(thread.Request.Id,
            new(markedThread.Request.Version, "Silicone Prime", "Material pricing", "Quote received"), Editor, default);
        var beforeRetry = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);

        var repeated = await f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id,
            new(beforeRetry.Quote.Version, true), Editor, default);
        Assert.Equal(beforeRetry.Quote.Version, repeated.Quote.Version);
        Assert.Equal(beforeRetry.Threads.Single().Request.Version, repeated.Threads.Single().Request.Version);
        Assert.Equal(beforeRetry.Activity.Count, repeated.Activity.Count);
        Assert.Equal("Quote received", repeated.Threads.Single().Request.Status);

        var cleared = await f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id,
            new(repeated.Quote.Version, false), Editor, default);
        Assert.False(cleared.Threads.Single().Messages.Single().IsRateRequest);
        Assert.Equal("Quote received", cleared.Threads.Single().Request.Status);
        Assert.Equal(repeated.Quote.Version + 1, cleared.Quote.Version);
        Assert.Equal(repeated.Threads.Single().Request.Version + 1, cleared.Threads.Single().Request.Version);

        var recleared = await f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id,
            new(cleared.Quote.Version, false), Editor, default);
        Assert.Equal(cleared.Quote.Version, recleared.Quote.Version);
        Assert.Equal(cleared.Activity.Count, recleared.Activity.Count);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("preview")]
    [InlineData("disabled")]
    [InlineData("other-estimator")]
    public async Task Rate_request_updates_require_write_access_to_the_quote(string scenario)
    {
        await using var f = await CreateAsync();
        await f.ThreadAsync();
        await f.Vendors.ImportAsync(Outgoing(), Editor, default);
        var email = await f.Db.Set<VendorQuoteMessage>().SingleAsync();
        var access = scenario switch
        {
            "viewer" => Editor with { Role = EstimatingRoles.Viewer },
            "preview" => Editor with { IsPreview = true },
            "disabled" => Editor with { IsEnabled = false },
            _ => Editor with { AccountName = "SONAERO\\other", DisplayName = "Someone Else" }
        };

        var failure = await Assert.ThrowsAsync<VendorQuoteException>(() =>
            f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id, new(0, true), access, default));
        Assert.Equal(403, failure.StatusCode);
        Assert.False(email.IsRateRequest);
        var quoteId = f.QuoteId();
        Assert.Equal(0, f.Db.QuoteHistory.Single(x => x.Id == quoteId).Version);
    }

    [Fact]
    public async Task Stale_version_is_rejected_even_for_a_noop_without_status_or_metadata_changes()
    {
        await using var f = await CreateAsync();
        await f.ThreadAsync();
        await f.Vendors.ImportAsync(Outgoing(), Editor, default);
        var email = await f.Db.Set<VendorQuoteMessage>().SingleAsync();
        await f.Quotes.AddNoteAsync(f.QuoteId(), new(0, "A newer quote edit"), Editor, default);

        foreach (var desired in new[] { false, true })
            Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() =>
                f.Quotes.SetEmailRateRequestAsync(f.QuoteId(), email.Id, new(0, desired), Editor, default))).StatusCode);
        Assert.False(email.IsRateRequest);
        Assert.Equal("Untouched", (await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).Threads.Single().Request.Status);
    }

    [Theory]
    [InlineData("incoming", 400)]
    [InlineData("unassigned", 400)]
    [InlineData("removed", 409)]
    [InlineData("wrong-quote", 404)]
    public async Task Only_active_outgoing_emails_on_this_quote_and_an_rfq_can_be_labeled(string scenario, int expectedCode)
    {
        await using var f = await CreateAsync();
        await f.ThreadAsync("A");
        if (scenario == "unassigned") await f.ThreadAsync("B");
        await f.Vendors.ImportAsync(scenario == "incoming" ? Message() : Outgoing(), Editor, default);
        var email = await f.Db.Set<VendorQuoteMessage>().SingleAsync();
        var quoteId = scenario == "wrong-quote" ? f.QuoteId(4451) : f.QuoteId();
        if (scenario == "removed")
            await f.Quotes.RemoveEmailAsync(f.QuoteId(), email.Id, new(0), Admin, default);
        var before = await f.Quotes.DetailAsync(quoteId, Editor, default);

        var failure = await Assert.ThrowsAsync<VendorQuoteException>(() =>
            f.Quotes.SetEmailRateRequestAsync(quoteId, email.Id, new(before.Quote.Version, true), Editor, default));
        Assert.Equal(expectedCode, failure.StatusCode);
        Assert.False(email.IsRateRequest);
        Assert.Equal(before.Quote.Version, (await f.Quotes.DetailAsync(quoteId, Editor, default)).Quote.Version);
        Assert.All(await f.Db.Set<VendorQuoteRequest>().ToListAsync(), x => Assert.Equal("Untouched", x.Status));
    }

    private static ImportVendorQuoteMessageDto Outgoing() => Message() with
    {
        Direction = "outgoing", FromAddress = "casey@company.example", FromName = "Casey Lee",
        ToAddresses = ["quotes@vendor.example"], ReceivedAt = null
    };
}

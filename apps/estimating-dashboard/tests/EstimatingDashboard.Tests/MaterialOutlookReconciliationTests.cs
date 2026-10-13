using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace EstimatingDashboard.Tests;

public sealed partial class MaterialOutlookTrackingTests
{
    [Fact]
    public async Task Confirmed_manual_eml_is_enriched_by_actual_sent_scan_and_pending_secondary_reply_is_reconciled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = await ImportConfirmedEditedEmlAsync(fixture);
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Reply())).Outcome);
        var request = await fixture.RequestAsync();
        request.Status = VendorQuoteStatuses.QuoteReceived;
        request.StatusChangedAt = sent.SentAt.AddMinutes(5);
        await fixture.Db.SaveChangesAsync();

        var duplicate = await fixture.ImportAsync(sent);

        Assert.Equal("duplicate", duplicate.Outcome);
        var outgoing = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync(row => row.Direction == "outgoing");
        Assert.Equal(sent.ConversationId, outgoing.ConversationId);
        Assert.Equal(2, await fixture.Db.Set<VendorQuoteMessage>().CountAsync());
        Assert.Equal(request.Id, (await fixture.Db.Set<VendorQuoteMessage>().SingleAsync(row => row.Direction == "incoming")).RequestId);
        Assert.Equal(VendorQuoteStatuses.QuoteReceived, request.Status);
        Assert.Single(await fixture.Db.Set<VendorQuoteActivity>().Where(row => row.Kind == "email-rate-request").ToListAsync());
        Assert.Equal("duplicate", (await fixture.ImportAsync(sent with { VendorEmail = "mark@example.test" })).Outcome);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("sender")]
    [InlineData("recipients")]
    [InlineData("token")]
    [InlineData("removed")]
    [InlineData("moved")]
    [InlineData("generation")]
    public async Task Unverified_or_handled_duplicate_cannot_establish_a_material_conversation(string condition)
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = await ImportConfirmedEditedEmlAsync(fixture);
        var outgoing = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync();
        if (condition == "removed") outgoing.RemovedAt = VendorQuoteTestFixture.Now;
        if (condition == "moved") outgoing.MovedAt = VendorQuoteTestFixture.Now;
        if (condition == "generation")
        {
            fixture.Reader.Material = fixture.Reader.Material with { QuoteQuantities = [20, 30] };
            await fixture.DraftAsync();
        }
        await fixture.Db.SaveChangesAsync();
        sent = condition switch
        {
            "date" => sent with { SentAt = sent.SentAt.AddSeconds(1) },
            "sender" => sent with { FromAddress = "other@company.example" },
            "recipients" => sent with { ToAddresses = ["cheri@example.test"] },
            "token" => sent with { MaterialDraftToken = new string('a', 32) },
            _ => sent
        };
        Assert.Equal("duplicate", (await fixture.ImportAsync(sent)).Outcome);
        Assert.Null(outgoing.ConversationId);
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Reply())).Outcome);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("moved")]
    [InlineData("assigned")]
    [InlineData("manual")]
    [InlineData("different-evidence")]
    public async Task Confirming_material_send_preserves_contact_copies_with_user_actions_or_different_evidence(string condition)
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = fixture.Outgoing(await fixture.DraftAsync()) with { MaterialDraftToken = null, BodyText = "Edited request." };
        await fixture.ImportAsync(sent);
        await fixture.ImportAsync(sent with { VendorEmail = "mark@example.test" });
        var copies = await fixture.Db.Set<VendorQuoteMessage>().ToListAsync();
        var secondary = copies.Single(row => row.VendorEmail == "mark@example.test");
        var request = await fixture.RequestAsync();
        if (condition == "removed") secondary.RemovedAt = VendorQuoteTestFixture.Now;
        if (condition == "moved") secondary.MovedAt = VendorQuoteTestFixture.Now;
        if (condition == "assigned") secondary.RequestId = request.Id;
        if (condition == "manual") secondary.Mailbox = "";
        if (condition == "different-evidence") secondary.BodyText = "A different body.";
        await fixture.Db.SaveChangesAsync();
        var primary = copies.Single(row => row.VendorEmail == "cheri@example.test");
        var assigned = await fixture.Quotes.AssignMessageAsync(fixture.Id, primary.Id, new(0, request.Id), VendorQuoteTestFixture.Editor, default);
        await fixture.Quotes.SetEmailRateRequestAsync(fixture.Id, primary.Id, new(assigned.Quote.Version, true), VendorQuoteTestFixture.Editor, default);
        Assert.Equal(2, await fixture.Db.Set<VendorQuoteMessage>().CountAsync());
    }

    [Fact]
    public async Task Outlook_second_precision_send_advances_a_request_created_in_the_same_second()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        var request = await fixture.RequestAsync();
        request.StatusChangedAt = VendorQuoteTestFixture.Now.AddMilliseconds(500);
        await fixture.Db.SaveChangesAsync();
        var sent = fixture.Outgoing(draft) with { SentAt = VendorQuoteTestFixture.Now };
        Assert.Equal("imported", (await fixture.ImportAsync(sent)).Outcome);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, request.Status);
    }

    private static async Task<ImportVendorQuoteMessageDto> ImportConfirmedEditedEmlAsync(Fixture fixture)
    {
        var draft = await fixture.DraftAsync();
        var sent = fixture.Outgoing(draft) with { MaterialDraftToken = null, BodyText = "Please quote this edited request." };
        using var input = new MemoryStream(draft.Content);
        using var message = MimeMessage.Load(input);
        message.From.Add(MailboxAddress.Parse(sent.FromAddress));
        message.Date = sent.SentAt;
        message.MessageId = "manual-sent@company.example";
        message.Headers.Remove(MaterialDraftTracking.Header);
        message.Headers.Remove("X-Unsent");
        message.Body = new TextPart("plain") { Text = sent.BodyText };
        using var output = new MemoryStream();
        message.WriteTo(output);
        await fixture.Vendors.ImportEmailAsync(fixture.Id, "sent.eml", output.ToArray(),
            new(0, (await fixture.RequestAsync()).Id, "outgoing", IsRateRequest: true), VendorQuoteTestFixture.Editor, default);
        var stored = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync();
        Assert.True(stored.IsRateRequest);
        Assert.Null(stored.ConversationId);
        Assert.NotNull(MaterialDraftTracking.Read(await fixture.Db.Set<MaterialPurchasingState>().SingleAsync()).Single().SentAt);
        return sent with { SourceMessageId = stored.SourceMessageId };
    }
}

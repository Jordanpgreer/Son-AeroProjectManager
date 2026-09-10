using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using EstimatingDashboard.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class ManualQuoteEmailTests
{
    [Fact]
    public async Task Preview_preserves_wrong_subject_and_writes_nothing()
    {
        await using var f = await CreateAsync();
        var result = await f.Vendors.PreviewEmailAsync(f.QuoteId(), "email.eml", ManualEmailFixtures.Eml(), Editor, default);
        Assert.Equal("Wrong customer subject", result.Subject); Assert.Equal("quotes@vendor.example", result.FromAddress);
        Assert.Equal(Now, result.SentAt); Assert.Equal(1, result.AttachmentCount);
        Assert.Empty(f.Db.Set<VendorQuoteMessage>()); Assert.Empty(f.Db.Set<VendorQuoteRequest>());
        Assert.Equal(0, f.Db.QuoteHistory.First().Version);
    }
    [Fact]
    public async Task Manual_import_keeps_original_subject_status_and_scope_and_idempotent_note()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync("PART-A", status: "Rates requested");
        var file = ManualEmailFixtures.Eml("Quote 999999");
        var options = new ManualQuoteEmailImportOptions(0, thread.Request.Id, "incoming", null, "Original email manually filed");
        var result = await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", file, options, Editor, default);
        Assert.Equal("imported", result.Outcome);
        var detail = await f.Vendors.DetailAsync(thread.Request.Id, Editor, default);
        Assert.Equal("Quote 999999", detail.Messages.Single().Subject);
        Assert.Equal("Rates requested", detail.Request.Status);
        Assert.Contains(detail.Activity, x => x.Text == "Email received from Silicone Prime (imported manually)" && x.AccountName == Editor.AccountName && x.OccurredAt == Now);
        Assert.Equal("Untouched", (await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).Quote.Status);
        var retry = await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", file, options, Editor, default);
        Assert.Equal("duplicate", retry.Outcome);
        Assert.Equal(1, (await f.Vendors.DetailAsync(thread.Request.Id, Editor, default)).Request.NoteCount);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connector_and_manual_import_share_exact_message_id_dedup(bool connectorFirst)
    {
        await using var f = await CreateAsync();
        var file = ManualEmailFixtures.Eml("Quote 4445", messageId: "shared-id@vendor.example");
        var connector = Message("<shared-id@vendor.example>", "Quote 4445");
        if (connectorFirst) await f.Vendors.ImportAsync(connector, Editor, default);
        else await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", file, new(0, null, "incoming"), Editor, default);
        var result = connectorFirst ? await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", file, new(0, null, "incoming"), Editor, default)
            : await f.Vendors.ImportAsync(connector, Editor, default);
        Assert.Equal("duplicate", result.Outcome);
        Assert.Single(await f.Db.Set<VendorQuoteMessage>().ToListAsync());
    }
    [Fact]
    public async Task Manual_outgoing_unassigned_names_chosen_vendor_and_requires_choice_for_multiple_recipients()
    {
        await using var f = await CreateAsync();
        var file = ManualEmailFixtures.Eml(outgoing: true);
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportEmailAsync(f.QuoteId(), "sent.eml", file, new(0, null, "outgoing"), Editor, default))).StatusCode);
        var result = await f.Vendors.ImportEmailAsync(f.QuoteId(), "sent.eml", file, new(0, null, "outgoing", "other@vendor.example"), Editor, default);
        Assert.Equal("unassigned", result.Outcome);
        var detail = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        Assert.Equal("outgoing", detail.UnassignedMessages.Single().Direction);
        Assert.Contains(detail.Activity, x => x.Text.StartsWith("Email sent to other@vendor.example (imported manually)"));
        Assert.Empty(detail.Threads);
    }
    [Fact]
    public async Task Automatic_outgoing_ambiguous_activity_is_not_labeled_received()
    {
        await using var f = await CreateAsync(); await f.ThreadAsync("A"); await f.ThreadAsync("B");
        await f.Vendors.ImportAsync(Message() with { Direction = "outgoing", FromAddress = "casey@company.example", ToAddresses = ["quotes@vendor.example"] }, Editor, default);
        Assert.Contains((await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).Activity, x => x.Text.StartsWith("Email sent to"));
    }
    [Fact]
    public async Task Html_only_is_imported_as_plain_text_without_scripts_or_remote_images()
    {
        await using var f = await CreateAsync();
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", ManualEmailFixtures.Eml(html: true), new(0, null, "incoming"), Editor, default);
        var text = (await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).UnassignedMessages.Single().BodyText;
        Assert.Contains("Vendor pricing & details", text); Assert.DoesNotContain("secretScript", text); Assert.DoesNotContain("never-fetch", text); Assert.DoesNotContain("<", text);
    }
    [Theory]
    [InlineData("invalid.msg", "garbage")]
    [InlineData("email.txt", "plain")]
    [InlineData("protected.eml", "From: Vendor <quotes@vendor.example>\r\nTo: casey@company.example\r\nDate: Thu, 10 Sep 2026 16:00:00 +0000\r\nContent-Type: application/pkcs7-mime\r\n\r\nencrypted")]
    public async Task Invalid_or_protected_files_are_rejected_without_storage(string name, string body)
    {
        await using var f = await CreateAsync();
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.PreviewEmailAsync(f.QuoteId(), name, Encoding.UTF8.GetBytes(body), Editor, default))).StatusCode);
        Assert.Empty(f.Db.Set<VendorQuoteMessage>());
    }
    [Fact]
    public async Task Wrong_quote_thread_correspondent_stale_version_and_preview_access_are_denied()
    {
        await using var f = await CreateAsync(); var thread = await f.ThreadAsync(email: "different@vendor.example");
        var bytes = ManualEmailFixtures.Eml();
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", bytes, new(0, thread.Request.Id, "incoming"), Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.PreviewEmailAsync(f.QuoteId(44450), "email.eml", bytes, Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.PreviewEmailAsync(f.QuoteId(), "email.eml", bytes, Editor with { IsPreview = true }, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", bytes, new(3, null, "incoming"), Editor, default))).StatusCode);
        Assert.Empty(f.Db.Set<VendorQuoteMessage>());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_synthetic_msg_extracts_smtp_sender_recipients_dates_body_and_attachments(bool exchange)
    {
        await using var f = await CreateAsync(); var file = ManualEmailFixtures.Msg(exchange: exchange);
        var preview = await f.Vendors.PreviewEmailAsync(f.QuoteId(), "vendor.msg", file, Editor, default);
        Assert.Equal("Incorrect Outlook subject", preview.Subject); Assert.Equal("quotes@vendor.example", preview.FromAddress);
        Assert.Equal("casey@company.example", preview.ToAddresses.Single()); Assert.Equal(Now, preview.SentAt); Assert.Equal(1, preview.AttachmentCount);
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "vendor.msg", file, new(0, null, "incoming"), Editor, default);
        var message = (await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).UnassignedMessages.Single();
        Assert.Contains("Synthetic MSG rates", message.BodyText); Assert.Equal("rates.txt", message.Attachments.Single().FileName);
        Assert.Equal("<msg-test@vendor.example>", f.Db.Set<VendorQuoteMessage>().Single().SourceMessageId);
    }
    [Fact]
    public async Task Non_email_msg_is_rejected()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.PreviewEmailAsync(f.QuoteId(), "appointment.msg", ManualEmailFixtures.Msg(messageClass: "IPM.Appointment"), Editor, default));
    }
    [Fact]
    public async Task Generic_internal_thread_activity_names_real_correspondent_instead_of_thread_label()
    {
        await using var f = await CreateAsync();
        var thread = await f.Vendors.CreateAsync(new(f.QuoteId(), "Engineering review", "", "Internal review"), Editor, default);
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", ManualEmailFixtures.Eml(), new(0, thread.Request.Id, "incoming"), Editor, default);
        var detail = await f.Vendors.DetailAsync(thread.Request.Id, Editor, default);
        Assert.Contains(detail.Activity, x => x.Text == "Email received from Silicone Prime (imported manually)");
        Assert.DoesNotContain(detail.Activity, x => x.Text.Contains("received from Engineering review"));
    }
    [Fact]
    public async Task Multipart_request_requires_custom_header_before_reading_body()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=synthetic";
        var error = await Assert.ThrowsAsync<VendorQuoteException>(() => ManualQuoteEmailEndpoints.Upload(context, default));
        Assert.Equal(403, error.StatusCode);
        context.Request.Headers["X-Arda-Request"] = "incorrect";
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => ManualQuoteEmailEndpoints.Upload(context, default))).StatusCode);
    }
    [Theory]
    [InlineData("text/rtf", "{\\rtf1 unsupported}")]
    [InlineData("multipart/mixed; boundary=a", "--a\r\nContent-Type: text/plain\r\n\r\nClear preview\r\n--a\r\nContent-Type: application/pkcs7-mime\r\n\r\nProtected part\r\n--a--")]
    public async Task Unsupported_or_nested_protected_content_is_not_silently_discarded(string type, string body)
    {
        await using var f = await CreateAsync();
        var source = $"From: quotes@vendor.example\r\nTo: casey@company.example\r\nDate: Thu, 10 Sep 2026 16:00:00 +0000\r\nContent-Type: {type}\r\n\r\n{body}";
        await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.PreviewEmailAsync(f.QuoteId(), "email.eml", Encoding.UTF8.GetBytes(source), Editor, default));
    }
    [Fact]
    public async Task Missing_message_id_uses_stable_file_hash_and_msg_identity_matches_connector()
    {
        await using var f = await CreateAsync();
        var file = ManualEmailFixtures.Eml(messageId: null);
        // MimeKit writes a generated Message-ID unless it is removed from the serialized fixture.
        var text = System.Text.RegularExpressions.Regex.Replace(Encoding.UTF8.GetString(file), @"(?im)^Message-Id:.*\r?\n", "");
        file = Encoding.UTF8.GetBytes(text);
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.eml", file, new(0, null, "incoming"), Editor, default);
        Assert.StartsWith("upload:", f.Db.Set<VendorQuoteMessage>().Single().SourceMessageId);
        Assert.Equal("duplicate", (await f.Vendors.ImportEmailAsync(f.QuoteId(), "renamed.eml", file, new(0, null, "incoming"), Editor, default)).Outcome);
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "email.msg", ManualEmailFixtures.Msg(), new(1, null, "incoming"), Editor, default);
        Assert.Equal("duplicate", (await f.Vendors.ImportAsync(Message("<msg-test@vendor.example>"), Editor, default)).Outcome);
    }
}

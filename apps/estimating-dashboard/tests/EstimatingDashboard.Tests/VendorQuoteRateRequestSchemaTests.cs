using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class VendorQuoteRateRequestSchemaTests
{
    [Fact]
    public async Task Existing_messages_gain_an_untagged_default_without_losing_original_content()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync("PART-A", status: "Waiting on vendor");
        await f.Vendors.ImportAsync(Message("legacy-rate-request") with
        {
            Direction = "outgoing", FromAddress = "casey@company.example", FromName = "Casey Lee",
            ToAddresses = ["quotes@vendor.example"], ReceivedAt = null,
            BodyText = "Please provide your rates for PART-A.",
            Attachments = [new("drawing.txt", "text/plain", "ZHJhd2luZw==")]
        }, Editor, default);
        var original = await f.Db.Set<VendorQuoteMessage>().AsNoTracking().SingleAsync();
        var attachmentId = await f.Db.Set<VendorQuoteAttachment>().Select(x => x.Id).SingleAsync();
        await f.Db.Database.ExecuteSqlRawAsync("ALTER TABLE EstimatingVendorMessages DROP COLUMN IsRateRequest;");
        f.Db.ChangeTracker.Clear();

        await new VendorQuoteSchemaInitializer(f.Db).InitializeAsync();

        var upgraded = await f.Db.Set<VendorQuoteMessage>().AsNoTracking().SingleAsync();
        Assert.False(upgraded.IsRateRequest);
        Assert.Equal(original.Id, upgraded.Id);
        Assert.Equal(original.RequestId, upgraded.RequestId);
        Assert.Equal(original.QuoteHistoryId, upgraded.QuoteHistoryId);
        Assert.Equal(original.SourceMessageId, upgraded.SourceMessageId);
        Assert.Equal(original.DeduplicationKey, upgraded.DeduplicationKey);
        Assert.Equal(original.Subject, upgraded.Subject);
        Assert.Equal(original.BodyText, upgraded.BodyText);
        Assert.Equal(original.SentAt, upgraded.SentAt);
        Assert.Equal(original.ImportedAt, upgraded.ImportedAt);
        Assert.Equal("drawing"u8.ToArray(), (await f.Vendors.AttachmentAsync(attachmentId, Editor, default)).Content);
        var detail = await f.Vendors.DetailAsync(thread.Request.Id, Editor, default);
        Assert.Equal("Waiting on vendor", detail.Request.Status);
        Assert.False(Assert.Single(detail.Messages).IsRateRequest);
    }

    [Fact]
    public async Task Repeated_upgrade_preserves_a_saved_rate_request_tag_and_other_untagged_messages()
    {
        await using var f = await CreateAsync();
        await f.Vendors.ImportAsync(Message("legacy-first") with
        {
            Direction = "outgoing", FromAddress = "casey@company.example", FromName = "Casey Lee",
            ToAddresses = ["quotes@vendor.example"], ReceivedAt = null
        }, Editor, default);
        await f.Vendors.ImportAsync(Message("legacy-second"), Editor, default);
        await f.Db.Database.ExecuteSqlRawAsync("ALTER TABLE EstimatingVendorMessages DROP COLUMN IsRateRequest;");
        f.Db.ChangeTracker.Clear();
        var initializer = new VendorQuoteSchemaInitializer(f.Db);
        await initializer.InitializeAsync();
        var tagged = await f.Db.Set<VendorQuoteMessage>().SingleAsync(x => x.SourceMessageId == "legacy-first");
        tagged.IsRateRequest = true;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        var messages = await f.Db.Set<VendorQuoteMessage>().AsNoTracking().ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.True(messages.Single(x => x.SourceMessageId == "legacy-first").IsRateRequest);
        Assert.False(messages.Single(x => x.SourceMessageId == "legacy-second").IsRateRequest);
    }
}

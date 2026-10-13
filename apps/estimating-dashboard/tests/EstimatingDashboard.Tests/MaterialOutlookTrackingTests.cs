using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace EstimatingDashboard.Tests;

public sealed partial class MaterialOutlookTrackingTests
{
    [Fact]
    public async Task Verified_sent_draft_tracks_all_contacts_as_one_rfq_and_one_message()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        var sent = fixture.Outgoing(draft);
        var first = await fixture.ImportAsync(sent);
        var second = await fixture.ImportAsync(sent with { VendorEmail = "mark@example.test" });
        Assert.Equal("imported", first.Outcome);
        Assert.Equal("duplicate", second.Outcome);
        var message = Assert.Single(await fixture.Db.Set<VendorQuoteMessage>().ToListAsync());
        Assert.True(message.IsRateRequest);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, (await fixture.RequestAsync()).Status);
        Assert.Equal(2, System.Text.Json.JsonSerializer.Deserialize<string[]>(message.ToAddressesJson)!.Length);
        Assert.Single(await fixture.Db.Set<VendorQuoteActivity>().Where(x => x.Kind == "email-rate-request").ToListAsync());
    }

    [Fact]
    public async Task Secondary_contact_reply_matches_verified_sent_conversation_without_marking_quote_received()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        var sent = await fixture.ImportAsync(fixture.Outgoing(draft));
        var reply = await fixture.ImportAsync(fixture.Reply());
        Assert.Equal(sent.RequestId, reply.RequestId);
        Assert.Equal("imported", reply.Outcome);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, (await fixture.RequestAsync()).Status);
        Assert.False((await fixture.Db.Set<VendorQuoteMessage>().SingleAsync(x => x.Direction == "incoming")).IsRateRequest);
        Assert.Single(await fixture.Db.Set<VendorQuoteRequest>().ToListAsync());
    }

    [Fact]
    public async Task Sent_followup_fanout_has_one_message_without_changing_status_or_tagging_rates_requested()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        await fixture.ImportAsync(fixture.Outgoing(draft));
        var request = await fixture.RequestAsync();
        request.Status = VendorQuoteStatuses.QuoteReceived;
        await fixture.Db.SaveChangesAsync();
        var followup = fixture.Outgoing(draft) with { SourceMessageId = "followup", Subject = "RE: Quote 4445 - RM004420",
            BodyText = "Thanks for the pricing; can you confirm the lead time?", MaterialDraftToken = null,
            SentAt = VendorQuoteTestFixture.Now.AddMinutes(3) };
        Assert.Equal("imported", (await fixture.ImportAsync(followup)).Outcome);
        Assert.Equal("duplicate", (await fixture.ImportAsync(followup with { VendorEmail = "mark@example.test" })).Outcome);
        var message = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync(x => x.SourceMessageId == "followup");
        Assert.False(message.IsRateRequest);
        Assert.Equal(request.Id, message.RequestId);
        Assert.Equal(VendorQuoteStatuses.QuoteReceived, request.Status);
        Assert.Equal(2, await fixture.Db.Set<VendorQuoteMessage>().CountAsync());
    }

    [Fact]
    public async Task Inbox_scanned_before_sent_items_is_reconciled_only_after_sent_evidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Reply())).Outcome);
        Assert.Equal(VendorQuoteStatuses.Untouched, (await fixture.RequestAsync()).Status);
        await fixture.ImportAsync(fixture.Outgoing(draft));
        Assert.All(await fixture.Db.Set<VendorQuoteMessage>().ToListAsync(), message => Assert.NotNull(message.RequestId));
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, (await fixture.RequestAsync()).Status);
    }

    [Fact]
    public async Task Delayed_reconciliation_preserves_a_wrong_token_rejection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        var reply = fixture.Reply() with { MaterialDraftToken = new string('a', 32), BodyText = "Pricing follows.\n" + draft.PlainText };
        Assert.Equal("unassigned", (await fixture.ImportAsync(reply)).Outcome);
        await fixture.ImportAsync(fixture.Outgoing(draft));
        var stored = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync(message => message.Direction == "incoming");
        Assert.Null(stored.RequestId);
        Assert.Equal(reply.MaterialDraftToken, stored.MaterialDraftToken);
    }

    [Fact]
    public async Task Tracking_column_upgrade_preserves_legacy_messages_and_is_idempotent()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await fixture.Vendors.ImportAsync(VendorQuoteTestFixture.Message(), VendorQuoteTestFixture.Editor, default);
        fixture.Db.ChangeTracker.Clear();
        await fixture.Db.Database.ExecuteSqlRawAsync("ALTER TABLE [EstimatingVendorMessages] DROP COLUMN [MaterialDraftToken];");
        await MaterialDraftTokenSchema.InitializeAsync(fixture.Db, default);
        await MaterialDraftTokenSchema.InitializeAsync(fixture.Db, default);
        var message = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync();
        Assert.Equal("Pricing attached.", message.BodyText);
        Assert.Null(message.MaterialDraftToken);
        Assert.Contains("nvarchar(32) NULL", MaterialDraftTokenSchema.SqlServer);
        message.MaterialDraftToken = new string('a', 32);
        await fixture.Db.SaveChangesAsync();
        await MaterialDraftTokenSchema.InitializeAsync(fixture.Db, default);
        await fixture.Db.Entry(message).ReloadAsync();
        Assert.Equal(new string('a', 32), message.MaterialDraftToken);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("secondary")]
    public async Task Subject_only_mail_without_a_sent_draft_does_not_match_material_request(string contact)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DraftAsync();
        var incoming = fixture.Reply();
        if (contact == "primary") incoming = incoming with { VendorEmail = "cheri@example.test", FromAddress = "cheri@example.test" };
        Assert.Equal("unassigned", (await fixture.ImportAsync(incoming)).Outcome);
        Assert.Equal(VendorQuoteStatuses.Untouched, (await fixture.RequestAsync()).Status);
    }

    [Fact]
    public async Task Stripped_header_fallback_accepts_original_template_with_signature_and_all_recipients()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        var sent = fixture.Outgoing(draft) with { MaterialDraftToken = null, BodyText = draft.PlainText + "\r\n\r\nCasey\r\nSon-Aero" };
        Assert.Equal("imported", (await fixture.ImportAsync(sent)).Outcome);
        Assert.True((await fixture.Db.Set<VendorQuoteMessage>().SingleAsync()).IsRateRequest);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, (await fixture.RequestAsync()).Status);
    }

    [Theory]
    [InlineData("changed-body")]
    [InlineData("missing-recipient")]
    [InlineData("before-prepared")]
    [InlineData("wrong-token")]
    public async Task Incomplete_or_stale_sent_evidence_never_advances_material_request(string condition)
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        var sent = fixture.Outgoing(draft) with { MaterialDraftToken = null };
        sent = condition switch {
            "changed-body" => sent with { BodyText = "Unrelated email about this quote." },
            "missing-recipient" => sent with { ToAddresses = ["cheri@example.test"] },
            "before-prepared" => sent with { SentAt = VendorQuoteTestFixture.Now.AddDays(-1) },
            _ => sent with { MaterialDraftToken = new string('a', 32) }
        };
        Assert.Equal("unassigned", (await fixture.ImportAsync(sent)).Outcome);
        Assert.Equal(VendorQuoteStatuses.Untouched, (await fixture.RequestAsync()).Status);
        Assert.False((await fixture.Db.Set<VendorQuoteMessage>().SingleAsync()).IsRateRequest);
    }

    [Fact]
    public async Task New_generation_rejects_prior_draft_even_if_old_email_is_sent_later()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.DraftAsync();
        fixture.Reader.Material = fixture.Reader.Material with { RequiredMinimum = 20, RequiredMaximum = 20, QuoteQuantities = [20, 30] };
        var current = await fixture.DraftAsync();
        Assert.NotEqual(Token(old), Token(current));
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Outgoing(old))).Outcome);
        Assert.Equal(VendorQuoteStatuses.Untouched, (await fixture.RequestAsync()).Status);
        Assert.Equal("imported", (await fixture.ImportAsync(fixture.Outgoing(current) with { SourceMessageId = "current-send" })).Outcome);
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, (await fixture.RequestAsync()).Status);
    }

    [Fact]
    public async Task Read_of_changed_bom_invalidates_old_draft_before_another_is_prepared()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.DraftAsync();
        fixture.Reader.Material = fixture.Reader.Material with { RequiredMinimum = 20, RequiredMaximum = 20, QuoteQuantities = [20, 30] };
        await fixture.Service.ReadAsync(fixture.Id, VendorQuoteTestFixture.Editor, default);
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Outgoing(old))).Outcome);
        Assert.Equal(VendorQuoteStatuses.Untouched, (await fixture.RequestAsync()).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_or_read_only_inspection_never_mutates_shared_tracking_state(bool preview)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DraftAsync();
        var state = await fixture.Db.Set<MaterialPurchasingState>().SingleAsync();
        var beforeJson = state.DraftsJson;
        var beforeVersion = state.Version;
        var beforeUpdated = state.UpdatedAt;
        fixture.Reader.Material = fixture.Reader.Material with { RequiredMinimum = 20, RequiredMaximum = 20, QuoteQuantities = [20, 30] };
        var access = preview ? VendorQuoteTestFixture.Editor with { IsPreview = true }
            : VendorQuoteTestFixture.Editor with { Role = EstimatingDashboard.Api.Auth.EstimatingRoles.Viewer };
        var review = await fixture.Service.ReadAsync(fixture.Id, access, default);
        Assert.Null(review.Materials[0].RequestId);
        Assert.Equal(beforeJson, state.DraftsJson);
        Assert.Equal(beforeVersion, state.Version);
        Assert.Equal(beforeUpdated, state.UpdatedAt);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Quantity_configuration_change_invalidates_prepared_draft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.DraftAsync();
        var state = await fixture.Db.Set<MaterialPurchasingState>().SingleAsync();
        await fixture.Service.SaveQuantitiesAsync(fixture.Id, new(state.Version, [new("top", 2000)]), VendorQuoteTestFixture.Editor, default);
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Outgoing(old))).Outcome);
        Assert.Equal(VendorQuoteStatuses.Untouched, (await fixture.RequestAsync()).Status);
    }

    [Fact]
    public async Task Reopening_unchanged_draft_preserves_generation_and_verified_sent_state()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.DraftAsync();
        await fixture.ImportAsync(fixture.Outgoing(first));
        var second = await fixture.DraftAsync();
        Assert.Equal(Token(first), Token(second));
        Assert.Equal(VendorQuoteStatuses.WaitingOnVendor, (await fixture.RequestAsync()).Status);
        var reply = await fixture.ImportAsync(fixture.Reply());
        Assert.NotNull(reply.RequestId);
    }

    [Fact]
    public async Task Multiple_verified_sends_preserve_reply_matching_for_earlier_conversations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = await fixture.DraftAsync();
        await fixture.ImportAsync(fixture.Outgoing(draft));
        await fixture.ImportAsync(fixture.Outgoing(draft) with { SourceMessageId = "second-send", ConversationId = "second-conversation",
            SentAt = VendorQuoteTestFixture.Now.AddMinutes(3) });
        var reply = await fixture.ImportAsync(fixture.Reply() with { SentAt = VendorQuoteTestFixture.Now.AddMinutes(4) });
        Assert.Equal("imported", reply.Outcome);
        Assert.NotNull(reply.RequestId);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("moved")]
    public async Task Secondary_contact_fanout_never_resurrects_removed_or_moved_messages(string handled)
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = fixture.Outgoing(await fixture.DraftAsync());
        await fixture.ImportAsync(sent);
        var message = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync();
        if (handled == "removed") message.RemovedAt = VendorQuoteTestFixture.Now;
        else message.MovedAt = VendorQuoteTestFixture.Now;
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("duplicate", (await fixture.ImportAsync(sent with { VendorEmail = "mark@example.test", SourceMessageId = " " + sent.SourceMessageId + " " })).Outcome);
        Assert.Single(await fixture.Db.Set<VendorQuoteMessage>().ToListAsync());
    }

    [Fact]
    public async Task Historical_sent_mail_does_not_reset_newer_quote_received_status()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = fixture.Outgoing(await fixture.DraftAsync());
        var request = await fixture.RequestAsync();
        request.Status = VendorQuoteStatuses.QuoteReceived;
        request.StatusChangedAt = sent.SentAt.AddMinutes(1);
        await fixture.Db.SaveChangesAsync();
        await fixture.ImportAsync(sent);
        Assert.Equal(VendorQuoteStatuses.QuoteReceived, request.Status);
        Assert.True((await fixture.Db.Set<VendorQuoteMessage>().SingleAsync()).IsRateRequest);
    }

    [Fact]
    public async Task Explicit_rates_requested_after_an_edited_send_enables_secondary_replies_and_deduplication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = fixture.Outgoing(await fixture.DraftAsync()) with { MaterialDraftToken = null, BodyText = "Please quote the revised request attached." };
        Assert.Equal("unassigned", (await fixture.ImportAsync(sent)).Outcome);
        Assert.Equal("unassigned", (await fixture.ImportAsync(sent with { VendorEmail = "mark@example.test" })).Outcome);
        var message = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync(row => row.VendorEmail == "cheri@example.test");
        var request = await fixture.RequestAsync();
        var assigned = await fixture.Quotes.AssignMessageAsync(fixture.Id, message.Id, new(0, request.Id), VendorQuoteTestFixture.Editor, default);
        await fixture.Quotes.SetEmailRateRequestAsync(fixture.Id, message.Id,
            new(assigned.Quote.Version, true), VendorQuoteTestFixture.Editor, default);
        Assert.Equal("imported", (await fixture.ImportAsync(fixture.Reply())).Outcome);
        Assert.Equal("duplicate", (await fixture.ImportAsync(sent with { VendorEmail = "mark@example.test" })).Outcome);
        Assert.Equal(2, await fixture.Db.Set<VendorQuoteMessage>().CountAsync());
    }

    [Fact]
    public async Task Explicit_confirmation_of_an_obsolete_token_never_establishes_current_reply_tracking()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sent = fixture.Outgoing(await fixture.DraftAsync()) with { MaterialDraftToken = new string('a', 32) };
        await fixture.ImportAsync(sent);
        var message = await fixture.Db.Set<VendorQuoteMessage>().SingleAsync();
        var assigned = await fixture.Quotes.AssignMessageAsync(fixture.Id, message.Id, new(0, (await fixture.RequestAsync()).Id), VendorQuoteTestFixture.Editor, default);
        await fixture.Quotes.SetEmailRateRequestAsync(fixture.Id, message.Id,
            new(assigned.Quote.Version, true), VendorQuoteTestFixture.Editor, default);
        Assert.Equal("unassigned", (await fixture.ImportAsync(fixture.Reply())).Outcome);
        Assert.Null(MaterialDraftTracking.Read(await fixture.Db.Set<MaterialPurchasingState>().SingleAsync()).Single().SentAt);
    }

    private static string Token(MaterialRfqDraftFile draft)
    {
        using var stream = new MemoryStream(draft.Content);
        using var message = MimeMessage.Load(stream);
        return message.Headers[MaterialDraftTracking.Header] ?? throw new InvalidOperationException("Draft tracking header is missing.");
    }

    private sealed class Fixture(VendorQuoteTestFixture fixture) : IAsyncDisposable
    {
        public EstimatingDashboard.Api.Data.EstimatingAccessDbContext Db => fixture.Db;
        public QuoteStatusService Quotes => fixture.Quotes;
        public VendorQuoteService Vendors => fixture.Vendors;
        public int Id => fixture.QuoteId();
        public Reader Reader { get; } = new();
        public QuoteMaterialPurchasingService Service { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var backing = await VendorQuoteTestFixture.CreateAsync();
            var result = new Fixture(backing);
            result.Service = new(result.Db, backing.Vendors, result.Reader, new Clock());
            return result;
        }
        public Task<MaterialRfqDraftFile> DraftAsync() => Service.DraftAsync(Id, "material-1", VendorQuoteTestFixture.Editor, default);
        public Task<VendorQuoteRequest> RequestAsync() => Db.Set<VendorQuoteRequest>().SingleAsync();
        public Task<VendorQuoteImportResultDto> ImportAsync(ImportVendorQuoteMessageDto dto) => fixture.Vendors.ImportAsync(dto, VendorQuoteTestFixture.Editor, default);
        public ImportVendorQuoteMessageDto Outgoing(MaterialRfqDraftFile draft) => new("sent-1", "casey@company.example", "outgoing",
            "Quote 4445 - RM004420", "casey@company.example", "Casey", ["cheri@example.test", "mark@example.test"],
            "cheri@example.test", "Nexeo", VendorQuoteTestFixture.Now.AddMinutes(1), null, draft.PlainText!, [], "material-conversation", Token(draft));
        public ImportVendorQuoteMessageDto Reply() => new("reply-1", "casey@company.example", "incoming",
            "RE: Quote 4445 - RM004420", "mark@example.test", "Mark", ["casey@company.example"],
            "mark@example.test", "Nexeo", VendorQuoteTestFixture.Now.AddMinutes(2), VendorQuoteTestFixture.Now.AddMinutes(2),
            "Please see our pricing.", [], "material-conversation");
        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => VendorQuoteTestFixture.Now; }
    private sealed class Reader : IFulcrumMaterialRequirementsReader
    {
        public MaterialRequirementDto Material { get; set; } = MaterialRfqDraftTests.Material;
        public Task<MaterialRequirementsDto> InspectAsync(int id, int number, string sourceId, CancellationToken ct,
            IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null) => Task.FromResult(new MaterialRequirementsDto(
                id, number, VendorQuoteTestFixture.Now, [new("line", "top", "TOP", null, [1000])], [Material], []));
    }
}

using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class QuoteItemLifecycleTests
{
    private static EstimatingAccessProfile Remover => Editor with { GrantedPermissions = [.. Editor.Permissions, EstimatingPermissions.DeleteQuotes] };
    [Fact]
    public async Task Email_remove_restore_excludes_active_counts_attachments_and_sync_but_keeps_audit_and_bytes()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync("PART-A");
        await f.Vendors.ImportAsync(Message("old") with { SentAt = Now.AddDays(-1), ReceivedAt = Now.AddDays(-1) }, Editor, default);
        await f.Vendors.ImportAsync(Message("new") with { Attachments = [new("quote.pdf", null, "dGVzdA==")] }, Editor, default);
        var original = await f.Vendors.DetailAsync(thread.Request.Id, Editor, default);
        var latest = original.Messages.Single(x => x.Attachments.Count > 0);
        var attachment = latest.Attachments.Single();
        var removed = await f.Quotes.RemoveEmailAsync(f.QuoteId(), latest.Id, new(0), Remover, default);
        Assert.True(removed.Quote.CanRemove); Assert.Equal(1, removed.Quote.MessageCount);
        Assert.Equal(Now.AddDays(-1), removed.Quote.LastMessageAt);
        Assert.Equal(Now.AddDays(-1), removed.Threads.Single().Request.LastMessageAt);
        Assert.Equal(latest.Id, removed.RemovedMessages.Single().Id);
        Assert.Contains(removed.Activity, x => x.Kind == "email-removed");
        Assert.Contains(removed.Activity, x => x.Kind == "email"); // Historical import evidence remains immutable.
        Assert.Equal(404, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.AttachmentAsync(attachment.Id, Remover, default))).StatusCode);
        Assert.Equal(4, (await f.Db.Set<VendorQuoteAttachment>().SingleAsync()).Content.Length);
        var duplicate = await f.Vendors.ImportAsync(Message("new"), Editor, default);
        Assert.Equal("duplicate", duplicate.Outcome); Assert.Null(duplicate.RequestId); Assert.Null(duplicate.QuoteNumber);
        var restored = await f.Quotes.RestoreEmailAsync(f.QuoteId(), latest.Id, new(removed.Quote.Version), Remover, default);
        Assert.Empty(restored.RemovedMessages); Assert.Equal(2, restored.Quote.MessageCount);
        Assert.Equal(Now, restored.Threads.Single().Request.LastMessageAt);
        Assert.Equal(original.Request.Status, restored.Threads.Single().Request.Status);
        Assert.Equal(4, (await f.Vendors.AttachmentAsync(attachment.Id, Remover, default)).Content.Length);
        Assert.Contains(restored.Activity, x => x.Kind == "email-restored");
    }
    [Fact]
    public async Task Manual_reimport_of_removed_email_returns_restore_message_without_notes_or_new_message()
    {
        await using var f = await CreateAsync(); var file = ManualEmailFixtures.Eml();
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "mail.eml", file, new(0, null, "incoming"), Editor, default);
        var messageId = await f.Db.Set<VendorQuoteMessage>().Select(x => x.Id).SingleAsync();
        await f.Quotes.RemoveEmailAsync(f.QuoteId(), messageId, new(1), Remover, default);
        var result = await f.Vendors.ImportEmailAsync(f.QuoteId(), "mail.eml", file, new(0, null, "incoming", null, "Do not duplicate"), Editor, default);
        Assert.Equal("duplicate", result.Outcome); Assert.Contains("Restore", result.Message);
        Assert.Single(await f.Db.Set<VendorQuoteMessage>().ToListAsync());
        Assert.DoesNotContain(await f.Db.Set<QuoteStatusActivity>().ToListAsync(), x => x.Text == "Do not duplicate");
    }
    [Fact]
    public async Task Cross_quote_move_preserves_content_and_source_history_and_suppresses_old_sync_without_destination_leak()
    {
        await using var f = await CreateAsync();
        var result = await f.Vendors.ImportAsync(Message() with { Attachments = [new("rates.txt", null, "dGVzdA==")] }, Editor, default);
        var source = await f.Vendors.DetailAsync(result.RequestId!.Value, Editor, default);
        var message = source.Messages.Single();
        var sourceQuote = f.QuoteId(); var targetQuote = f.QuoteId(44450);
        var destination = await f.Vendors.CreateAsync(new(targetQuote, "Silicone Prime", "quotes@vendor.example", "Correct quote", PartNumber: "PART-B"), Admin, default);
        var moved = await f.Quotes.MoveEmailAsync(sourceQuote, message.Id, new(0, targetQuote, 0, destination.Request.Id), Admin, default);
        Assert.Equal(0, moved.Quote.MessageCount); Assert.Null(moved.Threads.Single().Request.LastMessageAt);
        Assert.Contains(moved.Activity, x => x.Kind == "email-moved");
        Assert.Contains(moved.Activity, x => x.Kind == "email");
        var target = await f.Quotes.DetailAsync(targetQuote, Admin, default);
        Assert.Equal(1, target.Quote.MessageCount); Assert.Equal(1, target.Quote.Version);
        Assert.Contains(target.Activity, x => x.Kind == "email-moved");
        Assert.Equal(message.Subject, target.Threads.Single().Messages.Single().Subject);
        Assert.Equal(message.BodyText, target.Threads.Single().Messages.Single().BodyText);
        var attachmentId = message.Attachments.Single().Id;
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.AttachmentAsync(attachmentId, Editor, default))).StatusCode);
        Assert.Equal(4, (await f.Vendors.AttachmentAsync(attachmentId, Admin, default)).Content.Length);
        Assert.Contains(target.Activity, x => x.Kind == "email-moved" && x.NewValue!.Contains("Silicone Prime, part PART-B"));
        Assert.Equal("Untouched", target.Quote.Status);
        var retry = await f.Vendors.ImportAsync(Message(), Editor, default);
        Assert.Equal("duplicate", retry.Outcome); Assert.Null(retry.QuoteNumber); Assert.Null(retry.RequestId);
        Assert.DoesNotContain("44450", retry.Message);
        var file = ManualEmailFixtures.Eml("Quote 4445", messageId: "message-1@vendor.example");
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.ImportEmailAsync(sourceQuote, "mail.eml", file, new(1, null, "incoming"), Editor, default))).StatusCode);
    }
    [Fact]
    public async Task Same_quote_move_updates_source_and_target_thread_timestamps_without_status_transition()
    {
        await using var f = await CreateAsync(); var first = await f.ThreadAsync("A"); var second = await f.ThreadAsync("B");
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "mail.eml", ManualEmailFixtures.Eml(), new(0, first.Request.Id, "incoming"), Editor, default);
        var message = f.Db.Set<VendorQuoteMessage>().Single();
        var moved = await f.Quotes.MoveEmailAsync(f.QuoteId(), message.Id, new(1, f.QuoteId(), 1, second.Request.Id), Editor, default);
        Assert.Equal(2, moved.Quote.Version); Assert.Equal(1, moved.Quote.MessageCount);
        Assert.Null(moved.Threads.Single(x => x.Request.Id == first.Request.Id).Request.LastMessageAt);
        Assert.Equal(Now, moved.Threads.Single(x => x.Request.Id == second.Request.Id).Request.LastMessageAt);
        Assert.All(moved.Threads, x => Assert.Equal("Untouched", x.Request.Status));
        Assert.Single(moved.Activity, x => x.Kind == "email-moved");
    }
    [Fact]
    public async Task Move_checks_both_quote_permissions_versions_and_correspondent_before_any_mutation()
    {
        await using var f = await CreateAsync();
        await f.Vendors.ImportEmailAsync(f.QuoteId(), "mail.eml", ManualEmailFixtures.Eml(), new(0, null, "incoming"), Editor, default);
        var messageId = f.Db.Set<VendorQuoteMessage>().Single().Id;
        var other = await f.ThreadAsync(email: "other@vendor.example");
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.MoveEmailAsync(f.QuoteId(), messageId, new(1, f.QuoteId(44450), 0), Editor, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.MoveEmailAsync(f.QuoteId(), messageId, new(0, f.QuoteId(4451), 0), Editor, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.MoveEmailAsync(f.QuoteId(), messageId, new(1, f.QuoteId(4451), 2), Editor, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.MoveEmailAsync(f.QuoteId(), messageId, new(1, f.QuoteId(), 1, other.Request.Id), Editor, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.MoveEmailAsync(f.QuoteId(), messageId, new(1, f.QuoteId(4451), 0, other.Request.Id), Editor, default))).StatusCode);
        Assert.Null(f.Db.Set<VendorQuoteMessage>().Single().MovedAt);
        await f.Quotes.RemoveEmailAsync(f.QuoteId(), messageId, new(1), Remover, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.MoveEmailAsync(f.QuoteId(), messageId, new(2, f.QuoteId(4451), 0), Editor, default))).StatusCode);
    }
    [Fact]
    public async Task Remove_restore_permissions_are_additive_and_preview_cannot_mutate()
    {
        await using var f = await CreateAsync();
        var detail = await f.Quotes.AddNoteAsync(f.QuoteId(), new(0, "Internal note"), Editor, default);
        var noteId = detail.Activity.Single(x => x.Kind == "note").Id;
        await f.Vendors.ImportAsync(Message(), Editor, default);
        var messageId = f.Db.Set<VendorQuoteMessage>().Single().Id;
        Assert.False((await f.Quotes.DetailAsync(f.QuoteId(), Editor, default)).Quote.CanRemove);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RemoveEmailAsync(f.QuoteId(), messageId, new(1), Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RestoreEmailAsync(f.QuoteId(), messageId, new(1), Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RemoveNoteAsync(f.QuoteId(), noteId, new(1), Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RestoreNoteAsync(f.QuoteId(), noteId, new(1), Editor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.EditNoteAsync(f.QuoteId(), noteId, new(1, "no"), Remover with { IsPreview = true }, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RemoveEmailAsync(f.QuoteId(), messageId, new(1), Remover with { IsPreview = true }, default))).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Internal_notes_edit_remove_restore_preserve_original_time_and_full_audit(bool threadScope)
    {
        await using var f = await CreateAsync();
        if (threadScope) { var thread = await f.ThreadAsync(); await f.Vendors.AddNoteAsync(thread.Request.Id, new(0, new string('a', 4000)), Editor, default); }
        else await f.Quotes.AddNoteAsync(f.QuoteId(), new(0, new string('a', 4000)), Editor, default);
        var initial = await f.Quotes.DetailAsync(f.QuoteId(), Editor, default);
        var note = initial.Activity.Single(x => x.Kind == "note");
        var edited = await f.Quotes.EditNoteAsync(f.QuoteId(), note.Id, new(initial.Quote.Version, new string('b', 4000)), Editor, default);
        var active = edited.Activity.Single(x => x.Id == note.Id);
        Assert.Equal(note.OccurredAt, active.OccurredAt); Assert.Equal(Now, active.EditedAt); Assert.Equal(Editor.DisplayName, active.EditedBy);
        Assert.Contains(edited.Activity, x => x.Kind == "note-edited" && x.OldValue!.Length == 4000 && x.NewValue!.Length == 4000);
        var removed = await f.Quotes.RemoveNoteAsync(f.QuoteId(), note.Id, new(edited.Quote.Version), Remover, default);
        Assert.DoesNotContain(removed.Activity, x => x.Id == note.Id);
        Assert.Equal(note.Id, removed.RemovedNotes.Single().Id);
        Assert.Contains(removed.Activity, x => x.Kind == "note-removed");
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.EditNoteAsync(f.QuoteId(), note.Id, new(removed.Quote.Version, "no"), Editor, default))).StatusCode);
        var restored = await f.Quotes.RestoreNoteAsync(f.QuoteId(), note.Id, new(removed.Quote.Version), Remover, default);
        Assert.Empty(restored.RemovedNotes); Assert.Equal(new string('b', 4000), restored.Activity.Single(x => x.Id == note.Id).Text);
        Assert.Contains(restored.Activity, x => x.Kind == "note-restored");
        if (threadScope) Assert.Equal(1, restored.Threads.Single().Request.NoteCount);
    }
    [Fact]
    public async Task Notes_cannot_cross_quote_boundaries_or_mutate_automatic_history()
    {
        await using var f = await CreateAsync(); var thread = await f.ThreadAsync();
        var detail = await f.Quotes.AddNoteAsync(f.QuoteId(), new(0, "note"), Editor, default);
        var note = detail.Activity.Single(x => x.Kind == "note");
        Assert.Equal(404, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.EditNoteAsync(f.QuoteId(4451), note.Id, new(0, "wrong"), Editor, default))).StatusCode);
        var created = detail.Activity.Single(x => x.Kind == "created");
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RemoveNoteAsync(f.QuoteId(), created.Id, new(1), Remover, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.EditNoteAsync(f.QuoteId(), "audit-1", new(1, "wrong"), Editor, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.EditNoteAsync(f.QuoteId(), note.Id, new(0, "stale"), Editor, default))).StatusCode);
    }
    [Fact]
    public async Task Removed_message_cannot_steer_new_conversation_routing()
    {
        await using var f = await CreateAsync(); var first = await f.ThreadAsync("A"); await f.ThreadAsync("B");
        await f.Vendors.ImportAsync(Message(), Editor, default);
        var message = f.Db.Set<VendorQuoteMessage>().Single();
        await f.Quotes.AssignMessageAsync(f.QuoteId(), message.Id, new(0, first.Request.Id), Editor, default);
        await f.Quotes.RemoveEmailAsync(f.QuoteId(), message.Id, new(1), Remover, default);
        var result = await f.Vendors.ImportAsync(Message("second-in-conversation"), Editor, default);
        Assert.Equal("unassigned", result.Outcome); Assert.Null(result.RequestId);
    }
    [Fact]
    public async Task Concurrent_parent_update_rolls_back_soft_removal_and_its_audit()
    {
        await using var f = await CreateAsync(); await f.Vendors.ImportAsync(Message(), Editor, default);
        var messageId = f.Db.Set<VendorQuoteMessage>().Single().Id;
        await using var otherDb = f.AnotherContext();
        var otherVendors = new VendorQuoteService(otherDb, TimeProvider.System);
        var otherQuotes = new QuoteStatusService(otherDb, TimeProvider.System, otherVendors, new(otherDb, TimeProvider.System));
        await otherQuotes.AddNoteAsync(f.QuoteId(), new(0, "Concurrent edit"), Editor, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RemoveEmailAsync(f.QuoteId(), messageId, new(0), Remover, default))).StatusCode);
        Assert.Null((await otherDb.Set<VendorQuoteMessage>().AsNoTracking().SingleAsync()).RemovedAt);
        Assert.Empty(await otherDb.Set<QuoteStatusActivity>().Where(x => x.Kind == "email-removed").ToListAsync());
    }
    [Fact]
    public async Task Lifecycle_upgrade_preserves_populated_legacy_tables_and_is_repeatable()
    {
        await using var f = await CreateAsync();
        var thread = await f.ThreadAsync(); await f.Vendors.AddNoteAsync(thread.Request.Id, new(0, "Preserve thread note"), Editor, default);
        await f.Quotes.AddNoteAsync(f.QuoteId(), new(0, "Preserve quote note"), Editor, default);
        await f.Vendors.ImportAsync(Message(), Editor, default);
        await f.Db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE EstimatingVendorMessages DROP COLUMN RemovedAt;
            ALTER TABLE EstimatingVendorMessages DROP COLUMN RemovedBy;
            ALTER TABLE EstimatingVendorMessages DROP COLUMN MovedAt;
            ALTER TABLE EstimatingVendorActivities DROP COLUMN EditedAt;
            ALTER TABLE EstimatingVendorActivities DROP COLUMN EditedBy;
            ALTER TABLE EstimatingVendorActivities DROP COLUMN RemovedAt;
            ALTER TABLE EstimatingVendorActivities DROP COLUMN RemovedBy;
            ALTER TABLE EstimatingQuoteStatusActivities DROP COLUMN EditedAt;
            ALTER TABLE EstimatingQuoteStatusActivities DROP COLUMN EditedBy;
            ALTER TABLE EstimatingQuoteStatusActivities DROP COLUMN RemovedAt;
            ALTER TABLE EstimatingQuoteStatusActivities DROP COLUMN RemovedBy;
            """);
        await new VendorQuoteSchemaInitializer(f.Db).InitializeAsync();
        await new VendorQuoteSchemaInitializer(f.Db).InitializeAsync();
        f.Db.ChangeTracker.Clear();
        var detail = await f.Quotes.DetailAsync(f.QuoteId(), Remover, default);
        Assert.Equal(1, detail.Quote.MessageCount); Assert.Equal(2, detail.Activity.Count(x => x.Kind == "note"));
        Assert.Empty(detail.RemovedMessages); Assert.Empty(detail.RemovedNotes);
        var removed = await f.Quotes.RemoveEmailAsync(f.QuoteId(), detail.Threads.Single().Messages.Single().Id, new(detail.Quote.Version), Remover, default);
        Assert.Single(removed.RemovedMessages);
    }
    [Fact]
    public void Sql_server_upgrade_is_guarded_and_preserves_full_note_revision_text()
    {
        var sql = VendorQuoteLifecycleSchema.SqlServerSql();
        Assert.Contains("IF COL_LENGTH", sql); Assert.Contains("[RemovedAt] datetimeoffset NULL", sql);
        Assert.Contains("[EditedBy] nvarchar(160) NULL", sql);
        Assert.Contains("[EstimatingVendorActivities] ALTER COLUMN [OldValue] nvarchar(4000)", sql);
        Assert.Contains("[EstimatingQuoteStatusActivities] ALTER COLUMN [NewValue] nvarchar(4000)", sql);
        Assert.DoesNotContain("DROP ", sql); Assert.DoesNotContain("DELETE ", sql);
    }
    [Fact]
    public async Task Restoration_revalidates_current_thread_correspondent_without_changing_removed_state()
    {
        await using var f = await CreateAsync(); var thread = await f.ThreadAsync();
        await f.Vendors.ImportAsync(Message(), Editor, default);
        var messageId = f.Db.Set<VendorQuoteMessage>().Single().Id;
        var removed = await f.Quotes.RemoveEmailAsync(f.QuoteId(), messageId, new(0), Remover, default);
        var current = removed.Threads.Single().Request;
        await f.Vendors.UpdateAsync(thread.Request.Id, new(current.Version, current.VendorName, current.Title,
            current.Status, VendorEmail: "different@vendor.example"), Editor, default);
        Assert.Equal(400, (await Assert.ThrowsAsync<VendorQuoteException>(() => f.Quotes.RestoreEmailAsync(f.QuoteId(), messageId, new(removed.Quote.Version), Remover, default))).StatusCode);
        Assert.NotNull((await f.Db.Set<VendorQuoteMessage>().AsNoTracking().SingleAsync()).RemovedAt);
    }
}

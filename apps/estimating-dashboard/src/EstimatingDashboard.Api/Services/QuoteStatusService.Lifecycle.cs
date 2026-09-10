using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed partial class QuoteStatusService
{
    private static bool CanRemove(EstimatingAccessProfile access) => !access.IsPreview
        && VendorQuoteService.Has(access, EstimatingPermissions.ManageQuotes)
        && VendorQuoteService.Has(access, EstimatingPermissions.DeleteQuotes);
    private async Task<EstimatingQuoteHistoryRecord> LifecycleQuoteAsync(int id, int version,
        EstimatingAccessProfile access, bool remove, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        if (remove && !CanRemove(access)) throw new VendorQuoteException(403, "Removing or restoring items requires the Delete quotes permission.");
        VendorQuoteService.Version(quote.Version, version);
        return quote;
    }
    private async Task<VendorQuoteMessage> LifecycleEmailAsync(int id, long messageId, CancellationToken ct) =>
        await db.Set<VendorQuoteMessage>().SingleOrDefaultAsync(x => x.Id == messageId && x.QuoteHistoryId == id, ct)
        ?? throw new VendorQuoteException(404, "The email was not found on this quote.");
    private async Task<VendorQuoteRequest?> DestinationAsync(int quoteId, int? requestId, string vendorEmail, CancellationToken ct)
    {
        if (!requestId.HasValue) return null;
        var thread = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(x => x.Id == requestId && x.QuoteHistoryId == quoteId, ct)
            ?? throw new VendorQuoteException(400, "Choose a thread belonging to the selected quote.");
        if (thread.VendorEmail.Length > 0 && thread.VendorEmail != vendorEmail)
            throw new VendorQuoteException(400, "Choose a thread for the same email correspondent, or a general internal thread.");
        return thread;
    }
    public async Task<QuoteStatusDetailDto> RemoveEmailAsync(int id, long messageId, QuoteItemVersionDto dto,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await LifecycleQuoteAsync(id, dto.ExpectedVersion, access, true, ct);
        var message = await LifecycleEmailAsync(id, messageId, ct);
        if (message.RemovedAt.HasValue) throw new VendorQuoteException(409, "This email is already in Removed items.");
        message.RemovedAt = clock.GetUtcNow(); message.RemovedBy = access.DisplayName;
        Activity(quote, "email-removed", $"Email removed from Arda (#{message.Id})", access, message.Subject, null);
        await RefreshThreadsAsync([message.RequestId], message, ct);
        Touch(quote, access); await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    public async Task<QuoteStatusDetailDto> RestoreEmailAsync(int id, long messageId, QuoteItemVersionDto dto,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await LifecycleQuoteAsync(id, dto.ExpectedVersion, access, true, ct);
        var message = await LifecycleEmailAsync(id, messageId, ct);
        if (!message.RemovedAt.HasValue) throw new VendorQuoteException(409, "This email is already active.");
        await DestinationAsync(id, message.RequestId, message.VendorEmail, ct);
        message.RemovedAt = null; message.RemovedBy = null;
        Activity(quote, "email-restored", $"Email restored to Arda (#{message.Id})", access, null, message.Subject);
        await RefreshThreadsAsync([message.RequestId], message, ct);
        Touch(quote, access); await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    public async Task<QuoteStatusDetailDto> MoveEmailAsync(int id, long messageId, MoveQuoteEmailDto dto,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var source = await LifecycleQuoteAsync(id, dto.ExpectedVersion, access, false, ct);
        var target = await LifecycleQuoteAsync(dto.TargetQuoteHistoryId, dto.TargetExpectedVersion, access, false, ct);
        var message = await LifecycleEmailAsync(id, messageId, ct);
        if (message.RemovedAt.HasValue) throw new VendorQuoteException(409, "Restore this email before moving it.");
        var destination = await DestinationAsync(target.Id, dto.RequestId, message.VendorEmail, ct);
        if (source.Id == target.Id && message.RequestId == destination?.Id) return await DetailAsync(id, access, ct);
        var oldRequestId = message.RequestId;
        var originThread = oldRequestId.HasValue ? await db.Set<VendorQuoteRequest>().SingleAsync(x => x.Id == oldRequestId, ct) : null;
        var before = LocationLabel(source.QuoteNumber, originThread);
        var after = LocationLabel(target.QuoteNumber, destination);
        message.QuoteHistory = target; message.QuoteHistoryId = target.Id;
        message.Request = destination; message.RequestId = destination?.Id; message.MovedAt = clock.GetUtcNow();
        Activity(source, "email-moved", $"Email moved (#{message.Id}): {message.Subject}", access, before, after);
        if (source.Id != target.Id) Activity(target, "email-moved", $"Email moved here (#{message.Id}): {message.Subject}", access, before, after);
        await RefreshThreadsAsync([oldRequestId, destination?.Id], message, ct);
        Touch(source, access); if (target.Id != source.Id) Touch(target, access);
        await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    private static string LocationLabel(int quoteNumber, VendorQuoteRequest? thread) => $"Quote {quoteNumber}, "
        + (thread is null ? "quote correspondence" : thread.VendorName + (string.IsNullOrEmpty(thread.PartNumber) ? "" : $", part {thread.PartNumber}"));
    private async Task RefreshThreadsAsync(IEnumerable<int?> requestIds, VendorQuoteMessage changed, CancellationToken ct)
    {
        foreach (var id in requestIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct())
        {
            var thread = await db.Set<VendorQuoteRequest>().SingleAsync(x => x.Id == id, ct);
            // Include the tracked changed message: the database still has its previous location until atomic SaveChanges.
            var stored = await db.Set<VendorQuoteMessage>().Where(x => x.RequestId == id && x.RemovedAt == null).ToListAsync(ct);
            thread.LastMessageAt = stored.Append(changed).DistinctBy(x => x.Id)
                .Where(x => x.RequestId == id && x.RemovedAt == null).Select(x => (DateTimeOffset?)(x.ReceivedAt ?? x.SentAt)).DefaultIfEmpty().Max();
            thread.UpdatedAt = clock.GetUtcNow(); thread.Version++;
        }
    }
    public Task<QuoteStatusDetailDto> EditNoteAsync(int id, string activityId, EditQuoteNoteDto dto,
        EstimatingAccessProfile access, CancellationToken ct) => ChangeNoteAsync(id, activityId, dto.ExpectedVersion, "edit", dto.Text, access, ct);
    public Task<QuoteStatusDetailDto> RemoveNoteAsync(int id, string activityId, QuoteItemVersionDto dto,
        EstimatingAccessProfile access, CancellationToken ct) => ChangeNoteAsync(id, activityId, dto.ExpectedVersion, "remove", null, access, ct);
    public Task<QuoteStatusDetailDto> RestoreNoteAsync(int id, string activityId, QuoteItemVersionDto dto,
        EstimatingAccessProfile access, CancellationToken ct) => ChangeNoteAsync(id, activityId, dto.ExpectedVersion, "restore", null, access, ct);
    private async Task<QuoteStatusDetailDto> ChangeNoteAsync(int id, string activityId, int version, string action, string? text,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await LifecycleQuoteAsync(id, version, access, action != "edit", ct);
        var segments = activityId.Split('-');
        if (segments.Length != 2 || !long.TryParse(segments[1], out var noteId) || noteId < 1)
            throw new VendorQuoteException(400, "Choose an internal note to change.");
        IQuoteNoteRecord note;
        VendorQuoteRequest? thread = null;
        if (segments[0] == "quote") note = await db.Set<QuoteStatusActivity>().SingleOrDefaultAsync(x => x.Id == noteId && x.QuoteHistoryId == id, ct)
            ?? throw new VendorQuoteException(404, "The internal note was not found on this quote.");
        else if (segments[0] == "thread")
        {
            var record = await db.Set<VendorQuoteActivity>().Include(x => x.Request).SingleOrDefaultAsync(x => x.Id == noteId && x.Request.QuoteHistoryId == id, ct)
                ?? throw new VendorQuoteException(404, "The internal note was not found on this quote.");
            note = record; thread = record.Request;
        }
        else throw new VendorQuoteException(400, "Automatic activity and audit history cannot be edited or removed.");
        if (note.Kind != "note") throw new VendorQuoteException(400, "Only internal notes can be edited or removed. Audit history is permanent.");
        var prior = note.Text;
        if (action == "edit")
        {
            if (note.RemovedAt.HasValue) throw new VendorQuoteException(409, "Restore this note before editing it.");
            note.Text = VendorQuoteService.Required(text, "Note", 4000);
            if (prior == note.Text) return await DetailAsync(id, access, ct);
            note.EditedAt = clock.GetUtcNow(); note.EditedBy = access.DisplayName;
        }
        else if (action == "remove")
        {
            if (note.RemovedAt.HasValue) throw new VendorQuoteException(409, "This note is already in Removed items.");
            note.RemovedAt = clock.GetUtcNow(); note.RemovedBy = access.DisplayName;
        }
        else
        {
            if (!note.RemovedAt.HasValue) throw new VendorQuoteException(409, "This note is already active.");
            note.RemovedAt = null; note.RemovedBy = null;
        }
        var kind = action == "edit" ? "note-edited" : action == "remove" ? "note-removed" : "note-restored";
        var description = action == "edit" ? "Internal note edited" : action == "remove" ? "Internal note removed" : "Internal note restored";
        var oldValue = action == "restore" ? null : prior; var newValue = action == "remove" ? null : note.Text;
        if (thread is null) Activity(quote, kind, $"{description} ({activityId})", access, oldValue, newValue);
        else
        {
            thread.Activity.Add(new VendorQuoteActivity { Request = thread, Kind = kind, Text = $"{description} ({activityId})",
                OldValue = oldValue, NewValue = newValue, OccurredAt = clock.GetUtcNow(), AccountName = access.AccountName, DisplayName = access.DisplayName });
            thread.UpdatedAt = clock.GetUtcNow(); thread.Version++;
        }
        Touch(quote, access); await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    private async Task<IReadOnlyList<RemovedQuoteEmailDto>> RemovedEmailsAsync(int id, CancellationToken ct) =>
        (await db.Set<VendorQuoteMessage>().AsNoTracking().Where(x => x.QuoteHistoryId == id && x.RemovedAt != null)
            .Select(x => new RemovedQuoteEmailDto(x.Id, x.Subject, x.Direction, x.FromAddress, x.FromName, x.VendorEmail,
                x.SentAt, x.RemovedAt!.Value, x.RemovedBy, x.RequestId, x.Request != null ? x.Request.VendorName : null,
                x.Request != null ? x.Request.PartNumber : null)).ToListAsync(ct)).OrderByDescending(x => x.RemovedAt).ToList();
    private async Task<IReadOnlyList<QuoteStatusActivityDto>> RemovedNotesAsync(int id, CancellationToken ct)
    {
        var notes = (await db.Set<QuoteStatusActivity>().AsNoTracking().Where(x => x.QuoteHistoryId == id && x.Kind == "note" && x.RemovedAt != null).ToListAsync(ct))
            .Select(x => new QuoteStatusActivityDto($"quote-{x.Id}", x.Kind, x.Text, x.OldValue, x.NewValue, x.OccurredAt,
                x.AccountName, x.DisplayName, null, null, null, x.EditedAt, x.EditedBy, x.RemovedAt, x.RemovedBy)).ToList();
        notes.AddRange((await db.Set<VendorQuoteActivity>().AsNoTracking().Include(x => x.Request)
                .Where(x => x.Request.QuoteHistoryId == id && x.Kind == "note" && x.RemovedAt != null).ToListAsync(ct))
            .Select(x => new QuoteStatusActivityDto($"thread-{x.Id}", x.Kind, x.Text, x.OldValue, x.NewValue, x.OccurredAt,
                x.AccountName, x.DisplayName, x.RequestId, x.Request.VendorName, x.Request.PartNumber, x.EditedAt, x.EditedBy, x.RemovedAt, x.RemovedBy)));
        return notes.OrderByDescending(x => x.RemovedAt).ToList();
    }
}

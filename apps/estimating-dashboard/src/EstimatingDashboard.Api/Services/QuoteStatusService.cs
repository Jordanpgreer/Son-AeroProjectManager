using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

public sealed partial class QuoteStatusService(EstimatingAccessDbContext db, TimeProvider clock, VendorQuoteService vendors,
    EstimatingQuoteWorkflowService workflow)
{
    public async Task<QuoteStatusPageDto> ListAsync(EstimatingAccessProfile access, string? search, string? status,
        int? quoteNumber, int page, int pageSize, CancellationToken ct)
    {
        var accessible = await vendors.AccessibleQuotesAsync(access, ct);
        var searchThreadQuoteIds = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var ids = accessible.Select(x => x.Id).ToArray();
            var branches = await db.Set<VendorQuoteRequest>().AsNoTracking().Where(x => ids.Contains(x.QuoteHistoryId))
                .Select(x => new { x.QuoteHistoryId, x.Title, x.VendorName, x.VendorEmail, x.PartNumber }).ToListAsync(ct);
            searchThreadQuoteIds = branches.Where(x => $"{x.Title} {x.VendorName} {x.VendorEmail} {x.PartNumber}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(x => x.QuoteHistoryId).ToHashSet();
        }
        var rows = accessible.Where(x =>
            (!quoteNumber.HasValue || x.QuoteNumber == quoteNumber)
            && (string.IsNullOrWhiteSpace(status) || DisplayStatus(x).Equals(status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(search) || searchThreadQuoteIds.Contains(x.Id) || $"{x.QuoteNumber} {x.Customer} {x.EstimatingRep}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.QuoteNumber).ToList();
        page = Math.Clamp(page, 1, 1000000); pageSize = Math.Clamp(pageSize, 1, 100);
        var selected = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new(await SummariesAsync(selected, access, ct), rows.Count, page, pageSize);
    }
    private async Task<IReadOnlyList<QuoteStatusSummaryDto>> SummariesAsync(List<EstimatingQuoteHistoryRecord> quotes, EstimatingAccessProfile access, CancellationToken ct)
    {
        var ids = quotes.Select(x => x.Id).ToArray();
        var metadata = await db.Set<QuoteStatusMetadata>().AsNoTracking().Where(x => ids.Contains(x.QuoteHistoryId)).ToDictionaryAsync(x => x.QuoteHistoryId, ct);
        var threads = await db.Set<VendorQuoteRequest>().AsNoTracking().Where(x => ids.Contains(x.QuoteHistoryId))
            .Select(x => new { x.QuoteHistoryId, x.UpdatedAt }).ToListAsync(ct);
        var messages = await db.Set<VendorQuoteMessage>().AsNoTracking().Where(x => ids.Contains(x.QuoteHistoryId) && x.RemovedAt == null)
            .Select(x => new { x.QuoteHistoryId, x.RequestId, x.SentAt, x.ReceivedAt, x.ImportedAt }).ToListAsync(ct);
        var workflowUpdates = await db.QuoteHistoryAudits.AsNoTracking().Where(x => ids.Contains(x.QuoteHistoryId)
                && x.Action == EstimatingQuoteAuditActions.WorkflowUpdated)
            .Select(x => new { x.QuoteHistoryId, x.ChangedAt }).ToListAsync(ct);
        return quotes.Select(q =>
        {
            var threadRows = threads.Where(x => x.QuoteHistoryId == q.Id).ToList();
            var emailRows = messages.Where(x => x.QuoteHistoryId == q.Id).ToList();
            return new QuoteStatusSummaryDto(q.Id, q.QuoteNumber, q.Customer, q.EstimatingRep,
                DisplayStatus(q), q.ArdaStatusChangedAt ?? q.FirstImportedAt,
                q.ArdaStatusChangedBy is null ? null : WindowsAccountNames.DisplayName(q.ArdaStatusChangedBy),
                metadata.GetValueOrDefault(q.Id)?.FollowUpDate,
                threadRows.Select(x => x.UpdatedAt).Concat(emailRows.Select(x => x.ImportedAt))
                    .Concat(workflowUpdates.Where(x => x.QuoteHistoryId == q.Id).Select(x => x.ChangedAt)).Append(q.UpdatedAt).Max(),
                emailRows.Select(x => (DateTimeOffset?)(x.ReceivedAt ?? x.SentAt)).DefaultIfEmpty().Max(),
                threadRows.Count, emailRows.Count, emailRows.Count(x => x.RequestId is null), q.Version,
                !access.IsPreview && VendorQuoteService.Has(access, EstimatingPermissions.ManageQuotes), CanRemove(access));
        }).ToList();
    }
    public async Task<QuoteStatusDetailDto> DetailAsync(int id, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, false, ct);
        var summary = (await SummariesAsync([quote], access, ct)).Single();
        var workflowDetails = await workflow.DescribeAsync(quote, ct);
        summary = summary with { Status = workflowDetails.ArdaStatus ?? EstimatingArdaStatuses.Untouched, StatusChangedAt = workflowDetails.ArdaStatusChangedAt,
            StatusChangedBy = workflowDetails.ArdaStatusChangedBy };
        var threadIds = await db.Set<VendorQuoteRequest>().Where(x => x.QuoteHistoryId == id).Select(x => x.Id).ToListAsync(ct);
        var threads = new List<VendorQuoteDetailDto>();
        foreach (var threadId in threadIds) threads.Add(await vendors.DetailAsync(threadId, access, ct));
        var events = (await db.Set<QuoteStatusActivity>().AsNoTracking().Where(x => x.QuoteHistoryId == id && x.RemovedAt == null).ToListAsync(ct))
            .Select(x => new QuoteStatusActivityDto($"quote-{x.Id}", x.Kind, x.Text, x.OldValue, x.NewValue,
                x.OccurredAt, x.AccountName, x.DisplayName, null, null, null, x.EditedAt, x.EditedBy)).ToList();
        // Include dashboard status edits made before or outside this page, preserving one overall status.
        events.AddRange((await db.QuoteHistoryAudits.AsNoTracking().Where(x => x.QuoteHistoryId == id
                && x.Action == EstimatingQuoteAuditActions.WorkflowUpdated).ToListAsync(ct))
            .Select(x => new QuoteStatusActivityDto($"audit-{x.Id}", x.FieldName == "Arda status" ? "status" : "details",
                x.FieldName == "Arda status" ? "Overall status updated" : x.FieldName + " updated", x.OldValue, x.NewValue,
                x.ChangedAt, x.ChangedBy, WindowsAccountNames.DisplayName(x.ChangedBy), null, null, null)));
        events.AddRange(threads.SelectMany(t => t.Activity.Select(x => new QuoteStatusActivityDto($"thread-{x.Id}", x.Kind,
            x.Text, x.OldValue, x.NewValue, x.OccurredAt, x.AccountName, x.DisplayName, t.Request.Id, t.Request.VendorName, t.Request.PartNumber, x.EditedAt, x.EditedBy))));
        var unassigned = await vendors.MessagesAsync(db.Set<VendorQuoteMessage>().Where(x => x.QuoteHistoryId == id && x.RequestId == null), ct);
        return new(summary, events.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).ToList(),
            threads.OrderBy(x => x.Request.PartNumber).ThenBy(x => x.Request.VendorName).ToList(), unassigned, workflowDetails,
            await RemovedEmailsAsync(id, ct), await RemovedNotesAsync(id, ct));
    }
    public async Task<QuoteStatusDetailDto> UpdateAsync(int id, UpdateQuoteStatusDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        VendorQuoteService.Version(quote.Version, dto.ExpectedVersion);
        var status = EstimatingArdaStatuses.All.FirstOrDefault(x => x.Equals(dto.Status?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new VendorQuoteException(400, "Choose an available overall quote status.");
        var followUp = VendorQuoteService.Date(dto.FollowUpDate);
        var note = VendorQuoteService.Optional(dto.Note, "Note", 4000);
        var metadata = await db.Set<QuoteStatusMetadata>().SingleOrDefaultAsync(x => x.QuoteHistoryId == id, ct);
        if (metadata is null) { metadata = new() { QuoteHistory = quote }; db.Add(metadata); }
        if (DisplayStatus(quote) != status)
        {
            quote.AuditHistory.Add(new EstimatingQuoteHistoryAuditRecord { QuoteHistory = quote, QuoteNumber = quote.QuoteNumber,
                ImportBatchId = Guid.Empty, Action = EstimatingQuoteAuditActions.WorkflowUpdated, FieldName = "Arda status",
                OldValue = DisplayStatus(quote), NewValue = status, ChangedAt = clock.GetUtcNow(), ChangedBy = access.AccountName });
            quote.ArdaStatus = status; quote.ArdaStatusChangedAt = clock.GetUtcNow(); quote.ArdaStatusChangedBy = access.AccountName;
        }
        if (metadata.FollowUpDate != followUp) Activity(quote, "follow-up", "Quote follow-up date updated", access,
            metadata.FollowUpDate?.ToString("yyyy-MM-dd"), followUp?.ToString("yyyy-MM-dd"));
        metadata.FollowUpDate = followUp;
        if (note is not null) Activity(quote, "note", note, access);
        Touch(quote, access);
        await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    public async Task<QuoteStatusDetailDto> AddNoteAsync(int id, AddQuoteStatusNoteDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        VendorQuoteService.Version(quote.Version, dto.ExpectedVersion);
        Activity(quote, "note", VendorQuoteService.Required(dto.Text, "Note", 4000), access);
        Touch(quote, access);
        await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    public async Task<QuoteStatusDetailDto> AssignMessageAsync(int id, long messageId, AssignQuoteMessageDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        VendorQuoteService.Version(quote.Version, dto.ExpectedVersion);
        var message = await db.Set<VendorQuoteMessage>().SingleOrDefaultAsync(x => x.Id == messageId && x.QuoteHistoryId == id && x.RemovedAt == null, ct)
            ?? throw new VendorQuoteException(404, "The email was not found on this quote.");
        var thread = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(x => x.Id == dto.RequestId && x.QuoteHistoryId == id, ct)
            ?? throw new VendorQuoteException(400, "Choose a thread belonging to this quote.");
        if (message.RequestId is not null) throw new VendorQuoteException(409, "This email has already been assigned to a thread.");
        if (!string.IsNullOrEmpty(thread.VendorEmail) && thread.VendorEmail != message.VendorEmail)
            throw new VendorQuoteException(400, "Choose a thread for the same vendor email address.");
        message.Request = thread;
        var occurredAt = message.ReceivedAt ?? message.SentAt;
        thread.LastMessageAt = thread.LastMessageAt is null || thread.LastMessageAt < occurredAt ? occurredAt : thread.LastMessageAt;
        thread.UpdatedAt = clock.GetUtcNow(); thread.Version++;
        thread.Activity.Add(new VendorQuoteActivity { Request = thread, Kind = "email", Text = "Email assigned to this thread",
            NewValue = message.Subject, OccurredAt = clock.GetUtcNow(), AccountName = access.AccountName, DisplayName = access.DisplayName });
        Activity(quote, "email-assigned", $"Email assigned to {thread.VendorName}", access, null, thread.PartNumber);
        Touch(quote, access);
        await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    private void Activity(EstimatingQuoteHistoryRecord quote, string kind, string text, EstimatingAccessProfile access,
        string? oldValue = null, string? newValue = null) => db.Add(new QuoteStatusActivity { QuoteHistory = quote,
            Kind = kind, Text = text, OldValue = oldValue, NewValue = newValue, OccurredAt = clock.GetUtcNow(),
            AccountName = access.AccountName, DisplayName = access.DisplayName });
    private void Touch(EstimatingQuoteHistoryRecord quote, EstimatingAccessProfile access)
    {
        quote.Version++; quote.UpdatedAt = clock.GetUtcNow(); quote.UpdatedBy = access.AccountName;
    }
    private static string DisplayStatus(EstimatingQuoteHistoryRecord quote) => EstimatingQuoteWorkflowService.DisplayStatus(quote.ArdaStatus);
}

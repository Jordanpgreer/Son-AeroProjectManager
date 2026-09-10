using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

public sealed partial class VendorQuoteService(EstimatingAccessDbContext db, TimeProvider clock)
{
    internal static bool Has(EstimatingAccessProfile access, string permission) => access.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    internal static void Guard(EstimatingAccessProfile access, bool write = false)
    {
        if (!access.IsEnabled || !Has(access, EstimatingPermissions.ViewHistory)
            || (write && (access.IsPreview || !Has(access, EstimatingPermissions.ManageQuotes))))
            throw new VendorQuoteException(403, "You do not have access to this quote operation.");
    }
    internal async Task<List<EstimatingQuoteHistoryRecord>> AccessibleQuotesAsync(EstimatingAccessProfile access, CancellationToken ct)
    {
        Guard(access);
        var records = await db.QuoteHistory.AsNoTracking().ToListAsync(ct);
        if (Has(access, EstimatingPermissions.ManageHistory)) return records;
        var estimators = records.Select(x => x.EstimatingRep).Distinct().ToList();
        return records.Where(x => EstimatingEstimatorIdentity.MatchesUnambiguously(x.EstimatingRep, estimators, access)).ToList();
    }
    internal async Task<EstimatingQuoteHistoryRecord> QuoteAsync(int id, EstimatingAccessProfile access, bool write, CancellationToken ct)
    {
        Guard(access, write);
        var quote = await db.QuoteHistory.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new VendorQuoteException(404, "The quote was not found.");
        if (!Has(access, EstimatingPermissions.ManageHistory))
        {
            var estimators = await db.QuoteHistory.AsNoTracking().Select(x => x.EstimatingRep).Distinct().ToListAsync(ct);
            if (!EstimatingEstimatorIdentity.MatchesUnambiguously(quote.EstimatingRep, estimators, access))
                throw new VendorQuoteException(403, "This quote is assigned to another estimator.");
        }
        return quote;
    }
    private async Task<VendorQuoteRequest> RequestAsync(int id, EstimatingAccessProfile access, bool write, CancellationToken ct)
    {
        Guard(access, write);
        var request = await db.Set<VendorQuoteRequest>().Include(x => x.QuoteHistory).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new VendorQuoteException(404, "The thread was not found.");
        await QuoteAsync(request.QuoteHistoryId, access, write, ct);
        return request;
    }
    public async Task<VendorQuotePageDto> ListAsync(EstimatingAccessProfile access, string? search, string? status,
        int? quoteNumber, int page, int pageSize, CancellationToken ct)
    {
        var ids = (await AccessibleQuotesAsync(access, ct)).Select(x => x.Id).ToArray();
        var query = db.Set<VendorQuoteRequest>().AsNoTracking().Include(x => x.QuoteHistory).Where(x => ids.Contains(x.QuoteHistoryId));
        var rows = await query.ToListAsync(ct);
        rows = rows.Where(x => (!quoteNumber.HasValue || x.QuoteHistory.QuoteNumber == quoteNumber)
            && (string.IsNullOrWhiteSpace(status) || x.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(search) || $"{x.QuoteHistory.QuoteNumber} {x.QuoteHistory.Customer} {x.VendorName} {x.VendorEmail} {x.Title} {x.PartNumber}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.UpdatedAt).ToList();
        page = Math.Clamp(page, 1, 1000000); pageSize = Math.Clamp(pageSize, 1, 100);
        var selected = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var selectedIds = selected.Select(x => x.Id).ToArray();
        var counts = await db.Set<VendorQuoteMessage>().Where(x => x.RequestId.HasValue && selectedIds.Contains(x.RequestId.Value)).GroupBy(x => x.RequestId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var notes = await db.Set<VendorQuoteActivity>().Where(x => selectedIds.Contains(x.RequestId) && x.Kind == "note").GroupBy(x => x.RequestId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return new(selected.Select(x => Summary(x, access, counts.GetValueOrDefault(x.Id), notes.GetValueOrDefault(x.Id))).ToList(), rows.Count, page, pageSize);
    }
    public async Task<VendorQuoteDetailDto> DetailAsync(int id, EstimatingAccessProfile access, CancellationToken ct)
    {
        var request = await RequestAsync(id, access, false, ct);
        var messages = await MessagesAsync(db.Set<VendorQuoteMessage>().Where(x => x.RequestId == id), ct);
        var activity = await db.Set<VendorQuoteActivity>().AsNoTracking().Where(x => x.RequestId == id).ToListAsync(ct);
        return new(Summary(request, access, messages.Count, activity.Count(x => x.Kind == "note")), messages,
            activity.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                .Select(x => new VendorQuoteActivityDto(x.Id, x.Kind, x.Text, x.OldValue, x.NewValue, x.OccurredAt, x.AccountName, x.DisplayName)).ToList());
    }
    internal async Task<IReadOnlyList<VendorQuoteMessageDto>> MessagesAsync(IQueryable<VendorQuoteMessage> query, CancellationToken ct)
    {
        // Project attachment metadata only: opening a thread never loads every attachment BLOB.
        var messages = await query.AsNoTracking().Select(x => new VendorQuoteMessageDto(x.Id, x.Direction,
            x.Subject, x.FromAddress, x.FromName, Array.Empty<string>(), x.SentAt, x.ReceivedAt, x.ImportedAt,
            x.BodyText, x.Attachments.Select(a => new VendorQuoteAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes)).ToList(), x.VendorEmail)).ToListAsync(ct);
        var recipients = await query.AsNoTracking().Select(x => new { x.Id, x.ToAddressesJson }).ToDictionaryAsync(x => x.Id, x => x.ToAddressesJson, ct);
        return messages.Select(x => x with { ToAddresses = JsonSerializer.Deserialize<string[]>(recipients[x.Id]) ?? [] })
            .OrderByDescending(x => x.SentAt).ThenByDescending(x => x.Id).ToList();
    }
    public async Task<VendorQuoteOptionsDto> OptionsAsync(EstimatingAccessProfile access, string? search, CancellationToken ct) =>
        new(VendorQuoteStatuses.All, (await AccessibleQuotesAsync(access, ct))
            .Where(x => string.IsNullOrWhiteSpace(search) || $"{x.QuoteNumber} {x.Customer}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.QuoteNumber).Select(x => new VendorQuoteOptionDto(x.Id, x.QuoteNumber, x.Customer, x.EstimatingRep)).ToList());
    public async Task<VendorQuoteDetailDto> CreateAsync(CreateVendorQuoteDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await QuoteAsync(dto.QuoteHistoryId, access, true, ct);
        var name = Required(dto.VendorName, "Thread or vendor name", 200);
        var email = string.IsNullOrWhiteSpace(dto.VendorEmail) ? "" : Email(dto.VendorEmail);
        var title = Required(dto.Title, "Title", 240);
        var part = Optional(dto.PartNumber, "Part number", 160);
        var key = ThreadKey(email, name, part, title);
        if (await db.Set<VendorQuoteRequest>().AnyAsync(x => x.QuoteHistoryId == quote.Id && x.ThreadKey == key, ct))
            throw new VendorQuoteException(409, "A thread for this vendor and part already exists.");
        var request = NewRequest(quote, email, name, title, part, access);
        request.Status = Status(dto.Status ?? "Untouched");
        request.Activity.Single(x => x.Kind == "created").NewValue = request.Status;
        request.FollowUpDate = Date(dto.FollowUpDate);
        var note = Optional(dto.Note, "Note", 4000);
        if (note is not null) AddActivity(request, "note", note, access);
        db.Add(request);
        await SaveAsync(ct);
        return await DetailAsync(request.Id, access, ct);
    }
    public async Task<VendorQuoteDetailDto> UpdateAsync(int id, UpdateVendorQuoteDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        var request = await RequestAsync(id, access, true, ct);
        Version(request.Version, dto.ExpectedVersion);
        var name = Required(dto.VendorName, "Thread or vendor name", 200);
        var title = Required(dto.Title, "Title", 240);
        var status = Status(dto.Status);
        var followUp = Date(dto.FollowUpDate);
        var note = Optional(dto.Note, "Note", 4000);
        var part = dto.PartNumber is null ? request.PartNumber : Optional(dto.PartNumber, "Part number", 160);
        var email = dto.VendorEmail is null ? request.VendorEmail : string.IsNullOrWhiteSpace(dto.VendorEmail) ? "" : Email(dto.VendorEmail);
        var key = ThreadKey(email, name, part, title);
        if (await db.Set<VendorQuoteRequest>().AnyAsync(x => x.Id != id && x.QuoteHistoryId == request.QuoteHistoryId && x.ThreadKey == key, ct))
            throw new VendorQuoteException(409, "A thread for this vendor and part already exists.");
        if (part != request.PartNumber) AddActivity(request, "details", "Part number updated", access, request.PartNumber, part);
        if (email != request.VendorEmail) AddActivity(request, "details", "Vendor email updated", access, request.VendorEmail, email);
        if (request.VendorName != name) AddActivity(request, "details", "Thread name updated", access, request.VendorName, name);
        if (request.Title != title) AddActivity(request, "details", "Title updated", access, request.Title, title);
        if (request.FollowUpDate != followUp) AddActivity(request, "follow-up", "Follow-up date updated", access, request.FollowUpDate?.ToString("yyyy-MM-dd"), followUp?.ToString("yyyy-MM-dd"));
        SetStatus(request, status, access);
        request.VendorName = name; request.Title = title; request.FollowUpDate = followUp;
        request.PartNumber = part; request.VendorEmail = email; request.ThreadKey = key;
        if (note is not null) AddActivity(request, "note", note, access);
        request.UpdatedAt = clock.GetUtcNow(); request.Version++;
        await SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    public async Task<VendorQuoteDetailDto> AddNoteAsync(int id, AddVendorQuoteNoteDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        var request = await RequestAsync(id, access, true, ct);
        Version(request.Version, dto.ExpectedVersion);
        AddActivity(request, "note", Required(dto.Text, "Note", 4000), access);
        request.UpdatedAt = clock.GetUtcNow(); request.Version++;
        await SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
    public async Task<VendorQuoteAttachment> AttachmentAsync(long id, EstimatingAccessProfile access, CancellationToken ct)
    {
        Guard(access);
        var parent = await db.Set<VendorQuoteAttachment>().Where(x => x.Id == id).Select(x => (int?)x.Message.QuoteHistoryId).SingleOrDefaultAsync(ct)
            ?? throw new VendorQuoteException(404, "The attachment was not found.");
        await QuoteAsync(parent, access, false, ct);
        return await db.Set<VendorQuoteAttachment>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
    }
    private VendorQuoteRequest NewRequest(EstimatingQuoteHistoryRecord quote, string email, string name, string title, string? part, EstimatingAccessProfile access)
    {
        var request = new VendorQuoteRequest { QuoteHistory = quote, QuoteHistoryId = quote.Id, VendorEmail = email,
            VendorName = name, Title = title, PartNumber = part, ThreadKey = ThreadKey(email, name, part, title),
            CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(), StatusChangedAt = clock.GetUtcNow(), StatusChangedBy = access.DisplayName };
        AddActivity(request, "created", "Thread created", access, null, request.Status);
        return request;
    }
    private static string ThreadKey(string email, string name, string? part, string title) => Hash($"{(email.Length > 0 ? email : name + "|" + title).ToLowerInvariant()}\n{part?.ToLowerInvariant()}");
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void SetStatus(VendorQuoteRequest request, string status, EstimatingAccessProfile access)
    {
        if (request.Status == status) return;
        AddActivity(request, "status", "Status updated", access, request.Status, status);
        request.Status = status; request.StatusChangedAt = clock.GetUtcNow(); request.StatusChangedBy = access.DisplayName;
    }
    private void AddActivity(VendorQuoteRequest request, string kind, string text, EstimatingAccessProfile access, string? oldValue = null, string? newValue = null) =>
        request.Activity.Add(new VendorQuoteActivity { Request = request, Kind = kind, Text = text, OldValue = oldValue,
            NewValue = newValue, OccurredAt = clock.GetUtcNow(), AccountName = access.AccountName, DisplayName = access.DisplayName });
    internal async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new VendorQuoteException(409, "This record changed. Refresh before saving again."); }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex)) { throw new VendorQuoteException(409, "This record was created or changed concurrently. Refresh and retry."); }
    }
    private static bool IsUniqueConflict(DbUpdateException ex) => ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
        || ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
    internal static void Version(int actual, int expected) { if (actual != expected) throw new VendorQuoteException(409, "This record changed. Refresh before saving again."); }
    internal static string Required(string? value, string field, int max) => Optional(value, field, max) ?? throw new VendorQuoteException(400, $"{field} is required.");
    internal static string? Optional(string? value, string field, int max)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (clean.Length > max || clean.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')) throw new VendorQuoteException(400, $"{field} must be {max:N0} characters or fewer and contain no invalid control characters.");
        return clean;
    }
    internal static string Email(string? value)
    {
        var clean = Required(value, "Email address", 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(clean, out var address) || address.Address != clean || clean.Contains('\n') || clean.Contains('\r') || !clean.Contains('@'))
            throw new VendorQuoteException(400, "Enter a valid SMTP email address.");
        return clean;
    }
    internal static DateTime? Date(DateTime? value)
    {
        if (value.HasValue && (value.Value.Year < 2000 || value.Value.Year > 2200)) throw new VendorQuoteException(400, "Follow-up date must be between 2000 and 2200.");
        return value?.Date;
    }
    private static string Status(string? value) => VendorQuoteStatuses.All.FirstOrDefault(x => x.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new VendorQuoteException(400, "Choose an available thread status.");
    private static VendorQuoteSummaryDto Summary(VendorQuoteRequest x, EstimatingAccessProfile access, int messages, int notes) =>
        new(x.Id, x.QuoteHistoryId, x.QuoteHistory.QuoteNumber, x.QuoteHistory.Customer, x.QuoteHistory.EstimatingRep,
            x.VendorName, x.VendorEmail, x.Title, x.Status, x.StatusChangedAt, x.StatusChangedBy, x.FollowUpDate,
            x.CreatedAt, x.UpdatedAt, x.LastMessageAt, messages, notes, x.Version,
            !access.IsPreview && Has(access, EstimatingPermissions.ManageQuotes), x.PartNumber);
}

public sealed class VendorQuoteException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

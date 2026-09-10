using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EstimatingDashboard.Api.Services;

public sealed partial class VendorQuoteService
{
    private static readonly Regex QuoteSubject = new(@"^\s*(?:(?:RE|FW|FWD)\s*:\s*)*Quote\s+([0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static int? MatchQuoteNumber(string? subject)
    {
        if (subject is null || subject.Length > 998) return null;
        var match = QuoteSubject.Match(subject);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) && number > 0 ? number : null;
    }
    public async Task<VendorQuoteImportResultDto> ImportAsync(ImportVendorQuoteMessageDto dto, EstimatingAccessProfile access, CancellationToken ct)
        => await ImportCoreAsync(dto, access, ct);

    private async Task<VendorQuoteImportResultDto> ImportCoreAsync(ImportVendorQuoteMessageDto dto, EstimatingAccessProfile access,
        CancellationToken ct, int? explicitQuoteId = null, ManualQuoteEmailImportOptions? manual = null)
    {
        Guard(access, true);
        var subject = manual is null ? Required(dto.Subject, "Subject", 998) : dto.Subject;
        if (subject.Length > 998) throw new VendorQuoteException(400, "Subject exceeds 998 characters.");
        var quoteNumber = MatchQuoteNumber(subject);
        if (quoteNumber is null && explicitQuoteId is null)
        {
            var ambiguous = Regex.Matches(subject, @"\bQuote\s+[0-9]+\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Count > 1;
            return new(ambiguous ? "ambiguous" : "unmatched", null, null, "Subject must be Quote followed by one exact quote number, optionally prefixed by RE, FW, or FWD.");
        }
        var quoteId = explicitQuoteId ?? await db.QuoteHistory.Where(x => x.QuoteNumber == quoteNumber).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
        if (!quoteId.HasValue) return new("unmatched", null, quoteNumber, "This quote is not yet in Arda. The connector can retry later.");
        var quote = await QuoteAsync(quoteId.Value, access, true, ct);
        quoteNumber = quote.QuoteNumber;
        var vendor = Email(dto.VendorEmail);
        VendorQuoteRequest? explicitThread = null;
        if (manual?.RequestId is int threadId)
        {
            explicitThread = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(x => x.Id == threadId && x.QuoteHistoryId == quote.Id, ct)
                ?? throw new VendorQuoteException(400, "Choose a thread belonging to this quote.");
            if (explicitThread.VendorEmail.Length > 0 && explicitThread.VendorEmail != vendor)
                throw new VendorQuoteException(400, "The selected thread belongs to a different email correspondent.");
        }
        var sourceId = Required(dto.SourceMessageId, "Source message ID", 1024);
        var dedup = Hash(vendor + "\n" + sourceId);
        var existing = await db.Set<VendorQuoteMessage>().AsNoTracking().SingleOrDefaultAsync(x => x.DeduplicationKey == dedup, ct);
        if (existing is not null)
        {
            // A reused source ID cannot expose another quote or silently rewrite previously imported evidence.
            if (existing.QuoteHistoryId != quote.Id) throw new VendorQuoteException(409, "The source message ID is already attached to a different quote.");
            return new("duplicate", existing.RequestId, quoteNumber, "This email has already been imported. No email or additional note was added.");
        }
        if (manual is not null) Version(quote.Version, manual.ExpectedVersion);
        var mailbox = manual is null ? Email(dto.Mailbox) : "";
        var from = Email(dto.FromAddress);
        var direction = Required(dto.Direction, "Direction", 16).ToLowerInvariant();
        if (direction is not ("incoming" or "outgoing")) throw new VendorQuoteException(400, "Direction must be incoming or outgoing.");
        if (dto.ToAddresses is null || dto.ToAddresses.Count > 100) throw new VendorQuoteException(400, "A message may contain up to 100 recipients.");
        var recipients = dto.ToAddresses.Select(Email).Distinct().ToList();
        if ((direction == "incoming" && vendor != from) || (direction == "outgoing" && !recipients.Contains(vendor)))
            throw new VendorQuoteException(400, "The vendor must be the sender of an incoming message or a recipient of an outgoing message.");
        var body = Optional(dto.BodyText, "Message body", 200000) ?? "";
        var fromName = Optional(dto.FromName, "Sender name", 200);
        var conversation = Optional(dto.ConversationId, "Conversation ID", 512);
        if (dto.SentAt.Year < 2000 || dto.SentAt > clock.GetUtcNow().AddDays(1)
            || (dto.ReceivedAt.HasValue && (dto.ReceivedAt.Value.Year < 2000 || dto.ReceivedAt > clock.GetUtcNow().AddDays(1))))
            throw new VendorQuoteException(400, "The message dates are outside the supported range.");
        var attachments = DecodeAttachments(dto.Attachments);
        var note = Optional(manual?.Note, "Note", 4000);
        var candidates = await db.Set<VendorQuoteRequest>().Where(x => x.QuoteHistoryId == quote.Id && x.VendorEmail == vendor).ToListAsync(ct);
        VendorQuoteRequest? request = explicitThread;
        var createdThread = false;
        if (manual is null && conversation is not null)
        {
            var related = await db.Set<VendorQuoteMessage>().Where(x => x.QuoteHistoryId == quote.Id && x.VendorEmail == vendor
                && x.ConversationId == conversation && x.RequestId != null).Select(x => x.RequestId!.Value).Distinct().ToListAsync(ct);
            var match = candidates.Where(x => related.Contains(x.Id)).ToList();
            if (match.Count == 1) request = match[0];
        }
        if (manual is null && request is null && candidates.Count == 1) request = candidates[0];
        if (manual is null && request is null && candidates.Count == 0)
        {
            request = NewRequest(quote, vendor, Optional(dto.VendorName, "Vendor name", 200) ?? vendor,
                $"Correspondence with {vendor}"[..Math.Min(240, $"Correspondence with {vendor}".Length)], null, access);
            db.Add(request);
            createdThread = true;
        }
        var message = new VendorQuoteMessage { Request = request, QuoteHistory = quote, QuoteHistoryId = quote.Id,
            VendorEmail = vendor, ConversationId = conversation, DeduplicationKey = dedup, SourceMessageId = sourceId,
            Mailbox = mailbox, Direction = direction, Subject = subject, FromAddress = from, FromName = fromName,
            ToAddressesJson = JsonSerializer.Serialize(recipients), SentAt = dto.SentAt.ToUniversalTime(),
            ReceivedAt = dto.ReceivedAt?.ToUniversalTime(), ImportedAt = clock.GetUtcNow(), ImportedBy = access.AccountName,
            BodyText = body, Attachments = attachments };
        db.Add(message);
        if (request is not null)
        {
            var eventTime = message.ReceivedAt ?? message.SentAt;
            request.LastMessageAt = request.LastMessageAt is null || request.LastMessageAt < eventTime ? eventTime : request.LastMessageAt;
            request.UpdatedAt = clock.GetUtcNow(); request.Version++;
            var correspondent = request.VendorEmail.Length > 0 ? request.VendorName
                : direction == "incoming" ? fromName ?? from : vendor;
            AddActivity(request, "email", EmailActivity(direction, correspondent, manual is not null), access, null, subject);
            if (note is not null) AddActivity(request, "note", note, access);
            if (manual is null && direction == "incoming" && (createdThread || eventTime >= request.StatusChangedAt)
                && request.Status is "Untouched" or "Rates requested" or "Waiting on vendor") SetStatus(request, "Reply received", access);
        }
        else
        {
            db.Add(new QuoteStatusActivity { QuoteHistory = quote, Kind = "email-unassigned",
                Text = EmailActivity(direction, Optional(dto.VendorName, "Vendor name", 200) ?? vendor, manual is not null)
                    + "; choose the matching part and thread", NewValue = subject,
                OccurredAt = clock.GetUtcNow(), AccountName = access.AccountName, DisplayName = access.DisplayName });
            if (note is not null) db.Add(new QuoteStatusActivity { QuoteHistory = quote, Kind = "note", Text = note,
                OccurredAt = clock.GetUtcNow(), AccountName = access.AccountName, DisplayName = access.DisplayName });
        }
        if (manual is not null) { quote.Version++; quote.UpdatedAt = clock.GetUtcNow(); quote.UpdatedBy = access.AccountName; }
        // EF wraps the message, blobs, state change, and audit inserts in one transaction.
        await SaveAsync(ct);
        return new(request is null ? "unassigned" : "imported", request?.Id, quoteNumber,
            request is null ? "Email saved to this quote. Select a thread in Arda to assign it." : "Email imported into the matching quote thread.");
    }
    private static string EmailActivity(string direction, string vendor, bool manual) =>
        $"Email {(direction == "incoming" ? "received from" : "sent to")} {vendor}" + (manual ? " (imported manually)" : "");
    private static List<VendorQuoteAttachment> DecodeAttachments(IReadOnlyList<ImportVendorQuoteAttachmentDto>? inputs)
    {
        if (inputs is null || inputs.Count > 25) throw new VendorQuoteException(400, "A message may contain up to 25 attachments.");
        var result = new List<VendorQuoteAttachment>();
        long total = 0;
        foreach (var input in inputs)
        {
            if (input is null || input.ContentBase64 is null || input.ContentBase64.Length > 13981016)
                throw new VendorQuoteException(400, "Each attachment must be 10 MB or smaller.");
            byte[] content;
            try { content = Convert.FromBase64String(input.ContentBase64); }
            catch (FormatException) { throw new VendorQuoteException(400, "An attachment is not valid base64."); }
            total += content.Length;
            if (content.Length > 10 * 1024 * 1024 || total > 20 * 1024 * 1024) throw new VendorQuoteException(400, "Attachments must be at most 10 MB each and 20 MB per message.");
            var fileName = SafeFileName(input.FileName);
            result.Add(new VendorQuoteAttachment { FileName = fileName, Content = content, SizeBytes = content.Length,
                ContentType = SafeContentType(fileName) });
        }
        return result;
    }
    public static string SafeFileName(string? value)
    {
        var file = Required(value, "Attachment filename", 1024).Replace('\\', '/').Split('/').Last();
        file = new string(file.Where(c => !char.IsControl(c) && !"<>:\"|?*".Contains(c)).ToArray()).Trim(' ', '.');
        if (file.Length == 0) file = "attachment";
        return file[..Math.Min(file.Length, 180)];
    }
    private static string SafeContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".txt" => "text/plain", ".csv" => "text/csv", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", _ => "application/octet-stream"
    };
    public async Task<IReadOnlyList<VendorQuoteSyncStatusDto>> SyncStatusAsync(EstimatingAccessProfile access, CancellationToken ct)
    {
        Guard(access);
        var account = access.AccountName.ToLowerInvariant();
        return (await db.Set<VendorQuoteSyncState>().AsNoTracking().Where(x => x.AccountName == account).ToListAsync(ct))
            .OrderByDescending(x => x.LastCheckedAt).Select(SyncDto).ToList();
    }
    public async Task<VendorQuoteSyncStatusDto> HeartbeatAsync(VendorQuoteSyncHeartbeatDto dto, EstimatingAccessProfile access, CancellationToken ct)
    {
        Guard(access, true);
        var account = access.AccountName.ToLowerInvariant(); var mailbox = Email(dto.Mailbox);
        var client = Required(dto.ClientName, "Client name", 64);
        if (dto.ImportedCount < 0 || dto.DuplicateCount < 0 || dto.DeferredCount < 0 || dto.ImportedCount > 10000000 || dto.DuplicateCount > 10000000 || dto.DeferredCount > 10000000)
            throw new VendorQuoteException(400, "Sync counts must be between zero and ten million.");
        var error = Optional(dto.Error, "Sync error", 500);
        var state = await db.Set<VendorQuoteSyncState>().SingleOrDefaultAsync(x => x.AccountName == account && x.Mailbox == mailbox && x.ClientName == client, ct);
        if (state is null) { state = new() { AccountName = account, Mailbox = mailbox, ClientName = client }; db.Add(state); }
        state.LastCheckedAt = clock.GetUtcNow();
        if (error is null && dto.DeferredCount == 0) state.LastSuccessAt = state.LastCheckedAt;
        state.ImportedCount = dto.ImportedCount; state.DuplicateCount = dto.DuplicateCount; state.DeferredCount = dto.DeferredCount; state.Error = error;
        await SaveAsync(ct);
        return SyncDto(state);
    }
    private static VendorQuoteSyncStatusDto SyncDto(VendorQuoteSyncState x) => new(x.Mailbox, x.ClientName, x.LastCheckedAt, x.LastSuccessAt, x.ImportedCount, x.DuplicateCount, x.DeferredCount, x.Error);
}

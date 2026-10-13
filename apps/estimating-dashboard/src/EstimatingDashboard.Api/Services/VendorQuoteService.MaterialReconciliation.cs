using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed partial class VendorQuoteService
{
    private async Task BackfillMaterialConversationAsync(VendorQuoteMessage existing,
        ImportVendorQuoteMessageDto dto, string sourceId, EstimatingAccessProfile access, CancellationToken ct)
    {
        // A manually imported .eml has no Outlook ConversationId. Only enrich a confirmed send
        // when the connector later finds the same actual sent message; never reclassify a duplicate.
        if (!existing.IsRateRequest || existing.RequestId is null || existing.Direction != "outgoing"
            || existing.RemovedAt.HasValue || existing.MovedAt.HasValue || dto.Direction?.Trim().ToLowerInvariant() != "outgoing"
            || existing.SourceMessageId != sourceId || existing.Subject != dto.Subject
            || existing.SentAt.ToUnixTimeSeconds() != dto.SentAt.ToUnixTimeSeconds()
            || string.IsNullOrWhiteSpace(dto.ConversationId) || dto.ConversationId.Length > 512
            || dto.ConversationId.Any(char.IsControl) || dto.ToAddresses is null || dto.ToAddresses.Count > 100) return;
        string[] recipients;
        try
        {
            if (Email(dto.FromAddress) != existing.FromAddress) return;
            recipients = dto.ToAddresses.Select(Email).Distinct().ToArray();
        }
        catch (VendorQuoteException) { return; }
        var state = await db.Set<MaterialPurchasingState>().SingleOrDefaultAsync(row => row.QuoteHistoryId == existing.QuoteHistoryId, ct);
        if (state is null) return;
        var links = MaterialDraftTracking.Read(state).Where(link => link.IsCurrent && link.RequestId == existing.RequestId
            && MaterialDraftTracking.ValidToken(link.Token) && link.SentAt.HasValue && link.SentSourceId == sourceId
            && link.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)
            && link.Subject == dto.Subject && link.RecipientEmails is { Count: > 0 }
            && link.RecipientEmails.All(email => recipients.Contains(email, StringComparer.OrdinalIgnoreCase))
            && link.RecipientEmails.Contains(dto.VendorEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            && (dto.MaterialDraftToken is null || string.Equals(dto.MaterialDraftToken, link.Token, StringComparison.OrdinalIgnoreCase))
            && (existing.MaterialDraftToken is null || string.Equals(existing.MaterialDraftToken, link.Token, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (links.Length != 1 || MaterialDraftTracking.MatchesConversation(links[0], dto.ConversationId)) return;
        var request = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(row => row.Id == existing.RequestId
            && row.QuoteHistoryId == existing.QuoteHistoryId, ct);
        if (request is null || !string.Equals(request.PartNumber, links[0].PartNumber, StringComparison.OrdinalIgnoreCase)
            || !links[0].RecipientEmails!.Contains(request.VendorEmail, StringComparer.OrdinalIgnoreCase)) return;
        var stored = await db.Set<VendorQuoteMessage>().SingleAsync(row => row.Id == existing.Id, ct);
        stored.ConversationId = dto.ConversationId;
        await RecordMaterialSendAsync(new(request, state, links[0], true, new HashSet<int> { request.Id }), stored, access, ct);
        await SaveAsync(ct);
    }

    private async Task CollapseMaterialFanoutAsync(MaterialDraftLink link, VendorQuoteMessage sent, CancellationToken ct)
    {
        // Edited, header-stripped emails may have been scanned once per contact before a user
        // confirms Rates requested. Collapse only untouched automatic copies with identical evidence.
        if (sent.Id == 0 || link.RecipientEmails is null) return;
        await db.Entry(sent).Collection(message => message.Attachments).LoadAsync(ct);
        var copies = await db.Set<VendorQuoteMessage>().Include(message => message.Attachments)
            .Where(message => message.QuoteHistoryId == sent.QuoteHistoryId && message.SourceMessageId == sent.SourceMessageId
                && message.Id != sent.Id && message.RequestId == null && message.Mailbox != ""
                && message.Direction == "outgoing" && !message.IsRateRequest && message.RemovedAt == null && message.MovedAt == null)
            .ToListAsync(ct);
        var recipients = JsonSerializer.Deserialize<string[]>(sent.ToAddressesJson) ?? [];
        foreach (var copy in copies.Where(copy => link.RecipientEmails.Contains(copy.VendorEmail, StringComparer.OrdinalIgnoreCase)
            && copy.Subject == sent.Subject && copy.FromAddress == sent.FromAddress && copy.SentAt == sent.SentAt
            && copy.BodyText == sent.BodyText && copy.MaterialDraftToken == sent.MaterialDraftToken
            && recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(JsonSerializer.Deserialize<string[]>(copy.ToAddressesJson) ?? [])
            && SameAttachments(copy.Attachments, sent.Attachments)))
        {
            db.RemoveRange(copy.Attachments);
            db.Remove(copy);
        }
    }

    private static bool SameAttachments(ICollection<VendorQuoteAttachment> left, ICollection<VendorQuoteAttachment> right)
    {
        var remaining = right.ToList();
        foreach (var attachment in left)
        {
            var match = remaining.FindIndex(other => other.FileName == attachment.FileName && other.ContentType == attachment.ContentType
                && other.SizeBytes == attachment.SizeBytes && other.Content.AsSpan().SequenceEqual(attachment.Content));
            if (match < 0) return false;
            remaining.RemoveAt(match);
        }
        return remaining.Count == 0;
    }
}

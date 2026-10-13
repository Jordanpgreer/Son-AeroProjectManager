using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed partial class VendorQuoteService
{
    private sealed record MaterialMessageMatch(VendorQuoteRequest? Request, MaterialPurchasingState? State,
        MaterialDraftLink? Link, bool IsRateRequest, IReadOnlySet<int> ManagedRequestIds);

    private async Task<MaterialMessageMatch> MatchMaterialMessageAsync(int quoteId, string subjectItem,
        ImportVendorQuoteMessageDto dto, string vendor, string direction, string body,
        IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var state = await db.Set<MaterialPurchasingState>().SingleOrDefaultAsync(x => x.QuoteHistoryId == quoteId, ct);
        var links = MaterialDraftTracking.Read(state);
        var managed = links.Where(link => MaterialDraftTracking.ValidToken(link.Token)).Select(link => link.RequestId).ToHashSet();
        var noMatch = new MaterialMessageMatch(null, state, null, false, managed);
        if (state is null || subjectItem.Length == 0) return noMatch;
        var matches = new List<(MaterialDraftLink Link, bool Sent)>();
        foreach (var link in links.Where(link => link.IsCurrent && MaterialDraftTracking.ValidToken(link.Token)
            && link.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)
            && string.Equals(link.PartNumber, subjectItem, StringComparison.OrdinalIgnoreCase)
            && link.RecipientEmails?.Contains(vendor, StringComparer.OrdinalIgnoreCase) == true))
        {
            if (direction == "outgoing" && link.PreparedAt.HasValue
                && dto.SentAt.ToUnixTimeSeconds() >= link.PreparedAt.Value.ToUnixTimeSeconds()
                && string.Equals(dto.Subject, link.Subject, StringComparison.Ordinal)
                && link.RecipientEmails!.All(email => recipients.Contains(email, StringComparer.OrdinalIgnoreCase)))
            {
                // A supplied but obsolete token must never fall back to body matching.
                var verifiesDraft = dto.MaterialDraftToken is not null
                    ? string.Equals(dto.MaterialDraftToken, link.Token, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrWhiteSpace(link.TemplateText)
                        && MaterialDraftTracking.NormalizeBody(body).StartsWith(
                            MaterialDraftTracking.NormalizeBody(link.TemplateText), StringComparison.Ordinal);
                if (verifiesDraft) { matches.Add((link, true)); continue; }
            }
            if (link.SentAt.HasValue && dto.SentAt >= link.SentAt
                && (dto.MaterialDraftToken is null || string.Equals(dto.MaterialDraftToken, link.Token, StringComparison.OrdinalIgnoreCase))
                && (MaterialDraftTracking.MatchesConversation(link, dto.ConversationId)
                    || MaterialDraftTracking.ContainsTemplate(link.TemplateText, body)))
                matches.Add((link, false));
        }
        if (matches.Count != 1) return noMatch;
        var match = matches[0];
        var request = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(x => x.Id == match.Link.RequestId
            && x.QuoteHistoryId == quoteId, ct);
        if (request is null || !string.Equals(request.PartNumber, match.Link.PartNumber, StringComparison.OrdinalIgnoreCase)
            || !match.Link.RecipientEmails!.Contains(request.VendorEmail, StringComparer.OrdinalIgnoreCase)) return noMatch;
        return new(request, state, match.Link, match.Sent, managed);
    }

    private async Task RecordMaterialSendAsync(MaterialMessageMatch match, VendorQuoteMessage sent,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        if (!match.IsRateRequest || match.Link is null || match.State is null || match.Request is null) return;
        await CollapseMaterialFanoutAsync(match.Link, sent, ct);
        var canonicalDedup = MaterialDraftTracking.SentDeduplicationKey(sent.SourceMessageId);
        if (!await db.Set<VendorQuoteMessage>().AnyAsync(message => message.DeduplicationKey == canonicalDedup && message.Id != sent.Id, ct))
            sent.DeduplicationKey = canonicalDedup;
        var link = match.Link with { SentAt = sent.SentAt, SentSourceId = sent.SourceMessageId,
            ConversationId = sent.ConversationId ?? match.Link.ConversationId,
            ConversationIds = (match.Link.ConversationIds ?? []).Append(match.Link.ConversationId ?? "")
                .Append(sent.ConversationId ?? "").Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray() };
        // Keep the first verified send as the generation's reply boundary.
        if (match.Link.SentAt.HasValue && match.Link.SentAt < sent.SentAt)
            link = link with { SentAt = match.Link.SentAt };
        match.State.DraftsJson = JsonSerializer.Serialize(MaterialDraftTracking.Read(match.State)
            .Select(existing => existing.ItemId == link.ItemId ? link : existing));
        match.State.Version++;
        match.State.UpdatedAt = clock.GetUtcNow();
        match.State.UpdatedBy = access.AccountName;

        // Outlook may scan the Inbox before Sent Items. Reconcile only untouched, unassigned replies
        // after sent evidence exists; removed/moved/manually assigned messages are never relocated.
        var unassigned = await db.Set<VendorQuoteMessage>().Where(message => message.QuoteHistoryId == sent.QuoteHistoryId
            && message.RequestId == null && message.Direction == "incoming" && message.RemovedAt == null
            && message.MovedAt == null).ToListAsync(ct);
        foreach (var reply in unassigned.Where(reply => reply.SentAt >= link.SentAt
            && link.RecipientEmails!.Contains(reply.FromAddress, StringComparer.OrdinalIgnoreCase)
            && (reply.MaterialDraftToken is null || string.Equals(reply.MaterialDraftToken, link.Token, StringComparison.OrdinalIgnoreCase))
            && string.Equals(QuoteSubject.Match(reply.Subject).Groups["item"].Value.Trim(), link.PartNumber, StringComparison.OrdinalIgnoreCase)
            && (MaterialDraftTracking.MatchesConversation(link, reply.ConversationId)
                || MaterialDraftTracking.ContainsTemplate(link.TemplateText, reply.BodyText))))
        {
            reply.Request = match.Request;
            reply.RequestId = match.Request.Id;
            var when = reply.ReceivedAt ?? reply.SentAt;
            if (match.Request.LastMessageAt is null || match.Request.LastMessageAt < when) match.Request.LastMessageAt = when;
            AddActivity(match.Request, "email", $"Email received from {reply.FromAddress}; matched after the material request was sent", access, null, reply.Subject);
            match.Request.UpdatedAt = clock.GetUtcNow();
            match.Request.Version++;
        }
    }

    internal async Task ConfirmMaterialSentAsync(VendorQuoteRequest request, VendorQuoteMessage message,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        Guard(access, true);
        if (!message.IsRateRequest || message.Direction != "outgoing" || message.RemovedAt.HasValue || message.MovedAt.HasValue) return;
        var state = await db.Set<MaterialPurchasingState>().SingleOrDefaultAsync(row => row.QuoteHistoryId == request.QuoteHistoryId, ct);
        if (state is null) return;
        var recipients = JsonSerializer.Deserialize<string[]>(message.ToAddressesJson) ?? [];
        var links = MaterialDraftTracking.Read(state).Where(link => link.IsCurrent && link.RequestId == request.Id
            && link.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)
            && MaterialDraftTracking.ValidToken(link.Token) && link.PreparedAt.HasValue
            && message.SentAt.ToUnixTimeSeconds() >= link.PreparedAt.Value.ToUnixTimeSeconds()
            && message.Subject == link.Subject && string.Equals(request.PartNumber, link.PartNumber, StringComparison.OrdinalIgnoreCase)
            && (message.MaterialDraftToken is null || string.Equals(message.MaterialDraftToken, link.Token, StringComparison.OrdinalIgnoreCase))
            && link.RecipientEmails is { Count: > 0 } && link.RecipientEmails.All(email => recipients.Contains(email, StringComparer.OrdinalIgnoreCase))
            && link.RecipientEmails.Contains(request.VendorEmail, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (links.Length == 1)
            await RecordMaterialSendAsync(new(request, state, links[0], true, new HashSet<int> { request.Id }), message, access, ct);
    }
}

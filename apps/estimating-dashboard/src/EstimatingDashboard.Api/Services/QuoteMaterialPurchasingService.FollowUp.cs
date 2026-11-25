using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

internal sealed partial class QuoteMaterialPurchasingService
{
    internal async Task<MaterialFollowUpDraftDto> GetFollowUpDraftAsync(int id, string itemId, EstimatingAccessProfile access,
        CancellationToken ct, bool inspectCurrentRequirements = true)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > 100 || itemId.Any(char.IsControl))
            throw new VendorQuoteException(400, "Choose a valid material from this quote.");

        var state = await StateAsync(id, ct);
        var link = Drafts(state).SingleOrDefault(entry => entry.Kind == "material" && entry.IsCurrent && entry.ItemId == itemId
            && entry.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)
            && entry.SentAt.HasValue && entry.RecipientEmails is { Count: > 0 });
        if (link is null) throw FollowUpUnavailable();
        // The browser request already verified the current BOM. A connector claim shortly afterward
        // rechecks saved tracking and correspondence without waiting on a second Fulcrum inspection.
        if (inspectCurrentRequirements)
        {
            var review = await WithRequestsAsync(await InspectAsync(quote, Selections(state), ct), state, access, ct);
            var followUp = review.Materials.SingleOrDefault(material => material.ItemId == itemId)?.FollowUp;
            if (followUp?.State != MaterialFollowUpTracking.NeedsFollowUp) throw FollowUpUnavailable();
        }
        var messages = await FollowUpMessagesAsync(id, [link.RequestId], ct);
        var original = MaterialFollowUpTracking.Original(link, messages);
        if (original is null) throw FollowUpUnavailable();
        if (!MaterialFollowUpTracking.CanOpenDraft(original, access))
            throw new VendorQuoteException(409, "Open the follow-up from the Outlook mailbox and Arda account that synced the original RFQ.");
        // Recheck after loading the original. The vendor can reply while the review is open.
        if (MaterialFollowUpTracking.Project(link, messages, access, clock.GetUtcNow())?.State != MaterialFollowUpTracking.NeedsFollowUp)
            throw FollowUpUnavailable();

        return new(original.SourceMessageId, original.Mailbox, original.Subject, MaterialFollowUpTracking.Recipients(original),
            MaterialFollowUpTracking.Body, original.SentAt, original.ConversationId, link.RecipientEmails!);
    }

    private async Task<IReadOnlyList<VendorQuoteMessage>> FollowUpMessagesAsync(int quoteId, int[] requestIds, CancellationToken ct)
    {
        if (requestIds.Length == 0) return [];
        return await db.Set<VendorQuoteMessage>().AsNoTracking().Where(message =>
            message.QuoteHistoryId == quoteId && message.RequestId.HasValue && requestIds.Contains(message.RequestId.Value)
            && message.RemovedAt == null && message.MovedAt == null).ToListAsync(ct);
    }

    private static VendorQuoteException FollowUpUnavailable() =>
        new(409, "This material no longer needs a follow-up. Refresh the material list.");
}

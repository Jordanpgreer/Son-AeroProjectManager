using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Api.Services;

internal static class MaterialFollowUpTracking
{
    public const string Body = "Hi team,\n\nJust following up on this request.\n\nDo you have this quote available?\n\nThank you!";
    public const string NeedsFollowUp = "needs-follow-up";

    private static readonly TimeSpan Wait = TimeSpan.FromHours(24);
    private static readonly string FollowUpBody = MaterialDraftTracking.NormalizeBody(Body);

    internal static VendorQuoteMessage? Original(MaterialDraftLink link, IEnumerable<VendorQuoteMessage> messages) =>
        string.IsNullOrWhiteSpace(link.SentSourceId) ? null : messages.FirstOrDefault(message =>
            message.Direction == "outgoing" && message.IsRateRequest && message.SourceMessageId == link.SentSourceId);

    internal static MaterialFollowUpDto? Project(MaterialDraftLink link, IEnumerable<VendorQuoteMessage> messages,
        EstimatingAccessProfile access, DateTimeOffset now)
    {
        if (link.SentAt is not { } sentAt || link.RecipientEmails is not { Count: > 0 } contacts) return null;
        var original = Original(link, messages);
        if (original is null) return null;

        var replyAt = messages.Where(message => message.Direction == "incoming" && message.SentAt >= sentAt
                && contacts.Contains(message.FromAddress, StringComparer.OrdinalIgnoreCase))
            .Select(message => (DateTimeOffset?)(message.ReceivedAt ?? message.SentAt)).Min();
        var followUpAt = messages.Where(message => IsSentFollowUp(link, original, message))
            .Select(message => (DateTimeOffset?)message.SentAt).Min();
        var state = replyAt.HasValue ? "vendor-replied" : followUpAt.HasValue ? "follow-up-sent"
            : now - sentAt >= Wait ? NeedsFollowUp : "waiting";
        return new(state, sentAt, original.Id, original.Subject, Recipients(original), followUpAt, replyAt,
            state == NeedsFollowUp && CanOpenDraft(original, access));
    }

    internal static bool CanOpenDraft(VendorQuoteMessage original, EstimatingAccessProfile access) =>
        !access.IsPreview && VendorQuoteService.Has(access, EstimatingPermissions.ManageQuotes)
        && !string.IsNullOrWhiteSpace(original.Mailbox)
        && string.Equals(original.Mailbox, original.FromAddress, StringComparison.OrdinalIgnoreCase)
        && string.Equals(original.ImportedBy, access.AccountName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSentFollowUp(MaterialDraftLink link, VendorQuoteMessage original, VendorQuoteMessage message) =>
        message.Direction == "outgoing" && !message.IsRateRequest && message.SentAt > original.SentAt
        && !string.IsNullOrWhiteSpace(message.Mailbox)
        && string.Equals(message.Mailbox, original.Mailbox, StringComparison.OrdinalIgnoreCase)
        && string.Equals(message.FromAddress, original.FromAddress, StringComparison.OrdinalIgnoreCase)
        && MaterialDraftTracking.MatchesConversation(link, message.ConversationId)
        && message.Subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase)
        && string.Equals(message.Subject[3..].Trim(), original.Subject, StringComparison.OrdinalIgnoreCase)
        && BodyMatches(message.BodyText)
        && link.RecipientEmails is { Count: > 0 } contacts
        && contacts.All(Recipients(message).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);

    private static bool BodyMatches(string text)
    {
        var normalized = MaterialDraftTracking.NormalizeBody(text);
        return normalized == FollowUpBody || normalized.StartsWith(FollowUpBody + " ", StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> Recipients(VendorQuoteMessage message) =>
        JsonSerializer.Deserialize<string[]>(message.ToAddressesJson) ?? [];
}

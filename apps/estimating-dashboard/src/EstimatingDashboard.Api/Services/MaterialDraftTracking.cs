using System.Text.Json;
using System.Text.RegularExpressions;
using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Api.Services;

internal sealed record MaterialDraftLink(string ItemId, int RequestId, string Fingerprint,
    string? Token = null, DateTimeOffset? PreparedAt = null, string? PartNumber = null,
    string? Subject = null, string? TemplateText = null, IReadOnlyList<string>? RecipientEmails = null,
    string? QuantitiesFingerprint = null, bool IsCurrent = true, DateTimeOffset? SentAt = null,
    string? SentSourceId = null, string? ConversationId = null, IReadOnlyList<string>? ConversationIds = null);

internal static class MaterialDraftTracking
{
    public const string Header = "X-Arda-Material-Draft";
    public static IReadOnlyList<MaterialDraftLink> Read(MaterialPurchasingState? state) =>
        JsonSerializer.Deserialize<MaterialDraftLink[]>(state?.DraftsJson ?? "[]") ?? [];
    public static string QuantitiesFingerprint(MaterialPurchasingState? state) =>
        VendorQuoteService.Hash(state?.QuantitiesJson ?? "[]");
    public static string SentDeduplicationKey(string sourceId) => VendorQuoteService.Hash("material-rfq\n" + sourceId);
    public static bool ValidToken(string? token) => token is { Length: 32 } && token.All(char.IsAsciiHexDigit);
    public static string NormalizeBody(string text) => Regex.Replace(
        Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"(?m)^\s*>+\s?", "",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), @"\s+", " ", RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)).Trim();
    public static bool ContainsTemplate(string? template, string body) => !string.IsNullOrWhiteSpace(template)
        && NormalizeBody(body).Contains(NormalizeBody(template), StringComparison.Ordinal);
    public static bool MatchesConversation(MaterialDraftLink link, string? conversation) => !string.IsNullOrWhiteSpace(conversation)
        && (link.ConversationId == conversation || link.ConversationIds?.Contains(conversation, StringComparer.Ordinal) == true);
}

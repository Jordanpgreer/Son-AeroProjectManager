using System.Text.Json;

namespace EstimatingDashboard.Api.Services;

internal sealed record FulcrumQuoteDateDefinition(string Name, string Label);
internal sealed record FulcrumQuoteDateDefinitions(string Status,
    IReadOnlyList<FulcrumQuoteDateDefinition> Fields, int? UpstreamStatusCode = null, DateTimeOffset? RetryAt = null);
internal readonly record struct FulcrumQuoteDateField(bool Found, string Name, JsonElement Value, bool Ambiguous = false);

internal static class FulcrumQuoteDateFields
{
    public static string Normalize(string value) => string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));

    public static FulcrumQuoteDateField Find(FulcrumQuoteDto quote, FulcrumQuoteDateDefinitions? definitions,
        params string[] names)
    {
        var fields = quote.CustomFields;
        if (fields is null) return default;
        var ambiguous = false;
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var direct = fields.FirstOrDefault(pair => Normalize(pair.Key) == Normalize(name));
            // An explicitly present primary field, including a blank or malformed value, always wins.
            if (direct.Key is not null) return new(true, direct.Key, direct.Value);
            // Ambiguous optional metadata must not suppress an existing documented direct alias.
            if (ambiguous) continue;
            var matches = MatchingDefinitions(definitions, name);
            if (matches.Count > 1) { ambiguous = true; continue; }
            if (matches.Count == 0) continue;
            var candidates = fields.Where(pair => Normalize(pair.Key) == Normalize(matches[0].Name)
                || Normalize(pair.Key) == Normalize(matches[0].Label)).ToArray();
            if (candidates.Length > 1) { ambiguous = true; continue; }
            if (candidates.Length == 1) return new(true, candidates[0].Key, candidates[0].Value);
        }
        return new(false, string.Empty, default, ambiguous);
    }

    public static IReadOnlyList<FulcrumQuoteDateDefinition> MatchingDefinitions(
        FulcrumQuoteDateDefinitions? definitions, string name) => definitions?.Status == "Succeeded"
        ? definitions.Fields.Where(field => Normalize(field.Name) == Normalize(name)
            || Normalize(field.Label) == Normalize(name)).ToArray() : [];
}

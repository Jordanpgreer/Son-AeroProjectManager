using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Api.Services;

internal static class QuoteTimingDiagnosticProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static string BoundedName(string? name) => new((name ?? string.Empty)
        .Where(character => !char.IsControl(character)).Take(100).ToArray());

    public static QuoteTimingReadDto Project(JsonElement source, EstimatingQuoteHistoryRecord stored,
        FulcrumQuoteSyncOptions options, int? returnedCount = null)
    {
        var quote = source.Deserialize<FulcrumQuoteDto>(JsonOptions);
        if (quote is null || quote.Number != stored.QuoteNumber
            || !string.Equals(quote.Id, stored.SourceId, StringComparison.OrdinalIgnoreCase))
            return new("UnexpectedQuote", ReturnedCount: returnedCount);

        var fields = quote.CustomFields ?? [];
        var observations = new List<QuoteTimingFieldDto>();
        Observe("assignment", [options.CustomFields.DateToEstimating], ["Date to Estimating"]);
        Observe("completion", [options.CustomFields.EstimatingCompletionDate,
            "Estimate Completion Date", "Estimating Complete Date"], ["Estimating Completion Date"]);
        Observe("due", [options.CustomFields.RfqDueDate], ["RFQ Due Date"]);
        var mapped = FulcrumQuoteMapper.Map([new(quote, null)],
            new Dictionary<int, EstimatingQuoteHistoryRecord> { [stored.QuoteNumber] = stored }, options);
        var row = mapped.Rows.Single();
        return new("Succeeded", UpstreamStatusCode: 200, ReturnedCount: returnedCount,
            AvailableFieldNames: fields.Keys.Take(48).Select(BoundedName).ToList(),
            FieldNamesTruncated: fields.Count > 48,
            RecognizedFields: observations,
            ProjectedDates: new(row.DateToEstimating, row.EstimatingCompletionDate, row.RfqDueDate),
            HasMappingWarnings: mapped.Warnings.Count > 0);

        void Observe(string purpose, string[] priority, string[] defaults)
        {
            // Use the same name precedence as DateField; values are never serialized into this diagnostic.
            var selected = priority.Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => fields.Keys.FirstOrDefault(key => Normalize(key) == Normalize(name)))
                .FirstOrDefault(key => key is not null);
            var names = priority.Concat(defaults).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(Normalize).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in fields.Where(pair => names.Contains(Normalize(pair.Key))).Take(8))
                observations.Add(new(purpose, BoundedName(pair.Key), pair.Key == selected,
                    pair.Value.ValueKind.ToString(), Shape(pair.Value)));
        }
    }

    private static string Normalize(string value) => string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string Shape(JsonElement value, int depth = 0)
    {
        if (depth >= 4) return "Nested";
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "value", "displayValue", "name" })
                if (value.TryGetProperty(name, out var nested)) return name + "/" + Shape(nested, depth + 1);
            return "ObjectWithoutRecognizedScalar";
        }
        return value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())
            ? "BlankString" : value.ValueKind.ToString();
    }
}

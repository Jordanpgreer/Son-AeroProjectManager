using System.Globalization;
using System.Text.Json;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

internal sealed class FulcrumQuoteSyncService(
    EstimatingAccessDbContext db,
    FulcrumQuoteClient client,
    EstimatingHistoryImportService importer,
    IOptions<FulcrumQuoteSyncOptions> options,
    ILogger<FulcrumQuoteSyncService> logger)
{
    private const string Actor = "FULCRUM_API_SCHEDULE";

    public async Task RunScheduledAsync(
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        scheduledForUtc = scheduledForUtc.ToUniversalTime();
        var run = new FulcrumQuoteSyncRun
        {
            Id = Guid.NewGuid(),
            ScheduledForUtc = scheduledForUtc,
            StartedAt = DateTimeOffset.UtcNow,
            Status = FulcrumQuoteSyncStatuses.Running
        };
        db.FulcrumQuoteSyncRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.FulcrumQuoteSyncRuns
                .AsNoTracking()
                .AnyAsync(candidate => candidate.ScheduledForUtc == scheduledForUtc, cancellationToken))
            {
                logger.LogInformation(
                    "The Fulcrum quote sync scheduled for {ScheduledForUtc} was already claimed by another process.",
                    scheduledForUtc);
                return;
            }
            throw;
        }

        try
        {
            var snapshots = await client.GetQuotesAsync(cancellationToken);
            var quoteNumbers = snapshots.Select(snapshot => snapshot.Quote.Number).Distinct().ToList();
            var existing = await db.QuoteHistory
                .AsNoTracking()
                .Where(record => quoteNumbers.Contains(record.QuoteNumber))
                .ToDictionaryAsync(record => record.QuoteNumber, cancellationToken);
            var mapping = FulcrumQuoteMapper.Map(snapshots, existing, options.Value);
            foreach (var warning in mapping.Warnings.Take(25))
                logger.LogWarning("Fulcrum quote sync mapping warning: {Warning}", warning);
            if (mapping.Warnings.Count > 25)
                logger.LogWarning(
                    "Fulcrum quote sync produced {AdditionalWarningCount} additional mapping warnings.",
                    mapping.Warnings.Count - 25);

            var result = await importer.ApplyAutomatedAsync(
                mapping.Rows,
                $"Fulcrum API sync {scheduledForUtc:yyyy-MM-dd HHmm} UTC",
                Actor,
                cancellationToken);
            run.Status = FulcrumQuoteSyncStatuses.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.QuotesReceived = snapshots.Count;
            run.NewRecords = result.NewRecords;
            run.UpdatedRecords = result.UpdatedRecords;
            run.UnchangedRecords = result.UnchangedRecords;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Fulcrum quote sync completed with {QuoteCount} quotes: {NewCount} new, {UpdatedCount} updated, and {UnchangedCount} unchanged.",
                snapshots.Count,
                result.NewRecords,
                result.UpdatedRecords,
                result.UnchangedRecords);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            var failedRun = await db.FulcrumQuoteSyncRuns
                .SingleAsync(candidate => candidate.Id == run.Id, CancellationToken.None);
            failedRun.Status = FulcrumQuoteSyncStatuses.Failed;
            failedRun.CompletedAt = DateTimeOffset.UtcNow;
            failedRun.ErrorMessage = Truncate(exception.Message, 2000);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

internal static class FulcrumQuoteMapper
{
    public static FulcrumQuoteMappingResult Map(
        IReadOnlyList<FulcrumQuoteSnapshot> snapshots,
        IReadOnlyDictionary<int, EstimatingQuoteHistoryRecord> existing,
        FulcrumQuoteSyncOptions options)
    {
        var rows = new List<EstimatingHistoryImportRow>(snapshots.Count);
        var warnings = new List<string>();
        var rowNumber = 1;
        foreach (var snapshot in snapshots.OrderBy(candidate => candidate.Quote.Number))
        {
            rowNumber++;
            var quote = snapshot.Quote;
            existing.TryGetValue(quote.Number, out var current);

            var customerContact = TextField(
                quote,
                current?.CustomerContact,
                options.CustomFields.CustomerContact,
                "Customer Contact");
            var rfqReference = TextField(
                quote,
                null,
                options.CustomFields.RfqReferenceNumber,
                "RFQ REF No",
                "RFQ Reference Number");
            if (!rfqReference.Found)
                rfqReference = ExternalReference(quote, options.CustomFields.RfqReferenceNumber);
            var rfqDueDate = DateField(
                quote,
                current?.RfqDueDate,
                warnings,
                options.CustomFields.RfqDueDate);
            var dateToEstimating = DateField(
                quote,
                current?.DateToEstimating,
                warnings,
                options.CustomFields.DateToEstimating);
            var completionDate = DateField(
                quote,
                current?.EstimatingCompletionDate,
                warnings,
                options.CustomFields.EstimatingCompletionDate,
                "Estimate Completion Date");
            var numberOfParts = IntField(
                quote,
                current?.NumberOfParts ?? 0,
                warnings,
                options.CustomFields.NumberOfParts,
                "Number of Parts");
            var estimator = TextField(
                quote,
                current?.EstimatingRep ?? "Unassigned",
                options.CustomFields.EstimatingRep,
                "Estimator");

            rows.Add(EstimatingHistoryImportService.CreateRow(
                rowNumber,
                quote.Id,
                quote.Number,
                FirstText(snapshot.Report?.CustomerName, current?.Customer, "Unknown customer"),
                customerContact.Value,
                FirstText(snapshot.Report?.SalesPersonName, current?.SalesPerson, "Unassigned"),
                DisplayStatus(FirstText(snapshot.Report?.Status, quote.Status, current?.QuoteStatus, "Unknown")),
                rfqReference.Found ? rfqReference.Value : current?.RfqReferenceNumber,
                FirstText(estimator.Value, current?.EstimatingRep, "Unassigned"),
                snapshot.Report?.TotalInPrimaryCurrency
                    ?? quote.TotalInPrimaryCurrency
                    ?? current?.TotalValue
                    ?? 0m,
                rfqDueDate,
                dateToEstimating,
                TextField(quote, current?.Issues, options.CustomFields.Issues, "Issues").Value,
                TextField(quote, current?.QuoteOnTrack, options.CustomFields.QuoteOnTrack, "Quote On Track").Value,
                TextField(quote, current?.QuoteComplexity, options.CustomFields.QuoteComplexity, "Complexity").Value,
                numberOfParts,
                TextField(quote, current?.EstimatingStatus, options.CustomFields.EstimatingStatus).Value,
                completionDate));
        }
        return new FulcrumQuoteMappingResult(rows, warnings);
    }

    private static FieldText TextField(
        FulcrumQuoteDto quote,
        string? fallback,
        params string[] names)
    {
        var field = FindCustomField(quote, names);
        return field.Found
            ? new FieldText(true, ScalarText(field.Value))
            : new FieldText(false, fallback);
    }

    private static DateTime? DateField(
        FulcrumQuoteDto quote,
        DateTime? fallback,
        ICollection<string> warnings,
        params string[] names)
    {
        var field = FindCustomField(quote, names);
        if (!field.Found) return fallback;
        var text = ScalarText(field.Value);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var offset))
            return offset.Date;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
            return local.Date;
        warnings.Add($"Quote {quote.Number} has an invalid date in custom field '{field.Name}': '{text}'. The previous value was retained.");
        return fallback;
    }

    private static int IntField(
        FulcrumQuoteDto quote,
        int fallback,
        ICollection<string> warnings,
        params string[] names)
    {
        var field = FindCustomField(quote, names);
        if (!field.Found) return fallback;
        var text = ScalarText(field.Value);
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return value;
        warnings.Add($"Quote {quote.Number} has an invalid whole number in custom field '{field.Name}': '{text}'. The previous value was retained.");
        return fallback;
    }

    private static FieldValue FindCustomField(FulcrumQuoteDto quote, params string[] names)
    {
        if (quote.CustomFields is null || quote.CustomFields.Count == 0)
            return new FieldValue(false, string.Empty, default);
        var normalizedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in quote.CustomFields)
            if (normalizedNames.Contains(Normalize(pair.Key)))
                return new FieldValue(true, pair.Key, pair.Value);
        return new FieldValue(false, string.Empty, default);
    }

    private static FieldText ExternalReference(FulcrumQuoteDto quote, params string[] names)
    {
        if (quote.ExternalReferences is null)
            return new FieldText(false, null);
        var normalizedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(Normalize)
            .Append("RFQ")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in quote.ExternalReferences)
        {
            var key = Normalize(pair.Key);
            var type = Normalize(pair.Value.Type ?? string.Empty);
            if (!normalizedNames.Any(name => key.Contains(name, StringComparison.Ordinal)
                    || type.Contains(name, StringComparison.Ordinal)))
                continue;
            return new FieldText(true, FirstText(pair.Value.DisplayId, pair.Value.ExternalId));
        }
        return new FieldText(false, null);
    }

    private static string? ScalarText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return Clean(value.GetString());
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Object:
                foreach (var propertyName in new[] { "value", "displayValue", "name" })
                    if (value.TryGetProperty(propertyName, out var nested))
                        return ScalarText(nested);
                return Clean(value.GetRawText());
            case JsonValueKind.Array:
                return string.Join(", ", value.EnumerateArray().Select(ScalarText).Where(text => text is not null));
            default:
                return Clean(value.GetRawText());
        }
    }

    private static string DisplayStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "needsapproval" or "needs approval" => "Needs Approval",
        "draft" => "Draft",
        "open" => "Open",
        "approved" => "Approved",
        "sent" => "Sent",
        "won" => "Won",
        "lost" => "Lost",
        _ => value.Trim()
    };

    private static string FirstText(params string?[] values) =>
        values.Select(Clean).FirstOrDefault(value => value is not null) ?? string.Empty;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string value) =>
        string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));

    private readonly record struct FieldValue(bool Found, string Name, JsonElement Value);
    private readonly record struct FieldText(bool Found, string? Value);
}

internal sealed record FulcrumQuoteMappingResult(
    IReadOnlyList<EstimatingHistoryImportRow> Rows,
    IReadOnlyList<string> Warnings);

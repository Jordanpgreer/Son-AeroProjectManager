using System.Globalization;
using System.Text.Json;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Api.Services;

internal sealed class EnterpriseQuoteSyncService(
    EstimatingAccessDbContext db,
    EstimatingHistoryImportService importer,
    IEnumerable<IEstimatingQuoteProvider> providers,
    IEnterpriseProviderSource providerSource,
    ILogger<EnterpriseQuoteSyncService> logger)
{
    private static readonly SemaphoreSlim SynchronizationGate = new(1, 1);

    public async Task RunScheduledAsync(
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        if (!await SynchronizationGate.WaitAsync(0, cancellationToken))
        {
            logger.LogInformation(
                "Skipped the enterprise quote sync scheduled for {ScheduledForUtc} because another sync is already running.",
                scheduledForUtc);
            return;
        }

        try
        {
            var provider = await ResolveProviderAsync(cancellationToken);
            await RunAsync(
                provider,
                scheduledForUtc,
                $"{provider.ProviderName.ToUpperInvariant()}_API_SCHEDULE",
                $"{provider.ProviderName} API sync {scheduledForUtc:yyyy-MM-dd HHmm} UTC",
                cancellationToken);
        }
        finally
        {
            SynchronizationGate.Release();
        }
    }

    public async Task<EnterpriseQuoteSyncResult> RunManualAsync(
        string actor,
        CancellationToken cancellationToken)
    {
        if (!await SynchronizationGate.WaitAsync(0, cancellationToken))
            throw new EnterpriseQuoteSyncAlreadyRunningException();

        try
        {
            var requestedAt = DateTimeOffset.UtcNow;
            var provider = await ResolveProviderAsync(cancellationToken);
            var result = await RunAsync(
                provider,
                requestedAt,
                string.IsNullOrWhiteSpace(actor) ? "UNKNOWN_ADMIN" : actor.Trim(),
                $"{provider.ProviderName} API manual sync {requestedAt:yyyy-MM-dd HHmmss} UTC",
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "The manual sync could not be claimed because an identical sync run already exists.");
        }
        finally
        {
            SynchronizationGate.Release();
        }
    }

    private async Task<IEstimatingQuoteProvider> ResolveProviderAsync(
        CancellationToken cancellationToken)
    {
        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        return EnterpriseAdapterSelector.Select(
            providers,
            activeProvider,
            EnterpriseDataRoutes.EstimatingQuotes);
    }

    private async Task<EnterpriseQuoteSyncResult?> RunAsync(
        IEstimatingQuoteProvider provider,
        DateTimeOffset scheduledForUtc,
        string actor,
        string batchName,
        CancellationToken cancellationToken)
    {
        scheduledForUtc = scheduledForUtc.ToUniversalTime();
        var run = new FulcrumQuoteSyncRun
        {
            Id = Guid.NewGuid(),
            ScheduledForUtc = scheduledForUtc,
            StartedAt = DateTimeOffset.UtcNow,
            ProviderName = provider.ProviderName,
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
                    "The enterprise quote sync scheduled for {ScheduledForUtc} was already claimed by another process.",
                    scheduledForUtc);
                return null;
            }
            throw;
        }

        try
        {
            var pull = await provider.PullAsync(cancellationToken);
            foreach (var warning in pull.Warnings.Take(25))
                logger.LogWarning("{Provider} quote sync mapping warning: {Warning}", provider.ProviderName, warning);
            if (pull.Warnings.Count > 25)
                logger.LogWarning(
                    "{Provider} quote sync produced {AdditionalWarningCount} additional mapping warnings.",
                    provider.ProviderName,
                    pull.Warnings.Count - 25);

            var result = await importer.ApplyAutomatedAsync(
                pull.Rows,
                batchName,
                actor,
                cancellationToken);
            run.Status = FulcrumQuoteSyncStatuses.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.QuotesReceived = pull.RecordsReceived;
            run.NewRecords = result.NewRecords;
            run.UpdatedRecords = result.UpdatedRecords;
            run.UnchangedRecords = result.UnchangedRecords;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "{Provider} quote sync completed with {QuoteCount} quotes: {NewCount} new, {UpdatedCount} updated, and {UnchangedCount} unchanged.",
                provider.ProviderName,
                pull.RecordsReceived,
                result.NewRecords,
                result.UpdatedRecords,
                result.UnchangedRecords);
            return new EnterpriseQuoteSyncResult(
                run.Id,
                provider.ProviderName,
                run.StartedAt,
                run.CompletedAt.Value,
                pull.RecordsReceived,
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

internal sealed record EnterpriseQuoteSyncResult(
    Guid RunId,
    string ProviderName,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RecordsReceived,
    int NewRecords,
    int UpdatedRecords,
    int UnchangedRecords);

internal sealed class EnterpriseQuoteSyncAlreadyRunningException()
    : Exception("An enterprise quote synchronization is already running. Wait for it to finish before starting another pull.");

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

using System.Globalization;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Estimating;

namespace EstimatingDashboard.Api.Services;

public sealed partial class EstimatingQuoteWorkflowService
{
    public async Task<EstimatingQuoteDashboardDto> GetDashboardAsync(EstimatingAccessProfile access,
        string? scope, string? from, string? to, string? search, string? status,
        int page, int pageSize, CancellationToken ct, string? estimator = null)
    {
        if (!access.IsEnabled || !Has(access, EstimatingPermissions.QuotesView))
            throw new EstimatingQuoteWorkflowForbiddenException();
        var canViewTeam = Has(access, EstimatingPermissions.ViewHistory);
        if (!canViewTeam && estimator is not null)
            throw new EstimatingQuoteWorkflowForbiddenException();
        var selection = ValidateDashboard(scope, from, to, search, status, page, pageSize);
        ValidateEstimatorScope(selection.Scope, estimator, canViewTeam);

        // Identity ambiguity and the estimator roster use the whole history, never just the visible rows.
        var knownNames = (await db.QuoteHistory.AsNoTracking().Select(record => record.EstimatingRep).ToListAsync(ct))
            .Distinct(StringComparer.Ordinal).ToList();
        var estimators = canViewTeam ? PerformanceEstimators(knownNames) : [];
        var estimatorKey = ResolveEstimator(estimator, estimators);
        var query = db.QuoteHistory.AsNoTracking();
        if (selection.From is { } first)
            query = query.Where(record => record.RfqDueDate.HasValue && record.RfqDueDate.Value.Date >= first);
        if (selection.To is { } last)
            query = query.Where(record => record.RfqDueDate.HasValue && record.RfqDueDate.Value.Date <= last);
        var stored = await query.ToListAsync(ct);
        bool IsMine(EstimatingQuoteHistoryRecord record) => EstimatingEstimatorIdentity.MatchesUnambiguously(
            record.EstimatingRep, knownNames, access);
        var cohort = stored.Where(record => selection.Scope == "mine" ? IsMine(record)
            : estimatorKey is null || EstimatorSettings.NormalizeKey(record.EstimatingRep) == estimatorKey).ToList();
        var today = timeProvider.GetLocalNow().Date;
        var stats = await DashboardStatsAsync(cohort.Select(record => new DashboardStatsRecord(
            record.Id, record.EstimatingRep, record.IsCompleted, record.QuoteStatus, record.TotalValue,
            record.DateToEstimating, record.EstimatingCompletionDate, record.Workdays, record.RfqDueDate)).ToList(),
            today, ct);

        var parts = cohort.ToDictionary(record => record.Id, record => DashboardPartNumbers(record.FulcrumQuoteItemsJson));
        var numberSearch = selection.Search is { Length: > 1 } term && term[0] == '#' && term[1..].All(char.IsAsciiDigit)
            ? term[1..] : selection.Search;
        var filtered = cohort.Where(record => DashboardStatusMatches(record, selection.Status, today)
            && (selection.Search is null || record.QuoteNumber.ToString(CultureInfo.InvariantCulture)
                .Contains(numberSearch!, StringComparison.OrdinalIgnoreCase)
                || record.Customer.Contains(selection.Search, StringComparison.OrdinalIgnoreCase)
                || parts[record.Id].Any(part => part.Contains(selection.Search, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(record => record.IsCompleted)
            .ThenBy(record => record.IsCompleted ? DateTime.MaxValue : DashboardDueDate(record) ?? DateTime.MaxValue)
            .ThenByDescending(record => record.IsCompleted ? record.EstimatingCompletionDate ?? DateTime.MinValue : DateTime.MinValue)
            .ThenByDescending(record => record.QuoteNumber).ThenByDescending(record => record.Id).ToList();
        var displayNames = await DisplayNamesAsync(ct);
        var canManageTeam = Has(access, EstimatingPermissions.ManageHistory);
        var rows = filtered.Skip((selection.Page - 1) * selection.PageSize).Take(selection.PageSize).Select(record =>
        {
            var mine = IsMine(record);
            var privateWorkflow = mine || canManageTeam;
            var row = ToDto(record, privateWorkflow ? DisplayNameFor(record.ArdaStatusChangedBy, displayNames) : null, today);
            return row with
            {
                ArdaStatusNotes = privateWorkflow ? row.ArdaStatusNotes : null,
                ArdaStatusChangedBy = privateWorkflow ? row.ArdaStatusChangedBy : null,
                IsOverdue = IsDashboardOverdue(record, today),
                PartNumbers = parts[record.Id],
                CanOpenQuoteStatus = privateWorkflow && Has(access, EstimatingPermissions.ViewHistory)
                    && Has(access, EstimatingPermissions.QuoteStatusView),
                CanGenerateEstimate = mine && !record.IsCompleted && !access.IsPreview
                    && Has(access, EstimatingPermissions.ManageInputs) && Has(access, EstimatingPermissions.ManageQuotes)
            };
        }).ToList();
        return new(rows, filtered.Count, selection.Page, selection.PageSize, stats, estimators);
    }

    public async Task<EstimatingQuotePerformanceDto> GetPerformanceAsync(EstimatingAccessProfile access,
        string? scope, string? estimator, CancellationToken ct, string? month = null)
    {
        if (!access.IsEnabled || !Has(access, EstimatingPermissions.QuotesView))
            throw new EstimatingQuoteWorkflowForbiddenException();

        scope = NormalizeDashboardScope(scope);
        var canViewTeam = Has(access, EstimatingPermissions.ViewHistory);
        ValidateEstimatorScope(scope, estimator, canViewTeam);

        DateTime? performanceMonth = null;
        if (month is not null)
        {
            if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new EstimatingQuoteWorkflowValidationException("Use YYYY-MM for the performance month.");
            performanceMonth = parsed;
        }

        // Project only the fields needed for performance, leaving quote details and imported line JSON out.
        var records = await db.QuoteHistory.AsNoTracking().Select(record => new DashboardStatsRecord(
            record.Id, record.EstimatingRep, record.IsCompleted, record.QuoteStatus, record.TotalValue,
            record.DateToEstimating, record.EstimatingCompletionDate, record.Workdays, record.RfqDueDate)).ToListAsync(ct);
        var knownNames = records.Select(record => record.EstimatingRep).Distinct(StringComparer.Ordinal).ToList();
        var estimators = canViewTeam ? PerformanceEstimators(knownNames) : [];
        var estimatorKey = ResolveEstimator(estimator, estimators);

        // The roster and identity matching use all history, even when the metrics use only one RFQ due month.
        var period = performanceMonth is { } selectedMonth
            ? records.Where(record => record.RfqDueDate is { } due
                && due.Year == selectedMonth.Year && due.Month == selectedMonth.Month)
            : records;
        var cohort = scope == "mine"
            ? period.Where(record => EstimatingEstimatorIdentity.MatchesUnambiguously(record.EstimatingRep, knownNames, access))
            : period.Where(record => EstimatorSettings.IsEligible(record.EstimatingRep)
                && (estimatorKey is null || EstimatorSettings.NormalizeKey(record.EstimatingRep) == estimatorKey));
        var stats = await DashboardStatsAsync(cohort.ToList(), timeProvider.GetLocalNow().Date, ct);
        return new(stats, estimators);
    }

    private static void ValidateEstimatorScope(string scope, string? estimator, bool canViewTeam)
    {
        if (!canViewTeam && (scope == "all" || estimator is not null))
            throw new EstimatingQuoteWorkflowForbiddenException();
        if (estimator is not null && (scope != "all" || !EstimatorSettings.IsEligible(estimator)
            || estimator.Length > EstimatorSettings.NameMaxLength || estimator.Any(char.IsControl)))
            throw new EstimatingQuoteWorkflowValidationException("Choose an available estimator.");
    }

    private static string? ResolveEstimator(string? estimator, IReadOnlyList<EstimatingPerformanceEstimatorDto> estimators)
    {
        if (estimator is null) return null;
        var key = EstimatorSettings.NormalizeKey(estimator);
        return estimators.Any(option => option.Id == key) ? key
            : throw new EstimatingQuoteWorkflowValidationException("Choose an available estimator.");
    }

    private static List<EstimatingPerformanceEstimatorDto> PerformanceEstimators(IEnumerable<string> names) =>
        names.Where(EstimatorSettings.IsEligible).Select(name => name.Trim()).Order(StringComparer.Ordinal)
            .GroupBy(EstimatorSettings.NormalizeKey, StringComparer.Ordinal)
            .Select(group => new EstimatingPerformanceEstimatorDto(group.Key, group.First()))
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private sealed record DashboardStatsRecord(int Id, string EstimatingRep, bool IsCompleted,
        string QuoteStatus, decimal TotalValue, DateTime? DateToEstimating, DateTime? EstimatingCompletionDate,
        int? Workdays, DateTime? RfqDueDate);

    private async Task<EstimatingQuoteDashboardStatsDto> DashboardStatsAsync(
        IReadOnlyList<DashboardStatsRecord> records, DateTime today, CancellationToken ct)
    {
        var baseStats = await StatsForRecordsAsync(records.Select(record => new WorkflowStatsRecord(
            record.Id, record.EstimatingRep, record.IsCompleted, record.QuoteStatus, record.TotalValue,
            record.DateToEstimating, record.EstimatingCompletionDate, record.Workdays)).ToList(), ct);
        var measured = records.Where(record => record.IsCompleted && ValidOnTimeDates(record, today)).ToList();
        var onTime = measured.Count(record => record.EstimatingCompletionDate!.Value.Date <= record.RfqDueDate!.Value.Date);
        return new(baseStats.AverageCompletionWorkdays, baseStats.CompletionSampleSize, baseStats.WonCount,
            baseStats.LostCount, baseStats.CompletedCount, baseStats.WonTotalValue, baseStats.LostTotalValue,
            records.Count(record => !record.IsCompleted),
            records.Count(record => IsDashboardOverdue(record.IsCompleted, record.RfqDueDate, today)),
            onTime, measured.Count, measured.Count == 0 ? null : Math.Round(100m * onTime / measured.Count, 1),
            baseStats.CompletedCount - measured.Count);
    }

    private static bool Has(EstimatingAccessProfile access, string permission) =>
        access.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    private static DateTime? DashboardDueDate(EstimatingQuoteHistoryRecord record) =>
        record.RfqDueDate;

    private static bool IsDashboardOverdue(EstimatingQuoteHistoryRecord record, DateTime today) =>
        IsDashboardOverdue(record.IsCompleted, DashboardDueDate(record), today);

    private static bool IsDashboardOverdue(bool completed, DateTime? dueDate, DateTime today) =>
        !completed && dueDate is { } due && due.Date < today;

    private static bool ValidOnTimeDates(DashboardStatsRecord record, DateTime today) =>
        record.RfqDueDate is { } due && due.Date != DateTime.MinValue
        && record.EstimatingCompletionDate is { } completed && completed.Date != DateTime.MinValue && completed.Date <= today;

    private static bool DashboardStatusMatches(EstimatingQuoteHistoryRecord record, string status, DateTime today) => status switch
    {
        "all" => true,
        "active" => !record.IsCompleted,
        "overdue" => IsDashboardOverdue(record, today),
        "completed" => record.IsCompleted,
        _ => DisplayStatus(record.ArdaStatus).Equals(status, StringComparison.OrdinalIgnoreCase)
    };

    private sealed record DashboardSelection(string Scope, DateTime? From, DateTime? To, string? Search,
        string Status, int Page, int PageSize);

    private static DashboardSelection ValidateDashboard(string? scope, string? from, string? to,
        string? search, string? status, int page, int pageSize)
    {
        scope = NormalizeDashboardScope(scope);
        static DateTime? ParseDate(string? value)
        {
            if (value is null) return null;
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new EstimatingQuoteWorkflowValidationException("Use YYYY-MM-DD for dashboard date limits.");
            return parsed.Date;
        }
        var first = ParseDate(from); var last = ParseDate(to);
        if (first > last) throw new EstimatingQuoteWorkflowValidationException("The start date must be on or before the end date.");
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (search is { Length: > 200 } || search?.Any(char.IsControl) == true)
            throw new EstimatingQuoteWorkflowValidationException("Search cannot exceed 200 characters or contain control characters.");
        status = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim();
        var reserved = status.ToLowerInvariant();
        status = reserved is "all" or "active" or "overdue" or "completed" ? reserved
            : EstimatingArdaStatuses.Normalize(status)
                ?? throw new EstimatingQuoteWorkflowValidationException("Choose an available dashboard status.");
        return new(scope, first, last, search, status, Math.Clamp(page, 1, 1000000), Math.Clamp(pageSize, 1, 100));
    }

    private static string NormalizeDashboardScope(string? scope)
    {
        scope = string.IsNullOrWhiteSpace(scope) ? "mine" : scope.Trim().ToLowerInvariant();
        if (scope is not ("mine" or "all")) throw new EstimatingQuoteWorkflowValidationException("Choose mine or all for the quote scope.");
        return scope;
    }

    private static IReadOnlyList<string> DashboardPartNumbers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024) return [];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var number = item.EnumerateObject().FirstOrDefault(property =>
                    property.Name.Equals("PartNumber", StringComparison.OrdinalIgnoreCase)).Value;
                if (number.ValueKind != JsonValueKind.String) continue;
                var value = number.GetString()?.Trim();
                if (value is { Length: > 0 and <= 300 } && !value.Any(char.IsControl)) numbers.Add(value);
            }
            return numbers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
    }
}

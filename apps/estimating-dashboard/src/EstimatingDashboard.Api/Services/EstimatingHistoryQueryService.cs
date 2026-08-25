using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingHistoryQueryService(
    EstimatingAccessDbContext db,
    EstimatingEstimatorSettingsService estimatorSettings)
{
    public async Task<EstimatingHistoryPageDto> GetPageAsync(
        string? search,
        string? estimator,
        string? salesPerson,
        string? customer,
        string? quoteStatus,
        string? estimatingStatus,
        string? complexity,
        string? issues,
        string? quoteOnTrack,
        string? view,
        string? completion,
        string? onTime,
        DateTime? dueFrom,
        DateTime? dueTo,
        DateTime? completedFrom,
        DateTime? completedTo,
        decimal? minimumValue,
        decimal? maximumValue,
        string? sort,
        string? direction,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.QuoteHistory.AsNoTracking().AsQueryable();
        var term = Clean(search);
        if (term is not null)
        {
            var normalizedTerm = term.ToLower();
            var hasSearchDate = DateTime.TryParse(term, out var searchDate);
            var searchDateEnd = searchDate.Date.AddDays(1);
            query = query.Where(record =>
                record.SourceId.ToLower().Contains(normalizedTerm)
                || record.QuoteNumber.ToString().Contains(term)
                || record.Customer.ToLower().Contains(normalizedTerm)
                || (record.CustomerContact != null && record.CustomerContact.ToLower().Contains(normalizedTerm))
                || record.SalesPerson.ToLower().Contains(normalizedTerm)
                || record.QuoteStatus.ToLower().Contains(normalizedTerm)
                || record.EstimatingRep.ToLower().Contains(normalizedTerm)
                || (record.RfqReferenceNumber != null && record.RfqReferenceNumber.ToLower().Contains(normalizedTerm))
                || (record.Issues != null && record.Issues.ToLower().Contains(normalizedTerm))
                || (record.QuoteOnTrack != null && record.QuoteOnTrack.ToLower().Contains(normalizedTerm))
                || (record.QuoteComplexity != null && record.QuoteComplexity.ToLower().Contains(normalizedTerm))
                || record.NumberOfParts.ToString().Contains(term)
                || (record.EstimatingStatus != null && record.EstimatingStatus.ToLower().Contains(normalizedTerm))
                || record.OnTimeStatus.ToLower().Contains(normalizedTerm)
                || (record.Workdays != null && record.Workdays.Value.ToString().Contains(term))
                || (hasSearchDate && (
                    (record.RfqDueDate >= searchDate.Date && record.RfqDueDate < searchDateEnd)
                    || (record.DateToEstimating >= searchDate.Date && record.DateToEstimating < searchDateEnd))));
        }

        query = Exact(query, estimator, record => record.EstimatingRep);
        query = Exact(query, salesPerson, record => record.SalesPerson);
        query = Exact(query, customer, record => record.Customer);
        query = Exact(query, quoteStatus, record => record.QuoteStatus);
        query = Exact(query, estimatingStatus, record => record.EstimatingStatus!);
        query = Exact(query, complexity, record => record.QuoteComplexity!);
        query = Exact(query, issues, record => record.Issues!);
        query = Exact(query, quoteOnTrack, record => record.QuoteOnTrack!);

        if (string.Equals(view, "live", StringComparison.OrdinalIgnoreCase))
            query = query.Where(record => record.QuoteStatus == "Needs Approval");

        if (string.Equals(completion, "queue", StringComparison.OrdinalIgnoreCase))
            query = query.Where(record => !record.IsCompleted);
        else if (string.Equals(completion, "completed", StringComparison.OrdinalIgnoreCase))
            query = query.Where(record => record.IsCompleted);

        var onTimeValue = Clean(onTime);
        if (onTimeValue is not null)
            query = query.Where(record => record.OnTimeStatus == onTimeValue);
        if (dueFrom.HasValue)
            query = query.Where(record => record.RfqDueDate >= dueFrom.Value.Date);
        if (dueTo.HasValue)
        {
            var exclusiveEnd = dueTo.Value.Date.AddDays(1);
            query = query.Where(record => record.RfqDueDate < exclusiveEnd);
        }
        if (completedFrom.HasValue)
            query = query.Where(record => record.EstimatingCompletionDate >= completedFrom.Value.Date);
        if (completedTo.HasValue)
        {
            var exclusiveEnd = completedTo.Value.Date.AddDays(1);
            query = query.Where(record => record.EstimatingCompletionDate < exclusiveEnd);
        }
        if (minimumValue.HasValue)
            query = query.Where(record => record.TotalValue >= minimumValue.Value);
        if (maximumValue.HasValue)
            query = query.Where(record => record.TotalValue <= maximumValue.Value);

        var total = await query.CountAsync(cancellationToken);
        query = Order(query, sort, direction);
        var safePageSize = Math.Clamp(pageSize, 10, 200);
        var safePage = Math.Max(1, page);
        var records = await query
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(record => ToDto(record))
            .ToListAsync(cancellationToken);
        return new EstimatingHistoryPageDto(records, total, safePage, safePageSize);
    }

    public async Task<EstimatingHistoryFilterOptionsDto> GetFiltersAsync(CancellationToken cancellationToken)
    {
        var records = db.QuoteHistory.AsNoTracking();
        return new EstimatingHistoryFilterOptionsDto(
            await Values(records.Select(record => record.EstimatingRep), cancellationToken),
            await Values(records.Select(record => record.SalesPerson), cancellationToken),
            await Values(records.Select(record => record.Customer), cancellationToken),
            await Values(records.Select(record => record.QuoteStatus), cancellationToken));
    }

    public async Task<EstimatingHistoryDashboardDto> GetDashboardAsync(
        string? periodValue,
        EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        var records = await db.QuoteHistory.AsNoTracking().ToListAsync(cancellationToken);
        var activeEstimators = await estimatorSettings.GetActiveEstimatorNamesAsync(
            records.Select(record => record.EstimatingRep),
            cancellationToken);
        var currentEmployeeRecords = records
            .Where(record => activeEstimators.Contains(record.EstimatingRep.Trim()))
            .ToList();
        var tracked = currentEmployeeRecords
            .Where(record => IsTrackedEstimator(record.EstimatingRep))
            .ToList();
        var isTeamView = access.Permissions.Contains(
            EstimatingPermissions.ManageHistory,
            StringComparer.OrdinalIgnoreCase);
        if (!isTeamView)
            tracked = tracked.Where(record => IsCurrentEstimator(record.EstimatingRep, access)).ToList();
        var departmentRecords = isTeamView ? currentEmployeeRecords : tracked;

        var today = DateTime.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekEnd = weekStart.AddDays(7);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var period = EstimatingHistoryPeriods.Dashboard(periodValue, today);

        var users = tracked
            .GroupBy(record => record.EstimatingRep, StringComparer.OrdinalIgnoreCase)
            .Select(group => UserStats(group.Key, group, weekStart, weekEnd, monthStart, monthEnd, period))
            .OrderByDescending(user => user.InQueue)
            .ThenBy(user => user.Estimator, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var departmentCompletedInPeriod = CompletedInPeriod(departmentRecords, period);
        var department = new EstimatingHistoryDepartmentStatsDto(
            departmentRecords.Count(IsInQueue),
            CompletedBetween(departmentRecords, weekStart, weekEnd),
            CompletedBetween(departmentRecords, monthStart, monthEnd),
            departmentRecords.Count(record => record.IsCompleted),
            departmentRecords.Sum(record => record.TotalValue),
            departmentRecords.Where(record => record.IsCompleted).Sum(record => record.TotalValue),
            AverageWorkdays(departmentRecords),
            departmentCompletedInPeriod.Count,
            departmentCompletedInPeriod.Sum(record => record.TotalValue),
            departmentCompletedInPeriod.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.OnTime),
            departmentCompletedInPeriod.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.Late),
            AverageWorkdays(departmentCompletedInPeriod));
        return new EstimatingHistoryDashboardDto(
            DateTimeOffset.Now,
            period.Key,
            period.Label,
            period.Start,
            period.End,
            isTeamView,
            department,
            users);
    }

    public async Task<EstimatingQuoteAuditHistoryDto?> GetAuditHistoryAsync(
        int quoteHistoryId,
        CancellationToken cancellationToken)
    {
        var quote = await db.QuoteHistory
            .AsNoTracking()
            .Where(record => record.Id == quoteHistoryId)
            .Select(record => new { record.Id, record.QuoteNumber, record.Customer })
            .SingleOrDefaultAsync(cancellationToken);
        if (quote is null) return null;

        var rows = await db.QuoteHistoryAudits
            .AsNoTracking()
            .Where(audit => audit.QuoteHistoryId == quoteHistoryId)
            .ToListAsync(cancellationToken);
        var events = rows
            .OrderByDescending(audit => audit.ChangedAt)
            .ThenBy(audit => audit.Id)
            .GroupBy(audit => new
            {
                audit.ImportBatchId,
                audit.Action,
                audit.ChangedBy,
                audit.ChangedAt
            })
            .Select(group => new EstimatingQuoteAuditEventDto(
                group.Key.ImportBatchId,
                group.Key.Action,
                group.Key.ChangedBy,
                group.Key.ChangedAt,
                group.Select(audit => new EstimatingQuoteAuditChangeDto(
                    audit.FieldName,
                    audit.OldValue,
                    audit.NewValue)).ToList()))
            .ToList();
        return new EstimatingQuoteAuditHistoryDto(
            quote.Id,
            quote.QuoteNumber,
            quote.Customer,
            events);
    }

    private static EstimatingHistoryUserStatsDto UserStats(
        string estimator,
        IEnumerable<EstimatingQuoteHistoryRecord> source,
        DateTime weekStart,
        DateTime weekEnd,
        DateTime monthStart,
        DateTime monthEnd,
        EstimatingHistoryPeriod period)
    {
        var records = source.ToList();
        var completedInPeriod = CompletedInPeriod(records, period);
        return new EstimatingHistoryUserStatsDto(
            estimator,
            records.Count(IsInQueue),
            CompletedBetween(records, weekStart, weekEnd),
            CompletedBetween(records, monthStart, monthEnd),
            records.Count(record => record.IsCompleted),
            records.Sum(record => record.TotalValue),
            records.Where(record => record.IsCompleted).Sum(record => record.TotalValue),
            AverageWorkdays(records),
            completedInPeriod.Count,
            completedInPeriod.Sum(record => record.TotalValue),
            completedInPeriod.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.OnTime),
            completedInPeriod.Count(record => record.OnTimeStatus == EstimatingOnTimeStatuses.Late),
            AverageWorkdays(completedInPeriod));
    }

    private static List<EstimatingQuoteHistoryRecord> CompletedInPeriod(
        IEnumerable<EstimatingQuoteHistoryRecord> records,
        EstimatingHistoryPeriod period) => records
            .Where(record => EstimatingHistoryPeriods.Includes(record, period))
            .ToList();

    private static int CompletedBetween(
        IEnumerable<EstimatingQuoteHistoryRecord> records,
        DateTime start,
        DateTime end) => records.Count(record =>
            record.EstimatingCompletionDate >= start
            && record.EstimatingCompletionDate < end);

    private static double? AverageWorkdays(IEnumerable<EstimatingQuoteHistoryRecord> records)
    {
        var values = records
            .Where(record => record.IsCompleted && record.Workdays.HasValue && record.Workdays.Value >= 0)
            .Select(record => record.Workdays!.Value)
            .ToList();
        return values.Count == 0 ? null : Math.Round(values.Average(), 1);
    }

    private static bool IsTrackedEstimator(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "Unassigned", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "Sales", StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentEstimator(string estimator, EstimatingAccessProfile access)
    {
        var normalizedEstimator = estimator.Trim();
        var displayName = access.DisplayName.Trim();
        var accountName = access.AccountName.Split('\\').Last().Split('@').First();
        if (string.Equals(normalizedEstimator, displayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedEstimator, accountName, StringComparison.OrdinalIgnoreCase))
            return true;

        var displayFirstName = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var estimatorFirstName = normalizedEstimator.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return displayFirstName is not null
            && string.Equals(estimatorFirstName, displayFirstName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInQueue(EstimatingQuoteHistoryRecord record) =>
        string.Equals(record.QuoteStatus, "Needs Approval", StringComparison.OrdinalIgnoreCase);

    private static IQueryable<EstimatingQuoteHistoryRecord> Exact(
        IQueryable<EstimatingQuoteHistoryRecord> query,
        string? value,
        System.Linq.Expressions.Expression<Func<EstimatingQuoteHistoryRecord, string>> selector)
    {
        var clean = Clean(value);
        return clean is null ? query : query.Where(BuildEquals(selector, clean));
    }

    private static System.Linq.Expressions.Expression<Func<EstimatingQuoteHistoryRecord, bool>> BuildEquals(
        System.Linq.Expressions.Expression<Func<EstimatingQuoteHistoryRecord, string>> selector,
        string value)
    {
        var equals = System.Linq.Expressions.Expression.Equal(
            selector.Body,
            System.Linq.Expressions.Expression.Constant(value));
        return System.Linq.Expressions.Expression.Lambda<Func<EstimatingQuoteHistoryRecord, bool>>(equals, selector.Parameters);
    }

    private static async Task<IReadOnlyList<string>> Values(
        IQueryable<string> query,
        CancellationToken cancellationToken) => await query
            .Where(value => value != string.Empty)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync(cancellationToken);

    private static IQueryable<EstimatingQuoteHistoryRecord> Order(
        IQueryable<EstimatingQuoteHistoryRecord> query,
        string? sort,
        string? direction)
    {
        var descending = !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);
        return (sort?.ToLowerInvariant(), descending) switch
        {
            ("number", false) => query.OrderBy(record => record.QuoteNumber),
            ("number", true) => query.OrderByDescending(record => record.QuoteNumber),
            ("customer", false) => query.OrderBy(record => record.Customer).ThenByDescending(record => record.QuoteNumber),
            ("customer", true) => query.OrderByDescending(record => record.Customer).ThenByDescending(record => record.QuoteNumber),
            ("estimator", false) => query.OrderBy(record => record.EstimatingRep).ThenByDescending(record => record.QuoteNumber),
            ("estimator", true) => query.OrderByDescending(record => record.EstimatingRep).ThenByDescending(record => record.QuoteNumber),
            ("value", false) => query.OrderBy(record => (double)record.TotalValue),
            ("value", true) => query.OrderByDescending(record => (double)record.TotalValue),
            ("due", false) => query.OrderBy(record => record.RfqDueDate == null).ThenBy(record => record.RfqDueDate),
            ("due", true) => query.OrderByDescending(record => record.RfqDueDate),
            ("assigned", false) => query.OrderBy(record => record.DateToEstimating),
            ("assigned", true) => query.OrderByDescending(record => record.DateToEstimating),
            ("completed", false) => query.OrderBy(record => record.EstimatingCompletionDate),
            ("completed", true) => query.OrderByDescending(record => record.EstimatingCompletionDate),
            ("workdays", false) => query.OrderBy(record => record.Workdays),
            ("workdays", true) => query.OrderByDescending(record => record.Workdays),
            _ => query.OrderByDescending(record => record.QuoteNumber)
        };
    }

    private static EstimatingHistoryRowDto ToDto(EstimatingQuoteHistoryRecord record) => new(
        record.Id,
        record.SourceId,
        record.QuoteNumber,
        record.Customer,
        record.CustomerContact,
        record.SalesPerson,
        record.QuoteStatus,
        record.RfqReferenceNumber,
        record.EstimatingRep,
        record.TotalValue,
        record.RfqDueDate,
        record.DateToEstimating,
        record.Issues,
        record.QuoteOnTrack,
        record.QuoteComplexity,
        record.NumberOfParts,
        record.EstimatingStatus,
        record.EstimatingCompletionDate,
        record.OnTimeStatus,
        record.DaysLate,
        record.Workdays,
        record.CompletedMonth,
        record.CompletedYear,
        record.CompletedWeekOfMonth,
        record.CompletedMonthAndWeek,
        record.IsCompleted,
        record.CompletedWeekOfYear,
        record.IsOnTime,
        record.OnTimeRatio);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

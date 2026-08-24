using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingHistoryQueryService(EstimatingAccessDbContext db)
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
            query = query.Where(record =>
                record.SourceId.Contains(term)
                || record.QuoteNumber.ToString().Contains(term)
                || record.Customer.Contains(term)
                || (record.CustomerContact != null && record.CustomerContact.Contains(term))
                || record.SalesPerson.Contains(term)
                || record.EstimatingRep.Contains(term)
                || (record.RfqReferenceNumber != null && record.RfqReferenceNumber.Contains(term))
                || (record.Issues != null && record.Issues.Contains(term))
                || (record.QuoteOnTrack != null && record.QuoteOnTrack.Contains(term))
                || (record.EstimatingStatus != null && record.EstimatingStatus.Contains(term)));
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
            await Values(records.Select(record => record.QuoteStatus), cancellationToken),
            await Values(records.Where(record => record.EstimatingStatus != null).Select(record => record.EstimatingStatus!), cancellationToken),
            await Values(records.Where(record => record.QuoteComplexity != null).Select(record => record.QuoteComplexity!), cancellationToken),
            await Values(records.Where(record => record.Issues != null).Select(record => record.Issues!), cancellationToken),
            await Values(records.Where(record => record.QuoteOnTrack != null).Select(record => record.QuoteOnTrack!), cancellationToken));
    }

    public async Task<EstimatingHistoryDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var records = await db.QuoteHistory.AsNoTracking().ToListAsync(cancellationToken);
        var tracked = records.Where(record => IsTrackedEstimator(record.EstimatingRep)).ToList();
        var today = DateTime.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekEnd = weekStart.AddDays(7);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        var users = tracked
            .GroupBy(record => record.EstimatingRep, StringComparer.OrdinalIgnoreCase)
            .Select(group => UserStats(group.Key, group, weekStart, weekEnd, monthStart, monthEnd))
            .OrderByDescending(user => user.InQueue)
            .ThenBy(user => user.Estimator, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var department = new EstimatingHistoryDepartmentStatsDto(
            tracked.Count(IsInQueue),
            CompletedBetween(tracked, weekStart, weekEnd),
            CompletedBetween(tracked, monthStart, monthEnd),
            tracked.Count(record => record.IsCompleted),
            tracked.Sum(record => record.TotalValue),
            tracked.Where(record => record.IsCompleted).Sum(record => record.TotalValue),
            AverageWorkdays(tracked));
        return new EstimatingHistoryDashboardDto(DateTimeOffset.Now, department, users);
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
        DateTime monthEnd)
    {
        var records = source.ToList();
        return new EstimatingHistoryUserStatsDto(
            estimator,
            records.Count(IsInQueue),
            CompletedBetween(records, weekStart, weekEnd),
            CompletedBetween(records, monthStart, monthEnd),
            records.Count(record => record.IsCompleted),
            records.Sum(record => record.TotalValue),
            records.Where(record => record.IsCompleted).Sum(record => record.TotalValue),
            AverageWorkdays(records));
    }

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

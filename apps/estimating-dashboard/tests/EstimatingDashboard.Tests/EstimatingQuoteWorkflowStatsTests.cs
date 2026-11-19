using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Tests;

public sealed partial class EstimatingQuoteWorkflowServiceTests
{
    [Fact]
    public async Task Stats_are_scoped_to_the_current_estimator_and_report_partial_timing_coverage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var won = Quote(1201, "Casey Lee", completed: true);
        won.QuoteStatus = "WON";
        won.Workdays = 3;
        won.TotalValue = 12345.67m;
        var lost = Quote(1202, "Casey Lee", completed: true);
        lost.QuoteStatus = "lost";
        lost.Workdays = 4;
        lost.TotalValue = 4321.98m;
        var completedWithoutDuration = Quote(1203, "Casey Lee", completed: true);
        completedWithoutDuration.QuoteStatus = "Sent";
        var someoneElsesWin = Quote(1204, "Someone Else", completed: true);
        someoneElsesWin.QuoteStatus = "Won";
        someoneElsesWin.Workdays = 1;
        var secondWin = Quote(1206, "Casey Lee");
        secondWin.QuoteStatus = " Won ";
        secondWin.TotalValue = 100m;
        fixture.Db.QuoteHistory.AddRange(won, lost, completedWithoutDuration, someoneElsesWin, secondWin);
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Service.GetStatsAsync(Editor("Casey Lee"), default);

        Assert.Null(stats.AverageCompletionWorkdays);
        Assert.Equal(2, stats.CompletionSampleSize);
        Assert.Equal(3, stats.CompletedCount);
        Assert.Equal(2, stats.WonCount);
        Assert.Equal(1, stats.LostCount);
        Assert.Equal(12445.67m, stats.WonTotalValue);
        Assert.Equal(4321.98m, stats.LostTotalValue);
    }

    [Fact]
    public async Task Stats_return_no_average_when_completed_quotes_have_no_duration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var completed = Quote(1205, "Casey Lee", completed: true);
        completed.QuoteStatus = "Won";
        fixture.Db.QuoteHistory.Add(completed);
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Service.GetStatsAsync(Editor("Casey Lee"), default);

        Assert.Null(stats.AverageCompletionWorkdays);
        Assert.Equal(0, stats.CompletionSampleSize);
        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(1, stats.WonCount);
    }

    [Fact]
    public async Task Stats_average_includes_all_completed_quotes_when_every_duration_is_known()
    {
        await using var fixture = await Fixture.CreateAsync(new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero));
        var dated = Quote(1212, "Casey Lee", completed: true);
        dated.DateToEstimating = new DateTime(2026, 11, 13);
        dated.EstimatingCompletionDate = new DateTime(2026, 11, 16);
        var legacy = Quote(1213, "Casey Lee", completed: true);
        legacy.Workdays = 5;
        var active = Quote(1214, "Casey Lee");
        active.Workdays = 10;
        fixture.Db.QuoteHistory.AddRange(dated, legacy, active);
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Service.GetStatsAsync(Editor("Casey Lee"), default);

        Assert.Equal(3.5m, stats.AverageCompletionWorkdays);
        Assert.Equal(2, stats.CompletedCount);
        Assert.Equal(2, stats.CompletionSampleSize);
    }

    [Theory]
    [InlineData(13, 16, null, 2)]
    [InlineData(16, 16, 99, 1)]
    [InlineData(14, 14, null, 0)]
    public async Task Stats_recalculate_inclusive_business_days_from_authoritative_dates(
        int startDay, int endDay, int? storedWorkdays, int expectedWorkdays)
    {
        await using var fixture = await Fixture.CreateAsync(new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero));
        var record = Quote(1207, "Casey Lee", completed: true);
        record.DateToEstimating = new DateTime(2026, 11, startDay, 18, 0, 0);
        record.EstimatingCompletionDate = new DateTime(2026, 11, endDay, 9, 0, 0);
        record.Workdays = storedWorkdays;
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Service.GetStatsAsync(Editor("Casey Lee"), default);

        Assert.Equal((decimal)expectedWorkdays, stats.AverageCompletionWorkdays);
        Assert.Equal(1, stats.CompletionSampleSize);
        Assert.Equal(1, stats.CompletedCount);
    }

    [Fact]
    public async Task Stats_recover_current_source_dates_from_import_audit_when_legacy_projection_is_missing_them()
    {
        var now = new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
        await using var fixture = await Fixture.CreateAsync(now);
        var record = Quote(1208, "Casey Lee", completed: true);
        AddSourceAudit(record, EstimatingQuoteAuditFields.AssignmentDate, null, "2026-10-01", now.AddDays(-2));
        AddSourceAudit(record, EstimatingQuoteAuditFields.AssignmentDate, "2026-10-01", "2026-11-12", now.AddDays(-1));
        AddSourceAudit(record, EstimatingQuoteAuditFields.CompletionDate, null, "2026-11-17", now.AddDays(-1));
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Service.GetStatsAsync(Editor("Casey Lee"), default);

        Assert.Equal(4m, stats.AverageCompletionWorkdays);
        Assert.Equal(1, stats.CompletionSampleSize);
    }

    [Fact]
    public async Task Stats_exclude_cleared_dates_inverted_dates_and_workflow_timestamps()
    {
        var now = new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
        await using var fixture = await Fixture.CreateAsync(now);
        var cleared = Quote(1209, "Casey Lee", completed: true);
        cleared.Workdays = 7;
        cleared.DateToEstimating = new DateTime(2026, 11, 12);
        AddSourceAudit(cleared, EstimatingQuoteAuditFields.CompletionDate, null, "2026-11-17", now.AddDays(-1));
        AddSourceAudit(cleared, EstimatingQuoteAuditFields.CompletionDate, "2026-11-17", null, now);

        var inverted = Quote(1210, "Casey Lee", completed: true);
        inverted.DateToEstimating = new DateTime(2026, 11, 17);
        inverted.EstimatingCompletionDate = new DateTime(2026, 11, 12);
        inverted.Workdays = 5;

        var workflowOnly = Quote(1211, "Casey Lee", completed: true);
        workflowOnly.QuoteStatus = "Won";
        workflowOnly.RfqDueDate = new DateTime(2026, 11, 13);
        workflowOnly.FirstImportedAt = now.AddDays(-10);
        workflowOnly.UpdatedAt = now.AddDays(-1);
        workflowOnly.ArdaStatus = EstimatingArdaStatuses.Complete;
        workflowOnly.ArdaStatusChangedAt = now.AddDays(-1);
        workflowOnly.AuditHistory.Add(new EstimatingQuoteHistoryAuditRecord
        {
            QuoteHistory = workflowOnly, QuoteNumber = workflowOnly.QuoteNumber,
            Action = EstimatingQuoteAuditActions.WorkflowUpdated, FieldName = "Arda status",
            OldValue = EstimatingArdaStatuses.InProgress, NewValue = EstimatingArdaStatuses.Complete,
            ChangedBy = "SONAERO\\casey", ChangedAt = now.AddDays(-1)
        });
        fixture.Db.QuoteHistory.AddRange(cleared, inverted, workflowOnly);
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Service.GetStatsAsync(Editor("Casey Lee"), default);

        Assert.Null(stats.AverageCompletionWorkdays);
        Assert.Equal(0, stats.CompletionSampleSize);
        Assert.Equal(3, stats.CompletedCount);
    }

    private static void AddSourceAudit(EstimatingQuoteHistoryRecord record, string field, string? oldValue,
        string? newValue, DateTimeOffset changedAt) => record.AuditHistory.Add(new EstimatingQuoteHistoryAuditRecord
        {
            QuoteHistory = record, QuoteNumber = record.QuoteNumber, ImportBatchId = Guid.NewGuid(),
            Action = EstimatingQuoteAuditActions.Updated, FieldName = field, OldValue = oldValue, NewValue = newValue,
            ChangedBy = "provider", ChangedAt = changedAt
        });
}

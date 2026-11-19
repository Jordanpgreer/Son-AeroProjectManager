using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed partial class EstimatingQuoteWorkflowServiceTests
{
    private static readonly DateTimeOffset DashboardNow = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dashboard_period_is_inclusive_rfq_due_date_and_all_time_keeps_undated_records()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var first = DashboardQuote(2001, new(2026, 11, 1));
        var last = DashboardQuote(2002, new(2026, 11, 30, 23, 59, 59));
        var earlier = DashboardQuote(2003, new(2026, 10, 31));
        var later = DashboardQuote(2004, new(2026, 12, 1));
        var undated = DashboardQuote(2005, null);
        first.DateToEstimating = new(2026, 10, 1);
        last.EstimatingDueDateOverride = new(2026, 12, 5);
        fixture.Db.AddRange(first, last, earlier, later, undated);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var month = await Dashboard(fixture, from: "2026-11-01", to: "2026-11-30");
        Assert.Equal(new[] { 2001, 2002 }, month.Records.Select(row => row.QuoteNumber));
        Assert.Equal(2, month.Stats.ActiveCount);
        Assert.Equal(5, (await Dashboard(fixture)).Total);
        Assert.Equal(3, (await Dashboard(fixture, from: "2026-11-01")).Total);
        Assert.Equal(3, (await Dashboard(fixture, to: "2026-11-30")).Total);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Dashboard_mine_uses_global_unambiguous_identity_even_when_conflicting_name_is_outside_period()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        fixture.Db.AddRange(DashboardQuote(2010, new(2026, 11, 5), "Casey Lee"),
            DashboardQuote(2011, new(2026, 10, 5), "Casey Jones"),
            DashboardQuote(2012, new(2026, 11, 5), "Morgan"));
        await fixture.Db.SaveChangesAsync();
        var ambiguous = Editor("Casey") with { AccountName = "SONAERO\\clee" };
        Assert.Empty((await Dashboard(fixture, ambiguous, from: "2026-11-01", to: "2026-11-30")).Records);
        var exact = await Dashboard(fixture, Editor("Casey Lee"), from: "2026-11-01", to: "2026-11-30");
        Assert.Equal(2010, Assert.Single(exact.Records).QuoteNumber);
        Assert.Equal(2, (await Dashboard(fixture, ambiguous, scope: "all", from: "2026-11-01", to: "2026-11-30")).Total);
    }

    [Fact]
    public async Task Dashboard_all_metadata_redacts_other_workflow_notes_and_returns_exact_action_capabilities()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var own = DashboardQuote(2020, new(2026, 11, 20));
        var other = DashboardQuote(2021, new(2026, 11, 20), "Morgan");
        own.ArdaStatusNotes = "OWN-NOTES";
        other.ArdaStatusNotes = "PRIVATE-OTHER-NOTES";
        other.ArdaStatusChangedBy = "SONAERO\\restricted-actor";
        fixture.Db.AddRange(own, other);
        await fixture.Db.SaveChangesAsync();
        var editor = Editor("Casey Lee");

        var all = await Dashboard(fixture, editor, scope: "all");
        var ownRow = all.Records.Single(row => row.QuoteNumber == 2020);
        var otherRow = all.Records.Single(row => row.QuoteNumber == 2021);
        Assert.Equal("OWN-NOTES", ownRow.ArdaStatusNotes);
        Assert.True(ownRow.CanOpenQuoteStatus);
        Assert.True(ownRow.CanGenerateEstimate);
        Assert.Null(otherRow.ArdaStatusNotes);
        Assert.Null(otherRow.ArdaStatusChangedBy);
        Assert.False(otherRow.CanOpenQuoteStatus);
        Assert.False(otherRow.CanGenerateEstimate);
        Assert.DoesNotContain("PRIVATE-OTHER-NOTES", JsonSerializer.Serialize(all));
        Assert.DoesNotContain("restricted-actor", JsonSerializer.Serialize(all));

        var manager = editor with { Role = EstimatingRoles.Admin };
        var team = (await Dashboard(fixture, manager, scope: "all")).Records.Single(row => row.Id == other.Id);
        Assert.Equal("PRIVATE-OTHER-NOTES", team.ArdaStatusNotes);
        Assert.True(team.CanOpenQuoteStatus);
        Assert.False(team.CanGenerateEstimate);
        Assert.All((await Dashboard(fixture, editor with { IsPreview = true }, scope: "all")).Records,
            row => Assert.False(row.CanGenerateEstimate));
        var pageOnly = editor with { GrantedPermissions = [EstimatingPermissions.QuotesView] };
        var mine = Assert.Single((await Dashboard(fixture, pageOnly)).Records);
        Assert.False(mine.CanOpenQuoteStatus);
        Assert.False(mine.CanGenerateEstimate);
        var viewer = editor with { Role = EstimatingRoles.Viewer };
        Assert.False(Assert.Single((await Dashboard(fixture, viewer)).Records).CanGenerateEstimate);
    }

    [Theory]
    [InlineData("mine", false, false, false)]
    [InlineData("mine", true, true, false)]
    [InlineData("all", true, false, false)]
    [InlineData("all", true, false, true)]
    public async Task Dashboard_rejects_missing_scope_permissions_or_disabled_accounts(
        string scope, bool enabled, bool noQuotesPermission, bool manageOnly)
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var permissions = new List<string>();
        if (!noQuotesPermission) permissions.Add(EstimatingPermissions.QuotesView);
        if (manageOnly) permissions.Add(EstimatingPermissions.ManageHistory);
        var access = Editor("Casey Lee") with { IsEnabled = enabled, GrantedPermissions = permissions };
        await Assert.ThrowsAsync<EstimatingDashboard.Api.Services.EstimatingQuoteWorkflowForbiddenException>(() =>
            Dashboard(fixture, access, scope: scope));
    }

    [Fact]
    public async Task Dashboard_searches_imported_parts_customer_and_quote_number_without_changing_cohort_stats()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var one = DashboardQuote(2030, new(2026, 11, 20));
        one.Customer = "Aero Customer";
        one.FulcrumQuoteItemsJson = """[{"PartNumber":"PN-ABC"},{"partNumber":"pn-abc"},null,{"PartNumber":" "},{"PartNumber":42}]""";
        var two = DashboardQuote(2031, new(2026, 11, 21));
        two.FulcrumQuoteItemsJson = "not-json";
        var completed = DashboardQuote(2032, new(2026, 11, 16));
        completed.IsCompleted = true; completed.EstimatingCompletionDate = new(2026, 11, 16); completed.Workdays = 4;
        completed.QuoteStatus = "Won";
        fixture.Db.AddRange(one, two, completed);
        await fixture.Db.SaveChangesAsync();
        var baseline = await Dashboard(fixture);
        foreach (var search in new[] { "pn-aBc", "aero", "2030", "#2030" })
        {
            var result = await Dashboard(fixture, search: search, status: "active", pageSize: 1);
            Assert.Equal(2030, Assert.Single(result.Records).QuoteNumber);
            Assert.Equal(new[] { "PN-ABC" }, result.Records[0].PartNumbers);
            Assert.Equal(1, result.Total);
            Assert.Equal(baseline.Stats, result.Stats);
        }
        Assert.Empty((await Dashboard(fixture, search: "2031")).Records[0].PartNumbers);
        var paged = await Dashboard(fixture, page: 2, pageSize: 1);
        Assert.Equal(3, paged.Total);
        Assert.Equal(2031, Assert.Single(paged.Records).QuoteNumber);
        Assert.Equal(baseline.Stats, paged.Stats);
        Assert.Equal(baseline.Stats, (await Dashboard(fixture, status: "completed")).Stats);
    }

    [Fact]
    public async Task Dashboard_part_search_includes_later_imported_lines_without_stripping_part_number_hashes()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var quote = DashboardQuote(2033, new(2026, 11, 20));
        quote.FulcrumQuoteItemsJson = JsonSerializer.Serialize(Enumerable.Range(1, 250)
            .Select(number => new { PartNumber = "#PART-" + number }).ToArray());
        fixture.Db.Add(quote); await fixture.Db.SaveChangesAsync();
        var result = await Dashboard(fixture, search: "#PART-250");
        Assert.Equal(2033, Assert.Single(result.Records).QuoteNumber);
        Assert.Equal(250, result.Records[0].PartNumbers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_rejects_oversized_or_control_character_searches(bool control)
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        await Assert.ThrowsAsync<EstimatingDashboard.Api.Services.EstimatingQuoteWorkflowValidationException>(() =>
            Dashboard(fixture, search: control ? "part\nnumber" : new string('x', 201)));
    }

    [Fact]
    public async Task Dashboard_on_time_excludes_unverifiable_completion_and_uses_date_only_rfq_due()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var onDue = DashboardQuote(2040, new(2026, 11, 16, 1, 0, 0));
        onDue.EstimatingCompletionDate = new(2026, 11, 16, 23, 0, 0);
        var late = DashboardQuote(2041, new(2026, 11, 16)); late.EstimatingCompletionDate = new(2026, 11, 17);
        var missing = DashboardQuote(2042, new(2026, 11, 16));
        var future = DashboardQuote(2043, new(2026, 11, 16)); future.EstimatingCompletionDate = new(2026, 11, 20);
        var invalid = DashboardQuote(2044, new(2026, 11, 16)); invalid.EstimatingCompletionDate = DateTime.MinValue;
        var inverted = DashboardQuote(2045, new(2026, 11, 16));
        inverted.DateToEstimating = new(2026, 11, 18); inverted.EstimatingCompletionDate = new(2026, 11, 17);
        var noDue = DashboardQuote(2046, null); noDue.EstimatingCompletionDate = new(2026, 11, 16);
        var active = DashboardQuote(2047, new(2026, 11, 18));
        active.EstimatingCompletionDate = new(2026, 11, 17);
        foreach (var record in new[] { onDue, late, missing, future, invalid, inverted, noDue }) record.IsCompleted = true;
        fixture.Db.AddRange(onDue, late, missing, future, invalid, inverted, noDue, active);
        await fixture.Db.SaveChangesAsync();

        var result = await Dashboard(fixture);
        Assert.Equal(7, result.Stats.CompletedCount);
        Assert.Equal(1, result.Stats.OnTimeCount);
        Assert.Equal(3, result.Stats.OnTimeSampleSize);
        Assert.Equal(33.3m, result.Stats.OnTimePercentage);
        Assert.Equal(4, result.Stats.OnTimeMissingCount);
        Assert.Equal(1, result.Stats.ActiveCount);
        Assert.Equal(1, result.Stats.OverdueCount);
        Assert.All(result.Records.Where(row => row.IsCompleted), row => Assert.False(row.IsOverdue));
    }

    [Fact]
    public async Task Dashboard_on_time_uses_completion_and_rfq_dates_independently_of_invalid_assignment_duration()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var quote = DashboardQuote(2048, new(2026, 11, 17));
        quote.IsCompleted = true;
        quote.DateToEstimating = new(2026, 11, 18);
        quote.EstimatingCompletionDate = new(2026, 11, 17);
        fixture.Db.Add(quote); await fixture.Db.SaveChangesAsync();
        var stats = (await Dashboard(fixture)).Stats;
        Assert.Equal(1, stats.OnTimeCount);
        Assert.Equal(1, stats.OnTimeSampleSize);
        Assert.Equal(100m, stats.OnTimePercentage);
        Assert.Equal(0, stats.OnTimeMissingCount);
        Assert.Null(stats.AverageCompletionWorkdays);
        Assert.Equal(0, stats.CompletionSampleSize);
    }

    [Fact]
    public async Task Dashboard_overdue_is_customer_rfq_deadline_and_does_not_change_existing_personal_semantics()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var dueToday = DashboardQuote(2050, new(2026, 11, 19));
        dueToday.EstimatingDueDateOverride = new(2026, 11, 1);
        var rfqLate = DashboardQuote(2051, new(2026, 11, 18));
        rfqLate.EstimatingDueDateOverride = new(2026, 11, 30);
        fixture.Db.AddRange(dueToday, rfqLate);
        await fixture.Db.SaveChangesAsync();
        var late = await Dashboard(fixture, status: "overdue");
        Assert.Equal(2051, Assert.Single(late.Records).QuoteNumber);
        Assert.True(late.Records[0].IsOverdue);
        Assert.Equal(1, late.Stats.OverdueCount);
        var legacy = await fixture.Service.GetMineAsync(Editor("Casey Lee"), default);
        Assert.True(legacy.Single(row => row.QuoteNumber == 2050).IsOverdue);
        Assert.False(legacy.Single(row => row.QuoteNumber == 2051).IsOverdue);
    }

    [Fact]
    public async Task Dashboard_preserves_verified_average_audit_recovery_and_all_completed_coverage_rule()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var completed = DashboardQuote(2060, new(2026, 11, 18)); completed.IsCompleted = true;
        AddSourceAudit(completed, EstimatingQuoteAuditFields.AssignmentDate, null, "2026-11-12", DashboardNow.AddDays(-1));
        AddSourceAudit(completed, EstimatingQuoteAuditFields.CompletionDate, null, "2026-11-17", DashboardNow.AddDays(-1));
        fixture.Db.Add(completed); await fixture.Db.SaveChangesAsync();
        var stats = (await Dashboard(fixture)).Stats;
        Assert.Equal(4m, stats.AverageCompletionWorkdays);
        Assert.Equal(1, stats.CompletionSampleSize);
        Assert.Null(stats.OnTimePercentage);
        Assert.Equal(1, stats.OnTimeMissingCount);
        var unknown = DashboardQuote(2061, new(2026, 11, 18)); unknown.IsCompleted = true;
        fixture.Db.Add(unknown); await fixture.Db.SaveChangesAsync();
        var partial = (await Dashboard(fixture)).Stats;
        Assert.Null(partial.AverageCompletionWorkdays);
        Assert.Equal(1, partial.CompletionSampleSize);
        Assert.Equal(2, partial.CompletedCount);
    }

    [Fact]
    public async Task Dashboard_bounds_pagination_accepts_arda_status_and_empty_stats_are_unknown()
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        var quote = DashboardQuote(2070, new(2026, 11, 18)); quote.ArdaStatus = EstimatingArdaStatuses.InProgress;
        fixture.Db.Add(quote); await fixture.Db.SaveChangesAsync();
        var found = await Dashboard(fixture, status: "IN PROGRESS", page: -5, pageSize: 1000000);
        Assert.Single(found.Records); Assert.Equal(1, found.Page); Assert.Equal(100, found.PageSize);
        var beyond = await Dashboard(fixture, page: int.MaxValue, pageSize: int.MinValue);
        Assert.Empty(beyond.Records); Assert.Equal(1000000, beyond.Page); Assert.Equal(1, beyond.PageSize);
        var empty = await Dashboard(fixture, from: "2027-01-01");
        Assert.Equal(0, empty.Total); Assert.Null(empty.Stats.OnTimePercentage); Assert.Null(empty.Stats.AverageCompletionWorkdays);
    }

    [Theory]
    [InlineData("2026-2-01", null, null, null)]
    [InlineData("2026-11-20", "2026-11-01", null, null)]
    [InlineData("2026-02-30", null, null, null)]
    [InlineData(null, "2026-11-01T00:00:00", null, null)]
    [InlineData(null, null, "unknown", null)]
    [InlineData(null, null, null, "pending")]
    public async Task Dashboard_rejects_invalid_date_scope_or_status(string? from, string? to, string? scope, string? status)
    {
        await using var fixture = await Fixture.CreateAsync(DashboardNow);
        await Assert.ThrowsAsync<EstimatingDashboard.Api.Services.EstimatingQuoteWorkflowValidationException>(() =>
            Dashboard(fixture, from: from, to: to, scope: scope, status: status));
    }

    private static EstimatingQuoteHistoryRecord DashboardQuote(int number, DateTime? due, string estimator = "Casey Lee")
    { var record = Quote(number, estimator); record.RfqDueDate = due; return record; }

    private static Task<EstimatingQuoteDashboardDto> Dashboard(Fixture fixture, EstimatingAccessProfile? access = null,
        string? scope = "mine", string? from = null, string? to = null, string? search = null, string? status = "all",
        int page = 1, int pageSize = 25) => fixture.Service.GetDashboardAsync(access ?? Editor("Casey Lee"),
            scope, from, to, search, status, page, pageSize, default);
}

using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ScheduleCalculatorTests
{
    private readonly ScheduleCalculator calculator = new();

    [Fact]
    public void AddWorkingDaysInclusive_SkipsFridayThroughSunday()
    {
        var start = new DateOnly(2026, 6, 25); // Thursday
        var result = calculator.AddWorkingDaysInclusive(start, 2, ScheduleCalendar.Default);

        Assert.Equal(new DateOnly(2026, 6, 29), result); // Monday
    }

    [Fact]
    public void AddWorkingDaysInclusive_SkipsCompanyHoliday()
    {
        var start = new DateOnly(2026, 6, 25); // Thursday
        var holidays = new HashSet<DateOnly> { new(2026, 6, 29) };

        var calendar = new ScheduleCalendar(ScheduleCalendar.Default.WorkingDays, holidays);
        var result = calculator.AddWorkingDaysInclusive(start, 2, calendar);

        Assert.Equal(new DateOnly(2026, 6, 30), result);
    }

    [Fact]
    public void CalculateTaskStatus_MarksLateTaskBehind()
    {
        var task = new ProjectTask
        {
            Title = "FA inspection",
            StartDate = new DateOnly(2026, 6, 22),
            EndDate = new DateOnly(2026, 6, 25),
            PercentComplete = 0.25m
        };

        var status = calculator.CalculateTaskStatus(task, ScheduleCalendar.Default, new DateOnly(2026, 6, 25));

        Assert.Equal(TaskScheduleStatus.Behind, status);
    }

    [Fact]
    public void CalculateTaskStatus_MarksFullyCompleteTaskComplete()
    {
        var task = new ProjectTask
        {
            Title = "Tooling design",
            StartDate = new DateOnly(2026, 6, 22),
            EndDate = new DateOnly(2026, 6, 25),
            PercentComplete = 1m
        };

        var status = calculator.CalculateTaskStatus(task, ScheduleCalendar.Default, new DateOnly(2026, 6, 23));

        Assert.Equal(TaskScheduleStatus.Complete, status);
    }

    [Fact]
    public void AddWorkingDaysInclusive_UsesConfiguredFridayWorkday()
    {
        var calendar = new ScheduleCalendar(
            new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            new HashSet<DateOnly>());

        var result = calculator.AddWorkingDaysInclusive(new DateOnly(2026, 6, 25), 2, calendar);

        Assert.Equal(new DateOnly(2026, 6, 26), result);
    }

    [Fact]
    public void AddWorkingDaysInclusive_UsesTaskOvertimeOnNonWorkingDay()
    {
        var overtime = new HashSet<DateOnly> { new(2026, 6, 26) };

        var result = calculator.AddWorkingDaysInclusive(new DateOnly(2026, 6, 25), 2, ScheduleCalendar.Default, overtime);

        Assert.Equal(new DateOnly(2026, 6, 26), result);
    }

    [Fact]
    public void CountWorkingDays_ReturnsZeroWhenEndPrecedesStart()
    {
        var result = calculator.CountWorkingDays(
            new DateOnly(2026, 6, 25),
            new DateOnly(2026, 6, 24),
            ScheduleCalendar.Default);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CountWorkingDays_ReturnsZeroForNonWorkingOnlyRange()
    {
        var result = calculator.CountWorkingDays(
            new DateOnly(2026, 6, 26),
            new DateOnly(2026, 6, 28),
            ScheduleCalendar.Default);

        Assert.Equal(0, result);
    }
}

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
        var result = calculator.AddWorkingDaysInclusive(start, 2, new HashSet<DateOnly>());

        Assert.Equal(new DateOnly(2026, 6, 29), result); // Monday
    }

    [Fact]
    public void AddWorkingDaysInclusive_SkipsCompanyHoliday()
    {
        var start = new DateOnly(2026, 6, 25); // Thursday
        var holidays = new HashSet<DateOnly> { new(2026, 6, 29) };

        var result = calculator.AddWorkingDaysInclusive(start, 2, holidays);

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

        var status = calculator.CalculateTaskStatus(task, new HashSet<DateOnly>(), new DateOnly(2026, 6, 25));

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

        var status = calculator.CalculateTaskStatus(task, new HashSet<DateOnly>(), new DateOnly(2026, 6, 23));

        Assert.Equal(TaskScheduleStatus.Complete, status);
    }
}


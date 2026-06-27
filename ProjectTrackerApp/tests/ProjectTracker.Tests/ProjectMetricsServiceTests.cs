using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectMetricsServiceTests
{
    private readonly ProjectMetricsService metrics = new(new ScheduleCalculator());

    [Fact]
    public void RefreshProject_RecalculatesRemainingDatesAfterOperationDeletion()
    {
        var project = new Project
        {
            ProgramName = "Test project",
            ProgramStart = new DateOnly(2026, 6, 22),
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Remaining operation",
                    StartDate = new DateOnly(2026, 6, 23),
                    EndDate = new DateOnly(2026, 6, 24),
                    EstimatedDuration = 2
                }
            ]
        };

        metrics.RefreshProject(
            project,
            ScheduleCalendar.Default,
            new DateOnly(2026, 6, 20),
            recalculateDates: true);

        Assert.Equal(new DateOnly(2026, 6, 22), project.Tasks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 6, 23), project.Tasks[0].EndDate);
        Assert.Equal(2, project.Tasks[0].EstimatedDuration);
        Assert.Equal(new DateOnly(2026, 6, 23), project.TargetDelivery);
    }
}

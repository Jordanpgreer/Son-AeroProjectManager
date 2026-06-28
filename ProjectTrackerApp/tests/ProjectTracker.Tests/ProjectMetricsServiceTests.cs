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

    [Fact]
    public void RefreshProject_UsesDependencyEndDateWhenDependencyIsSelected()
    {
        var project = new Project
        {
            ProgramName = "Dependent project",
            ProgramStart = new DateOnly(2026, 6, 22),
            Tasks =
            [
                new ProjectTask { Id = 1, Sequence = 1, Title = "Op 1", EstimatedDuration = 2 },
                new ProjectTask { Id = 2, Sequence = 2, Title = "Op 2", EstimatedDuration = 2 },
                new ProjectTask { Id = 3, Sequence = 3, Title = "Op 3", EstimatedDuration = 2 },
                new ProjectTask { Id = 4, Sequence = 4, Title = "Op 4", EstimatedDuration = 4 },
                new ProjectTask { Id = 5, Sequence = 5, Title = "Op 5", DependencyTaskId = 3, EstimatedDuration = 2 }
            ]
        };

        metrics.RefreshProject(
            project,
            ScheduleCalendar.Default,
            new DateOnly(2026, 6, 20),
            recalculateDates: true);

        var operation3 = project.Tasks.Single(task => task.Id == 3);
        var operation4 = project.Tasks.Single(task => task.Id == 4);
        var operation5 = project.Tasks.Single(task => task.Id == 5);

        Assert.NotEqual(operation4.EndDate!.Value.AddDays(1), operation5.StartDate);
        Assert.Equal(operation3.EndDate!.Value.AddDays(1), operation5.StartDate);
    }
}

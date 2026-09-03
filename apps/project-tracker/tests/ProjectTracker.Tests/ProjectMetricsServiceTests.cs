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
    public void RefreshProject_PreservesExternallySynchronizedTaskDatesAndDurations()
    {
        var project = new Project
        {
            ProgramName = "Fulcrum progress",
            ProgramStart = new DateOnly(2026, 8, 25),
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Completed operation",
                    StartDate = new DateOnly(2026, 9, 1),
                    StartDateLocked = true,
                    OriginalStartDate = new DateOnly(2026, 8, 25),
                    EndDate = new DateOnly(2026, 9, 5),
                    OriginalEndDate = new DateOnly(2026, 9, 3),
                    EstimatedDuration = 3,
                    PercentComplete = 1m,
                    PercentCompleteManual = true
                }
            ]
        };

        metrics.RefreshProject(
            project,
            ScheduleCalendar.Default,
            new DateOnly(2026, 9, 6),
            recalculateDates: true,
            preserveTaskSchedule: true);

        var operation = Assert.Single(project.Tasks);
        Assert.Equal(new DateOnly(2026, 9, 1), operation.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 5), operation.EndDate);
        Assert.Equal(new DateOnly(2026, 8, 25), operation.OriginalStartDate);
        Assert.Equal(new DateOnly(2026, 9, 3), operation.OriginalEndDate);
        Assert.Equal(3, operation.EstimatedDuration);
        Assert.Equal(new DateOnly(2026, 9, 5), project.TargetDelivery);
        Assert.Equal(1m, project.Progress);
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

    [Fact]
    public void RefreshProject_PreservesArchivedProjectWithoutOperations()
    {
        var completedOn = new DateOnly(2026, 6, 25);
        var project = new Project
        {
            ProgramName = "Archived empty project",
            CompletedOn = completedOn,
            Status = ProjectStatus.Complete,
            Progress = 1m
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 6, 29));

        Assert.Equal(ProjectStatus.Complete, project.Status);
        Assert.Equal(1m, project.Progress);
        Assert.Equal(completedOn, project.CompletedOn);
        Assert.Equal("Program Complete", project.CurrentTask);
    }

    [Fact]
    public void RefreshProject_PreservesCompletedLateStatusForArchivedOperation()
    {
        var project = new Project
        {
            ProgramName = "Completed late project",
            CompletedOn = new DateOnly(2026, 8, 17),
            Status = ProjectStatus.Complete,
            Progress = 1m,
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Late operation",
                    EstimatedDuration = 5,
                    ActualDuration = 4,
                    PercentComplete = 1m,
                    Status = TaskScheduleStatus.Complete
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 8, 19));

        Assert.Equal(ProjectStatus.Complete, project.Status);
        Assert.Equal(TaskScheduleStatus.CompletedLate, project.Tasks[0].Status);
    }

    [Fact]
    public void RefreshProject_DoesNotCompleteOperationBecauseItsEndDatePassed()
    {
        var finalDate = new DateOnly(2026, 6, 23);
        var project = new Project
        {
            ProgramName = "Automatically completed project",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Final operation",
                    StartDate = new DateOnly(2026, 6, 22),
                    EndDate = finalDate,
                    EstimatedDuration = 2
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 6, 29));

        Assert.Equal(0m, project.Tasks[0].PercentComplete);
        Assert.Equal(TaskScheduleStatus.NotStarted, project.Tasks[0].Status);
        Assert.Equal(ProjectStatus.NotStarted, project.Status);
        Assert.Null(project.DaysBehind);
        Assert.Null(project.CompletedOn);
    }

    [Fact]
    public void RefreshProject_KeepsFullyReportedProjectActiveUntilItIsFormallyClosed()
    {
        var project = new Project
        {
            ProgramName = "Ready to close",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Final operation",
                    StartDate = new DateOnly(2026, 6, 22),
                    EndDate = new DateOnly(2026, 6, 23),
                    EstimatedDuration = 2,
                    PercentComplete = 1m,
                    PercentCompleteManual = true
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 6, 29));

        Assert.Equal(1m, project.Progress);
        Assert.Equal(ProjectStatus.OnTrack, project.Status);
        Assert.Equal("Ready to close", project.CurrentTask);
        Assert.Null(project.CompletedOn);
    }

    [Fact]
    public void RefreshProject_IncludesZeroDurationOperationsInProgress()
    {
        var project = new Project
        {
            ProgramName = "Milestone project",
            Tasks =
            [
                new ProjectTask { Sequence = 1, Title = "Production", EstimatedDuration = 4, PercentComplete = 1m },
                new ProjectTask { Sequence = 2, Title = "Final approval", EstimatedDuration = 0, PercentComplete = 0m }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 6, 20));

        Assert.Equal(0.8m, project.Progress);
    }

    [Fact]
    public void RefreshProject_DoesNotAdvanceVersionsWhenComputedValuesAreUnchanged()
    {
        var project = new Project
        {
            ProgramName = "Stable project",
            ProgramStart = new DateOnly(2026, 7, 6),
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Stable operation",
                    EstimatedDuration = 2
                }
            ]
        };
        var today = new DateOnly(2026, 7, 1);

        metrics.RefreshProject(project, ScheduleCalendar.Default, today, recalculateDates: true);
        var projectVersion = project.Version;
        var taskVersion = project.Tasks[0].Version;

        metrics.RefreshProject(project, ScheduleCalendar.Default, today, recalculateDates: true);

        Assert.Equal(projectVersion, project.Version);
        Assert.Equal(taskVersion, project.Tasks[0].Version);
    }

    [Fact]
    public void RefreshProject_DoesNotMarkProjectBehindFromProgressLagAlone()
    {
        var project = new Project
        {
            ProgramName = "Lagging project",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Lagging operation",
                    StartDate = new DateOnly(2026, 7, 6),
                    StartDateLocked = true,
                    EndDate = new DateOnly(2026, 7, 16),
                    EstimatedDuration = 8,
                    PercentComplete = 0.25m,
                    PercentCompleteManual = true
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 7, 9));

        Assert.Equal(TaskScheduleStatus.OnTrack, project.Tasks[0].Status);
        Assert.Equal(ProjectStatus.OnTrack, project.Status);
        Assert.Null(project.DaysBehind);
    }

    [Fact]
    public void RefreshProject_DoesNotMarkProjectBehindFromProjectedDelayAlone()
    {
        var project = new Project
        {
            ProgramName = "Overdue project",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Overdue operation",
                    StartDate = new DateOnly(2026, 7, 6),
                    StartDateLocked = true,
                    EndDate = new DateOnly(2026, 7, 9),
                    EstimatedDuration = 4,
                    PercentComplete = 0.5m,
                    PercentCompleteManual = true
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 7, 14));

        Assert.Equal(TaskScheduleStatus.OnTrack, project.Tasks[0].Status);
        Assert.Equal(ProjectStatus.OnTrack, project.Status);
        Assert.Null(project.DaysBehind);
    }

    [Fact]
    public void RefreshProject_AutomaticallyAdvancesConfirmedOperationByWorkingDay()
    {
        var project = new Project
        {
            ProgramName = "Automatic progress",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Build",
                    StartDate = new DateOnly(2026, 8, 10),
                    StartDateLocked = true,
                    EndDate = new DateOnly(2026, 8, 13),
                    EstimatedDuration = 4,
                    PercentCompleteManual = false
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 8, 11));

        Assert.Equal(0.5m, project.Tasks[0].PercentComplete);
        Assert.False(project.Tasks[0].PercentCompleteManual);
        Assert.Equal(TaskScheduleStatus.OnTrack, project.Tasks[0].Status);
    }

    [Fact]
    public void RefreshProject_CapsAutomaticProgressUntilFinishIsConfirmed()
    {
        var project = new Project
        {
            ProgramName = "Awaiting finish",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Build",
                    StartDate = new DateOnly(2026, 8, 10),
                    StartDateLocked = true,
                    EndDate = new DateOnly(2026, 8, 13),
                    EstimatedDuration = 4,
                    PercentCompleteManual = false
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 8, 17));

        Assert.Equal(0.99m, project.Tasks[0].PercentComplete);
        Assert.Equal(TaskScheduleStatus.OnTrack, project.Tasks[0].Status);
        Assert.NotEqual("Ready to close", project.CurrentTask);
    }

    [Fact]
    public void RefreshProject_UsesOnlyFinalOperationEndVarianceForProjectBehindStatus()
    {
        var project = new Project
        {
            ProgramName = "Final operation controls project",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Late intermediate operation",
                    StartDate = new DateOnly(2026, 8, 3),
                    EndDate = new DateOnly(2026, 8, 10),
                    OriginalStartDate = new DateOnly(2026, 8, 3),
                    OriginalEndDate = new DateOnly(2026, 8, 6),
                    EstimatedDuration = 5,
                    ActualDuration = 4,
                    PercentComplete = 1m
                },
                new ProjectTask
                {
                    Sequence = 2,
                    Title = "On-time final operation",
                    StartDate = new DateOnly(2026, 8, 11),
                    EndDate = new DateOnly(2026, 8, 13),
                    OriginalStartDate = new DateOnly(2026, 8, 10),
                    OriginalEndDate = new DateOnly(2026, 8, 13),
                    EstimatedDuration = 3,
                    ActualDuration = 4,
                    PercentComplete = 0.5m
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 8, 12));

        Assert.Equal(TaskScheduleStatus.CompletedLate, project.Tasks[0].Status);
        Assert.Equal(ProjectStatus.OnTrack, project.Status);
        Assert.Null(project.DaysBehind);
    }

    [Fact]
    public void RefreshProject_MarksProjectBehindWhenFinalOperationEndsAfterOriginalEnd()
    {
        var project = new Project
        {
            ProgramName = "Late final operation",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Final operation",
                    StartDate = new DateOnly(2026, 8, 10),
                    EndDate = new DateOnly(2026, 8, 17),
                    OriginalStartDate = new DateOnly(2026, 8, 10),
                    OriginalEndDate = new DateOnly(2026, 8, 13),
                    EstimatedDuration = 5,
                    ActualDuration = 4,
                    PercentComplete = 1m
                }
            ]
        };

        metrics.RefreshProject(project, ScheduleCalendar.Default, new DateOnly(2026, 8, 17));

        Assert.Equal(TaskScheduleStatus.CompletedLate, project.Tasks[0].Status);
        Assert.Equal(ProjectStatus.Behind, project.Status);
        Assert.Equal(1, project.DaysBehind);
        Assert.Equal("Ready to close", project.CurrentTask);
    }
}

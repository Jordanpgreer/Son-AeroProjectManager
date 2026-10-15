using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class OperationDependencyPolicyTests
{
    [Fact]
    public void DetachDateConflicts_removes_the_edited_operations_blocking_dependency()
    {
        var dependency = Task(1, 1, "Cut", "2026-10-01", "2026-10-10");
        var operation = Task(2, 2, "Weld", "2026-10-11", "2026-10-12", dependency.Id);
        var project = Project(dependency, operation);
        var proposed = Proposal(operation, startDate: "2026-10-08", endDate: "2026-10-12");

        var conflicts = OperationDependencyPolicy.FindDateConflicts(project, operation, proposed);
        var effective = OperationDependencyPolicy.DetachDateConflicts(
            operation,
            proposed,
            conflicts,
            DateTimeOffset.Parse("2026-10-15T12:00:00Z"));

        var conflict = Assert.Single(conflicts);
        Assert.Same(operation, conflict.Dependent);
        Assert.Same(dependency, conflict.Dependency);
        Assert.Null(effective.DependencyTaskId);
        Assert.True(effective.StartDateLocked);
        Assert.Equal(dependency.Id, operation.DependencyTaskId);
    }

    [Fact]
    public void DetachDateConflicts_removes_only_downstream_links_blocked_by_the_new_end_date()
    {
        var operation = Task(1, 1, "Cut", "2026-10-01", "2026-10-03");
        var blocked = Task(2, 2, "Weld", "2026-10-05", "2026-10-06", operation.Id);
        var unaffected = Task(3, 3, "Inspect", "2026-10-20", "2026-10-21", operation.Id);
        var project = Project(operation, blocked, unaffected);
        var proposed = Proposal(operation, startDate: "2026-10-01", endDate: "2026-10-10");
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

        var conflicts = OperationDependencyPolicy.FindDateConflicts(project, operation, proposed);
        OperationDependencyPolicy.DetachDateConflicts(operation, proposed, conflicts, now);

        var conflict = Assert.Single(conflicts);
        Assert.Same(blocked, conflict.Dependent);
        Assert.Null(blocked.DependencyTaskId);
        Assert.True(blocked.StartDateLocked);
        Assert.Equal(2, blocked.Version);
        Assert.Equal(now, blocked.UpdatedAt);
        Assert.Equal(operation.Id, unaffected.DependencyTaskId);
        Assert.False(unaffected.StartDateLocked);
        Assert.Equal(1, unaffected.Version);
    }

    [Fact]
    public void FindDateConflicts_allows_dates_that_remain_on_the_valid_side_of_dependencies()
    {
        var dependency = Task(1, 1, "Cut", "2026-10-01", "2026-10-03");
        var operation = Task(2, 2, "Weld", "2026-10-04", "2026-10-06", dependency.Id);
        var dependent = Task(3, 3, "Inspect", "2026-10-10", "2026-10-11", operation.Id);
        var project = Project(dependency, operation, dependent);
        var proposed = Proposal(operation, startDate: "2026-10-04", endDate: "2026-10-09");

        var conflicts = OperationDependencyPolicy.FindDateConflicts(project, operation, proposed);

        Assert.Empty(conflicts);
    }

    private static ProjectTask Task(
        int id,
        int sequence,
        string title,
        string startDate,
        string endDate,
        int? dependencyTaskId = null) => new()
        {
            Id = id,
            Sequence = sequence,
            Title = title,
            StartDate = DateOnly.Parse(startDate),
            EndDate = DateOnly.Parse(endDate),
            DependencyTaskId = dependencyTaskId
        };

    private static Project Project(params ProjectTask[] tasks)
    {
        var project = new Project { Id = 1, ProgramName = "Dependency policy test", Tasks = [.. tasks] };
        foreach (var task in tasks)
        {
            task.Project = project;
            task.ProjectId = project.Id;
        }
        return project;
    }

    private static TaskUpsertDto Proposal(ProjectTask task, string startDate, string endDate) => new(
        task.Sequence,
        task.ExternalTaskId,
        task.Title,
        task.Phase,
        task.WorkStation,
        task.DependencyTaskId,
        DateOnly.Parse(startDate),
        task.StartDateLocked,
        task.OriginalStartDate,
        DateOnly.Parse(endDate),
        task.OriginalEndDate,
        task.EstimatedDuration,
        task.ActualDuration,
        task.PercentComplete,
        task.PercentCompleteManual,
        task.Notes,
        [],
        task.Version,
        task.Project.Version);
}

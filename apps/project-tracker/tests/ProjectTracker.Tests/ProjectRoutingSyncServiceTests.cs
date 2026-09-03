using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectRoutingSyncServiceTests
{
    private readonly ProjectRoutingSyncService service = new();

    [Fact]
    public void Normal_pull_preserves_every_existing_operation_when_any_operation_has_a_name()
    {
        var project = ProjectWith(
            new ProjectTask
            {
                Id = 10,
                ProjectId = 42,
                Sequence = 1,
                ExternalTaskId = "1",
                Title = "Manually Renamed Cut",
                Notes = "Keep this operator note",
                Version = 3
            },
            new ProjectTask
            {
                Id = 20,
                ProjectId = 42,
                Sequence = 2,
                ExternalTaskId = "2",
                Title = "Arda Final Review",
                Version = 4
            });

        var result = service.Apply(
            project,
            [
                new ProjectRoutingStepSnapshot("fulcrum-cut", 10, "Cut"),
                new ProjectRoutingStepSnapshot("fulcrum-weld", 20, "Weld")
            ],
            "Fulcrum",
            DateTimeOffset.UtcNow);

        Assert.True(result.PreservedExisting);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal("Manually Renamed Cut", project.Tasks[0].Title);
        Assert.Equal("Keep this operator note", project.Tasks[0].Notes);
        Assert.Null(project.Tasks[0].ExternalSourceOperationId);
        Assert.Equal(3, project.Tasks[0].Version);
        Assert.Equal("Arda Final Review", project.Tasks[1].Title);
        Assert.Equal(4, project.Tasks[1].Version);
    }

    [Fact]
    public void Normal_pull_updates_actual_dates_and_completion_without_changing_original_schedule_or_manual_partial_progress()
    {
        var now = DateTimeOffset.Parse("2026-09-06T17:00:00Z");
        var cut = new ProjectTask
        {
            Id = 10,
            ProjectId = 42,
            Sequence = 1,
            ExternalTaskId = "1",
            ExternalSourceProvider = "Fulcrum",
            ExternalSourceOperationId = "fulcrum-cut",
            Title = "Manually Renamed Cut",
            Notes = "Keep this operator note",
            StartDate = new DateOnly(2026, 8, 30),
            OriginalStartDate = new DateOnly(2026, 8, 25),
            EndDate = new DateOnly(2026, 9, 10),
            OriginalEndDate = new DateOnly(2026, 9, 8),
            PercentComplete = 0.4m,
            PercentCompleteManual = true,
            Version = 3
        };
        var inspection = new ProjectTask
        {
            Id = 20,
            ProjectId = 42,
            Sequence = 2,
            ExternalTaskId = "2",
            Title = "Final Inspection",
            StartDate = new DateOnly(2026, 9, 5),
            OriginalStartDate = new DateOnly(2026, 9, 4),
            EndDate = new DateOnly(2026, 9, 12),
            OriginalEndDate = new DateOnly(2026, 9, 11),
            PercentComplete = 0.25m,
            PercentCompleteManual = true,
            Version = 4
        };
        var project = ProjectWith(cut, inspection);
        ProjectRoutingStepSnapshot[] progress =
        [
            new(
                "fulcrum-cut",
                10,
                "Cut",
                new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 5),
                IsComplete: true),
            new(
                "fulcrum-inspect",
                20,
                "Final Inspection",
                new DateOnly(2026, 9, 6))
        ];

        var result = service.Apply(project, progress, "Fulcrum", now);

        Assert.True(result.PreservedExisting);
        Assert.Equal(2, result.ProgressUpdated);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Warnings);
        Assert.Equal("Manually Renamed Cut", cut.Title);
        Assert.Equal("Keep this operator note", cut.Notes);
        Assert.Equal(new DateOnly(2026, 9, 1), cut.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 5), cut.EndDate);
        Assert.Equal(new DateOnly(2026, 8, 25), cut.OriginalStartDate);
        Assert.Equal(new DateOnly(2026, 9, 8), cut.OriginalEndDate);
        Assert.Equal(1m, cut.PercentComplete);
        Assert.True(cut.PercentCompleteManual);
        Assert.True(cut.StartDateLocked);
        Assert.Equal(4, cut.Version);
        Assert.Equal(now, cut.UpdatedAt);
        Assert.Equal("Final Inspection", inspection.Title);
        Assert.Equal("fulcrum-inspect", inspection.ExternalSourceOperationId);
        Assert.Equal(new DateOnly(2026, 9, 6), inspection.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 12), inspection.EndDate);
        Assert.Equal(new DateOnly(2026, 9, 4), inspection.OriginalStartDate);
        Assert.Equal(new DateOnly(2026, 9, 11), inspection.OriginalEndDate);
        Assert.Equal(0.25m, inspection.PercentComplete);
        Assert.Equal(5, inspection.Version);

        var secondResult = service.Apply(project, progress, "Fulcrum", now.AddHours(1));

        Assert.Equal(0, secondResult.ProgressUpdated);
        Assert.Equal(4, cut.Version);
        Assert.Equal(5, inspection.Version);
    }

    [Fact]
    public void Normal_pull_populates_routing_when_all_existing_operations_are_blank()
    {
        var now = DateTimeOffset.Parse("2026-09-02T17:00:00Z");
        var project = ProjectWith(new ProjectTask
        {
            Id = 10,
            ProjectId = 42,
            Sequence = 1,
            ExternalTaskId = "1",
            Title = " ",
            Notes = "Keep this placeholder note",
            Version = 3
        });

        var result = service.Apply(
            project,
            [
                new ProjectRoutingStepSnapshot("fulcrum-weld", 20, "Weld"),
                new ProjectRoutingStepSnapshot("fulcrum-cut", 10, "Cut")
            ],
            "Fulcrum",
            now);

        Assert.False(result.PreservedExisting);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Collection(
            project.Tasks.OrderBy(task => task.Sequence),
            task =>
            {
                Assert.Equal(10, task.Id);
                Assert.Equal("Cut", task.Title);
                Assert.Equal("Keep this placeholder note", task.Notes);
                Assert.Equal("fulcrum-cut", task.ExternalSourceOperationId);
            },
            task =>
            {
                Assert.Equal("Weld", task.Title);
                Assert.Equal("fulcrum-weld", task.ExternalSourceOperationId);
            });
    }

    [Fact]
    public void Force_override_applies_the_exact_Fulcrum_route_and_removes_manual_only_operations()
    {
        var project = ProjectWith(
            new ProjectTask
            {
                Id = 10,
                ProjectId = 42,
                Sequence = 1,
                ExternalTaskId = "1",
                ExternalSourceProvider = "Fulcrum",
                ExternalSourceOperationId = "fulcrum-cut",
                Title = "Manually Renamed Cut",
                Notes = "Keep this operator note",
                Version = 3
            },
            new ProjectTask
            {
                Id = 20,
                ProjectId = 42,
                Sequence = 2,
                ExternalTaskId = "2",
                Title = "Arda Final Review",
                Version = 4
            });

        var result = service.Apply(
            project,
            [
                new ProjectRoutingStepSnapshot("fulcrum-cut", 10, "Cut"),
                new ProjectRoutingStepSnapshot("fulcrum-weld", 20, "Weld")
            ],
            "Fulcrum",
            DateTimeOffset.UtcNow,
            ProjectRoutingSyncMode.ForceOverride);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.False(result.PreservedExisting);
        Assert.Single(result.RemovedTasks, task => task.Id == 20);
        Assert.DoesNotContain(project.Tasks, task => task.Id == 20);
        Assert.Collection(
            project.Tasks.OrderBy(task => task.Sequence),
            task =>
            {
                Assert.Equal("Cut", task.Title);
                Assert.Equal("Keep this operator note", task.Notes);
            },
            task => Assert.Equal("Weld", task.Title));
    }

    [Fact]
    public void Force_override_is_idempotent_after_source_operations_are_linked()
    {
        var project = ProjectWith(new ProjectTask
        {
            Id = 10,
            ProjectId = 42,
            Sequence = 1,
            ExternalTaskId = "1",
            ExternalSourceProvider = "Fulcrum",
            ExternalSourceOperationId = "fulcrum-cut",
            Title = "Cut",
            Version = 3
        });

        var result = service.Apply(
            project,
            [new ProjectRoutingStepSnapshot("fulcrum-cut", 10, "Cut")],
            "Fulcrum",
            DateTimeOffset.UtcNow,
            ProjectRoutingSyncMode.ForceOverride);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(3, project.Tasks[0].Version);
    }

    private static Project ProjectWith(params ProjectTask[] tasks) => new()
    {
        Id = 42,
        Tasks = tasks.ToList()
    };
}

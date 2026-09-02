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

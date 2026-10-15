using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed record OperationDateDependencyLink(
    ProjectTask Dependent,
    ProjectTask Dependency,
    string Reason);

public static class OperationDependencyPolicy
{
    public static IReadOnlyList<OperationDateDependencyLink> FindDateConflicts(
        Project project,
        ProjectTask operation,
        TaskUpsertDto proposed)
    {
        var conflicts = new List<OperationDateDependencyLink>();

        if (proposed.DependencyTaskId is { } dependencyId
            && proposed.StartDate is { } proposedStart
            && project.Tasks.FirstOrDefault(candidate => candidate.Id == dependencyId) is { EndDate: { } dependencyEnd } dependency
            && proposedStart <= dependencyEnd)
        {
            conflicts.Add(new OperationDateDependencyLink(
                operation,
                dependency,
                $"Operation {operation.Sequence} would start on {proposedStart:yyyy-MM-dd} before dependency operation {dependency.Sequence} finishes on {dependencyEnd:yyyy-MM-dd}."));
        }

        if (operation.Id > 0 && proposed.EndDate is { } proposedEnd)
        {
            foreach (var dependent in project.Tasks
                .Where(candidate => candidate.DependencyTaskId == operation.Id)
                .OrderBy(candidate => candidate.Sequence))
            {
                if (dependent.StartDate is not { } dependentStart || dependentStart > proposedEnd)
                    continue;

                conflicts.Add(new OperationDateDependencyLink(
                    dependent,
                    operation,
                    $"Operation {dependent.Sequence} currently starts on {dependentStart:yyyy-MM-dd}, before operation {operation.Sequence} would finish on {proposedEnd:yyyy-MM-dd}."));
            }
        }

        return conflicts;
    }

    public static TaskUpsertDto DetachDateConflicts(
        ProjectTask operation,
        TaskUpsertDto proposed,
        IReadOnlyCollection<OperationDateDependencyLink> conflicts,
        DateTimeOffset now)
    {
        var detachOperationDependency = conflicts.Any(conflict => ReferenceEquals(conflict.Dependent, operation));
        var effective = detachOperationDependency
            ? proposed with
            {
                DependencyTaskId = null,
                StartDateLocked = proposed.StartDate is not null || proposed.StartDateLocked
            }
            : proposed;

        foreach (var dependent in conflicts
            .Select(conflict => conflict.Dependent)
            .Where(dependent => !ReferenceEquals(dependent, operation))
            .DistinctBy(dependent => dependent.Id))
        {
            dependent.DependencyTaskId = null;
            dependent.DependencyTask = null;
            if (dependent.StartDate is not null) dependent.StartDateLocked = true;
            dependent.Version++;
            dependent.UpdatedAt = now;
        }

        return effective;
    }
}

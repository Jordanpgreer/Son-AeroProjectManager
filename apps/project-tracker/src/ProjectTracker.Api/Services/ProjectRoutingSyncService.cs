using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed record ProjectRoutingSyncResult(
    int Added,
    int Updated,
    int ProgressUpdated,
    int ArdaOnlyRetained,
    int Removed,
    bool PreservedExisting,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ProjectTask> RemovedTasks);

public enum ProjectRoutingSyncMode
{
    PopulateWhenBlank,
    ForceOverride
}

public sealed class ProjectRoutingSyncService
{
    public ProjectRoutingSyncResult Apply(
        Project project,
        IReadOnlyList<ProjectRoutingStepSnapshot> routingSteps,
        string provider,
        DateTimeOffset now,
        ProjectRoutingSyncMode mode = ProjectRoutingSyncMode.PopulateWhenBlank)
    {
        if (routingSteps.Count == 0)
            return EmptyResult();

        var sourceSteps = routingSteps
            .Select((step, index) => new { Step = step, Index = index })
            .Where(record => !string.IsNullOrWhiteSpace(record.Step.ExternalId)
                && !string.IsNullOrWhiteSpace(record.Step.Name))
            .OrderBy(record => record.Step.Sequence)
            .ThenBy(record => record.Index)
            .Select(record => record.Step)
            .DistinctBy(step => step.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceSteps.Count == 0)
            return EmptyResult();

        var existingInOrder = project.Tasks
            .OrderBy(task => task.Sequence)
            .ThenBy(task => task.Id)
            .ToList();
        var hasMeaningfulOperations = existingInOrder.Any(task => !string.IsNullOrWhiteSpace(task.Title));
        if (mode == ProjectRoutingSyncMode.PopulateWhenBlank && hasMeaningfulOperations)
            return ApplyProgressOnly(project, sourceSteps, provider, now);

        var unmatched = new HashSet<ProjectTask>(existingInOrder);
        var ordered = new List<ProjectTask>(sourceSteps.Count + existingInOrder.Count);
        var addedTasks = new HashSet<ProjectTask>();
        var changedTasks = new HashSet<ProjectTask>();
        var updatedRoutingTasks = new HashSet<ProjectTask>();
        var progressUpdated = 0;
        var providerName = provider.Trim();

        for (var index = 0; index < sourceSteps.Count; index++)
        {
            var step = sourceSteps[index];
            var targetSequence = index + 1;
            var task = FindExisting(
                unmatched,
                step,
                providerName,
                targetSequence,
                allowBlankFallback: !hasMeaningfulOperations);
            if (task is null)
            {
                task = new ProjectTask
                {
                    ProjectId = project.Id,
                    Project = project,
                    Title = step.Name.Trim(),
                    ExternalSourceProvider = providerName,
                    ExternalSourceOperationId = step.ExternalId.Trim(),
                    Sequence = targetSequence,
                    ExternalTaskId = targetSequence.ToString(),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                project.Tasks.Add(task);
                addedTasks.Add(task);
            }
            else
            {
                unmatched.Remove(task);
                if (SetRoutingValues(task, step, providerName, targetSequence))
                {
                    changedTasks.Add(task);
                    updatedRoutingTasks.Add(task);
                }
            }

            if (SetProgressValues(task, step))
            {
                progressUpdated++;
                if (!addedTasks.Contains(task)) changedTasks.Add(task);
            }

            ordered.Add(task);
        }

        var unmatchedInOrder = existingInOrder.Where(unmatched.Contains).ToList();
        var removedTasks = mode == ProjectRoutingSyncMode.ForceOverride || !hasMeaningfulOperations
            ? unmatchedInOrder
            : [];
        var retained = removedTasks.Count == 0 ? unmatchedInOrder : [];
        ordered.AddRange(retained);
        foreach (var removedTask in removedTasks) project.Tasks.Remove(removedTask);
        for (var index = 0; index < ordered.Count; index++)
        {
            var task = ordered[index];
            var sequence = index + 1;
            var operationId = sequence.ToString();
            if (task.Sequence == sequence
                && string.Equals(task.ExternalTaskId, operationId, StringComparison.Ordinal))
                continue;

            task.Sequence = sequence;
            task.ExternalTaskId = operationId;
            if (!addedTasks.Contains(task)) changedTasks.Add(task);
        }

        var clearedDependencies = 0;
        var taskById = ordered
            .Where(task => task.Id > 0)
            .ToDictionary(task => task.Id);
        foreach (var task in ordered.Where(task => task.DependencyTaskId is not null))
        {
            if (taskById.TryGetValue(task.DependencyTaskId!.Value, out var dependency)
                && dependency.Sequence < task.Sequence)
                continue;

            task.DependencyTaskId = null;
            if (!addedTasks.Contains(task)) changedTasks.Add(task);
            if (!unmatched.Contains(task)) updatedRoutingTasks.Add(task);
            clearedDependencies++;
        }

        foreach (var task in changedTasks)
        {
            task.Version++;
            task.UpdatedAt = now;
        }

        IReadOnlyList<string> warnings = clearedDependencies == 0
            ? []
            : [$"Cleared {clearedDependencies} operation dependenc{(clearedDependencies == 1 ? "y" : "ies")} that no longer pointed to an earlier operation after the Fulcrum sequence was applied."];
        return new ProjectRoutingSyncResult(
            addedTasks.Count,
            updatedRoutingTasks.Count,
            progressUpdated,
            retained.Count,
            removedTasks.Count,
            false,
            warnings,
            removedTasks);
    }

    private static ProjectRoutingSyncResult ApplyProgressOnly(
        Project project,
        IReadOnlyList<ProjectRoutingStepSnapshot> sourceSteps,
        string provider,
        DateTimeOffset now)
    {
        var unmatched = new HashSet<ProjectTask>(project.Tasks);
        var providerName = provider.Trim();
        var progressUpdated = 0;
        var unmatchedProgress = 0;

        for (var index = 0; index < sourceSteps.Count; index++)
        {
            var step = sourceSteps[index];
            if (!HasProgress(step)) continue;

            var task = FindExisting(
                unmatched,
                step,
                providerName,
                index + 1,
                allowBlankFallback: false);
            if (task is null)
            {
                unmatchedProgress++;
                continue;
            }

            unmatched.Remove(task);
            var sourceChanged = SetSourceIdentity(task, step, providerName);
            var progressChanged = SetProgressValues(task, step);
            if (!sourceChanged && !progressChanged) continue;

            task.Version++;
            task.UpdatedAt = now;
            progressUpdated++;
        }

        IReadOnlyList<string> warnings = unmatchedProgress == 0
            ? []
            : [$"Could not match {unmatchedProgress} Fulcrum operation progress record{(unmatchedProgress == 1 ? string.Empty : "s")} to the existing project operations; operation names and order were preserved."];
        return new ProjectRoutingSyncResult(
            0,
            0,
            progressUpdated,
            0,
            0,
            true,
            warnings,
            []);
    }

    private static ProjectTask? FindExisting(
        IEnumerable<ProjectTask> candidates,
        ProjectRoutingStepSnapshot step,
        string provider,
        int targetSequence,
        bool allowBlankFallback)
    {
        var bySource = candidates.FirstOrDefault(task =>
            string.Equals(task.ExternalSourceProvider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(task.ExternalSourceOperationId, step.ExternalId, StringComparison.OrdinalIgnoreCase));
        if (bySource is not null) return bySource;

        var normalizedName = NormalizeName(step.Name);
        var byName = candidates
                .Where(task => NormalizeName(task.Title) == normalizedName)
                .OrderBy(task => task.Sequence == targetSequence ? 0 : 1)
                .ThenBy(task => Math.Abs(task.Sequence - targetSequence))
                .ThenBy(task => task.Id)
                .FirstOrDefault();
        if (byName is not null || !allowBlankFallback) return byName;

        return candidates
            .Where(task => string.IsNullOrWhiteSpace(task.Title))
            .OrderBy(task => task.Sequence == targetSequence ? 0 : 1)
            .ThenBy(task => Math.Abs(task.Sequence - targetSequence))
            .ThenBy(task => task.Id)
            .FirstOrDefault();
    }

    private static bool SetRoutingValues(
        ProjectTask task,
        ProjectRoutingStepSnapshot step,
        string provider,
        int targetSequence)
    {
        var title = step.Name.Trim();
        var sourceId = step.ExternalId.Trim();
        var operationId = targetSequence.ToString();
        var changed = !string.Equals(task.Title, title, StringComparison.Ordinal)
            || !string.Equals(task.ExternalSourceProvider, provider, StringComparison.Ordinal)
            || !string.Equals(task.ExternalSourceOperationId, sourceId, StringComparison.Ordinal)
            || task.Sequence != targetSequence
            || !string.Equals(task.ExternalTaskId, operationId, StringComparison.Ordinal);
        task.Title = title;
        task.ExternalSourceProvider = provider;
        task.ExternalSourceOperationId = sourceId;
        task.Sequence = targetSequence;
        task.ExternalTaskId = operationId;
        return changed;
    }

    private static bool SetSourceIdentity(
        ProjectTask task,
        ProjectRoutingStepSnapshot step,
        string provider)
    {
        var sourceId = step.ExternalId.Trim();
        var changed = !string.Equals(task.ExternalSourceProvider, provider, StringComparison.Ordinal)
            || !string.Equals(task.ExternalSourceOperationId, sourceId, StringComparison.Ordinal);
        task.ExternalSourceProvider = provider;
        task.ExternalSourceOperationId = sourceId;
        return changed;
    }

    private static bool SetProgressValues(ProjectTask task, ProjectRoutingStepSnapshot step)
    {
        var changed = false;
        if (step.ActualStartDate is { } actualStart)
        {
            changed = task.StartDate != actualStart || !task.StartDateLocked || changed;
            task.StartDate = actualStart;
            task.StartDateLocked = true;
        }

        if (!step.IsComplete) return changed;

        changed = task.PercentComplete != 1m || !task.PercentCompleteManual || changed;
        task.PercentComplete = 1m;
        task.PercentCompleteManual = true;
        if (step.ActualCompletionDate is { } actualCompletion)
        {
            changed = task.EndDate != actualCompletion || changed;
            task.EndDate = actualCompletion;
        }

        return changed;
    }

    private static bool HasProgress(ProjectRoutingStepSnapshot step) =>
        step.ActualStartDate is not null
        || step.ActualCompletionDate is not null
        || step.IsComplete;

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private static ProjectRoutingSyncResult EmptyResult() =>
        new(0, 0, 0, 0, 0, false, [], []);
}

using System.Globalization;
using System.Text.Json;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed record ProjectAuditChange(string Field, string? OldValue, string? NewValue);

public sealed class ProjectAuditService(CurrentUserService currentUser)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(
        ProjectTrackerDbContext db,
        Project project,
        string action,
        string summary,
        IReadOnlyCollection<ProjectAuditChange>? changes = null,
        int? projectTaskId = null)
    {
        db.ProjectAuditEntries.Add(new ProjectAuditEntry
        {
            Project = project,
            ProjectTaskId = projectTaskId,
            Action = action,
            Summary = summary,
            ChangesJson = JsonSerializer.Serialize(changes ?? [], JsonOptions),
            ChangedByAccountName = currentUser.AccountName,
            ChangedByDisplayName = currentUser.DisplayName
        });
    }

    public static IReadOnlyList<ProjectAuditChange> ReadChanges(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProjectAuditChange>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<ProjectAuditChange> Diff(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after)
    {
        var fields = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal);
        return fields
            .Select(field => new ProjectAuditChange(
                field,
                before.GetValueOrDefault(field),
                after.GetValueOrDefault(field)))
            .Where(change => !string.Equals(change.OldValue, change.NewValue, StringComparison.Ordinal))
            .ToList();
    }

    public static IReadOnlyDictionary<string, string?> CaptureProject(Project project) =>
        new Dictionary<string, string?>
        {
            ["Part number"] = project.ProgramName,
            ["Contact lead"] = project.ProgramManager,
            ["Engineer"] = project.Engineer,
            ["Customer"] = project.CustomerName,
            ["Sales order"] = project.SalesOrderNumber,
            ["Project start"] = Date(project.ProgramStart),
            ["Status"] = Friendly(project.Status.ToString()),
            ["Priority"] = project.PriorityRank is null ? null : $"P{project.PriorityRank}",
            ["Completion"] = Percent(project.Progress),
            ["Target delivery"] = Date(project.TargetDelivery),
            ["Completed on"] = Date(project.CompletedOn)
        };

    public static IReadOnlyDictionary<string, string?> CaptureTask(ProjectTask task) =>
        new Dictionary<string, string?>
        {
            ["Step order"] = task.Sequence.ToString(CultureInfo.InvariantCulture),
            ["External ID"] = task.ExternalTaskId,
            ["Operation"] = task.Title,
            ["Phase"] = task.Phase,
            ["Work station"] = task.WorkStation,
            ["Depends on task ID"] = task.DependencyTaskId?.ToString(CultureInfo.InvariantCulture),
            ["Start"] = Date(task.StartDate),
            ["End"] = Date(task.EndDate),
            ["Original start"] = Date(task.OriginalStartDate),
            ["Original end"] = Date(task.OriginalEndDate),
            ["Duration"] = task.EstimatedDuration?.ToString(CultureInfo.InvariantCulture),
            ["Actual duration"] = task.ActualDuration?.ToString(CultureInfo.InvariantCulture),
            ["Completion"] = Percent(task.PercentComplete),
            ["Status"] = Friendly(task.Status.ToString()),
            ["Start date locked"] = task.StartDateLocked ? "Yes" : "No",
            ["Notes"] = task.Notes,
            ["Overtime days"] = task.OvertimeDays.Count == 0
                ? null
                : string.Join(", ", task.OvertimeDays.OrderBy(day => day.Date).Select(day =>
                    string.IsNullOrWhiteSpace(day.Note) ? Date(day.Date) : $"{Date(day.Date)} ({day.Note})"))
        };

    private static string? Date(DateOnly? value) => value?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    private static string Percent(decimal value) => $"{Math.Round(value * 100m)}%";

    private static string Friendly(string value) => value switch
    {
        "NotStarted" => "Not Started",
        "OnTrack" => "On Track",
        _ => value
    };
}

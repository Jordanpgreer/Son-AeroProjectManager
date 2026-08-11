using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public static class ProjectImportCompletion
{
    public sealed record MissingField(string Key, string Label);

    public static IReadOnlyList<MissingField> GetMissingFields(Project project)
    {
        if (!project.ImportNeedsCompletion) return [];

        var fields = new List<MissingField>();
        AddIfBlank(fields, project.CustomerName, "customerName", "Customer");
        AddIfBlank(fields, project.ProgramManager, "programManager", "Contact Lead");
        AddIfBlank(fields, project.Engineer, "engineer", "Engineer");
        AddIfBlank(fields, project.SalesOrderNumber, "salesOrderNumber", "Sales Order");
        AddIfBlank(fields, project.JobNumber, "jobNumber", "Job Number");
        return fields;
    }

    public static void Refresh(Project project)
    {
        if (project.ImportNeedsCompletion && GetMissingFields(project).Count == 0)
            project.ImportNeedsCompletion = false;
    }

    private static void AddIfBlank(
        ICollection<MissingField> fields,
        string? value,
        string key,
        string label)
    {
        if (string.IsNullOrWhiteSpace(value)) fields.Add(new MissingField(key, label));
    }
}

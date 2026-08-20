using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public static class ProjectTrackerGroups
{
    public const string ViewOnly = "View Only";
}

public static class ProjectTrackerPermissions
{
    public const string ProjectActivityView = "project.activity.view";
    public const string ProjectEditJobNumber = "project.edit.jobNumber";
    public const string ProjectEditExternalLinks = "project.edit.externalLinks";
    public const string ArchivedDelete = "archived.delete";
    public const string OperationScheduleConfirm = "notifications.operationSchedule.confirm";
    public const string WorkCentersImport = "settings.workCenters.import";

    public static readonly IReadOnlyList<PermissionDefinition> Local =
    [
        new(ProjectActivityView, "View Project Activity", "View and export the project activity log.", "Projects"),
        new(ProjectEditJobNumber, "Edit Job Number", "Change the project job number field.", "Projects"),
        new(ProjectEditExternalLinks, "Edit SO / Job Links", "Add, change, or remove external links for sales order and job numbers.", "Projects"),
        new(OperationScheduleConfirm, "Operation Start / Finish Prompts", "Receive and confirm operation start and finish reminders.", "Operations"),
        new(WorkCentersImport, "Import Work Centers", "Upload an Excel workbook to add work-center names without changing existing entries.", "Administration"),
        new(ArchivedDelete, "Permanently Delete Archived Projects", "Administrators only: permanently remove an archived project and its related records.", "Administration")
    ];

    public static readonly IReadOnlyList<PermissionDefinition> All =
        ApplicationPermissions.All.Concat(Local).ToList();

    public static readonly ISet<string> AllKeys =
        All.Select(permission => permission.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ISet<string> LocalKeys =
        Local.Select(permission => permission.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DefaultsForGroup(string groupName) => groupName switch
    {
        ApplicationGroups.Administrators => [ProjectActivityView, ProjectEditJobNumber, ProjectEditExternalLinks, OperationScheduleConfirm, WorkCentersImport, ArchivedDelete],
        ApplicationGroups.Managers => [ProjectActivityView, ProjectEditJobNumber, OperationScheduleConfirm],
        ApplicationGroups.Engineering => [ProjectActivityView, OperationScheduleConfirm],
        ApplicationGroups.Sales => [ProjectActivityView, ProjectEditJobNumber],
        _ => []
    };
}

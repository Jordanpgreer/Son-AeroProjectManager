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

    public static readonly IReadOnlyList<PermissionDefinition> Local =
    [
        new(ProjectActivityView, "View Project Activity", "View and export the project activity log.", "Projects"),
        new(ProjectEditJobNumber, "Edit Job Number", "Change the project job number field.", "Projects")
    ];

    public static readonly IReadOnlyList<PermissionDefinition> All =
        ApplicationPermissions.All.Concat(Local).ToList();

    public static readonly ISet<string> AllKeys =
        All.Select(permission => permission.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ISet<string> LocalKeys =
        Local.Select(permission => permission.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DefaultsForGroup(string groupName) => groupName switch
    {
        ApplicationGroups.Administrators => [ProjectActivityView, ProjectEditJobNumber],
        ApplicationGroups.Managers => [ProjectActivityView, ProjectEditJobNumber],
        ApplicationGroups.Engineering => [ProjectActivityView],
        ApplicationGroups.Sales => [ProjectActivityView, ProjectEditJobNumber],
        _ => []
    };
}

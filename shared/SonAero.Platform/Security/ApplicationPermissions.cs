namespace SonAero.Platform.Security;

public static class ApplicationPermissions
{
    public const string ModuleView = "module.view";

    public const string ProjectCreate = "project.create";
    public const string ProjectEditProgramName = "project.edit.programName";
    public const string ProjectEditProgramManager = "project.edit.programManager";
    public const string ProjectEditEngineer = "project.edit.engineer";
    public const string ProjectEditCustomerName = "project.edit.customerName";
    public const string ProjectEditSalesOrderNumber = "project.edit.salesOrderNumber";
    public const string ProjectReorderPriority = "project.edit.priority";
    public const string ProjectComplete = "project.complete";
    public const string ProjectReopen = "project.reopen";
    public const string ProjectArchive = "project.archive";

    public const string TaskCreate = "task.create";
    public const string TaskDelete = "task.delete";
    public const string TaskEditTitle = "task.edit.title";
    public const string TaskEditWorkStation = "task.edit.workStation";
    public const string TaskEditDependency = "task.edit.dependency";
    public const string TaskEditStartDateLocked = "task.edit.startDateLocked";
    public const string TaskEditStartDate = "task.edit.startDate";
    public const string TaskEditEndDate = "task.edit.endDate";
    public const string TaskEditOriginalStartDate = "task.edit.originalStartDate";
    public const string TaskEditOriginalEndDate = "task.edit.originalEndDate";
    public const string TaskEditEstimatedDuration = "task.edit.estimatedDuration";
    public const string TaskEditActualDuration = "task.edit.actualDuration";
    public const string TaskEditPercentComplete = "task.edit.percentComplete";
    public const string TaskEditNotes = "task.edit.notes";
    public const string TaskEditOvertimeDays = "task.edit.overtimeDays";
    public const string TaskReorder = "task.edit.sequence";

    public const string SettingsWorkCalendarManage = "settings.workCalendar.manage";
    public const string SettingsHolidaysManage = "settings.holidays.manage";
    public const string SettingsWorkCentersManage = "settings.workCenters.manage";
    public const string ImportManage = "import.manage";
    public const string ArchivedRestore = "archived.restore";
    public const string AccessManageUsers = "access.manageUsers";
    public const string AccessManageGroups = "access.manageGroups";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(ModuleView, "Module Access", "Open the Project Tracker module and read project data.", "General"),
        new(ProjectCreate, "Create Projects", "Add new projects to the portfolio.", "Projects"),
        new(ProjectEditProgramName, "Edit Part Number", "Change the project part / program name.", "Projects"),
        new(ProjectEditProgramManager, "Edit Contact Lead", "Change the contact lead / program manager field.", "Projects"),
        new(ProjectEditEngineer, "Edit Engineer", "Change the engineer field.", "Projects"),
        new(ProjectEditCustomerName, "Edit Customer", "Change the customer field.", "Projects"),
        new(ProjectEditSalesOrderNumber, "Edit Sales Order", "Change the sales order number field.", "Projects"),
        new(ProjectReorderPriority, "Edit Priority", "Reorder project priority in the dashboard queue.", "Projects"),
        new(ProjectComplete, "Complete Projects", "Mark a project complete.", "Projects"),
        new(ProjectReopen, "Reopen Projects", "Return a completed project to the active queue.", "Projects"),
        new(ProjectArchive, "Archive Projects", "Archive a project from active views.", "Projects"),
        new(TaskCreate, "Create Operations", "Add operations to a project.", "Operations"),
        new(TaskDelete, "Delete Operations", "Remove operations from a project.", "Operations"),
        new(TaskEditTitle, "Edit Operation Name", "Change the operation title.", "Operations"),
        new(TaskEditWorkStation, "Edit Work Station", "Change the work station assignment.", "Operations"),
        new(TaskEditDependency, "Edit Dependency", "Change the operation dependency.", "Operations"),
        new(TaskEditStartDateLocked, "Edit Start Lock", "Lock or unlock the operation start date.", "Operations"),
        new(TaskEditStartDate, "Edit Start Date", "Change the operation start date.", "Operations"),
        new(TaskEditEndDate, "Edit End Date", "Change the operation end date.", "Operations"),
        new(TaskEditOriginalStartDate, "Edit Original Start", "Change the original planned start date.", "Operations"),
        new(TaskEditOriginalEndDate, "Edit Original End", "Change the original planned end date.", "Operations"),
        new(TaskEditEstimatedDuration, "Edit Duration", "Change the estimated operation duration.", "Operations"),
        new(TaskEditActualDuration, "Edit Original Duration", "Change the original / actual duration field.", "Operations"),
        new(TaskEditPercentComplete, "Edit Completion", "Change operation percent complete.", "Operations"),
        new(TaskEditNotes, "Edit Notes", "Change operation notes.", "Operations"),
        new(TaskEditOvertimeDays, "Edit Overtime Days", "Manage approved overtime dates.", "Operations"),
        new(TaskReorder, "Reorder Operations", "Change the operation sequence.", "Operations"),
        new(SettingsWorkCalendarManage, "Manage Work Calendar", "Edit the standard work week.", "Administration"),
        new(SettingsHolidaysManage, "Manage Holidays", "Create, edit, or remove holidays.", "Administration"),
        new(SettingsWorkCentersManage, "Manage Work Centers", "Create, edit, or remove work centers.", "Administration"),
        new(ImportManage, "Run Imports", "Upload and import workbook data.", "Administration"),
        new(ArchivedRestore, "Restore Archived Projects", "Restore archived projects back into active or completed views.", "Administration"),
        new(AccessManageUsers, "Manage Registered Users", "Register users, activate/deactivate them, and assign groups.", "Access"),
        new(AccessManageGroups, "Manage Groups", "Create groups and assign group permissions.", "Access")
    ];

    public static readonly ISet<string> AllKeys = All.Select(permission => permission.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> ProjectFieldEditPermissions =
    [
        ProjectEditProgramName,
        ProjectEditProgramManager,
        ProjectEditEngineer,
        ProjectEditCustomerName,
        ProjectEditSalesOrderNumber
    ];

    public static readonly IReadOnlyList<string> TaskFieldEditPermissions =
    [
        TaskEditTitle,
        TaskEditWorkStation,
        TaskEditDependency,
        TaskEditStartDateLocked,
        TaskEditStartDate,
        TaskEditEndDate,
        TaskEditOriginalStartDate,
        TaskEditOriginalEndDate,
        TaskEditEstimatedDuration,
        TaskEditActualDuration,
        TaskEditPercentComplete,
        TaskEditNotes,
        TaskEditOvertimeDays,
        TaskReorder
    ];

    public static readonly IReadOnlyList<string> DefaultAdministratorPermissions = All.Select(permission => permission.Key).ToList();

    public static string[] DefaultManagerPermissions =>
    [
        ModuleView,
        ProjectCreate,
        ProjectEditProgramName,
        ProjectEditProgramManager,
        ProjectEditEngineer,
        ProjectEditCustomerName,
        ProjectEditSalesOrderNumber,
        ProjectReorderPriority,
        ProjectComplete,
        ProjectReopen,
        ProjectArchive,
        TaskCreate,
        TaskDelete,
        TaskEditTitle,
        TaskEditWorkStation,
        TaskEditDependency,
        TaskEditStartDateLocked,
        TaskEditStartDate,
        TaskEditEndDate,
        TaskEditOriginalStartDate,
        TaskEditOriginalEndDate,
        TaskEditEstimatedDuration,
        TaskEditActualDuration,
        TaskEditPercentComplete,
        TaskEditNotes,
        TaskEditOvertimeDays,
        TaskReorder,
        ArchivedRestore
    ];

    public static string[] DefaultEngineeringPermissions =>
    [
        ModuleView,
        TaskCreate,
        TaskEditTitle,
        TaskEditWorkStation,
        TaskEditDependency,
        TaskEditStartDateLocked,
        TaskEditStartDate,
        TaskEditEndDate,
        TaskEditOriginalStartDate,
        TaskEditOriginalEndDate,
        TaskEditEstimatedDuration,
        TaskEditActualDuration,
        TaskEditPercentComplete,
        TaskEditNotes,
        TaskEditOvertimeDays,
        TaskReorder
    ];

    public static string[] DefaultSalesPermissions =>
    [
        ModuleView,
        ProjectEditCustomerName,
        ProjectEditSalesOrderNumber,
        ProjectEditProgramManager,
        TaskEditNotes
    ];
}

public sealed record PermissionDefinition(string Key, string Label, string Description, string Category);

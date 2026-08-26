namespace SonAero.Platform.Security;

public static class EngineeringPermissions
{
    public const string ModuleView = "engineering.module.view";
    public const string DashboardView = "engineering.dashboard.view";
    public const string DrawingsView = "engineering.drawings.view";
    public const string DrawingFilesView = "engineering.drawings.files.view";
    public const string DrawingCreate = "engineering.drawings.create";
    public const string DrawingMetadataEdit = "engineering.drawings.metadata.edit";
    public const string DrawingArchive = "engineering.drawings.archive";
    public const string DrawingDelete = "engineering.drawings.delete";
    public const string PendingRevisionsView = "engineering.revisions.pending.view";
    public const string RevisionHistoryView = "engineering.revisions.history.view";
    public const string RevisionCreate = "engineering.revisions.create";
    public const string RevisionEdit = "engineering.revisions.edit";
    public const string RevisionSubmit = "engineering.revisions.submit";
    public const string RevisionApprove = "engineering.revisions.approve";
    public const string RevisionMakeCurrent = "engineering.revisions.current.manage";
    public const string RevisionDelete = "engineering.revisions.delete";
    public const string SpecificationsView = "engineering.specifications.view";
    public const string SpecificationsEdit = "engineering.specifications.edit";
    public const string SupportingDocumentsView = "engineering.supporting-documents.view";
    public const string SupportingDocumentsManage = "engineering.supporting-documents.manage";
    public const string MylarView = "engineering.mylar.view";
    public const string MylarManage = "engineering.mylar.manage";
    public const string ValidationsView = "engineering.validations.view";
    public const string ValidationsManage = "engineering.validations.manage";
    public const string AuditView = "engineering.audit.view";
    public const string ToolingView = "engineering.tooling.view";
    public const string ToolingRecordsManage = "engineering.tooling.records.manage";
    public const string ToolingArchiveManage = "engineering.tooling.archive.manage";
    public const string ToolingCustodyManage = "engineering.tooling.custody.manage";
    public const string ToolingDocumentsManage = "engineering.tooling.documents.manage";
    public const string ToolingLocationsManage = "engineering.tooling.locations.manage";
    public const string ToolingAuditImport = "engineering.tooling.audit.import";
    public const string CompoundDataView = "engineering.compound-data.view";
    public const string SettingsView = "engineering.settings.view";
    public const string SettingsManageUsers = "engineering.settings.users.manage";
    public const string SettingsManageGroups = "engineering.settings.groups.manage";
    public const string SettingsManageStorage = "engineering.settings.storage.manage";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        Permission(ModuleView, "Open Engineering Hub", "Sign in to and open the Engineering Hub.", "Module access"),
        Permission(DashboardView, "View engineering dashboard", "View cross-record engineering search and summary information.", "Module access"),
        Permission(DrawingsView, "View drawing register", "View drawing identity and the current controlled revision.", "Drawing records"),
        Permission(DrawingFilesView, "View controlled drawing files", "Preview and download the current controlled drawing file.", "Drawing records"),
        Permission(DrawingCreate, "Create drawings", "Create a new controlled drawing record.", "Drawing records"),
        Permission(DrawingMetadataEdit, "Edit drawing metadata", "Edit drawing number, title, design authority, linked parts, notes, and Mylar location.", "Drawing records"),
        Permission(DrawingArchive, "Archive drawings", "Archive or mark a drawing obsolete.", "Drawing records"),
        Permission(DrawingDelete, "Permanently delete drawings", "Permanently delete eligible drawing records.", "Drawing records"),
        Permission(PendingRevisionsView, "View pending revision indicators", "See draft and approval-pending revision indicators and review queues.", "Revision control"),
        Permission(RevisionHistoryView, "View revision history", "View revisions other than the current controlled revision.", "Revision control"),
        Permission(RevisionCreate, "Create revisions", "Upload a new revision package.", "Revision control"),
        Permission(RevisionEdit, "Edit draft revisions", "Edit revision metadata and reopen controlled revisions as drafts.", "Revision control"),
        Permission(RevisionSubmit, "Submit revisions for review", "Move draft revisions into the engineering review queue.", "Revision control"),
        Permission(RevisionApprove, "Approve revisions", "Approve revisions and record approval disposition.", "Revision control"),
        Permission(RevisionMakeCurrent, "Change current revision", "Select the current controlled revision.", "Revision control"),
        Permission(RevisionDelete, "Permanently delete revisions", "Permanently delete eligible revision packages.", "Revision control"),
        Permission(SpecificationsView, "View specification tags", "See which specifications apply to drawings.", "Engineering references"),
        Permission(SpecificationsEdit, "Edit specification tags", "Add and remove drawing specification tags.", "Engineering references"),
        Permission(SupportingDocumentsView, "View supporting documents", "See supporting documents attached to drawing revisions.", "Engineering references"),
        Permission(SupportingDocumentsManage, "Manage supporting documents", "Upload, carry forward, and remove revision supporting documents.", "Engineering references"),
        Permission(MylarView, "View Mylar custody", "View physical Mylar locations and custody history.", "Controlled assets"),
        Permission(MylarManage, "Manage Mylar custody", "Register, check out, and check in physical Mylars.", "Controlled assets"),
        Permission(ValidationsView, "View validation records", "View drawing validation and inspection records.", "Quality records"),
        Permission(ValidationsManage, "Manage validation records", "Add validation and inspection records.", "Quality records"),
        Permission(AuditView, "View drawing audit history", "View permanent drawing and revision audit events.", "Quality records"),
        Permission(ToolingView, "View tooling management", "Open tooling records and tooling search results.", "Other engineering areas"),
        Permission(ToolingRecordsManage, "Manage tool records", "Create and update tool identity, ownership, notes, and part-number assignments.", "Tooling control"),
        Permission(ToolingArchiveManage, "Archive or restore tools", "Archive in-storage tools or restore archived tools to active service. Intended for managers and administrators.", "Tooling control"),
        Permission(ToolingCustodyManage, "Manage tool custody", "Check tools in or out and record required inspection sign-off.", "Tooling control"),
        Permission(ToolingDocumentsManage, "Manage tool documents", "Upload receiving and shipping documents to permanent tool history.", "Tooling control"),
        Permission(ToolingLocationsManage, "Manage tool locations", "Create and activate physical tooling bin locations.", "Tooling control"),
        Permission(ToolingAuditImport, "Import tooling audit dates", "Mass update last-audit dates from a controlled CSV import.", "Tooling control"),
        Permission(CompoundDataView, "View compound and test data", "Open compound, certification, and test-data records.", "Other engineering areas"),
        Permission(SettingsView, "View Engineering settings", "Open Engineering module settings.", "Administration"),
        Permission(SettingsManageStorage, "Manage Engineering file storage", "Set the controlled drawing root and create approved design-authority folders.", "Administration")
    ];

    public static readonly IReadOnlySet<string> Keys = All
        .Select(permission => permission.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DefaultsForGroup(string groupName) => groupName.Trim().ToUpperInvariant() switch
    {
        "ADMINISTRATORS" => All.Select(permission => permission.Key).ToArray(),
        "MANAGERS" => ManagerDefaults,
        "ENGINEERING" => EngineeringDefaults,
        "SALES" => BasicViewerDefaults,
        "VIEW ONLY" => BasicViewerDefaults,
        _ => []
    };

    public static IReadOnlyList<string> DefaultsForRole(string role) => role.Trim().ToUpperInvariant() switch
    {
        "ADMIN" => All.Select(permission => permission.Key).ToArray(),
        "EDITOR" => EngineeringDefaults,
        _ => BasicViewerDefaults
    };

    public static HashSet<string> Expand(IEnumerable<string> permissions)
    {
        var expanded = permissions.Where(Keys.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddDependency(expanded, SpecificationsEdit, SpecificationsView);
        AddDependency(expanded, SupportingDocumentsManage, SupportingDocumentsView);
        AddDependency(expanded, SupportingDocumentsManage, PendingRevisionsView);
        AddDependency(expanded, MylarManage, MylarView);
        AddDependency(expanded, ValidationsManage, ValidationsView);
        AddDependency(expanded, ToolingRecordsManage, ToolingView);
        AddDependency(expanded, ToolingArchiveManage, ToolingView);
        AddDependency(expanded, ToolingCustodyManage, ToolingView);
        AddDependency(expanded, ToolingDocumentsManage, ToolingView);
        AddDependency(expanded, ToolingLocationsManage, ToolingView);
        AddDependency(expanded, ToolingAuditImport, ToolingView);
        foreach (var drawingDetailPermission in new[]
                 {
                     SpecificationsView, SupportingDocumentsView, MylarView, ValidationsView
                 })
            AddDependency(expanded, drawingDetailPermission, DrawingsView);
        AddDependency(expanded, RevisionMakeCurrent, RevisionHistoryView);
        AddDependency(expanded, RevisionDelete, RevisionHistoryView);
        AddDependency(expanded, SettingsManageUsers, SettingsView);
        AddDependency(expanded, SettingsManageGroups, SettingsView);
        AddDependency(expanded, SettingsManageStorage, SettingsView);
        foreach (var revisionPermission in new[]
                 {
                     RevisionCreate, RevisionEdit, RevisionSubmit, RevisionApprove,
                     RevisionMakeCurrent, RevisionDelete
                 })
        {
            AddDependency(expanded, revisionPermission, DrawingsView);
            AddDependency(expanded, revisionPermission, PendingRevisionsView);
        }
        foreach (var drawingPermission in new[]
                 {
                     DrawingFilesView, DrawingCreate, DrawingMetadataEdit,
                     DrawingArchive, DrawingDelete
                 })
            AddDependency(expanded, drawingPermission, DrawingsView);
        return expanded;
    }

    public static string? RoleFor(IEnumerable<string> permissions)
    {
        var expanded = Expand(permissions);
        if (!expanded.Contains(ModuleView)) return null;
        if (expanded.Contains(SettingsManageGroups) || expanded.Contains(SettingsManageUsers) || expanded.Contains(SettingsManageStorage))
            return ApplicationRoles.Admin;
        return expanded.Any(IsMutationPermission) ? ApplicationRoles.Editor : ApplicationRoles.Viewer;
    }

    private static readonly string[] BasicViewerDefaults =
    [
        ModuleView,
        DashboardView,
        DrawingsView,
        DrawingFilesView
    ];

    private static readonly string[] ManagerDefaults =
    [
        .. BasicViewerDefaults,
        PendingRevisionsView,
        RevisionHistoryView,
        RevisionSubmit,
        RevisionApprove,
        RevisionMakeCurrent,
        SpecificationsView,
        SupportingDocumentsView,
        MylarView,
        ValidationsView,
        AuditView,
        ToolingView,
        ToolingArchiveManage,
        CompoundDataView
    ];

    private static readonly string[] EngineeringDefaults =
    [
        .. ManagerDefaults.Where(permission => permission != ToolingArchiveManage),
        DrawingCreate,
        DrawingMetadataEdit,
        RevisionCreate,
        RevisionEdit,
        SpecificationsEdit,
        SupportingDocumentsManage,
        MylarManage,
        ValidationsManage,
        ToolingRecordsManage,
        ToolingCustodyManage,
        ToolingDocumentsManage
    ];

    private static PermissionDefinition Permission(string key, string label, string description, string category) =>
        new(key, label, description, category);

    private static void AddDependency(ISet<string> permissions, string permission, string dependency)
    {
        if (permissions.Contains(permission)) permissions.Add(dependency);
    }

    private static bool IsMutationPermission(string permission) =>
        permission.EndsWith(".edit", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".create", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".manage", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".approve", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".delete", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".submit", StringComparison.OrdinalIgnoreCase)
        || permission.EndsWith(".import", StringComparison.OrdinalIgnoreCase);
}

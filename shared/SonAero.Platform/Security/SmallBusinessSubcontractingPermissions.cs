namespace SonAero.Platform.Security;

public static class SmallBusinessSubcontractingPermissions
{
    public const string ModuleView = "small-business-subcontracting.module.view";
    public const string VendorsView = "small-business-subcontracting.vendors.view";
    public const string DashboardView = "small-business-subcontracting.dashboard.view";
    public const string Export = "small-business-subcontracting.export";
    public const string ComplianceManage = "small-business-subcontracting.compliance.manage";
    public const string DocumentsManage = "small-business-subcontracting.documents.manage";
    public const string FulcrumSync = "small-business-subcontracting.fulcrum.sync";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        Permission(ModuleView, "Open Small Business Subcontracting", "Open the Small Business Subcontracting module.", "Module access"),
        Permission(VendorsView, "View Vendors", "View Fulcrum vendor identity, contacts, compliance tags, and associated documents.", "Pages"),
        Permission(DashboardView, "View Compliance Dashboard", "Search and filter vendor business-size certification data.", "Pages"),
        Permission(Export, "Export Compliance Data", "Download the currently filtered vendor compliance grid as Excel.", "Reporting"),
        Permission(ComplianceManage, "Manage Compliance Data", "Create and assign business-size tags and update certification dates.", "Vendor compliance"),
        Permission(DocumentsManage, "Manage Vendor Documents", "Upload documents to permanent vendor records.", "Vendor compliance"),
        Permission(FulcrumSync, "Synchronize Fulcrum Vendors", "Refresh vendor identity and contacts from Fulcrum without changing Arda-owned compliance data.", "Administration")
    ];

    public static readonly IReadOnlySet<string> ViewerDefaults = new HashSet<string>(
        [ModuleView, VendorsView, DashboardView, Export],
        StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> EditorDefaults = new HashSet<string>(
        [.. ViewerDefaults, ComplianceManage, DocumentsManage],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Expand(IEnumerable<string> permissions)
    {
        var knownPermissions = All
            .Select(permission => permission.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expanded = permissions
            .Where(knownPermissions.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (expanded.Count > 0)
            expanded.Add(ModuleView);
        if (expanded.Contains(Export))
            expanded.Add(DashboardView);
        if (expanded.Contains(ComplianceManage)
            || expanded.Contains(DocumentsManage)
            || expanded.Contains(FulcrumSync))
        {
            expanded.Add(VendorsView);
        }

        return expanded;
    }

    private static PermissionDefinition Permission(string key, string label, string description, string category) =>
        new(key, label, description, category);
}

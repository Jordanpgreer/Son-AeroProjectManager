namespace SonAero.Platform.Security;

public static class ApplicationModules
{
    public const string Engineering = "engineering";
    public const string Estimating = "estimating";
    public const string QualityAssurance = "quality-assurance";

    public static readonly IReadOnlyList<string> All =
        [Engineering, Estimating, QualityAssurance];

    public static string? Normalize(string? moduleKey) => moduleKey?.Trim().ToLowerInvariant() switch
    {
        Engineering => Engineering,
        Estimating => Estimating,
        QualityAssurance => QualityAssurance,
        _ => null
    };
}

public static class ApplicationModuleRoles
{
    public static readonly IReadOnlyList<string> All =
    [
        ApplicationRoles.Viewer,
        ApplicationRoles.Editor,
        ApplicationRoles.Admin
    ];

    public static string? Normalize(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        "VIEWER" => ApplicationRoles.Viewer,
        "EDITOR" => ApplicationRoles.Editor,
        "ADMIN" => ApplicationRoles.Admin,
        _ => null
    };
}

public static class EstimatingPagePermissions
{
    public const string QuotesDashboardView = "estimating.quotes.view";
    public const string QuoteStatusView = "estimating.quote-status.view";
    public const string CalculatorView = "estimating.calculator.view";
    public const string RatesView = "estimating.rates.view";
    public const string OperationRulesView = "estimating.operation-rules.view";

    public static readonly IReadOnlyList<string> All =
    [
        QuotesDashboardView,
        QuoteStatusView,
        CalculatorView,
        RatesView,
        OperationRulesView
    ];
}

public sealed record ApplicationModuleDefinition(
    string Key,
    string Name,
    IReadOnlyList<ApplicationModuleRoleDefinition> Roles);

public sealed record ApplicationModuleRoleDefinition(
    string Role,
    IReadOnlyList<PermissionDefinition> Permissions);

public static class ApplicationModuleCatalog
{
    public static readonly IReadOnlyList<ApplicationModuleDefinition> All =
    [
        CreateEngineeringModule(),
        CreateEstimatingModule(),
        CreateQualityAssuranceModule()
    ];

    public static ApplicationModuleDefinition? Find(string? moduleKey)
    {
        var normalized = ApplicationModules.Normalize(moduleKey);
        return normalized is null
            ? null
            : All.Single(module => module.Key == normalized);
    }

    public static IReadOnlyList<PermissionDefinition> PermissionsFor(string moduleKey, string role)
    {
        var module = Find(moduleKey)
            ?? throw new ArgumentException($"Unknown module key '{moduleKey}'.", nameof(moduleKey));
        var normalizedRole = ApplicationModuleRoles.Normalize(role)
            ?? throw new ArgumentException($"Unknown module role '{role}'.", nameof(role));
        return module.Roles.Single(candidate => candidate.Role == normalizedRole).Permissions;
    }

    public static IReadOnlyList<PermissionDefinition> PermissionsForModule(string moduleKey)
    {
        var module = Find(moduleKey)
            ?? throw new ArgumentException($"Unknown module key '{moduleKey}'.", nameof(moduleKey));
        if (module.Key == ApplicationModules.QualityAssurance)
            return QualityAssurancePermissions.All;
        return module.Roles
            .SelectMany(role => role.Permissions)
            .DistinctBy(permission => permission.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? RoleForPermissions(string moduleKey, IEnumerable<string> permissions)
    {
        var module = Find(moduleKey)
            ?? throw new ArgumentException($"Unknown module key '{moduleKey}'.", nameof(moduleKey));
        var granted = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return module.Roles
            .Reverse()
            .FirstOrDefault(role => role.Permissions.All(permission => granted.Contains(permission.Key)))
            ?.Role;
    }

    private static ApplicationModuleDefinition CreateEngineeringModule()
    {
        const string key = ApplicationModules.Engineering;
        const string name = "Engineering";
        var view = new PermissionDefinition(
            $"{key}.module.view",
            $"View {name}",
            $"Open and view the {name} module.",
            name);
        var edit = new PermissionDefinition(
            $"{key}.module.edit",
            $"Edit {name}",
            $"Create and update records in the {name} module.",
            name);
        var admin = new PermissionDefinition(
            $"{key}.module.admin",
            $"Administer {name}",
            $"Manage administrative settings in the {name} module.",
            name);

        return new ApplicationModuleDefinition(
            key,
            name,
            [
                new ApplicationModuleRoleDefinition(ApplicationRoles.Viewer, [view]),
                new ApplicationModuleRoleDefinition(ApplicationRoles.Editor, [view, edit]),
                new ApplicationModuleRoleDefinition(ApplicationRoles.Admin, [view, edit, admin])
            ]);
    }

    private static ApplicationModuleDefinition CreateEstimatingModule()
    {
        const string category = "Estimating";
        const string pageCategory = "Pages";
        var view = new PermissionDefinition(
            "estimating.view",
            "View estimating",
            "Open and view the Estimating module.",
            category);
        var viewQuotes = new PermissionDefinition(
            EstimatingPagePermissions.QuotesDashboardView,
            "View Quotes Dashboard",
            "Open the personal quotes dashboard and assigned Fulcrum quote queue.",
            pageCategory);
        var viewQuoteStatus = new PermissionDefinition(
            EstimatingPagePermissions.QuoteStatusView,
            "View Quote Status",
            "Open shared quote status records, vendor threads, and communications.",
            pageCategory);
        var viewCalculator = new PermissionDefinition(
            EstimatingPagePermissions.CalculatorView,
            "View Estimate Calculator",
            "Open saved estimates and the estimate calculation workspace.",
            pageCategory);
        var viewRates = new PermissionDefinition(
            EstimatingPagePermissions.RatesView,
            "View Rates Reference",
            "Open controlled estimating rates and source information.",
            pageCategory);
        var viewOperationRules = new PermissionDefinition(
            EstimatingPagePermissions.OperationRulesView,
            "View Operation Rules",
            "Open controlled operation-step translation rules.",
            pageCategory);
        var calculate = new PermissionDefinition(
            "estimating.calculate",
            "Calculate estimates",
            "Run estimate calculations without changing saved quote records.",
            category);
        var manageQuotes = new PermissionDefinition(
            "estimating.quotes.manage",
            "Manage quotes",
            "Create, update, and duplicate quote records.",
            category);
        var deleteQuotes = new PermissionDefinition(
            "estimating.quotes.delete",
            "Delete quotes",
            "Permanently delete any local quote, including published revisions and their drafts. Remove and restore quote emails and internal notes.",
            category);
        var manageInputs = new PermissionDefinition(
            "estimating.inputs.manage",
            "Manage estimate inputs",
            "Change operations, materials, processes, quantities, and estimate context.",
            category);
        var administerRates = new PermissionDefinition(
            "estimating.rates.admin",
            "Administer rates",
            "Manage controlled estimating rate data when persistence is enabled.",
            category);
        var administerSettings = new PermissionDefinition(
            "estimating.settings.admin",
            "Administer estimating settings",
            "Manage Estimating module configuration.",
            category);
        var viewHistory = new PermissionDefinition(
            "estimating.history.view",
            "View Estimating Logs",
            "Open Estimating Logs to search imported quotes and view estimator statistics.",
            pageCategory);
        var importHistory = new PermissionDefinition(
            "estimating.history.import",
            "Import Estimating Logs",
            "Validate and import controlled Fulcrum workbooks into Estimating Logs.",
            category);
        var manageHistory = new PermissionDefinition(
            "estimating.history.manage",
            "Manage Estimating Logs",
            "View team statistics and audits, and download Estimating Logs reports.",
            category);

        return new ApplicationModuleDefinition(
            ApplicationModules.Estimating,
            category,
            [
                new ApplicationModuleRoleDefinition(
                    ApplicationRoles.Viewer,
                    [view, viewQuotes, viewQuoteStatus, viewCalculator, viewHistory, viewRates, viewOperationRules, calculate]),
                new ApplicationModuleRoleDefinition(
                    ApplicationRoles.Editor,
                    [view, viewQuotes, viewQuoteStatus, viewCalculator, viewHistory, viewRates, viewOperationRules, calculate, manageQuotes, manageInputs]),
                new ApplicationModuleRoleDefinition(
                    ApplicationRoles.Admin,
                    [view, viewQuotes, viewQuoteStatus, viewCalculator, viewHistory, viewRates, viewOperationRules, calculate, manageQuotes, deleteQuotes, manageInputs, importHistory, manageHistory, administerRates, administerSettings])
            ]);
    }

    private static ApplicationModuleDefinition CreateQualityAssuranceModule()
    {
        const string category = "Quality Assurance";
        return new ApplicationModuleDefinition(
            ApplicationModules.QualityAssurance,
            category,
            [
                new ApplicationModuleRoleDefinition(
                    ApplicationRoles.Viewer,
                    QualityAssurancePermissions.All.Where(permission =>
                        QualityAssurancePermissions.ViewerDefaults.Contains(permission.Key)).ToArray()),
                new ApplicationModuleRoleDefinition(
                    ApplicationRoles.Editor,
                    QualityAssurancePermissions.All.Where(permission =>
                        QualityAssurancePermissions.EditorDefaults.Contains(permission.Key)).ToArray()),
                new ApplicationModuleRoleDefinition(
                    ApplicationRoles.Admin,
                    QualityAssurancePermissions.All.Where(permission =>
                        QualityAssurancePermissions.AdministratorDefaults.Contains(permission.Key)).ToArray())
            ]);
    }
}

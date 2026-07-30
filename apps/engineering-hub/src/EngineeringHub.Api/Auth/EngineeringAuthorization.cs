using SonAero.Platform.Security;

namespace EngineeringHub.Api.Auth;

public static class EngineeringAuthorization
{
    public const string ModuleKey = ApplicationModules.Engineering;

    public const string ReadPolicy = "Engineering.Read";
    public const string WritePolicy = "Engineering.Write";
    public const string AdminPolicy = "Engineering.Admin";

    public const string PermissionClaimType = "sonaero.module.permission";
    public const string ReadPermission = "engineering.module.view";
    public const string WritePermission = "engineering.module.edit";
    public const string AdminPermission = "engineering.module.admin";

    public static IReadOnlyList<string> PermissionsForRole(string role) =>
        ApplicationModuleCatalog.PermissionsFor(ModuleKey, role)
            .Select(permission => permission.Key)
            .ToArray();
}

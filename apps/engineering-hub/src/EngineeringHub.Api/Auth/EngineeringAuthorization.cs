using SonAero.Platform.Security;

namespace EngineeringHub.Api.Auth;

public static class EngineeringAuthorization
{
    public const string ModuleKey = ApplicationModules.Engineering;

    public const string ReadPolicy = "Engineering.Read";

    public const string PermissionClaimType = "sonaero.module.permission";
    public const string ReadPermission = EngineeringPermissions.ModuleView;
    public const string AccessItem = "EngineeringAccess";

    public static IReadOnlyList<string> PermissionsForRole(string role) =>
        EngineeringPermissions.DefaultsForRole(role);
}

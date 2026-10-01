using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonAero.Platform.Security;

namespace SmallBusinessSubcontracting.Api;

public static class SubcontractingAuthorization
{
    public const string PermissionClaim = "sonaero.module.permission";
    public const string AccessItem = "SubcontractingAccess";
}

public sealed record SubcontractingAccess(
    string AccountName,
    string DisplayName,
    string Role,
    IReadOnlyList<string> Permissions);

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var account = configuration["Authentication:DevelopmentAccount"] ?? "DEV\\ProjectTrackerAdmin";
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, account)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class SubcontractingAccessService(
    IConfiguration configuration,
    RoleStoreDbContext roleStore,
    ILogger<SubcontractingAccessService> logger)
{
    public async Task<SubcontractingAccess?> ResolveAsync(string? accountName, CancellationToken cancellationToken)
    {
        var account = WindowsAccountNames.Normalize(accountName
            ?? configuration["Authentication:DevelopmentAccount"]
            ?? "DEV\\ProjectTrackerAdmin");
        if (account is null) return null;

        if (string.Equals(configuration["Authentication:Mode"], "Development", StringComparison.OrdinalIgnoreCase))
        {
            var role = ApplicationModuleRoles.Normalize(configuration["Authentication:DevelopmentRole"])
                ?? ApplicationRoles.Admin;
            return Build(account, WindowsAccountNames.DisplayName(account), role);
        }

        try
        {
            var lookup = WindowsAccountNames.LookupKeys(account);
            var user = await roleStore.Users.AsNoTracking()
                .Where(candidate => lookup.Contains(candidate.AccountName.ToUpper()))
                .Select(candidate => new
                {
                    candidate.AccountName,
                    candidate.DisplayName,
                    candidate.IsActive,
                    Role = candidate.ModuleAssignments
                        .Where(assignment => assignment.ModuleKey == ApplicationModules.SmallBusinessSubcontracting)
                        .Select(assignment => assignment.Role)
                        .FirstOrDefault(),
                    Permissions = candidate.GroupMemberships
                        .SelectMany(membership => membership.Group.Permissions)
                        .Where(permission => permission.PermissionKey.StartsWith("small-business-subcontracting."))
                        .Select(permission => permission.PermissionKey)
                        .ToList()
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (user is not { IsActive: true }) return null;

            var grantedPermissions = SmallBusinessSubcontractingPermissions.Expand(user.Permissions);
            if (grantedPermissions.Contains(SmallBusinessSubcontractingPermissions.ModuleView))
            {
                var grantedRole = ApplicationModuleCatalog.RoleForPermissions(
                        ApplicationModules.SmallBusinessSubcontracting,
                        grantedPermissions)
                    ?? ApplicationRoles.Viewer;
                return Build(user.AccountName, user.DisplayName, grantedRole, grantedPermissions);
            }

            var legacyRole = ApplicationModuleRoles.Normalize(user.Role);
            return legacyRole is null
                ? null
                : Build(user.AccountName, user.DisplayName, legacyRole);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            logger.LogError(exception, "The shared access store is unavailable; Small Business Subcontracting access is denied.");
            return null;
        }
    }

    public ClaimsPrincipal Attach(ClaimsPrincipal principal, SubcontractingAccess access)
    {
        var claims = principal.Claims
            .Where(claim => claim.Type != ClaimTypes.Role && claim.Type != SubcontractingAuthorization.PermissionClaim)
            .ToList();
        claims.Add(new Claim(ClaimTypes.Role, access.Role));
        claims.AddRange(access.Permissions.Select(permission =>
            new Claim(SubcontractingAuthorization.PermissionClaim, permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            principal.Identity?.AuthenticationType,
            ClaimTypes.Name,
            ClaimTypes.Role));
    }

    private static SubcontractingAccess Build(
        string accountName,
        string? displayName,
        string role,
        IEnumerable<string>? grantedPermissions = null)
    {
        var permissions = grantedPermissions is null
            ? ApplicationModuleCatalog.PermissionsFor(
                    ApplicationModules.SmallBusinessSubcontracting,
                    role)
                .Select(permission => permission.Key)
                .ToArray()
            : SmallBusinessSubcontractingPermissions.Expand(grantedPermissions)
                .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return new SubcontractingAccess(
            WindowsAccountNames.Normalize(accountName) ?? accountName,
            string.IsNullOrWhiteSpace(displayName) ? WindowsAccountNames.DisplayName(accountName) : displayName.Trim(),
            role,
            permissions);
    }
}

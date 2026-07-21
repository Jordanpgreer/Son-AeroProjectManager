using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public sealed class RoleClaimsTransformation(ProjectTrackerDbContext db) : IClaimsTransformation
{
    private const string ApplicationRoleIdentity = "ProjectTrackerRoles";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.Identity.Name is null)
        {
            return principal;
        }

        if (principal.Identities.Any(identity => identity.AuthenticationType == ApplicationRoleIdentity))
        {
            return principal;
        }

        var account = principal.Identity.Name;
        var normalizedAccount = account.ToUpperInvariant();
        var access = await db.Users
            .AsNoTracking()
            .Include(user => user.GroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.Permissions)
            .FirstOrDefaultAsync(user => user.AccountName.ToUpper() == normalizedAccount && user.IsActive);
        var identity = new ClaimsIdentity(ApplicationRoleIdentity);

        if (access is null)
        {
            principal.AddIdentity(identity);
            return principal;
        }

        var groups = access.GroupMemberships
            .Select(membership => membership.Group.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var permissions = access.GroupMemberships
            .SelectMany(membership => membership.Group.Permissions.Select(permission => permission.PermissionKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        identity.AddClaim(new Claim(ApplicationClaimTypes.RegisteredUser, "true"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));

        foreach (var group in groups)
        {
            identity.AddClaim(new Claim(ApplicationClaimTypes.Group, group));
        }

        foreach (var permission in permissions)
        {
            identity.AddClaim(new Claim(ApplicationClaimTypes.Permission, permission));
        }

        principal.AddIdentity(identity);
        return principal;
    }
}

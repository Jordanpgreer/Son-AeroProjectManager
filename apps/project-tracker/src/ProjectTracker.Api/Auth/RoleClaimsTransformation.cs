using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public sealed class RoleClaimsTransformation(
    ProjectTrackerDbContext db,
    ProjectTrackerAccessPreviewService? accessPreview = null,
    IHttpContextAccessor? httpContextAccessor = null) : IClaimsTransformation
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

        var account = WindowsAccountNames.Normalize(principal.Identity.Name);
        if (account is null)
        {
            return principal;
        }

        if (accessPreview is not null && httpContextAccessor?.HttpContext is { } httpContext)
        {
            var previewResolution = await accessPreview.ResolveAsync(
                principal,
                httpContext.Request,
                httpContext.RequestAborted);
            if (previewResolution.Status != AccessPreviewResolutionStatus.None)
            {
                var previewIdentity = new ClaimsIdentity(ApplicationRoleIdentity);
                if (previewResolution is { Status: AccessPreviewResolutionStatus.Active, Preview: { } preview })
                {
                    previewIdentity.AddClaim(new Claim(ApplicationClaimTypes.RegisteredUser, "true"));
                    previewIdentity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
                    previewIdentity.AddClaim(new Claim(AccessPreviewClaimTypes.Active, "true"));
                    previewIdentity.AddClaim(new Claim(AccessPreviewClaimTypes.ActorAccountName, preview.ActorAccountName));
                    previewIdentity.AddClaim(new Claim(AccessPreviewClaimTypes.TargetKey, preview.TargetKey));
                    previewIdentity.AddClaim(new Claim(AccessPreviewClaimTypes.TargetTitle, preview.TargetTitle));
                    previewIdentity.AddClaim(new Claim(AccessPreviewClaimTypes.ApplicationId, AccessPreviewApplications.ProjectTracker));
                    if (preview.TargetAccountName is not null)
                    {
                        previewIdentity.AddClaim(new Claim(AccessPreviewClaimTypes.TargetAccountName, preview.TargetAccountName));
                    }
                    foreach (var group in preview.Groups)
                    {
                        previewIdentity.AddClaim(new Claim(ApplicationClaimTypes.Group, group));
                    }
                    foreach (var permission in preview.Permissions)
                    {
                        previewIdentity.AddClaim(new Claim(ApplicationClaimTypes.Permission, permission));
                    }
                }

                principal.AddIdentity(previewIdentity);
                return principal;
            }
        }

        var lookupKeys = WindowsAccountNames.LookupKeys(account);
        var access = await db.Users
            .Include(user => user.GroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.Permissions)
            .FirstOrDefaultAsync(user => lookupKeys.Contains(user.AccountName.ToUpper()));
        var identity = new ClaimsIdentity(ApplicationRoleIdentity);

        if (access is null)
        {
            principal.AddIdentity(identity);
            return principal;
        }

        if (access?.IsActive != true)
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
        identity.AddClaim(new Claim(ApplicationClaimTypes.DisplayName, access.DisplayName));
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

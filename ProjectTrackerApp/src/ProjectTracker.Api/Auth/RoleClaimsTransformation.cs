using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;

namespace ProjectTracker.Api.Auth;

public sealed class RoleClaimsTransformation(IConfiguration configuration, ProjectTrackerDbContext db) : IClaimsTransformation
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
        var normalizedAccount = account.ToUpper();
        var storedRole = await db.Users
            .AsNoTracking()
            .Where(user => user.AccountName.ToUpper() == normalizedAccount)
            .Select(user => user.Role)
            .FirstOrDefaultAsync();
        var role = NormalizeRole(storedRole) ?? ConfiguredRole(account);
        var identity = new ClaimsIdentity(ApplicationRoleIdentity);

        if (role == "Admin")
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Editor"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
        }
        else if (role == "Editor")
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Editor"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
        }
        else
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
        }

        principal.AddIdentity(identity);
        return principal;
    }

    private string ConfiguredRole(string account)
    {
        var admins = configuration.GetSection("Security:Admins").Get<string[]>() ?? [];
        var editors = configuration.GetSection("Security:Editors").Get<string[]>() ?? [];

        if (ContainsAccount(admins, account))
        {
            return "Admin";
        }
        if (ContainsAccount(editors, account))
        {
            return "Editor";
        }
        return "Viewer";
    }

    private static bool ContainsAccount(IEnumerable<string> accounts, string account)
    {
        return accounts.Any(candidate => string.Equals(candidate, account, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeRole(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        "ADMIN" => "Admin",
        "EDITOR" or "EDIT" => "Editor",
        "VIEWER" or "VIEW ONLY" => "Viewer",
        _ => null
    };
}

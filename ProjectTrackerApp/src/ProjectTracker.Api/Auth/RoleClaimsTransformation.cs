using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace ProjectTracker.Api.Auth;

public sealed class RoleClaimsTransformation(IConfiguration configuration) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.Identity.Name is null)
        {
            return Task.FromResult(principal);
        }

        if (principal.HasClaim(claim => claim.Type == ClaimTypes.Role))
        {
            return Task.FromResult(principal);
        }

        var identity = (ClaimsIdentity)principal.Identity;
        var account = principal.Identity.Name;
        var admins = configuration.GetSection("Security:Admins").Get<string[]>() ?? [];
        var editors = configuration.GetSection("Security:Editors").Get<string[]>() ?? [];

        if (ContainsAccount(admins, account))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Editor"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
        }
        else if (ContainsAccount(editors, account))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Editor"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
        }
        else
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Viewer"));
        }

        return Task.FromResult(principal);
    }

    private static bool ContainsAccount(IEnumerable<string> accounts, string account)
    {
        return accounts.Any(candidate => string.Equals(candidate, account, StringComparison.OrdinalIgnoreCase));
    }
}


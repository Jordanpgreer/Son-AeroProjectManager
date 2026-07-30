using System.Security.Claims;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingUserService(
    IConfiguration configuration,
    IEstimatingAccessStore accessStore)
{
    public async Task<EstimatingAccessProfile?> ResolveAccessAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var accountName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\estimating.user";
        }

        return await accessStore.FindEnabledAsync(accountName, cancellationToken);
    }

    public static MeDto Current(EstimatingAccessProfile access) => new(
        access.AccountName,
        access.DisplayName,
        EstimatingModule.Key,
        access.Role,
        access.Permissions);
}

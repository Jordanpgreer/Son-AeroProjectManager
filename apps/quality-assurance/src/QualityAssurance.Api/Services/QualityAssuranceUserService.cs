using System.Security.Claims;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityAssuranceUserService(
    IConfiguration configuration,
    IQualityAssuranceAccessStore accessStore)
{
    public async Task<QualityAssuranceAccessProfile?> ResolveAccessAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var accountName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = configuration["Authentication:DevelopmentAccount"]
                ?? "DEV\\ProjectTrackerAdmin";
        }

        accountName = WindowsAccountNames.Normalize(accountName);
        return accountName is null
            ? null
            : await accessStore.FindAccessAsync(accountName, cancellationToken);
    }

    public static MeDto Current(QualityAssuranceAccessProfile access) => new(
        access.AccountName,
        access.DisplayName,
        ApplicationModules.QualityAssurance,
        access.Role,
        access.Permissions,
        access.Groups.Select(group => group.Name).ToList());
}

using System.Security.Claims;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingUserService(IConfiguration configuration)
{
    public MeDto Current(ClaimsPrincipal principal)
    {
        var accountName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\estimating.user";
        }

        return new MeDto(accountName, ToDisplayName(accountName));
    }

    private static string ToDisplayName(string accountName)
    {
        var name = accountName;
        var separator = name.LastIndexOf('\\');
        if (separator >= 0 && separator < name.Length - 1)
        {
            name = name[(separator + 1)..];
        }

        name = name.Replace('.', ' ').Replace('_', ' ').Trim();
        if (name.Length == 0)
        {
            return accountName;
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);
        return string.Join(' ', words);
    }
}

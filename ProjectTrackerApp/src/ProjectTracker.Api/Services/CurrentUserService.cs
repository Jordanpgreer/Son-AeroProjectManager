using System.Security.Claims;

namespace ProjectTracker.Api.Services;

public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor)
{
    public string AccountName => httpContextAccessor.HttpContext?.User.Identity?.Name ?? "Unknown";

    public string DisplayName
    {
        get
        {
            var account = AccountName;
            var slashIndex = account.LastIndexOf('\\');
            return slashIndex >= 0 ? account[(slashIndex + 1)..] : account;
        }
    }

    public string Role
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.IsInRole("Admin") == true)
            {
                return "Admin";
            }

            if (user?.IsInRole("Editor") == true)
            {
                return "Editor";
            }

            return "Viewer";
        }
    }

    public bool CanEdit => httpContextAccessor.HttpContext?.User.IsInRole("Editor") == true
        || httpContextAccessor.HttpContext?.User.IsInRole("Admin") == true;

    public bool IsAdmin => httpContextAccessor.HttpContext?.User.IsInRole("Admin") == true;
}


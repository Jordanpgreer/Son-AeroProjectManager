namespace SonAero.Platform.Security;

public static class ApplicationRoles
{
    public const string Admin = "Admin";
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";

    public static string? Normalize(string? role) => role?.Trim().ToUpperInvariant() switch
    {
        "ADMIN" => Admin,
        "EDITOR" or "EDIT" => Editor,
        "VIEWER" or "VIEW ONLY" => Viewer,
        _ => null
    };
}

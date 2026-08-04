namespace SonAero.Platform.Security;

/// <summary>
/// Normalizes Windows account names used by the hub. Windows reports domain accounts as
/// DOMAIN\user, while administrators sometimes enter the visually similar DOMAIN/user form.
/// Both forms must resolve to the same role assignment.
/// </summary>
public static class WindowsAccountNames
{
    public static string? Normalize(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return null;
        }

        var normalized = accountName.Trim().Replace('/', '\\');
        if (normalized.Length > 160)
        {
            return null;
        }
        var separator = normalized.IndexOf('\\');
        if (separator < 0)
        {
            return null;
        }

        var domain = normalized[..separator].Trim();
        var user = normalized[(separator + 1)..].Trim();
        return domain.Length == 0 || user.Length == 0 || user.Contains('\\')
            ? null
            : $"{domain}\\{user}";
    }

    public static IReadOnlyList<string> LookupKeys(string? accountName)
    {
        var normalized = Normalize(accountName);
        if (normalized is null)
        {
            return [];
        }

        var key = normalized.ToUpperInvariant();
        return key.Contains('\\')
            ? [key, key.Replace('\\', '/')]
            : [key];
    }

    public static bool Equals(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft is not null
            && normalizedRight is not null
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    public static string DisplayName(string? accountName)
    {
        var normalized = Normalize(accountName);
        if (normalized is null)
        {
            return string.Empty;
        }

        var separator = normalized.LastIndexOf('\\');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }
}

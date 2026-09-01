using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

internal sealed class EngineeringDisplayNames
{
    private readonly IReadOnlyDictionary<string, string> displayNames;

    private EngineeringDisplayNames(IReadOnlyDictionary<string, string> displayNames)
    {
        this.displayNames = displayNames;
    }

    public static async Task<EngineeringDisplayNames> LoadAsync(
        IEngineeringRoleStore? roleStore,
        IEnumerable<string?> values,
        CancellationToken cancellationToken)
    {
        var accounts = values
            .Select(WindowsAccountNames.Normalize)
            .Where(account => account is not null)
            .Select(account => account!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directoryNames = roleStore is null || accounts.Length == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await roleStore.FindDisplayNamesAsync(accounts, cancellationToken);
        var resolved = accounts.ToDictionary(
            account => account,
            account => directoryNames.TryGetValue(account, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : WindowsAccountNames.DisplayName(account),
            StringComparer.OrdinalIgnoreCase);
        return new EngineeringDisplayNames(resolved);
    }

    public string Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = WindowsAccountNames.Normalize(value);
        if (normalized is null) return value;
        return displayNames.TryGetValue(normalized, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : WindowsAccountNames.DisplayName(normalized);
    }

    public string? ResolveNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : Resolve(value);

    public string ResolveEmbeddedAccounts(string value)
    {
        var resolved = value;
        foreach (var account in displayNames.Keys.OrderByDescending(account => account.Length))
        {
            resolved = resolved.Replace(account, Resolve(account), StringComparison.OrdinalIgnoreCase);
            resolved = resolved.Replace(account.Replace('\\', '/'), Resolve(account), StringComparison.OrdinalIgnoreCase);
        }
        return resolved;
    }
}

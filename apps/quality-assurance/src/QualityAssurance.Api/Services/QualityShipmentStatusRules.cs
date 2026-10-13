namespace QualityAssurance.Api.Services;

public static class QualityShipmentStatusRules
{
    public static readonly IReadOnlyList<string> UserStatuses =
    [
        "WIP",
        "Pending Source",
        "Pending Customer Service",
        "Pending FAI Approval"
    ];

    public static string NormalizeUserStatus(string? value)
    {
        var match = UserStatuses.FirstOrDefault(status =>
            string.Equals(status, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException(
            $"Status must be one of: {string.Join(", ", UserStatuses)}.");
    }

    public static string? ForAssignedGroup(string? groupName)
    {
        var normalized = groupName?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Contains("customer service", StringComparison.Ordinal)
            || normalized.Contains("customer support", StringComparison.Ordinal)
            || normalized.Contains("sales", StringComparison.Ordinal))
            return "Pending Customer Service";
        if (normalized.Contains("fai", StringComparison.Ordinal))
            return "Pending FAI Approval";
        if (normalized.Contains("source", StringComparison.Ordinal))
            return "Pending Source";
        if (normalized.Contains("quality", StringComparison.Ordinal)
            || normalized.Equals("qa", StringComparison.Ordinal))
            return "WIP";
        return null;
    }
}

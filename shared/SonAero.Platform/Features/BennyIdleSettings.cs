using System.Data;
using System.Data.Common;

namespace SonAero.Platform.Features;

public static class BennyIdleModules
{
    public const string ProjectTracker = "project-tracker";
    public const string EngineeringHub = "engineering-hub";
    public const string EstimatingDashboard = "estimating-dashboard";
    public const string QualityAssurance = "quality-assurance";

    public static IReadOnlyList<BennyIdleModuleDefinition> All { get; } =
    [
        new(ProjectTracker, "Project Tracker"),
        new(EngineeringHub, "Engineering Hub"),
        new(EstimatingDashboard, "Estimating Dashboard"),
        new(QualityAssurance, "Quality Assurance")
    ];

    public static string? Normalize(string? moduleKey) => All
        .FirstOrDefault(module => string.Equals(
            module.Key,
            moduleKey?.Trim(),
            StringComparison.OrdinalIgnoreCase))
        ?.Key;

    public static IReadOnlyList<string> NormalizeMany(IEnumerable<string>? moduleKeys)
    {
        var requested = (moduleKeys ?? [])
            .Select(Normalize)
            .Where(moduleKey => moduleKey is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All
            .Where(module => requested.Contains(module.Key))
            .Select(module => module.Key)
            .ToList();
    }
}

public sealed record BennyIdleModuleDefinition(string Key, string Name);

public sealed record BennyIdleModuleSettings(
    string ModuleKey,
    bool Enabled,
    string AssistantName,
    int IdleDelayMinutes);

public static class BennyIdleSettingsStore
{
    public const int DefaultDelayMinutes = 10;
    public const int MinimumDelayMinutes = 5;
    public const int MaximumDelayMinutes = 60;
    public const string DefaultModules = BennyIdleModules.ProjectTracker;

    public static IReadOnlyList<string> ParseModules(string? value) =>
        BennyIdleModules.NormalizeMany((value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string SerializeModules(IEnumerable<string>? moduleKeys) =>
        string.Join(',', BennyIdleModules.NormalizeMany(moduleKeys));

    public static int NormalizeDelay(int value) =>
        Math.Clamp(value, MinimumDelayMinutes, MaximumDelayMinutes);

    public static async Task<BennyIdleModuleSettings> ReadAsync(
        DbConnection connection,
        string moduleKey,
        CancellationToken cancellationToken)
    {
        var normalizedModule = BennyIdleModules.Normalize(moduleKey)
            ?? throw new ArgumentException("The Benny idle module key is not supported.", nameof(moduleKey));
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT AssistantName, AssistantIdleDelayMinutes, AssistantIdleModules
                FROM FeatureSettings
                WHERE Id = 1
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return DefaultFor(normalizedModule);

            var assistantName = reader.IsDBNull(0)
                ? "Benny"
                : reader.GetString(0).Trim();
            var delay = reader.IsDBNull(1)
                ? DefaultDelayMinutes
                : Convert.ToInt32(reader.GetValue(1));
            var modules = reader.IsDBNull(2)
                ? ParseModules(DefaultModules)
                : ParseModules(reader.GetString(2));
            return new BennyIdleModuleSettings(
                normalizedModule,
                modules.Contains(normalizedModule, StringComparer.OrdinalIgnoreCase),
                assistantName.Length > 0 ? assistantName : "Benny",
                NormalizeDelay(delay));
        }
        catch (DbException)
        {
            // Cosmetic settings fail closed while an older database is being upgraded.
            return DefaultFor(normalizedModule) with { Enabled = false };
        }
        finally
        {
            if (closeConnection && connection.State != ConnectionState.Closed)
                await connection.CloseAsync();
        }
    }

    private static BennyIdleModuleSettings DefaultFor(string moduleKey) => new(
        moduleKey,
        string.Equals(moduleKey, BennyIdleModules.ProjectTracker, StringComparison.OrdinalIgnoreCase),
        "Benny",
        DefaultDelayMinutes);
}

using System.Data;
using System.Data.Common;

namespace SonAero.Platform.Integrations;

public static class EnterpriseProviderNames
{
    public const string Fulcrum = "Fulcrum";
    public const string Acumatica = "Acumatica";

    public static IReadOnlyList<string> All { get; } = [Fulcrum, Acumatica];

    public static string Normalize(string? provider) =>
        All.FirstOrDefault(candidate =>
            string.Equals(candidate, provider?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? string.Empty;

    public static bool IsSupported(string? provider) => Normalize(provider).Length > 0;
}

public static class EnterpriseDataRoutes
{
    public const string EstimatingQuotes = "estimating-quotes";
    public const string ProjectQuantities = "project-quantities";
    public const string EngineeringRecords = "engineering-records";
    public const string QualityRecords = "quality-records";
}

public sealed record ExternalRecordIdentity(string SourceSystem, string ExternalId);

public interface IEnterpriseIntegrationAdapter
{
    string ProviderName { get; }
    string RouteName { get; }
}

public interface IEnterpriseProviderSource
{
    Task<string> GetActiveProviderAsync(CancellationToken cancellationToken);
}

public static class EnterpriseAdapterSelector
{
    public static T Select<T>(
        IEnumerable<T> adapters,
        string provider,
        string routeName)
        where T : IEnterpriseIntegrationAdapter
    {
        var normalizedProvider = EnterpriseProviderNames.Normalize(provider);
        if (normalizedProvider.Length == 0)
            throw new InvalidOperationException($"Enterprise provider '{provider}' is not supported.");

        return adapters.FirstOrDefault(adapter =>
                   string.Equals(adapter.ProviderName, normalizedProvider, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(adapter.RouteName, routeName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"{normalizedProvider} is not configured for the '{routeName}' data route.");
    }
}

public static class EnterpriseIntegrationSchema
{
    public const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "EnterpriseIntegrationSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EnterpriseIntegrationSettings" PRIMARY KEY,
            "ActiveProvider" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );

        INSERT OR IGNORE INTO "EnterpriseIntegrationSettings" ("Id", "ActiveProvider", "UpdatedAt", "UpdatedBy")
        VALUES (1, 'Fulcrum', CURRENT_TIMESTAMP, 'SYSTEM');

        CREATE TABLE IF NOT EXISTS "EnterpriseIntegrationSettingAudits" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EnterpriseIntegrationSettingAudits" PRIMARY KEY AUTOINCREMENT,
            "PreviousProvider" TEXT NOT NULL,
            "NewProvider" TEXT NOT NULL,
            "ChangedAt" TEXT NOT NULL,
            "ChangedBy" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_EnterpriseIntegrationSettingAudits_ChangedAt"
            ON "EnterpriseIntegrationSettingAudits" ("ChangedAt");
        """;

    public const string SqlServer = """
        IF OBJECT_ID(N'[EnterpriseIntegrationSettings]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EnterpriseIntegrationSettings] (
                [Id] int NOT NULL CONSTRAINT [PK_EnterpriseIntegrationSettings] PRIMARY KEY,
                [ActiveProvider] nvarchar(40) NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM [EnterpriseIntegrationSettings] WHERE [Id] = 1)
        BEGIN
            INSERT INTO [EnterpriseIntegrationSettings] ([Id], [ActiveProvider], [UpdatedAt], [UpdatedBy])
            VALUES (1, 'Fulcrum', SYSDATETIMEOFFSET(), 'SYSTEM');
        END;

        IF OBJECT_ID(N'[EnterpriseIntegrationSettingAudits]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EnterpriseIntegrationSettingAudits] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EnterpriseIntegrationSettingAudits] PRIMARY KEY,
                [PreviousProvider] nvarchar(40) NOT NULL,
                [NewProvider] nvarchar(40) NOT NULL,
                [ChangedAt] datetimeoffset NOT NULL,
                [ChangedBy] nvarchar(160) NOT NULL
            );
            CREATE INDEX [IX_EnterpriseIntegrationSettingAudits_ChangedAt]
                ON [EnterpriseIntegrationSettingAudits] ([ChangedAt]);
        END;
        """;
}

public static class EnterpriseIntegrationStore
{
    public static async Task<string> ReadActiveProviderAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ActiveProvider FROM EnterpriseIntegrationSettings WHERE Id = 1";
            var value = await command.ExecuteScalarAsync(cancellationToken);
            var provider = EnterpriseProviderNames.Normalize(Convert.ToString(value));
            return provider.Length > 0 ? provider : EnterpriseProviderNames.Fulcrum;
        }
        finally
        {
            if (closeConnection && connection.State != ConnectionState.Closed)
                await connection.CloseAsync();
        }
    }
}

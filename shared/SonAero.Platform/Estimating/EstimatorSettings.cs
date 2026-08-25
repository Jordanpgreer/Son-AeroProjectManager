namespace SonAero.Platform.Estimating;

public static class EstimatorSettings
{
    public const int NameMaxLength = 160;

    public static string NormalizeKey(string estimatorName) => estimatorName.Trim().ToUpperInvariant();

    public static bool IsEligible(string? estimatorName) =>
        !string.IsNullOrWhiteSpace(estimatorName)
        && !string.Equals(estimatorName.Trim(), "Unassigned", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(estimatorName.Trim(), "Sales", StringComparison.OrdinalIgnoreCase);

    public static bool IsActiveByDefault(string estimatorName) =>
        !string.Equals(estimatorName.Trim(), "Abel", StringComparison.OrdinalIgnoreCase)
        && !estimatorName.Trim().StartsWith("Abel ", StringComparison.OrdinalIgnoreCase);

    public const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS "EstimatingEstimatorSettings" (
            "EstimatorKey" TEXT NOT NULL CONSTRAINT "PK_EstimatingEstimatorSettings" PRIMARY KEY,
            "EstimatorName" TEXT NOT NULL,
            "IsActive" INTEGER NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );
        """;

    public const string SqlServerSchema = """
        IF OBJECT_ID(N'[EstimatingEstimatorSettings]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EstimatingEstimatorSettings] (
                [EstimatorKey] nvarchar(160) NOT NULL CONSTRAINT [PK_EstimatingEstimatorSettings] PRIMARY KEY,
                [EstimatorName] nvarchar(160) NOT NULL,
                [IsActive] bit NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL
            );
        END;
        """;
}

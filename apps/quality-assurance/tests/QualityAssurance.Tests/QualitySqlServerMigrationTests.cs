using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QualityAssurance.Api.Data;

namespace QualityAssurance.Tests;

public sealed class QualitySqlServerMigrationTests
{
    private static readonly string[] ExpectedMigrationIds =
    [
        "20260813181727_InitialQualityShipping",
        "20260813190849_AddQualityShippingLayoutPreferences",
        "20260826201500_AddQualityShipmentComments",
        "20260901133114_AddLegacyQualityAssigneeTags"
    ];

    [Fact]
    public void Every_quality_migration_has_discovery_metadata_and_expected_order()
    {
        using var db = CreateSqlServerContext();

        Assert.Equal(ExpectedMigrationIds, db.Database.GetMigrations());

        var migrationTypes = typeof(QualityAssuranceDbContext).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .ToArray();
        var missingMetadata = migrationTypes
            .Where(type => type.GetCustomAttribute<MigrationAttribute>() is null
                || type.GetCustomAttribute<DbContextAttribute>()?.ContextType
                    != typeof(QualityAssuranceDbContext))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Equal(ExpectedMigrationIds.Length, migrationTypes.Length);
        Assert.Empty(missingMetadata);
    }

    [Fact]
    public void Full_migration_script_uses_native_sql_server_types_and_identity_columns()
    {
        using var db = CreateSqlServerContext();
        var migrator = db.Database.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            options: MigrationsSqlGenerationOptions.Idempotent);

        var identityTypes = new Dictionary<string, string>
        {
            ["QualityAssignmentRules"] = "int",
            ["QualityShipments"] = "int",
            ["QualityShipmentAuditEntries"] = "bigint",
            ["QualityShippingLayoutPreferences"] = "int",
            ["QualityShipmentComments"] = "bigint",
            ["QualityMentionNotifications"] = "bigint"
        };
        foreach (var (table, identityType) in identityTypes)
        {
            Assert.Matches(
                $@"(?s)CREATE TABLE \[{Regex.Escape(table)}\]\s*\(\s*\[Id\] {identityType} NOT NULL IDENTITY,",
                script);
        }
        Assert.Contains("[Customer] nvarchar(240) NOT NULL", script);
        Assert.Contains("[TaskType] nvarchar(120) NOT NULL", script);
        Assert.Contains("[QaArrivalDate] date NULL", script);
        Assert.Contains("[Quantity] decimal(18,3) NULL", script);
        Assert.Contains("[DollarValue] decimal(18,2) NULL", script);
        Assert.Contains("[LegacyAssigneeTag] nvarchar(160) NULL", script);
        Assert.Contains("[CreatedAt] datetimeoffset NOT NULL", script);
        Assert.Contains("[IsShipped] bit NOT NULL", script);
        Assert.Contains("LEFT(CONVERT(nvarchar(max), Comments), 8000)", script);
        Assert.DoesNotContain("substr(Comments", script, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Regex.Matches(
            script,
            @"(?im)^\s+\[[^\]]+\]\s+(?:TEXT|INTEGER|REAL|BLOB)\b"));
    }

    [Fact]
    public void Design_time_model_is_canonical_sql_server_and_matches_the_snapshot()
    {
        using var db = new QualityAssuranceDbContextFactory().CreateDbContext([]);

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static QualityAssuranceDbContext CreateSqlServerContext()
    {
        var options = new DbContextOptionsBuilder<QualityAssuranceDbContext>()
            .UseSqlServer(
                "Server=(local);Database=QualityMigrationValidation;Integrated Security=True;" +
                "TrustServerCertificate=True")
            .Options;
        return new QualityAssuranceDbContext(options);
    }
}

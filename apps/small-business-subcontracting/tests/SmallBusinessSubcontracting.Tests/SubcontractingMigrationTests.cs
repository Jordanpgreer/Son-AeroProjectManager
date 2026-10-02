using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmallBusinessSubcontracting.Api;

namespace SmallBusinessSubcontracting.Tests;

public sealed class SubcontractingMigrationTests
{
    [Fact]
    public void Reviewed_dba_script_matches_the_current_provider_generated_migration()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "deployment", "migrations")))
            root = root.Parent;
        Assert.NotNull(root);
        var artifact = File.ReadAllText(Path.Combine(root.FullName, "deployment", "migrations",
            "SmallBusinessSubcontracting.Initial.sql"));
        Assert.Contains(":on error exit", artifact);
        Assert.Contains("SERVERPROPERTY(N'MachineName')", artifact);
        Assert.Contains("IF DB_NAME() <> N'SmallBusinessSubcontracting'", artifact);
        const string marker = "-- BEGIN GENERATED EF CORE SCRIPT";
        var offset = artifact.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset > 0);
        using var db = new SubcontractingDbContextFactory().CreateDbContext([]);
        var expected = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Equal(expected.Replace("\r\n", "\n").Trim(),
            artifact[(offset + marker.Length)..].Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public void Sql_server_script_uses_native_types_identity_keys_and_idempotent_history()
    {
        using var db = new SubcontractingDbContextFactory().CreateDbContext([]);
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
        Assert.Equal([SubcontractingDatabaseInitializer.InitialMigration], db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Equal(3, Regex.Matches(script, @"\bNOT NULL IDENTITY\b").Count);
        Assert.Contains("[Id] uniqueidentifier NOT NULL", script);
        Assert.Contains("[LastCertificationDate] date NULL", script);
        Assert.Contains("[LastSyncedAt] datetimeoffset NOT NULL", script);
        Assert.Contains("[Active] bit NOT NULL", script);
        Assert.Contains("[Name] nvarchar(200) NOT NULL", script);
        Assert.Contains("[Name] nvarchar(80) NOT NULL", script);
        Assert.Contains("[FileSize] bigint NOT NULL", script);
        Assert.Contains("ON DELETE CASCADE", script);
        Assert.Contains("IF NOT EXISTS", script);
        Assert.Contains("[__EFMigrationsHistory]", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Vendors_FulcrumId]", script);
        Assert.Empty(Regex.Matches(script, @"(?im)^\s+\[[^\]]+\]\s+(?:TEXT|INTEGER|REAL|BLOB)\b"));
        Assert.DoesNotContain("CREATE TABLE [Users]", script);
        Assert.DoesNotContain("CREATE TABLE [IntegrationCredentials]", script);
    }

    [Fact]
    public async Task Fresh_sqlite_migration_preserves_relations_dates_and_generated_keys_on_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await SubcontractingDatabaseInitializer.InitializeAsync(db, isDevelopment: true);
        var vendor = Vendor();
        vendor.BusinessSizes.Add(new VendorBusinessSizeTag
        {
            BusinessSizeTag = new BusinessSizeTag { Name = "Small", NormalizedName = "SMALL", CreatedBy = "Tester" }
        });
        vendor.AuditEvents.Add(new VendorAuditEvent { Kind = "Created", Actor = "Tester", Summary = "Created vendor" });
        vendor.Documents.Add(new VendorDocument
        {
            Id = Guid.NewGuid(), OriginalFileName = "certificate.pdf", RelativePath = "1/certificate.pdf",
            ContentType = "application/pdf", FileSize = 100, FileHash = "ABC", DocumentType = "Certificate",
            DocumentDate = new DateOnly(2026, 10, 1), UploadedBy = "Tester", UploadedAt = DateTimeOffset.UtcNow
        });
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        Assert.True(vendor.Id > 0);
        Assert.True(vendor.AuditEvents.Single().Id > 0);
        Assert.True(vendor.BusinessSizes.Single().BusinessSizeTagId > 0);
        await SubcontractingDatabaseInitializer.InitializeAsync(db, isDevelopment: true);
        db.ChangeTracker.Clear();
        var saved = await db.Vendors.Include(row => row.Documents).Include(row => row.BusinessSizes)
            .ThenInclude(row => row.BusinessSizeTag).Include(row => row.AuditEvents).SingleAsync();
        Assert.Equal(new DateOnly(2026, 10, 1), saved.LastCertificationDate);
        Assert.Equal(new DateOnly(2026, 10, 1), saved.Documents.Single().DocumentDate);
        Assert.Equal("Small", saved.BusinessSizes.Single().BusinessSizeTag.Name);
        Assert.Single(saved.AuditEvents);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        db.Vendors.Remove(saved);
        await db.SaveChangesAsync();
        Assert.Empty(await db.VendorDocuments.ToListAsync());
        Assert.Empty(await db.VendorBusinessSizeTags.ToListAsync());
        Assert.Empty(await db.VendorAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Exact_legacy_sqlite_schema_is_baselined_without_losing_vendor_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        db.Vendors.Add(Vendor());
        await db.SaveChangesAsync();
        await SubcontractingDatabaseInitializer.InitializeAsync(db, isDevelopment: true);
        Assert.Equal("Existing vendor", (await db.Vendors.SingleAsync()).Name);
        Assert.Equal([SubcontractingDatabaseInitializer.InitialMigration], await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Unexpected_legacy_schema_fails_without_baselining_or_losing_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        db.Vendors.Add(Vendor());
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Vendors ADD COLUMN Unexpected TEXT;");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubcontractingDatabaseInitializer.InitializeAsync(db, isDevelopment: true));
        Assert.Contains("preserved", exception.Message);
        Assert.Equal("Existing vendor", (await db.Vendors.SingleAsync()).Name);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Production_rejects_sqlite_before_creating_schema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubcontractingDatabaseInitializer.InitializeAsync(db, isDevelopment: false));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static SubcontractingDbContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<SubcontractingDbContext>().UseSqlite(connection).Options);

    private static VendorRecord Vendor() => new()
    {
        Name = "Existing vendor", FulcrumId = "vendor-1", Active = true,
        LastCertificationDate = new DateOnly(2026, 10, 1), LastSyncedAt = DateTimeOffset.UtcNow
    };
}

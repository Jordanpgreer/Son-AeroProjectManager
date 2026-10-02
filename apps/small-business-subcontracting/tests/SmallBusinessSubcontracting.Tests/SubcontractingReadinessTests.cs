using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmallBusinessSubcontracting.Api;

namespace SmallBusinessSubcontracting.Tests;

public sealed class SubcontractingReadinessTests
{
    [Fact]
    public async Task Health_requires_migrated_module_shared_tables_and_existing_document_storage()
    {
        await using var ownConnection = new SqliteConnection("Data Source=:memory:");
        await using var roleConnection = new SqliteConnection("Data Source=:memory:");
        await ownConnection.OpenAsync();
        await roleConnection.OpenAsync();
        await using var own = new SubcontractingDbContext(new DbContextOptionsBuilder<SubcontractingDbContext>()
            .UseSqlite(ownConnection).Options);
        await using var roles = new RoleStoreDbContext(new DbContextOptionsBuilder<RoleStoreDbContext>()
            .UseSqlite(roleConnection).Options);
        var temporary = Path.Combine(Path.GetTempPath(), $"subcontracting-readiness-{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VendorDocumentStorage:RootPath"] = temporary
        }).Build();
        var documents = new VendorDocumentStore(config, new TestEnvironment { EnvironmentName = "Development" });
        try
        {
            await own.Database.MigrateAsync();
            await roles.Database.EnsureCreatedAsync();
            await documents.VerifyWritableAsync(CancellationToken.None);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporary));
            var healthy = await SubcontractingReadiness.ReadAsync(own, roles, documents, NullLogger.Instance);
            Assert.Equal(new SubcontractingHealth("ok", "Sqlite", "ready", "ready", "ready"), healthy);

            await roles.Database.ExecuteSqlRawAsync("DROP TABLE IntegrationCredentials;");
            var rolesFailed = await SubcontractingReadiness.ReadAsync(own, roles, documents, NullLogger.Instance);
            Assert.Equal("unavailable", rolesFailed.Status);
            Assert.Equal("unavailable", rolesFailed.RoleStore);

            await own.Database.ExecuteSqlRawAsync("DROP TABLE VendorDocuments;");
            var schemaFailed = await SubcontractingReadiness.ReadAsync(own, roles, documents, NullLogger.Instance);
            Assert.Equal("unavailable", schemaFailed.Migrations);

            Directory.Delete(temporary);
            var storageFailed = await SubcontractingReadiness.ReadAsync(own, roles, documents, NullLogger.Instance);
            Assert.Equal("unavailable", storageFailed.DocumentStorage);
            Assert.False(Directory.Exists(temporary));
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }
}

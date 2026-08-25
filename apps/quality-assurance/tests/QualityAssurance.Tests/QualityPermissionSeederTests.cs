using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityPermissionSeederTests
{
    [Fact]
    public async Task SeedAddsNewAdminPermissionsWithRequiredTimestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new QualityAssuranceAccessDbContext(
            new DbContextOptionsBuilder<QualityAssuranceAccessDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        db.Groups.Add(new QualityAssuranceAccessGroupRecord
        {
            Name = ApplicationGroups.Administrators,
            Description = "Existing shared admin group",
        });
        await db.SaveChangesAsync();

        var seeder = new QualityPermissionSeeder(db, NullLogger<QualityPermissionSeeder>.Instance);
        await seeder.SeedAsync(default);

        var imported = await db.GroupPermissions.SingleAsync(
            permission => permission.PermissionKey == QualityAssurancePermissions.ShipmentImport);
        Assert.NotEqual(default, imported.CreatedAt);
    }
}

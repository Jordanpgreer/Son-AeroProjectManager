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
    public void AssignmentEligibilityAndResponsibleGroupAreIndependentAdminToggles()
    {
        Assert.Contains(QualityAssurancePermissions.All, permission => permission.Key == QualityAssurancePermissions.AssignmentEligible);
        Assert.Contains(QualityAssurancePermissions.All, permission => permission.Key == QualityAssurancePermissions.ResponsibleGroupEligible);
        Assert.DoesNotContain(QualityAssurancePermissions.AssignmentEligible, QualityAssurancePermissions.AdministratorDefaults);
        Assert.DoesNotContain(QualityAssurancePermissions.ResponsibleGroupEligible, QualityAssurancePermissions.AdministratorDefaults);
        Assert.DoesNotContain(QualityAssurancePermissions.AssignmentEligible, QualityAssurancePermissions.EditorDefaults);
        Assert.DoesNotContain(QualityAssurancePermissions.ResponsibleGroupEligible, QualityAssurancePermissions.EditorDefaults);
        Assert.NotEqual(
            QualityAssurancePermissions.AssignmentEligible,
            QualityAssurancePermissions.ResponsibleGroupEligible);
        Assert.Equal(
            ApplicationRoles.Admin,
            ApplicationModuleCatalog.RoleForPermissions(
                ApplicationModules.QualityAssurance,
                QualityAssurancePermissions.AdministratorDefaults));
        Assert.Equal(
            QualityAssurancePermissions.All.Select(permission => permission.Key),
            ApplicationModuleCatalog.PermissionsForModule(ApplicationModules.QualityAssurance)
                .Select(permission => permission.Key));
    }

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
        Assert.DoesNotContain(await db.GroupPermissions.ToListAsync(), permission =>
            permission.PermissionKey is QualityAssurancePermissions.AssignmentEligible
                or QualityAssurancePermissions.ResponsibleGroupEligible);
    }
}

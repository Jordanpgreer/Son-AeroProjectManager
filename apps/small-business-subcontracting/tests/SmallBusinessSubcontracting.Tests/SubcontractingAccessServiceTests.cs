using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmallBusinessSubcontracting.Api;
using SonAero.Platform.Security;

namespace SmallBusinessSubcontracting.Tests;

public sealed class SubcontractingAccessServiceTests
{
    [Fact]
    public async Task ResolveAsync_UsesExactSharedGroupPermissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RoleStoreDbContext>().UseSqlite(connection).Options;
        await using var db = new RoleStoreDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new RoleUser
        {
            AccountName = "SONAERO\\Compliance.One",
            DisplayName = "Compliance One",
            IsActive = true,
            GroupMemberships =
            [
                new RoleUserGroupMembership
                {
                    Group = new RoleAccessGroup
                    {
                        Name = "Vendor document coordinators",
                        Permissions =
                        [
                            new RoleGroupPermission
                            {
                                PermissionKey = SmallBusinessSubcontractingPermissions.DocumentsManage
                            }
                        ]
                    }
                }
            ]
        });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Windows"
            })
            .Build();
        var service = new SubcontractingAccessService(
            configuration,
            db,
            NullLogger<SubcontractingAccessService>.Instance);

        var access = await service.ResolveAsync("sonaero/compliance.one", CancellationToken.None);

        Assert.NotNull(access);
        Assert.Equal(ApplicationRoles.Viewer, access.Role);
        Assert.Contains(SmallBusinessSubcontractingPermissions.ModuleView, access.Permissions);
        Assert.Contains(SmallBusinessSubcontractingPermissions.VendorsView, access.Permissions);
        Assert.Contains(SmallBusinessSubcontractingPermissions.DocumentsManage, access.Permissions);
        Assert.DoesNotContain(SmallBusinessSubcontractingPermissions.DashboardView, access.Permissions);
        Assert.DoesNotContain(SmallBusinessSubcontractingPermissions.ComplianceManage, access.Permissions);
    }
}

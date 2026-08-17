using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Api.Data;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class PortalRoleStoreTests
{
    [Fact]
    public async Task FindRoleAsync_ReadsTrackerUserRoleCaseInsensitively()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = "SONAERO\\Planner.One",
            DisplayName = "Planner One",
            Role = "Editor"
        });
        await db.SaveChangesAsync();

        var store = new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance);

        Assert.Equal("Editor", await store.FindRoleAsync("sonaero/planner.one"));
    }

    [Fact]
    public async Task FindDisplayNameAsync_ReadsAdministratorConfiguredNameCaseInsensitively()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = "SONAERO\\Planner.One",
            DisplayName = "Preferred Application Name",
            Role = "Editor",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var store = new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance);

        Assert.Equal(
            "Preferred Application Name",
            await store.FindDisplayNameAsync("sonaero/planner.one"));
    }

    [Fact]
    public async Task FindModuleRolesAsync_DerivesRolesFromSharedGroupPermissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new PortalRoleRecord
        {
            AccountName = "SONAERO\\Estimator.One",
            DisplayName = "Estimator One",
            Role = "Viewer",
            IsActive = true,
            ProjectTrackerGroupMemberships =
            [
                new PortalProjectTrackerMembershipRecord
                {
                    Group = new PortalProjectTrackerGroupRecord
                    {
                        Name = "Estimating Editors",
                        Permissions = ApplicationModuleCatalog
                            .PermissionsFor(ApplicationModules.Estimating, ApplicationRoles.Editor)
                            .Select(permission => new PortalProjectTrackerPermissionRecord { PermissionKey = permission.Key })
                            .ToList()
                    }
                }
            ]
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var store = new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance);
        var roles = await store.FindModuleRolesAsync("sonaero/estimator.one");

        Assert.Equal("Editor", Assert.Single(roles).Value);
    }

    [Fact]
    public async Task FindRoleAsync_DoesNotReturnRoleForInactiveUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = @"SON4L\inactive.user",
            DisplayName = "Inactive User",
            Role = "Admin",
            IsActive = false
        });
        await db.SaveChangesAsync();

        var store = new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance);

        Assert.Null(await store.FindRoleAsync("son4l/inactive.user"));
    }

    [Fact]
    public async Task Shared_groups_hold_engineering_permissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var group = new PortalProjectTrackerGroupRecord
        {
            Name = "Engineering",
            Permissions =
            [
                new PortalProjectTrackerPermissionRecord { PermissionKey = EngineeringPermissions.ModuleView },
                new PortalProjectTrackerPermissionRecord { PermissionKey = EngineeringPermissions.DrawingCreate }
            ]
        };
        var user = new PortalRoleRecord
        {
            AccountName = @"SON4L\engineering.user",
            DisplayName = "Engineering User",
            Role = ApplicationRoles.Viewer,
            IsActive = true,
            ProjectTrackerGroupMemberships =
            [
                new PortalProjectTrackerMembershipRecord { Group = group }
            ]
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var permissions = await db.ProjectTrackerUserGroupMemberships
            .Where(membership => membership.AppUserId == user.Id)
            .SelectMany(membership => membership.Group.Permissions)
            .Select(permission => permission.PermissionKey)
            .ToListAsync();

        Assert.Equal(ApplicationRoles.Editor, EngineeringPermissions.RoleFor(permissions));
        Assert.Equal("Engineering", Assert.Single(user.ProjectTrackerGroupMemberships).Group.Name);
    }
}

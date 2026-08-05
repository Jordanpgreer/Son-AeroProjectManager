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
    public async Task FindModuleRolesAsync_ReturnsEnabledAssignmentsForActiveUserOnly()
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
            ModuleAccessAssignments =
            [
                new PortalModuleAccessRecord { ModuleKey = "estimating", Role = "Editor" },
                new PortalModuleAccessRecord { ModuleKey = "engineering", Role = null }
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
    public async Task Engineering_groups_share_registered_users_and_permissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var group = new PortalEngineeringGroupRecord
        {
            Name = "Engineering",
            Permissions =
            [
                new PortalEngineeringPermissionRecord { PermissionKey = EngineeringPermissions.ModuleView },
                new PortalEngineeringPermissionRecord { PermissionKey = EngineeringPermissions.DrawingCreate }
            ]
        };
        var user = new PortalRoleRecord
        {
            AccountName = @"SON4L\engineering.user",
            DisplayName = "Engineering User",
            Role = ApplicationRoles.Viewer,
            IsActive = true,
            EngineeringGroupMemberships =
            [
                new PortalEngineeringMembershipRecord { Group = group }
            ]
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var permissions = await db.EngineeringUserGroupMemberships
            .Where(membership => membership.AppUserId == user.Id)
            .SelectMany(membership => membership.Group.Permissions)
            .Select(permission => permission.PermissionKey)
            .ToListAsync();

        Assert.Equal(ApplicationRoles.Editor, EngineeringPermissions.RoleFor(permissions));
        Assert.Equal("Engineering", Assert.Single(user.EngineeringGroupMemberships).Group.Name);
    }
}

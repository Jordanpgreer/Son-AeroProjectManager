using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class RoleClaimsTransformationTests
{
    [Fact]
    public async Task TransformAsync_LoadsStoredGroupsAndPermissionsForRegisteredUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var engineering = new AppGroup
        {
            Name = ApplicationGroups.Engineering,
            Description = "Engineering team",
            IsSystemGroup = true,
            Permissions =
            [
                new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView },
                new AppGroupPermission { PermissionKey = ApplicationPermissions.TaskEditEstimatedDuration }
            ]
        };
        var user = new AppUser
        {
            AccountName = "DOMAIN\\planner.one",
            DisplayName = "Planner One",
            IsActive = true,
            GroupMemberships =
            [
                new AppUserGroupMembership { Group = engineering }
            ]
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var principal = AuthenticatedPrincipal("DOMAIN\\planner.one");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.True(principal.HasClaim(ApplicationClaimTypes.RegisteredUser, "true"));
        Assert.True(principal.HasClaim(ApplicationClaimTypes.Group, ApplicationGroups.Engineering));
        Assert.True(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView));
        Assert.True(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.TaskEditEstimatedDuration));
        Assert.True(principal.IsInRole("Viewer"));
    }

    [Fact]
    public async Task TransformAsync_DoesNotGrantRegisteredClaimsToUnknownUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var principal = AuthenticatedPrincipal("DOMAIN\\new.user");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.False(principal.HasClaim(ApplicationClaimTypes.RegisteredUser, "true"));
        Assert.False(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView));
        Assert.False(principal.IsInRole("Viewer"));
    }

    [Fact]
    public async Task TransformAsync_DoesNotGrantPermissionsFromUnassignedGroups()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var assignedGroup = new AppGroup
        {
            Name = "Assigned group",
            Permissions = [new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView }]
        };
        var unassignedGroup = new AppGroup
        {
            Name = "Unassigned group",
            Permissions = [new AppGroupPermission { PermissionKey = ApplicationPermissions.AccessManageGroups }]
        };
        db.Groups.Add(unassignedGroup);
        db.Users.Add(new AppUser
        {
            AccountName = "DOMAIN\\group.member",
            DisplayName = "Group Member",
            IsActive = true,
            GroupMemberships = [new AppUserGroupMembership { Group = assignedGroup }]
        });
        await db.SaveChangesAsync();

        var principal = AuthenticatedPrincipal("DOMAIN\\group.member");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.True(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView));
        Assert.False(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AccessManageGroups));
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(string accountName) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, accountName)], "Test"));
}

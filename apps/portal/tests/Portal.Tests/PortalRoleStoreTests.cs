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
    public async Task FindAccountAsync_ReadsTrackerUserCaseInsensitively()
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

        var account = await store.FindAccountAsync("sonaero/planner.one");

        Assert.Equal(PortalAccountLookupStatus.Found, account.Status);
        Assert.True(account.IsActive);
        Assert.Equal("Editor", account.Role);
    }

    [Fact]
    public async Task FindAccountAsync_ReadsAdministratorConfiguredNameCaseInsensitively()
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
            (await store.FindAccountAsync("sonaero/planner.one")).DisplayName);
    }

    [Fact]
    public async Task FindAccountAsync_DerivesRolesFromSharedGroupPermissions()
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
        var account = await store.FindAccountAsync("sonaero/estimator.one");

        Assert.Equal("Editor", Assert.Single(account.ModuleRoles).Value);
    }

    [Fact]
    public async Task FindAccountAsync_QualityEntryPermissionIsEnoughForLiveCatalogVisibility()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = "SONAERO\\Quality.One",
            DisplayName = "Quality One",
            Role = ApplicationRoles.Viewer,
            IsActive = true,
            ProjectTrackerGroupMemberships =
            [
                new PortalProjectTrackerMembershipRecord
                {
                    Group = new PortalProjectTrackerGroupRecord
                    {
                        Name = "Quality Module Access",
                        Permissions =
                        [
                            new PortalProjectTrackerPermissionRecord
                            {
                                PermissionKey = QualityAssurancePermissions.ModuleView
                            }
                        ]
                    }
                }
            ]
        });
        await db.SaveChangesAsync();

        var store = new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance);
        var account = await store.FindAccountAsync("sonaero/quality.one");

        Assert.Equal(ApplicationRoles.Viewer, account.ModuleRoles[ApplicationModules.QualityAssurance]);
    }

    [Fact]
    public async Task FindAccountAsync_PreservesInactiveState()
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

        var account = await store.FindAccountAsync("son4l/inactive.user");

        Assert.Equal(PortalAccountLookupStatus.Found, account.Status);
        Assert.False(account.IsActive);
        Assert.Equal("Admin", account.Role);
    }

    [Fact]
    public async Task FindAccountAsync_ReturnsMissingForUnknownAccount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var account = await new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance)
            .FindAccountAsync(@"SON4L\unknown.user");

        Assert.Equal(PortalAccountLookupStatus.Missing, account.Status);
        Assert.False(account.HasProjectTrackerAccess);
        Assert.Empty(account.ModuleRoles);
    }

    [Fact]
    public async Task FindAccountAsync_ActiveAccountWithoutAccessHasNoEffectiveApplications()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = @"SON4L\pending.user",
            DisplayName = "Pending User",
            Role = ApplicationRoles.Viewer,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var account = await new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance)
            .FindAccountAsync(@"SON4L\pending.user");

        Assert.Equal(PortalAccountLookupStatus.Found, account.Status);
        Assert.True(account.IsActive);
        Assert.False(account.HasProjectTrackerAccess);
        Assert.Empty(account.ModuleRoles);
    }

    [Fact]
    public async Task FindAccountAsync_RecognizesProjectTrackerModuleViewPermission()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = @"SON4L\tracker.viewer",
            DisplayName = "Tracker Viewer",
            Role = ApplicationRoles.Viewer,
            IsActive = true,
            ProjectTrackerGroupMemberships =
            [
                new PortalProjectTrackerMembershipRecord
                {
                    Group = new PortalProjectTrackerGroupRecord
                    {
                        Name = "Tracker viewers",
                        Permissions =
                        [
                            new PortalProjectTrackerPermissionRecord
                            {
                                PermissionKey = ApplicationPermissions.ModuleView
                            }
                        ]
                    }
                }
            ]
        });
        await db.SaveChangesAsync();

        var account = await new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance)
            .FindAccountAsync(@"SON4L\tracker.viewer");

        Assert.True(account.HasProjectTrackerAccess);
    }

    [Fact]
    public async Task FindAccountAsync_RecognizesValidDirectModuleAssignment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = @"SON4L\assigned.user",
            DisplayName = "Assigned User",
            Role = ApplicationRoles.Viewer,
            IsActive = true,
            ModuleAccessAssignments =
            [
                new PortalModuleAccessRecord
                {
                    ModuleKey = ApplicationModules.Engineering,
                    Role = ApplicationRoles.Viewer
                }
            ]
        });
        await db.SaveChangesAsync();

        var account = await new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance)
            .FindAccountAsync(@"SON4L\assigned.user");

        Assert.Equal(ApplicationRoles.Viewer, account.ModuleRoles[ApplicationModules.Engineering]);
    }

    [Fact]
    public async Task FindAccountAsync_ReturnsUnavailableWhenStoreCannotBeRead()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await connection.CloseAsync();

        var account = await new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance)
            .FindAccountAsync(@"SON4L\ordinary.user");

        Assert.Equal(PortalAccountLookupStatus.Unavailable, account.Status);
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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class AccessControlSeederTests
{
    [Fact]
    public async Task Seed_CreatesDefaultsAndUsesAdminPrecedence()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var configuration = Configuration(
            ("Security:Admins:0", "DOMAIN\\lead.user"),
            ("Security:Editors:0", "DOMAIN\\lead.user"));

        await new AccessControlSeeder().SeedAsync(fixture.Db, configuration);

        var user = await fixture.Db.Users
            .Include(candidate => candidate.GroupMemberships)
                .ThenInclude(membership => membership.Group)
            .SingleAsync(candidate => candidate.AccountName == "DOMAIN\\lead.user");
        Assert.Contains(user.GroupMemberships, membership => membership.Group.Name == ApplicationGroups.Administrators);
        Assert.DoesNotContain(user.GroupMemberships, membership => membership.Group.Name == ApplicationGroups.Managers);
        Assert.Contains(await fixture.Db.Groups.ToListAsync(), group => group.Name == ProjectTrackerGroups.ViewOnly);
        var administratorGroup = await fixture.Db.Groups
            .Include(group => group.Permissions)
            .SingleAsync(group => group.Name == ApplicationGroups.Administrators);
        Assert.True(administratorGroup.IsSystemGroup);
        Assert.Contains(administratorGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ProjectEditExternalLinks);
        Assert.Contains(administratorGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ArchivedDelete);
        Assert.Contains(administratorGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.WorkCentersImport);
        var managerGroup = await fixture.Db.Groups
            .Include(group => group.Permissions)
            .SingleAsync(group => group.Name == ApplicationGroups.Managers);
        Assert.False(managerGroup.IsSystemGroup);
        Assert.DoesNotContain(managerGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ProjectEditExternalLinks);
        Assert.DoesNotContain(managerGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ArchivedDelete);
        Assert.DoesNotContain(managerGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.WorkCentersImport);
        Assert.Contains(managerGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.OperationScheduleConfirm);
        var engineeringGroup = await fixture.Db.Groups
            .Include(group => group.Permissions)
            .SingleAsync(group => group.Name == ApplicationGroups.Engineering);
        Assert.False(engineeringGroup.IsSystemGroup);
        Assert.Contains(engineeringGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.OperationScheduleConfirm);
    }

    [Fact]
    public async Task Seed_DoesNotRecreateDeletedStarterGroupsOnRestart()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var seeder = new AccessControlSeeder();
        await seeder.SeedAsync(fixture.Db, Configuration());

        var removableGroups = await fixture.Db.Groups
            .Where(group => group.Name != ApplicationGroups.Administrators)
            .ToListAsync();
        Assert.NotEmpty(removableGroups);
        fixture.Db.Groups.RemoveRange(removableGroups);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await seeder.SeedAsync(
            fixture.Db,
            Configuration(("Security:Editors:0", "DOMAIN\\configured.editor")));

        var remainingGroup = Assert.Single(await fixture.Db.Groups.AsNoTracking().ToListAsync());
        Assert.Equal(ApplicationGroups.Administrators, remainingGroup.Name);
        Assert.True(remainingGroup.IsSystemGroup);
        Assert.False(await fixture.Db.Users.AnyAsync(user => user.AccountName == "DOMAIN\\configured.editor"));
    }

    [Fact]
    public async Task Seed_PreservesLegacyDefaultSystemFlagsForBinaryRollbackCompatibility()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var seeder = new AccessControlSeeder();
        await seeder.SeedAsync(fixture.Db, Configuration());

        var manager = await fixture.Db.Groups.SingleAsync(group => group.Name == ApplicationGroups.Managers);
        manager.IsSystemGroup = true;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await seeder.SeedAsync(fixture.Db, Configuration());

        Assert.True((await fixture.Db.Groups.AsNoTracking()
            .SingleAsync(group => group.Name == ApplicationGroups.Managers)).IsSystemGroup);
        Assert.True((await fixture.Db.Groups.AsNoTracking()
            .SingleAsync(group => group.Name == ApplicationGroups.Administrators)).IsSystemGroup);
    }

    [Fact]
    public async Task Seed_DoesNotUndoAdministrativeChangesOnRestart()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var configuration = Configuration(("Security:Admins:0", "DOMAIN\\admin.user"));
        var seeder = new AccessControlSeeder();
        await seeder.SeedAsync(fixture.Db, configuration);

        var administrator = await fixture.Db.Users
            .Include(user => user.GroupMemberships)
            .SingleAsync(user => user.AccountName == "DOMAIN\\admin.user");
        var adminGroup = await fixture.Db.Groups
            .Include(group => group.Permissions)
            .SingleAsync(group => group.Name == ApplicationGroups.Administrators);
        var viewOnlyGroup = await fixture.Db.Groups.SingleAsync(group => group.Name == ProjectTrackerGroups.ViewOnly);
        var removedPermission = adminGroup.Permissions.Single(permission =>
            permission.PermissionKey == ApplicationPermissions.ProjectCreate);
        var removedExternalLinksPermission = adminGroup.Permissions.Single(permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ProjectEditExternalLinks);
        var removedArchivedDeletePermission = adminGroup.Permissions.Single(permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ArchivedDelete);
        var removedOperationSchedulePermission = adminGroup.Permissions.Single(permission =>
            permission.PermissionKey == ProjectTrackerPermissions.OperationScheduleConfirm);
        var removedWorkCenterImportPermission = adminGroup.Permissions.Single(permission =>
            permission.PermissionKey == ProjectTrackerPermissions.WorkCentersImport);

        fixture.Db.GroupPermissions.Remove(removedPermission);
        fixture.Db.GroupPermissions.Remove(removedExternalLinksPermission);
        fixture.Db.GroupPermissions.Remove(removedArchivedDeletePermission);
        fixture.Db.GroupPermissions.Remove(removedOperationSchedulePermission);
        fixture.Db.GroupPermissions.Remove(removedWorkCenterImportPermission);
        administrator.IsActive = false;
        administrator.GroupMemberships.Clear();
        administrator.GroupMemberships.Add(new AppUserGroupMembership { AppGroupId = viewOnlyGroup.Id });
        fixture.Db.SetLegacyRole(administrator, "Viewer");
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await seeder.SeedAsync(fixture.Db, configuration);

        var savedUser = await fixture.Db.Users
            .AsNoTracking()
            .Include(user => user.GroupMemberships)
                .ThenInclude(membership => membership.Group)
            .SingleAsync(user => user.AccountName == "DOMAIN\\admin.user");
        Assert.False(savedUser.IsActive);
        Assert.Collection(savedUser.GroupMemberships, membership =>
            Assert.Equal(ProjectTrackerGroups.ViewOnly, membership.Group.Name));
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == adminGroup.Id
            && permission.PermissionKey == ApplicationPermissions.ProjectCreate));
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == adminGroup.Id
            && permission.PermissionKey == ProjectTrackerPermissions.ProjectEditExternalLinks));
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == adminGroup.Id
            && permission.PermissionKey == ProjectTrackerPermissions.ArchivedDelete));
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == adminGroup.Id
            && permission.PermissionKey == ProjectTrackerPermissions.OperationScheduleConfirm));
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == adminGroup.Id
            && permission.PermissionKey == ProjectTrackerPermissions.WorkCentersImport));
    }

    [Fact]
    public async Task Seed_DoesNotDuplicateSlashVariantOfExistingWindowsAccount()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        fixture.Db.Users.Add(new AppUser
        {
            AccountName = @"SON4L\jordan.greer",
            DisplayName = "Jordan Greer",
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();

        await new AccessControlSeeder().SeedAsync(
            fixture.Db,
            Configuration(("Security:Admins:0", "son4l/jordan.greer")));

        Assert.Single(await fixture.Db.Users.ToListAsync());
    }

    [Fact]
    public async Task Seed_RemovesAdministratorOnlyPermissionsFromNonAdministratorGroups()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        fixture.Db.Groups.Add(new AppGroup
        {
            Name = "Legacy Importers",
            Permissions =
            [
                new AppGroupPermission { PermissionKey = ApplicationPermissions.ImportManage },
                new AppGroupPermission { PermissionKey = ProjectTrackerPermissions.ArchivedDelete },
                new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView }
            ]
        });
        await fixture.Db.SaveChangesAsync();

        await new AccessControlSeeder().SeedAsync(fixture.Db, Configuration());

        var legacyGroup = await fixture.Db.Groups
            .AsNoTracking()
            .Include(group => group.Permissions)
            .SingleAsync(group => group.Name == "Legacy Importers");
        Assert.DoesNotContain(legacyGroup.Permissions, permission =>
            permission.PermissionKey == ApplicationPermissions.ImportManage);
        Assert.DoesNotContain(legacyGroup.Permissions, permission =>
            permission.PermissionKey == ProjectTrackerPermissions.ArchivedDelete);
        Assert.Contains(legacyGroup.Permissions, permission =>
            permission.PermissionKey == ApplicationPermissions.ModuleView);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    private sealed class AccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private AccessFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public ProjectTrackerDbContext Db { get; }

        public static async Task<AccessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new AccessFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

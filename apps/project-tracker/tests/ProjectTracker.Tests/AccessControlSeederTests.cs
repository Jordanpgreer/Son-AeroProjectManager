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

        fixture.Db.GroupPermissions.Remove(removedPermission);
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

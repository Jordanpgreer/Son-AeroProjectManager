using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class ModuleAccessServiceTests
{
    [Fact]
    public void Catalog_DefinesPermissionsForEverySupportedRole()
    {
        foreach (var module in ApplicationModuleCatalog.All)
        {
            Assert.NotEmpty(module.Roles);
            foreach (var role in module.Roles)
            {
                var permissions = ApplicationModuleCatalog.PermissionsFor(module.Key, role.Role);
                Assert.NotEmpty(permissions);
                Assert.All(
                    permissions,
                    permission => Assert.StartsWith($"{module.Key}.", permission.Key));
            }
        }
    }

    [Fact]
    public async Task BootstrapInitialAdministratorAssignments_GrantsConfiguredAdminButLeavesNormalUsersUnassigned()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var administrator = AddLegacyUser(fixture.Db, "DOMAIN\\admin", ApplicationRoles.Admin);
        var editor = AddLegacyUser(fixture.Db, "DOMAIN\\editor", ApplicationRoles.Editor);
        var viewer = AddLegacyUser(fixture.Db, "DOMAIN\\viewer", ApplicationRoles.Viewer);
        AddGroup(administrator, ApplicationGroups.Administrators);
        AddGroup(editor, ApplicationGroups.Managers);
        AddGroup(viewer, ProjectTracker.Api.Auth.ProjectTrackerGroups.ViewOnly);
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.BootstrapInitialAdministratorAssignmentsAsync(
            fixture.Db,
            ["domain/admin"]);

        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Engineering, ApplicationRoles.Admin);
        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Estimating, ApplicationRoles.Admin);
        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.QualityAssurance, ApplicationRoles.Admin);
        Assert.DoesNotContain(
            fixture.Db.UserModuleAccess.Local,
            access => access.AppUserId == editor.Id || access.AppUserId == viewer.Id);
        Assert.Equal(3, await fixture.Db.UserModuleAccess.CountAsync());
    }

    [Fact]
    public async Task BootstrapInitialAdministratorAssignments_DoesNotOverwriteExplicitAssignments()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var administrator = AddLegacyUser(fixture.Db, "DOMAIN\\admin", ApplicationRoles.Admin);
        AddGroup(administrator, ApplicationGroups.Administrators);
        administrator.ModuleAccessAssignments =
        [
            new AppUserModuleAccess
            {
                ModuleKey = ApplicationModules.Engineering,
                Role = null
            },
            new AppUserModuleAccess
            {
                ModuleKey = ApplicationModules.Estimating,
                Role = ApplicationRoles.Viewer
            }
        ];
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.BootstrapInitialAdministratorAssignmentsAsync(
            fixture.Db,
            ["DOMAIN\\admin"]);

        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Engineering, null);
        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Estimating, ApplicationRoles.Viewer);
        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.QualityAssurance, ApplicationRoles.Admin);
        Assert.Equal(3, await fixture.Db.UserModuleAccess.CountAsync());
    }

    [Fact]
    public async Task BootstrapInitialAdministratorAssignments_DoesNotGrantUnconfiguredAdministrator()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var configured = AddLegacyUser(fixture.Db, "DOMAIN\\configured", ApplicationRoles.Admin);
        var laterAdministrator = AddLegacyUser(fixture.Db, "DOMAIN\\later.admin", ApplicationRoles.Admin);
        var administrators = new AppGroup { Name = ApplicationGroups.Administrators };
        configured.GroupMemberships.Add(new AppUserGroupMembership { Group = administrators });
        laterAdministrator.GroupMemberships.Add(new AppUserGroupMembership { Group = administrators });
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.BootstrapInitialAdministratorAssignmentsAsync(
            fixture.Db,
            ["DOMAIN\\configured"]);

        Assert.Equal(3, await fixture.Db.UserModuleAccess.CountAsync());
        Assert.DoesNotContain(
            fixture.Db.UserModuleAccess.Local,
            access => access.AppUserId == laterAdministrator.Id);
    }

    [Fact]
    public async Task SetAsync_RejectsUnknownModulesAndRoles()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var user = AddLegacyUser(fixture.Db, "DOMAIN\\user", ApplicationRoles.Viewer);
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ModuleAccessValidationException>(() =>
            fixture.Service.SetAsync(fixture.Db, user.Id, "unknown", true, ApplicationRoles.Viewer));
        await Assert.ThrowsAsync<ModuleAccessValidationException>(() =>
            fixture.Service.SetAsync(fixture.Db, user.Id, ApplicationModules.Engineering, true, "Owner"));
        await fixture.Service.SetAsync(
            fixture.Db,
            user.Id,
            ApplicationModules.QualityAssurance,
            true,
            ApplicationRoles.Viewer);
        AssertAssignment(fixture.Db, user.Id, ApplicationModules.QualityAssurance, ApplicationRoles.Viewer);
    }

    [Fact]
    public async Task SetAsync_PreventsRemovingTheLastActiveModuleAdministrator()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var first = AddLegacyUser(fixture.Db, "DOMAIN\\first", ApplicationRoles.Admin);
        var second = AddLegacyUser(fixture.Db, "DOMAIN\\second", ApplicationRoles.Admin);
        first.ModuleAccessAssignments.Add(Assignment(ApplicationModules.Engineering, ApplicationRoles.Admin));
        second.ModuleAccessAssignments.Add(Assignment(ApplicationModules.Engineering, ApplicationRoles.Admin));
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.SetAsync(
            fixture.Db,
            first.Id,
            ApplicationModules.Engineering,
            false,
            null);

        var exception = await Assert.ThrowsAsync<LastModuleAdministratorException>(() =>
            fixture.Service.SetAsync(
                fixture.Db,
                second.Id,
                ApplicationModules.Engineering,
                true,
                ApplicationRoles.Editor));

        Assert.Equal(ApplicationModules.Engineering, exception.ModuleKey);
        Assert.Equal(
            ApplicationRoles.Admin,
            await fixture.Db.UserModuleAccess
                .Where(access =>
                    access.AppUserId == second.Id
                    && access.ModuleKey == ApplicationModules.Engineering)
                .Select(access => access.Role)
                .SingleAsync());
    }

    [Fact]
    public async Task EnsureUserCanBeDeactivated_ProtectsLastActiveAdministrators()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var user = AddLegacyUser(fixture.Db, "DOMAIN\\admin", ApplicationRoles.Admin);
        user.ModuleAccessAssignments.Add(Assignment(ApplicationModules.Estimating, ApplicationRoles.Admin));
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<LastModuleAdministratorException>(() =>
            fixture.Service.EnsureUserCanBeDeactivatedAsync(fixture.Db, user.Id));

        Assert.Equal(ApplicationModules.Estimating, exception.ModuleKey);
    }

    private static AppUser AddLegacyUser(
        ProjectTrackerDbContext db,
        string accountName,
        string role)
    {
        var user = new AppUser
        {
            AccountName = accountName,
            DisplayName = accountName,
            IsActive = true
        };
        db.Users.Add(user);
        db.SetLegacyRole(user, role);
        return user;
    }

    private static AppUserModuleAccess Assignment(string moduleKey, string? role) =>
        new()
        {
            ModuleKey = moduleKey,
            Role = role
        };

    private static void AddGroup(AppUser user, string groupName)
    {
        user.GroupMemberships.Add(new AppUserGroupMembership
        {
            Group = new AppGroup { Name = groupName }
        });
    }

    private static void AssertAssignment(
        ProjectTrackerDbContext db,
        int userId,
        string moduleKey,
        string? role)
    {
        var assignment = db.UserModuleAccess.Local.Single(access =>
            access.AppUserId == userId && access.ModuleKey == moduleKey);
        Assert.Equal(role, assignment.Role);
    }

    private sealed class ModuleAccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ModuleAccessFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public ProjectTrackerDbContext Db { get; }
        public ModuleAccessService Service { get; } = new();

        public static async Task<ModuleAccessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new ModuleAccessFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

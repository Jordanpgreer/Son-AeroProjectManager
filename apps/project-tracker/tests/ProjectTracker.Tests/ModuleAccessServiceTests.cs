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
    public void Catalog_DerivesInheritedPermissionsForEveryRole()
    {
        foreach (var module in ApplicationModuleCatalog.All)
        {
            var viewer = ApplicationModuleCatalog.PermissionsFor(module.Key, ApplicationRoles.Viewer);
            var editor = ApplicationModuleCatalog.PermissionsFor(module.Key, ApplicationRoles.Editor);
            var admin = ApplicationModuleCatalog.PermissionsFor(module.Key, ApplicationRoles.Admin);

            Assert.NotEmpty(viewer);
            Assert.True(editor.Count > viewer.Count);
            Assert.True(admin.Count > editor.Count);
            Assert.Subset(
                editor.Select(permission => permission.Key).ToHashSet(),
                viewer.Select(permission => permission.Key).ToHashSet());
            Assert.Subset(
                admin.Select(permission => permission.Key).ToHashSet(),
                editor.Select(permission => permission.Key).ToHashSet());
            Assert.All(admin, permission => Assert.StartsWith($"{module.Key}.", permission.Key));
        }
    }

    [Fact]
    public async Task BootstrapLegacyAssignments_UsesModuleSpecificCompatibilityRules()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var administrator = AddLegacyUser(fixture.Db, "DOMAIN\\admin", ApplicationRoles.Admin);
        var editor = AddLegacyUser(fixture.Db, "DOMAIN\\editor", ApplicationRoles.Editor);
        var viewer = AddLegacyUser(fixture.Db, "DOMAIN\\viewer", ApplicationRoles.Viewer);
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.BootstrapLegacyAssignmentsAsync(fixture.Db);

        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Engineering, ApplicationRoles.Admin);
        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Estimating, ApplicationRoles.Admin);
        AssertAssignment(fixture.Db, editor.Id, ApplicationModules.Engineering, null);
        AssertAssignment(fixture.Db, editor.Id, ApplicationModules.Estimating, ApplicationRoles.Editor);
        AssertAssignment(fixture.Db, viewer.Id, ApplicationModules.Engineering, null);
        AssertAssignment(fixture.Db, viewer.Id, ApplicationModules.Estimating, ApplicationRoles.Viewer);
        Assert.Equal(6, await fixture.Db.UserModuleAccess.CountAsync());
    }

    [Fact]
    public async Task BootstrapLegacyAssignments_DoesNotOverwriteExplicitAssignments()
    {
        await using var fixture = await ModuleAccessFixture.CreateAsync();
        var administrator = AddLegacyUser(fixture.Db, "DOMAIN\\admin", ApplicationRoles.Admin);
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

        await fixture.Service.BootstrapLegacyAssignmentsAsync(fixture.Db);

        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Engineering, null);
        AssertAssignment(fixture.Db, administrator.Id, ApplicationModules.Estimating, ApplicationRoles.Viewer);
        Assert.Equal(2, await fixture.Db.UserModuleAccess.CountAsync());
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

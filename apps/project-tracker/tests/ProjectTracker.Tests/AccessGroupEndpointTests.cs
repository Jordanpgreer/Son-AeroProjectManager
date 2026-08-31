using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class AccessGroupEndpointTests
{
    [Fact]
    public void DeleteGroupRoute_RequiresManageGroupsPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentUserService>();
        builder.Services.AddScoped<ModuleAccessService>();
        builder.Services.AddDbContext<ProjectTrackerDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapUserEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/admin/groups/{id:int}"
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);

        Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == "ManageGroups");
    }

    [Fact]
    public void EstimatingHistoryImportRoute_RequiresManageGroupsPolicyAndPut()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentUserService>();
        builder.Services.AddScoped<ModuleAccessService>();
        builder.Services.AddDbContext<ProjectTrackerDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapUserEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText ==
                "/api/admin/groups/{id:int}/estimating-history-import");

        Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == "ManageGroups");
        Assert.Equal(
            ["PUT"],
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public async Task CreateGroup_RejectsClientCreatedSystemGroupsAndInvalidPermissions()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var systemResult = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto("Fake system group", null, true, []),
            fixture.Db,
            CancellationToken.None);
        var permissionResult = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto("Unsafe permissions", null, false, ["unknown.permission"]),
            fixture.Db,
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(systemResult);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(permissionResult);
        Assert.Empty(await fixture.Db.Groups.ToListAsync());
    }

    [Fact]
    public async Task CreateGroup_ValidatesStorageLengthsBeforeWriting()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var nameResult = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto(new string('n', 81), null, false, []),
            fixture.Db,
            CancellationToken.None);
        var descriptionResult = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto("Valid name", new string('d', 241), false, []),
            fixture.Db,
            CancellationToken.None);
        var permissionsResult = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto("Missing permissions", null, false, null),
            fixture.Db,
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(nameResult);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(descriptionResult);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(permissionsResult);
        Assert.Empty(await fixture.Db.Groups.ToListAsync());
    }

    [Fact]
    public async Task CreateGroup_PersistsSelectedPermissionsAndEnforcesCaseInsensitiveNames()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var created = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto(
                "Project Coordinators",
                "Coordinates active projects",
                false,
                [ApplicationPermissions.ModuleView]),
            fixture.Db,
            CancellationToken.None);
        var duplicate = await UserEndpoints.CreateGroupAsync(
            new AccessGroupUpsertDto("project coordinators", null, false, []),
            fixture.Db,
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Created<AccessGroupDto>>(created);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(duplicate);
        var group = await fixture.Db.Groups.Include(candidate => candidate.Permissions).SingleAsync();
        Assert.Equal("Project Coordinators", group.Name);
        Assert.Equal("Coordinates active projects", group.Description);
        Assert.Equal(ApplicationPermissions.ModuleView, Assert.Single(group.Permissions).PermissionKey);
    }

    [Fact]
    public async Task EstimatingHistoryImportUpdate_ChangesOnlyImportAndPreservesCurrentPermissions()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var group = new AppGroup
        {
            Name = "Estimating Importers",
            Permissions =
            [
                new AppGroupPermission { PermissionKey = "estimating.view" },
                new AppGroupPermission { PermissionKey = "estimating.history.view" },
                new AppGroupPermission { PermissionKey = ApplicationPermissions.ProjectCreate }
            ]
        };
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();

        // This grant represents a permission added after the Portal loaded its overview.
        group.Permissions.Add(new AppGroupPermission
        {
            PermissionKey = EngineeringPermissions.ModuleView
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var enabled = await UserEndpoints.UpdateEstimatingHistoryImportAccessAsync(
            group.Id,
            new EstimatingHistoryImportAccessUpdateDto(true),
            fixture.Db,
            CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<AccessGroupDto>>(enabled);

        var enabledPermissions = await fixture.Db.GroupPermissions
            .Where(permission => permission.AppGroupId == group.Id)
            .Select(permission => permission.PermissionKey)
            .ToListAsync();
        Assert.Contains(ApplicationPermissions.ProjectCreate, enabledPermissions);
        Assert.Contains(EngineeringPermissions.ModuleView, enabledPermissions);
        Assert.Contains("estimating.history.import", enabledPermissions);

        var enabledAgain = await UserEndpoints.UpdateEstimatingHistoryImportAccessAsync(
            group.Id,
            new EstimatingHistoryImportAccessUpdateDto(true),
            fixture.Db,
            CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<AccessGroupDto>>(enabledAgain);
        Assert.Equal(1, await fixture.Db.GroupPermissions.CountAsync(permission =>
            permission.AppGroupId == group.Id
            && permission.PermissionKey == "estimating.history.import"));

        var disabled = await UserEndpoints.UpdateEstimatingHistoryImportAccessAsync(
            group.Id,
            new EstimatingHistoryImportAccessUpdateDto(false),
            fixture.Db,
            CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<AccessGroupDto>>(disabled);

        var disabledPermissions = await fixture.Db.GroupPermissions
            .Where(permission => permission.AppGroupId == group.Id)
            .Select(permission => permission.PermissionKey)
            .ToListAsync();
        Assert.Contains(ApplicationPermissions.ProjectCreate, disabledPermissions);
        Assert.Contains(EngineeringPermissions.ModuleView, disabledPermissions);
        Assert.DoesNotContain("estimating.history.import", disabledPermissions);
    }

    [Fact]
    public async Task EstimatingHistoryImportUpdate_RequiresModuleAndHistoryViewBeforeEnable()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var group = new AppGroup
        {
            Name = "Incomplete Estimating Access",
            Permissions =
            [
                new AppGroupPermission { PermissionKey = "estimating.view" },
                new AppGroupPermission { PermissionKey = ApplicationPermissions.ProjectCreate }
            ]
        };
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();

        var result = await UserEndpoints.UpdateEstimatingHistoryImportAccessAsync(
            group.Id,
            new EstimatingHistoryImportAccessUpdateDto(true),
            fixture.Db,
            CancellationToken.None);

        var badRequest = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(result);
        Assert.Contains("View Estimating Logs", badRequest.Value);
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == group.Id
            && permission.PermissionKey == "estimating.history.import"));
        Assert.True(await fixture.Db.GroupPermissions.AnyAsync(permission =>
            permission.AppGroupId == group.Id
            && permission.PermissionKey == ApplicationPermissions.ProjectCreate));
    }

    [Fact]
    public async Task DeleteGroup_RejectsProtectedSystemGroup()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var group = new AppGroup { Name = ApplicationGroups.Administrators, IsSystemGroup = true };
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();

        var result = await UserEndpoints.DeleteGroupAsync(group.Id, fixture.Db, CancellationToken.None);

        var conflict = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Conflict<AccessGroupDeleteConflictDto>>(result);
        Assert.Equal("SystemGroup", conflict.Value!.Code);
        Assert.True(await fixture.Db.Groups.AnyAsync(candidate => candidate.Id == group.Id));
    }

    [Fact]
    public async Task DeleteGroup_RejectsGroupWithAssignedUsers()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var group = new AppGroup { Name = "In use" };
        var user = new AppUser
        {
            AccountName = @"SON4L\assigned.user",
            DisplayName = "Assigned User",
            GroupMemberships = [new AppUserGroupMembership { Group = group }]
        };
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();

        var result = await UserEndpoints.DeleteGroupAsync(group.Id, fixture.Db, CancellationToken.None);

        var conflict = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Conflict<AccessGroupDeleteConflictDto>>(result);
        Assert.Equal("GroupInUse", conflict.Value!.Code);
        Assert.Equal(1, conflict.Value.UserCount);
        Assert.True(await fixture.Db.Groups.AnyAsync(candidate => candidate.Id == group.Id));
        Assert.True(await fixture.Db.Users.AnyAsync(candidate => candidate.Id == user.Id));
    }

    [Fact]
    public async Task DeleteGroup_AllowsUnusedLegacyDefaultGroupEvenWhenItsSystemFlagIsStale()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var group = new AppGroup { Name = ApplicationGroups.Engineering, IsSystemGroup = true };
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();

        var result = await UserEndpoints.DeleteGroupAsync(group.Id, fixture.Db, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(result);
        Assert.False(await fixture.Db.Groups.AnyAsync(candidate => candidate.Id == group.Id));
    }

    [Fact]
    public async Task DeleteGroup_RemovesUnusedCustomGroupAndItsPermissions()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var group = new AppGroup
        {
            Name = "Temporary coordinators",
            Permissions = [new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView }]
        };
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();
        var groupId = group.Id;
        fixture.Db.ChangeTracker.Clear();

        var result = await UserEndpoints.DeleteGroupAsync(groupId, fixture.Db, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(result);
        Assert.False(await fixture.Db.Groups.AnyAsync(candidate => candidate.Id == groupId));
        Assert.False(await fixture.Db.GroupPermissions.AnyAsync(permission => permission.AppGroupId == groupId));
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private DatabaseFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public ProjectTrackerDbContext Db { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DatabaseFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

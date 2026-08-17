using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class RoleClaimsTransformationTests
{
    [Fact]
    public async Task TransformAsync_PreviewUsesOnlyLiveTargetPermissionsAndPreservesActorIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var administrators = new AppGroup
        {
            Name = ApplicationGroups.Administrators,
            Permissions =
            [
                new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView },
                new AppGroupPermission { PermissionKey = ApplicationPermissions.AccessManageUsers }
            ]
        };
        var viewers = new AppGroup
        {
            Name = "Preview viewers",
            Permissions = [new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView }]
        };
        var actor = new AppUser
        {
            AccountName = @"SON4L\admin.user",
            DisplayName = "Admin User",
            IsActive = true,
            GroupMemberships = [new AppUserGroupMembership { Group = administrators }]
        };
        var target = new AppUser
        {
            AccountName = @"SON4L\viewer.user",
            DisplayName = "Viewer User",
            IsActive = true,
            GroupMemberships = [new AppUserGroupMembership { Group = viewers }]
        };
        db.Users.AddRange(actor, target);
        db.SetLegacyRole(actor, "Admin");
        await db.SaveChangesAsync();

        var token = AccessPreviewTokens.Create();
        db.AccessPreviewSessions.Add(new AccessPreviewSessionRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = AccessPreviewTokens.Hash(token),
            AdministratorAccountName = actor.AccountName,
            TargetKey = $"{AccessPreviewTargetKinds.User}:{target.Id}",
            ApplicationId = AccessPreviewApplications.ProjectTracker,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LaunchExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            RedeemedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.User = AuthenticatedPrincipal(actor.AccountName);
        httpContext.Request.Headers.Cookie = $"{ProjectTrackerAccessPreviewService.CookieName}={token}";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = new ProjectTrackerAccessPreviewService(db, new ConfigurationBuilder().Build());

        await new RoleClaimsTransformation(db, service, accessor).TransformAsync(httpContext.User);

        Assert.Equal(actor.AccountName, httpContext.User.Identity!.Name);
        Assert.True(httpContext.User.HasClaim(AccessPreviewClaimTypes.Active, "true"));
        Assert.True(httpContext.User.HasClaim(ApplicationClaimTypes.Group, viewers.Name));
        Assert.True(httpContext.User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView));
        Assert.False(httpContext.User.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AccessManageUsers));
    }

    [Fact]
    public async Task RedeemAsync_IsSingleUseAndRotatesTheLaunchToken()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var actor = new AppUser { AccountName = @"SON4L\admin.user", DisplayName = "Admin", IsActive = true };
        var group = new AppGroup
        {
            Name = "View only",
            Permissions = [new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView }]
        };
        db.Users.Add(actor);
        db.Groups.Add(group);
        db.SetLegacyRole(actor, "Admin");
        await db.SaveChangesAsync();

        var launchToken = AccessPreviewTokens.Create();
        db.AccessPreviewSessions.Add(new AccessPreviewSessionRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = AccessPreviewTokens.Hash(launchToken),
            AdministratorAccountName = actor.AccountName,
            TargetKey = $"{AccessPreviewTargetKinds.ProjectTrackerGroup}:{group.Id}",
            ApplicationId = AccessPreviewApplications.ProjectTracker,
            IssuedAt = DateTimeOffset.UtcNow,
            LaunchExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        });
        await db.SaveChangesAsync();

        var service = new ProjectTrackerAccessPreviewService(db, new ConfigurationBuilder().Build());
        var principal = AuthenticatedPrincipal(actor.AccountName);
        var first = await service.RedeemAsync(principal, launchToken);
        var second = await service.RedeemAsync(principal, launchToken);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.NotEqual(launchToken, first.SessionToken);
        Assert.Equal(
            AccessPreviewTokens.Hash(first.SessionToken!),
            (await db.AccessPreviewSessions.AsNoTracking().SingleAsync()).TokenHash);
    }

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
        Assert.True(principal.HasClaim(ApplicationClaimTypes.DisplayName, "Planner One"));
        Assert.True(principal.HasClaim(ApplicationClaimTypes.Group, ApplicationGroups.Engineering));
        Assert.True(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView));
        Assert.True(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.TaskEditEstimatedDuration));
        Assert.True(principal.IsInRole("Viewer"));

        var httpContext = new DefaultHttpContext { User = principal };
        var currentUser = new CurrentUserService(new HttpContextAccessor { HttpContext = httpContext });
        Assert.Equal("Planner One", currentUser.DisplayName);
    }

    [Fact]
    public async Task TransformAsync_MatchesLegacyForwardSlashAssignmentToWindowsIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var group = new AppGroup
        {
            Name = ApplicationGroups.Administrators,
            Permissions = [new AppGroupPermission { PermissionKey = ApplicationPermissions.AccessManageUsers }]
        };
        db.Users.Add(new AppUser
        {
            AccountName = "son4l/jordan.greer",
            DisplayName = "Jordan Greer",
            IsActive = true,
            GroupMemberships = [new AppUserGroupMembership { Group = group }]
        });
        await db.SaveChangesAsync();

        var principal = AuthenticatedPrincipal(@"SON4L\jordan.greer");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.True(principal.HasClaim(ApplicationClaimTypes.RegisteredUser, "true"));
        Assert.True(principal.HasClaim(
            ApplicationClaimTypes.Permission,
            ApplicationPermissions.AccessManageUsers));
        Assert.Single(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task TransformAsync_DeniesUnknownAuthenticatedUserWithoutPersistingIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await new AccessControlSeeder().SeedAsync(db, new ConfigurationBuilder().Build());

        var principal = AuthenticatedPrincipal("DOMAIN\\new.user");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.False(principal.HasClaim(ApplicationClaimTypes.RegisteredUser, "true"));
        Assert.False(principal.HasClaim(ApplicationClaimTypes.Group, ProjectTrackerGroups.ViewOnly));
        Assert.False(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView));
        Assert.False(principal.HasClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ProjectCreate));
        Assert.False(principal.IsInRole("Viewer"));
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task TransformAsync_DoesNotReactivateInactiveUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new AppUser
        {
            AccountName = "DOMAIN\\inactive.user",
            DisplayName = "Inactive User",
            IsActive = false
        });
        await db.SaveChangesAsync();

        var principal = AuthenticatedPrincipal("DOMAIN\\inactive.user");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.False(principal.HasClaim(ApplicationClaimTypes.RegisteredUser, "true"));
        Assert.False((await db.Users.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task TransformAsync_ActiveRegisteredUserWithoutGroups_HasNoModuleViewPermission()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new AppUser
        {
            AccountName = "DOMAIN\\registered.no.groups",
            DisplayName = "Registered No Groups",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var principal = AuthenticatedPrincipal("DOMAIN\\registered.no.groups");
        await new RoleClaimsTransformation(db).TransformAsync(principal);

        Assert.True(principal.HasClaim(ApplicationClaimTypes.RegisteredUser, "true"));
        Assert.False(principal.HasClaim(
            ApplicationClaimTypes.Permission,
            ApplicationPermissions.ModuleView));
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

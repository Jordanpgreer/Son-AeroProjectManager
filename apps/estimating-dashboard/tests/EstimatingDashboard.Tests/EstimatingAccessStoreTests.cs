using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingAccessStoreTests
{
    [Fact]
    public async Task FindsActiveUserWithEstimatingGroupPermissions()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var group = new EstimatingAccessGroupRecord
        {
            Name = "Estimating Editors",
            Permissions = EstimatingPermissions.ForRole(EstimatingRoles.Editor)
                .Select(permission => new EstimatingGroupPermissionRecord { PermissionKey = permission })
                .ToList()
        };
        fixture.Db.Users.Add(new EstimatingUserRecord
        {
            Id = 7,
            AccountName = "SONAERO\\estimator",
            DisplayName = "Estimator",
            IsActive = true,
            GroupMemberships =
            [
                new EstimatingUserGroupMembershipRecord { AppUserId = 7, Group = group }
            ]
        });
        await fixture.Db.SaveChangesAsync();

        var access = await fixture.Store.FindEnabledAsync("sonaero/ESTIMATOR");

        Assert.NotNull(access);
        Assert.Equal(EstimatingRoles.Editor, access.Role);
        Assert.Contains(EstimatingPermissions.ManageQuotes, access.Permissions);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DeniesInactiveOrMissingGroupPermission(
        bool active,
        bool hasPermission)
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var group = new EstimatingAccessGroupRecord
        {
            Name = "No Estimating Access",
            Permissions = hasPermission
                ? [new EstimatingGroupPermissionRecord { PermissionKey = EstimatingPermissions.View }]
                : []
        };
        fixture.Db.Users.Add(new EstimatingUserRecord
        {
            Id = 8,
            AccountName = "SONAERO\\denied",
            DisplayName = "Denied",
            IsActive = active,
            GroupMemberships =
            [
                new EstimatingUserGroupMembershipRecord { AppUserId = 8, Group = group }
            ]
        });
        await fixture.Db.SaveChangesAsync();

        var access = await fixture.Store.FindEnabledAsync("SONAERO\\denied");

        Assert.Null(access);
    }

    [Fact]
    public async Task Preview_redeems_once_and_uses_the_target_users_live_estimating_role()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var administrator = new EstimatingUserRecord
        {
            Id = 20,
            AccountName = "SONAERO\\administrator",
            DisplayName = "Administrator",
            PortalRole = ApplicationRoles.Admin,
            IsActive = true
        };
        var target = new EstimatingUserRecord
        {
            Id = 21,
            AccountName = "SONAERO\\viewer",
            DisplayName = "Estimating Viewer",
            PortalRole = ApplicationRoles.Viewer,
            IsActive = true,
            GroupMemberships =
            [
                new EstimatingUserGroupMembershipRecord
                {
                    AppUserId = 21,
                    Group = new EstimatingAccessGroupRecord
                    {
                        Name = "Estimating Viewers",
                        Permissions = EstimatingPermissions.ForRole(EstimatingRoles.Viewer)
                            .Select(permission => new EstimatingGroupPermissionRecord { PermissionKey = permission })
                            .ToList()
                    }
                }
            ]
        };
        fixture.Db.Users.AddRange(administrator, target);
        var token = AccessPreviewTokens.Create();
        fixture.Db.AccessPreviewSessions.Add(new AccessPreviewSessionRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = AccessPreviewTokens.Hash(token),
            AdministratorAccountName = administrator.AccountName,
            TargetKey = $"{AccessPreviewTargetKinds.User}:{target.Id}",
            ApplicationId = AccessPreviewApplications.Estimating,
            IssuedAt = DateTimeOffset.UtcNow,
            LaunchExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await fixture.Db.SaveChangesAsync();

        var service = new EstimatingAccessPreviewService(
            fixture.Db,
            new ConfigurationBuilder().Build());
        var start = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, administrator.AccountName)], "Test"))
        };
        var first = await service.StartAsync(start, token);
        var second = await service.StartAsync(start, token);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(EstimatingAccessPreviewService.CookieName, start.Response.Headers.SetCookie.ToString());

        var request = new DefaultHttpContext { User = start.User };
        request.Request.Headers.Cookie = $"{EstimatingAccessPreviewService.CookieName}={token}";
        var access = await service.ResolveActiveAsync(request);

        Assert.NotNull(access);
        Assert.True(access.IsPreview);
        Assert.Equal(EstimatingRoles.Viewer, access.Role);
        Assert.Equal("SONAERO\\viewer", access.AccountName);
        Assert.Equal("SONAERO\\administrator", access.PreviewActorAccountName);
        Assert.DoesNotContain(EstimatingPermissions.ManageQuotes, access.Permissions);
    }

    private sealed class AccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private AccessFixture(
            SqliteConnection connection,
            EstimatingAccessDbContext db)
        {
            this.connection = connection;
            Db = db;
            Store = new EstimatingAccessStore(
                db,
                NullLogger<EstimatingAccessStore>.Instance);
        }

        public EstimatingAccessDbContext Db { get; }
        public EstimatingAccessStore Store { get; }

        public static async Task<AccessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new EstimatingAccessDbContext(options);
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

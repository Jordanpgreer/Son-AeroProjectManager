using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityAssuranceAccessPreviewServiceTests
{
    [Fact]
    public async Task User_preview_redeems_once_and_resolves_effective_quality_permissions()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var administrator = Administrator();
        var target = new QualityAssuranceUserRecord
        {
            AccountName = @"SONAERO\quality.viewer",
            DisplayName = "Quality Viewer",
            PortalRole = ApplicationRoles.Viewer,
            IsActive = true,
            GroupMemberships =
            [
                new QualityAssuranceUserGroupMembershipRecord
                {
                    Group = new QualityAssuranceAccessGroupRecord
                    {
                        Name = "Quality Viewers",
                        Permissions =
                        [
                            new QualityAssuranceGroupPermissionRecord
                            {
                                PermissionKey = QualityAssurancePermissions.ModuleView
                            },
                            new QualityAssuranceGroupPermissionRecord
                            {
                                PermissionKey = QualityAssurancePermissions.ShipmentsView
                            }
                        ]
                    }
                }
            ]
        };
        fixture.Db.Users.AddRange(administrator, target);
        await fixture.Db.SaveChangesAsync();
        var token = await fixture.AddSessionAsync(
            administrator.AccountName,
            $"{AccessPreviewTargetKinds.User}:{target.Id}");
        var service = fixture.CreateService();
        var start = Context(administrator.AccountName);

        var first = await service.StartAsync(start, token);
        var second = await service.StartAsync(start, token);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(QualityAssuranceAccessPreviewService.CookieName, start.Response.Headers.SetCookie.ToString());

        var request = Context(administrator.AccountName);
        request.Request.Headers.Cookie = $"{QualityAssuranceAccessPreviewService.CookieName}={token}";
        var access = await service.ResolveActiveAsync(request);

        Assert.NotNull(access);
        Assert.True(access.IsPreview);
        Assert.Equal(target.Id, access.UserId);
        Assert.Equal(target.AccountName, access.AccountName);
        Assert.Equal("Quality Viewer", access.DisplayName);
        Assert.Equal(ApplicationRoles.Viewer, access.Role);
        Assert.Equal(administrator.AccountName, access.PreviewActorAccountName);
        Assert.Contains(QualityAssurancePermissions.ShipmentsView, access.Permissions);
        Assert.DoesNotContain(QualityAssurancePermissions.ShipmentCreate, access.Permissions);
    }

    [Fact]
    public async Task Shared_group_preview_resolves_the_groups_exact_permissions_without_a_user_identity()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var administrator = Administrator();
        var group = new QualityAssuranceAccessGroupRecord
        {
            Name = "Quality",
            Permissions =
            [
                new QualityAssuranceGroupPermissionRecord
                {
                    PermissionKey = QualityAssurancePermissions.ModuleView
                },
                new QualityAssuranceGroupPermissionRecord
                {
                    PermissionKey = QualityAssurancePermissions.TeamDashboardView
                }
            ]
        };
        fixture.Db.Users.Add(administrator);
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();
        var token = await fixture.AddSessionAsync(
            administrator.AccountName,
            $"{AccessPreviewTargetKinds.ProjectTrackerGroup}:{group.Id}");
        var service = fixture.CreateService();
        var start = Context(administrator.AccountName);
        Assert.True((await service.StartAsync(start, token)).Succeeded);
        var request = Context(administrator.AccountName);
        request.Request.Headers.Cookie = $"{QualityAssuranceAccessPreviewService.CookieName}={token}";

        var access = await service.ResolveActiveAsync(request);

        Assert.NotNull(access);
        Assert.True(access.UserId < 0);
        Assert.Equal("Quality group", access.DisplayName);
        Assert.Equal("Quality", Assert.Single(access.Groups).Name);
        Assert.Contains(QualityAssurancePermissions.TeamDashboardView, access.Permissions);
        Assert.DoesNotContain(QualityAssurancePermissions.ShipmentCreate, access.Permissions);
    }

    [Fact]
    public async Task Target_without_quality_module_access_cannot_start_preview()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var administrator = Administrator();
        var group = new QualityAssuranceAccessGroupRecord
        {
            Name = "Unrelated",
            Permissions =
            [
                new QualityAssuranceGroupPermissionRecord
                {
                    PermissionKey = ApplicationPermissions.ModuleView
                }
            ]
        };
        fixture.Db.Users.Add(administrator);
        fixture.Db.Groups.Add(group);
        await fixture.Db.SaveChangesAsync();
        var token = await fixture.AddSessionAsync(
            administrator.AccountName,
            $"{AccessPreviewTargetKinds.ProjectTrackerGroup}:{group.Id}");

        var result = await fixture.CreateService().StartAsync(Context(administrator.AccountName), token);

        Assert.False(result.Succeeded);
        Assert.Equal("AccessPreviewTargetUnavailable", result.ErrorCode);
    }

    private static QualityAssuranceUserRecord Administrator() => new()
    {
        AccountName = @"SONAERO\administrator",
        DisplayName = "Administrator",
        PortalRole = ApplicationRoles.Admin,
        IsActive = true
    };

    private static DefaultHttpContext Context(string accountName) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, accountName)],
            "Test"))
    };

    private sealed class AccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private AccessFixture(SqliteConnection connection, QualityAssuranceAccessDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public QualityAssuranceAccessDbContext Db { get; }

        public QualityAssuranceAccessPreviewService CreateService() => new(
            Db,
            new ConfigurationBuilder().Build());

        public async Task<string> AddSessionAsync(string administrator, string targetKey)
        {
            var token = AccessPreviewTokens.Create();
            Db.AccessPreviewSessions.Add(new AccessPreviewSessionRecord
            {
                Id = Guid.NewGuid(),
                TokenHash = AccessPreviewTokens.Hash(token),
                AdministratorAccountName = administrator,
                TargetKey = targetKey,
                ApplicationId = AccessPreviewApplications.QualityAssurance,
                IssuedAt = DateTimeOffset.UtcNow,
                LaunchExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
                SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            });
            await Db.SaveChangesAsync();
            return token;
        }

        public static async Task<AccessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<QualityAssuranceAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new QualityAssuranceAccessDbContext(options);
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

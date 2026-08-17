using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityAssuranceAccessStoreTests
{
    [Fact]
    public async Task Active_user_with_quality_group_permission_can_open_quality_assurance()
    {
        await using var fixture = await QualityAccessFixture.CreateAsync();
        fixture.AddUser(
            "DOMAIN\\qa.admin",
            true,
            true);
        await fixture.Db.SaveChangesAsync();

        var access = await fixture.Store.FindAccessAsync("domain/qa.admin");

        Assert.NotNull(access);
        Assert.Equal("DOMAIN\\qa.admin", access.AccountName);
        Assert.Equal(ApplicationRoles.Viewer, access.Role);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_permission_or_inactive_accounts_are_denied(
        bool hasPermission,
        bool isActive)
    {
        await using var fixture = await QualityAccessFixture.CreateAsync();
        fixture.AddUser("DOMAIN\\denied", hasPermission, isActive);
        await fixture.Db.SaveChangesAsync();

        var access = await fixture.Store.FindAccessAsync("DOMAIN\\denied");

        Assert.Null(access);
    }

    private sealed class QualityAccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private QualityAccessFixture(
            SqliteConnection connection,
            QualityAssuranceAccessDbContext db)
        {
            this.connection = connection;
            Db = db;
            Store = new QualityAssuranceAccessStore(
                db,
                NullLogger<QualityAssuranceAccessStore>.Instance);
        }

        public QualityAssuranceAccessDbContext Db { get; }
        public QualityAssuranceAccessStore Store { get; }

        public void AddUser(
            string accountName,
            bool hasPermission,
            bool isActive)
        {
            var group = new QualityAssuranceAccessGroupRecord
            {
                Name = "Quality Test",
                Permissions = hasPermission
                    ? [new QualityAssuranceGroupPermissionRecord { PermissionKey = QualityAssurancePermissions.ModuleView }]
                    : []
            };
            Db.Users.Add(new QualityAssuranceUserRecord
            {
                AccountName = accountName,
                DisplayName = "Quality Administrator",
                PortalRole = ApplicationRoles.Viewer,
                IsActive = isActive,
                GroupMemberships =
                [
                    new QualityAssuranceUserGroupMembershipRecord { Group = group }
                ]
            });
        }

        public static async Task<QualityAccessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<QualityAssuranceAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new QualityAssuranceAccessDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new QualityAccessFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

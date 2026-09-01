using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityShipmentCommentServiceTests
{
    [Fact]
    public async Task Migration_backfills_the_previous_comments_field_into_the_thread()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>()
            .UseSqlite(connection)
            .Options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260813190849_AddQualityShippingLayoutPreferences");
        var legacyBody = new string('x', 2500);
        var createdAt = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO QualityShipments
                (Status, SalesOrderNumber, PartNumber, Customer, TaskType, Comments,
                 IsShipped, Version, CreatedAt, CreatedByAccountName, CreatedByDisplayName,
                 UpdatedAt, UpdatedByAccountName, UpdatedByDisplayName)
            VALUES
                ({"WIP"}, {"SO-LEGACY"}, {"PN-LEGACY"}, {"Customer"}, {"General"}, {legacyBody},
                 {false}, {0L}, {createdAt}, {"TEST\\legacy"}, {"Legacy User"},
                 {createdAt}, {"TEST\\legacy"}, {"Legacy User"})
            """);

        await migrator.MigrateAsync();

        var comment = await db.ShipmentComments.AsNoTracking().SingleAsync();
        Assert.Equal(legacyBody, comment.Body);
        Assert.Equal("Legacy User", comment.AuthorDisplayName);
        Assert.True(comment.IsLegacyImport);
    }

    [Fact]
    public async Task Posting_comment_preserves_legacy_note_and_updates_latest_preview()
    {
        await using var fixture = await CommentFixture.CreateAsync();
        var shipment = await fixture.AddShipmentAsync(comments: "Original spreadsheet note");

        var posted = await fixture.Service.PostAsync(
            shipment.Id,
            new QualityShipmentCommentCreateDto("Package is ready for @two to review."),
            fixture.Admin,
            default);
        var thread = await fixture.Service.ListAsync(shipment.Id, null, fixture.Admin, default);
        var refreshed = await fixture.Db.Shipments.SingleAsync(candidate => candidate.Id == shipment.Id);

        Assert.NotNull(posted);
        Assert.NotNull(thread);
        Assert.Equal(2, thread.Count);
        Assert.True(thread[0].IsLegacyImport);
        Assert.Equal("Original spreadsheet note", thread[0].Body);
        Assert.Equal("Package is ready for @two to review.", thread[1].Body);
        Assert.Equal(thread[1].Body, refreshed.Comments);
        Assert.Equal(2, refreshed.Version);
    }

    [Fact]
    public async Task Mentions_notify_each_permitted_recipient_once_and_can_be_marked_read()
    {
        await using var fixture = await CommentFixture.CreateAsync();
        var shipment = await fixture.AddShipmentAsync();

        var mentionable = await fixture.Service.MentionableUsersAsync(shipment.Id, fixture.Admin);
        Assert.NotNull(mentionable);
        Assert.Contains(mentionable, user => user.UserId == 2 && user.MentionHandle == "two");

        await fixture.Service.PostAsync(
            shipment.Id,
            new QualityShipmentCommentCreateDto("@two please review. Repeating @two should not duplicate it. @nobody is ignored."),
            fixture.Admin,
            default);

        var recipient = fixture.Access(2, "TEST\\two", "Person Two", canEdit: false, teamAccess: true);
        var notifications = await fixture.Service.NotificationsAsync(false, recipient);
        Assert.Single(notifications);
        Assert.Equal(shipment.Id, notifications[0].ShipmentId);
        Assert.Equal("Quality Admin", notifications[0].ActorDisplayName);
        Assert.Null(notifications[0].ReadAt);

        Assert.True(await fixture.Service.MarkNotificationReadAsync(notifications[0].Id, recipient));
        Assert.Empty(await fixture.Service.NotificationsAsync(true, recipient));

        shipment.AssignedGroupId = 20;
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(await fixture.Service.NotificationsAsync(false, recipient));
        Assert.Single(await fixture.Db.MentionNotifications.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Comment_thread_requires_field_permission_and_record_access()
    {
        await using var fixture = await CommentFixture.CreateAsync();
        var shipment = await fixture.AddShipmentAsync(assignedUserId: 2);
        var noCommentsPermission = new QualityAssuranceAccessProfile(
            2,
            "TEST\\two",
            "Person Two",
            ApplicationRoles.Viewer,
            [QualityAssurancePermissions.ModuleView, QualityAssurancePermissions.ShipmentsView],
            []);
        var otherQueue = fixture.Access(3, "TEST\\three", "Person Three", canEdit: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.ListAsync(shipment.Id, null, noCommentsPermission));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.ListAsync(shipment.Id, null, otherQueue));
    }

    private sealed class CommentFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly TestAccessStore directory;

        private CommentFixture(SqliteConnection connection, QualityAssuranceDbContext db)
        {
            this.connection = connection;
            Db = db;
            directory = new TestAccessStore();
            Service = new QualityShipmentCommentService(db, directory);
            Admin = Access(99, "TEST\\admin", "Quality Admin", canEdit: true, viewAll: true);
        }

        public QualityAssuranceDbContext Db { get; }
        public QualityShipmentCommentService Service { get; }
        public QualityAssuranceAccessProfile Admin { get; }

        public static async Task<CommentFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new CommentFixture(connection, db);
        }

        public async Task<QualityShipment> AddShipmentAsync(string? comments = null, int? assignedUserId = 99)
        {
            var shipment = new QualityShipment
            {
                SalesOrderNumber = "SO-COMMENT",
                PartNumber = "PN-COMMENT",
                Customer = "Customer",
                TaskType = "General",
                Comments = comments,
                AssignedGroupId = 10,
                AssignedUserId = assignedUserId,
                Version = 1,
                CreatedByAccountName = Admin.AccountName,
                CreatedByDisplayName = Admin.DisplayName,
                UpdatedByAccountName = Admin.AccountName,
                UpdatedByDisplayName = Admin.DisplayName
            };
            if (!string.IsNullOrWhiteSpace(comments))
            {
                shipment.CommentThread.Add(new QualityShipmentComment
                {
                    Body = comments,
                    AuthorUserId = Admin.UserId,
                    AuthorAccountName = Admin.AccountName,
                    AuthorDisplayName = Admin.DisplayName,
                    CreatedAt = shipment.UpdatedAt,
                    IsLegacyImport = true
                });
            }
            Db.Shipments.Add(shipment);
            await Db.SaveChangesAsync();
            return shipment;
        }

        public QualityAssuranceAccessProfile Access(
            int userId,
            string accountName,
            string displayName,
            bool canEdit,
            bool viewAll = false,
            bool teamAccess = false)
        {
            var permissions = new List<string>
            {
                QualityAssurancePermissions.ModuleView,
                QualityAssurancePermissions.ShipmentsView,
                QualityAssurancePermissions.CommentsView
            };
            if (canEdit) permissions.Add(QualityAssurancePermissions.CommentsEdit);
            if (viewAll) permissions.Add(QualityAssurancePermissions.ShipmentsViewAll);
            if (teamAccess) permissions.Add(QualityAssurancePermissions.TeamDashboardView);
            return new QualityAssuranceAccessProfile(
                userId,
                accountName,
                displayName,
                canEdit ? ApplicationRoles.Editor : ApplicationRoles.Viewer,
                permissions,
                teamAccess ? [new QualityAssuranceAccessGroup(10, "Quality")] : []);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestAccessStore : IQualityAssuranceAccessStore
    {
        private readonly IReadOnlyList<QualityDirectoryUser> users =
        [
            new QualityDirectoryUser(2, "TEST\\two", "Person Two", []),
            new QualityDirectoryUser(3, "TEST\\three", "Person Three", []),
            new QualityDirectoryUser(99, "TEST\\admin", "Quality Admin", [])
        ];

        public Task<QualityAssuranceAccessProfile?> FindAccessAsync(string accountName, CancellationToken cancellationToken = default)
        {
            var normalized = WindowsAccountNames.Normalize(accountName);
            QualityAssuranceAccessProfile? access = normalized?.ToUpperInvariant() switch
            {
                "TEST\\TWO" => Profile(2, "TEST\\two", "Person Two", teamAccess: true),
                "TEST\\THREE" => Profile(3, "TEST\\three", "Person Three"),
                "TEST\\ADMIN" => Profile(99, "TEST\\admin", "Quality Admin", canEdit: true, viewAll: true),
                _ => null
            };
            return Task.FromResult(access);
        }

        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QualityDirectoryGroup>>([]);

        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsWithPermissionAsync(
            string permissionKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QualityDirectoryGroup>>([]);

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(int? groupId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(users);

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersWithPermissionAsync(
            string permissionKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(users);

        private static QualityAssuranceAccessProfile Profile(
            int id,
            string accountName,
            string displayName,
            bool canEdit = false,
            bool viewAll = false,
            bool teamAccess = false)
        {
            var permissions = new List<string>
            {
                QualityAssurancePermissions.ModuleView,
                QualityAssurancePermissions.ShipmentsView,
                QualityAssurancePermissions.CommentsView
            };
            if (canEdit) permissions.Add(QualityAssurancePermissions.CommentsEdit);
            if (viewAll) permissions.Add(QualityAssurancePermissions.ShipmentsViewAll);
            if (teamAccess) permissions.Add(QualityAssurancePermissions.TeamDashboardView);
            return new QualityAssuranceAccessProfile(
                id,
                accountName,
                displayName,
                canEdit ? ApplicationRoles.Editor : ApplicationRoles.Viewer,
                permissions,
                teamAccess ? [new QualityAssuranceAccessGroup(10, "Quality")] : []);
        }
    }
}

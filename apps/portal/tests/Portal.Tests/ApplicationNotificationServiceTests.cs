using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Api.Data;
using Portal.Api.Services;

namespace Portal.Tests;

public sealed class ApplicationNotificationServiceTests
{
    [Fact]
    public async Task GetUnreadCountsAsync_ReturnsOnlyUnreadNotificationsForCurrentActiveUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var currentUser = new PortalRoleRecord
        {
            AccountName = "SONAERO\\Planner.One",
            DisplayName = "Planner One",
            Role = "Editor",
            IsActive = true
        };
        var otherUser = new PortalRoleRecord
        {
            AccountName = "SONAERO\\Planner.Two",
            DisplayName = "Planner Two",
            Role = "Editor",
            IsActive = true
        };
        db.Users.AddRange(currentUser, otherUser);
        await db.SaveChangesAsync();

        db.UserNotifications.AddRange(
            new PortalNotificationRecord
            {
                RecipientUserId = currentUser.Id,
                ProjectMessageId = 10,
                Kind = "ProjectChatMention",
                ActorAccountName = otherUser.AccountName,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PortalNotificationRecord
            {
                RecipientUserId = currentUser.Id,
                ProjectMessageId = 11,
                Kind = "ProjectChatMention",
                ActorAccountName = otherUser.AccountName,
                CreatedAt = DateTimeOffset.UtcNow,
                ReadAt = DateTimeOffset.UtcNow
            },
            new PortalNotificationRecord
            {
                RecipientUserId = otherUser.Id,
                ProjectMessageId = 12,
                Kind = "ProjectChatMention",
                ActorAccountName = currentUser.AccountName,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PortalNotificationRecord
            {
                RecipientUserId = currentUser.Id,
                ProjectMessageId = 13,
                Kind = "ProjectChatMention",
                ActorAccountName = currentUser.AccountName,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PortalNotificationRecord
            {
                RecipientUserId = currentUser.Id,
                Kind = "ProjectChatMention",
                ActorAccountName = otherUser.AccountName,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PortalNotificationRecord
            {
                RecipientUserId = currentUser.Id,
                ProjectMessageId = 14,
                Kind = "UnsupportedLegacyKind",
                ActorAccountName = otherUser.AccountName,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var service = new ApplicationNotificationService(
            db,
            NullLogger<ApplicationNotificationService>.Instance);

        var result = await service.GetUnreadCountsAsync("sonaero\\planner.one");

        var notification = Assert.Single(result);
        Assert.Equal("project-tracker", notification.ApplicationId);
        Assert.Equal(1, notification.UnreadCount);
    }

    [Fact]
    public async Task GetUnreadCountsAsync_DoesNotReturnNotificationsForInactiveUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new PortalRoleRecord
        {
            AccountName = "SONAERO\\Former.User",
            DisplayName = "Former User",
            Role = "Viewer",
            IsActive = false
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.UserNotifications.Add(new PortalNotificationRecord
        {
            RecipientUserId = user.Id,
            ProjectMessageId = 10,
            Kind = "ProjectChatMention",
            ActorAccountName = @"SONAERO\Active.User",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new ApplicationNotificationService(
            db,
            NullLogger<ApplicationNotificationService>.Instance);

        Assert.Empty(await service.GetUnreadCountsAsync(user.AccountName));
    }
}

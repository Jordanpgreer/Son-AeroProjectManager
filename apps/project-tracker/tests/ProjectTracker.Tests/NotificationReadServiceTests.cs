using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class NotificationReadServiceTests
{
    [Fact]
    public async Task GetAsync_orders_date_time_offsets_newest_first_on_sqlite()
    {
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();

        var user = new AppUser
        {
            AccountName = @"TEST\Reader",
            DisplayName = "Reader",
            IsActive = true
        };
        var project = new Project
        {
            ProgramName = "ORDER-TEST",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var message = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = @"TEST\Author",
            AuthorDisplayName = "Author",
            Body = "Ordering notifications"
        };
        db.AddRange(user, project, message);
        await db.SaveChangesAsync();

        db.UserNotifications.AddRange(
            CreateNotification(user.Id, project.Id, message.Id, "Older", new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)),
            CreateNotification(user.Id, project.Id, message.Id, "Newest", new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)),
            CreateNotification(user.Id, project.Id, message.Id, "Middle", new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var result = await new NotificationReadService(db).GetAsync(user.Id, user.AccountName, false, 2);

        Assert.Equal(["Newest", "Middle"], result.Select(notification => notification.Title));
    }

    [Fact]
    public async Task GetAsync_and_unread_count_ignore_self_authored_or_source_less_or_unsupported_rows()
    {
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();

        var user = new AppUser { AccountName = @"TEST\Reader", DisplayName = "Reader" };
        var project = new Project { ProgramName = "FILTER-TEST" };
        var message = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = @"TEST\Author",
            AuthorDisplayName = "Author",
            Body = "Supported notification"
        };
        db.AddRange(user, project, message);
        await db.SaveChangesAsync();

        db.UserNotifications.AddRange(
            CreateNotification(user.Id, project.Id, message.Id, "Real", DateTimeOffset.UtcNow),
            new UserNotification
            {
                RecipientUserId = user.Id,
                ProjectId = project.Id,
                ProjectMessageId = message.Id,
                Kind = NotificationKind.ProjectChatMention,
                ActorAccountName = user.AccountName,
                ActorDisplayName = user.DisplayName,
                Title = "Self",
                BodyPreview = "Self"
            },
            new UserNotification
            {
                RecipientUserId = user.Id,
                ProjectId = project.Id,
                Kind = NotificationKind.ProjectChatMention,
                ActorAccountName = @"TEST\Author",
                ActorDisplayName = "Author",
                Title = "Missing source",
                BodyPreview = "Missing source"
            });
        await db.SaveChangesAsync();

        var unsupportedKind = "UnsupportedLegacyKind";
        var actorAccountName = @"TEST\Author";
        var actorDisplayName = "Author";
        var unsupportedTitle = "Unsupported";
        var unsupportedPreview = "Unsupported";
        var unsupportedCreatedAt = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserNotifications"
                ("RecipientUserId", "ProjectId", "ProjectMessageId", "Kind", "ActorAccountName",
                 "ActorDisplayName", "Title", "BodyPreview", "CreatedAt")
            VALUES
                ({user.Id}, {project.Id}, {message.Id}, {unsupportedKind}, {actorAccountName},
                 {actorDisplayName}, {unsupportedTitle}, {unsupportedPreview}, {unsupportedCreatedAt});
            """);
        db.ChangeTracker.Clear();

        var service = new NotificationReadService(db);
        var result = await service.GetAsync(user.Id, user.AccountName, false, 20);
        var unreadCount = await service.GetUnreadCountAsync(user.Id, user.AccountName);

        Assert.Equal("Real", Assert.Single(result).Title);
        Assert.Equal(1, unreadCount);
    }

    private static ProjectTrackerDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new ProjectTrackerDbContext(options);
        db.Database.OpenConnection();
        return db;
    }

    private static UserNotification CreateNotification(
        int userId,
        int projectId,
        int messageId,
        string title,
        DateTimeOffset createdAt) =>
        new()
        {
            RecipientUserId = userId,
            ProjectId = projectId,
            ProjectMessageId = messageId,
            Kind = NotificationKind.ProjectChatMention,
            ActorAccountName = @"TEST\Author",
            ActorDisplayName = "Author",
            Title = title,
            BodyPreview = title,
            CreatedAt = createdAt
        };
}

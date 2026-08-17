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

    [Fact]
    public async Task MarkReadAsync_and_MarkAllReadAsync_update_only_valid_non_self_notifications()
    {
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();

        var user = new AppUser { AccountName = @"TEST\Reader", DisplayName = "Reader" };
        var project = new Project { ProgramName = "READ-TEST" };
        var firstMessage = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = @"TEST\Author",
            AuthorDisplayName = "Author",
            Body = "First"
        };
        var secondMessage = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = @"TEST\Author",
            AuthorDisplayName = "Author",
            Body = "Second"
        };
        var selfMessage = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = user.AccountName,
            AuthorDisplayName = user.DisplayName,
            Body = "Self"
        };
        db.AddRange(user, project, firstMessage, secondMessage, selfMessage);
        await db.SaveChangesAsync();

        var first = CreateNotification(user.Id, project.Id, firstMessage.Id, "First", DateTimeOffset.UtcNow);
        var second = CreateNotification(user.Id, project.Id, secondMessage.Id, "Second", DateTimeOffset.UtcNow);
        var self = new UserNotification
        {
            RecipientUserId = user.Id,
            ProjectId = project.Id,
            ProjectMessageId = selfMessage.Id,
            Kind = NotificationKind.ProjectChatMention,
            ActorAccountName = user.AccountName,
            ActorDisplayName = user.DisplayName,
            Title = "Self",
            BodyPreview = "Self"
        };
        db.UserNotifications.AddRange(first, second, self);
        await db.SaveChangesAsync();

        var service = new NotificationReadService(db);
        Assert.True(await service.MarkReadAsync(first.Id, user.Id, user.AccountName));
        Assert.False(await service.MarkReadAsync(999999, user.Id, user.AccountName));
        await service.MarkAllReadAsync(user.Id, user.AccountName);
        db.ChangeTracker.Clear();

        var readStates = await db.UserNotifications
            .IgnoreQueryFilters()
            .OrderBy(notification => notification.Id)
            .Select(notification => notification.ReadAt)
            .ToListAsync();
        Assert.NotNull(readStates[0]);
        Assert.NotNull(readStates[1]);
        Assert.Null(readStates[2]);
    }

    [Fact]
    public async Task DeleteAsync_deletes_only_the_matching_recipients_notification()
    {
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();

        var firstUser = new AppUser { AccountName = @"TEST\First", DisplayName = "First" };
        var secondUser = new AppUser { AccountName = @"TEST\Second", DisplayName = "Second" };
        var project = new Project { ProgramName = "DELETE-ONE-TEST" };
        var message = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = @"TEST\Author",
            AuthorDisplayName = "Author",
            Body = "Delete one"
        };
        db.AddRange(firstUser, secondUser, project, message);
        await db.SaveChangesAsync();

        var firstNotification = CreateNotification(firstUser.Id, project.Id, message.Id, "First", DateTimeOffset.UtcNow);
        var secondNotification = CreateNotification(secondUser.Id, project.Id, message.Id, "Second", DateTimeOffset.UtcNow);
        db.UserNotifications.AddRange(firstNotification, secondNotification);
        await db.SaveChangesAsync();

        var service = new NotificationReadService(db);
        Assert.False(await service.DeleteAsync(secondNotification.Id, firstUser.Id));
        Assert.True(await service.DeleteAsync(firstNotification.Id, firstUser.Id));
        Assert.False(await service.DeleteAsync(firstNotification.Id, firstUser.Id));

        var remaining = await db.UserNotifications
            .IgnoreQueryFilters()
            .Select(notification => notification.Id)
            .ToListAsync();
        Assert.Equal([secondNotification.Id], remaining);
    }

    [Fact]
    public async Task DeleteAllAsync_deletes_all_rows_for_the_recipient_only()
    {
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();

        var firstUser = new AppUser { AccountName = @"TEST\First", DisplayName = "First" };
        var secondUser = new AppUser { AccountName = @"TEST\Second", DisplayName = "Second" };
        var project = new Project { ProgramName = "DELETE-ALL-TEST" };
        var message = new ProjectMessage
        {
            Project = project,
            AuthorAccountName = @"TEST\Author",
            AuthorDisplayName = "Author",
            Body = "Delete all"
        };
        db.AddRange(firstUser, secondUser, project, message);
        await db.SaveChangesAsync();

        db.UserNotifications.AddRange(Enumerable.Range(1, 60).Select(index =>
            CreateNotification(firstUser.Id, project.Id, message.Id, $"First {index}", DateTimeOffset.UtcNow)));
        db.UserNotifications.Add(
            CreateNotification(secondUser.Id, project.Id, message.Id, "Second", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var service = new NotificationReadService(db);
        Assert.Equal(60, await service.DeleteAllAsync(firstUser.Id));
        Assert.Equal(0, await service.DeleteAllAsync(firstUser.Id));

        var remainingRecipients = await db.UserNotifications
            .IgnoreQueryFilters()
            .Select(notification => notification.RecipientUserId)
            .ToListAsync();
        Assert.Equal([secondUser.Id], remainingRecipients);
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

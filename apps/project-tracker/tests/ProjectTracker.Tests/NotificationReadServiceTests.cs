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
        db.AddRange(user, project);
        await db.SaveChangesAsync();

        db.UserNotifications.AddRange(
            CreateNotification(user.Id, project.Id, "Older", new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)),
            CreateNotification(user.Id, project.Id, "Newest", new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)),
            CreateNotification(user.Id, project.Id, "Middle", new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var result = await new NotificationReadService(db).GetAsync(user.Id, false, 2);

        Assert.Equal(["Newest", "Middle"], result.Select(notification => notification.Title));
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
        string title,
        DateTimeOffset createdAt) =>
        new()
        {
            RecipientUserId = userId,
            ProjectId = projectId,
            Kind = NotificationKind.ProjectChatMention,
            ActorAccountName = @"TEST\Author",
            ActorDisplayName = "Author",
            Title = title,
            BodyPreview = title,
            CreatedAt = createdAt
        };
}

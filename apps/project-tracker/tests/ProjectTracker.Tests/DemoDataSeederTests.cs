using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class DemoDataSeederTests
{
    [Fact]
    public async Task Seed_IsIdempotentAndCreatesOctoberConflictAndCompletedHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Groups.AddRange(
            new AppGroup { Name = "Managers" },
            new AppGroup { Name = "Engineering" });
        db.Users.Add(new AppUser
        {
            AccountName = @"DEV\ProjectTrackerAdmin",
            DisplayName = "Project Tracker Admin"
        });
        await db.SaveChangesAsync();

        var notifications = new MentionNotificationService();
        await DemoDataSeeder.SeedAsync(db, notifications);
        var firstCounts = new
        {
            Projects = await db.Projects.CountAsync(),
            Tasks = await db.Tasks.CountAsync(),
            Messages = await db.ProjectMessages.CountAsync(),
            Notifications = await db.UserNotifications.CountAsync()
        };

        await DemoDataSeeder.SeedAsync(db, notifications);
        var secondCounts = new
        {
            Projects = await db.Projects.CountAsync(),
            Tasks = await db.Tasks.CountAsync(),
            Messages = await db.ProjectMessages.CountAsync(),
            Notifications = await db.UserNotifications.CountAsync()
        };

        Assert.Equal(firstCounts, secondCounts);
        var active = await db.Projects
            .Include(project => project.Tasks)
            .Where(project => project.ProgramName == "Test 5" || project.ProgramName == "Test 6")
            .OrderBy(project => project.ProgramName)
            .ToListAsync();
        Assert.Equal(2, active.Count);
        Assert.All(active, project => Assert.Equal(10, project.TargetDelivery!.Value.Month));

        var sharedMillTasks = active
            .SelectMany(project => project.Tasks)
            .Where(task => task.WorkStation == "CNC Mill")
            .ToList();
        Assert.Equal(2, sharedMillTasks.Count);
        Assert.True(sharedMillTasks[0].StartDate <= sharedMillTasks[1].EndDate);
        Assert.True(sharedMillTasks[1].StartDate <= sharedMillTasks[0].EndDate);

        var completed = await db.Projects
            .Include(project => project.Messages)
            .Where(project => project.ProgramName == "Test 3" || project.ProgramName == "Test 4")
            .ToListAsync();
        Assert.Equal(2, completed.Count);
        Assert.All(completed, project =>
        {
            Assert.Equal(ProjectStatus.Complete, project.Status);
            Assert.NotNull(project.CompletedOn);
            Assert.True(project.Messages.Count >= 3);
        });
        var earlyProject = completed.Single(project => project.ProgramName == "Test 3");
        var lateProject = completed.Single(project => project.ProgramName == "Test 4");
        Assert.Equal(-3, earlyProject.CompletedOn!.Value.DayNumber - earlyProject.TargetDelivery!.Value.DayNumber);
        Assert.Equal(5, lateProject.CompletedOn!.Value.DayNumber - lateProject.TargetDelivery!.Value.DayNumber);
    }
}

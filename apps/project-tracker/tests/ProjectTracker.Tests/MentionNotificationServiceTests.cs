using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class MentionNotificationServiceTests
{
    [Fact]
    public async Task ChatMention_CreatesOnlyRecipientNotification()
    {
        await using var fixture = await NotificationFixture.CreateAsync();
        var message = new ProjectMessage
        {
            ProjectId = fixture.Project.Id,
            AuthorAccountName = fixture.Actor.AccountName,
            AuthorDisplayName = fixture.Actor.DisplayName,
            Body = "@author @alex.morgan @inactive.user @outside.user please review this schedule."
        };
        fixture.Db.ProjectMessages.Add(message);

        await fixture.Service.AddForProjectMessageAsync(
            fixture.Db,
            message,
            fixture.Project.ProgramName,
            fixture.Actor.AccountName,
            fixture.Actor.DisplayName);
        await fixture.Db.SaveChangesAsync();

        var notification = await fixture.Db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.Alex.Id, notification.RecipientUserId);
        Assert.Equal(NotificationKind.ProjectChatMention, notification.Kind);
        Assert.Equal(message.Id, notification.ProjectMessageId);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public async Task NoteEdit_NotifiesOnlyMentionsIntroducedByTheEdit()
    {
        await using var fixture = await NotificationFixture.CreateAsync();
        var previous = "@alex.morgan please review the setup.";
        var updated = "@alex.morgan please review the setup. @casey.lee please verify capacity.";

        await fixture.Service.AddForOperationNoteAsync(
            fixture.Db,
            fixture.Task,
            fixture.Project.ProgramName,
            updated,
            previous,
            fixture.Actor.AccountName,
            fixture.Actor.DisplayName);
        await fixture.Db.SaveChangesAsync();

        var notification = await fixture.Db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.Casey.Id, notification.RecipientUserId);
        Assert.Equal(NotificationKind.OperationNoteMention, notification.Kind);
        Assert.Equal(fixture.Task.Id, notification.ProjectTaskId);
    }

    [Fact]
    public async Task DeletingMentionSource_SetsNotificationForeignKeyToNull()
    {
        await using var fixture = await NotificationFixture.CreateAsync();
        var notification = new UserNotification
        {
            RecipientUserId = fixture.Alex.Id,
            ProjectId = fixture.Project.Id,
            ProjectTaskId = fixture.Task.Id,
            Kind = NotificationKind.OperationNoteMention,
            ActorAccountName = fixture.Actor.AccountName,
            ActorDisplayName = fixture.Actor.DisplayName,
            Title = "Mention",
            BodyPreview = "Preview"
        };
        fixture.Db.UserNotifications.Add(notification);
        await fixture.Db.SaveChangesAsync();

        fixture.Db.Tasks.Remove(fixture.Task);
        await fixture.Db.SaveChangesAsync();

        var saved = await fixture.Db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Null(saved.ProjectTaskId);
    }

    private sealed class NotificationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private NotificationFixture(
            SqliteConnection connection,
            ProjectTrackerDbContext db,
            Project project,
            ProjectTask task,
            AppUser actor,
            AppUser alex,
            AppUser casey)
        {
            this.connection = connection;
            Db = db;
            Project = project;
            Task = task;
            Actor = actor;
            Alex = alex;
            Casey = casey;
        }

        public ProjectTrackerDbContext Db { get; }
        public Project Project { get; }
        public ProjectTask Task { get; }
        public AppUser Actor { get; }
        public AppUser Alex { get; }
        public AppUser Casey { get; }
        public MentionNotificationService Service { get; } = new();

        public static async Task<NotificationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
            var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var actor = new AppUser { AccountName = @"SON-AERO\author", DisplayName = "Author" };
            var alex = new AppUser { AccountName = @"SON-AERO\alex.morgan", DisplayName = "Alex Morgan" };
            var casey = new AppUser { AccountName = @"SON-AERO\casey.lee", DisplayName = "Casey Lee" };
            var inactive = new AppUser
            {
                AccountName = @"SON-AERO\inactive.user",
                DisplayName = "Inactive User",
                IsActive = false
            };
            var outside = new AppUser { AccountName = @"SON-AERO\outside.user", DisplayName = "Outside User" };
            var projectTrackerUsers = new AppGroup
            {
                Name = "Project Tracker Users",
                Permissions =
                [
                    new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView }
                ]
            };
            foreach (var user in new[] { actor, alex, casey, inactive })
            {
                user.GroupMemberships.Add(new AppUserGroupMembership { Group = projectTrackerUsers });
            }
            var project = new Project { ProgramName = "Notification Test" };
            var task = new ProjectTask { Project = project, Sequence = 1, Title = "CNC Machining" };
            project.Tasks.Add(task);
            db.AddRange(actor, alex, casey, inactive, outside, projectTrackerUsers, project);
            await db.SaveChangesAsync();

            return new NotificationFixture(connection, db, project, task, actor, alex, casey);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

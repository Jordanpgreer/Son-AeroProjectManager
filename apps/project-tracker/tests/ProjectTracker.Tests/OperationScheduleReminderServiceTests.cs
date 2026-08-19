using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class OperationScheduleReminderServiceTests
{
    [Fact]
    public async Task EnsureRemindersAsync_UsesPriorWorkingDayPermissionAndIsIdempotent()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var eligible = fixture.AddUser("Eligible", includeReminderPermission: true);
        fixture.AddUser("No reminders", includeReminderPermission: false);
        var project = fixture.AddProject(
            new ProjectTask
            {
                Sequence = 1,
                Title = "Tooling",
                StartDate = new DateOnly(2026, 8, 13),
                EndDate = new DateOnly(2026, 8, 19),
                EstimatedDuration = 4,
                PercentCompleteManual = true
            },
            new ProjectTask
            {
                Sequence = 2,
                Title = "Inspection",
                StartDate = new DateOnly(2026, 8, 10),
                EndDate = new DateOnly(2026, 8, 13),
                EstimatedDuration = 4,
                PercentComplete = 0.75m,
                PercentCompleteManual = true
            });
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var second = await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        var notifications = await fixture.Db.UserNotifications.AsNoTracking().OrderBy(row => row.Kind).ToListAsync();
        Assert.All(notifications, notification => Assert.Equal(eligible.Id, notification.RecipientUserId));
        Assert.All(notifications, notification => Assert.Equal(new DateOnly(2026, 8, 13), notification.ScheduledDate));
        Assert.Contains(notifications, notification =>
            notification.Kind == NotificationKind.OperationStartConfirmation
            && notification.ProjectId == project.Id
            && notification.Title == "Did Tooling start Thursday?");
        Assert.Contains(notifications, notification =>
            notification.Kind == NotificationKind.OperationFinishConfirmation
            && notification.Title == "Did Inspection finish Thursday?");
        Assert.Equal(2, fixture.Queue.Ids.Count);
    }

    [Fact]
    public async Task ConfirmAsync_StartEnablesAutomaticProgressAndResolvesEveryRecipientsPrompt()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var firstUser = fixture.AddUser("First", includeReminderPermission: true);
        fixture.AddUser("Second", includeReminderPermission: true);
        var project = fixture.AddProject(new ProjectTask
        {
            Sequence = 1,
            Title = "Build",
            StartDate = new DateOnly(2026, 8, 13),
            EndDate = new DateOnly(2026, 8, 19),
            EstimatedDuration = 4,
            PercentCompleteManual = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var reminder = await fixture.Db.UserNotifications.SingleAsync(notification =>
            notification.RecipientUserId == firstUser.Id
            && notification.Kind == NotificationKind.OperationStartConfirmation);

        var result = await fixture.Service.ConfirmAsync(
            reminder.Id,
            firstUser.Id,
            hasPermission: true,
            @"TEST\First",
            "First",
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Confirmed, result.Status);
        fixture.Db.ChangeTracker.Clear();
        var task = await fixture.Db.Tasks.SingleAsync(candidate => candidate.ProjectId == project.Id);
        Assert.False(task.PercentCompleteManual);
        Assert.True(task.StartDateLocked);
        Assert.Equal(0.5m, task.PercentComplete);
        Assert.Equal(2, await fixture.Db.UserNotifications.CountAsync(notification => notification.RespondedAt != null));
        Assert.Contains(await fixture.Db.ProjectAuditEntries.ToListAsync(), entry => entry.Action == "OperationStartConfirmed");
    }

    [Fact]
    public async Task ConfirmAsync_SecondRecipientCannotConfirmSameOperationTwice()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var firstUser = fixture.AddUser("First confirmer", includeReminderPermission: true);
        var secondUser = fixture.AddUser("Second confirmer", includeReminderPermission: true);
        fixture.AddProject(new ProjectTask
        {
            Sequence = 1,
            Title = "Build",
            StartDate = new DateOnly(2026, 8, 13),
            EndDate = new DateOnly(2026, 8, 19),
            EstimatedDuration = 4,
            PercentCompleteManual = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var reminders = await fixture.Db.UserNotifications.OrderBy(candidate => candidate.RecipientUserId).ToListAsync();
        var firstReminder = reminders.Single(candidate => candidate.RecipientUserId == firstUser.Id);
        var secondReminder = reminders.Single(candidate => candidate.RecipientUserId == secondUser.Id);

        var first = await fixture.Service.ConfirmAsync(
            firstReminder.Id,
            firstUser.Id,
            hasPermission: true,
            @"TEST\First",
            "First",
            new DateOnly(2026, 8, 17));
        fixture.Db.ChangeTracker.Clear();
        var second = await fixture.Service.ConfirmAsync(
            secondReminder.Id,
            secondUser.Id,
            hasPermission: true,
            @"TEST\Second",
            "Second",
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Confirmed, first.Status);
        Assert.Equal(OperationScheduleConfirmationStatus.AlreadyConfirmed, second.Status);
        Assert.Equal(1, await fixture.Db.ProjectAuditEntries.CountAsync(entry => entry.Action == "OperationStartConfirmed"));
    }

    [Fact]
    public async Task ConfirmAsync_FinishCompletesOperation()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var user = fixture.AddUser("Finisher", includeReminderPermission: true);
        var project = fixture.AddProject(new ProjectTask
        {
            Sequence = 1,
            Title = "Inspection",
            StartDate = new DateOnly(2026, 8, 10),
            EndDate = new DateOnly(2026, 8, 13),
            EstimatedDuration = 4,
            PercentComplete = 0.75m,
            PercentCompleteManual = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var reminder = await fixture.Db.UserNotifications.SingleAsync(notification =>
            notification.Kind == NotificationKind.OperationFinishConfirmation);

        var result = await fixture.Service.ConfirmAsync(
            reminder.Id,
            user.Id,
            hasPermission: true,
            @"TEST\Finisher",
            "Finisher",
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Confirmed, result.Status);
        fixture.Db.ChangeTracker.Clear();
        var task = await fixture.Db.Tasks.SingleAsync(candidate => candidate.ProjectId == project.Id);
        Assert.Equal(1m, task.PercentComplete);
        Assert.True(task.PercentCompleteManual);
        Assert.Equal(TaskScheduleStatus.Complete, task.Status);
        Assert.NotNull(await fixture.Db.UserNotifications.Select(notification => notification.RespondedAt).SingleAsync());
    }

    [Fact]
    public async Task ConfirmAsync_RejectsMissingPermissionWithoutChangingTask()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var user = fixture.AddUser("Restricted", includeReminderPermission: true);
        fixture.AddProject(new ProjectTask
        {
            Sequence = 1,
            Title = "Build",
            StartDate = new DateOnly(2026, 8, 13),
            EndDate = new DateOnly(2026, 8, 19),
            EstimatedDuration = 4,
            PercentCompleteManual = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var reminder = await fixture.Db.UserNotifications.SingleAsync();

        var result = await fixture.Service.ConfirmAsync(
            reminder.Id,
            user.Id,
            hasPermission: false,
            @"TEST\Restricted",
            "Restricted",
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Forbidden, result.Status);
        Assert.True((await fixture.Db.Tasks.SingleAsync()).PercentCompleteManual);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfirmAsync_RejectsArchivedOrCompletedProject(bool deleted, bool completed)
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var user = fixture.AddUser("Restricted project", includeReminderPermission: true);
        var project = fixture.AddProject(new ProjectTask
        {
            Sequence = 1,
            Title = "Build",
            StartDate = new DateOnly(2026, 8, 13),
            EndDate = new DateOnly(2026, 8, 19),
            EstimatedDuration = 4,
            PercentCompleteManual = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var reminder = await fixture.Db.UserNotifications.SingleAsync();
        project.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
        project.CompletedOn = completed ? new DateOnly(2026, 8, 17) : null;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await fixture.Service.ConfirmAsync(
            reminder.Id,
            user.Id,
            hasPermission: true,
            @"TEST\Restricted",
            "Restricted",
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Stale, result.Status);
        var savedTask = await fixture.Db.Tasks.IgnoreQueryFilters().SingleAsync();
        Assert.True(savedTask.PercentCompleteManual);
        Assert.Null((await fixture.Db.UserNotifications.IgnoreQueryFilters().SingleAsync()).RespondedAt);
    }

    [Fact]
    public async Task ConfirmAsync_WrongRecipientDoesNotRevealOrChangeReminder()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        fixture.AddUser("Recipient", includeReminderPermission: true);
        var other = fixture.AddUser("Other", includeReminderPermission: true);
        fixture.AddProject(new ProjectTask
        {
            Sequence = 1,
            Title = "Build",
            StartDate = new DateOnly(2026, 8, 13),
            EndDate = new DateOnly(2026, 8, 19),
            EstimatedDuration = 4,
            PercentCompleteManual = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));
        var reminder = await fixture.Db.UserNotifications.OrderBy(candidate => candidate.Id).FirstAsync();

        var result = await fixture.Service.ConfirmAsync(
            reminder.Id,
            other.Id + 1000,
            hasPermission: true,
            @"TEST\Other",
            "Other",
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.NotFound, result.Status);
        Assert.All(await fixture.Db.UserNotifications.ToListAsync(), candidate => Assert.Null(candidate.RespondedAt));
    }

    private sealed class ReminderFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private int userNumber;

        private ReminderFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
            Queue = new RecordingQueue();
            Service = new OperationScheduleReminderService(
                db,
                new ScheduleCalculator(),
                new ProjectMetricsService(new ScheduleCalculator()),
                Queue);
        }

        public ProjectTrackerDbContext Db { get; }
        public RecordingQueue Queue { get; }
        public OperationScheduleReminderService Service { get; }

        public static async Task<ReminderFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.ScheduleSettings.Add(new ScheduleSettings());
            await db.SaveChangesAsync();
            return new ReminderFixture(connection, db);
        }

        public AppUser AddUser(string displayName, bool includeReminderPermission)
        {
            userNumber++;
            var permissions = new List<AppGroupPermission>
            {
                new() { PermissionKey = ApplicationPermissions.ModuleView }
            };
            if (includeReminderPermission)
            {
                permissions.Add(new AppGroupPermission
                {
                    PermissionKey = ProjectTrackerPermissions.OperationScheduleConfirm
                });
            }

            var group = new AppGroup
            {
                Name = $"{displayName} group {userNumber}",
                Permissions = permissions
            };
            var user = new AppUser
            {
                AccountName = $@"TEST\{displayName}",
                DisplayName = displayName,
                IsActive = true,
                GroupMemberships = [new AppUserGroupMembership { Group = group }]
            };
            Db.Users.Add(user);
            return user;
        }

        public Project AddProject(params ProjectTask[] tasks)
        {
            var project = new Project
            {
                ProgramName = $"Reminder project {Guid.NewGuid():N}",
                ProgramStart = tasks.Min(task => task.StartDate),
                Tasks = tasks.ToList()
            };
            Db.Projects.Add(project);
            return project;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingQueue : IPushNotificationQueue
    {
        public List<int> Ids { get; } = [];
        public bool TryEnqueue(int notificationId)
        {
            Ids.Add(notificationId);
            return true;
        }

        public async IAsyncEnumerable<int> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

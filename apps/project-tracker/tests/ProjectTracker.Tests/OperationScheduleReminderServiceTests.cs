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
        var secondUser = fixture.AddUser("Second", includeReminderPermission: true);
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
        var response = await fixture.Db.UserNotifications.SingleAsync(notification =>
            notification.Kind == NotificationKind.OperationStartResponse);
        Assert.Equal(secondUser.Id, response.RecipientUserId);
        Assert.Equal("First reported Build started", response.Title);
        Assert.Equal(3, fixture.Queue.Ids.Count);
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
    public async Task SnoozeAsync_HidesOnlyTheRecipientsPromptAndPushesItAgainTheNextDay()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var firstUser = fixture.AddUser("Snoozer", includeReminderPermission: true);
        var secondUser = fixture.AddUser("Still active", includeReminderPermission: true);
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
        var reminder = await fixture.Db.UserNotifications.SingleAsync(notification =>
            notification.RecipientUserId == firstUser.Id);

        var result = await fixture.Service.SnoozeAsync(
            reminder.Id,
            firstUser.Id,
            hasPermission: true,
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Snoozed, result.Status);
        fixture.Db.ChangeTracker.Clear();
        var snoozed = await fixture.Db.UserNotifications.SingleAsync(notification => notification.Id == reminder.Id);
        Assert.Equal(new DateOnly(2026, 8, 18), snoozed.SnoozedUntil);
        Assert.NotNull(snoozed.ReadAt);
        var readService = new NotificationReadService(fixture.Db);
        Assert.Empty(await readService.GetAsync(firstUser.Id, firstUser.AccountName, false, 20));
        Assert.Single(await readService.GetAsync(secondUser.Id, secondUser.AccountName, false, 20));

        Assert.Equal(0, await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17)));
        Assert.Equal(1, await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 18)));
        Assert.Equal(0, await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 18)));
        fixture.Db.ChangeTracker.Clear();
        var awakened = await fixture.Db.UserNotifications.SingleAsync(notification => notification.Id == reminder.Id);
        Assert.Null(awakened.SnoozedUntil);
        Assert.Null(awakened.ReadAt);
        Assert.Equal(3, fixture.Queue.Ids.Count);
        Assert.Equal(2, fixture.Queue.Ids.Count(id => id == reminder.Id));
    }

    [Fact]
    public async Task RespondAsync_NoResolvesEveryPromptAndNotifiesOtherCurrentlyEntitledUsers()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var reporter = fixture.AddUser("Reporter", includeReminderPermission: true);
        var colleague = fixture.AddUser("Colleague", includeReminderPermission: true);
        var restricted = fixture.AddUser("Restricted", includeReminderPermission: false);
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
            notification.RecipientUserId == reporter.Id);

        var result = await fixture.Service.RespondAsync(
            reminder.Id,
            reporter.Id,
            hasPermission: true,
            reporter.AccountName,
            reporter.DisplayName,
            OperationScheduleResponse.No,
            new DateOnly(2026, 8, 17));

        Assert.Equal(OperationScheduleConfirmationStatus.Confirmed, result.Status);
        fixture.Db.ChangeTracker.Clear();
        var savedTask = await fixture.Db.Tasks.SingleAsync(candidate => candidate.ProjectId == project.Id);
        Assert.Equal(0m, savedTask.PercentComplete);
        Assert.True(savedTask.PercentCompleteManual);
        Assert.Equal(2, await fixture.Db.UserNotifications.CountAsync(notification => notification.RespondedAt != null));
        var response = await fixture.Db.UserNotifications.SingleAsync(notification =>
            notification.Kind == NotificationKind.OperationStartResponse);
        Assert.Equal(colleague.Id, response.RecipientUserId);
        Assert.NotEqual(reporter.Id, response.RecipientUserId);
        Assert.NotEqual(restricted.Id, response.RecipientUserId);
        Assert.Equal("Reporter reported Build did not start", response.Title);
        Assert.Contains(project.ProgramName, response.BodyPreview);
        Assert.Contains(await fixture.Db.ProjectAuditEntries.ToListAsync(), entry =>
            entry.Action == "OperationStartDeclined"
            && entry.ChangedByAccountName == reporter.AccountName);
        Assert.Equal(3, fixture.Queue.Ids.Count);
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

    [Fact]
    public async Task EnsureRemindersAsync_AutoSubscribesAssignedRolesButNotUnassignedPermittedUsers()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var contact = fixture.AddUser("Contact", includeReminderPermission: true);
        var engineer = fixture.AddUser("Engineer", includeReminderPermission: true);
        var sales = fixture.AddUser("Sales", includeReminderPermission: true);
        var project = fixture.AddProject(DueStartTask());
        var unassignedAdmin = fixture.AddUser("Unassigned admin", includeReminderPermission: true);
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));

        Assert.Equal(3, created);
        var recipientIds = await fixture.Db.UserNotifications
            .Select(notification => notification.RecipientUserId)
            .ToListAsync();
        Assert.Contains(contact.Id, recipientIds);
        Assert.Contains(engineer.Id, recipientIds);
        Assert.Contains(sales.Id, recipientIds);
        Assert.DoesNotContain(unassignedAdmin.Id, recipientIds);
        Assert.Equal(contact.DisplayName, project.ProgramManager);
        Assert.Equal(engineer.DisplayName, project.Engineer);
        Assert.Equal(sales.DisplayName, project.SalesPerson);
    }

    [Fact]
    public async Task EnsureRemindersAsync_ExplicitPreferenceOverridesAutomaticAssignment()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var assigned = fixture.AddUser("Assigned", includeReminderPermission: true);
        var project = fixture.AddProject(DueStartTask());
        var optedIn = fixture.AddUser("Opted in", includeReminderPermission: true);
        project.NotificationPreferences.Add(new ProjectNotificationPreference
        {
            Project = project,
            User = assigned,
            Enabled = false
        });
        project.NotificationPreferences.Add(new ProjectNotificationPreference
        {
            Project = project,
            User = optedIn,
            Enabled = true
        });
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));

        Assert.Equal(1, created);
        var notification = await fixture.Db.UserNotifications.SingleAsync();
        Assert.Equal(optedIn.Id, notification.RecipientUserId);
        Assert.NotEqual(assigned.Id, notification.RecipientUserId);
    }

    [Fact]
    public async Task EnsureRemindersAsync_DoesNotAskAboutAStartAlreadyConfirmedByFulcrum()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        fixture.AddUser("Assigned", includeReminderPermission: true);
        var task = DueStartTask();
        task.ExternalActualStartDate = task.StartDate;
        fixture.AddProject(task);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(0, await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17)));
        Assert.Empty(await fixture.Db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task ResolveFromExternalProgressAsync_ClosesMatchingStartAndFinishPrompts()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var user = fixture.AddUser("Assigned", includeReminderPermission: true);
        var task = DueStartTask();
        var project = fixture.AddProject(task);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.UserNotifications.AddRange(
            new UserNotification
            {
                RecipientUserId = user.Id,
                ProjectId = project.Id,
                ProjectTaskId = task.Id,
                Kind = NotificationKind.OperationStartConfirmation,
                Title = "Did Build start?"
            },
            new UserNotification
            {
                RecipientUserId = user.Id,
                ProjectId = project.Id,
                ProjectTaskId = task.Id,
                Kind = NotificationKind.OperationFinishConfirmation,
                Title = "Did Build finish?"
            });
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service.ResolveFromExternalProgressAsync([task.Id], []));
        Assert.Equal(1, await fixture.Db.UserNotifications.CountAsync(notification => notification.RespondedAt != null));
        Assert.Equal(1, await fixture.Service.ResolveFromExternalProgressAsync([], [task.Id]));
        fixture.Db.ChangeTracker.Clear();
        Assert.All(await fixture.Db.UserNotifications.ToListAsync(), notification =>
        {
            Assert.NotNull(notification.ReadAt);
            Assert.NotNull(notification.RespondedAt);
        });
    }

    [Fact]
    public async Task ProjectPreference_DisablingAssignedUserClosesExistingPromptsAndPersistsOptOut()
    {
        await using var fixture = await ReminderFixture.CreateAsync();
        var assigned = fixture.AddUser("Assigned", includeReminderPermission: true);
        var project = fixture.AddProject(DueStartTask());
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.EnsureRemindersAsync(new DateOnly(2026, 8, 17));

        var preference = await fixture.Audience.SetAsync(
            project.Id,
            assigned.AccountName,
            enabled: false,
            actorAccountName: assigned.AccountName);

        Assert.NotNull(preference);
        Assert.False(preference.Enabled);
        Assert.False(preference.IsAutomatic);
        Assert.Contains("Contact Lead", preference.AssignedRoles);
        fixture.Db.ChangeTracker.Clear();
        var notification = await fixture.Db.UserNotifications.SingleAsync();
        Assert.NotNull(notification.ReadAt);
        Assert.NotNull(notification.RespondedAt);
        Assert.Empty(await fixture.Audience.LoadRecipientsAsync(project));
    }

    private static ProjectTask DueStartTask() => new()
    {
        Sequence = 1,
        Title = "Build",
        StartDate = new DateOnly(2026, 8, 13),
        EndDate = new DateOnly(2026, 8, 19),
        EstimatedDuration = 4,
        PercentCompleteManual = true
    };

    private sealed class ReminderFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly List<AppUser> reminderUsers = [];
        private int userNumber;

        private ReminderFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
            Queue = new RecordingQueue();
            Audience = new ProjectNotificationAudienceService(db);
            Service = new OperationScheduleReminderService(
                db,
                new ScheduleCalculator(),
                new ProjectMetricsService(new ScheduleCalculator()),
                Queue,
                Audience);
        }

        public ProjectTrackerDbContext Db { get; }
        public RecordingQueue Queue { get; }
        public ProjectNotificationAudienceService Audience { get; }
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
            if (includeReminderPermission)
            {
                reminderUsers.Add(user);
            }
            return user;
        }

        public Project AddProject(params ProjectTask[] tasks)
        {
            var project = new Project
            {
                ProgramName = $"Reminder project {Guid.NewGuid():N}",
                ProgramStart = tasks.Min(task => task.StartDate),
                ProgramManager = reminderUsers.ElementAtOrDefault(0)?.DisplayName,
                Engineer = reminderUsers.ElementAtOrDefault(1)?.DisplayName,
                SalesPerson = reminderUsers.ElementAtOrDefault(2)?.DisplayName,
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

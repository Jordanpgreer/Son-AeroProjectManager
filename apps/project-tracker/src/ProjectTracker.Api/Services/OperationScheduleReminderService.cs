using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public enum OperationScheduleConfirmationStatus
{
    Confirmed,
    Snoozed,
    AlreadyConfirmed,
    NotFound,
    Forbidden,
    Stale
}

public enum OperationScheduleResponse
{
    Yes,
    No
}

public sealed record OperationScheduleConfirmationResult(
    OperationScheduleConfirmationStatus Status,
    int? ProjectId = null,
    int? ProjectTaskId = null);

public sealed class OperationScheduleReminderService(
    ProjectTrackerDbContext db,
    ScheduleCalculator scheduleCalculator,
    ProjectMetricsService metrics,
    IPushNotificationQueue pushQueue,
    ProjectNotificationAudienceService notificationAudience)
{
    private const string SystemAccountName = "PROJECT-TRACKER";
    private const string SystemDisplayName = "Project Tracker";

    public async Task<int> EnsureRemindersAsync(
        DateOnly today,
        CancellationToken cancellationToken = default)
        => await EnsureRemindersAsync(today, retryUniqueConflict: true, cancellationToken);

    private async Task<int> EnsureRemindersAsync(
        DateOnly today,
        bool retryUniqueConflict,
        CancellationToken cancellationToken)
    {
        var awakened = await WakeDueSnoozesAsync(today, cancellationToken);
        var calendar = await LoadCalendarAsync(cancellationToken);
        var scheduledDate = scheduleCalculator.PreviousWorkingDay(today, calendar);
        var tasks = await db.Tasks
            .AsNoTracking()
            .Include(task => task.Project)
            .Where(task => task.Project.CompletedOn == null
                && ((task.StartDate == scheduledDate
                        && task.PercentComplete == 0m
                        && task.PercentCompleteManual
                        && task.ExternalActualStartDate == null)
                    || (task.EndDate == scheduledDate && task.PercentComplete < 1m)))
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return awakened;
        }

        var taskIds = tasks.Select(task => task.Id).ToArray();
        var existing = await db.UserNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(notification =>
                notification.ProjectTaskId != null
                && taskIds.Contains(notification.ProjectTaskId.Value)
                && notification.ScheduledDate == scheduledDate
                && (notification.Kind == NotificationKind.OperationStartConfirmation
                    || notification.Kind == NotificationKind.OperationFinishConfirmation))
            .Select(notification => new
            {
                notification.RecipientUserId,
                ProjectTaskId = notification.ProjectTaskId!.Value,
                notification.Kind
            })
            .ToListAsync(cancellationToken);
        var existingKeys = existing
            .Select(row => (row.RecipientUserId, row.ProjectTaskId, row.Kind))
            .ToHashSet();

        var created = new List<UserNotification>();
        var recipientsByProject = new Dictionary<int, IReadOnlyCollection<int>>();
        foreach (var task in tasks)
        {
            if (!recipientsByProject.TryGetValue(task.ProjectId, out var recipientIds))
            {
                recipientIds = (await notificationAudience.LoadRecipientsAsync(task.Project, cancellationToken))
                    .Select(user => user.Id)
                    .ToArray();
                recipientsByProject[task.ProjectId] = recipientIds;
            }
            if (task.StartDate == scheduledDate
                && task.PercentComplete == 0m
                && task.PercentCompleteManual
                && task.ExternalActualStartDate is null)
            {
                AddForRecipients(task, NotificationKind.OperationStartConfirmation, "start", recipientIds, existingKeys, created, today, scheduledDate);
            }
            if (task.EndDate == scheduledDate && task.PercentComplete < 1m)
            {
                AddForRecipients(task, NotificationKind.OperationFinishConfirmation, "finish", recipientIds, existingKeys, created, today, scheduledDate);
            }
        }

        if (created.Count == 0)
        {
            return awakened;
        }

        db.UserNotifications.AddRange(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (retryUniqueConflict && IsUniqueConstraintViolation(exception))
        {
            // The direct IIS application and Portal gateway are separate
            // processes. If both workers race, the database unique index is the
            // authority; detach the rolled-back batch and re-read once so no
            // reminder is lost and the expected race is not logged as a fault.
            foreach (var notification in created)
            {
                db.Entry(notification).State = EntityState.Detached;
            }

            return awakened + await EnsureRemindersAsync(today, retryUniqueConflict: false, cancellationToken);
        }

        foreach (var notification in created)
        {
            pushQueue.TryEnqueue(notification.Id);
        }

        return awakened + created.Count;
    }

    public async Task<OperationScheduleConfirmationResult> ConfirmAsync(
        int notificationId,
        int recipientUserId,
        bool hasPermission,
        string actorAccountName,
        string actorDisplayName,
        DateOnly today,
        CancellationToken cancellationToken = default)
        => await RespondAsync(
            notificationId,
            recipientUserId,
            hasPermission,
            actorAccountName,
            actorDisplayName,
            OperationScheduleResponse.Yes,
            today,
            cancellationToken);

    public Task<int> ResolveFromExternalProgressAsync(
        IReadOnlyCollection<int> startedTaskIds,
        IReadOnlyCollection<int> completedTaskIds,
        CancellationToken cancellationToken = default)
    {
        if (startedTaskIds.Count == 0 && completedTaskIds.Count == 0)
            return Task.FromResult(0);

        var resolvedAt = DateTimeOffset.UtcNow;
        return db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification =>
                notification.ProjectTaskId != null
                && notification.RespondedAt == null
                && ((notification.Kind == NotificationKind.OperationStartConfirmation
                        && startedTaskIds.Contains(notification.ProjectTaskId.Value))
                    || (notification.Kind == NotificationKind.OperationFinishConfirmation
                        && completedTaskIds.Contains(notification.ProjectTaskId.Value))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(notification => notification.ReadAt, notification => notification.ReadAt ?? resolvedAt)
                .SetProperty(notification => notification.RespondedAt, resolvedAt),
                cancellationToken);
    }

    public async Task<OperationScheduleConfirmationResult> RespondAsync(
        int notificationId,
        int recipientUserId,
        bool hasPermission,
        string actorAccountName,
        string actorDisplayName,
        OperationScheduleResponse response,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        if (!hasPermission)
        {
            return new(OperationScheduleConfirmationStatus.Forbidden);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var notification = await db.UserNotifications
            .IgnoreQueryFilters()
            .Include(candidate => candidate.ProjectTask)!
                .ThenInclude(task => task!.OvertimeDays)
            .Include(candidate => candidate.ProjectTask)!
                .ThenInclude(task => task!.Project)
                    .ThenInclude(project => project.Tasks)
                        .ThenInclude(task => task.OvertimeDays)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == notificationId
                && candidate.RecipientUserId == recipientUserId,
                cancellationToken);
        if (notification is null || !IsScheduleConfirmation(notification.Kind))
        {
            return new(OperationScheduleConfirmationStatus.NotFound);
        }

        if (notification.RespondedAt is not null)
        {
            return new(
                OperationScheduleConfirmationStatus.AlreadyConfirmed,
                notification.ProjectId,
                notification.ProjectTaskId);
        }

        var task = notification.ProjectTask;
        var scheduledDate = notification.ScheduledDate;
        if (task is null
            || scheduledDate is null
            || task.Project.DeletedAt is not null
            || task.Project.CompletedOn is not null
            || (notification.Kind == NotificationKind.OperationStartConfirmation
                && (task.StartDate != scheduledDate
                    || task.PercentComplete > 0m
                    || !task.PercentCompleteManual
                    || task.ExternalActualStartDate is not null))
            || (notification.Kind == NotificationKind.OperationFinishConfirmation
                && (task.EndDate != scheduledDate || task.PercentComplete >= 1m)))
        {
            return new(OperationScheduleConfirmationStatus.Stale, notification.ProjectId, notification.ProjectTaskId);
        }

        var respondedAt = DateTimeOffset.UtcNow;
        var claimed = await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(candidate =>
                candidate.ProjectTaskId == task.Id
                && candidate.Kind == notification.Kind
                && candidate.ScheduledDate == scheduledDate
                && candidate.RespondedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.ReadAt, candidate => candidate.ReadAt ?? respondedAt)
                .SetProperty(candidate => candidate.RespondedAt, respondedAt),
                cancellationToken);
        if (claimed == 0)
        {
            return new(
                OperationScheduleConfirmationStatus.AlreadyConfirmed,
                notification.ProjectId,
                notification.ProjectTaskId);
        }

        var isStart = notification.Kind == NotificationKind.OperationStartConfirmation;
        var before = ProjectAuditService.CaptureTask(task);
        if (response == OperationScheduleResponse.Yes && isStart)
        {
            task.StartDateLocked = true;
            task.PercentCompleteManual = false;
        }
        else if (response == OperationScheduleResponse.Yes)
        {
            task.StartDateLocked = true;
            task.PercentComplete = 1m;
            task.PercentCompleteManual = true;
        }

        if (response == OperationScheduleResponse.Yes)
        {
            task.Version++;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            task.Project.Version++;
            task.Project.UpdatedAt = DateTimeOffset.UtcNow;
            var calendar = await LoadCalendarAsync(cancellationToken);
            metrics.RefreshProject(task.Project, calendar, today);
        }

        var after = ProjectAuditService.CaptureTask(task);
        var action = response == OperationScheduleResponse.Yes
            ? isStart ? "OperationStartConfirmed" : "OperationFinishConfirmed"
            : isStart ? "OperationStartDeclined" : "OperationFinishDeclined";
        var outcome = response == OperationScheduleResponse.Yes
            ? isStart ? "started" : "finished"
            : isStart ? "did not start" : "did not finish";
        db.ProjectAuditEntries.Add(new ProjectAuditEntry
        {
            Project = task.Project,
            ProjectTaskId = task.Id,
            Action = action,
            Summary = $"Reported operation {task.Sequence} {outcome} on {scheduledDate:MMM d, yyyy}",
            ChangesJson = System.Text.Json.JsonSerializer.Serialize(
                ProjectAuditService.Diff(before, after),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            ChangedByAccountName = actorAccountName,
            ChangedByDisplayName = actorDisplayName
        });

        var responseNotifications = await AddResponseNotificationsAsync(
            task,
            notification.Kind,
            response,
            recipientUserId,
            actorAccountName,
            actorDisplayName,
            scheduledDate.Value,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var responseNotification in responseNotifications)
        {
            pushQueue.TryEnqueue(responseNotification.Id);
        }

        return new(OperationScheduleConfirmationStatus.Confirmed, task.ProjectId, task.Id);
    }

    public async Task<OperationScheduleConfirmationResult> SnoozeAsync(
        int notificationId,
        int recipientUserId,
        bool hasPermission,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        if (!hasPermission)
        {
            return new(OperationScheduleConfirmationStatus.Forbidden);
        }

        var notification = await db.UserNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(candidate => candidate.ProjectTask)!
                .ThenInclude(task => task!.Project)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == notificationId
                && candidate.RecipientUserId == recipientUserId,
                cancellationToken);
        if (notification is null || !IsScheduleConfirmation(notification.Kind))
        {
            return new(OperationScheduleConfirmationStatus.NotFound);
        }

        if (notification.RespondedAt is not null)
        {
            return new(
                OperationScheduleConfirmationStatus.AlreadyConfirmed,
                notification.ProjectId,
                notification.ProjectTaskId);
        }

        var task = notification.ProjectTask;
        var scheduledDate = notification.ScheduledDate;
        if (!IsCurrentPrompt(notification, task, scheduledDate))
        {
            return new(OperationScheduleConfirmationStatus.Stale, notification.ProjectId, notification.ProjectTaskId);
        }

        var snoozedUntil = today.AddDays(1);
        var snoozedAt = DateTimeOffset.UtcNow;
        var updated = await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(candidate =>
                candidate.Id == notificationId
                && candidate.RecipientUserId == recipientUserId
                && candidate.RespondedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.SnoozedUntil, snoozedUntil)
                .SetProperty(candidate => candidate.ReadAt, snoozedAt),
                cancellationToken);

        return updated == 1
            ? new(OperationScheduleConfirmationStatus.Snoozed, notification.ProjectId, notification.ProjectTaskId)
            : new(OperationScheduleConfirmationStatus.AlreadyConfirmed, notification.ProjectId, notification.ProjectTaskId);
    }

    private async Task<IReadOnlyList<UserNotification>> AddResponseNotificationsAsync(
        ProjectTask task,
        NotificationKind promptKind,
        OperationScheduleResponse response,
        int actorUserId,
        string actorAccountName,
        string actorDisplayName,
        DateOnly scheduledDate,
        CancellationToken cancellationToken)
    {
        var isStart = promptKind == NotificationKind.OperationStartConfirmation;
        var responseKind = isStart
            ? NotificationKind.OperationStartResponse
            : NotificationKind.OperationFinishResponse;
        var existingRecipientIds = await db.UserNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(notification =>
                notification.ProjectTaskId == task.Id
                && notification.Kind == responseKind
                && notification.ScheduledDate == scheduledDate)
            .Select(notification => notification.RecipientUserId)
            .ToListAsync(cancellationToken);
        var recipientIds = (await notificationAudience.LoadRecipientsAsync(task.Project, cancellationToken))
            .Where(user => user.Id != actorUserId && !existingRecipientIds.Contains(user.Id))
            .Select(user => user.Id)
            .ToList();
        if (recipientIds.Count == 0)
        {
            return [];
        }

        var outcome = response == OperationScheduleResponse.Yes
            ? isStart ? "started" : "finished"
            : isStart ? "did not start" : "did not finish";
        var scheduleLabel = scheduledDate.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        var notifications = recipientIds.Select(recipientUserId => new UserNotification
        {
            RecipientUserId = recipientUserId,
            ProjectId = task.ProjectId,
            ProjectTaskId = task.Id,
            Kind = responseKind,
            ActorAccountName = actorAccountName,
            ActorDisplayName = actorDisplayName,
            Title = $"{actorDisplayName} reported {task.Title} {outcome}",
            BodyPreview = $"{task.Project.ProgramName} · Scheduled {(isStart ? "start" : "finish")}: {scheduleLabel}",
            ScheduledDate = scheduledDate
        }).ToList();
        db.UserNotifications.AddRange(notifications);
        return notifications;
    }

    private async Task<int> WakeDueSnoozesAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var candidates = await db.UserNotifications
            .AsNoTracking()
            .Include(notification => notification.Project)
            .Include(notification => notification.RecipientUser)
                .ThenInclude(user => user.ProjectNotificationPreferences)
            .Where(notification =>
                notification.SnoozedUntil != null
                && notification.SnoozedUntil <= today
                && notification.RespondedAt == null
                && (notification.Kind == NotificationKind.OperationStartConfirmation
                    || notification.Kind == NotificationKind.OperationFinishConfirmation)
                && notification.ProjectTask != null
                && notification.Project.CompletedOn == null
                && notification.RecipientUser.IsActive
                && notification.RecipientUser.GroupMemberships.Any(membership =>
                    membership.Group.Permissions.Any(permission =>
                        permission.PermissionKey == ApplicationPermissions.ModuleView))
                && notification.RecipientUser.GroupMemberships.Any(membership =>
                    membership.Group.Permissions.Any(permission =>
                        permission.PermissionKey == ProjectTrackerPermissions.OperationScheduleConfirm))
                && ((notification.Kind == NotificationKind.OperationStartConfirmation
                        && notification.ScheduledDate == notification.ProjectTask.StartDate
                        && notification.ProjectTask.PercentComplete == 0m
                        && notification.ProjectTask.PercentCompleteManual
                        && notification.ProjectTask.ExternalActualStartDate == null)
                    || (notification.Kind == NotificationKind.OperationFinishConfirmation
                        && notification.ScheduledDate == notification.ProjectTask.EndDate
                        && notification.ProjectTask.PercentComplete < 1m)))
            .ToListAsync(cancellationToken);
        var candidateIds = candidates
            .Where(notification => ProjectNotificationAudienceService.IsEnabled(
                notification.Project,
                notification.RecipientUser))
            .Select(notification => notification.Id)
            .ToList();

        var awakenedIds = new List<int>(candidateIds.Count);
        foreach (var notificationId in candidateIds)
        {
            var awakenedAt = DateTimeOffset.UtcNow;
            var claimed = await db.UserNotifications
                .IgnoreQueryFilters()
                .Where(notification =>
                    notification.Id == notificationId
                    && notification.SnoozedUntil != null
                    && notification.SnoozedUntil <= today
                    && notification.RespondedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(notification => notification.SnoozedUntil, (DateOnly?)null)
                    .SetProperty(notification => notification.ReadAt, (DateTimeOffset?)null)
                    .SetProperty(notification => notification.CreatedAt, awakenedAt),
                    cancellationToken);
            if (claimed == 1)
            {
                awakenedIds.Add(notificationId);
            }
        }

        foreach (var notificationId in awakenedIds)
        {
            pushQueue.TryEnqueue(notificationId);
        }

        return awakenedIds.Count;
    }

    private static bool IsCurrentPrompt(
        UserNotification notification,
        ProjectTask? task,
        DateOnly? scheduledDate) =>
        task is not null
        && scheduledDate is not null
        && task.Project.DeletedAt is null
        && task.Project.CompletedOn is null
        && ((notification.Kind == NotificationKind.OperationStartConfirmation
                && task.StartDate == scheduledDate
                && task.PercentComplete == 0m
                && task.PercentCompleteManual
                && task.ExternalActualStartDate is null)
            || (notification.Kind == NotificationKind.OperationFinishConfirmation
                && task.EndDate == scheduledDate
                && task.PercentComplete < 1m));

    private async Task<ScheduleCalendar> LoadCalendarAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ScheduleSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? new ScheduleSettings();
        var holidays = (await db.Holidays.AsNoTracking()
            .Select(holiday => holiday.Date)
            .ToListAsync(cancellationToken)).ToHashSet();
        return new ScheduleCalendar(settings.GetWorkingDays(), holidays);
    }

    private static bool IsScheduleConfirmation(NotificationKind kind) =>
        kind is NotificationKind.OperationStartConfirmation or NotificationKind.OperationFinishConfirmation;

    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            if (current is SqliteException { SqliteErrorCode: 19 })
            {
                return true;
            }

            if (current.InnerException is null)
            {
                break;
            }
        }

        return false;
    }

    private static void AddForRecipients(
        ProjectTask task,
        NotificationKind kind,
        string verb,
        IReadOnlyCollection<int> recipientIds,
        ISet<(int RecipientUserId, int ProjectTaskId, NotificationKind Kind)> existingKeys,
        ICollection<UserNotification> created,
        DateOnly today,
        DateOnly scheduledDate)
    {
        var relativeDate = scheduledDate == today.AddDays(-1)
            ? "yesterday"
            : scheduledDate.ToString("dddd", CultureInfo.InvariantCulture);
        foreach (var recipientUserId in recipientIds)
        {
            if (!existingKeys.Add((recipientUserId, task.Id, kind)))
            {
                continue;
            }

            created.Add(new UserNotification
            {
                RecipientUserId = recipientUserId,
                ProjectId = task.ProjectId,
                ProjectTaskId = task.Id,
                Kind = kind,
                ActorAccountName = SystemAccountName,
                ActorDisplayName = SystemDisplayName,
                Title = $"Did {task.Title} {verb} {relativeDate}?",
                BodyPreview = $"{task.Project.ProgramName} · Scheduled {verb}: {scheduledDate.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)}",
                ScheduledDate = scheduledDate
            });
        }
    }
}

public sealed class OperationScheduleReminderWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OperationScheduleReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the immutable IIS cutover enough time to finish or roll back
        // before this release writes values older binaries cannot deserialize.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reminders = scope.ServiceProvider.GetRequiredService<OperationScheduleReminderService>();
                await reminders.EnsureRemindersAsync(
                    DateOnly.FromDateTime(DateTime.Today),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Operation schedule reminders could not be generated.");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

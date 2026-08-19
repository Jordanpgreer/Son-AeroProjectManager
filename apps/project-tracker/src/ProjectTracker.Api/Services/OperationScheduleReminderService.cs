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
    AlreadyConfirmed,
    NotFound,
    Forbidden,
    Stale
}

public sealed record OperationScheduleConfirmationResult(
    OperationScheduleConfirmationStatus Status,
    int? ProjectId = null,
    int? ProjectTaskId = null);

public sealed class OperationScheduleReminderService(
    ProjectTrackerDbContext db,
    ScheduleCalculator scheduleCalculator,
    ProjectMetricsService metrics,
    IPushNotificationQueue pushQueue)
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
        var calendar = await LoadCalendarAsync(cancellationToken);
        var scheduledDate = scheduleCalculator.PreviousWorkingDay(today, calendar);
        var recipientIds = await db.Users
            .AsNoTracking()
            .Where(user =>
                user.IsActive
                && user.GroupMemberships.Any(membership => membership.Group.Permissions.Any(permission =>
                    permission.PermissionKey == ApplicationPermissions.ModuleView))
                && user.GroupMemberships.Any(membership => membership.Group.Permissions.Any(permission =>
                    permission.PermissionKey == ProjectTrackerPermissions.OperationScheduleConfirm)))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        if (recipientIds.Count == 0)
        {
            return 0;
        }

        var tasks = await db.Tasks
            .AsNoTracking()
            .Include(task => task.Project)
            .Where(task => task.Project.CompletedOn == null
                && ((task.StartDate == scheduledDate
                        && task.PercentComplete == 0m
                        && task.PercentCompleteManual)
                    || (task.EndDate == scheduledDate && task.PercentComplete < 1m)))
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return 0;
        }

        var taskIds = tasks.Select(task => task.Id).ToArray();
        var existing = await db.UserNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(notification =>
                recipientIds.Contains(notification.RecipientUserId)
                && notification.ProjectTaskId != null
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
        foreach (var task in tasks)
        {
            if (task.StartDate == scheduledDate && task.PercentComplete == 0m && task.PercentCompleteManual)
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
            return 0;
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

            return await EnsureRemindersAsync(today, retryUniqueConflict: false, cancellationToken);
        }

        foreach (var notification in created)
        {
            pushQueue.TryEnqueue(notification.Id);
        }

        return created.Count;
    }

    public async Task<OperationScheduleConfirmationResult> ConfirmAsync(
        int notificationId,
        int recipientUserId,
        bool hasPermission,
        string actorAccountName,
        string actorDisplayName,
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
                && task.StartDate != scheduledDate)
            || (notification.Kind == NotificationKind.OperationFinishConfirmation
                && task.EndDate != scheduledDate))
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

        var before = ProjectAuditService.CaptureTask(task);
        if (notification.Kind == NotificationKind.OperationStartConfirmation)
        {
            if (task.PercentComplete > 0m || !task.PercentCompleteManual)
            {
                return new(OperationScheduleConfirmationStatus.Stale, task.ProjectId, task.Id);
            }

            task.StartDateLocked = true;
            task.PercentCompleteManual = false;
        }
        else
        {
            if (task.PercentComplete >= 1m)
            {
                return new(OperationScheduleConfirmationStatus.Stale, task.ProjectId, task.Id);
            }

            task.StartDateLocked = true;
            task.PercentComplete = 1m;
            task.PercentCompleteManual = true;
        }

        task.Version++;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        task.Project.Version++;
        task.Project.UpdatedAt = DateTimeOffset.UtcNow;
        var calendar = await LoadCalendarAsync(cancellationToken);
        metrics.RefreshProject(task.Project, calendar, today);

        var after = ProjectAuditService.CaptureTask(task);
        db.ProjectAuditEntries.Add(new ProjectAuditEntry
        {
            Project = task.Project,
            ProjectTaskId = task.Id,
            Action = notification.Kind == NotificationKind.OperationStartConfirmation
                ? "OperationStartConfirmed"
                : "OperationFinishConfirmed",
            Summary = notification.Kind == NotificationKind.OperationStartConfirmation
                ? $"Confirmed operation {task.Sequence} started on {scheduledDate:MMM d, yyyy}"
                : $"Confirmed operation {task.Sequence} finished on {scheduledDate:MMM d, yyyy}",
            ChangesJson = System.Text.Json.JsonSerializer.Serialize(
                ProjectAuditService.Diff(before, after),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            ChangedByAccountName = actorAccountName,
            ChangedByDisplayName = actorDisplayName
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OperationScheduleConfirmationStatus.Confirmed, task.ProjectId, task.Id);
    }

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

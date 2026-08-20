using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class NotificationReadService(
    ProjectTrackerDbContext db,
    ILogger<NotificationReadService>? logger = null)
{
    private const string ProjectChatMention = nameof(NotificationKind.ProjectChatMention);
    private const string OperationNoteMention = nameof(NotificationKind.OperationNoteMention);
    private const string OperationStartConfirmation = nameof(NotificationKind.OperationStartConfirmation);
    private const string OperationFinishConfirmation = nameof(NotificationKind.OperationFinishConfirmation);
    private const string OperationStartResponse = nameof(NotificationKind.OperationStartResponse);
    private const string OperationFinishResponse = nameof(NotificationKind.OperationFinishResponse);

    public async Task<IReadOnlyList<UserNotificationDto>> GetAsync(
        int recipientUserId,
        string recipientAccountName,
        bool unreadOnly,
        int take,
        bool includeScheduleConfirmations = true,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadValidRowsAsync(recipientUserId, cancellationToken);
        return rows
            .Where(row => !WindowsAccountNames.Equals(row.ActorAccountName, recipientAccountName))
            .Where(row => includeScheduleConfirmations || !IsScheduleConfirmation(row.Kind))
            .Where(row => !unreadOnly || row.ReadAt is null)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(take)
            .Select(ToDto)
            .ToList();
    }

    public async Task<int> GetUnreadCountAsync(
        int recipientUserId,
        string recipientAccountName,
        bool includeScheduleConfirmations = true,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadValidRowsAsync(recipientUserId, cancellationToken);
        return rows.Count(row =>
            row.ReadAt is null
            && (includeScheduleConfirmations || !IsScheduleConfirmation(row.Kind))
            && !WindowsAccountNames.Equals(row.ActorAccountName, recipientAccountName));
    }

    public async Task<bool> MarkReadAsync(
        int id,
        int recipientUserId,
        string recipientAccountName,
        bool includeScheduleConfirmations = true,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadValidRowsAsync(recipientUserId, cancellationToken);
        if (!rows.Any(row =>
                row.Id == id
                && (includeScheduleConfirmations || !IsScheduleConfirmation(row.Kind))
                && !WindowsAccountNames.Equals(row.ActorAccountName, recipientAccountName)))
        {
            return false;
        }

        var readAt = DateTimeOffset.UtcNow;
        await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification => notification.Id == id && notification.RecipientUserId == recipientUserId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notification => notification.ReadAt, readAt),
                cancellationToken);
        return true;
    }

    public async Task MarkAllReadAsync(
        int recipientUserId,
        string recipientAccountName,
        bool includeScheduleConfirmations = true,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadValidRowsAsync(recipientUserId, cancellationToken);
        var unreadIds = rows
            .Where(row =>
                row.ReadAt is null
                && (includeScheduleConfirmations || !IsScheduleConfirmation(row.Kind))
                && !WindowsAccountNames.Equals(row.ActorAccountName, recipientAccountName))
            .Select(row => row.Id)
            .ToArray();
        if (unreadIds.Length == 0)
        {
            return;
        }

        var readAt = DateTimeOffset.UtcNow;
        await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification =>
                notification.RecipientUserId == recipientUserId
                && unreadIds.Contains(notification.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notification => notification.ReadAt, readAt),
                cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        int id,
        int recipientUserId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification =>
                notification.Id == id
                && notification.RecipientUserId == recipientUserId
                && (notification.RespondedAt != null
                    || (notification.Kind != NotificationKind.OperationStartConfirmation
                        && notification.Kind != NotificationKind.OperationFinishConfirmation)))
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    public Task<int> DeleteAllAsync(
        int recipientUserId,
        CancellationToken cancellationToken = default) =>
        db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification => notification.RecipientUserId == recipientUserId
                && (notification.RespondedAt != null
                    || (notification.Kind != NotificationKind.OperationStartConfirmation
                        && notification.Kind != NotificationKind.OperationFinishConfirmation)))
            .ExecuteDeleteAsync(cancellationToken);

    private async Task<IReadOnlyList<NotificationReadRow>> LoadValidRowsAsync(
        int recipientUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.SqlQuery<NotificationReadRow>($"""
                SELECT
                    notification.[Id],
                    notification.[Kind],
                    notification.[ProjectId],
                    project.[ProgramName] AS [ProjectName],
                    notification.[ProjectTaskId],
                    task.[Title] AS [OperationName],
                    notification.[ActorAccountName],
                    notification.[ActorDisplayName],
                    notification.[Title],
                    notification.[BodyPreview],
                    notification.[ScheduledDate],
                    notification.[CreatedAt],
                    notification.[ReadAt],
                    notification.[RespondedAt]
                FROM [UserNotifications] AS notification
                INNER JOIN [Projects] AS project
                    ON project.[Id] = notification.[ProjectId]
                    AND project.[DeletedAt] IS NULL
                LEFT JOIN [Tasks] AS task
                    ON task.[Id] = notification.[ProjectTaskId]
                    AND task.[ProjectId] = notification.[ProjectId]
                LEFT JOIN [ProjectMessages] AS message
                    ON message.[Id] = notification.[ProjectMessageId]
                    AND message.[ProjectId] = notification.[ProjectId]
                WHERE notification.[RecipientUserId] = {recipientUserId}
                    AND (
                        (notification.[Kind] = {ProjectChatMention} AND message.[Id] IS NOT NULL)
                        OR
                        (notification.[Kind] = {OperationNoteMention} AND task.[Id] IS NOT NULL)
                        OR
                        (notification.[Kind] = {OperationStartConfirmation}
                            AND task.[Id] IS NOT NULL
                            AND notification.[RespondedAt] IS NULL
                            AND notification.[SnoozedUntil] IS NULL
                            AND notification.[ScheduledDate] = task.[StartDate]
                            AND CAST(task.[PercentComplete] AS REAL) = 0
                            AND task.[PercentCompleteManual] = 1)
                        OR
                        (notification.[Kind] = {OperationFinishConfirmation}
                            AND task.[Id] IS NOT NULL
                            AND notification.[RespondedAt] IS NULL
                            AND notification.[SnoozedUntil] IS NULL
                            AND notification.[ScheduledDate] = task.[EndDate]
                            AND CAST(task.[PercentComplete] AS REAL) < 1)
                        OR
                        (notification.[Kind] = {OperationStartResponse} AND task.[Id] IS NOT NULL)
                        OR
                        (notification.[Kind] = {OperationFinishResponse} AND task.[Id] IS NOT NULL)
                    )
                """).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(
                exception,
                "Notifications could not be read for user {RecipientUserId}; returning an empty inbox instead.",
                recipientUserId);
            return [];
        }
    }

    private static UserNotificationDto ToDto(NotificationReadRow row) => new(
        row.Id,
        row.Kind switch
        {
            OperationNoteMention => NotificationKind.OperationNoteMention,
            OperationStartConfirmation => NotificationKind.OperationStartConfirmation,
            OperationFinishConfirmation => NotificationKind.OperationFinishConfirmation,
            OperationStartResponse => NotificationKind.OperationStartResponse,
            OperationFinishResponse => NotificationKind.OperationFinishResponse,
            _ => NotificationKind.ProjectChatMention
        },
        row.ProjectId,
        row.ProjectName,
        row.ProjectTaskId,
        row.OperationName,
        row.ActorAccountName,
        row.ActorDisplayName,
        row.Title,
        row.BodyPreview,
        row.ScheduledDate,
        row.CreatedAt,
        row.ReadAt);

    private static bool IsScheduleConfirmation(string kind) =>
        kind is OperationStartConfirmation
            or OperationFinishConfirmation
            or OperationStartResponse
            or OperationFinishResponse;

    private sealed class NotificationReadRow
    {
        public int Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public int ProjectId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public int? ProjectTaskId { get; set; }
        public string? OperationName { get; set; }
        public string ActorAccountName { get; set; } = string.Empty;
        public string ActorDisplayName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string BodyPreview { get; set; } = string.Empty;
        public DateOnly? ScheduledDate { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ReadAt { get; set; }
        public DateTimeOffset? RespondedAt { get; set; }
    }
}

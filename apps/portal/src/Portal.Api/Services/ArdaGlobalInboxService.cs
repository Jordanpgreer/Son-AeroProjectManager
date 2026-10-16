using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Models;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

/// <summary>One current-user inbox spanning durable Project Tracker mentions and Portal broker items.</summary>
public sealed class ArdaGlobalInboxService(
    PortalRoleDbContext db,
    ApplicationRegistry applications,
    ILogger<ArdaGlobalInboxService> logger)
{
    private const string ProjectTracker = "project-tracker";
    private const string ProjectChatMention = "ProjectChatMention";
    private const string OperationNoteMention = "OperationNoteMention";
    private const string OperationStartConfirmation = "OperationStartConfirmation";
    private const string OperationFinishConfirmation = "OperationFinishConfirmation";
    private const string OperationStartResponse = "OperationStartResponse";
    private const string OperationFinishResponse = "OperationFinishResponse";
    private const string OperationScheduleConfirmPermission = "notifications.operationSchedule.confirm";
    private const int PageSize = 50;

    public async Task<IReadOnlySet<string>> AuthorizedSourceIdsAsync(
        MeDto user, CancellationToken cancellationToken)
    {
        var ids = VisibleApplications(user);
        if (ids.ContainsKey(ProjectTracker))
        {
            var keys = WindowsAccountNames.LookupKeys(user.AccountName);
            var userId = await db.Users.AsNoTracking()
                .Where(candidate => candidate.IsActive && keys.Contains(candidate.AccountName.ToUpper()))
                .Select(candidate => (int?)candidate.Id).FirstOrDefaultAsync(cancellationToken);
            if (userId is null || !await CanViewTrackerProjectAsync(userId.Value, cancellationToken))
                ids.Remove(ProjectTracker);
        }
        return ids.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task<ArdaGlobalInboxDto> GetAsync(MeDto currentUser, CancellationToken cancellationToken) =>
        ReadAsync(currentUser, PageSize, cancellationToken);

    private async Task<ArdaGlobalInboxDto> ReadAsync(
        MeDto currentUser, int? limit, CancellationToken cancellationToken)
    {
        var visible = VisibleApplications(currentUser);
        if (visible.Count == 0) return new([], 0);

        var lookupKeys = WindowsAccountNames.LookupKeys(currentUser.AccountName);
        var accountKey = AccountKey(currentUser);
        var pushItems = new List<ArdaGlobalInboxItemDto>();
        var pushCount = 0;
        foreach (var source in visible.Values.Where(app => app.Id != ProjectTracker))
        {
            var originPrefix = OriginPrefix(source.Url);
            if (originPrefix is null) continue;
            var sourceId = source.Id;
            var query = db.ArdaPushNotifications.AsNoTracking()
                .Where(item => lookupKeys.Contains(item.RecipientAccountName.ToUpper())
                    && item.SourceModule == sourceId
                    && item.TargetUrl.StartsWith(originPrefix)
                    && !db.ArdaInboxDismissals.Any(dismissal => dismissal.AccountName == accountKey
                        && dismissal.SourceKind == "push" && dismissal.SourceNotificationId == item.Id));
            pushCount += await query.CountAsync(item => item.ReadAt == null, cancellationToken);
            var ordered = query.OrderByDescending(item => item.Id).AsQueryable();
            if (limit is { } take) ordered = ordered.Take(take);
            var candidates = await ordered
                .Select(item => new ArdaGlobalInboxItemDto(
                    $"push:{item.Id}", item.SourceModule, item.Title, item.Body,
                    item.TargetUrl, item.CreatedAt, item.ReadAt))
                .ToListAsync(cancellationToken);
            pushItems.AddRange(candidates.Where(item => IsSafeTarget(item.TargetUrl, source.Url)));
        }

        var trackerItems = new List<ArdaGlobalInboxItemDto>();
        var trackerCount = 0;
        if (visible.TryGetValue(ProjectTracker, out var tracker))
        {
            try
            {
                var user = await db.Users.AsNoTracking()
                    .Where(candidate => candidate.IsActive
                        && lookupKeys.Contains(candidate.AccountName.ToUpper()))
                    .Select(candidate => new { candidate.Id, candidate.AccountName })
                    .FirstOrDefaultAsync(cancellationToken);
                if (user is not null && await CanViewTrackerProjectAsync(user.Id, cancellationToken))
                {
                    var query = ValidTrackerRows(user.Id, user.AccountName)
                        .Where(item => !db.ArdaInboxDismissals.Any(dismissal => dismissal.AccountName == accountKey
                            && dismissal.SourceKind == ProjectTracker && dismissal.SourceNotificationId == item.Id));
                    trackerCount = await query.CountAsync(item => item.ReadAt == null, cancellationToken);
                    var ordered = query.OrderByDescending(item => item.Id).AsQueryable();
                    if (limit is { } take) ordered = ordered.Take(take);
                    var rows = await ordered.ToListAsync(cancellationToken);
                    trackerItems = rows.Select(item => new ArdaGlobalInboxItemDto(
                        $"project-tracker:{item.Id}", ProjectTracker, item.Title, item.BodyPreview,
                        TrackerTargetUrl(tracker.Url, item), item.CreatedAt, item.ReadAt)).ToList();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Project Tracker notifications are temporarily unavailable in the global inbox.");
            }
        }

        var combined = pushItems.Concat(trackerItems)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).AsEnumerable();
        if (limit is { } total) combined = combined.Take(total);
        return new ArdaGlobalInboxDto(combined.ToList(), pushCount + trackerCount);
    }

    public async Task<string?> OpenAsync(string id, MeDto currentUser, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, currentUser, cancellationToken);
        if (item is null) return null;
        if (!await MarkReadAsync(id, currentUser, cancellationToken)) return null;
        return item.TargetUrl;
    }

    public async Task<bool> MarkReadAsync(string id, MeDto currentUser, CancellationToken cancellationToken)
    {
        if (await IsDismissedAsync(id, currentUser, cancellationToken)) return false;
        var visible = VisibleApplications(currentUser);
        var lookupKeys = WindowsAccountNames.LookupKeys(currentUser.AccountName);
        if (TryId(id, "push:", out var pushId))
        {
            var row = await db.ArdaPushNotifications.SingleOrDefaultAsync(item => item.Id == pushId
                && lookupKeys.Contains(item.RecipientAccountName.ToUpper()), cancellationToken);
            if (row is null || !visible.TryGetValue(row.SourceModule, out var source)
                || !IsSafeTarget(row.TargetUrl, source.Url)) return false;
            row.ReadAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        if (!TryId(id, "project-tracker:", out var trackerId)
            || !visible.ContainsKey(ProjectTracker) || trackerId > int.MaxValue) return false;
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.IsActive && lookupKeys.Contains(candidate.AccountName.ToUpper()))
            .Select(candidate => new { candidate.Id, candidate.AccountName })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null) return false;
        if (!await CanViewTrackerProjectAsync(user.Id, cancellationToken)) return false;
        var rowTracker = await ValidTrackerRows(user.Id, user.AccountName)
            .SingleOrDefaultAsync(item => item.Id == (int)trackerId, cancellationToken);
        if (rowTracker is null) return false;
        rowTracker.ReadAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DismissAsync(string id, MeDto currentUser, CancellationToken cancellationToken)
    {
        // Resolve authorization and safe navigation from the source before storing any state.
        if (await FindAsync(id, currentUser, cancellationToken) is null) return false;
        await StoreDismissalAsync(id, currentUser, cancellationToken);
        return true;
    }

    public async Task<int> ClearAsync(MeDto currentUser, CancellationToken cancellationToken)
    {
        // Clear the complete authorized inbox, including read rows beyond the visible 50-item page.
        // New notifications arriving after this snapshot remain available.
        var snapshot = await ReadAsync(currentUser, null, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var item in snapshot.Items)
            await StoreDismissalAsync(item.Id, currentUser, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return snapshot.Items.Count;
    }

    private Task<bool> IsDismissedAsync(string id, MeDto user, CancellationToken cancellationToken)
    {
        if (!TrySource(id, out var source, out var number)) return Task.FromResult(false);
        var accountKey = AccountKey(user);
        return db.ArdaInboxDismissals.AnyAsync(item => item.AccountName == accountKey
            && item.SourceKind == source && item.SourceNotificationId == number, cancellationToken);
    }

    private async Task StoreDismissalAsync(string id, MeDto user, CancellationToken cancellationToken)
    {
        if (!TrySource(id, out var source, out var number)) return;
        var accountKey = AccountKey(user);
        var dismissedAt = DateTimeOffset.UtcNow;
        // Parameterized, atomic insert-if-missing makes simultaneous tabs and retries idempotent.
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ArdaInboxDismissals" ("AccountName", "SourceKind", "SourceNotificationId", "DismissedAt")
                VALUES ({accountKey}, {source}, {number}, {dismissedAt})
                ON CONFLICT ("AccountName", "SourceKind", "SourceNotificationId") DO NOTHING;
                """, cancellationToken);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [dbo].[ArdaInboxDismissals] ([AccountName], [SourceKind], [SourceNotificationId], [DismissedAt])
                SELECT {accountKey}, {source}, {number}, {dismissedAt}
                WHERE NOT EXISTS (SELECT 1 FROM [dbo].[ArdaInboxDismissals] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [AccountName] = {accountKey} AND [SourceKind] = {source} AND [SourceNotificationId] = {number});
                """, cancellationToken);
    }

    private static string AccountKey(MeDto user) =>
        WindowsAccountNames.Normalize(user.AccountName)?.ToUpperInvariant() ?? string.Empty;

    private static bool TrySource(string id, out string source, out long number)
    {
        source = "push";
        if (TryId(id, "push:", out number)) return true;
        source = ProjectTracker;
        return TryId(id, "project-tracker:", out number) && number <= int.MaxValue;
    }

    private async Task<ArdaGlobalInboxItemDto?> FindAsync(
        string id, MeDto currentUser, CancellationToken cancellationToken)
    {
        var visible = VisibleApplications(currentUser);
        var lookupKeys = WindowsAccountNames.LookupKeys(currentUser.AccountName);
        if (TryId(id, "push:", out var pushId))
        {
            var row = await db.ArdaPushNotifications.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == pushId
                    && lookupKeys.Contains(item.RecipientAccountName.ToUpper()), cancellationToken);
            return row is not null && visible.TryGetValue(row.SourceModule, out var app)
                && IsSafeTarget(row.TargetUrl, app.Url)
                ? new(id, row.SourceModule, row.Title, row.Body, row.TargetUrl, row.CreatedAt, row.ReadAt)
                : null;
        }
        if (!TryId(id, "project-tracker:", out var trackerId)
            || !visible.TryGetValue(ProjectTracker, out var tracker) || trackerId > int.MaxValue) return null;
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.IsActive && lookupKeys.Contains(candidate.AccountName.ToUpper()))
            .Select(candidate => new { candidate.Id, candidate.AccountName })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null) return null;
        if (!await CanViewTrackerProjectAsync(user.Id, cancellationToken)) return null;
        var rowTracker = await ValidTrackerRows(user.Id, user.AccountName).AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == (int)trackerId, cancellationToken);
        return rowTracker is null ? null : new(id, ProjectTracker, rowTracker.Title, rowTracker.BodyPreview,
            TrackerTargetUrl(tracker.Url, rowTracker), rowTracker.CreatedAt, rowTracker.ReadAt);
    }

    private IQueryable<PortalNotificationRecord> ValidTrackerRows(int userId, string accountName)
    {
        var self = WindowsAccountNames.LookupKeys(accountName);
        return db.UserNotifications.Where(item => item.RecipientUserId == userId
            && !self.Contains(item.ActorAccountName.ToUpper())
            && db.NotificationProjects.Any(project => project.Id == item.ProjectId && project.DeletedAt == null)
            && ((item.Kind == ProjectChatMention && item.ProjectMessageId != null
                    && db.NotificationMessages.Any(message => message.Id == item.ProjectMessageId
                        && message.ProjectId == item.ProjectId))
                || (item.Kind == OperationNoteMention && item.ProjectTaskId != null
                    && db.NotificationTasks.Any(task => task.Id == item.ProjectTaskId
                        && task.ProjectId == item.ProjectId))
                || (item.Kind == OperationStartResponse || item.Kind == OperationFinishResponse)
                    && item.ProjectTaskId != null
                    && db.NotificationTasks.Any(task => task.Id == item.ProjectTaskId
                        && task.ProjectId == item.ProjectId)
                || item.Kind == OperationStartConfirmation && item.ProjectTaskId != null
                    && db.Set<PortalProjectTrackerMembershipRecord>().Any(membership =>
                        membership.AppUserId == userId && membership.Group.Permissions.Any(permission =>
                            permission.PermissionKey == OperationScheduleConfirmPermission))
                    && item.RespondedAt == null && item.SnoozedUntil == null
                    && db.NotificationTasks.Any(task => task.Id == item.ProjectTaskId
                        && task.ProjectId == item.ProjectId
                        && task.StartDate == item.ScheduledDate
                        && task.PercentComplete == 0
                        && task.PercentCompleteManual
                        && task.ExternalActualStartDate == null)
                || item.Kind == OperationFinishConfirmation && item.ProjectTaskId != null
                    && db.Set<PortalProjectTrackerMembershipRecord>().Any(membership =>
                        membership.AppUserId == userId && membership.Group.Permissions.Any(permission =>
                            permission.PermissionKey == OperationScheduleConfirmPermission))
                    && item.RespondedAt == null && item.SnoozedUntil == null
                    && db.NotificationTasks.Any(task => task.Id == item.ProjectTaskId
                        && task.ProjectId == item.ProjectId
                        && task.EndDate == item.ScheduledDate
                        && task.PercentComplete < 1)));
    }

    private Dictionary<string, ApplicationEntry> VisibleApplications(MeDto user)
    {
        var visible = applications.GetVisibleFor(user)
            .ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
        // Quality's current producer publishes shipment-comment notifications only.
        var canSeeQualityShipments = user.Modules.Any(module =>
            module.ModuleKey == ApplicationModules.QualityAssurance
            && module.Permissions.Contains(QualityAssurancePermissions.ShipmentsView,
                StringComparer.OrdinalIgnoreCase));
        if (!canSeeQualityShipments) visible.Remove("quality-assurance");
        return visible;
    }

    private Task<bool> CanViewTrackerProjectAsync(int userId, CancellationToken cancellationToken) =>
        db.Set<PortalProjectTrackerMembershipRecord>().AsNoTracking().AnyAsync(membership =>
            membership.AppUserId == userId && membership.Group.Permissions.Any(permission =>
                permission.PermissionKey == ApplicationPermissions.ProjectDetailView), cancellationToken);

    private static bool TryId(string id, string prefix, out long number)
    {
        number = 0;
        return id.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(prefix.Length), out number) && number > 0;
    }

    private static string TrackerTargetUrl(string configuredUrl, PortalNotificationRecord item)
    {
        var uri = new Uri(configuredUrl);
        var path = $"/?notificationProjectId={item.ProjectId}&notificationKind={Uri.EscapeDataString(item.Kind)}&notificationId={item.Id}";
        if (item.ProjectTaskId is { } taskId) path += $"&notificationTaskId={taskId}";
        return new Uri(uri, path).AbsoluteUri;
    }

    internal static bool IsSafeTarget(string targetUrl, string configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var origin)
            || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)) return false;
        return origin.Scheme is "http" or "https" && target.Scheme == origin.Scheme
            && string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == origin.Port && string.IsNullOrEmpty(target.UserInfo);
    }

    private static string? OriginPrefix(string configuredUrl) =>
        Uri.TryCreate(configuredUrl, UriKind.Absolute, out var origin)
            && origin.Scheme is "http" or "https"
            ? origin.GetLeftPart(UriPartial.Authority) + "/"
            : null;
}

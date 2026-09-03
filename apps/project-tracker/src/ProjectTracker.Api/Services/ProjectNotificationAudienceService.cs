using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class ProjectNotificationAudienceService(ProjectTrackerDbContext db)
{
    public async Task<ProjectNotificationPreferenceDto?> GetAsync(
        int projectId,
        string? accountName,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        var user = await FindUserAsync(accountName, cancellationToken);
        if (project is null || user is null) return null;

        return ToDto(project, user);
    }

    public async Task<ProjectNotificationPreferenceDto?> SetAsync(
        int projectId,
        string? accountName,
        bool enabled,
        string actorAccountName,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        var user = await FindUserAsync(accountName, cancellationToken, track: true);
        if (project is null || user is null) return null;

        var preference = await db.ProjectNotificationPreferences
            .SingleOrDefaultAsync(candidate =>
                candidate.ProjectId == projectId
                && candidate.AppUserId == user.Id,
                cancellationToken);
        if (preference is null)
        {
            preference = new ProjectNotificationPreference
            {
                ProjectId = projectId,
                AppUserId = user.Id
            };
            db.ProjectNotificationPreferences.Add(preference);
        }

        preference.Enabled = enabled;
        preference.UpdatedAt = DateTimeOffset.UtcNow;
        preference.UpdatedByAccountName = actorAccountName;
        await db.SaveChangesAsync(cancellationToken);

        if (!enabled)
        {
            await ResolveOpenPromptsAsync(
                projectId,
                [user.Id],
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        return ToDto(project, user, preference);
    }

    public async Task<IReadOnlyList<AppUser>> LoadRecipientsAsync(
        Project project,
        CancellationToken cancellationToken = default)
    {
        var users = await EntitledUsers()
            .Include(user => user.ProjectNotificationPreferences.Where(preference => preference.ProjectId == project.Id))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return users.Where(user => IsEnabled(project, user)).ToList();
    }

    public async Task<int> ReconcileOpenPromptsAsync(
        Project project,
        CancellationToken cancellationToken = default)
    {
        var allowedIds = (await LoadRecipientsAsync(project, cancellationToken))
            .Select(user => user.Id)
            .ToHashSet();
        var openRecipientIds = await db.UserNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(notification =>
                notification.ProjectId == project.Id
                && notification.RespondedAt == null
                && (notification.Kind == NotificationKind.OperationStartConfirmation
                    || notification.Kind == NotificationKind.OperationFinishConfirmation))
            .Select(notification => notification.RecipientUserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var disabledRecipientIds = openRecipientIds.Where(id => !allowedIds.Contains(id)).ToArray();
        return disabledRecipientIds.Length == 0
            ? 0
            : await ResolveOpenPromptsAsync(
                project.Id,
                disabledRecipientIds,
                DateTimeOffset.UtcNow,
                cancellationToken);
    }

    public IQueryable<AppUser> EntitledUsers() => db.Users.Where(user =>
        user.IsActive
        && user.GroupMemberships.Any(membership => membership.Group.Permissions.Any(permission =>
            permission.PermissionKey == ApplicationPermissions.ModuleView))
        && user.GroupMemberships.Any(membership => membership.Group.Permissions.Any(permission =>
            permission.PermissionKey == ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm)));

    public static bool IsEnabled(Project project, AppUser user)
    {
        var preference = user.ProjectNotificationPreferences
            .SingleOrDefault(candidate => candidate.ProjectId == project.Id);
        return preference?.Enabled ?? AssignedRoles(project, user).Count > 0;
    }

    public static IReadOnlyList<string> AssignedRoles(Project project, AppUser user)
    {
        var roles = new List<string>(3);
        if (Matches(project.ProgramManager, user)) roles.Add("Contact Lead");
        if (Matches(project.Engineer, user)) roles.Add("Engineer");
        if (Matches(project.SalesPerson, user)) roles.Add("Sales Person");
        return roles;
    }

    private async Task<AppUser?> FindUserAsync(
        string? accountName,
        CancellationToken cancellationToken,
        bool track = false)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        if (lookupKeys.Count == 0) return null;
        var query = track ? db.Users.AsQueryable() : db.Users.AsNoTracking();
        return await query
            .Include(user => user.ProjectNotificationPreferences)
            .SingleOrDefaultAsync(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()), cancellationToken);
    }

    private static ProjectNotificationPreferenceDto ToDto(
        Project project,
        AppUser user,
        ProjectNotificationPreference? preference = null)
    {
        preference ??= user.ProjectNotificationPreferences.SingleOrDefault(candidate => candidate.ProjectId == project.Id);
        var assignedRoles = AssignedRoles(project, user);
        return new ProjectNotificationPreferenceDto(
            project.Id,
            preference?.Enabled ?? assignedRoles.Count > 0,
            preference is null,
            assignedRoles);
    }

    private static bool Matches(string? assignment, AppUser user)
    {
        var value = assignment?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (WindowsAccountNames.Equals(value, user.AccountName)) return true;

        value = value.TrimStart('@');
        return string.Equals(value, user.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, WindowsAccountNames.DisplayName(user.AccountName), StringComparison.OrdinalIgnoreCase);
    }

    private Task<int> ResolveOpenPromptsAsync(
        int projectId,
        IReadOnlyCollection<int> recipientIds,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken) =>
        db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification =>
                notification.ProjectId == projectId
                && recipientIds.Contains(notification.RecipientUserId)
                && notification.RespondedAt == null
                && (notification.Kind == NotificationKind.OperationStartConfirmation
                    || notification.Kind == NotificationKind.OperationFinishConfirmation))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(notification => notification.ReadAt, notification => notification.ReadAt ?? resolvedAt)
                .SetProperty(notification => notification.RespondedAt, resolvedAt),
                cancellationToken);
}

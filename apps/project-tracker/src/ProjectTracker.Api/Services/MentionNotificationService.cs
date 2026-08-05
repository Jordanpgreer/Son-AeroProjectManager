using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed partial class MentionNotificationService
{
    public async Task AddForProjectMessageAsync(
        ProjectTrackerDbContext db,
        ProjectMessage message,
        string projectName,
        string actorAccountName,
        string actorDisplayName,
        CancellationToken cancellationToken = default)
    {
        var recipients = await FindRecipientsAsync(db, message.Body, actorAccountName, null, cancellationToken);
        foreach (var recipient in recipients)
        {
            db.UserNotifications.Add(new UserNotification
            {
                RecipientUserId = recipient.Id,
                ProjectId = message.ProjectId,
                ProjectMessage = message,
                Kind = NotificationKind.ProjectChatMention,
                ActorAccountName = actorAccountName,
                ActorDisplayName = actorDisplayName,
                Title = $"{actorDisplayName} mentioned you in {projectName}",
                BodyPreview = Preview(message.Body)
            });
        }
    }

    public async Task AddForOperationNoteAsync(
        ProjectTrackerDbContext db,
        ProjectTask task,
        string projectName,
        string note,
        string? previousNote,
        string actorAccountName,
        string actorDisplayName,
        CancellationToken cancellationToken = default)
    {
        var existingHandles = ExtractHandles(previousNote);
        var recipients = await FindRecipientsAsync(db, note, actorAccountName, existingHandles, cancellationToken);
        foreach (var recipient in recipients)
        {
            db.UserNotifications.Add(new UserNotification
            {
                RecipientUserId = recipient.Id,
                ProjectId = task.ProjectId,
                ProjectTask = task,
                Kind = NotificationKind.OperationNoteMention,
                ActorAccountName = actorAccountName,
                ActorDisplayName = actorDisplayName,
                Title = $"{actorDisplayName} mentioned you in {projectName}",
                BodyPreview = $"{task.Title}: {Preview(note)}"
            });
        }
    }

    public static string MentionHandle(string accountName)
    {
        var handle = WindowsAccountNames.DisplayName(accountName);
        return new string(handle
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '.')
            .ToArray());
    }

    private static async Task<IReadOnlyList<AppUser>> FindRecipientsAsync(
        ProjectTrackerDbContext db,
        string content,
        string actorAccountName,
        IReadOnlySet<string>? excludedHandles,
        CancellationToken cancellationToken)
    {
        var handles = ExtractHandles(content);
        if (excludedHandles is not null)
        {
            handles.ExceptWith(excludedHandles);
        }
        if (handles.Count == 0)
        {
            return [];
        }

        var users = await db.Users
            .Where(user =>
                user.IsActive
                && user.GroupMemberships.Any(membership =>
                    membership.Group.Permissions.Any(permission =>
                        permission.PermissionKey == ApplicationPermissions.ModuleView)))
            .ToListAsync(cancellationToken);

        return users
            .Where(user =>
                !WindowsAccountNames.Equals(user.AccountName, actorAccountName)
                && handles.Contains(MentionHandle(user.AccountName)))
            .DistinctBy(user => user.Id)
            .ToList();
    }

    private static HashSet<string> ExtractHandles(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return MentionRegex()
            .Matches(content)
            .Select(match => match.Groups["handle"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Preview(string content)
    {
        var compact = WhitespaceRegex().Replace(content.Trim(), " ");
        return compact.Length <= 300 ? compact : $"{compact[..297]}...";
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9._-])@(?<handle>[A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

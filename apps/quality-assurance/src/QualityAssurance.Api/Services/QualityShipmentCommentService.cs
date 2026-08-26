using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed partial class QualityShipmentCommentService(
    QualityAssuranceDbContext db,
    IQualityAssuranceAccessStore accessStore)
{
    public async Task<IReadOnlyList<QualityShipmentCommentDto>?> ListAsync(
        int shipmentId,
        long? afterId,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(access, QualityAssurancePermissions.ShipmentsView, "view the shipping queue");
        EnsurePermission(access, QualityAssurancePermissions.CommentsView, "view shipment comments");
        var shipment = await FindShipmentAsync(shipmentId, access, cancellationToken);
        if (shipment is null) return null;

        var query = db.ShipmentComments
            .AsNoTracking()
            .Where(comment => comment.ShipmentId == shipmentId);
        if (afterId.HasValue && afterId.Value > 0)
        {
            return await query
                .Where(comment => comment.Id > afterId.Value)
                .OrderBy(comment => comment.Id)
                .Select(ToDtoExpression())
                .ToListAsync(cancellationToken);
        }

        var recent = await query
            .OrderByDescending(comment => comment.Id)
            .Take(200)
            .Select(ToDtoExpression())
            .ToListAsync(cancellationToken);
        recent.Reverse();
        return recent;
    }

    public async Task<QualityShipmentCommentDto?> PostAsync(
        int shipmentId,
        QualityShipmentCommentCreateDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(access, QualityAssurancePermissions.ShipmentsView, "view the shipping queue");
        EnsurePermission(access, QualityAssurancePermissions.CommentsView, "view shipment comments");
        EnsurePermission(access, QualityAssurancePermissions.CommentsEdit, "add shipment comments");
        var body = NormalizeBody(dto.Body);
        var shipment = await FindShipmentAsync(shipmentId, access, cancellationToken);
        if (shipment is null) return null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var comment = new QualityShipmentComment
        {
            ShipmentId = shipment.Id,
            Body = body,
            AuthorUserId = access.UserId,
            AuthorAccountName = access.AccountName,
            AuthorDisplayName = access.DisplayName,
            CreatedAt = now
        };
        db.ShipmentComments.Add(comment);

        // Keep the legacy field as the current-message preview so existing exports,
        // search, and older clients remain compatible with threaded comments.
        shipment.Comments = body;
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        await db.SaveChangesAsync(cancellationToken);

        var mentionHandles = ExtractHandles(body);
        if (mentionHandles.Count > 0)
        {
            var users = await accessStore.GetUsersWithPermissionAsync(
                QualityAssurancePermissions.CommentsView,
                cancellationToken);
            var handles = MentionHandles(users);
            var candidates = users
                .Where(user => !WindowsAccountNames.Equals(user.AccountName, access.AccountName)
                    && mentionHandles.Contains(handles[user.Id]))
                .DistinctBy(user => user.Id);
            foreach (var recipient in candidates)
            {
                var recipientAccess = await accessStore.FindAccessAsync(recipient.AccountName, cancellationToken);
                if (recipientAccess is null
                    || !recipientAccess.HasPermission(QualityAssurancePermissions.CommentsView)
                    || !HasRecordAccess(shipment, recipientAccess)) continue;
                db.MentionNotifications.Add(new QualityMentionNotification
                {
                    RecipientUserId = recipient.Id,
                    RecipientAccountName = recipient.AccountName,
                    ShipmentId = shipment.Id,
                    CommentId = comment.Id,
                    ActorAccountName = access.AccountName,
                    ActorDisplayName = access.DisplayName,
                    BodyPreview = Preview(body),
                    CreatedAt = now
                });
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ToDto(comment);
    }

    public async Task<IReadOnlyList<QualityMentionableUserDto>?> MentionableUsersAsync(
        int shipmentId,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(access, QualityAssurancePermissions.ShipmentsView, "view the shipping queue");
        EnsurePermission(access, QualityAssurancePermissions.CommentsView, "view shipment comments");
        var shipment = await FindShipmentAsync(shipmentId, access, cancellationToken);
        if (shipment is null) return null;
        var users = await accessStore.GetUsersWithPermissionAsync(
            QualityAssurancePermissions.CommentsView,
            cancellationToken);
        var handles = MentionHandles(users);
        var mentionable = new List<QualityMentionableUserDto>();
        foreach (var user in users)
        {
            if (WindowsAccountNames.Equals(user.AccountName, access.AccountName)) continue;
            var candidateAccess = await accessStore.FindAccessAsync(user.AccountName, cancellationToken);
            if (candidateAccess is null || !HasRecordAccess(shipment, candidateAccess)) continue;
            mentionable.Add(new QualityMentionableUserDto(
                user.Id,
                user.AccountName,
                user.DisplayName,
                handles[user.Id]));
        }
        return mentionable;
    }

    public async Task<IReadOnlyList<QualityMentionNotificationDto>> NotificationsAsync(
        bool unreadOnly,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(access, QualityAssurancePermissions.ShipmentsView, "view the shipping queue");
        EnsurePermission(access, QualityAssurancePermissions.CommentsView, "view shipment comments");
        var query = VisibleNotifications(db.MentionNotifications.AsNoTracking(), access)
            .Where(notification => notification.RecipientUserId == access.UserId);
        if (unreadOnly) query = query.Where(notification => notification.ReadAt == null);
        return await query
            // IDs are assigned in creation order and remain sortable on both
            // SQLite and SQL Server; SQLite cannot order DateTimeOffset values.
            .OrderByDescending(notification => notification.Id)
            .Take(50)
            .Select(notification => new QualityMentionNotificationDto(
                notification.Id,
                notification.ShipmentId,
                notification.CommentId,
                db.Shipments.Any(shipment => shipment.Id == notification.ShipmentId && shipment.IsShipped),
                notification.ActorAccountName,
                notification.ActorDisplayName,
                notification.BodyPreview,
                notification.CreatedAt,
                notification.ReadAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> MarkNotificationReadAsync(
        long notificationId,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(access, QualityAssurancePermissions.ShipmentsView, "view the shipping queue");
        EnsurePermission(access, QualityAssurancePermissions.CommentsView, "view shipment comments");
        var notification = await VisibleNotifications(db.MentionNotifications, access)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == notificationId && candidate.RecipientUserId == access.UserId,
                cancellationToken);
        if (notification is null) return false;
        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task MarkAllNotificationsReadAsync(
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(access, QualityAssurancePermissions.ShipmentsView, "view the shipping queue");
        EnsurePermission(access, QualityAssurancePermissions.CommentsView, "view shipment comments");
        var unread = await VisibleNotifications(db.MentionNotifications, access)
            .Where(notification => notification.RecipientUserId == access.UserId && notification.ReadAt == null)
            .ToListAsync(cancellationToken);
        if (unread.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var notification in unread) notification.ReadAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string MentionHandle(string accountName)
    {
        var source = WindowsAccountNames.DisplayName(accountName);
        var handle = new System.Text.StringBuilder(source.Length);
        foreach (var character in source)
        {
            var asciiLetterOrDigit = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9';
            if (asciiLetterOrDigit)
            {
                handle.Append(char.ToLowerInvariant(character));
                continue;
            }
            if (handle.Length > 0 && character is '.' or '_' or '-') handle.Append(character);
            else if (handle.Length > 0 && handle[^1] != '.') handle.Append('.');
        }
        while (handle.Length > 0 && handle[^1] is '.' or '_' or '-') handle.Length--;
        return handle.Length == 0 ? "user" : handle.ToString();
    }

    private static IReadOnlyDictionary<int, string> MentionHandles(IReadOnlyList<QualityDirectoryUser> users)
    {
        var assigned = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users.OrderBy(user => user.Id))
        {
            var baseHandle = MentionHandle(user.AccountName);
            var candidate = baseHandle;
            var attempt = 0;
            while (!used.Add(candidate))
            {
                attempt++;
                candidate = attempt == 1 ? $"{baseHandle}-{user.Id}" : $"{baseHandle}-{user.Id}-{attempt}";
            }
            assigned[user.Id] = candidate;
        }
        return assigned;
    }

    private async Task<QualityShipment?> FindShipmentAsync(
        int shipmentId,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.SingleOrDefaultAsync(
            candidate => candidate.Id == shipmentId,
            cancellationToken);
        if (shipment is not null) EnsureRecordAccess(shipment, access);
        return shipment;
    }

    private static void EnsurePermission(
        QualityAssuranceAccessProfile access,
        string permission,
        string action)
    {
        if (!access.HasPermission(permission))
            throw new UnauthorizedAccessException($"You do not have permission to {action}.");
    }

    private static void EnsureRecordAccess(
        QualityShipment shipment,
        QualityAssuranceAccessProfile access)
    {
        if (HasRecordAccess(shipment, access)) return;
        throw new UnauthorizedAccessException("This shipment is not in your permitted queue.");
    }

    private static bool HasRecordAccess(
        QualityShipment shipment,
        QualityAssuranceAccessProfile access)
    {
        if (!access.HasPermission(QualityAssurancePermissions.ShipmentsView)) return false;
        if (access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return true;
        if (shipment.AssignedUserId == access.UserId) return true;
        if (!shipment.AssignedGroupId.HasValue
            && !shipment.AssignedUserId.HasValue
            && access.HasPermission(QualityAssurancePermissions.AssignmentGroup)) return true;
        if (access.HasPermission(QualityAssurancePermissions.TeamDashboardView)
            && shipment.AssignedGroupId.HasValue
            && access.Groups.Any(group => group.Id == shipment.AssignedGroupId.Value)) return true;
        return false;
    }

    private IQueryable<QualityMentionNotification> VisibleNotifications(
        IQueryable<QualityMentionNotification> query,
        QualityAssuranceAccessProfile access)
    {
        if (access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return query;
        var groupIds = access.Groups.Select(group => group.Id).ToList();
        var canReviewUnassigned = access.HasPermission(QualityAssurancePermissions.AssignmentGroup);
        var canViewTeam = access.HasPermission(QualityAssurancePermissions.TeamDashboardView);
        return query.Where(notification => db.Shipments.Any(shipment =>
            shipment.Id == notification.ShipmentId
            && (shipment.AssignedUserId == access.UserId
                || (canReviewUnassigned
                    && !shipment.AssignedGroupId.HasValue
                    && !shipment.AssignedUserId.HasValue)
                || (canViewTeam
                    && shipment.AssignedGroupId.HasValue
                    && groupIds.Contains(shipment.AssignedGroupId.Value)))));
    }

    private static string NormalizeBody(string? body)
    {
        var value = body?.Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Write a comment before sending.", nameof(body));
        if (value.Length > 2000) throw new ArgumentException("Shipment comments cannot exceed 2,000 characters.", nameof(body));
        return value;
    }

    private static HashSet<string> ExtractHandles(string content) =>
        MentionRegex()
            .Matches(content)
            .Select(match => match.Groups["handle"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Preview(string content)
    {
        var compact = WhitespaceRegex().Replace(content.Trim(), " ");
        return compact.Length <= 300 ? compact : $"{compact[..297]}...";
    }

    private static System.Linq.Expressions.Expression<Func<QualityShipmentComment, QualityShipmentCommentDto>> ToDtoExpression() =>
        comment => new QualityShipmentCommentDto(
            comment.Id,
            comment.ShipmentId,
            comment.Body,
            comment.AuthorUserId,
            comment.AuthorAccountName,
            comment.AuthorDisplayName,
            comment.CreatedAt,
            comment.IsLegacyImport);

    private static QualityShipmentCommentDto ToDto(QualityShipmentComment comment) => new(
        comment.Id,
        comment.ShipmentId,
        comment.Body,
        comment.AuthorUserId,
        comment.AuthorAccountName,
        comment.AuthorDisplayName,
        comment.CreatedAt,
        comment.IsLegacyImport);

    [GeneratedRegex(@"(?<![A-Za-z0-9._-])@(?<handle>[A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

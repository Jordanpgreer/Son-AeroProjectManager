using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityLegacyAssignmentReconciler(
    QualityAssuranceDbContext db,
    IQualityAssuranceAccessStore accessStore,
    ILogger<QualityLegacyAssignmentReconciler> logger)
{
    private const string SystemAccount = "ARDA\\QualityAssignmentReconciler";
    private const string SystemDisplayName = "Arda Quality assignment reconciliation";

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var groups = await accessStore.GetGroupsWithPermissionAsync(
            QualityAssurancePermissions.ResponsibleGroupEligible,
            cancellationToken);
        var users = await accessStore.GetUsersWithPermissionAsync(
            QualityAssurancePermissions.AssignmentEligible,
            cancellationToken);
        var candidates = await db.Shipments
            .Include(shipment => shipment.AuditEntries)
            .Where(shipment => shipment.LegacyAssigneeTag != null
                || (shipment.NextAction != null && shipment.NextAction.ToUpper().Contains("QA")))
            .ToListAsync(cancellationToken);

        var changed = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var shipment in candidates)
        {
            var tag = QualityLegacyAssignmentIdentity.NormalizeStoredTag(shipment.LegacyAssigneeTag)
                ?? QualityLegacyAssignmentIdentity.TryNormalizePrefixedTag(shipment.NextAction);
            if (tag is null || HasManualAssignment(shipment)) continue;

            var owner = QualityLegacyAssignmentIdentity.ResolveOwnerByFirstName(tag, users, groups);
            if (!Apply(shipment, tag, owner, now)) continue;
            changed++;
        }

        if (changed == 0) return 0;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Reconciled {Count} legacy Quality assignment tag(s).", changed);
            return changed;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogInformation(exception, "Legacy Quality assignments changed concurrently; reconciliation will retry on the next request.");
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    private static bool Apply(
        QualityShipment shipment,
        string tag,
        ResolvedQualityOwner? owner,
        DateTimeOffset now)
    {
        var oldAction = shipment.NextAction;
        var oldAssignment = AssignmentLabel(shipment);
        var nextAction = owner?.User.DisplayName ?? tag;
        var nextTag = owner is null ? tag : null;
        if (shipment.AssignedGroupId == owner?.Group.Id
            && shipment.AssignedUserId == owner?.User.Id
            && string.Equals(shipment.NextAction, nextAction, StringComparison.Ordinal)
            && string.Equals(shipment.LegacyAssigneeTag, nextTag, StringComparison.Ordinal)) return false;

        shipment.AssignedGroupId = owner?.Group.Id;
        shipment.AssignedGroupName = owner?.Group.Name;
        shipment.AssignedUserId = owner?.User.Id;
        shipment.AssignedAccountName = owner?.User.AccountName;
        shipment.AssignedDisplayName = owner?.User.DisplayName;
        shipment.NextAction = nextAction;
        shipment.LegacyAssigneeTag = nextTag;
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = SystemAccount;
        shipment.UpdatedByDisplayName = SystemDisplayName;
        shipment.Version++;
        shipment.AuditEntries.Add(new QualityShipmentAuditEntry
        {
            EventType = owner is null ? "LegacyAssignmentNormalized" : "LegacyAssignmentPromoted",
            FieldName = "assignment",
            OldValue = string.IsNullOrWhiteSpace(oldAssignment) ? oldAction : oldAssignment,
            NewValue = owner is null ? tag : AssignmentLabel(shipment),
            AccountName = SystemAccount,
            DisplayName = SystemDisplayName,
            OccurredAt = now
        });
        return true;
    }

    private static bool HasManualAssignment(QualityShipment shipment) =>
        shipment.AuditEntries.Any(entry => entry.EventType == "Assigned");

    private static string AssignmentLabel(QualityShipment shipment) =>
        string.Join(" / ", new[] { shipment.AssignedGroupName, shipment.AssignedDisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}

internal sealed record ResolvedQualityOwner(QualityDirectoryUser User, QualityDirectoryGroup Group);

internal static class QualityLegacyAssignmentIdentity
{
    private static readonly Regex PrefixedTag = new(
        @"^\s*QA\s*[-_:]\s*(?<tag>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? TryNormalizePrefixedTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = PrefixedTag.Match(value);
        return match.Success ? NormalizeStoredTag(match.Groups["tag"].Value) : null;
    }

    public static string? NormalizeStoredTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    public static ResolvedQualityOwner? ResolveOwnerByFirstName(
        string tag,
        IReadOnlyList<QualityDirectoryUser> users,
        IReadOnlyList<QualityDirectoryGroup> groups)
    {
        var firstName = FirstToken(tag);
        if (firstName is null) return null;
        var matches = users.Where(user => string.Equals(
                FirstToken(user.DisplayName),
                firstName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1) return null;

        var user = matches[0];
        var group = groups
            .Where(candidate => user.GroupIds.Contains(candidate.Id))
            .OrderByDescending(candidate => candidate.Name.Contains("Quality", StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
        return group is null ? null : new ResolvedQualityOwner(user, group);
    }

    private static string? FirstToken(string value) => Regex.Match(value, @"[\p{L}\p{N}']+") is { Success: true } match
        ? match.Value
        : null;
}

using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityAssignmentService(
    QualityAssuranceDbContext qualityDb,
    IQualityAssuranceAccessStore accessStore)
{
    public async Task<QualityAssignmentRule?> ApplyFirstMatchingRuleAsync(
        QualityShipment shipment,
        CancellationToken cancellationToken)
    {
        var rules = await qualityDb.AssignmentRules
            .AsNoTracking()
            .Where(rule => rule.IsEnabled)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);
        var eligibleGroups = await accessStore.GetGroupsWithPermissionAsync(
            QualityAssurancePermissions.ResponsibleGroupEligible,
            cancellationToken);
        var eligibleGroupIds = eligibleGroups.Select(group => group.Id).ToHashSet();
        var rule = rules.FirstOrDefault(candidate =>
            eligibleGroupIds.Contains(candidate.TargetGroupId) && Matches(candidate, shipment));
        if (rule is null) return null;

        shipment.AssignedGroupId = rule.TargetGroupId;
        shipment.AssignedGroupName = rule.TargetGroupName;
        shipment.AssignedUserId = null;
        shipment.AssignedAccountName = null;
        shipment.AssignedDisplayName = null;

        var users = (await accessStore.GetUsersWithPermissionAsync(
                QualityAssurancePermissions.AssignmentEligible,
                cancellationToken))
            .Where(user => user.GroupIds.Contains(rule.TargetGroupId))
            .ToList();
        QualityDirectoryUser? selected = null;
        if (string.Equals(rule.AssignmentMode, "SpecificUser", StringComparison.OrdinalIgnoreCase))
        {
            selected = users.SingleOrDefault(user => user.Id == rule.TargetUserId);
        }
        else if (string.Equals(rule.AssignmentMode, "LeastLoaded", StringComparison.OrdinalIgnoreCase)
            && users.Count > 0)
        {
            var userIds = users.Select(user => user.Id).ToList();
            var queueCounts = await qualityDb.Shipments
                .AsNoTracking()
                .Where(candidate => !candidate.IsShipped
                    && candidate.AssignedUserId.HasValue
                    && userIds.Contains(candidate.AssignedUserId.Value))
                .GroupBy(candidate => candidate.AssignedUserId!.Value)
                .Select(group => new { UserId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);
            selected = users
                .OrderBy(user => queueCounts.GetValueOrDefault(user.Id))
                .ThenBy(user => user.DisplayName)
                .First();
        }

        if (selected is not null)
        {
            shipment.AssignedUserId = selected.Id;
            shipment.AssignedAccountName = selected.AccountName;
            shipment.AssignedDisplayName = selected.DisplayName;
        }
        return rule;
    }

    public async Task<QualityAssignmentRuleDto> CreateRuleAsync(
        QualityAssignmentRuleUpsertDto dto,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var rule = new QualityAssignmentRule
        {
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = actor.AccountName
        };
        await ApplyRuleUpdateAsync(rule, dto, actor, cancellationToken);
        qualityDb.AssignmentRules.Add(rule);
        await qualityDb.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task<QualityAssignmentRuleDto?> UpdateRuleAsync(
        int id,
        QualityAssignmentRuleUpsertDto dto,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var rule = await qualityDb.AssignmentRules.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (rule is null) return null;
        if (dto.Version is null || dto.Version.Value != rule.Version)
            throw new DbUpdateConcurrencyException("The assignment rule changed. Reload before saving.");
        qualityDb.Entry(rule).Property(candidate => candidate.Version).OriginalValue = dto.Version.Value;
        await ApplyRuleUpdateAsync(rule, dto, actor, cancellationToken);
        await qualityDb.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task<bool> DeleteRuleAsync(int id, long version, CancellationToken cancellationToken)
    {
        var rule = await qualityDb.AssignmentRules.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (rule is null) return false;
        if (version != rule.Version)
            throw new DbUpdateConcurrencyException("The assignment rule changed. Reload before deleting.");
        qualityDb.Entry(rule).Property(candidate => candidate.Version).OriginalValue = version;
        qualityDb.AssignmentRules.Remove(rule);
        await qualityDb.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<QualityAssignmentRuleDto>> GetRulesAsync(CancellationToken cancellationToken) =>
        await qualityDb.AssignmentRules
            .AsNoTracking()
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Name)
            .Select(rule => new QualityAssignmentRuleDto(
                rule.Id,
                rule.Name,
                rule.IsEnabled,
                rule.Priority,
                rule.MatchField,
                rule.MatchOperator,
                rule.MatchValue,
                rule.TargetGroupId,
                rule.TargetGroupName,
                rule.AssignmentMode,
                rule.TargetUserId,
                rule.TargetDisplayName,
                rule.Version,
                rule.UpdatedAt,
                rule.UpdatedBy))
            .ToListAsync(cancellationToken);

    private async Task ApplyRuleUpdateAsync(
        QualityAssignmentRule rule,
        QualityAssignmentRuleUpsertDto dto,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var name = dto.Name.Trim();
        var value = dto.MatchValue.Trim();
        if (name.Length is < 2 or > 160) throw new ArgumentException("Rule name must be between 2 and 160 characters.");
        if (value.Length is < 1 or > 240) throw new ArgumentException("A customer or task-type match value is required.");
        if (dto.Priority is < 0 or > 10000) throw new ArgumentException("Priority must be between 0 and 10,000.");
        if (dto.MatchField is not ("Customer" or "TaskType")) throw new ArgumentException("Match field must be Customer or TaskType.");
        if (dto.MatchOperator is not ("Equals" or "Contains" or "StartsWith")) throw new ArgumentException("Unsupported match operator.");
        if (dto.AssignmentMode is not ("GroupOnly" or "SpecificUser" or "LeastLoaded")) throw new ArgumentException("Unsupported assignment mode.");

        var groups = await accessStore.GetGroupsWithPermissionAsync(
            QualityAssurancePermissions.ResponsibleGroupEligible,
            cancellationToken);
        var group = groups.SingleOrDefault(candidate => candidate.Id == dto.TargetGroupId)
            ?? throw new ArgumentException("Select an existing shared access group.");
        QualityDirectoryUser? user = null;
        if (dto.AssignmentMode == "SpecificUser")
        {
            if (!dto.TargetUserId.HasValue) throw new ArgumentException("Select a user for specific-user assignment.");
            user = (await accessStore.GetUsersWithPermissionAsync(
                    QualityAssurancePermissions.AssignmentEligible,
                    cancellationToken))
                .Where(candidate => candidate.GroupIds.Contains(group.Id))
                .SingleOrDefault(candidate => candidate.Id == dto.TargetUserId.Value)
                ?? throw new ArgumentException("The selected user must be active, eligible for Quality assignment, and assigned to the target group.");
        }

        rule.Name = name;
        rule.IsEnabled = dto.IsEnabled;
        rule.Priority = dto.Priority;
        rule.MatchField = dto.MatchField;
        rule.MatchOperator = dto.MatchOperator;
        rule.MatchValue = value;
        rule.TargetGroupId = group.Id;
        rule.TargetGroupName = group.Name;
        rule.AssignmentMode = dto.AssignmentMode;
        rule.TargetUserId = user?.Id;
        rule.TargetAccountName = user?.AccountName;
        rule.TargetDisplayName = user?.DisplayName;
        rule.Version++;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        rule.UpdatedBy = actor.AccountName;
    }

    private static bool Matches(QualityAssignmentRule rule, QualityShipment shipment)
    {
        var source = rule.MatchField == "Customer" ? shipment.Customer : shipment.TaskType;
        return rule.MatchOperator switch
        {
            "Equals" => string.Equals(source.Trim(), rule.MatchValue.Trim(), StringComparison.OrdinalIgnoreCase),
            "Contains" => source.Contains(rule.MatchValue.Trim(), StringComparison.OrdinalIgnoreCase),
            "StartsWith" => source.StartsWith(rule.MatchValue.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static QualityAssignmentRuleDto ToDto(QualityAssignmentRule rule) => new(
        rule.Id,
        rule.Name,
        rule.IsEnabled,
        rule.Priority,
        rule.MatchField,
        rule.MatchOperator,
        rule.MatchValue,
        rule.TargetGroupId,
        rule.TargetGroupName,
        rule.AssignmentMode,
        rule.TargetUserId,
        rule.TargetDisplayName,
        rule.Version,
        rule.UpdatedAt,
        rule.UpdatedBy);
}

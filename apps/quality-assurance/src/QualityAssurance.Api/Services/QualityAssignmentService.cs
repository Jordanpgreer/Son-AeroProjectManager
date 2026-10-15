using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityAssignmentService(
    QualityAssuranceDbContext qualityDb,
    QualityTeamService teams)
{
    public async Task<QualityAssignmentRule?> ApplyFirstMatchingRuleAsync(
        QualityShipment shipment,
        CancellationToken cancellationToken)
    {
        if (await qualityDb.Workflows.AsNoTracking().AnyAsync(
            workflow => workflow.Module == QualityWorkflowGraphEngine.Module && workflow.PublishedJson != null, cancellationToken))
            return null;
        var rules = await qualityDb.AssignmentRules
            .AsNoTracking()
            .Where(rule => rule.IsEnabled && rule.UsesOperationalTeam)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);
        QualityAssignmentRule? rule = null;
        QualityTeam? team = null;
        foreach (var candidate in rules.Where(candidate => Matches(candidate, shipment)))
        {
            team = await teams.FindActiveTeamAsync(candidate.TargetGroupId, cancellationToken);
            if (team is null) continue;
            rule = candidate;
            break;
        }
        if (rule is null) return null;

        shipment.AssignedGroupId = team!.Id;
        shipment.AssignedGroupName = team.Name;
        shipment.AssignedUserId = null;
        shipment.AssignedAccountName = null;
        shipment.AssignedDisplayName = null;
        shipment.IsOperationalTeamAssignment = true;

        var users = await teams.GetActiveMembersAsync(team.Id, cancellationToken);
        QualityTeamMember? selected = null;
        if (string.Equals(rule.AssignmentMode, "SpecificUser", StringComparison.OrdinalIgnoreCase))
        {
            selected = users.SingleOrDefault(user => user.AppUserId == rule.TargetUserId);
        }
        else if (string.Equals(rule.AssignmentMode, "LeastLoaded", StringComparison.OrdinalIgnoreCase)
            && users.Count > 0)
        {
            var userIds = users.Select(user => user.AppUserId).ToList();
            var queueCounts = await qualityDb.Shipments
                .AsNoTracking()
                .Where(candidate => !candidate.IsShipped
                    && candidate.AssignedUserId.HasValue
                    && userIds.Contains(candidate.AssignedUserId.Value))
                .GroupBy(candidate => candidate.AssignedUserId!.Value)
                .Select(group => new { UserId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);
            selected = users
                .OrderBy(user => queueCounts.GetValueOrDefault(user.AppUserId))
                .ThenBy(user => user.DisplayName)
                .First();
        }

        if (selected is not null)
        {
            shipment.AssignedUserId = selected.AppUserId;
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

        var team = await teams.FindActiveTeamAsync(dto.TargetGroupId, cancellationToken)
            ?? throw new ArgumentException("Select an active Quality team.");
        QualityTeamMember? user = null;
        if (dto.AssignmentMode == "SpecificUser")
        {
            if (!dto.TargetUserId.HasValue) throw new ArgumentException("Select a user for specific-user assignment.");
            user = (await teams.GetActiveMembersAsync(team.Id, cancellationToken))
                .SingleOrDefault(candidate => candidate.AppUserId == dto.TargetUserId.Value)
                ?? throw new ArgumentException("The selected user must be active and belong to the destination Quality team.");
        }

        rule.Name = name;
        rule.IsEnabled = dto.IsEnabled;
        rule.Priority = dto.Priority;
        rule.MatchField = dto.MatchField;
        rule.MatchOperator = dto.MatchOperator;
        rule.MatchValue = value;
        rule.TargetGroupId = team.Id;
        rule.TargetGroupName = team.Name;
        rule.AssignmentMode = dto.AssignmentMode;
        rule.TargetUserId = user?.AppUserId;
        rule.TargetAccountName = user?.AccountName;
        rule.TargetDisplayName = user?.DisplayName;
        rule.UsesOperationalTeam = true;
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

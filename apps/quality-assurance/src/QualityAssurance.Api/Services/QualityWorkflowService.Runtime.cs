using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed partial class QualityWorkflowService
{
    public async Task<IReadOnlyList<string>> GetRestrictedActionsAsync(
        QualityAssuranceAccessProfile actor, CancellationToken cancellationToken)
    {
        var published = await db.Workflows.AsNoTracking().Where(item => item.Module == QualityWorkflowGraphEngine.Module)
            .Select(item => item.PublishedJson).SingleOrDefaultAsync(cancellationToken);
        if (published is null) return [];
        var groupIds = actor.Groups.Select(group => group.Id).ToHashSet();
        return ReadGraph(published).Nodes.Where(node => node.Type == "trigger"
                && node.Trigger is not null && node.AllowedGroupIds is { Count: > 0 }
                && !node.AllowedGroupIds.Any(groupIds.Contains))
            .Select(node => node.Trigger!).Distinct().ToList();
    }

    // Called only after the native action's permission, record access, and version checks.
    // Mutations and the execution audit are committed by the enclosing shipment transaction.
    public async Task<bool> ApplyAsync(
        string trigger, QualityShipment shipment, QualityAssuranceAccessProfile actor, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(
            item => item.Module == QualityWorkflowGraphEngine.Module, cancellationToken);
        if (workflow?.PublishedJson is null) return false;
        var graph = ReadGraph(workflow.PublishedJson);
        var outcome = QualityWorkflowGraphEngine.Evaluate(graph, trigger, new QualityWorkflowContext(
            shipment.Customer, shipment.TaskType, shipment.Status, shipment.HoldReason,
            shipment.AssignedGroupId, actor.Groups.Select(group => group.Id).ToList()));
        if (outcome.Outcome == "no-trigger") return false;
        if (outcome.Outcome == "blocked") throw new UnauthorizedAccessException(outcome.Message);
        var oldAssignment = Assignment(shipment);
        if (outcome.Outcome == "route")
        {
            var groups = await accessStore.GetGroupsWithPermissionAsync(QualityAssurancePermissions.ResponsibleGroupEligible, cancellationToken);
            var group = groups.SingleOrDefault(group => group.Id == outcome.TargetGroupId)
                ?? throw new ArgumentException("The workflow destination is no longer an eligible Quality Responsible Group. Ask a workflow administrator to update it.");
            QualityDirectoryUser? selected = null;
            if (outcome.AssignmentMode is "SpecificUser" or "LeastLoaded")
            {
                var users = (await accessStore.GetUsersWithPermissionAsync(QualityAssurancePermissions.AssignmentEligible, cancellationToken))
                    .Where(user => user.GroupIds.Contains(group.Id)).ToList();
                if (outcome.AssignmentMode == "SpecificUser")
                    selected = users.SingleOrDefault(user => user.Id == outcome.TargetUserId)
                        ?? throw new ArgumentException("The workflow recipient is no longer eligible for this queue. Ask a workflow administrator to update it.");
                else if (users.Count > 0)
                {
                    var ids = users.Select(user => user.Id).ToList();
                    var loads = await db.Shipments.AsNoTracking()
                        .Where(item => !item.IsShipped && item.Id != shipment.Id
                            && item.AssignedUserId.HasValue && ids.Contains(item.AssignedUserId.Value))
                        .GroupBy(item => item.AssignedUserId!.Value)
                        .Select(group => new { UserId = group.Key, Count = group.Count() })
                        .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);
                    // Earlier rows in a workbook share this unit of work and are not persisted yet.
                    foreach (var pending in db.ChangeTracker.Entries<QualityShipment>()
                        .Where(entry => entry.State == EntityState.Added && !ReferenceEquals(entry.Entity, shipment))
                        .Select(entry => entry.Entity)
                        .Where(item => !item.IsShipped && item.AssignedUserId.HasValue && ids.Contains(item.AssignedUserId.Value)))
                    {
                        var userId = pending.AssignedUserId!.Value;
                        loads[userId] = loads.GetValueOrDefault(userId) + 1;
                    }
                    selected = users.OrderBy(user => loads.GetValueOrDefault(user.Id)).ThenBy(user => user.Id).First();
                }
            }
            shipment.AssignedGroupId = group.Id;
            shipment.AssignedGroupName = group.Name;
            shipment.AssignedUserId = selected?.Id;
            shipment.AssignedAccountName = selected?.AccountName;
            shipment.AssignedDisplayName = selected?.DisplayName;
            shipment.LegacyAssigneeTag = null;
            shipment.NextAction = selected?.DisplayName ?? group.Name;
        }
        shipment.AuditEntries.Add(new QualityShipmentAuditEntry
        {
            EventType = "WorkflowExecuted", FieldName = "workflow", OldValue = oldAssignment,
            NewValue = JsonSerializer.Serialize(new
            {
                workflow.PublishedRevision, Trigger = trigger, outcome.Path, outcome.Outcome,
                Assignment = Assignment(shipment)
            }, JsonOptions),
            AccountName = actor.AccountName, DisplayName = actor.DisplayName, OccurredAt = DateTimeOffset.UtcNow
        });
        return true;
    }

    private static string Assignment(QualityShipment shipment) =>
        $"{shipment.AssignedGroupName ?? "Unassigned"} / {shipment.AssignedDisplayName ?? "Group queue"}";
}

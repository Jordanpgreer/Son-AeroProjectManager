using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Dtos;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed partial class QualityWorkflowService
{
    private async Task<QualityWorkflowGraph> CreateInitialGraphAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<QualityWorkflowNode>();
        var edges = new List<QualityWorkflowEdge>();
        var groups = await accessStore.GetGroupsWithPermissionAsync(QualityAssurancePermissions.ResponsibleGroupEligible, cancellationToken);
        var rules = await db.AssignmentRules.AsNoTracking().Where(rule => rule.IsEnabled)
            .OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id).ToListAsync(cancellationToken);
        // Preserve the ordered creation rules in an editable branch chain.
        nodes.Add(new("created", "trigger", "Shipment created", 80, 80, Trigger: "shipment-created"));
        nodes.Add(new("updated", "trigger", "Shipment updated", 80, 260, Trigger: "shipment-updated"));
        nodes.Add(new("keep", "end", "Keep current assignment", 760, 140 + rules.Count * 230));
        var first = rules.Count > 0 ? $"rule-{rules[0].Id}" : "keep";
        edges.Add(new("created-next", "created", first));
        // Legacy update rules ran only on unassigned records; creation rules remain editable in draft.
        edges.Add(new("updated-next", "updated", "keep"));
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var id = $"rule-{rule.Id}";
            nodes.Add(new(id, "condition", rule.Name, 400, 80 + index * 230,
                Field: rule.MatchField == "Customer" ? "customer" : "taskType", Operator: rule.MatchOperator, Value: rule.MatchValue));
            nodes.Add(new($"route-{rule.Id}", "route", $"Send to {rule.TargetGroupName}", 760, 80 + index * 230,
                TargetGroupId: rule.TargetGroupId, AssignmentMode: rule.AssignmentMode, TargetUserId: rule.TargetUserId));
            edges.Add(new($"{id}-yes", id, $"route-{rule.Id}", "yes"));
            edges.Add(new($"{id}-no", id, index + 1 < rules.Count ? $"rule-{rules[index + 1].Id}" : "keep", "no"));
        }
        var configuredName = configuration?["QualityWorkflow:ShippingGroupName"]?.Trim();
        var shippingName = string.IsNullOrWhiteSpace(configuredName) || string.Equals(configuredName, "Shipping", StringComparison.OrdinalIgnoreCase)
            ? ApplicationGroups.Shipper : configuredName;
        var shipping = groups.FirstOrDefault(group => string.Equals(group.Name, shippingName, StringComparison.OrdinalIgnoreCase));
        var actionY = 520 + rules.Count * 230;
        if (shipping is not null)
        {
            nodes.Add(new("qa-completed", "trigger", "QA Complete pressed", 80, actionY, Trigger: "qa-completed"));
            nodes.Add(new("shipping-queue", "route", $"Send to {shipping.Name}", 420, actionY,
                TargetGroupId: shipping.Id, AssignmentMode: "GroupOnly"));
            edges.Add(new("qa-route", "qa-completed", "shipping-queue"));
        }
        return new(QualityWorkflowGraphEngine.Module, "Quality Assurance workflow", nodes, edges);
    }
}

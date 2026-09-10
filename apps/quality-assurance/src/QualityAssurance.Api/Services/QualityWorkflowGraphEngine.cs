using QualityAssurance.Api.Dtos;

namespace QualityAssurance.Api.Services;

public static class QualityWorkflowGraphEngine
{
    public const string Module = "quality-assurance";
    public static readonly string[] Triggers =
    ["shipment-created", "shipment-updated", "assignment-changed", "qa-completed", "shipment-shipped", "shipment-imported"];

    public static void EnsureBounded(QualityWorkflowGraph graph)
    {
        if (graph is null || graph.Module != Module) throw new ArgumentException("Select the Quality Assurance workflow.");
        if (string.IsNullOrWhiteSpace(graph.Name) || graph.Name.Trim().Length > 160)
            throw new ArgumentException("Workflow name is required and must be at most 160 characters.");
        if (graph.Nodes is null || graph.Edges is null || graph.Nodes.Count > 100 || graph.Edges.Count > 200)
            throw new ArgumentException("A workflow can contain at most 100 steps and 200 connections.");
        foreach (var node in graph.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 80
                || string.IsNullOrWhiteSpace(node.Label) || node.Label.Length > 160
                || node.Type is not ("trigger" or "condition" or "route" or "end")
                || !double.IsFinite(node.X) || !double.IsFinite(node.Y)
                || Math.Abs(node.X) > 100000 || Math.Abs(node.Y) > 100000
                || node.Value?.Length > 240 || node.AllowedGroupIds?.Count > 100
                || node.Trigger?.Length > 80 || node.Field?.Length > 40 || node.Operator?.Length > 40
                || node.AssignmentMode?.Length > 40)
                throw new ArgumentException("A workflow step has invalid or oversized data.");
        }
        if (graph.Edges.Any(edge => edge is null || string.IsNullOrWhiteSpace(edge.Id) || edge.Id.Length > 80
            || string.IsNullOrWhiteSpace(edge.Source) || edge.Source.Length > 80
            || string.IsNullOrWhiteSpace(edge.Target) || edge.Target.Length > 80
            || edge.Branch is not (null or "yes" or "no")))
            throw new ArgumentException("A workflow connection has invalid data.");
    }

    public static QualityWorkflowValidation Validate(QualityWorkflowGraph graph)
    {
        EnsureBounded(graph);
        var issues = new List<QualityWorkflowIssue>();
        void Issue(string? id, string message) => issues.Add(new(id, message));
        if (graph.Nodes.Count == 0) Issue(null, "Add at least one action trigger.");
        if (graph.Nodes.Select(node => node.Id).Distinct().Count() != graph.Nodes.Count)
            Issue(null, "Every step must have a unique ID.");
        if (graph.Edges.Select(edge => edge.Id).Distinct().Count() != graph.Edges.Count)
            Issue(null, "Every connection must have a unique ID.");
        var nodes = graph.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var edge in graph.Edges)
            if (!nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target))
                Issue(null, "A connection points to a missing step.");
        var outgoing = graph.Edges.ToLookup(edge => edge.Source);
        var incoming = graph.Edges.ToLookup(edge => edge.Target);
        var triggerNodes = graph.Nodes.Where(node => node.Type == "trigger").ToList();
        if (triggerNodes.Count == 0) Issue(null, "Add at least one action trigger.");
        foreach (var duplicate in triggerNodes.GroupBy(node => node.Trigger).Where(group => group.Count() > 1))
            foreach (var node in duplicate) Issue(node.Id, "Each action can have only one trigger.");
        foreach (var node in graph.Nodes)
        {
            var edges = outgoing[node.Id].ToList();
            switch (node.Type)
            {
                case "trigger":
                    if (!Triggers.Contains(node.Trigger)) Issue(node.Id, "Select a supported Quality action.");
                    if (incoming[node.Id].Any()) Issue(node.Id, "Triggers cannot have incoming connections.");
                    if (edges.Count != 1 || edges.Any(edge => edge.Branch is not null))
                        Issue(node.Id, "Connect the trigger to exactly one next step.");
                    if (node.AllowedGroupIds?.Any(id => id <= 0) == true)
                        Issue(node.Id, "Select valid access groups for this trigger.");
                    break;
                case "condition":
                    if (node.Field is not ("customer" or "taskType" or "status" or "holdReason"))
                        Issue(node.Id, "Select Customer, Task type, Status, or Hold reason.");
                    if (node.Operator is not ("Equals" or "Contains" or "StartsWith" or "IsEmpty"))
                        Issue(node.Id, "Select a supported condition operator.");
                    if (node.Operator != "IsEmpty" && string.IsNullOrWhiteSpace(node.Value))
                        Issue(node.Id, "Enter a value for this condition.");
                    if (edges.Count != 2 || edges.Count(edge => edge.Branch == "yes") != 1
                        || edges.Count(edge => edge.Branch == "no") != 1)
                        Issue(node.Id, "Connect both the Yes and No paths exactly once.");
                    break;
                case "route":
                    if (node.TargetGroupId is null or <= 0) Issue(node.Id, "Select a destination queue.");
                    if (node.AssignmentMode is not ("GroupOnly" or "SpecificUser" or "LeastLoaded"))
                        Issue(node.Id, "Select a queue assignment mode.");
                    if (node.AssignmentMode == "SpecificUser" && node.TargetUserId is null or <= 0)
                        Issue(node.Id, "Select the person who will receive this work.");
                    if (edges.Count > 1 || edges.Any(edge => edge.Branch is not null
                        || !nodes.TryGetValue(edge.Target, out var target) || target.Type != "end"))
                        Issue(node.Id, "A queue route can end here or connect to one End step.");
                    break;
                case "end":
                    if (edges.Count != 0) Issue(node.Id, "End steps cannot have outgoing connections.");
                    break;
            }
        }
        var states = new Dictionary<string, int>();
        void Visit(string id)
        {
            if (!nodes.ContainsKey(id)) return;
            if (states.TryGetValue(id, out var state))
            {
                if (state == 1) Issue(id, "This connection creates a loop. Every path must finish.");
                return;
            }
            states[id] = 1;
            foreach (var edge in outgoing[id]) Visit(edge.Target);
            states[id] = 2;
        }
        foreach (var trigger in triggerNodes) Visit(trigger.Id);
        foreach (var node in graph.Nodes.Where(node => !states.ContainsKey(node.Id)).ToList())
        {
            Issue(node.Id, "Connect this step to an action trigger or remove it.");
            Visit(node.Id);
        }
        return new(issues.Count == 0, issues);
    }

    public static QualityWorkflowSimulation Evaluate(
        QualityWorkflowGraph graph, string trigger, QualityWorkflowContext context)
    {
        var validation = Validate(graph);
        if (!validation.IsValid) return new(false, validation.Issues, [], "blocked", null, null, null, "Fix the workflow issues before testing.");
        var current = graph.Nodes.SingleOrDefault(node => node.Type == "trigger" && node.Trigger == trigger);
        if (current is null) return new(true, [], [], "no-trigger", null, null, null, "No trigger for this action; existing action behavior applies.");
        var path = new List<string> { current.Id };
        if (current.AllowedGroupIds is { Count: > 0 } allowed
            && !(context.ActorGroupIds ?? []).Any(allowed.Contains))
            return new(true, [], path, "blocked", null, null, null, "Your access groups cannot run this workflow action.");
        var nodes = graph.Nodes.ToDictionary(node => node.Id);
        var outgoing = graph.Edges.ToLookup(edge => edge.Source);
        QualityWorkflowNode? route = null;
        while (true)
        {
            if (current.Type == "route") route = current;
            var next = current.Type == "condition"
                ? outgoing[current.Id].Single(edge => edge.Branch == (Matches(current, context) ? "yes" : "no"))
                : outgoing[current.Id].SingleOrDefault();
            if (next is null) break;
            current = nodes[next.Target];
            path.Add(current.Id);
        }
        return new(true, [], path, route is null ? "keep" : "route", route?.TargetGroupId,
            route?.AssignmentMode == "SpecificUser" ? route.TargetUserId : null, route?.AssignmentMode,
            route is null ? "Keep the current assignment." : "Route work to the selected queue.");
    }

    private static bool Matches(QualityWorkflowNode node, QualityWorkflowContext context)
    {
        var source = (node.Field switch
        {
            "customer" => context.Customer, "taskType" => context.TaskType,
            "status" => context.Status, "holdReason" => context.HoldReason, _ => null
        })?.Trim() ?? string.Empty;
        var value = node.Value?.Trim() ?? string.Empty;
        return node.Operator switch
        {
            "Equals" => string.Equals(source, value, StringComparison.OrdinalIgnoreCase),
            "Contains" => source.Contains(value, StringComparison.OrdinalIgnoreCase),
            "StartsWith" => source.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            "IsEmpty" => source.Length == 0, _ => false
        };
    }
}

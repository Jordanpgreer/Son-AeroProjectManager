using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;

namespace QualityAssurance.Tests;

public sealed class QualityWorkflowGraphEngineTests
{
    internal static QualityWorkflowGraph Graph(string trigger = "shipment-created") => new("quality-assurance", "Quality flow",
    [
        new("start", "trigger", "Start", 0, 0, Trigger: trigger),
        new("check", "condition", "Customer is Acme", 200, 0, Field: "customer", Operator: "Equals", Value: "Acme"),
        new("queue", "route", "Inspection queue", 400, 0, TargetGroupId: 20, AssignmentMode: "GroupOnly"),
        new("done", "end", "Keep assignment", 400, 200)
    ], [new("a", "start", "check"), new("b", "check", "queue", "yes"), new("c", "check", "done", "no")]);

    [Theory]
    [InlineData("ACME", "route", "queue")]
    [InlineData("  Acme  ", "route", "queue")]
    [InlineData("Elsewhere", "keep", "done")]
    public void Branches_evaluate_case_insensitively_and_return_exact_path(string customer, string outcome, string terminal)
    {
        var result = QualityWorkflowGraphEngine.Evaluate(Graph(), "shipment-created", new(Customer: customer));
        Assert.True(result.IsValid);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(["start", "check", terminal], result.Path);
    }

    [Fact]
    public void Cycles_missing_branches_and_orphan_nodes_are_rejected()
    {
        var graph = Graph();
        var loop = graph with { Edges = [new("a", "start", "check"), new("b", "check", "check", "yes")] };
        var result = QualityWorkflowGraphEngine.Validate(loop);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("loop"));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("Yes and No"));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("Connect this step"));
    }

    [Fact]
    public void Duplicate_triggers_and_dangling_connections_are_rejected()
    {
        var graph = Graph();
        var result = QualityWorkflowGraphEngine.Validate(graph with
        {
            Nodes = [.. graph.Nodes, new("duplicate", "trigger", "Duplicate", 0, 300, Trigger: "shipment-created")],
            Edges = [.. graph.Edges, new("missing", "duplicate", "absent")]
        });
        Assert.Contains(result.Issues, issue => issue.Message.Contains("only one trigger"));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("missing step"));
    }

    [Fact]
    public void A_route_cannot_chain_into_another_queue()
    {
        var graph = Graph();
        var result = QualityWorkflowGraphEngine.Validate(graph with { Edges = [.. graph.Edges, new("bad", "queue", "check")] });
        Assert.Contains(result.Issues, issue => issue.NodeId == "queue");
    }

    [Fact]
    public void Trigger_groups_can_only_restrict_execution()
    {
        var graph = Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "trigger" ? node with { AllowedGroupIds = [20] } : node).ToList() };
        Assert.Equal("blocked", QualityWorkflowGraphEngine.Evaluate(graph, "shipment-created", new(Customer: "Acme", ActorGroupIds: [10])).Outcome);
        Assert.Equal("route", QualityWorkflowGraphEngine.Evaluate(graph, "shipment-created", new(Customer: "Acme", ActorGroupIds: [20])).Outcome);
    }

    [Fact]
    public void Module_and_graph_size_are_boundary_validated()
    {
        Assert.Throws<ArgumentException>(() => QualityWorkflowGraphEngine.Validate(Graph() with { Module = "portal" }));
        Assert.Throws<ArgumentException>(() => QualityWorkflowGraphEngine.Validate(Graph() with
        {
            Nodes = Enumerable.Range(0, 101).Select(index => new QualityWorkflowNode(index.ToString(), "end", "End", 0, 0)).ToList()
        }));
    }

    [Theory]
    [InlineData("Contains", "cme", "Acme customer", true)]
    [InlineData("StartsWith", "Ac", "Acme", true)]
    [InlineData("StartsWith", "Ac", "Customer Acme", false)]
    [InlineData("IsEmpty", null, "  ", true)]
    [InlineData("IsEmpty", null, "Hold", false)]
    public void Business_conditions_support_text_matching(string op, string? value, string source, bool matches)
    {
        var graph = Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "condition" ? node with { Operator = op, Value = value } : node).ToList() };
        Assert.Equal(matches ? "route" : "keep", QualityWorkflowGraphEngine.Evaluate(graph, "shipment-created", new(Customer: source)).Outcome);
    }
}

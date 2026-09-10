using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityWorkflowServiceTests
{
    [Fact]
    public async Task Read_and_simulation_do_not_write_anything()
    {
        await using var f = await Fixture.CreateAsync();
        var initial = await f.Workflows.GetAsync(f.Admin, default);
        var result = await f.Workflows.SimulateAsync(new(QualityWorkflowGraphEngineTests.Graph(), "shipment-created", new(Customer: "Acme")), f.Admin, default);
        Assert.Equal(0, initial.Version);
        Assert.Null(initial.Published);
        Assert.Equal("route", result.Outcome);
        Assert.Equal(0, await f.Db.Workflows.CountAsync());
        Assert.Equal(0, await f.Db.WorkflowAuditEntries.CountAsync());
        Assert.Equal(0, await f.Db.Shipments.CountAsync());
    }

    [Theory]
    [InlineData("qa-completed", "Ready to Ship")]
    [InlineData("shipment-shipped", "Shipped")]
    public async Task Simulation_status_conditions_follow_the_native_action_and_match_its_execution_path(string trigger, string expectedStatus)
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph(trigger);
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "condition"
            ? node with { Label = "Native status", Field = "status", Value = expectedStatus } : node).ToList() };
        await f.PublishAsync(graph);
        var simulation = await f.Workflows.SimulateAsync(new(graph, trigger, new(Customer: "Acme", Status: "WIP")), f.Admin, default);
        Assert.Equal("route", simulation.Outcome);
        Assert.Equal(["start", "check", "queue"], simulation.Path);
        Assert.Equal(0, await f.Db.Shipments.CountAsync());
        var created = await f.Shipments.CreateAsync(CreateDto(), f.Admin, default);
        var updated = trigger == "qa-completed"
            ? await f.Shipments.MarkQaCompleteAsync(created.Id, created.Version, f.Admin, default)
            : await f.Shipments.MarkShippedAsync(created.Id, created.Version, f.Admin, default);
        Assert.Equal(expectedStatus, updated!.Status);
        Assert.Equal(simulation.TargetGroupId, updated.AssignedGroupId);
        var audit = await f.Db.ShipmentAuditEntries.SingleAsync(entry => entry.EventType == "WorkflowExecuted");
        using var execution = System.Text.Json.JsonDocument.Parse(audit.NewValue!);
        Assert.Equal(simulation.Path, execution.RootElement.GetProperty("path").EnumerateArray().Select(node => node.GetString()));
    }

    [Fact]
    public async Task Incomplete_draft_can_be_saved_but_cannot_be_published()
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph() with { Edges = [] };
        var saved = await f.Workflows.SaveAsync(new(0, graph), f.Admin, default);
        Assert.False(saved.Validation.IsValid);
        Assert.Equal(1, saved.Version);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Workflows.PublishAsync(saved.Version, f.Admin, default));
        Assert.Null((await f.Db.Workflows.SingleAsync()).PublishedJson);
    }

    [Fact]
    public async Task Draft_and_published_versions_are_independent_and_audited()
    {
        await using var f = await Fixture.CreateAsync();
        var saved = await f.Workflows.SaveAsync(new(0, QualityWorkflowGraphEngineTests.Graph()), f.Admin, default);
        var published = await f.Workflows.PublishAsync(saved.Version, f.Admin, default);
        var changed = await f.Workflows.SaveAsync(new(published.Version, published.Draft with { Name = "Next version" }), f.Admin, default);
        Assert.Equal(1, published.PublishedRevision);
        Assert.Equal("Quality flow", changed.Published!.Name);
        Assert.Equal("Next version", changed.Draft.Name);
        Assert.Equal(["DraftSaved", "Published", "DraftSaved"], changed.History.Select(entry => entry.Action));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Workflows.SaveAsync(new(saved.Version, saved.Draft), f.Admin, default));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => f.Workflows.PublishAsync(published.Version, f.Admin, default));
    }

    [Fact]
    public async Task Non_managers_cannot_read_save_publish_or_simulate()
    {
        await using var f = await Fixture.CreateAsync();
        var denied = new QualityAssuranceAccessProfile(2, "TEST\\denied", "Denied", ApplicationRoles.Viewer, [], []);
        var graph = QualityWorkflowGraphEngineTests.Graph();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Workflows.GetAsync(denied, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Workflows.SaveAsync(new(0, graph), denied, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Workflows.PublishAsync(0, denied, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Workflows.SimulateAsync(new(graph, "shipment-created", new()), denied, default));
        Assert.Empty(await f.Db.WorkflowAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task Eligibility_is_rechecked_at_publication_and_execution()
    {
        await using var f = await Fixture.CreateAsync();
        var saved = await f.Workflows.SaveAsync(new(0, QualityWorkflowGraphEngineTests.Graph()), f.Admin, default);
        f.Directory.Enabled = false;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Workflows.PublishAsync(saved.Version, f.Admin, default));
        f.Directory.Enabled = true;
        await f.Workflows.PublishAsync(saved.Version, f.Admin, default);
        f.Directory.Enabled = false;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Shipments.CreateAsync(CreateDto(), f.Admin, default));
        Assert.Equal(0, await f.Db.Shipments.CountAsync());
    }

    [Fact]
    public async Task Legacy_rules_stay_live_until_publish_then_workflow_owns_routing()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.AssignmentRules.Add(new QualityAssignmentRule { Name = "Old", MatchValue = "Acme", TargetGroupId = 10, TargetGroupName = "Quality", AssignmentMode = "GroupOnly" });
        await f.Db.SaveChangesAsync();
        var saved = await f.Workflows.SaveAsync(new(0, QualityWorkflowGraphEngineTests.Graph()), f.Admin, default);
        var before = await f.Shipments.CreateAsync(CreateDto("BEFORE"), f.Admin, default);
        Assert.Equal(10, before.AssignedGroupId);
        await f.Workflows.PublishAsync(saved.Version, f.Admin, default);
        var after = await f.Shipments.CreateAsync(CreateDto("AFTER"), f.Admin, default);
        Assert.Equal(20, after.AssignedGroupId);
        Assert.Equal(1, await f.Db.AssignmentRules.CountAsync());
        var audit = await f.Db.ShipmentAuditEntries.SingleAsync(entry => entry.ShipmentId == after.Id && entry.EventType == "WorkflowExecuted");
        Assert.Contains("\"publishedRevision\":1", audit.NewValue);
        Assert.Contains("\"path\":[\"start\",\"check\",\"queue\"]", audit.NewValue);
    }

    [Fact]
    public async Task Restricted_trigger_blocks_the_native_action_without_committing_a_shipment()
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "trigger" ? node with { AllowedGroupIds = [20] } : node).ToList() };
        await f.PublishAsync(graph);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Shipments.CreateAsync(CreateDto(), f.Admin, default));
        Assert.Equal(0, await f.Db.Shipments.CountAsync());
        Assert.Equal(0, await f.Db.ShipmentAuditEntries.CountAsync());
    }

    [Fact]
    public async Task Qa_complete_uses_published_queue_instead_of_native_shipper()
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph("qa-completed");
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "route" ? node with { TargetGroupId = 10 } : node).ToList() };
        await f.PublishAsync(graph);
        var created = await f.Shipments.CreateAsync(CreateDto(), f.Admin, default);
        var completed = await f.Shipments.MarkQaCompleteAsync(created.Id, created.Version, f.Admin, default);
        Assert.Equal("Ready to Ship", completed!.Status);
        Assert.Equal(10, completed.AssignedGroupId);
        Assert.Null(completed.AssignedUserId);
        Assert.Contains(await f.Db.ShipmentAuditEntries.ToListAsync(), entry => entry.EventType == "WorkflowExecuted");
    }

    [Theory]
    [InlineData("shipment-shipped")]
    [InlineData("assignment-changed")]
    [InlineData("shipment-updated")]
    public async Task Native_actions_run_their_published_trigger(string trigger)
    {
        await using var f = await Fixture.CreateAsync();
        await f.PublishAsync(QualityWorkflowGraphEngineTests.Graph(trigger));
        var created = await f.Shipments.CreateAsync(CreateDto(), f.Admin, default);
        QualityShipmentDto? changed = trigger switch
        {
            "shipment-shipped" => await f.Shipments.MarkShippedAsync(created.Id, created.Version, f.Admin, default),
            "assignment-changed" => await f.Shipments.AssignAsync(created.Id, new(created.Version, 10, 1), f.Admin, default),
            _ => await f.Shipments.PatchAsync(created.Id, new(created.Version, new Dictionary<string, System.Text.Json.JsonElement>
            { ["holdReason"] = System.Text.Json.JsonSerializer.SerializeToElement("Review") }), f.Admin, default)
        };
        Assert.Equal(20, changed!.AssignedGroupId);
        Assert.Equal(trigger == "shipment-shipped", changed.IsShipped);
        Assert.Contains(await f.Db.ShipmentAuditEntries.ToListAsync(), entry => entry.EventType == "WorkflowExecuted" && entry.NewValue!.Contains(trigger));
    }

    private static QualityShipmentCreateDto CreateDto(string number = "SHIP-TEST") => new(
        "WIP", number, new DateOnly(2026, 9, 10), "PART-1", "PO-1", "Acme", "General", 1, 25, new DateOnly(2026, 9, 30), null, null, null, null);

    [Fact]
    public async Task Least_loaded_routing_counts_open_work_and_uses_eligible_group_members()
    {
        await using var f = await Fixture.CreateAsync();
        var busy = await f.Shipments.CreateAsync(CreateDto("BUSY-ADMIN"), f.Admin, default);
        Assert.Equal(99, busy.AssignedUserId);
        var graph = QualityWorkflowGraphEngineTests.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "route"
            ? node with { TargetGroupId = 10, AssignmentMode = "LeastLoaded" } : node).ToList() };
        await f.PublishAsync(graph);
        var assigned = await f.Shipments.CreateAsync(CreateDto("BALANCED"), f.Admin, default);
        Assert.Equal(1, assigned.AssignedUserId);
        Assert.Equal(10, assigned.AssignedGroupId);
    }

    [Fact]
    public async Task Specific_user_requires_membership_and_does_not_grant_any_permissions()
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "route"
            ? node with { AssignmentMode = "SpecificUser", TargetUserId = 99 } : node).ToList() };
        var validation = await f.Workflows.ValidateAsync(graph, default);
        Assert.False(validation.IsValid);
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "route"
            ? node with { TargetUserId = 1 } : node).ToList() };
        await f.PublishAsync(graph);
        var assigned = await f.Shipments.CreateAsync(CreateDto(), f.Admin, default);
        Assert.Equal(1, assigned.AssignedUserId);
        Assert.Equal(20, assigned.AssignedGroupId);
        Assert.False(new QualityAssuranceAccessProfile(1, "TEST\\one", "One", ApplicationRoles.Viewer, [], [new(20, "Shipper")])
            .HasPermission(QualityAssurancePermissions.RulesManage));
    }

    [Fact]
    public async Task Native_record_access_remains_required_even_when_a_workflow_would_route_it()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PublishAsync(QualityWorkflowGraphEngineTests.Graph("shipment-shipped"));
        var created = await f.Shipments.CreateAsync(CreateDto(), f.Admin, default);
        var other = new QualityAssuranceAccessProfile(2, "TEST\\other", "Other", ApplicationRoles.Editor,
            [QualityAssurancePermissions.MarkShipped, QualityAssurancePermissions.ShipmentsView], [new(20, "Shipper")]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Shipments.MarkShippedAsync(created.Id, created.Version, other, default));
        Assert.False((await f.Db.Shipments.AsNoTracking().SingleAsync()).IsShipped);
        Assert.DoesNotContain(await f.Db.ShipmentAuditEntries.ToListAsync(), entry => entry.EventType == "WorkflowExecuted");
    }

    [Fact]
    public async Task User_and_dashboard_capabilities_reflect_published_restrictions()
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph("shipment-updated");
        graph = graph with
        {
            Nodes = [.. graph.Nodes.Select(node => node.Type == "trigger" ? node with { AllowedGroupIds = [20] } : node),
                new("assign", "trigger", "Assignment changed", 0, 500, Trigger: "assignment-changed", AllowedGroupIds: [20])],
            Edges = [.. graph.Edges, new("assign-next", "assign", "done")]
        };
        await f.PublishAsync(graph);
        var restricted = await f.Workflows.GetRestrictedActionsAsync(f.Admin, default);
        Assert.Equal(["shipment-updated", "assignment-changed"], restricted);
        var dashboard = await f.Shipments.DashboardAsync(f.Admin, default);
        Assert.False(dashboard.CanAssign);
        Assert.False(dashboard.CanAssignGroup);
        Assert.False(dashboard.CanAssignUser);
        Assert.All(dashboard.Fields.Where(field => field.Key != "comments"), field => Assert.False(field.CanEdit));
        Assert.True(dashboard.Fields.Single(field => field.Key == "comments").CanEdit);
        var list = await f.Shipments.ListAsync(f.Admin, "all", "mine", "oldest", null, null, null, null, null, default);
        Assert.All(list.Fields.Where(field => field.Key != "comments"), field => Assert.False(field.CanEdit));
        Assert.True(list.Fields.Single(field => field.Key == "comments").CanEdit);
        var allowed = new QualityAssuranceAccessProfile(1, "TEST\\one", "One", ApplicationRoles.Viewer, [], [new(20, "Shipper")]);
        Assert.Empty(await f.Workflows.GetRestrictedActionsAsync(allowed, default));
    }

    [Fact]
    public async Task Legacy_reconciliation_cannot_override_a_workflow_decision()
    {
        await using var f = await Fixture.CreateAsync();
        var shipment = new QualityShipment
        {
            SalesOrderNumber = "LEGACY-WORKFLOW", AssignedGroupId = 20, AssignedGroupName = ApplicationGroups.Shipper,
            AssignedUserId = 1, AssignedDisplayName = "One", LegacyAssigneeTag = "One", NextAction = "QA - One",
            AuditEntries = [new QualityShipmentAuditEntry { EventType = "WorkflowExecuted" }]
        };
        f.Db.Shipments.Add(shipment);
        await f.Db.SaveChangesAsync();
        var reconciler = new QualityLegacyAssignmentReconciler(f.Db, f.Directory, NullLogger<QualityLegacyAssignmentReconciler>.Instance);
        Assert.Equal(0, await reconciler.ReconcileAsync());
        Assert.Equal(20, (await f.Db.Shipments.SingleAsync()).AssignedGroupId);
    }

    [Fact]
    public async Task Least_loaded_routing_balances_pending_import_rows_before_the_batch_is_saved()
    {
        await using var f = await Fixture.CreateAsync();
        var graph = QualityWorkflowGraphEngineTests.Graph("shipment-imported");
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == "route"
            ? node with { TargetGroupId = 10, AssignmentMode = "LeastLoaded" } : node).ToList() };
        await f.PublishAsync(graph);
        var recipients = new List<int?>();
        for (var index = 0; index < 4; index++)
        {
            var shipment = new QualityShipment { Customer = "Acme", SalesOrderNumber = $"BATCH-{index}" };
            await f.Workflows.ApplyAsync("shipment-imported", shipment, f.Admin, default);
            recipients.Add(shipment.AssignedUserId);
            f.Db.Shipments.Add(shipment);
        }
        Assert.Equal([1, 99, 1, 99], recipients);
        Assert.Equal(0, await f.Db.Shipments.CountAsync());
        await f.Db.SaveChangesAsync();
        Assert.Equal(2, await f.Db.Shipments.CountAsync(shipment => shipment.AssignedUserId == 1));
        Assert.Equal(2, await f.Db.Shipments.CountAsync(shipment => shipment.AssignedUserId == 99));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public QualityAssuranceDbContext Db { get; }
        public DirectoryStore Directory { get; } = new();
        public QualityWorkflowService Workflows { get; }
        public QualityShipmentService Shipments { get; }
        public QualityAssuranceAccessProfile Admin { get; } = new(99, "TEST\\admin", "Admin", ApplicationRoles.Admin,
            [.. QualityAssurancePermissions.AdministratorDefaults, QualityAssurancePermissions.AssignmentEligible], [new(10, "Quality")]);

        private Fixture(SqliteConnection connection, QualityAssuranceDbContext db)
        {
            this.connection = connection;
            Db = db;
            Workflows = new(db, Directory);
            Shipments = new(db, Directory, new(db, Directory), new(db, Directory, NullLogger<QualityLegacyAssignmentReconciler>.Instance), workflows: Workflows);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new(connection, db);
        }

        public async Task PublishAsync(QualityWorkflowGraph graph)
        {
            var saved = await Workflows.SaveAsync(new(0, graph), Admin, default);
            await Workflows.PublishAsync(saved.Version, Admin, default);
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class DirectoryStore : IQualityAssuranceAccessStore
    {
        public bool Enabled { get; set; } = true;
        private readonly IReadOnlyList<QualityDirectoryGroup> groups = [new(10, "Quality", "Quality", 2), new(20, ApplicationGroups.Shipper, "Shipper", 1)];
        private readonly IReadOnlyList<QualityDirectoryUser> users = [new(1, "TEST\\one", "One", [10, 20]), new(99, "TEST\\admin", "Admin", [10])];
        public Task<QualityAssuranceAccessProfile?> FindAccessAsync(string accountName, CancellationToken cancellationToken = default) => Task.FromResult<QualityAssuranceAccessProfile?>(null);
        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult(groups);
        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsWithPermissionAsync(string permissionKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QualityDirectoryGroup>>(Enabled ? groups : []);
        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(int? groupId = null, CancellationToken cancellationToken = default) => Task.FromResult(users);
        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersWithPermissionAsync(string permissionKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QualityDirectoryUser>>(Enabled ? users : []);
    }
}

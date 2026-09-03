using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityShipmentWorkflowTests
{
    [Fact]
    public async Task Create_and_ship_preserves_audit_and_moves_record_to_past_shipments()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var created = await fixture.Shipments.CreateAsync(new QualityShipmentCreateDto(
            "WIP", "SO-100", new DateOnly(2026, 8, 1), "PN-100", "PO-10", "Customer A",
            "Source Inspection", 5, 1250, new DateOnly(2026, 8, 15), null, null, "Review package", "Initial note"),
            fixture.Admin,
            CancellationToken.None);

        Assert.Equal(fixture.Admin.UserId, created.AssignedUserId);
        Assert.Equal(2, await fixture.Db.ShipmentAuditEntries.CountAsync());

        var shipped = await fixture.Shipments.MarkShippedAsync(created.Id, created.Version, fixture.Admin, CancellationToken.None);
        Assert.NotNull(shipped);
        Assert.True(shipped.IsShipped);

        var open = await fixture.Shipments.ListAsync(fixture.Admin, "open", "mine", "oldest", null, null, null, null, null, CancellationToken.None);
        var past = await fixture.Shipments.ListAsync(fixture.Admin, "shipped", "mine", "oldest", null, null, null, null, null, CancellationToken.None);
        var audit = await fixture.Shipments.AuditAsync(created.Id, fixture.Admin, CancellationToken.None);

        Assert.Empty(open.Items);
        Assert.Single(past.Items);
        Assert.Contains(audit!, entry => entry.EventType == "Shipped");
    }

    [Fact]
    public async Task Create_supports_multiple_parts_and_calculates_record_totals()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var created = await fixture.Shipments.CreateAsync(new QualityShipmentCreateDto(
            "WIP", "SHIP-MULTI", new DateOnly(2026, 9, 3), "LEGACY", "PO-MULTI", "Customer A",
            "General", null, null, new DateOnly(2026, 9, 12), null, null, null, null,
            [
                new QualityShipmentPartInputDto("PART-A", 2, 12.50m),
                new QualityShipmentPartInputDto("PART-B", 3, 20m)
            ]), fixture.Admin, default);

        Assert.Equal(2, created.Parts.Count);
        Assert.Equal(5m, created.Quantity);
        Assert.Equal(85m, created.DollarValue);
        Assert.Equal("PART-A, PART-B", created.PartNumber);
    }

    [Fact]
    public async Task Qa_complete_routes_quality_work_to_shipping_without_marking_it_shipped()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var shipment = ShipmentForGrid("SHIP-QA", "WIP", "Customer", 100, 99);
        fixture.Db.Shipments.Add(shipment);
        await fixture.Db.SaveChangesAsync();

        var updated = await fixture.Shipments.MarkQaCompleteAsync(
            shipment.Id,
            shipment.Version,
            fixture.Admin,
            default);

        Assert.NotNull(updated);
        Assert.Equal("Ready to Ship", updated.Status);
        Assert.Equal(20, updated.AssignedGroupId);
        Assert.Equal("Shipping", updated.AssignedGroupName);
        Assert.Null(updated.AssignedUserId);
        Assert.False(updated.IsShipped);
        Assert.Contains(await fixture.Db.ShipmentAuditEntries.ToListAsync(), entry => entry.EventType == "QaCompleted");
    }

    [Fact]
    public async Task Shipping_group_members_see_ready_to_ship_group_work_in_their_default_queue()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var shipment = ShipmentForGrid("SHIP-GROUP-QUEUE", "Ready to Ship", "Customer", 100, null);
        shipment.AssignedGroupId = 20;
        shipment.AssignedGroupName = "Shipping";
        shipment.ShipDate = new DateOnly(2026, 9, 8);
        fixture.Db.Shipments.Add(shipment);
        await fixture.Db.SaveChangesAsync();
        var shippingUser = new QualityAssuranceAccessProfile(
            40,
            "TEST\\shipping",
            "Shipping User",
            ApplicationRoles.Editor,
            [.. QualityAssurancePermissions.EditorDefaults],
            [new QualityAssuranceAccessGroup(20, "Shipping")]);

        var list = await fixture.Shipments.ListAsync(
            shippingUser, "open", "mine", "ship-date", "asc", null, null, null, null, default);
        var dashboard = await fixture.Shipments.DashboardAsync(shippingUser, default);

        Assert.Equal("SHIP-GROUP-QUEUE", Assert.Single(list.Items).SalesOrderNumber);
        Assert.Equal("SHIP-GROUP-QUEUE", Assert.Single(dashboard.Queue).SalesOrderNumber);
        Assert.NotNull(await fixture.Shipments.GetAsync(shipment.Id, shippingUser, default));
    }

    [Fact]
    public async Task CreatingWorkDoesNotAssignAnIneligibleCreatorAsTheOwner()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var permissions = fixture.Admin.Permissions
            .Where(permission => permission != QualityAssurancePermissions.AssignmentEligible)
            .ToList();
        var creator = fixture.Admin with { Permissions = permissions };

        var created = await fixture.Shipments.CreateAsync(new QualityShipmentCreateDto(
            "WIP", "SO-INELIGIBLE-CREATOR", new DateOnly(2026, 9, 1), "PN-CREATOR", "PO-CREATOR", "Customer",
            "General", null, null, new DateOnly(2026, 9, 10), null, null, null, null),
            creator,
            default);

        Assert.Equal(10, created.AssignedGroupId);
        Assert.Null(created.AssignedUserId);
        Assert.Null(created.AssignedDisplayName);
    }

    [Fact]
    public async Task Least_loaded_rule_assigns_the_person_with_the_smallest_open_queue()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.AssignmentRules.Add(new QualityAssignmentRule
        {
            Name = "Customer A balance",
            IsEnabled = true,
            Priority = 1,
            MatchField = "Customer",
            MatchOperator = "Equals",
            MatchValue = "Customer A",
            TargetGroupId = 10,
            TargetGroupName = "Quality",
            AssignmentMode = "LeastLoaded",
            CreatedBy = "TEST\\admin",
            UpdatedBy = "TEST\\admin"
        });
        fixture.Db.Shipments.Add(new QualityShipment
        {
            SalesOrderNumber = "EXISTING",
            PartNumber = "PN",
            Customer = "Other",
            TaskType = "General",
            AssignedGroupId = 10,
            AssignedUserId = 1,
            CreatedByAccountName = "TEST\\admin",
            CreatedByDisplayName = "Admin",
            UpdatedByAccountName = "TEST\\admin",
            UpdatedByDisplayName = "Admin"
        });
        await fixture.Db.SaveChangesAsync();

        var created = await fixture.Shipments.CreateAsync(new QualityShipmentCreateDto(
            "WIP", "SO-200", new DateOnly(2026, 9, 1), "PN-200", "PO-200", "Customer A", "General",
            null, null, new DateOnly(2026, 9, 10), null, null, null, null), fixture.Admin, CancellationToken.None);

        Assert.Equal(2, created.AssignedUserId);
        Assert.Equal("Person Two", created.AssignedDisplayName);
    }

    [Fact]
    public async Task Field_permissions_mask_values_that_the_user_cannot_view()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.Add(new QualityShipment
        {
            SalesOrderNumber = "SO-VISIBLE",
            PartNumber = "PN-HIDDEN",
            Customer = "Hidden Customer",
            TaskType = "General",
            AssignedUserId = 20,
            CreatedByAccountName = "TEST\\viewer",
            CreatedByDisplayName = "Viewer",
            UpdatedByAccountName = "TEST\\viewer",
            UpdatedByDisplayName = "Viewer"
        });
        await fixture.Db.SaveChangesAsync();
        var viewer = new QualityAssuranceAccessProfile(
            20,
            "TEST\\viewer",
            "Viewer",
            ApplicationRoles.Viewer,
            [QualityAssurancePermissions.ModuleView, QualityAssurancePermissions.ShipmentsView, QualityAssurancePermissions.SalesOrderView],
            []);

        var result = await fixture.Shipments.ListAsync(viewer, "open", "mine", "oldest", null, null, null, null, null, CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("SO-VISIBLE", result.Items[0].SalesOrderNumber);
        Assert.Null(result.Items[0].Customer);
        Assert.Null(result.Items[0].PartNumber);
    }

    [Fact]
    public async Task Oldest_queue_orders_by_qa_arrival_date_before_record_creation()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.AddRange(
            Shipment("SO-LATER-ARRIVAL", new DateOnly(2026, 8, 10), new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero)),
            Shipment("SO-OLDEST-ARRIVAL", new DateOnly(2026, 8, 2), new DateTimeOffset(2026, 8, 5, 8, 0, 0, TimeSpan.Zero)),
            Shipment("SO-NO-ARRIVAL", null, new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero)));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Shipments.ListAsync(
            fixture.Admin, "open", "mine", "oldest", null, null, null, null, null, CancellationToken.None);

        Assert.Equal(
            ["SO-OLDEST-ARRIVAL", "SO-LATER-ARRIVAL", "SO-NO-ARRIVAL"],
            result.Items.Select(item => item.SalesOrderNumber));
    }

    [Fact]
    public async Task AssignmentManagerCanReviewUnassignedWorkFromDashboardAndMineQueue()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.Add(new QualityShipment
        {
            SalesOrderNumber = "SO-UNASSIGNED",
            PartNumber = "PN-REVIEW",
            Customer = "Customer",
            TaskType = "General",
            CreatedByAccountName = "TEST\\admin",
            CreatedByDisplayName = "Admin",
            UpdatedByAccountName = "TEST\\admin",
            UpdatedByDisplayName = "Admin",
        });
        await fixture.Db.SaveChangesAsync();
        var manager = new QualityAssuranceAccessProfile(
            50,
            "TEST\\manager",
            "Quality Manager",
            ApplicationRoles.Editor,
            [
                QualityAssurancePermissions.ModuleView,
                QualityAssurancePermissions.ShipmentsView,
                QualityAssurancePermissions.TeamDashboardView,
                QualityAssurancePermissions.ManagerReview,
                QualityAssurancePermissions.AssignmentView,
                QualityAssurancePermissions.AssignmentGroup,
                QualityAssurancePermissions.AssignmentUser,
                QualityAssurancePermissions.SalesOrderView,
                QualityAssurancePermissions.PartNumberView,
                QualityAssurancePermissions.CustomerView,
                QualityAssurancePermissions.TaskTypeView,
            ],
            [new QualityAssuranceAccessGroup(10, "Quality")]);

        var dashboard = await fixture.Shipments.DashboardAsync(manager, default);
        var mine = await fixture.Shipments.ListAsync(manager, "open", "mine", "oldest", null, null, null, null, null, default);

        Assert.Contains(dashboard.Queue, shipment => shipment.SalesOrderNumber == "SO-UNASSIGNED");
        Assert.Equal(1, dashboard.MyQueue.Open);
        Assert.Equal(0, dashboard.GroupQueue.Open);
        Assert.Equal(1, dashboard.UnassignedQueue.Open);
        Assert.True(dashboard.CanReviewUnassigned);
        Assert.True(dashboard.CanViewTeam);
        Assert.True(dashboard.CanViewAssignment);
        Assert.True(dashboard.CanAssign);
        Assert.True(dashboard.CanAssignGroup);
        Assert.True(dashboard.CanAssignUser);
        Assert.False(dashboard.CanViewDollarValue);
        Assert.Null(dashboard.MyQueue.OpenDollarValue);
        Assert.Contains(dashboard.Fields, field => field.Key == "salesOrderNumber" && field.CanView);
        Assert.Contains(dashboard.Fields, field => field.Key == "dollarValue" && !field.CanView);
        Assert.Contains(mine.Items, shipment => shipment.SalesOrderNumber == "SO-UNASSIGNED");
    }

    [Fact]
    public async Task AssignmentEditorsWithoutManagerDashboardPermissionDoNotSeeUnassignedInMine()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.Add(new QualityShipment
        {
            SalesOrderNumber = "SO-MANAGER-ONLY",
            PartNumber = "PN-MANAGER-ONLY",
            Customer = "Customer",
            TaskType = "General",
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        });
        await fixture.Db.SaveChangesAsync();
        var assignmentEditor = new QualityAssuranceAccessProfile(
            50,
            "TEST\\assignment-editor",
            "Assignment Editor",
            ApplicationRoles.Editor,
            [
                QualityAssurancePermissions.ModuleView,
                QualityAssurancePermissions.ShipmentsView,
                QualityAssurancePermissions.TeamDashboardView,
                QualityAssurancePermissions.AssignmentView,
                QualityAssurancePermissions.AssignmentGroup,
                QualityAssurancePermissions.AssignmentUser,
            ],
            [new QualityAssuranceAccessGroup(10, "Quality")]);

        var dashboard = await fixture.Shipments.DashboardAsync(assignmentEditor, default);
        var mine = await fixture.Shipments.ListAsync(
            assignmentEditor, "open", "mine", "oldest", null, null, null, null, null, default);

        Assert.Empty(dashboard.Queue);
        Assert.Equal(0, dashboard.MyQueue.Open);
        Assert.True(dashboard.CanViewTeam);
        Assert.False(dashboard.CanReviewUnassigned);
        Assert.Equal(0, dashboard.UnassignedQueue.Open);
        Assert.Empty(dashboard.UnassignedShipments);
        Assert.Empty(mine.Items);
    }

    [Fact]
    public async Task Dashboard_keeps_group_queue_work_separate_from_people_and_fully_unassigned_work()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var groupOnly = ShipmentForGrid("SO-GROUP-QUEUE", "WIP", "Customer", 700, null);
        groupOnly.AssignedGroupId = 10;
        groupOnly.AssignedGroupName = "Quality";
        fixture.Db.Shipments.Add(groupOnly);
        await fixture.Db.SaveChangesAsync();

        var dashboard = await fixture.Shipments.DashboardAsync(fixture.Admin, default);

        Assert.Equal(1, dashboard.GroupQueue.Open);
        Assert.Equal(700m, dashboard.GroupQueue.OpenDollarValue);
        Assert.Equal("SO-GROUP-QUEUE", Assert.Single(dashboard.GroupShipments).SalesOrderNumber);
        Assert.Equal(0, dashboard.UnassignedQueue.Open);
        Assert.DoesNotContain(
            dashboard.TeamQueues.SelectMany(person => person.OpenShipments),
            shipment => shipment.SalesOrderNumber == "SO-GROUP-QUEUE");
    }

    [Fact]
    public async Task ManagerDashboardProvidesDollarRiskAndPersonDrilldownStatistics()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        fixture.Db.Shipments.AddRange(
            new QualityShipment
            {
                SalesOrderNumber = "SO-OPEN-VALUE",
                PartNumber = "PN-OPEN",
                Customer = "Customer",
                TaskType = "General",
                DollarValue = 1200,
                ShipDate = today.AddDays(-1),
                AssignedGroupId = 10,
                AssignedGroupName = "Quality",
                AssignedUserId = 1,
                AssignedDisplayName = "Person One",
                CreatedByAccountName = fixture.Admin.AccountName,
                CreatedByDisplayName = fixture.Admin.DisplayName,
                UpdatedByAccountName = fixture.Admin.AccountName,
                UpdatedByDisplayName = fixture.Admin.DisplayName,
            },
            new QualityShipment
            {
                SalesOrderNumber = "SO-COMPLETED-VALUE",
                PartNumber = "PN-DONE",
                Customer = "Customer",
                TaskType = "General",
                DollarValue = 3400,
                QaArrivalDate = today.AddDays(-5),
                IsShipped = true,
                ShippedAt = DateTimeOffset.UtcNow.AddDays(-1),
                AssignedGroupId = 10,
                AssignedGroupName = "Quality",
                AssignedUserId = 1,
                AssignedDisplayName = "Person One",
                CreatedByAccountName = fixture.Admin.AccountName,
                CreatedByDisplayName = fixture.Admin.DisplayName,
                UpdatedByAccountName = fixture.Admin.AccountName,
                UpdatedByDisplayName = fixture.Admin.DisplayName,
            });
        await fixture.Db.SaveChangesAsync();

        var dashboard = await fixture.Shipments.DashboardAsync(fixture.Admin, default);
        var person = Assert.Single(dashboard.TeamQueues, candidate => candidate.UserId == 1);

        Assert.Equal(1, person.Metrics.Open);
        Assert.Equal(1, person.Metrics.Overdue);
        Assert.Equal(1200m, person.Metrics.OpenDollarValue);
        Assert.Equal(1, person.Metrics.Completed);
        Assert.Equal(3400m, person.Metrics.CompletedDollarValue);
        Assert.Equal(3400m, person.Metrics.CompletedDollarValueYtd);
        Assert.Equal(3400m, person.Metrics.CompletedDollarValueCurrentQuarter);
        Assert.Equal("SO-OPEN-VALUE", Assert.Single(person.OpenShipments).SalesOrderNumber);
    }

    [Fact]
    public async Task ManagerReportProducesSummaryAndOneDetailPagePerPerson()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.AddRange(
            new QualityShipment
            {
                SalesOrderNumber = "SO-REPORT",
                PartNumber = "PN-REPORT",
                Customer = "Report Customer",
                TaskType = "General",
                DollarValue = 5000,
                AssignedGroupId = 10,
                AssignedGroupName = "Quality",
                AssignedUserId = 1,
                AssignedDisplayName = "Person One",
                CreatedByAccountName = fixture.Admin.AccountName,
                CreatedByDisplayName = fixture.Admin.DisplayName,
                UpdatedByAccountName = fixture.Admin.AccountName,
                UpdatedByDisplayName = fixture.Admin.DisplayName,
            },
            new QualityShipment
            {
                SalesOrderNumber = "SO-GROUP-REPORT",
                PartNumber = "PN-GROUP",
                Customer = "Report Customer",
                TaskType = "General",
                DollarValue = 2500,
                AssignedGroupId = 10,
                AssignedGroupName = "Quality",
                CreatedByAccountName = fixture.Admin.AccountName,
                CreatedByDisplayName = fixture.Admin.DisplayName,
                UpdatedByAccountName = fixture.Admin.AccountName,
                UpdatedByDisplayName = fixture.Admin.DisplayName,
            });
        await fixture.Db.SaveChangesAsync();

        var dashboard = await fixture.Shipments.DashboardAsync(fixture.Admin, default);
        var pdf = new QualityDashboardReportService().Generate(
            dashboard,
            fixture.Admin.DisplayName,
            DateTimeOffset.UtcNow);
        var text = System.Text.Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.True(pdf.Length > 5_000);
        Assert.Equal(dashboard.TeamQueues.Count + 1, text.Split("/Type /Page ").Length - 1);
        Assert.Contains("Team Shipping Performance", text);
        Assert.Contains("Person One", text);
        Assert.Contains("Group queue - needs owner", text);
    }

    [Fact]
    public async Task AssigningRegisteredUserSynchronizesActionToDisplayName()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var shipment = new QualityShipment
        {
            SalesOrderNumber = "SO-ASSIGN",
            PartNumber = "PN-ASSIGN",
            Customer = "Customer",
            TaskType = "General",
            NextAction = "QA-ONE",
            Version = 1,
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        };
        fixture.Db.Shipments.Add(shipment);
        await fixture.Db.SaveChangesAsync();

        var assigned = await fixture.Shipments.AssignAsync(
            shipment.Id,
            new QualityShipmentAssignmentDto(shipment.Version, 10, 1),
            fixture.Admin,
            default);

        Assert.NotNull(assigned);
        Assert.Equal("Person One", assigned.NextAction);
        Assert.Contains(await fixture.Db.ShipmentAuditEntries.ToListAsync(), entry =>
            entry.FieldName == "nextAction" && entry.NewValue == "Person One");
    }

    [Fact]
    public async Task ManualGroupOnlyAssignmentWinsOverLaterLegacyTagPromotion()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var shipment = new QualityShipment
        {
            SalesOrderNumber = "SO-MANUAL-GROUP",
            PartNumber = "PN-MANUAL-GROUP",
            Customer = "Customer",
            TaskType = "General",
            NextAction = "Quality",
            LegacyAssigneeTag = "Quality",
            Version = 1,
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        };
        fixture.Db.Shipments.Add(shipment);
        await fixture.Db.SaveChangesAsync();

        var assigned = await fixture.Shipments.AssignAsync(
            shipment.Id,
            new QualityShipmentAssignmentDto(shipment.Version, 10, null),
            fixture.Admin,
            default);

        Assert.NotNull(assigned);
        Assert.Equal(10, assigned.AssignedGroupId);
        Assert.Null(assigned.AssignedUserId);
        Assert.Null(shipment.LegacyAssigneeTag);

        // Simulate an older record whose legacy marker survived a group-only manual assignment.
        shipment.LegacyAssigneeTag = "Quality";
        await fixture.Db.SaveChangesAsync();
        await fixture.Shipments.DashboardAsync(fixture.Admin, default);

        Assert.Equal(10, shipment.AssignedGroupId);
        Assert.Null(shipment.AssignedUserId);
        Assert.Equal("Quality", shipment.LegacyAssigneeTag);
        Assert.Contains(shipment.AuditEntries, entry => entry.EventType == "Assigned");
        Assert.DoesNotContain(shipment.AuditEntries, entry => entry.EventType == "LegacyAssignmentPromoted");
    }

    [Fact]
    public async Task ExplicitlySavingUnassignedClearsLegacyPromotionMarkerEvenWhenIdsAreUnchanged()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var shipment = new QualityShipment
        {
            SalesOrderNumber = "SO-MANUAL-UNASSIGNED",
            PartNumber = "PN-MANUAL-UNASSIGNED",
            Customer = "Customer",
            TaskType = "General",
            NextAction = "Quality",
            LegacyAssigneeTag = "Quality",
            Version = 1,
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        };
        fixture.Db.Shipments.Add(shipment);
        await fixture.Db.SaveChangesAsync();

        var saved = await fixture.Shipments.AssignAsync(
            shipment.Id,
            new QualityShipmentAssignmentDto(shipment.Version, null, null),
            fixture.Admin,
            default);

        Assert.NotNull(saved);
        Assert.Equal(2, saved.Version);
        Assert.Null(shipment.LegacyAssigneeTag);
        Assert.Null(shipment.AssignedGroupId);
        Assert.Null(shipment.AssignedUserId);
        Assert.Contains(shipment.AuditEntries, entry =>
            entry.EventType == "Assigned"
            && entry.OldValue == "Legacy tag: Quality"
            && entry.NewValue == "Unassigned");

        await fixture.Shipments.DashboardAsync(fixture.Admin, default);
        Assert.Null(shipment.AssignedUserId);
        Assert.DoesNotContain(shipment.AuditEntries, entry => entry.EventType == "LegacyAssignmentPromoted");
    }

    [Fact]
    public async Task AssignmentViewerCannotConfirmLegacyTagButAssignmentEditorCan()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var shipment = new QualityShipment
        {
            SalesOrderNumber = "SO-LEGACY-CONFIRM",
            PartNumber = "PN-LEGACY-CONFIRM",
            Customer = "Customer",
            TaskType = "General",
            NextAction = "Quality",
            LegacyAssigneeTag = "Quality",
            Version = 1,
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        };
        fixture.Db.Shipments.Add(shipment);
        await fixture.Db.SaveChangesAsync();
        var viewer = new QualityAssuranceAccessProfile(
            51,
            "TEST\\assignment-viewer",
            "Assignment Viewer",
            ApplicationRoles.Viewer,
            [
                QualityAssurancePermissions.ModuleView,
                QualityAssurancePermissions.ShipmentsView,
                QualityAssurancePermissions.TeamDashboardView,
                QualityAssurancePermissions.ManagerReview,
                QualityAssurancePermissions.AssignmentView,
            ],
            [new QualityAssuranceAccessGroup(10, "Quality")]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Shipments.AssignAsync(
            shipment.Id,
            new QualityShipmentAssignmentDto(shipment.Version, null, null),
            viewer,
            default));

        Assert.Equal("Quality", shipment.LegacyAssigneeTag);
        Assert.Equal(1, shipment.Version);
        Assert.DoesNotContain(shipment.AuditEntries, entry => entry.EventType == "Assigned");

        var editor = viewer with
        {
            Role = ApplicationRoles.Editor,
            Permissions =
            [
                .. viewer.Permissions,
                QualityAssurancePermissions.ManagerReview,
                QualityAssurancePermissions.AssignmentGroup
            ],
        };
        var saved = await fixture.Shipments.AssignAsync(
            shipment.Id,
            new QualityShipmentAssignmentDto(shipment.Version, null, null),
            editor,
            default);

        Assert.NotNull(saved);
        Assert.Null(shipment.LegacyAssigneeTag);
        Assert.Equal(2, shipment.Version);
        Assert.Contains(shipment.AuditEntries, entry =>
            entry.EventType == "Assigned"
            && entry.AccountName == editor.AccountName
            && entry.OldValue == "Legacy tag: Quality"
            && entry.NewValue == "Unassigned");
    }

    [Fact]
    public async Task Search_matches_the_visible_action_owner_and_group()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.Add(new QualityShipment
        {
            SalesOrderNumber = "SO-OWNER-SEARCH",
            PartNumber = "PN-OWNER-SEARCH",
            Customer = "Customer",
            TaskType = "General",
            AssignedGroupId = 10,
            AssignedGroupName = "Final Inspection",
            AssignedUserId = 1,
            AssignedDisplayName = "Person One",
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        });
        await fixture.Db.SaveChangesAsync();

        var byPerson = await fixture.Shipments.ListAsync(
            fixture.Admin, "open", "all", "oldest", null, "person one", null, null, null, default);
        var byGroup = await fixture.Shipments.ListAsync(
            fixture.Admin, "open", "all", "oldest", null, "final inspection", null, null, null, default);

        Assert.Contains(byPerson.Items, shipment => shipment.SalesOrderNumber == "SO-OWNER-SEARCH");
        Assert.Contains(byGroup.Items, shipment => shipment.SalesOrderNumber == "SO-OWNER-SEARCH");
    }

    [Fact]
    public async Task Grid_filters_sort_and_export_use_the_same_result_set()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.AddRange(
            ShipmentForGrid("SO-ACME-ASSIGNED", "WIP", "Acme", 100, 1),
            ShipmentForGrid("SO-BETA", "Ready to Ship", "Beta", 500, 2),
            ShipmentForGrid("SO-ACME-UNASSIGNED", "WIP", "Acme Aerospace", 300, null));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Shipments.ListAsync(
            fixture.Admin, "open", "all", "dollar-value", "desc", null,
            "WIP", ["Acme"], "unassigned", default);

        Assert.Single(result.Items);
        Assert.Equal("SO-ACME-UNASSIGNED", result.Items[0].SalesOrderNumber);
        Assert.Equal("dollar-value", result.Sort);
        Assert.Equal("desc", result.Direction);

        var exporter = new QualityShipmentGridExportService(fixture.Shipments);
        var file = await exporter.CreateAsync(
            fixture.Admin, "open", "all", "dollar-value", "desc", null,
            "WIP", ["Acme"], "unassigned", default);
        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheet("Grid Results");
        var salesOrderColumn = sheet.Row(1).CellsUsed()
            .Single(cell => cell.GetString() == "Shipper Number")
            .Address.ColumnNumber;
        Assert.Equal("SO-ACME-UNASSIGNED", sheet.Cell(2, salesOrderColumn).GetString());
        Assert.Contains("quality-shipping-results-", file.FileName);
    }

    [Fact]
    public async Task Hidden_fields_cannot_control_list_or_export_order()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var older = ShipmentForGrid("SO-OLDER", "WIP", "Customer", 100, 99);
        older.CreatedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var newer = ShipmentForGrid("SO-NEWER", "WIP", "Customer", 900, 99);
        newer.CreatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        fixture.Db.Shipments.AddRange(older, newer);
        await fixture.Db.SaveChangesAsync();
        var restricted = new QualityAssuranceAccessProfile(
            99,
            "TEST\\admin",
            "Restricted Viewer",
            ApplicationRoles.Viewer,
            [
                QualityAssurancePermissions.ModuleView,
                QualityAssurancePermissions.ShipmentsView,
                QualityAssurancePermissions.SalesOrderView,
            ],
            [new QualityAssuranceAccessGroup(10, "Quality")]);

        var list = await fixture.Shipments.ListAsync(
            restricted, "open", "mine", "dollar-value", "desc", null,
            null, null, null, default);
        var export = await fixture.Shipments.ExportRowsAsync(
            restricted, "open", "mine", "dollar-value", "desc", null,
            null, null, null, default);

        Assert.Equal("queue-age", list.Sort);
        Assert.Equal(["SO-OLDER", "SO-NEWER"], list.Items.Select(item => item.SalesOrderNumber));
        Assert.Equal(
            list.Items.Select(item => item.SalesOrderNumber),
            export.Select(item => item.SalesOrderNumber));
        Assert.All(list.Items, item => Assert.Null(item.DollarValue));
    }

    [Fact]
    public async Task Multiple_customer_filters_use_or_semantics_and_keep_comma_names_intact()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.AddRange(
            ShipmentForGrid("SO-ATLAS", "WIP", "ATLAS TOOL WORKS, INC.", 100, 1),
            ShipmentForGrid("SO-HONEYWELL", "WIP", "HONEYWELL", 200, 2),
            ShipmentForGrid("SO-BOEING", "WIP", "BOEING CO-DEFENSE", 300, 1));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Shipments.ListAsync(
            fixture.Admin, "open", "all", "customer", "asc", null, null,
            ["ATLAS TOOL WORKS, INC.", "honey"], null, default);

        Assert.Equal(2, result.Total);
        Assert.Equal(
            ["SO-ATLAS", "SO-HONEYWELL"],
            result.Items.Select(item => item.SalesOrderNumber).OrderBy(value => value));
    }

    [Fact]
    public async Task Grid_export_uses_the_same_multiple_customer_filter_as_the_list()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        fixture.Db.Shipments.AddRange(
            ShipmentForGrid("SO-ATLAS", "WIP", "ATLAS TOOL WORKS, INC.", 100, 1),
            ShipmentForGrid("SO-HONEYWELL", "WIP", "HONEYWELL", 200, 2),
            ShipmentForGrid("SO-BOEING", "WIP", "BOEING CO-DEFENSE", 300, 1));
        await fixture.Db.SaveChangesAsync();
        string[] customers = ["ATLAS TOOL WORKS, INC.", "HONEYWELL"];

        var list = await fixture.Shipments.ListAsync(
            fixture.Admin, "open", "all", "sales-order", "asc", null, null,
            customers, null, default);
        var exporter = new QualityShipmentGridExportService(fixture.Shipments);
        var file = await exporter.CreateAsync(
            fixture.Admin, "open", "all", "sales-order", "asc", null, null,
            customers, null, default);

        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheet("Grid Results");
        var salesOrderColumn = sheet.Row(1).CellsUsed()
            .Single(cell => cell.GetString() == "Shipper Number")
            .Address.ColumnNumber;
        var exportedSalesOrders = sheet.Column(salesOrderColumn).CellsUsed()
            .Skip(1)
            .Select(cell => cell.GetString())
            .ToList();
        Assert.Equal(
            list.Items.Select(item => item.SalesOrderNumber ?? string.Empty).ToList(),
            exportedSalesOrders);
        Assert.DoesNotContain("SO-BOEING", exportedSalesOrders);
    }

    [Fact]
    public async Task Customer_options_respect_field_permission_scope_and_open_status()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var openMine = ShipmentForGrid("SO-MINE", "WIP", "Zulu Customer", 100, 99);
        var openMineDuplicate = ShipmentForGrid("SO-MINE-2", "WIP", "zulu customer", 100, 99);
        var shippedMine = ShipmentForGrid("SO-SHIPPED", "Shipped", "Archived Customer", 100, 99);
        shippedMine.IsShipped = true;
        var openOther = ShipmentForGrid("SO-OTHER", "WIP", "Alpha Customer", 100, 1);
        fixture.Db.Shipments.AddRange(openMine, openMineDuplicate, shippedMine, openOther);
        await fixture.Db.SaveChangesAsync();

        var mine = await fixture.Shipments.CustomerOptionsAsync(
            fixture.Admin, "open", "mine", default);
        var allOpen = await fixture.Shipments.CustomerOptionsAsync(
            fixture.Admin, "open", "all", default);
        var withoutCustomerPermission = new QualityAssuranceAccessProfile(
            99,
            "TEST\\admin",
            "Quality Admin",
            ApplicationRoles.Viewer,
            [QualityAssurancePermissions.ModuleView, QualityAssurancePermissions.ShipmentsView],
            [new QualityAssuranceAccessGroup(10, "Quality")]);
        var hidden = await fixture.Shipments.CustomerOptionsAsync(
            withoutCustomerPermission, "all", "mine", default);

        Assert.Equal(["Zulu Customer"], mine);
        Assert.Equal(["Alpha Customer", "Zulu Customer"], allOpen);
        Assert.DoesNotContain("Archived Customer", allOpen);
        Assert.Empty(hidden);
    }

    private static QualityShipment ShipmentForGrid(
        string salesOrder,
        string status,
        string customer,
        decimal dollarValue,
        int? assignedUserId) => new()
    {
        SalesOrderNumber = salesOrder,
        PartNumber = $"PN-{salesOrder}",
        Customer = customer,
        TaskType = "General",
        Status = status,
        DollarValue = dollarValue,
        AssignedGroupId = assignedUserId.HasValue ? 10 : null,
        AssignedGroupName = assignedUserId.HasValue ? "Quality" : null,
        AssignedUserId = assignedUserId,
        AssignedDisplayName = assignedUserId.HasValue ? $"Person {assignedUserId}" : null,
        CreatedByAccountName = "TEST\\admin",
        CreatedByDisplayName = "Admin",
        UpdatedByAccountName = "TEST\\admin",
        UpdatedByDisplayName = "Admin",
    };

    private static QualityShipment Shipment(
        string salesOrderNumber,
        DateOnly? qaArrivalDate,
        DateTimeOffset createdAt) => new()
    {
        SalesOrderNumber = salesOrderNumber,
        QaArrivalDate = qaArrivalDate,
        PartNumber = "PN",
        Customer = "Customer",
        TaskType = "General",
        AssignedGroupId = 10,
        AssignedUserId = 99,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        CreatedByAccountName = "TEST\\admin",
        CreatedByDisplayName = "Admin",
        UpdatedByAccountName = "TEST\\admin",
        UpdatedByDisplayName = "Admin"
    };

    private sealed class WorkflowFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private WorkflowFixture(SqliteConnection connection, QualityAssuranceDbContext db)
        {
            this.connection = connection;
            Db = db;
            Directory = new TestAccessStore();
            Assignments = new QualityAssignmentService(db, Directory);
            var legacyAssignments = new QualityLegacyAssignmentReconciler(
                db,
                Directory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityLegacyAssignmentReconciler>.Instance);
            Shipments = new QualityShipmentService(db, Directory, Assignments, legacyAssignments);
            Admin = new QualityAssuranceAccessProfile(
                99,
                "TEST\\admin",
                "Quality Admin",
                ApplicationRoles.Admin,
                [.. QualityAssurancePermissions.AdministratorDefaults, QualityAssurancePermissions.AssignmentEligible],
                [new QualityAssuranceAccessGroup(10, "Quality")]);
        }

        public QualityAssuranceDbContext Db { get; }
        public TestAccessStore Directory { get; }
        public QualityAssignmentService Assignments { get; }
        public QualityShipmentService Shipments { get; }
        public QualityAssuranceAccessProfile Admin { get; }

        public static async Task<WorkflowFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new WorkflowFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestAccessStore : IQualityAssuranceAccessStore
    {
        private readonly IReadOnlyList<QualityDirectoryGroup> groups =
        [
            new QualityDirectoryGroup(10, "Quality", "Quality group", 3),
            new QualityDirectoryGroup(20, "Shipping", "Shipping group", 0)
        ];
        private readonly IReadOnlyList<QualityDirectoryUser> users =
        [
            new QualityDirectoryUser(1, "TEST\\one", "Person One", [10]),
            new QualityDirectoryUser(2, "TEST\\two", "Person Two", [10]),
            new QualityDirectoryUser(99, "TEST\\admin", "Quality Admin", [10])
        ];

        public Task<QualityAssuranceAccessProfile?> FindAccessAsync(string accountName, CancellationToken cancellationToken = default) =>
            Task.FromResult<QualityAssuranceAccessProfile?>(null);

        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(groups);

        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsWithPermissionAsync(
            string permissionKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(groups);

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(int? groupId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QualityDirectoryUser>>(groupId.HasValue
                ? users.Where(user => user.GroupIds.Contains(groupId.Value)).ToList()
                : users);

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersWithPermissionAsync(
            string permissionKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(users);
    }
}

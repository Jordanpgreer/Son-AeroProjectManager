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

        var open = await fixture.Shipments.ListAsync(fixture.Admin, "open", "mine", "oldest", null, CancellationToken.None);
        var past = await fixture.Shipments.ListAsync(fixture.Admin, "shipped", "mine", "oldest", null, CancellationToken.None);
        var audit = await fixture.Shipments.AuditAsync(created.Id, fixture.Admin, CancellationToken.None);

        Assert.Empty(open.Items);
        Assert.Single(past.Items);
        Assert.Contains(audit!, entry => entry.EventType == "Shipped");
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
            "WIP", "SO-200", null, "PN-200", null, "Customer A", "General",
            null, null, null, null, null, null, null), fixture.Admin, CancellationToken.None);

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

        var result = await fixture.Shipments.ListAsync(viewer, "open", "mine", "oldest", null, CancellationToken.None);

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
            fixture.Admin, "open", "mine", "oldest", null, CancellationToken.None);

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
        var mine = await fixture.Shipments.ListAsync(manager, "open", "mine", "oldest", null, default);

        Assert.Contains(dashboard.Queue, shipment => shipment.SalesOrderNumber == "SO-UNASSIGNED");
        Assert.Contains(mine.Items, shipment => shipment.SalesOrderNumber == "SO-UNASSIGNED");
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
            Shipments = new QualityShipmentService(db, Directory, Assignments);
            Admin = new QualityAssuranceAccessProfile(
                99,
                "TEST\\admin",
                "Quality Admin",
                ApplicationRoles.Admin,
                QualityAssurancePermissions.AdministratorDefaults,
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
            [new QualityDirectoryGroup(10, "Quality", "Quality group", 3)];
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

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(int? groupId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QualityDirectoryUser>>(groupId.HasValue
                ? users.Where(user => user.GroupIds.Contains(groupId.Value)).ToList()
                : users);
    }
}

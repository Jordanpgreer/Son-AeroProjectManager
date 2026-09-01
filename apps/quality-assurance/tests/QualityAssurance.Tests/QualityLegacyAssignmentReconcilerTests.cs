using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityLegacyAssignmentReconcilerTests
{
    [Fact]
    public async Task UnknownQaTagIsCleanedThenPromotedWhenAnEligibleFirstNameAppears()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new QualityAssuranceDbContext(
            new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var directory = new MutableAccessStore();
        var reconciler = new QualityLegacyAssignmentReconciler(
            db,
            directory,
            NullLogger<QualityLegacyAssignmentReconciler>.Instance);
        var shipment = Shipment("QA-rOrIe");
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();

        Assert.Equal(1, await reconciler.ReconcileAsync());
        Assert.Equal("rOrIe", shipment.NextAction);
        Assert.Equal("rOrIe", shipment.LegacyAssigneeTag);
        Assert.Null(shipment.AssignedUserId);

        directory.Users =
        [
            new QualityDirectoryUser(42, "TEST\\rorie", "Rorie Smith", [10])
        ];
        Assert.Equal(1, await reconciler.ReconcileAsync());

        Assert.Equal(42, shipment.AssignedUserId);
        Assert.Equal(10, shipment.AssignedGroupId);
        Assert.Equal("Rorie Smith", shipment.AssignedDisplayName);
        Assert.Equal("Rorie Smith", shipment.NextAction);
        Assert.Null(shipment.LegacyAssigneeTag);
        Assert.Contains(shipment.AuditEntries, entry => entry.EventType == "LegacyAssignmentNormalized");
        Assert.Contains(shipment.AuditEntries, entry => entry.EventType == "LegacyAssignmentPromoted");
    }

    [Fact]
    public async Task DuplicateFirstNamesRemainPlainTagsInsteadOfGuessing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new QualityAssuranceDbContext(
            new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var directory = new MutableAccessStore
        {
            Users =
            [
                new QualityDirectoryUser(1, "TEST\\julia-one", "Julia One", [10]),
                new QualityDirectoryUser(2, "TEST\\julia-two", "Julia Two", [10])
            ]
        };
        var reconciler = new QualityLegacyAssignmentReconciler(
            db,
            directory,
            NullLogger<QualityLegacyAssignmentReconciler>.Instance);
        var shipment = Shipment("QA-Julia");
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();

        Assert.Equal(1, await reconciler.ReconcileAsync());

        Assert.Equal("Julia", shipment.NextAction);
        Assert.Equal("Julia", shipment.LegacyAssigneeTag);
        Assert.Null(shipment.AssignedUserId);
    }

    private static QualityShipment Shipment(string nextAction) => new()
    {
        SalesOrderNumber = "SO-LEGACY-TAG",
        PartNumber = "PN-LEGACY-TAG",
        Customer = "Customer",
        TaskType = "General",
        NextAction = nextAction,
        CreatedByAccountName = "TEST\\import",
        CreatedByDisplayName = "Import",
        UpdatedByAccountName = "TEST\\import",
        UpdatedByDisplayName = "Import",
    };

    private sealed class MutableAccessStore : IQualityAssuranceAccessStore
    {
        public IReadOnlyList<QualityDirectoryGroup> Groups { get; set; } =
            [new QualityDirectoryGroup(10, "Quality", null, 2)];
        public IReadOnlyList<QualityDirectoryUser> Users { get; set; } = [];

        public Task<QualityAssuranceAccessProfile?> FindAccessAsync(
            string accountName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QualityAssuranceAccessProfile?>(null);

        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Groups);

        public Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsWithPermissionAsync(
            string permissionKey,
            CancellationToken cancellationToken = default) => Task.FromResult(Groups);

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(
            int? groupId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QualityDirectoryUser>>(groupId.HasValue
                ? Users.Where(user => user.GroupIds.Contains(groupId.Value)).ToList()
                : Users);

        public Task<IReadOnlyList<QualityDirectoryUser>> GetUsersWithPermissionAsync(
            string permissionKey,
            CancellationToken cancellationToken = default) => Task.FromResult(Users);
    }
}

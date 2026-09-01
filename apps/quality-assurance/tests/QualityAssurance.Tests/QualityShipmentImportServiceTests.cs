using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityShipmentImportServiceTests
{
    [Fact]
    public void ImportPermissionDefaultsToAdministratorsOnly()
    {
        Assert.Contains(QualityAssurancePermissions.ShipmentImport, QualityAssurancePermissions.AdministratorDefaults);
        Assert.DoesNotContain(QualityAssurancePermissions.ShipmentImport, QualityAssurancePermissions.EditorDefaults);
        Assert.DoesNotContain(QualityAssurancePermissions.ShipmentImport, QualityAssurancePermissions.ViewerDefaults);
    }

    [Fact]
    public async Task CompleteListImportMapsRowsAndRepeatUploadSkipsExactDuplicates()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var workbook = Workbook([
            ["Pending Source Inspection", "2195-3", new DateTime(2026, 7, 15), "330602-019", "MP00474070", "NORTHROP - SPACE", 1m, 3260.05m, new DateTime(2026, 8, 20), "ATS submitted", new DateTime(2026, 8, 12), "QA-ADRIAN", new DateTime(2026, 8, 11), "SN 0000241"],
            ["Pending Source Inspection", "2195-3", new DateTime(2026, 7, 21), "330602-019", "MP00474070", "NORTHROP - SPACE", 1m, 3260.05m, new DateTime(2026, 8, 20), "ATS submitted", new DateTime(2026, 8, 12), "QA-ADRIAN", new DateTime(2026, 8, 12), "SN 0000244"]
        ]);

        await using var firstStream = new MemoryStream(workbook);
        var first = await fixture.Importer.ImportAsync(firstStream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(2, first.RowsRead);
        Assert.Equal(2, first.CreatedRecords);
        Assert.Equal(0, first.SkippedDuplicates);
        var saved = await fixture.Db.Shipments.Include(shipment => shipment.AuditEntries).OrderBy(shipment => shipment.QaArrivalDate).ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, shipment => Assert.Equal("2195-3", shipment.SalesOrderNumber));
        Assert.Equal(["SN 0000241", "SN 0000244"], saved.Select(shipment => shipment.Comments));
        Assert.Equal(new DateOnly(2026, 7, 15), saved[0].QaArrivalDate);
        Assert.Equal(new DateOnly(2026, 8, 20), saved[0].ShipDate);
        Assert.Equal(3260.05m, saved[0].DollarValue);
        Assert.Equal("General", saved[0].TaskType);
        Assert.Null(saved[0].AssignedGroupId);
        Assert.Null(saved[0].AssignedUserId);
        Assert.Equal("ADRIAN", saved[0].NextAction);
        Assert.Equal("ADRIAN", saved[0].LegacyAssigneeTag);
        Assert.Equal(new DateTime(2026, 8, 11), saved[0].LastWorkedAt?.UtcDateTime);
        Assert.Contains(saved[0].AuditEntries, entry =>
            entry.EventType == "Imported" && entry.NewValue == "shipping.xlsx / Complete List row 2");
        Assert.Contains(saved[0].AuditEntries, entry => entry.EventType == "AssignmentPending");

        await using var secondStream = new MemoryStream(workbook);
        var second = await fixture.Importer.ImportAsync(secondStream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(0, second.CreatedRecords);
        Assert.Equal(2, second.SkippedDuplicates);
        Assert.Equal(0, second.ReconciledAssignments);
        Assert.Equal(2, await fixture.Db.Shipments.CountAsync());
    }

    [Fact]
    public async Task ImportRequiresCompleteListAndAllExpectedHeaders()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Pending Shipments");
        sheet.Cell(1, 1).Value = "Sales Order#";
        await using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Importer.ImportAsync(stream, "wrong.xlsx", fixture.Admin, default));

        Assert.Contains("Complete List", error.Message);
        Assert.Empty(fixture.Db.Shipments);
    }

    [Fact]
    public async Task ImportAcceptsRecognizedLegacyQuantityAndCurrencyFormats()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var workbook = Workbook([
            ["WIP", "SO3105-1", new DateTime(2026, 7, 20), "MFG2-5226", "3506847477", "HONEYWELL", "7 KIT", "4410-63", new DateTime(2026, 8, 10), null, null, "QA-ALICIA", null, "JOB 8925"]
        ]);

        await using var stream = new MemoryStream(workbook);
        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(1, result.CreatedRecords);
        var saved = await fixture.Db.Shipments.SingleAsync();
        Assert.Equal(7m, saved.Quantity);
        Assert.Equal(4410.63m, saved.DollarValue);
    }

    [Fact]
    public async Task ImportAssignsActionOwnerToTheirRegisteredWorkGroup()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            [
                new QualityDirectoryGroup(9, "Administrators", null, 1),
                new QualityDirectoryGroup(10, "Quality", null, 1),
            ],
            [new QualityDirectoryUser(7, "SON-AERO\\alicia", "Alicia Chavez", [9, 10])]);
        var workbook = Workbook([
            ["WIP", "SO3105-1", null, "MFG2-5226", null, "HONEYWELL", 7m, 23783.83m, null, null, null, "QA-ALICIA", null, "JOB 8925"]
        ]);

        await using var stream = new MemoryStream(workbook);
        await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        var saved = await fixture.Db.Shipments.Include(shipment => shipment.AuditEntries).SingleAsync();
        Assert.Equal(10, saved.AssignedGroupId);
        Assert.Equal("Quality", saved.AssignedGroupName);
        Assert.Equal(7, saved.AssignedUserId);
        Assert.Equal("Alicia Chavez", saved.AssignedDisplayName);
        Assert.Equal("Alicia Chavez", saved.NextAction);
        Assert.Contains(saved.AuditEntries, entry =>
            entry.EventType == "AutoAssigned" && entry.NewValue == "Quality / Alicia Chavez");
    }

    [Fact]
    public async Task RepeatImportResolvesPendingActionAfterUserIsRegistered()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            [new QualityDirectoryGroup(10, "Quality", null, 1)],
            [new QualityDirectoryUser(7, "SON-AERO\\julia", "Julia Santos", [10])]);
        var pending = ImportedShipment("SO-JULIA", "QA-JULIA");
        pending.AuditEntries.Add(new QualityAssurance.Api.Models.QualityShipmentAuditEntry
        {
            EventType = "AssignmentPending",
            FieldName = "assignment",
            NewValue = "Unassigned",
            AccountName = fixture.Admin.AccountName,
            DisplayName = fixture.Admin.DisplayName,
        });
        fixture.Db.Shipments.Add(pending);
        await fixture.Db.SaveChangesAsync();
        var workbook = Workbook([
            ["WIP", "SO-JULIA", null, "PN-IMPORT", null, "HONEYWELL", null, null, null, null, null, "QA-JULIA", null, null]
        ]);

        await using var stream = new MemoryStream(workbook);
        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(0, result.CreatedRecords);
        Assert.Equal(1, result.ReconciledAssignments);
        Assert.Equal(7, pending.AssignedUserId);
        Assert.Equal(10, pending.AssignedGroupId);
        Assert.Equal("Julia Santos", pending.NextAction);
        Assert.Contains(pending.AuditEntries, entry =>
            entry.EventType == "UpdatedByImport" && entry.FieldName == "nextAction" && entry.NewValue == "Julia Santos");
    }

    [Fact]
    public async Task RepeatImportMassUpdatesActionWithoutCreatingDuplicate()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var pending = ImportedShipment("SO-ACTION-EDIT", "QA-OLD");
        pending.AuditEntries.Add(new QualityAssurance.Api.Models.QualityShipmentAuditEntry
        {
            EventType = "AssignmentPending",
            FieldName = "assignment",
            NewValue = "Unassigned",
            AccountName = fixture.Admin.AccountName,
            DisplayName = fixture.Admin.DisplayName,
        });
        fixture.Db.Shipments.Add(pending);
        await fixture.Db.SaveChangesAsync();
        var workbook = Workbook([
            ["WIP", "SO-ACTION-EDIT", null, "PN-IMPORT", null, "HONEYWELL", null, null, null, null, null, "QA-NEW", null, null]
        ]);

        await using var stream = new MemoryStream(workbook);
        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(0, result.CreatedRecords);
        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(1, result.ReconciledAssignments);
        Assert.Equal("NEW", pending.NextAction);
        Assert.Equal("NEW", pending.LegacyAssigneeTag);
        Assert.Equal(1, await fixture.Db.Shipments.CountAsync());
    }

    [Fact]
    public async Task RepeatImportReconcilesOnlyLegacyImportedAssignments()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var legacy = new QualityAssurance.Api.Models.QualityShipment
        {
            Status = "WIP",
            SalesOrderNumber = "SO-LEGACY",
            PartNumber = "PN-LEGACY",
            Customer = "HONEYWELL",
            TaskType = "General",
            NextAction = "QA-UNKNOWN",
            AssignedGroupId = 9,
            AssignedGroupName = "Administrators",
            AssignedUserId = fixture.Admin.UserId,
            AssignedAccountName = fixture.Admin.AccountName,
            AssignedDisplayName = fixture.Admin.DisplayName,
            CreatedByAccountName = fixture.Admin.AccountName,
            CreatedByDisplayName = fixture.Admin.DisplayName,
            UpdatedByAccountName = fixture.Admin.AccountName,
            UpdatedByDisplayName = fixture.Admin.DisplayName,
        };
        legacy.AuditEntries.Add(new QualityAssurance.Api.Models.QualityShipmentAuditEntry
        {
            EventType = "Imported",
            NewValue = "shipping.xlsx / Complete List row 2",
            AccountName = fixture.Admin.AccountName,
            DisplayName = fixture.Admin.DisplayName,
        });
        fixture.Db.Shipments.Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var workbook = Workbook([
            ["WIP", "SO-LEGACY", null, "PN-LEGACY", null, "HONEYWELL", null, null, null, null, null, "QA-UNKNOWN", null, null]
        ]);

        await using var stream = new MemoryStream(workbook);
        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(0, result.CreatedRecords);
        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(1, result.ReconciledAssignments);
        Assert.Null(legacy.AssignedGroupId);
        Assert.Null(legacy.AssignedUserId);
        Assert.Contains(legacy.AuditEntries, entry =>
            entry.EventType == "AssignmentPending" && entry.OldValue == "Administrators / Quality Admin");
    }

    private static QualityAssurance.Api.Models.QualityShipment ImportedShipment(string salesOrder, string action)
    {
        var shipment = new QualityAssurance.Api.Models.QualityShipment
        {
            Status = "WIP",
            SalesOrderNumber = salesOrder,
            PartNumber = "PN-IMPORT",
            Customer = "HONEYWELL",
            TaskType = "General",
            NextAction = action,
            CreatedByAccountName = "TEST\\admin",
            CreatedByDisplayName = "Quality Admin",
            UpdatedByAccountName = "TEST\\admin",
            UpdatedByDisplayName = "Quality Admin",
        };
        shipment.AuditEntries.Add(new QualityAssurance.Api.Models.QualityShipmentAuditEntry
        {
            EventType = "Imported",
            NewValue = "shipping.xlsx / Complete List row 2",
            AccountName = "TEST\\admin",
            DisplayName = "Quality Admin",
        });
        return shipment;
    }

    private static byte[] Workbook(IReadOnlyList<object?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Complete List");
        var headers = new[]
        {
            "Status:", "Sales Order#", "QA Arrival Date", "Part Number:", "P.O.", "Customer:",
            "Quantity:", "Dollar Value:", "Ship Date:", "Hold Reason:", "When Was Source Requested:",
            "Action:", "Date Last Worked On:", "COMMENTS:"
        };
        for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                var cell = sheet.Cell(row + 2, column + 1);
                var value = rows[row][column];
                if (value is DateTime date) cell.Value = date;
                else if (value is decimal number) cell.Value = number;
                else if (value is not null) cell.Value = value.ToString();
            }
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private sealed class ImportFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ImportFixture(
            SqliteConnection connection,
            QualityAssuranceDbContext db,
            TestAccessStore accessStore)
        {
            this.connection = connection;
            Db = db;
            Importer = new QualityShipmentImportService(db, accessStore);
            Admin = new QualityAssuranceAccessProfile(
                99,
                "TEST\\admin",
                "Quality Admin",
                ApplicationRoles.Admin,
                QualityAssurancePermissions.AdministratorDefaults,
                [new QualityAssuranceAccessGroup(10, "Quality")]);
        }

        public QualityAssuranceDbContext Db { get; }
        public QualityShipmentImportService Importer { get; }
        public QualityAssuranceAccessProfile Admin { get; }

        public static async Task<ImportFixture> CreateAsync(
            IReadOnlyList<QualityDirectoryGroup>? groups = null,
            IReadOnlyList<QualityDirectoryUser>? users = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new ImportFixture(connection, db, new TestAccessStore(groups, users));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestAccessStore : IQualityAssuranceAccessStore
    {
        private readonly IReadOnlyList<QualityDirectoryGroup> groups;
        private readonly IReadOnlyList<QualityDirectoryUser> users;

        public TestAccessStore(
            IReadOnlyList<QualityDirectoryGroup>? groups = null,
            IReadOnlyList<QualityDirectoryUser>? users = null)
        {
            this.groups = groups ?? [new QualityDirectoryGroup(10, "Quality", null, 1)];
            this.users = users ?? [];
        }

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

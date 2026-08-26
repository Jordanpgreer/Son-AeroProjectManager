using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Endpoints;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonAero.Platform.Engineering;
using SonAero.Platform.Security;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class ToolingControlTests
{
    [Fact]
    public async Task ToolingSchema_BackfillsDocumentDateForExistingDatabases()
    {
        var path = Path.Combine(Path.GetTempPath(), $"engineering-tooling-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "ToolDocuments" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "ToolRecordId" INTEGER NOT NULL,
                        "UploadedAt" TEXT NOT NULL
                    );
                    INSERT INTO "ToolDocuments" ("ToolRecordId", "UploadedAt")
                    VALUES (42, '2026-08-18T13:45:00');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<EngineeringDbContext>().UseSqlite($"Data Source={path}").Options;
            await using var db = new EngineeringDbContext(options);
            await new ToolingSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            await db.Database.OpenConnectionAsync();
            await using var verify = db.Database.GetDbConnection().CreateCommand();
            verify.CommandText = "SELECT \"DocumentDate\" FROM \"ToolDocuments\" WHERE \"Id\" = 1;";
            Assert.Equal("2026-08-18T13:45:00", await verify.ExecuteScalarAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ToolingSchema_BackfillsDefaultCheckinLocationForExistingTools()
    {
        var path = Path.Combine(Path.GetTempPath(), $"engineering-tool-home-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "ToolLocations" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "Code" TEXT NOT NULL,
                        "NormalizedCode" TEXT NOT NULL,
                        "Description" TEXT NULL,
                        "IsActive" INTEGER NOT NULL,
                        "CreatedBy" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL
                    );
                    CREATE TABLE "Tools" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "ToolNumber" TEXT NOT NULL,
                        "NormalizedToolNumber" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "ToolType" TEXT NOT NULL,
                        "Owner" TEXT NOT NULL,
                        "Description" TEXT NULL,
                        "Notes" TEXT NULL,
                        "IsArchived" INTEGER NOT NULL DEFAULT 0,
                        "CustodyStatus" TEXT NOT NULL,
                        "CurrentLocationId" INTEGER NULL,
                        "CurrentHolder" TEXT NULL,
                        "CurrentVendor" TEXT NULL,
                        "CheckedOutAt" TEXT NULL,
                        "LastAuditDate" TEXT NULL,
                        "LastAuditBy" TEXT NULL,
                        "CreatedBy" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedBy" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL,
                        "Version" INTEGER NOT NULL DEFAULT 0,
                        FOREIGN KEY ("CurrentLocationId") REFERENCES "ToolLocations" ("Id") ON DELETE RESTRICT
                    );
                    INSERT INTO "ToolLocations" ("Code", "NormalizedCode", "IsActive", "CreatedBy", "CreatedAt")
                    VALUES ('A001-002', 'A001002', 1, 'TEST\\Admin', '2026-08-18T13:45:00');
                    INSERT INTO "Tools" (
                        "ToolNumber", "NormalizedToolNumber", "Name", "ToolType", "Owner", "CustodyStatus",
                        "CurrentLocationId", "CreatedBy", "CreatedAt", "UpdatedBy", "UpdatedAt")
                    VALUES (
                        'TL-900', 'TL900', 'Legacy Fixture', 'Fixture', 'Son-Aero', 'InStorage',
                        1, 'TEST\\Admin', '2026-08-18T13:45:00', 'TEST\\Admin', '2026-08-18T13:45:00');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<EngineeringDbContext>().UseSqlite($"Data Source={path}").Options;
            await using var db = new EngineeringDbContext(options);
            await new ToolingSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            await db.Database.OpenConnectionAsync();
            await using var verify = db.Database.GetDbConnection().CreateCommand();
            verify.CommandText = "SELECT printf('%s|%s|%s', \"tool\".\"Id\", \"tool\".\"CurrentLocationId\", \"home\".\"LocationId\") FROM \"Tools\" AS \"tool\" LEFT JOIN \"ToolHomeLocations\" AS \"home\" ON \"home\".\"ToolRecordId\" = \"tool\".\"Id\";";
            Assert.Equal("1|1|1", await verify.ExecuteScalarAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ToolingSchema_PersistsLocationsToolsAndAppendOnlyHistory()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = new ToolLocation
        {
            Code = "A001-002", NormalizedCode = "A001002", Description = "Shared bin",
            CreatedBy = "TEST\\Admin", CreatedAt = DateTime.UtcNow
        };
        var tool = CreateTool(location);
        tool.Movements.Add(new ToolMovement
        {
            Type = ToolMovementType.Registered, Location = location, LocationCode = location.Code,
            Person = "TEST\\Admin", SignedOffBy = "TEST\\Admin", RecordedAt = DateTime.UtcNow
        });
        tool.AuditEntries.Add(new ToolAuditEntry
        {
            Tool = tool, Action = "ToolCreated", Details = "Created test tool.", Actor = "TEST\\Admin", OccurredAt = DateTime.UtcNow
        });
        fixture.Db.Tools.Add(tool);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal("A001-002", (await fixture.Db.Tools.Include(x => x.CurrentLocation).SingleAsync()).CurrentLocation!.Code);
        Assert.Equal("A001-002", (await fixture.Db.Tools.Include(x => x.HomeLocationAssignment).ThenInclude(x => x!.Location).SingleAsync()).HomeLocationAssignment!.Location.Code);
        var movement = await fixture.Db.ToolMovements.SingleAsync();
        movement.Purpose = "Attempted rewrite";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolArchiveWorkflow_BlocksActiveCustodyAndSupportsArchiveCheckoutGuardAndRestore()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = CreateLocation();
        var storedTool = CreateTool(location);
        var checkedOutTool = CreateTool(location, "TL-OUT", "PN-OUT");
        checkedOutTool.CustodyStatus = ToolCustodyStatus.CheckedOut;
        checkedOutTool.CurrentHolder = "TEST\\Operator";
        checkedOutTool.CheckedOutAt = DateTime.UtcNow;
        fixture.Db.Tools.AddRange(storedTool, checkedOutTool);
        await fixture.Db.SaveChangesAsync();

        await using var app = CreateToolingApp(fixture.Db);
        var custodyConflict = await InvokeJsonAsync(
            app, "PUT", "/api/tools/{id:int}/archive", checkedOutTool.Id,
            new ToolArchiveStatusDto(true, checkedOutTool.Version));
        Assert.Equal(StatusCodes.Status409Conflict, custodyConflict.StatusCode);

        var editBypass = await InvokeJsonAsync(
            app, "PUT", "/api/tools/{id:int}", storedTool.Id,
            new ToolUpsertDto(
                storedTool.ToolNumber, storedTool.Name, storedTool.ToolType, storedTool.Owner,
                storedTool.Description, storedTool.Notes, location.Id,
                storedTool.PartNumbers.Select(part => part.PartNumber).ToArray(), true, storedTool.Version));
        Assert.Equal(StatusCodes.Status400BadRequest, editBypass.StatusCode);
        Assert.Contains("manager-controlled archive or restore action", editBypass.Body, StringComparison.OrdinalIgnoreCase);

        var archivedResponse = await InvokeJsonAsync(
            app, "PUT", "/api/tools/{id:int}/archive", storedTool.Id,
            new ToolArchiveStatusDto(true, storedTool.Version));
        Assert.Equal(StatusCodes.Status200OK, archivedResponse.StatusCode);

        fixture.Db.ChangeTracker.Clear();
        var archived = await fixture.Db.Tools.Include(tool => tool.AuditEntries)
            .SingleAsync(tool => tool.Id == storedTool.Id);
        Assert.True(archived.IsArchived);
        Assert.Contains(archived.AuditEntries, entry => entry.Action == "ToolArchived");

        var checkoutResponse = await InvokeJsonAsync(
            app, "POST", "/api/tools/{id:int}/checkout", archived.Id,
            new ToolCheckoutDto("location", location.Id, null, "TEST\\Operator", "Test release", true, null));
        Assert.Equal(StatusCodes.Status409Conflict, checkoutResponse.StatusCode);
        Assert.Contains("Archived tools cannot be checked out", checkoutResponse.Body, StringComparison.OrdinalIgnoreCase);

        var restoredResponse = await InvokeJsonAsync(
            app, "PUT", "/api/tools/{id:int}/archive", archived.Id,
            new ToolArchiveStatusDto(false, archived.Version));
        Assert.Equal(StatusCodes.Status200OK, restoredResponse.StatusCode);

        fixture.Db.ChangeTracker.Clear();
        var restored = await fixture.Db.Tools.Include(tool => tool.AuditEntries)
            .SingleAsync(tool => tool.Id == storedTool.Id);
        Assert.False(restored.IsArchived);
        Assert.Contains(restored.AuditEntries, entry => entry.Action == "ToolRestored");
    }

    [Fact]
    public async Task ToolDocuments_AreStoredInReservedFolderAndRemainAppendOnly()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = new ToolLocation
        {
            Code = "B004", NormalizedCode = "B004", CreatedBy = "TEST\\Admin", CreatedAt = DateTime.UtcNow
        };
        var tool = CreateTool(location);
        fixture.Db.Tools.Add(tool);
        await fixture.Db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), $"engineering-tool-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new DrawingFileStore(Options.Create(new DrawingStorageOptions { RootPath = root, RequireUncPath = false }));
            var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 tooling shipping document");
            await using var stream = new MemoryStream(bytes);
            var upload = new FormFile(stream, 0, bytes.Length, "document", "packing-slip.pdf")
            {
                Headers = new HeaderDictionary(), ContentType = "application/pdf"
            };
            var stored = await store.StoreToolDocumentAsync(tool.Id, tool.ToolNumber, "Shipping", upload, CancellationToken.None);
            Assert.StartsWith($".tooling{Path.DirectorySeparatorChar}", stored.RelativePath);
            Assert.Empty(EngineeringStoragePolicy.EnumerateAuthorities(root));
            Assert.True(File.Exists(await store.ResolvePathAsync(stored.RelativePath, CancellationToken.None)));

            tool.Documents.Add(new ToolDocument
            {
                Kind = ToolDocumentKind.Shipping, OriginalFileName = upload.FileName,
                StoredFilePath = stored.RelativePath, FileType = upload.ContentType, FileSize = bytes.Length,
                FileHash = stored.Hash, DocumentDate = new DateTime(2026, 8, 19),
                UploadedBy = "TEST\\Admin", UploadedAt = DateTime.UtcNow
            });
            await fixture.Db.SaveChangesAsync();
            var document = await fixture.Db.ToolDocuments.SingleAsync();
            Assert.Equal(new DateTime(2026, 8, 19), document.DocumentDate);
            document.Notes = "Attempted rewrite";
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EngineeringSearch_UsesLiveToolRecordsAndSearchesOwnerLocationAndNotes()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = new ToolLocation
        {
            Code = "CELL-09", NormalizedCode = "CELL09", Description = "Bonding area",
            CreatedBy = "TEST\\Admin", CreatedAt = DateTime.UtcNow
        };
        fixture.Db.Tools.Add(WithNotes(CreateTool(location), "Unique searchable calibration note"));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new EngineeringSearchService(fixture.Db).GetDashboardAsync(
            "calibration", "tools", null, null, false,
            canViewPending: true, canViewSpecifications: true, canViewSupportingDocuments: true,
            canViewMylar: true, canViewTooling: true, canViewCompoundData: true, CancellationToken.None);

        var result = Assert.Single(dashboard.Results);
        Assert.Equal("TL-900", result.Identifier);
        Assert.Equal("Test Customer", result.Customer);
        Assert.NotNull(result.ToolId);
    }

    [Fact]
    public async Task EngineeringSearch_FindsACheckedOutToolByItsDefaultBin()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = new ToolLocation
        {
            Code = "HOME-BIN-17", NormalizedCode = "HOMEBIN17", Description = "Normal return location",
            CreatedBy = "TEST\\Admin", CreatedAt = DateTime.UtcNow
        };
        var tool = CreateTool(location);
        tool.CustodyStatus = ToolCustodyStatus.OutsideProcessing;
        tool.CurrentLocation = null;
        tool.CurrentVendor = "Test Processing Vendor";
        fixture.Db.Tools.Add(tool);
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new EngineeringSearchService(fixture.Db).GetDashboardAsync(
            "HOME-BIN-17", "tools", null, null, false,
            canViewPending: true, canViewSpecifications: true, canViewSupportingDocuments: true,
            canViewMylar: true, canViewTooling: true, canViewCompoundData: true, CancellationToken.None);

        Assert.Equal("TL-900", Assert.Single(dashboard.Results).Identifier);
    }

    [Fact]
    public async Task EngineeringSearch_FindsToolByAssociatedPartNumber()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        fixture.Db.Tools.Add(CreateTool(CreateLocation(), partNumber: "PN-SPECIAL-447"));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await new EngineeringSearchService(fixture.Db).GetDashboardAsync(
            "PN-SPECIAL-447", "tools", null, null, false,
            canViewPending: true, canViewSpecifications: true, canViewSupportingDocuments: true,
            canViewMylar: true, canViewTooling: true, canViewCompoundData: true, CancellationToken.None);

        Assert.Equal("TL-900", Assert.Single(dashboard.Results).Identifier);
    }

    [Fact]
    public async Task ToolCatalogExport_ContainsCurrentCatalogueAndEditableAuditColumns()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = CreateLocation();
        fixture.Db.Tools.Add(CreateTool(location));
        await fixture.Db.SaveChangesAsync();

        var service = new ToolCatalogWorkbookService(new ToolCatalogReviewStore());
        var bytes = await service.ExportAsync(fixture.Db);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet(ToolCatalogWorkbookService.SheetName);
        Assert.Equal("Record ID", sheet.Cell(1, 1).GetString());
        Assert.Equal("New Audit Date", sheet.Cell(1, 11).GetString());
        Assert.Equal("Part Numbers (Required)", sheet.Cell(1, 12).GetString());
        Assert.Equal("TL-900", sheet.Cell(2, 2).GetString());
        Assert.Equal("In Storage", sheet.Cell(2, 6).GetString());
        Assert.Equal("A001-002", sheet.Cell(2, 7).GetString());
        Assert.Equal("PN-900", sheet.Cell(2, 12).GetString());
        Assert.NotNull(workbook.Worksheet("Instructions"));
    }

    [Fact]
    public async Task ToolCatalogValidation_HighlightsAndCommentsOnInvalidCells()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        fixture.Db.Tools.Add(CreateTool(CreateLocation()));
        await fixture.Db.SaveChangesAsync();
        var service = new ToolCatalogWorkbookService(new ToolCatalogReviewStore());
        var bytes = await service.ExportAsync(fixture.Db);
        using (var workbook = new XLWorkbook(new MemoryStream(bytes)))
        {
            var sheet = workbook.Worksheet(ToolCatalogWorkbookService.SheetName);
            sheet.Cell(2, 5).Clear();
            sheet.Cell(2, 11).Value = DateTime.UtcNow.Date.AddDays(1);
            bytes = SaveWorkbook(workbook);
        }

        var review = await service.ValidateAsync(fixture.Db, bytes, "edited.xlsx", "TEST\\Admin");

        Assert.Equal(1, review.ErrorRows);
        Assert.Contains(review.Errors, issue => issue.Column == "Owner (Required)");
        Assert.Contains(review.Errors, issue => issue.Column == "New Audit Date");
        using var annotated = new XLWorkbook(new MemoryStream(service.BuildReviewWorkbook(review.ReviewId, "TEST\\Admin")));
        var annotatedSheet = annotated.Worksheet(ToolCatalogWorkbookService.SheetName);
        Assert.True(annotatedSheet.Cell(2, 5).HasComment);
        Assert.Contains("Owner is required", annotatedSheet.Cell(2, 5).GetComment().Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(XLColor.NoColor, annotatedSheet.Cell(2, 11).Style.Fill.BackgroundColor);
    }

    [Fact]
    public async Task ToolCatalogApply_UpdatesExistingAndAddsNewToolsWithoutReplacementSemantics()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        var location = CreateLocation();
        fixture.Db.Tools.Add(CreateTool(location));
        fixture.Db.Tools.Add(CreateTool(location, "TL-KEEP", "PN-KEEP"));
        await fixture.Db.SaveChangesAsync();
        var service = new ToolCatalogWorkbookService(new ToolCatalogReviewStore());
        var bytes = await service.ExportAsync(fixture.Db);
        using (var workbook = new XLWorkbook(new MemoryStream(bytes)))
        {
            var sheet = workbook.Worksheet(ToolCatalogWorkbookService.SheetName);
            var keepRow = sheet.RowsUsed().Single(row => row.Cell(2).GetString() == "TL-KEEP").RowNumber();
            sheet.Row(keepRow).Delete();
            var existingRow = sheet.RowsUsed().Single(row => row.Cell(2).GetString() == "TL-900").RowNumber();
            sheet.Cell(existingRow, 5).Value = "Updated Customer";
            sheet.Cell(existingRow, 11).Value = new DateTime(2026, 8, 20);
            sheet.Cell(existingRow, 12).Value = "PN-900; PN-901-A";
            var newRow = sheet.LastRowUsed()!.RowNumber() + 1;
            sheet.Cell(newRow, 2).Value = "TL-NEW";
            sheet.Cell(newRow, 5).Value = "Son-Aero";
            sheet.Cell(newRow, 6).Value = "In Storage";
            sheet.Cell(newRow, 7).Value = "A001-002";
            sheet.Cell(newRow, 8).Value = "A001-002";
            sheet.Cell(newRow, 12).Value = "PN-NEW-1";
            bytes = SaveWorkbook(workbook);
        }

        var review = await service.ValidateAsync(fixture.Db, bytes, "edited.xlsx", "TEST\\Admin");
        Assert.Equal(0, review.ErrorRows);
        Assert.Equal(1, review.NewRecords);
        Assert.Equal(1, review.UpdatedRecords);
        var applied = await service.ApplyAsync(fixture.Db, review.ReviewId, "TEST\\Admin", false);

        Assert.Equal(1, applied.Added);
        Assert.Equal(1, applied.Updated);
        var tools = await fixture.Db.Tools.Include(tool => tool.PartNumbers).Include(tool => tool.AuditEntries).OrderBy(tool => tool.ToolNumber).ToListAsync();
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, tool => tool.ToolNumber == "TL-KEEP");
        var updated = tools.Single(tool => tool.ToolNumber == "TL-900");
        Assert.Equal("Updated Customer", updated.Owner);
        Assert.Equal(new DateTime(2026, 8, 20), updated.LastAuditDate);
        Assert.Equal(["PN-900", "PN-901-A"], updated.PartNumbers.Select(part => part.PartNumber).OrderBy(value => value).ToArray());
        Assert.Contains(updated.AuditEntries, entry => entry.Action == "ToolCatalogImported");
        var created = tools.Single(tool => tool.ToolNumber == "TL-NEW");
        Assert.Equal("TL-NEW", created.Name);
        Assert.Equal("General tool", created.ToolType);
        Assert.Equal("PN-NEW-1", Assert.Single(created.PartNumbers).PartNumber);
    }

    [Fact]
    public async Task ToolCatalogApply_RequiresConfirmationAndSkipsInvalidRowsWhenForced()
    {
        await using var fixture = await ToolingFixture.CreateAsync();
        fixture.Db.Tools.Add(CreateTool(CreateLocation()));
        await fixture.Db.SaveChangesAsync();
        var service = new ToolCatalogWorkbookService(new ToolCatalogReviewStore());
        var bytes = await service.ExportAsync(fixture.Db);
        using (var workbook = new XLWorkbook(new MemoryStream(bytes)))
        {
            var sheet = workbook.Worksheet(ToolCatalogWorkbookService.SheetName);
            sheet.Cell(2, 5).Clear();
            sheet.Cell(3, 2).Value = "TL-VALID-NEW";
            sheet.Cell(3, 5).Value = "Son-Aero";
            sheet.Cell(3, 6).Value = "In Storage";
            sheet.Cell(3, 7).Value = "A001-002";
            sheet.Cell(3, 8).Value = "A001-002";
            sheet.Cell(3, 12).Value = "PN-VALID";
            bytes = SaveWorkbook(workbook);
        }

        var review = await service.ValidateAsync(fixture.Db, bytes, "mixed.xlsx", "TEST\\Admin");
        await Assert.ThrowsAsync<ToolCatalogValidationException>(() =>
            service.ApplyAsync(fixture.Db, review.ReviewId, "TEST\\Admin", false));
        var applied = await service.ApplyAsync(fixture.Db, review.ReviewId, "TEST\\Admin", true);

        Assert.Equal(1, applied.Added);
        Assert.Equal(0, applied.Updated);
        Assert.Equal(1, applied.Skipped);
        Assert.Equal("Test Customer", (await fixture.Db.Tools.SingleAsync(tool => tool.ToolNumber == "TL-900")).Owner);
        Assert.True(await fixture.Db.Tools.AnyAsync(tool => tool.ToolNumber == "TL-VALID-NEW"));
    }

    [Fact]
    public void ToolingPermissions_AreDelegableAndKeepAdministrativeImportsLockedByDefault()
    {
        var managers = EngineeringPermissions.Expand(EngineeringPermissions.DefaultsForGroup("Managers"));
        Assert.Contains(EngineeringPermissions.ToolingArchiveManage, managers);

        var engineering = EngineeringPermissions.Expand(EngineeringPermissions.DefaultsForGroup("Engineering"));
        Assert.Contains(EngineeringPermissions.ToolingRecordsManage, engineering);
        Assert.Contains(EngineeringPermissions.ToolingCustodyManage, engineering);
        Assert.Contains(EngineeringPermissions.ToolingDocumentsManage, engineering);
        Assert.DoesNotContain(EngineeringPermissions.ToolingArchiveManage, engineering);
        Assert.DoesNotContain(EngineeringPermissions.ToolingLocationsManage, engineering);
        Assert.DoesNotContain(EngineeringPermissions.ToolingAuditImport, engineering);

        var archiveOnly = EngineeringPermissions.Expand([
            EngineeringPermissions.ModuleView,
            EngineeringPermissions.ToolingArchiveManage
        ]);
        Assert.Contains(EngineeringPermissions.ToolingView, archiveOnly);
        Assert.Equal(ApplicationRoles.Editor, EngineeringPermissions.RoleFor(archiveOnly));

        var importOnly = EngineeringPermissions.Expand([
            EngineeringPermissions.ModuleView,
            EngineeringPermissions.ToolingAuditImport
        ]);
        Assert.Contains(EngineeringPermissions.ToolingView, importOnly);
        Assert.Equal(ApplicationRoles.Editor, EngineeringPermissions.RoleFor(importOnly));
    }

    [Fact]
    public async Task AccessSeeder_AddsAdministrativePermissionsAndMigratesExistingManagersOnce()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new EngineeringRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Groups.Add(new EngineeringAccessGroupRecord
        {
            Name = "Administrators",
            IsSystemGroup = true,
            Permissions = [new EngineeringGroupPermissionRecord { PermissionKey = EngineeringPermissions.ModuleView }]
        });
        db.Groups.Add(new EngineeringAccessGroupRecord
        {
            Name = "Managers",
            IsSystemGroup = true
        });
        await db.SaveChangesAsync();

        await new EngineeringAccessSeeder(db, new EngineeringAccessSchemaInitializer(db)).SeedAsync();

        var administrator = await db.Groups.Include(group => group.Permissions)
            .SingleAsync(group => group.Name == "Administrators");
        Assert.Contains(administrator.Permissions, permission => permission.PermissionKey == EngineeringPermissions.ToolingArchiveManage);
        Assert.Contains(administrator.Permissions, permission => permission.PermissionKey == EngineeringPermissions.ToolingAuditImport);
        Assert.Contains(administrator.Permissions, permission => permission.PermissionKey == EngineeringPermissions.ToolingLocationsManage);

        var managers = await db.Groups.Include(group => group.Permissions)
            .SingleAsync(group => group.Name == "Managers");
        var managerArchivePermission = Assert.Single(managers.Permissions.Where(permission =>
            permission.PermissionKey == EngineeringPermissions.ToolingArchiveManage));
        db.GroupPermissions.Remove(managerArchivePermission);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new EngineeringAccessSeeder(db, new EngineeringAccessSchemaInitializer(db)).SeedAsync();

        managers = await db.Groups.Include(group => group.Permissions)
            .SingleAsync(group => group.Name == "Managers");
        Assert.DoesNotContain(managers.Permissions, permission =>
            permission.PermissionKey == EngineeringPermissions.ToolingArchiveManage);
    }

    private static ToolLocation CreateLocation() => new()
    {
        Code = "A001-002", NormalizedCode = "A001002", Description = "Shared bin",
        CreatedBy = "TEST\\Admin", CreatedAt = DateTime.UtcNow
    };

    private static ToolRecord CreateTool(ToolLocation location, string toolNumber = "TL-900", string partNumber = "PN-900") => new()
    {
        ToolNumber = toolNumber, NormalizedToolNumber = string.Concat(toolNumber.ToUpperInvariant().Where(char.IsLetterOrDigit)), Name = "Calibration Fixture",
        ToolType = "Inspection fixture", Owner = "Test Customer",
        HomeLocationAssignment = new ToolHomeLocation { Location = location }, CurrentLocation = location,
        CreatedBy = "TEST\\Admin", CreatedAt = DateTime.UtcNow, UpdatedBy = "TEST\\Admin", UpdatedAt = DateTime.UtcNow,
        PartNumbers = [new ToolPartNumber
        {
            PartNumber = partNumber,
            NormalizedPartNumber = string.Concat(partNumber.ToUpperInvariant().Where(char.IsLetterOrDigit))
        }]
    };

    private static byte[] SaveWorkbook(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static ToolRecord WithNotes(ToolRecord tool, string notes)
    {
        tool.Notes = notes;
        return tool;
    }

    private static WebApplication CreateToolingApp(EngineeringDbContext db)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<ToolCatalogReviewStore>();
        builder.Services.AddScoped<ToolCatalogWorkbookService>();
        builder.Services.Configure<DrawingStorageOptions>(options =>
            options.RootPath = Path.Combine(Path.GetTempPath(), "engineering-tooling-tests"));
        builder.Services.AddScoped<IDrawingFileStore, DrawingFileStore>();
        var app = builder.Build();
        app.MapGroup("/api").MapToolingEndpoints();
        return app;
    }

    private static async Task<EndpointResponse> InvokeJsonAsync<T>(
        WebApplication app,
        string method,
        string route,
        int id,
        T body)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        await using var scope = app.Services.CreateAsyncScope();
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "TEST\\Manager")],
                "Test"))
        };
        context.Request.Method = method;
        context.Request.RouteValues["id"] = id.ToString();
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = content.Length;
        context.Request.Body = new MemoryStream(content);
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new StaticRequestBodyDetectionFeature());
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return new EndpointResponse(context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class StaticRequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed record EndpointResponse(int StatusCode, string Body);

    private sealed class ToolingFixture : IAsyncDisposable
    {
        private readonly string path;
        public EngineeringDbContext Db { get; }

        private ToolingFixture(string path, EngineeringDbContext db)
        {
            this.path = path;
            Db = db;
        }

        public static async Task<ToolingFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"engineering-tooling-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<EngineeringDbContext>().UseSqlite($"Data Source={path}").Options;
            var db = new EngineeringDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await new ToolingSchemaInitializer(db).InitializeAsync(CancellationToken.None);
            return new ToolingFixture(path, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

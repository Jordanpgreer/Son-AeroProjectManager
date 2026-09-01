using System.Text.Json;
using System.IO.Compression;
using ClosedXML.Excel;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumEstimateBuilderTests
{
    [Fact]
    public async Task Preview_maps_routing_bom_identity_times_and_manual_steps()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = SourceWorkbook();

        var preview = await fixture.Importer.PreviewAsync(
            workbook,
            "renamed fulcrum export.xlsx",
            @"TEST\jgreer",
            "Jordan Greer",
            default);

        Assert.True(preview.CanExport);
        Assert.Empty(preview.Issues);
        Assert.Equal("PN-100", preview.PartNumber);
        Assert.Equal("NC", preview.Revision);
        Assert.Equal("2026-09-01", preview.EstimateDate);
        Assert.Equal("JG", preview.EstimatorInitials);
        Assert.Equal(2026, preview.RateYear);
        Assert.Collection(
            preview.Operations,
            operation =>
            {
                Assert.Equal("Admin/Setup", operation.TargetOperation);
                Assert.Equal(2, operation.OperationNumber);
                Assert.Equal(5m, operation.SuggestedSetupMinutes);
                Assert.Equal(3m, operation.SuggestedRunMinutes);
            },
            operation =>
            {
                Assert.Equal("Rubber Mold", operation.TargetOperation);
                Assert.Equal(0m, operation.SuggestedSetupMinutes);
                Assert.Equal(2m, operation.SuggestedRunMinutes);
            },
            operation =>
            {
                Assert.Equal("Quality", operation.TargetOperation);
                Assert.Equal(4m, operation.SuggestedSetupMinutes);
                Assert.Equal(0m, operation.SuggestedRunMinutes);
            });
        var material = Assert.Single(preview.Materials);
        Assert.Equal("Rubber compound", material.Description);
        Assert.Equal(1.5m, material.UnitsRequired);
        Assert.Equal(47, material.TargetRow);
        Assert.Equal(13, preview.ManualFields.Count);
        Assert.Contains(preview.ManualFields, field => field.Id == "customer" && field.Cell == "B2");
        Assert.Contains(preview.ManualFields, field => field.Id == "quantity8" && field.Cell == "M13");
        Assert.Contains(preview.ManualFields, field => field.Id == "bom-3.unitPrice" && field.Cell == "D47");
    }

    [Fact]
    public async Task Export_populates_allowlisted_cells_rates_audit_sheet_and_exact_filename()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = SourceWorkbook();
        var preview = await fixture.Importer.PreviewAsync(
            workbook,
            "source.xlsx",
            @"TEST\jgreer",
            "Jordan Greer",
            default);
        var manual = ManualValues(preview);
        var request = new FulcrumEstimateExportDto(
            manual,
            [new FulcrumOperationOverrideDto("routing-4", 7m, 8m)],
            RateSnapshot(preview.RateYear));

        var result = fixture.Exporter.Export(preview.ReviewId, request, @"TEST\jgreer");

        Assert.Equal("PN-100 NC 09-01-2026 JG.xlsx", result.FileName);
        using (var package = new ZipArchive(new MemoryStream(result.Content), ZipArchiveMode.Read))
        {
            Assert.DoesNotContain(
                package.Entries,
                entry => entry.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                package.Entries,
                entry => entry.FullName.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                package.Entries,
                entry => string.Equals(entry.FullName, "docProps/custom.xml", StringComparison.OrdinalIgnoreCase));
            var sensitivityProperties = package.GetEntry("docProps/custom.xml");
            Assert.NotNull(sensitivityProperties);
            using (var reader = new StreamReader(sensitivityProperties.Open()))
            {
                var customProperties = reader.ReadToEnd();
                Assert.Contains("MSIP_Label_", customProperties, StringComparison.Ordinal);
                Assert.Contains(
                    "Non CUI - Controlled Unclassified Information",
                    customProperties,
                    StringComparison.Ordinal);
            }
            foreach (var relationships in package.Entries.Where(entry =>
                         entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(relationships.Open());
                var relationshipXml = reader.ReadToEnd();
                Assert.DoesNotContain(
                    "TargetMode=\"External\"",
                    relationshipXml,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "/customXml\"",
                    relationshipXml,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        using var exported = new XLWorkbook(new MemoryStream(result.Content));
        using var templateStream = typeof(FulcrumEstimateExportService).Assembly
            .GetManifestResourceStream(FulcrumEstimateExportService.TemplateResourceName);
        Assert.NotNull(templateStream);
        using var original = new XLWorkbook(templateStream);
        Assert.Equal(
            [FulcrumEstimateImportService.TargetSheet, FulcrumEstimateExportService.SnapshotSheet],
            exported.Worksheets.Select(sheet => sheet.Name).ToArray());
        var sheet = exported.Worksheet(FulcrumEstimateImportService.TargetSheet);
        var originalSheet = original.Worksheet(FulcrumEstimateImportService.TargetSheet);
        Assert.Equal(originalSheet.Cell("A2").Style.Font.FontName, sheet.Cell("A2").Style.Font.FontName);
        Assert.Equal(originalSheet.Cell("A2").Style.Fill.PatternType, sheet.Cell("A2").Style.Fill.PatternType);
        var preservedFormula = originalSheet.CellsUsed(cell =>
                cell.HasFormula
                && !cell.FormulaA1.Contains("[1]!Rates2020", StringComparison.OrdinalIgnoreCase)
                && cell.Address.RowNumber is not (18 or 19 or 20))
            .First();
        Assert.Equal(
            preservedFormula.FormulaA1,
            sheet.Cell(preservedFormula.Address).FormulaA1);
        Assert.Equal("PN-100", sheet.Cell("B3").GetString());
        Assert.Equal("NC", sheet.Cell("B4").GetString());
        Assert.Equal(new DateTime(2026, 9, 1), sheet.Cell("B7").GetDateTime());
        Assert.Equal("JG", sheet.Cell("B8").GetString());
        Assert.Equal(2026, sheet.Cell("B9").GetValue<int>());
        Assert.Equal("Test Customer", sheet.Cell("B2").GetString());
        Assert.Equal("QL-100", sheet.Cell("B5").GetString());
        Assert.Equal(1m, sheet.Cell("F13").GetValue<decimal>());
        Assert.Equal(8m, sheet.Cell("M13").GetValue<decimal>());
        Assert.Equal("Admin/Setup", sheet.Cell("A18").GetString());
        Assert.Equal(5m, sheet.Cell("B18").GetValue<decimal>());
        Assert.Equal(3m, sheet.Cell("C18").GetValue<decimal>());
        Assert.Equal("OP 2", sheet.Cell("O18").GetString());
        Assert.Equal(0.4005m, sheet.Cell("E18").GetValue<decimal>());
        Assert.Equal(7m, sheet.Cell("B19").GetValue<decimal>());
        Assert.Equal(8m, sheet.Cell("C19").GetValue<decimal>());
        Assert.Equal("Rubber compound", sheet.Cell("A47").GetString());
        Assert.Equal("LB", sheet.Cell("B47").GetString());
        Assert.Equal(1.5m, sheet.Cell("C47").GetValue<decimal>());
        Assert.Equal(12.25m, sheet.Cell("D47").GetValue<decimal>());
        Assert.Equal("Supplier quote required", sheet.Cell("O47").GetString());
        Assert.Equal(4.15m, sheet.Cell("D42").GetValue<decimal>());
        Assert.Equal(0.2m, sheet.Cell("E69").GetValue<decimal>());
        Assert.Equal(0.2m, sheet.Cell("E75").GetValue<decimal>());
        var printArea = Assert.Single(sheet.PageSetup.PrintAreas);
        Assert.Equal(2, printArea.FirstCell().Address.RowNumber);
        Assert.Equal(1, printArea.FirstCell().Address.ColumnNumber);
        Assert.Equal(89, printArea.LastCell().Address.RowNumber);
        Assert.Equal(15, printArea.LastCell().Address.ColumnNumber);
        Assert.DoesNotContain(
            sheet.CellsUsed(cell => cell.HasFormula),
            cell => cell.FormulaA1.Contains("[1]!Rates2020", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("F44+F69+F73", sheet.Cell("F79").FormulaA1);
        Assert.Contains(sheet.CellsUsed(), cell => cell.HasFormula);
        Assert.Equal(XLWorksheetVisibility.VeryHidden, exported.Worksheet(FulcrumEstimateExportService.SnapshotSheet).Visibility);
        Assert.Equal(2026, exported.Worksheet(FulcrumEstimateExportService.SnapshotSheet).Cell("B2").GetValue<int>());
        Assert.Equal(XLCalculateMode.Auto, exported.CalculateMode);
    }

    [Fact]
    public async Task Preview_reports_mismatched_bom_formula_unknown_rule_and_invalid_units_per_hour()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = SourceWorkbook(source =>
        {
            var routing = source.Worksheet("Routing");
            routing.Cell("G3").FormulaA1 = "=\"Unknown route\"";
            routing.Cell("N4").Value = "UnitsPerHour";
            routing.Cell("O4").Value = 0;
            source.Worksheet("Bill of Materials").Cell("D3").Value = "OTHER";
        });

        var preview = await fixture.Importer.PreviewAsync(
            workbook,
            "invalid.xlsx",
            @"TEST\jgreer",
            "Jordan Greer",
            default);

        Assert.False(preview.CanExport);
        Assert.Contains(preview.Issues, issue => issue.Message.Contains("Formulas are not accepted", StringComparison.Ordinal));
        Assert.Contains(preview.Issues, issue => issue.Message.Contains("No active estimating operation rule", StringComparison.Ordinal));
        Assert.Contains(preview.Issues, issue => issue.Message.Contains("UnitsPerHour must be greater than zero", StringComparison.Ordinal));
        Assert.Contains(preview.Issues, issue => issue.Message.Contains("does not match Routing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_rejects_units_per_hour_that_would_overflow_the_estimate()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = SourceWorkbook(source =>
            source.Worksheet("Routing").Cell("O4").Value = 0.0000000000000000000000000001m);

        var preview = await fixture.Importer.PreviewAsync(
            workbook,
            "tiny-units-per-hour.xlsx",
            @"TEST\jgreer",
            "Jordan Greer",
            default);

        Assert.False(preview.CanExport);
        Assert.Contains(
            preview.Issues,
            issue => issue.Message.Contains("converts to more than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_rejects_unsafe_workbook_package_paths()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = WorkbookPackage(
            "[Content_Types].xml",
            "xl/workbook.xml",
            "../outside.xml");

        var exception = await Assert.ThrowsAsync<FulcrumEstimateValidationException>(() =>
            fixture.Importer.PreviewAsync(
                workbook,
                "unsafe.xlsx",
                @"TEST\jgreer",
                "Jordan Greer",
                default));

        Assert.Equal("The workbook package contains an unsafe file path.", exception.Message);
    }

    [Fact]
    public async Task Preview_store_bounds_each_users_active_reviews()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reviewIds = new List<Guid>();
        for (var index = 0; index <= FulcrumEstimateReviewStore.MaximumReviewsPerActor; index++)
        {
            await using var workbook = SourceWorkbook();
            var preview = await fixture.Importer.PreviewAsync(
                workbook,
                $"review-{index}.xlsx",
                @"TEST\jgreer",
                "Jordan Greer",
                default);
            reviewIds.Add(preview.ReviewId);
        }

        var missing = 0;
        foreach (var reviewId in reviewIds)
        {
            var exception = Record.Exception(() => fixture.Exporter.Export(
                reviewId,
                new FulcrumEstimateExportDto(null, null, null),
                @"TEST\jgreer"));
            if (exception is FulcrumEstimateReviewNotFoundException) missing++;
            else Assert.IsType<FulcrumEstimateManualValidationException>(exception);
        }
        Assert.Equal(1, missing);
    }

    [Fact]
    public async Task Export_rejects_missing_manual_values_and_tampered_rate_name()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = SourceWorkbook();
        var preview = await fixture.Importer.PreviewAsync(
            workbook,
            "source.xlsx",
            @"TEST\jgreer",
            "Jordan Greer",
            default);
        var manual = ManualValues(preview);
        manual.Remove("bom-3.notes");
        Assert.Throws<FulcrumEstimateManualValidationException>(() => fixture.Exporter.Export(
            preview.ReviewId,
            new FulcrumEstimateExportDto(manual, null, RateSnapshot(preview.RateYear)),
            @"TEST\jgreer"));

        manual = ManualValues(preview);
        var snapshot = RateSnapshot(preview.RateYear);
        var tampered = snapshot with
        {
            OperationRates = snapshot.OperationRates
                .Select(rate => rate.RateReferenceKey == "manufacturing:9"
                    ? rate with { Operation = "Wrong operation" }
                    : rate)
                .ToList()
        };
        Assert.Throws<FulcrumEstimateManualValidationException>(() => fixture.Exporter.Export(
            preview.ReviewId,
            new FulcrumEstimateExportDto(manual, null, tampered),
            @"TEST\jgreer"));

        var tamperedValue = snapshot with
        {
            OperationRates = snapshot.OperationRates
                .Select(rate => rate.RateReferenceKey == "manufacturing:9"
                    ? rate with { Value = rate.Value + 1m }
                    : rate)
                .ToList()
        };
        Assert.Throws<FulcrumEstimateManualValidationException>(() => fixture.Exporter.Export(
            preview.ReviewId,
            new FulcrumEstimateExportDto(manual, null, tamperedValue),
            @"TEST\jgreer"));

        var tamperedAssumption = snapshot with
        {
            Assumptions = snapshot.Assumptions with { Burden = 9m }
        };
        Assert.Throws<FulcrumEstimateManualValidationException>(() => fixture.Exporter.Export(
            preview.ReviewId,
            new FulcrumEstimateExportDto(manual, null, tamperedAssumption),
            @"TEST\jgreer"));
    }

    [Fact]
    public async Task Preview_enforces_template_operation_and_material_capacities_without_truncating_silently()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = SourceWorkbook(source =>
        {
            var routing = source.Worksheet("Routing");
            routing.Range("G3:O100").Clear(XLClearOptions.Contents);
            for (var index = 0; index < 23; index++)
            {
                var row = 3 + index;
                routing.Cell(row, 7).Value = "Material prep rubber";
                routing.Cell(row, 8).Value = index + 1;
            }
            var bom = source.Worksheet("Bill of Materials");
            bom.Range("H3:L100").Clear(XLClearOptions.Contents);
            for (var index = 0; index < 13; index++)
            {
                var row = 3 + index;
                bom.Cell(row, 8).Value = $"Material {index + 1}";
                bom.Cell(row, 12).Value = 1;
            }
        });

        var preview = await fixture.Importer.PreviewAsync(
            workbook,
            "capacity.xlsx",
            @"TEST\jgreer",
            "Jordan Greer",
            default);

        Assert.False(preview.CanExport);
        Assert.Equal(22, preview.Operations.Count);
        Assert.Equal(12, preview.Materials.Count);
        Assert.Contains(preview.Issues, issue => issue.Message.Contains("supports 22", StringComparison.Ordinal));
        Assert.Contains(preview.Issues, issue => issue.Message.Contains("supports 12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rules_are_seeded_linked_versioned_and_audited()
    {
        await using var fixture = await Fixture.CreateAsync();
        var catalog = await fixture.Mappings.GetCatalogAsync();
        Assert.Equal(10, catalog.Rules.Count(rule => rule.IsActive));
        Assert.Contains(catalog.Rules, rule =>
            rule.FulcrumOperation == "Material prep rubber"
            && rule.RateReferenceKey == "rubber-breakdown:34"
            && rule.EstimatingOperation == "Admin/Setup");

        var created = await fixture.Mappings.CreateAsync(
            new CreateEstimatingOperationMappingDto("  New   Fulcrum Step ", "rubber-breakdown:35"),
            @"TEST\admin");
        Assert.Equal("New Fulcrum Step", created.FulcrumOperation);
        var updated = await fixture.Mappings.UpdateAsync(
            created.Id,
            new UpdateEstimatingOperationMappingDto("New Fulcrum Step", "rubber-breakdown:36", created.Version),
            @"TEST\admin");
        Assert.Equal(1, updated.Version);
        Assert.Equal("Bond Room", updated.EstimatingOperation);
        var deactivated = await fixture.Mappings.DeactivateAsync(
            updated.Id,
            updated.Version,
            @"TEST\admin");
        Assert.False(deactivated.IsActive);
        Assert.Equal(2, deactivated.Version);
        Assert.Equal(3, await fixture.Db.EstimatingOperationMappingAudits
            .CountAsync(audit => audit.OperationMappingId == created.Id));
        await Assert.ThrowsAsync<EstimatingOperationMappingConflictException>(() =>
            fixture.Mappings.UpdateAsync(
                created.Id,
                new UpdateEstimatingOperationMappingDto("Stale", "rubber-breakdown:35", 0),
                @"TEST\admin"));
    }

    [Fact]
    public async Task Rule_version_is_a_database_concurrency_token()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Db.EstimatingOperationMappings
            .Select(mapping => mapping.Id)
            .FirstAsync();
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = await firstDb.EstimatingOperationMappings.SingleAsync(mapping => mapping.Id == id);
        var second = await secondDb.EstimatingOperationMappings.SingleAsync(mapping => mapping.Id == id);

        first.Version++;
        first.UpdatedBy = @"TEST\first";
        await firstDb.SaveChangesAsync();
        second.Version++;
        second.UpdatedBy = @"TEST\second";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    private static MemoryStream SourceWorkbook(Action<XLWorkbook>? change = null)
    {
        using var workbook = new XLWorkbook();
        var routing = workbook.AddWorksheet("Routing");
        routing.Cell("D3").Value = "PN-100";
        routing.Cell("E3").Value = "NC";
        routing.Cell("G3").Value = "Material prep rubber";
        routing.Cell("H3").Value = 2;
        routing.Cell("L3").Value = 5;
        routing.Cell("N3").Value = "PerUnit";
        routing.Cell("O3").Value = 3;
        routing.Cell("G4").Value = "Rubber Mold Production";
        routing.Cell("H4").Value = 10;
        routing.Cell("N4").Value = "UnitsPerHour";
        routing.Cell("O4").Value = 30;
        routing.Cell("G5").Value = "QA Final Inspection";
        routing.Cell("H5").Value = 20;
        routing.Cell("N5").Value = "Fixed";
        routing.Cell("O5").Value = 4;
        var bom = workbook.AddWorksheet("Bill of Materials");
        bom.Cell("D3").Value = "PN-100";
        bom.Cell("E3").Value = "NC";
        bom.Cell("H3").Value = "Rubber compound";
        bom.Cell("L3").Value = 1.5m;
        change?.Invoke(workbook);
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream WorkbookPackage(params string[] entryNames)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entryName in entryNames)
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("<root />");
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static Dictionary<string, JsonElement> ManualValues(FulcrumEstimatePreviewDto preview)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["customer"] = JsonSerializer.SerializeToElement("Test Customer"),
            ["quoteLogNumber"] = JsonSerializer.SerializeToElement("QL-100")
        };
        for (var index = 1; index <= 8; index++)
            values[$"quantity{index}"] = JsonSerializer.SerializeToElement(index);
        foreach (var material in preview.Materials)
        {
            values[$"{material.Id}.unitOfMeasure"] = JsonSerializer.SerializeToElement("LB");
            values[$"{material.Id}.unitPrice"] = JsonSerializer.SerializeToElement(12.25m);
            values[$"{material.Id}.notes"] = JsonSerializer.SerializeToElement("Supplier quote required");
        }
        return values;
    }

    private static FulcrumRateSnapshotDto RateSnapshot(int year)
    {
        Assert.True(EstimatingControlledRates.TryGetAssumptions(year, out var assumptions));
        return new(
            year,
            EstimatingRateReferenceCatalog.References
                .Select(reference =>
                {
                    Assert.True(EstimatingControlledRates.TryGetRate(reference.Key, year, out var rate));
                    return new FulcrumOperationRateDto(reference.Key, reference.Operation, rate);
                })
                .ToList(),
            new FulcrumRateAssumptionsDto(
                assumptions.Burden,
                assumptions.LaborGa,
                assumptions.MaterialGa,
                assumptions.ProcessGa,
                assumptions.LaborProfit,
                assumptions.MaterialProfit,
                assumptions.ProcessProfit));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public EstimatingAccessDbContext Db { get; }
        public FulcrumEstimateImportService Importer { get; }
        public FulcrumEstimateExportService Exporter { get; }
        public EstimatingOperationMappingService Mappings { get; }

        private Fixture(
            SqliteConnection connection,
            EstimatingAccessDbContext db,
            FulcrumEstimateImportService importer,
            FulcrumEstimateExportService exporter,
            EstimatingOperationMappingService mappings)
        {
            this.connection = connection;
            Db = db;
            Importer = importer;
            Exporter = exporter;
            Mappings = mappings;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new EstimatingAccessDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await new EstimatingHistorySchemaInitializer(db).InitializeAsync();
            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero));
            var reviews = new FulcrumEstimateReviewStore(timeProvider);
            return new Fixture(
                connection,
                db,
                new FulcrumEstimateImportService(
                    db,
                    reviews,
                    timeProvider),
                new FulcrumEstimateExportService(reviews),
                new EstimatingOperationMappingService(db));
        }

        public EstimatingAccessDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<EstimatingAccessDbContext>()
                .UseSqlite(connection)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

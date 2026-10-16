using Microsoft.EntityFrameworkCore;

namespace QualityAssurance.Tests;

public sealed partial class QualityShipmentImportServiceTests
{
    [Fact]
    public async Task Import_skips_sales_orders_without_shipment_suffix_before_validating_their_other_cells()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var workbook = Workbook([
            ["WIP", "SO2877", "invalid date", null, null, null, "invalid quantity", null, null, null, null, null, null, null],
            ["WIP", "SHP-SO2877", null, null, null, null, null, null, null, null, null, null, null, null],
            ["WIP", null, null, null, null, null, null, null, null, null, null, null, null, null],
            ["WIP", "SO2877-2", null, "PART-A", null, "Customer", 2m, null, null, null, null, null, null, null]
        ]);
        await using var stream = new MemoryStream(workbook);

        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(4, result.RowsRead);
        Assert.Equal(3, result.SkippedWithoutShipmentSuffix);
        Assert.Equal(1, result.CreatedRecords);
        Assert.Equal("SHP-SO2877-2", (await fixture.Db.Shipments.SingleAsync()).SalesOrderNumber);
    }

    [Fact]
    public async Task Import_reports_an_all_skipped_workbook_without_creating_records()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await using var stream = new MemoryStream(Workbook([
            ["WIP", "SO2877", null, null, null, null, null, null, null, null, null, null, null, null]
        ]));

        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(1, result.SkippedWithoutShipmentSuffix);
        Assert.Equal(0, result.CreatedRecords);
        Assert.Empty(fixture.Db.Shipments);
    }

    [Fact]
    public async Task Import_recognizes_existing_legacy_number_after_canonicalization()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        fixture.Db.Shipments.Add(ImportedShipment("SO2877-2", "QA-OLD"));
        await fixture.Db.SaveChangesAsync();
        await using var stream = new MemoryStream(Workbook([
            ["WIP", "SHP-SO2877-2", null, "PN-IMPORT", null, "HONEYWELL", null, null, null, null, null, "QA-OLD", null, null]
        ]));

        var result = await fixture.Importer.ImportAsync(stream, "shipping.xlsx", fixture.Admin, default);

        Assert.Equal(0, result.CreatedRecords);
        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Single(fixture.Db.Shipments);
    }

}

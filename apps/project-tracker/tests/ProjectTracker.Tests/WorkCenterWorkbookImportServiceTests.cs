using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services.Import;

namespace ProjectTracker.Tests;

public sealed class WorkCenterWorkbookImportServiceTests
{
    [Fact]
    public void ImportRoute_RequiresTheWorkCenterImportPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<ProjectTrackerDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddScoped<WorkCenterWorkbookImportService>();
        var app = builder.Build();
        app.MapGroup("/api").MapWorkCenterImportEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/work-centers/import"
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

        Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == WorkCenterImportEndpoints.AuthorizationPolicy);
    }

    [Fact]
    public async Task Import_TrimsNamesAndSkipsCaseInsensitiveWorkbookAndDatabaseDuplicates()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        fixture.Db.WorkCenters.Add(new WorkCenter { Name = "Existing Center" });
        await fixture.Db.SaveChangesAsync();
        await using var workbook = Workbook(
            (1, 1, "Unrelated note"),
            (3, 2, "  work CENTER name  "),
            (4, 2, "  CNC Mill  "),
            (5, 2, "cnc mill"),
            (6, 2, "EXISTING CENTER"),
            (8, 2, "Laser"));

        var result = await new WorkCenterWorkbookImportService()
            .ImportAsync(fixture.Db, workbook);

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(["CNC Mill", "Laser"], result.AddedNames);
        Assert.Contains("cnc mill", result.SkippedNames);
        Assert.Contains("EXISTING CENTER", result.SkippedNames);
        Assert.Equal(
            ["CNC Mill", "Existing Center", "Laser"],
            await fixture.Db.WorkCenters.OrderBy(workCenter => workCenter.Name).Select(workCenter => workCenter.Name).ToListAsync());
    }

    [Fact]
    public async Task Import_RejectsAnInvalidNameBeforeSavingAnyRows()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await using var workbook = Workbook(
            (1, 1, "Work Center Name"),
            (2, 1, "Valid Center"),
            (3, 1, new string('x', WorkCenterWorkbookImportService.MaxWorkCenterNameLength + 1)));

        var exception = await Assert.ThrowsAsync<WorkCenterWorkbookImportException>(() =>
            new WorkCenterWorkbookImportService().ImportAsync(fixture.Db, workbook));

        Assert.Contains("row 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.WorkCenters.ToListAsync());
    }

    [Fact]
    public async Task Import_RejectsFormulaCellsWithoutEvaluatingThem()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Work Centers");
        sheet.Cell(1, 1).Value = "Work Center Name";
        sheet.Cell(2, 1).FormulaA1 = "REPT(\"x\",1000000)";
        await using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<WorkCenterWorkbookImportException>(() =>
            new WorkCenterWorkbookImportService().ImportAsync(fixture.Db, stream));

        Assert.Contains("formula", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("row 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.WorkCenters.ToListAsync());
    }

    [Fact]
    public async Task Import_RejectsBlankAndMalformedWorkbooks()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var service = new WorkCenterWorkbookImportService();

        var empty = await Assert.ThrowsAsync<WorkCenterWorkbookImportException>(() =>
            service.ImportAsync(fixture.Db, new MemoryStream()));
        var malformed = await Assert.ThrowsAsync<WorkCenterWorkbookImportException>(() =>
            service.ImportAsync(fixture.Db, new MemoryStream([1, 2, 3, 4])));

        Assert.Contains("empty", empty.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valid .xlsx", malformed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.WorkCenters.ToListAsync());
    }

    [Fact]
    public async Task Endpoint_RejectsUnsupportedWorkbookExtensions()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await using var content = new MemoryStream([1]);
        var file = new FormFile(content, 0, content.Length, "file", "work-centers.xlsm");
        var context = TrustedMultipartContext();

        var result = await WorkCenterImportEndpoints.ImportAsync(
            context.Request,
            file,
            fixture.Db,
            new WorkCenterWorkbookImportService(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<string>>(result);
        Assert.Contains(".xlsx", badRequest.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Endpoint_RejectsMultipartRequestsWithoutTheAdminScreenHeader()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await using var content = new MemoryStream([1]);
        var file = new FormFile(content, 0, content.Length, "file", "work-centers.xlsx");
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";

        var result = await WorkCenterImportEndpoints.ImportAsync(
            context.Request,
            file,
            fixture.Db,
            new WorkCenterWorkbookImportService(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<string>>(result);
        Assert.Contains("admin screen", badRequest.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Endpoint_RejectsOversizedWorkbooksBeforeReadingTheBody()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var file = new FormFile(
            Stream.Null,
            0,
            WorkCenterWorkbookImportService.MaxWorkbookBytes + 1L,
            "file",
            "work-centers.xlsx");

        var result = await WorkCenterImportEndpoints.ImportAsync(
            TrustedMultipartContext().Request,
            file,
            fixture.Db,
            new WorkCenterWorkbookImportService(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<string>>(result);
        Assert.Contains("5 MB", badRequest.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultHttpContext TrustedMultipartContext()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return context;
    }

    private static MemoryStream Workbook(params (int Row, int Column, string Value)[] cells)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Work Centers");
        foreach (var cell in cells)
        {
            sheet.Cell(cell.Row, cell.Column).Value = cell.Value;
        }
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private sealed class ImportFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ImportFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public ProjectTrackerDbContext Db { get; }

        public static async Task<ImportFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ProjectTrackerDbContext(new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new ImportFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

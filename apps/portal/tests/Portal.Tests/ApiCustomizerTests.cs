using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using Portal.Api.Services;
using Portal.Api.Services.ApiCustomizer;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class ApiCustomizerTests
{
    private static readonly FulcrumReportCatalog Catalog = new();

    [Fact]
    public void Catalogue_contains_current_parts_routing_materials_and_only_documented_reads()
    {
        Assert.Contains(Catalog.Catalog.Sources, s => s.Path == "/api/items/list/v2");
        Assert.DoesNotContain(Catalog.Catalog.Sources, s => s.Path == "/api/items/list");
        Assert.Equal(InventoryBomReport.SourceId, Assert.Single(Catalog.Catalog.Sources.Where(s => s.Method == "COMPOSE")).Id);
        Assert.All(Catalog.Catalog.Sources.Where(s => s.Id != InventoryBomReport.SourceId), s => Assert.True(s.Method == "GET"
            || s.Method == "POST" && System.Text.RegularExpressions.Regex.IsMatch(s.Path, @"/list(?:/v\d+)?$")));
        var parts = Catalog.Source("POST /api/items/list/v2");
        Assert.Contains(parts.Fields, f => f.Path == "number");
        Assert.Contains(parts.Fields, f => f.Path == "revision.revision");
        Assert.Contains(parts.Inputs, i => i.Key == "body.isArchived" && i.Type == "boolean");
        Assert.Contains(parts.Inputs, i => i.Key == "body.numbers" && i.Children[0].Type == "object");
        Assert.Equal("paged", Catalog.Source("POST /api/reporting/quote/list").Shape);
        Assert.Contains(Catalog.Source("POST /api/items/{itemId}/routing/input-materials/list").Fields, f => f.Path == "materialName");
    }

    [Fact]
    public async Task Live_pull_joins_exact_parent_ids_and_exports_the_same_values_with_text_identifiers()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.Sheets[0].Inputs["body.isArchived"] = JsonSerializer.SerializeToElement(false);
        definition.Sheets[0].StartRow = 3;
        definition.Sheets[0].StartColumn = 2;
        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Contains("\"isArchived\":false", fixture.Handler.Requests[0].Body);
        Assert.Contains("/api/items/000000000000000000000001/routing/operations/list", fixture.Handler.Requests[1].Uri);
        Assert.All(fixture.Handler.Requests, r => Assert.Equal("Bearer TEST_TOKEN", r.Authorization));
        Assert.Equal("000045", result.Sheets[1].Rows[0][0]);
        Assert.Equal("Cut", result.Sheets[1].Rows[0][1]);
        Assert.Equal("Inspect", result.Sheets[1].Rows[1][1]);
        var snapshot = fixture.Cache.Get<ReportSnapshot>("customizer-run:" + result.Id)!;
        using var workbook = new XLWorkbook(new MemoryStream(ReportWorkbook.Create(snapshot)));
        Assert.Equal("Part Number", workbook.Worksheet("Parts").Cell("B3").GetString());
        Assert.Equal("000045", workbook.Worksheet("Parts").Cell("B4").GetString());
        Assert.Equal(XLDataType.Text, workbook.Worksheet("Parts").Cell("B4").DataType);
        Assert.False(workbook.Worksheet("Parts").Cell("C4").HasFormula);
        Assert.Equal("=HYPERLINK(\"https://example.invalid\")", workbook.Worksheet("Parts").Cell("C4").GetString());
        Assert.Equal(3, workbook.Worksheet("Parts").SheetView.SplitRow);
        Assert.Equal("Inspect", workbook.Worksheet("Routing").Cell("B3").GetString());
    }

    [Fact]
    public async Task Sample_mode_never_contacts_fulcrum_and_new_preview_replaces_earlier_snapshot()
    {
        await using var fixture = await Fixture.Create(false);
        var first = await fixture.Runner.RunAsync(new(Definition(), true), "TEST\\admin", default);
        var second = await fixture.Runner.RunAsync(new(Definition(), true), "TEST\\admin", default);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(0, first.RequestCount);
        Assert.Contains(first.Warnings, w => w.StartsWith("SAMPLE DATA"));
        Assert.False(fixture.Cache.TryGetValue("customizer-run:" + first.Id, out _));
        Assert.True(fixture.Cache.TryGetValue("customizer-run:" + second.Id, out _));
    }

    [Fact]
    public async Task Limits_and_permission_failures_do_not_produce_partial_workbooks()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.MaxRecords = 1;
        var error = await Assert.ThrowsAsync<ReportValidationException>(() => fixture.Runner.RunAsync(new(definition), "TEST\\admin", default));
        Assert.Contains("no partial workbook", error.Message);
        fixture.Handler.Status = HttpStatusCode.Forbidden;
        error = await Assert.ThrowsAsync<ReportValidationException>(() => fixture.Runner.RunAsync(new(Definition()), "TEST\\admin", default));
        Assert.Contains("token cannot read", error.Message);
    }

    [Fact]
    public async Task Invalid_sources_filters_and_relationships_are_rejected_before_api_access()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.Sheets[0].SourceId = "POST https://example.invalid/steal-token";
        Assert.Throws<ReportValidationException>(() => fixture.Runner.Validate(definition));
        definition = Definition();
        definition.Sheets[0].Inputs["body.isArchived"] = JsonSerializer.SerializeToElement("false");
        Assert.Throws<ReportValidationException>(() => fixture.Runner.Validate(definition));
        definition = Definition();
        definition.Sheets[0].ParentSheetId = "routing";
        Assert.Throws<ReportValidationException>(() => fixture.Runner.Validate(definition));
        definition = Definition();
        definition.Sheets[1].Bindings["path.itemId"] = new("routing", "id");
        Assert.Throws<ReportValidationException>(() => fixture.Runner.Validate(definition));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public void Paged_reporting_and_array_fields_keep_their_shape()
    {
        var source = Catalog.Source("POST /api/reporting/quote/list");
        using var document = JsonDocument.Parse("{\"data\":[{\"number\":12}],\"totalCount\":1,\"hasNextPage\":false}");
        Assert.Single(FulcrumReportRunner.Extract(source, document.RootElement));
        using var array = JsonDocument.Parse("{\"tags\":[{\"name\":\"A\"},{\"name\":\"B\"}]}");
        Assert.Equal("A; B", FulcrumReportRunner.Read(array.RootElement, "tags[].name"));
        Assert.Throws<ReportValidationException>(() => FulcrumReportRunner.Extract(source, array.RootElement));
        Assert.Throws<ReportValidationException>(() => FulcrumReportRunner.Extract(source, array.RootElement.GetProperty("tags")));
        using var custom = JsonDocument.Parse("{\"customFields\":{\"Engineering.Rep\":\"Test Person\"}}");
        Assert.Equal("Test Person", FulcrumReportRunner.Read(custom.RootElement, "customFields.Engineering.Rep"));
    }

    [Fact]
    public async Task Pagination_reads_all_pages_and_rejects_repeated_pages()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.Sheets.RemoveAt(1);
        definition.MaxRecords = 1000;
        var fullPage = JsonSerializer.Serialize(Enumerable.Range(0, 500).Select(i => new { number = i.ToString("D6") }));
        fixture.Handler.Response = request => request.RequestUri!.Query.Contains("Skip=0", StringComparison.OrdinalIgnoreCase)
            ? fullPage : "[{\"number\":\"000500\"}]";
        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);
        Assert.Equal(501, result.Sheets[0].Rows.Count);
        Assert.Equal(2, result.RequestCount);
        Assert.Contains("Skip=500", fixture.Handler.Requests[1].Uri, StringComparison.OrdinalIgnoreCase);
        fixture.Handler.Response = _ => fullPage;
        var error = await Assert.ThrowsAsync<ReportValidationException>(() => fixture.Runner.RunAsync(new(definition), "TEST\\admin", default));
        Assert.Contains("repeated a page", error.Message);
    }

    [Fact]
    public async Task Paginated_sources_can_safely_read_more_than_5000_records()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.Sheets.RemoveAt(1);
        definition.MaxRecords = 6000;
        fixture.Handler.Response = request =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(request.RequestUri!.Query, @"(?i)(?:[?&])skip=(\d+)");
            var skip = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var count = skip < 5500 ? 500 : 1;
            return JsonSerializer.Serialize(Enumerable.Range(skip, count).Select(i => new { number = i.ToString("D6") }));
        };

        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);

        Assert.Equal(5501, result.Sheets[0].Rows.Count);
        Assert.Equal(12, result.RequestCount);
        Assert.Contains("Skip=5500", fixture.Handler.Requests[^1].Uri, StringComparison.OrdinalIgnoreCase);
        Assert.All(fixture.Handler.Requests, request =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(request.Uri, @"(?i)(?:[?&])take=(\d+)");
            Assert.True(match.Success);
            Assert.InRange(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1, 500);
        });
    }

    [Fact]
    public async Task Record_limit_accepts_50000_and_rejects_larger_values()
    {
        await using var fixture = await Fixture.Create(false);
        var definition = Definition();
        definition.MaxRecords = FulcrumReportRunner.MaxRecordsPerSource;
        fixture.Runner.Validate(definition);
        definition.MaxRecords++;

        var error = Assert.Throws<ReportValidationException>(() => fixture.Runner.Validate(definition));

        Assert.Contains("50,000", error.Message);
    }

    [Fact]
    public async Task Oversized_excel_cells_are_rejected_without_truncating_source_text()
    {
        await using var fixture = await Fixture.Create();
        fixture.Handler.Response = _ => JsonSerializer.Serialize(new[] { new { description = new string('x', 32768) } });
        var definition = Definition();
        definition.Sheets.RemoveAt(1);
        var error = await Assert.ThrowsAsync<ReportValidationException>(() => fixture.Runner.RunAsync(new(definition), "TEST\\admin", default));
        Assert.Contains("cell limit", error.Message);
    }

    [Fact]
    public async Task Schema_initializer_is_repeatable_and_layout_versions_enforce_concurrency()
    {
        await using var fixture = await Fixture.Create();
        await new CustomizerSchemaInitializer(fixture.Db).InitializeAsync();
        await new CustomizerSchemaInitializer(fixture.Db).InitializeAsync();
        var record = new CustomizerReportRecord { Id = Guid.NewGuid(), Name = "Template", Version = 1, UpdatedBy = "TEST\\admin", UpdatedAt = DateTimeOffset.UtcNow, DefinitionJson = "{}" };
        fixture.Db.Add(record);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("ApiCustomizerReports", fixture.Db.Model.FindEntityType(typeof(CustomizerReportRecord))!.GetTableName());
        Assert.True(fixture.Db.Model.FindEntityType(typeof(CustomizerReportRecord))!.FindProperty("Version")!.IsConcurrencyToken);
        Assert.Single(await fixture.Db.Set<CustomizerReportRecord>().ToListAsync());
    }

    [Theory]
    [InlineData("Viewer", 403)]
    [InlineData("Editor", 403)]
    [InlineData("Admin", 200)]
    public async Task Catalogue_endpoint_enforces_current_admin_role(string role, int status)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Mode"] = "Development", ["Portal:DevelopmentRole"] = role });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<PortalUserService>();
        builder.Services.AddSingleton<IPortalRoleStore, EmptyRoleStore>();
        builder.Services.AddSingleton(Catalog);
        builder.Services.AddScoped<FulcrumReportRunner>();
        builder.Services.AddDbContext<PortalRoleDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddMemoryCache();
        await using var app = builder.Build();
        app.MapGroup("/api").MapApiCustomizerEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/admin/api-customizer/catalogue");
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "TEST\\person")], "test"));
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        await endpoint.RequestDelegate!(context);
        Assert.Equal(status, context.Response.StatusCode);
    }

    private static ReportDefinition Definition() => new()
    {
        Name = "Test Workbook", MaxRecords = 10,
        Sheets = [new() { Id = "parts", Name = "Parts", SourceId = "POST /api/items/list/v2",
            Columns = [new("parts", "number", "Part Number"), new("parts", "description", "Description")] },
            new() { Id = "routing", Name = "Routing", ParentSheetId = "parts", SourceId = "POST /api/items/{itemId}/routing/operations/list",
                Bindings = new() { ["path.itemId"] = new("parts", "id") },
                Columns = [new("parts", "number", "Part Number"), new("routing", "name", "Step")] }]
    };

    [Fact]
    public async Task Combined_report_uses_selected_row_type_and_automatically_sizes_export()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.OutputMode = "combined";
        definition.DetailSheetId = "routing";
        definition.OutputColumns = [new("parts", "number", "Item Number"), new("routing", "name", "Step")];
        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);
        Assert.Single(result.Sheets);
        Assert.Equal(2, result.Sheets[0].Rows.Count);
        Assert.Equal("000045", result.Sheets[0].Rows[1][0]);
        Assert.Equal("Inspect", result.Sheets[0].Rows[1][1]);
        using var workbook = new XLWorkbook(new MemoryStream(ReportWorkbook.Create(fixture.Cache.Get<ReportSnapshot>("customizer-run:" + result.Id)!)));
        Assert.Single(workbook.Worksheets);
        Assert.Equal("000045", workbook.Worksheet("Report").Cell("A2").GetString());
        Assert.InRange(workbook.Worksheet("Report").Column(1).Width, 12, 60);
        Assert.True(workbook.Worksheet("Report").Cell("B2").Style.Alignment.WrapText);
        definition.DetailSheetId = "parts";
        result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);
        Assert.Single(result.Sheets[0].Rows);
        Assert.Equal("Cut\nInspect", result.Sheets[0].Rows[0][1]);
    }

    [Fact]
    public async Task Combined_sibling_details_never_multiply_rows_or_mix_parent_records()
    {
        await using var fixture = await Fixture.Create();
        var definition = Definition();
        definition.OutputMode = "combined";
        definition.DetailSheetId = "routing";
        definition.Sheets.Add(new() { Id = "materials", Name = "Materials", ParentSheetId = "parts",
            SourceId = "POST /api/items/{itemId}/routing/input-materials/list", Bindings = new() { ["path.itemId"] = new("parts", "id") } });
        definition.OutputColumns = [new("parts", "number", "Item"), new("routing", "name", "Step"), new("materials", "materialName", "Material")];
        fixture.Handler.Response = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/items/list/v2" => "[{\"id\":\"a\",\"number\":\"A\"},{\"id\":\"b\",\"number\":\"B\"}]",
            var path when path.EndsWith("operations/list") => "[{\"name\":\"Cut\"},{\"name\":\"Inspect\"}]",
            var path when path.Contains("/a/") => "[{\"materialName\":\"Steel\"},{\"materialName\":\"Rubber\"}]",
            _ => "[{\"materialName\":\"Aluminum\"}]"
        };
        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);
        Assert.Equal(4, result.Sheets[0].Rows.Count);
        Assert.Equal("Steel\nRubber", result.Sheets[0].Rows[0][2]);
        Assert.Equal("Aluminum", result.Sheets[0].Rows[3][2]);
        definition.Sheets[2].ParentSheetId = null;
        Assert.Throws<ReportValidationException>(() => fixture.Runner.Validate(definition));
    }

    [Fact]
    public void Automatic_dimensions_fit_short_values_and_bound_long_notes()
    {
        var sheet = new ReportSheetResult("test", "Test", [new("source", "id", "ID"), new("source", "notes", "Notes")], [["001", new string('x', 200)]]);
        var sized = ReportPresentation.Size(sheet, true);
        Assert.Equal(12, sized.Columns[0].Width);
        Assert.Equal(60, sized.Columns[1].Width);
        Assert.Equal(new string('x', 200), sized.Rows[0][1]);
    }

    private static ReportDefinition BomDefinition() => new()
    {
        Name = "Inventory BOM", OutputMode = "combined", DetailSheetId = "bom", MaxRecords = 5000,
        Sheets = [new() { Id = "bom", Name = "BOM", SourceId = InventoryBomReport.SourceId,
            Columns = InventoryBomReport.TemplateFields.Select(f => new ReportColumn("bom", f.Path, f.Label,
                f.Type == "duration" ? "duration" : f.Type is "number" or "integer" ? "number" : "text")).ToList() }]
    };

    [Fact]
    public void Bom_template_matches_all_32_reference_headers_without_guessing_unmapped_values()
    {
        var expected = "BOM ID|Revision|Hold|Inventory ID|Subitem|Warehouse|Start Date|End Date|Description|Operation Nbr|Operation Descr|Work Center|Setup Time|Run Units|Run Time|Machine Units|Machine Time|Queue Time|Backflush Labor|Scrap Action|Matl Inventory ID|Matl Subitem|Qty Req|UOM|Unit Cost|Material Type|Phantom Routing|Backflush|Matl Warehouse|Location|Scrap Factor|NOTES".Split('|');
        Assert.Equal(expected, InventoryBomReport.TemplateFields.Select(f => f.Label));
        Assert.Equal("unmapped", InventoryBomReport.TemplateFields.Single(f => f.Path == "bomId").Availability);
        Assert.Contains(Catalog.Source(InventoryBomReport.SourceId).Fields, f => f.Path == "item.customFields");
        Assert.Contains(Catalog.Source(InventoryBomReport.SourceId).Fields, f => f.Path == "component.isMaterialLine");
    }

    [Fact]
    public async Task Bom_sample_keeps_steps_and_materials_aligned_and_exports_numeric_durations()
    {
        await using var fixture = await Fixture.Create(false);
        var result = await fixture.Runner.RunAsync(new(BomDefinition(), true), "TEST\\admin", default);
        Assert.Empty(fixture.Handler.Requests);
        var rows = Assert.Single(result.Sheets).Rows;
        Assert.Equal(4, rows.Count);
        Assert.Equal("DEMO-001", rows[0][3]);
        Assert.Equal(1m, rows[0][9]);
        Assert.Null(rows[1][9]);
        Assert.Equal(2m, rows[2][9]);
        Assert.Null(rows[2][20]);
        Assert.Equal("DEMO-PACKAGING-003", rows[3][20]);
        Assert.Null(rows[3][9]);
        Assert.Equal(0.5m, rows[0][22]);
        Assert.Null(rows[0][0]);
        Assert.Null(rows[0][18]);
        Assert.Contains(result.Warnings, w => w.StartsWith("Unmapped columns"));
        using var book = new XLWorkbook(new MemoryStream(ReportWorkbook.Create(fixture.Cache.Get<ReportSnapshot>("customizer-run:" + result.Id)!)));
        var sheet = book.Worksheet("Report");
        Assert.Equal(32, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(XLDataType.TimeSpan, sheet.Cell("M2").DataType);
        Assert.Equal(TimeSpan.FromMinutes(15), sheet.Cell("M2").GetTimeSpan());
        Assert.Equal("[h]:mm:ss", sheet.Cell("M2").Style.NumberFormat.Format);
        Assert.Equal(100d, sheet.Cell("N4").GetDouble());
        Assert.Equal(TimeSpan.FromHours(1), sheet.Cell("O4").GetTimeSpan());
    }

    [Fact]
    public async Task Sorting_a_bom_preserves_operation_context_on_every_input_row()
    {
        await using var fixture = await Fixture.Create(false);
        var definition = BomDefinition();
        definition.OutputSortColumn = 20;
        var result = await fixture.Runner.RunAsync(new(definition, true), "TEST\\admin", default);
        var secondInput = result.Sheets[0].Rows.Single(r => Equals(r[20], "DEMO-COMPONENT-002"));
        Assert.Equal(1m, secondInput[9]);
        Assert.Equal("Prepare Material", secondInput[10]);
        Assert.Contains(result.Warnings, w => w.Contains("sorts rows"));
    }

    [Fact]
    public async Task Bom_live_uses_only_documented_reads_and_honors_scope_and_custom_columns()
    {
        await using var fixture = await Fixture.Create();
        fixture.Handler.Response = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/items/list/v2" => """[{"id":"parent-001","number":"000045","customFields":{"BOM Number":"BOM-0045"}}]""",
            var path when path.EndsWith("operations/list") => """[{"id":"op-1","order":1,"name":"Cut"}]""",
            var path when path.EndsWith("input-items/list") => """[{"id":"input-1","routingStepId":"op-1","number":"RM-001","valueTypeUnits":0.5,"valueType":"requires"}]""",
            _ => throw new Exception("Unexpected API read")
        };
        var definition = BomDefinition();
        var inputs = definition.Sheets[0].Inputs;
        inputs["report.itemSearch"] = JsonSerializer.SerializeToElement("000045,000046");
        inputs["report.matchMode"] = JsonSerializer.SerializeToElement("startsWith");
        inputs["body.isArchived"] = JsonSerializer.SerializeToElement(false);
        inputs["body.latestRevision"] = JsonSerializer.SerializeToElement(true);
        definition.Sheets[0].Columns[0] = new("bom", "item.customFields.BOM Number", "BOM ID");
        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);
        Assert.Equal(3, result.RequestCount);
        Assert.All(fixture.Handler.Requests, r => Assert.StartsWith("https://api.fulcrumpro.us/api/items", r.Uri));
        using var body = JsonDocument.Parse(fixture.Handler.Requests[0].Body);
        Assert.Equal("000045", body.RootElement.GetProperty("numbers")[0].GetProperty("query").GetString());
        Assert.Equal("startsWith", body.RootElement.GetProperty("numbers")[0].GetProperty("mode").GetString());
        Assert.True(body.RootElement.GetProperty("latestRevision").GetBoolean());
        Assert.DoesNotContain("report.", fixture.Handler.Requests[0].Body);
        Assert.All(fixture.Handler.Requests.Skip(1), r => Assert.Contains("/parent-001/routing/", r.Uri));
        Assert.Equal("BOM-0045", result.Sheets[0].Rows[0][0]);
        Assert.Equal("RM-001", result.Sheets[0].Rows[0][20]);
    }

    [Fact]
    public async Task Bom_can_safely_read_more_than_5000_starting_items()
    {
        await using var fixture = await Fixture.Create();
        fixture.Handler.Response = request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/items/list/v2") return "[]";
            var match = System.Text.RegularExpressions.Regex.Match(request.RequestUri.Query, @"(?i)(?:[?&])skip=(\d+)");
            var skip = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var count = skip < 5000 ? 500 : 1;
            return JsonSerializer.Serialize(Enumerable.Range(skip, count)
                .Select(i => new { id = i.ToString("D24"), number = i.ToString("D6") }));
        };
        var definition = BomDefinition();
        definition.MaxRecords = 6000;

        var result = await fixture.Runner.RunAsync(new(definition), "TEST\\admin", default);

        Assert.Equal(5001, result.Sheets[0].Rows.Count);
        Assert.Equal(10013, result.RequestCount);
        Assert.Equal("005000", result.Sheets[0].Rows[^1][3]);
    }

    [Fact]
    public void Bom_orphans_and_quantity_bases_never_become_incorrect_operation_matches_or_zeroes()
    {
        var item = JsonSerializer.SerializeToElement(new { id = "item", number = "0001" });
        var steps = JsonSerializer.Deserialize<List<JsonElement>>("""[{"id":"op","order":2,"name":"Cut"}]""")!;
        var components = JsonSerializer.Deserialize<List<JsonElement>>("""[{"routingStepId":"op","number":"ZERO","valueType":"requires","valueTypeUnits":0},{"routingStepId":"op","number":"YIELD","valueType":"creates","valueTypeUnits":2},{"routingStepId":"missing","number":"ORPHAN"}]""")!;
        var warnings = new List<string>();
        var rows = InventoryBomReport.Assemble(item, steps, components, true, warnings);
        Assert.Equal(3, rows.Count);
        Assert.Equal(0m, FulcrumReportRunner.Read(rows[0], "quantityRequired"));
        Assert.Null(FulcrumReportRunner.Read(rows[1], "quantityRequired"));
        Assert.Equal(2m, FulcrumReportRunner.Read(rows[1], "operationNbr"));
        Assert.Equal("Missing Operation", FulcrumReportRunner.Read(rows[2], "matchStatus"));
        Assert.Null(FulcrumReportRunner.Read(rows[2], "operationNbr"));
        Assert.Contains(warnings, w => w.Contains("requires basis"));
        Assert.Contains(warnings, w => w.Contains("missing routing step"));
        Assert.Throws<ReportValidationException>(() => InventoryBomReport.Assemble(item, [steps[0], steps[0]], [], false, []));
        Assert.Single(InventoryBomReport.Assemble(item, [], [], false, []));
    }

    [Theory]
    [InlineData("fixedSeconds", 90, "0:01:30")]
    [InlineData("fixedMinutes", 15, "0:15:00")]
    [InlineData("fixedHours", 27, "27:00:00")]
    [InlineData("fixedDays", 2, "48:00:00")]
    [InlineData("secondsPerUnit", 45, "0:00:45")]
    [InlineData("minutesPerUnit", 5, "0:05:00")]
    [InlineData("hoursPerUnit", 2, "2:00:00")]
    [InlineData("daysPerUnit", 1, "24:00:00")]
    [InlineData("unitsPerHour", 100, "1:00:00")]
    public void Bom_time_conversion_preserves_the_declared_time_basis(string option, int time, string display)
    {
        var record = JsonSerializer.SerializeToElement(new { value = new { time, option } });
        var converted = InventoryBomReport.Time(record, "value", []);
        Assert.Equal(display, ReportPresentation.Display(converted.Duration, "duration"));
    }

    [Fact]
    public async Task Bom_supports_reference_sized_reports_above_20000_rows()
    {
        await using var fixture = await Fixture.Create();
        var items = JsonSerializer.Serialize(Enumerable.Range(0, 50).Select(i => new { id = "item-" + i, number = i.ToString("D6") }));
        var operations = JsonSerializer.Serialize(Enumerable.Range(0, 500).Select(i => new { id = "op-" + i, name = "Operation " + i, order = i }));
        fixture.Handler.Response = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/items/list/v2" => items,
            var path when path.EndsWith("operations/list") => request.RequestUri.Query.Contains("Skip=0", StringComparison.OrdinalIgnoreCase) ? operations : """[{"id":"op-500","name":"Operation 500","order":500}]""",
            _ => "[]"
        };
        var result = await fixture.Runner.RunAsync(new(BomDefinition()), "TEST\\admin", default);
        Assert.Equal(25050, result.Sheets[0].Rows.Count);
        Assert.Equal(151, result.RequestCount);
        Assert.Equal("000049", result.Sheets[0].Rows[^1][3]);
    }

    private sealed class EmptyRoleStore : IPortalRoleStore
    {
        public Task<PortalAccountLookup> FindAccountAsync(string account, CancellationToken ct) => Task.FromResult(PortalAccountLookup.Missing());
        public Task<PortalAccountLookup> RegisterPendingAccountAsync(string account, string name, CancellationToken ct) => Task.FromResult(PortalAccountLookup.Missing());
    }
    private sealed class Protector : IIntegrationSecretProtector
    {
        public string Protect(string secret) => "protected";
        public string Unprotect(string value) => "TEST_TOKEN";
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Uri, string Body, string Authorization)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Func<HttpRequestMessage, string>? Response { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization!.ToString()));
            var content = request.RequestUri.AbsolutePath == "/api/items/list/v2"
                ? "[{\"id\":\"000000000000000000000001\",\"number\":\"000045\",\"description\":\"=HYPERLINK(\\\"https://example.invalid\\\")\"}]"
                : "[{\"name\":\"Cut\"},{\"name\":\"Inspect\"}]";
            return new HttpResponseMessage(Status) { Content = new StringContent(Response?.Invoke(request) ?? content, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required PortalRoleDbContext Db { get; init; }
        public required FulcrumReportRunner Runner { get; init; }
        public required Handler Handler { get; init; }
        public required MemoryCache Cache { get; init; }
        public static async Task<Fixture> Create(bool credential = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new PortalRoleDbContext(new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options);
            await new PortalIntegrationCredentialSchemaInitializer(db).InitializeAsync();
            if (credential) { db.IntegrationCredentials.Add(new() { CredentialKey = IntegrationCredentialNames.FulcrumPublicApi, DisplayName = "Test", EncryptedSecret = "protected", UpdatedBy = "test" }); await db.SaveChangesAsync(); }
            var handler = new Handler(); var cache = new MemoryCache(new MemoryCacheOptions());
            return new() { Connection = connection, Db = db, Handler = handler, Cache = cache, Runner = new(Catalog, new HttpClient(handler), db, new Protector(), cache) };
        }
        public async ValueTask DisposeAsync() { Cache.Dispose(); await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}

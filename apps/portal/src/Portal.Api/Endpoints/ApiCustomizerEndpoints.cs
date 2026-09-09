using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Portal.Api.Data;
using Portal.Api.Services;
using Portal.Api.Services.ApiCustomizer;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class ApiCustomizerEndpoints
{
    private static readonly SemaphoreSlim RunSlots = new(2);

    public static void MapApiCustomizerEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/admin/api-customizer").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            var user = await context.HttpContext.RequestServices.GetRequiredService<PortalUserService>()
                .CurrentAsync(context.HttpContext.RequestAborted);
            if (!string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
                return Results.Problem(statusCode: 403, detail: "API Customizer is available only to Arda administrators.");
            context.HttpContext.Items["CustomizerActor"] = user.AccountName;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.Request.Method != "GET" && context.HttpContext.Request.Headers["X-Arda-Customizer"] != "1")
                return Results.Problem(statusCode: 400, detail: "Open API Customizer in Arda to submit this request.");
            try { return await next(context); }
            catch (ReportValidationException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
            catch (DbUpdateConcurrencyException) { return Results.Problem(statusCode: 409, detail: "Another admin updated this layout. Reload it before saving."); }
            catch (OperationCanceledException) { return Results.Problem(statusCode: 408, detail: "The report was cancelled or timed out. Narrow the parent filters and try again."); }
            catch (HttpRequestException) { return Results.Problem(statusCode: 502, detail: "The Arda server could not reach Fulcrum. Check the connection and try again."); }
            catch (JsonException) { return Results.Problem(statusCode: 502, detail: "A response did not match the expected data format. No workbook was created."); }
        });
        routes.MapGet("/catalogue", ([FromServices] FulcrumReportCatalog catalog) => Results.Ok(catalog.Catalog));
        routes.MapGet("/reports", async ([FromServices] PortalRoleDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Set<CustomizerReportRecord>().AsNoTracking().ToListAsync(ct))
                .OrderBy(r => r.Name).Select(ToDto)));
        routes.MapPut("/reports/{id:guid}", async (Guid id, [FromBody] SavedReportRequest request,
            [FromServices] FulcrumReportRunner runner, [FromServices] PortalRoleDbContext db, HttpContext context, CancellationToken ct) =>
        {
            runner.Validate(request.Definition);
            var record = await db.Set<CustomizerReportRecord>().FindAsync([id], ct);
            if (record is null)
            {
                if (request.Version != 0) return Results.Problem(statusCode: 409, detail: "This saved layout was removed. Save a new copy.");
                record = new() { Id = id };
                db.Add(record);
            }
            else if (request.Version != record.Version)
                return Results.Problem(statusCode: 409, detail: "Another admin updated this layout. Reload it before saving.");
            var previous = record.DefinitionJson;
            record.Name = request.Definition.Name;
            record.DefinitionJson = JsonSerializer.Serialize(request.Definition, FulcrumReportRunner.JsonOptions);
            record.Version++;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            record.UpdatedBy = Actor(context);
            Audit(db, context, "Save Layout", record.Name, JsonSerializer.Serialize(new { previous, current = record.DefinitionJson }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(record));
        });
        routes.MapDelete("/reports/{id:guid}", async (Guid id, int version, [FromServices] PortalRoleDbContext db, HttpContext context, CancellationToken ct) =>
        {
            var record = await db.Set<CustomizerReportRecord>().FindAsync([id], ct);
            if (record is null) return Results.NotFound();
            if (record.Version != version) return Results.Problem(statusCode: 409, detail: "This layout changed. Reload it before deleting.");
            Audit(db, context, "Delete Layout", record.Name, record.DefinitionJson);
            db.Remove(record);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        routes.MapPost("/preview", async ([FromBody] ReportRunRequest request, [FromServices] FulcrumReportRunner runner,
            [FromServices] PortalRoleDbContext db, HttpContext context, CancellationToken ct) =>
        {
            if (!await RunSlots.WaitAsync(0, ct)) return Results.Problem(statusCode: 429, detail: "Two reports are already running. Wait for one to finish and retry.");
            try
            {
                var result = await runner.RunAsync(request, Actor(context), ct);
                Audit(db, context, request.Sample ? "Sample Preview" : "Fulcrum Pull", result.Name,
                    JsonSerializer.Serialize(new { result.Id, result.RequestCount, Rows = result.Sheets.Sum(s => s.Rows.Count), request.Definition }, FulcrumReportRunner.JsonOptions));
                await db.SaveChangesAsync(ct);
                return Results.Ok(result);
            }
            finally { RunSlots.Release(); }
        });
        routes.MapGet("/runs/{id:guid}/excel", async (Guid id, [FromServices] IMemoryCache cache,
            [FromServices] PortalRoleDbContext db, HttpContext context, CancellationToken ct) =>
        {
            if (!cache.TryGetValue<ReportSnapshot>("customizer-run:" + id, out var snapshot) || snapshot is null
                || !string.Equals(snapshot.Actor, Actor(context), StringComparison.OrdinalIgnoreCase))
                return Results.Problem(statusCode: 404, detail: "This preview expired or belongs to another administrator. Generate a fresh preview.");
            var bytes = ReportWorkbook.Create(snapshot);
            Audit(db, context, "Excel Download", snapshot.Result.Name, id.ToString());
            await db.SaveChangesAsync(ct);
            var name = System.Text.RegularExpressions.Regex.Replace(snapshot.Result.Name, @"[^\w .-]", "_");
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                (snapshot.Result.Sample ? "SAMPLE - " : "") + name + ".xlsx");
        });
        routes.MapGet("/history", async ([FromServices] PortalRoleDbContext db, CancellationToken ct) =>
        {
            var query = db.Database.IsSqlite()
                ? db.Set<CustomizerAuditRecord>().FromSqlRaw("SELECT * FROM ApiCustomizerAudits ORDER BY OccurredAt DESC LIMIT 100")
                : db.Set<CustomizerAuditRecord>().OrderByDescending(x => x.OccurredAt).Take(100);
            return Results.Ok((await query.AsNoTracking().ToListAsync(ct))
                .OrderByDescending(x => x.OccurredAt).Select(x => new { x.Id, x.Actor, x.Action, x.ReportName, x.OccurredAt }));
        });
    }

    private static string Actor(HttpContext context) => (string)context.Items["CustomizerActor"]!;
    private static object ToDto(CustomizerReportRecord record) => new
    {
        record.Id, record.Name, record.Version, record.UpdatedAt, record.UpdatedBy,
        Definition = JsonSerializer.Deserialize<ReportDefinition>(record.DefinitionJson, FulcrumReportRunner.JsonOptions)
    };
    private static void Audit(PortalRoleDbContext db, HttpContext context, string action, string name, string detail) =>
        db.Add(new CustomizerAuditRecord { Id = Guid.NewGuid(), Actor = Actor(context), Action = action,
            ReportName = name, Detail = detail, OccurredAt = DateTimeOffset.UtcNow });
}

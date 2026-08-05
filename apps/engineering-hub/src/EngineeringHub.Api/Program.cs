using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Endpoints;
using EngineeringHub.Api.Services;
using SonAero.Platform.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<EngineeringUserService>();
builder.Services.AddScoped<EngineeringAccessPreviewService>();
builder.Services.AddScoped<IEngineeringRoleStore, EngineeringRoleStore>();
builder.Services.AddScoped<EngineeringAccessSchemaInitializer>();
builder.Services.AddScoped<EngineeringAccessSeeder>();
builder.Services.AddScoped<EngineeringSearchService>();
builder.Services.AddScoped<EngineeringDemoDataSeeder>();
builder.Services.AddScoped<MylarCustodyService>();
builder.Services.AddScoped<EngineeringSchemaInitializer>();
builder.Services.Configure<DrawingStorageOptions>(builder.Configuration.GetSection(DrawingStorageOptions.SectionName));
builder.Services.AddSingleton<IDrawingFileStore, DrawingFileStore>();
builder.Services.AddDbContext<EngineeringDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["EngineeringDatabase:Provider"] ?? "Sqlite";
    var connectionString = configuration.GetConnectionString("EngineeringStore") ?? "Data Source=engineering-hub.db";
    if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        options.UseSqlServer(connectionString);
    else
        options.UseSqlite(connectionString);
});
builder.Services.AddDbContext<EngineeringRoleDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    var connectionString = configuration.GetConnectionString("RoleStore");
    if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
    }
    else
    {
        options.UseSqlServer(connectionString);
    }
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var authMode = builder.Configuration["Authentication:Mode"]
    ?? (builder.Environment.IsDevelopment() ? "Development" : "Windows");

if (string.Equals(authMode, "Windows", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
else
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName, _ => { });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(EngineeringAuthorization.ReadPolicy, policy =>
        policy.RequireClaim(
            EngineeringAuthorization.PermissionClaimType,
            EngineeringAuthorization.ReadPermission));
    foreach (var permission in EngineeringPermissions.All)
    {
        options.AddPolicy(permission.Key, policy =>
            policy.RequireClaim(EngineeringAuthorization.PermissionClaimType, permission.Key));
    }
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<EngineeringAccessSeeder>().SeedAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<EngineeringSchemaInitializer>().InitializeAsync(CancellationToken.None);
    if (app.Environment.IsDevelopment() && builder.Configuration.GetValue("Engineering:SeedDemoData", true))
        await scope.ServiceProvider.GetRequiredService<EngineeringDemoDataSeeder>().SeedAsync(CancellationToken.None);
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }
        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/health"))
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync();
        return;
    }

    var previews = context.RequestServices.GetRequiredService<EngineeringAccessPreviewService>();
    var previewEndpoint = context.Request.Path.StartsWithSegments("/access-preview/start")
        || context.Request.Path.StartsWithSegments("/access-preview/end");
    if (previewEndpoint)
    {
        await next();
        return;
    }

    var normalLaunch = context.Request.Query.ContainsKey("launch");
    if (normalLaunch)
        await previews.RevokeAndClearAsync(context, context.RequestAborted);

    var users = context.RequestServices.GetRequiredService<EngineeringUserService>();
    if (!normalLaunch && context.Request.Cookies.ContainsKey(EngineeringAccessPreviewService.CookieName))
    {
        var previewAccess = await previews.ResolveActiveAsync(context, context.RequestAborted);
        if (previewAccess is null)
        {
            previews.DeleteCookie(context);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorDto(
                "InvalidAccessPreview",
                "This access preview is invalid, expired, or no longer authorized. Return to the Hub and start a new preview."));
            return;
        }

        context.Items[EngineeringAuthorization.AccessItem] = previewAccess;
        context.User = users.AttachAccess(context.User, previewAccess);
        if (!AccessPreviewRequests.IsReadOnlyMethod(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorDto(
                "PreviewReadOnly",
                "Access preview is read-only. Return to Admin to make changes."));
            return;
        }

        await next();
        return;
    }

    var access = await users.ResolveAccessAsync(context.User.Identity?.Name, context.RequestAborted);
    if (access is null || !access.IsEnabled)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorDto(
            "ModuleAccessDenied",
            "Your account does not have active access to the Engineering module."));
        return;
    }

    context.Items[EngineeringAuthorization.AccessItem] = access;
    context.User = users.AttachAccess(context.User, access);
    await next();
});

app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/access-preview/start", async (
    HttpContext context,
    EngineeringAccessPreviewService previews,
    CancellationToken cancellationToken) =>
{
    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new ErrorDto("InvalidAccessPreview", "A preview token is required."));
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var result = await previews.StartAsync(context, form["token"].ToString(), cancellationToken);
    return result.Succeeded
        ? Results.Redirect("/")
        : Results.Json(new ErrorDto(result.ErrorCode!, result.ErrorMessage!), statusCode: StatusCodes.Status403Forbidden);
}).DisableAntiforgery().RequireAuthorization();

app.MapGet("/access-preview/end", async (
    HttpContext context,
    EngineeringAccessPreviewService previews,
    CancellationToken cancellationToken) =>
{
    await previews.RevokeAndClearAsync(context, cancellationToken);
    return Results.Redirect(previews.GetReturnToAdminUrl(context));
}).RequireAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api")
    .RequireAuthorization(EngineeringAuthorization.ReadPolicy);
api.MapDrawingEndpoints();
api.MapDrawingOperationalEndpoints();
api.MapEngineeringAccessEndpoints();

api.MapGet("/me", async (EngineeringUserService users, HttpContext httpContext, CancellationToken cancellationToken) =>
    await users.CurrentAsync(
        httpContext.User,
        httpContext.Items[EngineeringAuthorization.AccessItem] as EngineeringModuleAccess,
        cancellationToken));

api.MapGet("/dashboard", async (
    string? query,
    string? category,
    string? customer,
    string? status,
    bool? reviewQueue,
    EngineeringSearchService search,
    HttpContext http,
    CancellationToken cancellationToken) =>
    Results.Ok(await search.GetDashboardAsync(
        query,
        category,
        customer,
        status,
        reviewQueue ?? false,
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, EngineeringPermissions.PendingRevisionsView),
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, EngineeringPermissions.SpecificationsView),
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, EngineeringPermissions.SupportingDocumentsView),
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, EngineeringPermissions.MylarView),
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, EngineeringPermissions.ToolingView),
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, EngineeringPermissions.CompoundDataView),
        cancellationToken)))
    .RequireAuthorization(EngineeringPermissions.DashboardView);

api.MapGet("/navigation", () => Results.Ok(new EngineeringModuleDto(
    ApplicationModules.Engineering,
    "Engineering Module",
    "Engineering records and controlled drawing workflows.",
    "Module access: Viewer, Editor, or Admin",
    [
        new EngineeringSectionDto(
            "dashboard",
            "Dashboard",
            "Global engineering search across parts, tools, drawings, compounds, reports, specifications, and related documents.",
            "Search workspace ready",
            [
                "Global engineering search",
                "Grouped category results",
                "Cross-reference lookup"
            ]),
        new EngineeringSectionDto(
            "drawing-document-control",
            "Drawing and document control",
            "Manage drawing indexes, revision packages, release status, and controlled engineering references.",
            "Testing shell ready",
            [
                "Drawing register",
                "Revision queue",
                "Release approvals"
            ]),
        new EngineeringSectionDto(
            "tooling-management",
            "Tooling management",
            "Track tooling records, ownership, maintenance checkpoints, and storage assignments in a dedicated workspace.",
            "Testing shell ready",
            [
                "Tool inventory",
                "Maintenance log",
                "Storage map"
            ]),
        new EngineeringSectionDto(
            "compound-test-data-management",
            "Compound and test-data management",
            "Organize compound specifications, certification packets, and engineering test results without touching project schedules.",
            "Testing shell ready",
            [
                "Compound library",
                "Certification records",
                "Test data archive"
            ])
    ])));

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

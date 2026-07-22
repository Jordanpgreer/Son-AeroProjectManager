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
builder.Services.AddScoped<IEngineeringRoleStore, EngineeringRoleStore>();
builder.Services.AddSingleton<EngineeringSearchService>();
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

builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<EngineeringDbContext>().Database.EnsureCreatedAsync();
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
app.UseAuthorization();

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

    var users = context.RequestServices.GetRequiredService<EngineeringUserService>();
    var role = await users.ResolveRoleAsync(context.User.Identity?.Name, context.RequestAborted);
    if (!string.Equals(role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorDto(
            "AdminOnly",
            "This engineering module is restricted to administrators while testing is in progress."));
        return;
    }

    context.User = users.AttachRole(context.User, role);
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapDrawingEndpoints();

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/me", async (EngineeringUserService users, HttpContext httpContext, CancellationToken cancellationToken) =>
    await users.CurrentAsync(httpContext.User, cancellationToken));

api.MapGet("/dashboard", (string? query, EngineeringSearchService search) =>
    Results.Ok(search.GetDashboard(query)));

api.MapGet("/navigation", () => Results.Ok(new EngineeringModuleDto(
    "engineering-hub",
    "Engineering Module",
    "Standalone testing workspace for engineering records and controls. This module is isolated from Project Tracker while the workflow is under review.",
    "Testing access: Admin only",
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

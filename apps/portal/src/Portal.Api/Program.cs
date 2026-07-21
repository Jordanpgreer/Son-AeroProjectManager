using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Auth;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Models;
using Portal.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ApplicationRegistry>();
builder.Services.AddScoped<PortalUserService>();
builder.Services.AddScoped<IPortalRoleStore, PortalRoleStore>();
builder.Services.AddHttpClient<TrackerPreviewService>();
builder.Services.AddDbContext<PortalRoleDbContext>((serviceProvider, options) =>
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
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Windows Authentication in production; a development handler for local runs.
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

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/me", (PortalUserService users, CancellationToken cancellationToken) => users.CurrentAsync(cancellationToken))
    .RequireAuthorization();

// The application catalog, filtered to what the current user's role is allowed to see.
// Hiding a card here is a usability convenience, not a security boundary — each target
// application enforces its own authorization independently.
api.MapGet("/apps", async (PortalUserService users, ApplicationRegistry registry, CancellationToken cancellationToken) =>
{
    var role = (await users.CurrentAsync(cancellationToken)).Role;
    return registry.GetVisibleFor(role).Select(ToApplicationDto).ToList();
}).RequireAuthorization();

// Live "minimized dashboard" data for the Project Tracker card. Best-effort and read-only.
api.MapGet("/preview/project-tracker", async (TrackerPreviewService preview, CancellationToken cancellationToken) =>
{
    var snapshot = await preview.GetProjectTrackerAsync(cancellationToken);
    return snapshot is null ? Results.NoContent() : Results.Ok(snapshot);
}).RequireAuthorization();

app.MapFallbackToFile("index.html");

app.Run();

static ApplicationDto ToApplicationDto(ApplicationEntry entry) => new(
    entry.Id,
    entry.Name,
    entry.Description,
    entry.Category,
    entry.Icon,
    entry.Url,
    entry.Order,
    entry.Status,
    !string.IsNullOrWhiteSpace(entry.PreviewPath));

// Exposed for integration testing with WebApplicationFactory.
public partial class Program;

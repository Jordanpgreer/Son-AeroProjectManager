using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Endpoints;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<QualityAssuranceUserService>();
builder.Services.AddScoped<IQualityAssuranceAccessStore, QualityAssuranceAccessStore>();
builder.Services.AddScoped<QualityAssignmentService>();
builder.Services.AddScoped<QualityShipmentService>();
builder.Services.AddScoped<QualityShipmentImportService>();
builder.Services.AddScoped<QualityShippingLayoutService>();
builder.Services.AddScoped<QualityPermissionSeeder>();
builder.Services.AddDbContext<QualityAssuranceAccessDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    var connectionString = configuration.GetConnectionString("ModuleAccessStore")
        ?? throw new InvalidOperationException("ConnectionStrings:ModuleAccessStore is required.");
    if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(connectionString);
    else
        options.UseSqlServer(connectionString);
});
builder.Services.AddDbContext<QualityAssuranceDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["QualityDatabase:Provider"]
        ?? configuration["Database:Provider"]
        ?? "SqlServer";
    var connectionString = configuration.GetConnectionString("QualityStore")
        ?? throw new InvalidOperationException("ConnectionStrings:QualityStore is required.");
    if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(connectionString);
    else
        options.UseSqlServer(connectionString);
});
builder.Services.AddCors(options => options.AddPolicy("HubAdmin", policy =>
{
    var origins = new[]
    {
        builder.Configuration["Portal:Url"],
        "http://localhost:5140",
        "http://127.0.0.1:5140",
        "https://hub.son4l.local",
        "https://SON-IIS2:6140"
    }.Where(origin => !string.IsNullOrWhiteSpace(origin)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    policy.WithOrigins(origins!).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));
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
else if (string.Equals(authMode, "Development", StringComparison.OrdinalIgnoreCase)
    && builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName,
            _ => { });
}
else
{
    throw new InvalidOperationException(
        $"Authentication mode '{authMode}' is invalid for environment '{builder.Environment.EnvironmentName}'.");
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(QualityAssurancePolicies.ModuleView, policy => policy.RequireClaim(
        QualityAssurancePolicies.PermissionClaim,
        QualityAssurancePermissions.ModuleView));
    foreach (var permission in QualityAssurancePermissions.All)
    {
        options.AddPolicy(permission.Key, policy => policy.RequireClaim(
            QualityAssurancePolicies.PermissionClaim,
            permission.Key));
    }
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<QualityPermissionSeeder>()
        .SeedAsync(CancellationToken.None);
    var db = scope.ServiceProvider.GetRequiredService<QualityAssuranceDbContext>();
    await db.Database.MigrateAsync();
}

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

    var users = context.RequestServices.GetRequiredService<QualityAssuranceUserService>();
    var access = await users.ResolveAccessAsync(context.User, context.RequestAborted);
    if (access is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorDto(
            "QualityAssuranceAccessDenied",
            "Your active SON-AERO account does not have permission to access Quality Assurance."));
        return;
    }

    context.Items[QualityAssurancePolicies.AccessItem] = access;
    context.User = QualityAssurancePolicies.Attach(context.User, access);
    await next();
});
app.UseRouting();
app.UseCors("HubAdmin");
app.UseAuthorization();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (UnauthorizedAccessException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorDto("Forbidden", exception.Message));
    }
    catch (ArgumentException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ErrorDto("ValidationError", exception.Message));
    }
    catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new ErrorDto(
            "ConcurrencyConflict",
            "This record changed after you opened it. Refresh and try again."));
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
api.MapGet("/me", (HttpContext context) =>
{
    var access = context.Items[QualityAssurancePolicies.AccessItem]
        as QualityAssuranceAccessProfile;
    return access is null
        ? Results.Forbid()
        : Results.Ok(QualityAssuranceUserService.Current(access));
}).RequireAuthorization(QualityAssurancePolicies.ModuleView);
api.RequireAuthorization(QualityAssurancePolicies.ModuleView).MapQualityShippingEndpoints();

app.MapFallback("/api/{**path}", async context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsJsonAsync(new ErrorDto(
        "NotFound",
        "The requested API endpoint does not exist."));
});
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

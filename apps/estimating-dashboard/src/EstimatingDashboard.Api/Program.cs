using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<EstimatingUserService>();
builder.Services.AddScoped<IEstimatingAccessStore, EstimatingAccessStore>();
builder.Services.AddDbContext<EstimatingAccessDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    var connectionString = configuration.GetConnectionString("ModuleAccessStore")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:ModuleAccessStore is required.");
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
else if (string.Equals(authMode, "Development", StringComparison.OrdinalIgnoreCase)
    && builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName, _ => { });
}
else
{
    throw new InvalidOperationException(
        $"Authentication mode '{authMode}' is invalid for environment '{builder.Environment.EnvironmentName}'.");
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        EstimatingPolicies.Viewer,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.View));
    options.AddPolicy(
        EstimatingPolicies.Editor,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.ManageQuotes));
    options.AddPolicy(
        EstimatingPolicies.Admin,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.AdministerSettings));
    options.AddPolicy(
        EstimatingPolicies.Calculate,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.Calculate));
    options.AddPolicy(
        EstimatingPolicies.ManageInputs,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.ManageInputs));
    options.AddPolicy(
        EstimatingPolicies.AdministerRates,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.AdministerRates));
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
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

    var users = context.RequestServices.GetRequiredService<EstimatingUserService>();
    var access = await users.ResolveAccessAsync(context.User, context.RequestAborted);
    if (access is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ErrorDto(
            "EstimatingAccessDenied",
            "Your account does not have enabled access to the Estimating module."));
        return;
    }

    context.Items[EstimatingPolicies.AccessItem] = access;
    context.User = EstimatingPolicies.Attach(context.User, access);
    await next();
});

app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();
api.MapGet("/me", (HttpContext context) =>
{
    var access = context.Items[EstimatingPolicies.AccessItem]
        as EstimatingAccessProfile;
    return access is null
        ? Results.Forbid()
        : Results.Ok(EstimatingUserService.Current(access));
}).RequireAuthorization(EstimatingPolicies.Viewer);

app.MapFallback("/api/{**path}", async context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsJsonAsync(new
    {
        code = "NotFound",
        message = "The requested API endpoint does not exist."
    });
});

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

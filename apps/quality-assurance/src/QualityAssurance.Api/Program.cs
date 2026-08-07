using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<QualityAssuranceUserService>();
builder.Services.AddScoped<IQualityAssuranceAccessStore, QualityAssuranceAccessStore>();
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
    options.AddPolicy(
        QualityAssurancePolicies.Administrator,
        policy => policy.RequireClaim(
            QualityAssurancePolicies.PermissionClaim,
            QualityAssurancePermissions.View));
});

var app = builder.Build();

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
            "Only active SON-AERO administrators can access Quality Assurance."));
        return;
    }

    context.Items[QualityAssurancePolicies.AccessItem] = access;
    context.User = QualityAssurancePolicies.Attach(context.User, access);
    await next();
});
app.UseAuthorization();

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
}).RequireAuthorization(QualityAssurancePolicies.Administrator);

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

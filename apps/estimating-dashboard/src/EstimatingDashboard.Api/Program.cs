using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Services;
using SonAero.Platform.Integrations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<EstimatingUserService>();
builder.Services.AddScoped<EstimatingAccessPreviewService>();
builder.Services.AddScoped<IEstimatingAccessStore, EstimatingAccessStore>();
builder.Services.AddScoped<EstimatingHistorySchemaInitializer>();
builder.Services.AddScoped<EstimatingHistoryQueryService>();
builder.Services.AddScoped<EstimatingEstimatorSettingsService>();
builder.Services.AddScoped<EstimatingHistoryImportService>();
builder.Services.AddScoped<EstimatingHistoryReportService>();
builder.Services.AddScoped<EstimatingHistoryGridExportService>();
builder.Services.AddScoped<EstimatorSummaryReportService>();
builder.Services.AddSingleton<EstimatingHistoryReviewStore>();
builder.Services.Configure<FulcrumQuoteSyncOptions>(
    builder.Configuration.GetSection(FulcrumQuoteSyncOptions.SectionName));
builder.Services.AddOptions<EnterpriseQuoteSyncScheduleOptions>()
    .Configure<IConfiguration>((settings, configuration) => settings.BindConfiguration(configuration));
builder.Services.AddSingleton<SonAero.Platform.Security.IIntegrationSecretProtector,
    SonAero.Platform.Security.MachineIntegrationSecretProtector>();
builder.Services.AddScoped<IIntegrationCredentialReader, IntegrationCredentialReader>();
builder.Services.AddScoped<IEnterpriseProviderSource, EstimatingEnterpriseProviderSource>();
builder.Services.AddHttpClient<FulcrumQuoteClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddScoped<IEstimatingQuoteProvider, FulcrumEstimatingQuoteProvider>();
builder.Services.AddScoped<IEstimatingQuoteProvider, AcumaticaEstimatingQuoteProvider>();
builder.Services.AddScoped<EnterpriseQuoteSyncService>();
builder.Services.AddScoped<FulcrumEstimateImportService>();
builder.Services.AddScoped<FulcrumEstimateExportService>();
builder.Services.AddScoped<EstimatingOperationMappingService>();
builder.Services.AddSingleton<FulcrumEstimateReviewStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<FulcrumQuoteSyncWorker>();
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
builder.Services.AddCors(options => options.AddPolicy("HubAdmin", policy =>
{
    var origins = new[]
    {
        builder.Configuration["Portal:Url"],
        "http://localhost:5140",
        "http://127.0.0.1:5140",
        "https://hub.son4l.local",
        "https://SON-IIS2:6140"
    }.Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
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
    options.AddPolicy(
        EstimatingPolicies.ViewHistory,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.ViewHistory));
    options.AddPolicy(
        EstimatingPolicies.ImportHistory,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.ImportHistory));
    options.AddPolicy(
        EstimatingPolicies.ManageHistory,
        policy => policy.RequireClaim(
            EstimatingPolicies.PermissionClaim,
            EstimatingPermissions.ManageHistory));
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider
        .GetRequiredService<EstimatingHistorySchemaInitializer>()
        .InitializeAsync();
}

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

app.UseRouting();
app.UseCors("HubAdmin");
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

    var previews = context.RequestServices.GetRequiredService<EstimatingAccessPreviewService>();
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

    var users = context.RequestServices.GetRequiredService<EstimatingUserService>();
    if (!normalLaunch && context.Request.Cookies.ContainsKey(EstimatingAccessPreviewService.CookieName))
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

        context.Items[EstimatingPolicies.AccessItem] = previewAccess;
        context.User = EstimatingPolicies.Attach(context.User, previewAccess);
        if (!SonAero.Platform.Security.AccessPreviewRequests.IsReadOnlyMethod(context.Request.Method))
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

app.MapPost("/access-preview/start", async (
    HttpContext context,
    EstimatingAccessPreviewService previews,
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
    EstimatingAccessPreviewService previews,
    CancellationToken cancellationToken) =>
{
    await previews.RevokeAndClearAsync(context, cancellationToken);
    return Results.Redirect(previews.GetReturnToAdminUrl(context));
}).RequireAuthorization();

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
api.MapEstimatingHistoryEndpoints();
api.MapFulcrumEstimateEndpoints();

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

using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using SmallBusinessSubcontracting.Api;
using SonAero.Platform.Security;

var builder = WebApplication.CreateBuilder(args);
SubcontractingDatabaseConfiguration.Validate(builder.Configuration, builder.Environment);

builder.Services.AddDbContext<SubcontractingDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var provider = configuration["SubcontractingDatabase:Provider"] ?? "SqlServer";
    var connection = configuration.GetConnectionString("SubcontractingStore")
        ?? throw new InvalidOperationException("ConnectionStrings:SubcontractingStore is required.");
    SubcontractingDatabaseConfiguration.Configure(options, provider, connection);
});
builder.Services.AddDbContext<RoleStoreDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    var connection = configuration.GetConnectionString("RoleStore")
        ?? throw new InvalidOperationException("ConnectionStrings:RoleStore is required.");
    SubcontractingDatabaseConfiguration.Configure(options, provider, connection);
});
builder.Services.AddSingleton<IIntegrationSecretProtector, MachineIntegrationSecretProtector>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SubcontractingAccessService>();
builder.Services.AddScoped<VendorService>();
builder.Services.AddSingleton<VendorDocumentStore>();
builder.Services.AddHttpClient<IFulcrumVendorClient, FulcrumVendorClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
            DevelopmentAuthenticationHandler.SchemeName,
            _ => { });
}

builder.Services.AddAuthorization(options =>
{
    foreach (var permission in SmallBusinessSubcontractingPermissions.All)
    {
        options.AddPolicy(permission.Key, policy => policy.RequireClaim(
            SubcontractingAuthorization.PermissionClaim,
            permission.Key));
    }
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SubcontractingDbContext>();
    await SubcontractingDatabaseInitializer.InitializeAsync(db, app.Environment.IsDevelopment());
    await scope.ServiceProvider.GetRequiredService<VendorDocumentStore>().VerifyWritableAsync(CancellationToken.None);
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (VendorValidationException exception)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { detail = exception.Message });
    }
    catch (VendorConflictException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { detail = exception.Message });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Small Business Subcontracting request failed.");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            detail = app.Environment.IsDevelopment()
                ? exception.Message
                : "The request could not be completed. Contact an administrator if the problem continues."
        });
    }
});

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

    var accessService = context.RequestServices.GetRequiredService<SubcontractingAccessService>();
    var access = await accessService.ResolveAsync(context.User.Identity.Name, context.RequestAborted);
    if (access is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            detail = "Your account does not have active access to Small Business Subcontracting."
        });
        return;
    }
    context.Items[SubcontractingAuthorization.AccessItem] = access;
    context.User = accessService.Attach(context.User, access);
    await next();
});
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", SubcontractingReadiness.CheckAsync);
app.MapGroup("/api")
    .RequireAuthorization(SmallBusinessSubcontractingPermissions.ModuleView)
    .MapVendorEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

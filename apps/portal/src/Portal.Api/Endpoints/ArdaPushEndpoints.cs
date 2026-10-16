using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Portal.Api.Configuration;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Services;
using SonAero.Platform.Notifications;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class ArdaPushEndpoints
{
    internal const string ModuleClientHeader = "X-Arda-Client";
    internal const string ModuleClientHeaderValue = "module-presence-v1";

    public static IEndpointRouteBuilder MapArdaPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var authenticated = endpoints.MapGroup("/api/push").RequireAuthorization();
        authenticated.MapGet("/public-key", GetPublicKey);
        authenticated.MapGet("/status", GetStatusAsync);
        authenticated.MapPost("/subscriptions", UpsertSubscriptionAsync);
        authenticated.MapPost("/presence", RecordPresenceAsync).RequireCors("ArdaModules");
        authenticated.MapPost("/foreground/claim", ClaimForegroundAsync).RequireCors("ArdaModules");
        authenticated.MapPost("/foreground/{notificationId:long}/ack", AcknowledgeForegroundAsync)
            .RequireCors("ArdaModules");
        authenticated.MapGet("/open/{notificationId:long}", OpenAsync);
        authenticated.MapGet("/inbox", GetInboxAsync).RequireCors("ArdaModules");
        authenticated.MapPost("/inbox/{id}/read", MarkInboxReadAsync).RequireCors("ArdaModules");
        authenticated.MapPost("/inbox/{id}/dismiss", DismissInboxAsync).RequireCors("ArdaModules");
        authenticated.MapPost("/inbox/clear", ClearInboxAsync).RequireCors("ArdaModules");
        authenticated.MapGet("/inbox/{id}/open", OpenInboxAsync);

        endpoints.MapPost("/api/push/producers/{sourceModule}/notifications", EnqueueAsync);
        return endpoints;
    }

    public static IResult GetPublicKey(IOptions<WebPushOptions> options) =>
        Results.Ok(new ArdaPushPublicKeyDto(
            options.Value.IsConfigured ? options.Value.PublicKey : string.Empty,
            options.Value.IsConfigured));

    private static async Task<IResult> GetStatusAsync(
        PortalUserService users,
        ArdaPushSubscriptionService subscriptions,
        IOptions<WebPushOptions> options,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        var count = await subscriptions.RegisteredDeviceCountAsync(user.AccountName, cancellationToken);
        return Results.Ok(new ArdaPushStatusDto(
            options.Value.IsConfigured,
            count > 0,
            count,
            "Browser permission is centrally managed by IT policy."));
    }

    private static async Task<IResult> UpsertSubscriptionAsync(
        ArdaPushSubscriptionUpsertDto request,
        PortalUserService users,
        ArdaPushSubscriptionService subscriptions,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        var result = await subscriptions.UpsertAsync(user.AccountName, request, cancellationToken);
        return result.Status switch
        {
            ArdaPushSubscriptionUpsertStatus.Saved => Results.NoContent(),
            ArdaPushSubscriptionUpsertStatus.Invalid => Results.ValidationProblem(result.Errors!),
            ArdaPushSubscriptionUpsertStatus.UnknownUser => Results.Forbid(),
            ArdaPushSubscriptionUpsertStatus.EndpointOwnedByAnotherUser => Results.Conflict(
                "This browser push subscription already belongs to another Arda account."),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> RecordPresenceAsync(
        HttpRequest httpRequest,
        PortalUserService users,
        ApplicationRegistry registry,
        ArdaPresenceTracker presence,
        CancellationToken cancellationToken)
    {
        if (!IsModuleClientRequest(httpRequest)) return Results.BadRequest();
        var request = await ReadRequestAsync<ArdaPushPresenceRequest>(httpRequest, cancellationToken);
        if (request is null) return Results.BadRequest();
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        if (!ValidModule(request.ModuleId, registry)
            || string.IsNullOrWhiteSpace(request.ClientId)
            || request.ClientId.Length > 128)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["client"] = ["A valid Arda module and client id are required."]
            });
        presence.Record(user.AccountName, request.ModuleId!, request.ClientId!, request.Visible);
        return Results.NoContent();
    }

    private static async Task<IResult> ClaimForegroundAsync(
        HttpRequest httpRequest,
        PortalUserService users,
        ArdaPushForegroundService foreground,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        if (!IsModuleClientRequest(httpRequest)) return Results.BadRequest();
        var request = await ReadRequestAsync<ArdaPushForegroundRequest>(httpRequest, cancellationToken);
        if (request is null) return Results.BadRequest();
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        var sources = await inbox.AuthorizedSourceIdsAsync(user, cancellationToken);
        var claimed = await foreground.ClaimAsync(user.AccountName, request, sources, cancellationToken);
        return claimed is null ? Results.Forbid() : Results.Ok(claimed);
    }

    private static async Task<IResult> AcknowledgeForegroundAsync(
        long notificationId,
        HttpRequest httpRequest,
        PortalUserService users,
        ArdaPushForegroundService foreground,
        CancellationToken cancellationToken)
    {
        if (!IsModuleClientRequest(httpRequest)) return Results.BadRequest();
        var request = await ReadRequestAsync<ArdaPushForegroundRequest>(httpRequest, cancellationToken);
        if (request is null) return Results.BadRequest();
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        return await foreground.AcknowledgeAsync(
            notificationId, user.AccountName, request, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> OpenAsync(
        long notificationId,
        PortalUserService users,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        var target = await inbox.OpenAsync($"push:{notificationId}", user, cancellationToken);
        return target is null ? Results.NotFound() : Results.Redirect(target);
    }

    private static async Task<IResult> GetInboxAsync(
        PortalUserService users,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        return Results.Ok(await inbox.GetAsync(user, cancellationToken));
    }

    internal static async Task<IResult> MarkInboxReadAsync(
        string id,
        HttpRequest request,
        ApplicationRegistry registry,
        PortalUserService users,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        if (!ArdaInboxRequestIntegrity.IsTrustedOrigin(request, registry)) return Results.BadRequest();
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        return await inbox.MarkReadAsync(id, user, cancellationToken)
            ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> DismissInboxAsync(
        string id,
        HttpRequest request,
        ApplicationRegistry registry,
        PortalUserService users,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        if (!ArdaInboxRequestIntegrity.IsTrustedOrigin(request, registry)) return Results.BadRequest();
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != PortalAccountStatus.Configured) return Results.Forbid();
        return await inbox.DismissAsync(id, user, cancellationToken)
            ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> ClearInboxAsync(
        HttpRequest request,
        ApplicationRegistry registry,
        PortalUserService users,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        if (!ArdaInboxRequestIntegrity.IsTrustedOrigin(request, registry)) return Results.BadRequest();
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != PortalAccountStatus.Configured) return Results.Forbid();
        var count = await inbox.ClearAsync(user, cancellationToken);
        return Results.Ok(new { clearedCount = count });
    }

    private static async Task<IResult> OpenInboxAsync(
        string id,
        PortalUserService users,
        ArdaGlobalInboxService inbox,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (user.AccountStatus != Portal.Api.Dtos.PortalAccountStatus.Configured) return Results.Forbid();
        var target = await inbox.OpenAsync(id, user, cancellationToken);
        return target is null ? Results.NotFound() : Results.Redirect(target);
    }

    private static async Task<IResult> EnqueueAsync(
        string sourceModule,
        [FromHeader(Name = "X-Arda-Push-Key")] string? producerKey,
        ArdaPushNotificationRequest request,
        ArdaPushProducerService producer,
        CancellationToken cancellationToken)
    {
        var result = await producer.EnqueueAsync(
            sourceModule, producerKey, request, cancellationToken);
        return result.Status switch
        {
            ArdaPushEnqueueStatus.Accepted => Results.Accepted(
                value: new { notificationId = result.NotificationId }),
            ArdaPushEnqueueStatus.Invalid => Results.ValidationProblem(result.Errors!),
            ArdaPushEnqueueStatus.Unauthorized => Results.Unauthorized(),
            ArdaPushEnqueueStatus.UnknownModule => Results.NotFound(),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private static bool ValidModule(string? moduleId, ApplicationRegistry registry) =>
        string.Equals(moduleId, "portal", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(moduleId)
            && registry.All.Any(application => string.Equals(
                application.Id, moduleId, StringComparison.OrdinalIgnoreCase)));

    internal static bool IsModuleClientRequest(HttpRequest request) =>
        string.Equals(request.Headers[ModuleClientHeader], ModuleClientHeaderValue,
            StringComparison.Ordinal);

    private static async Task<T?> ReadRequestAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

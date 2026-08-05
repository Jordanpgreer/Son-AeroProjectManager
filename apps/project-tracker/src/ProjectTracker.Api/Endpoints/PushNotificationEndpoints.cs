using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Configuration;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Endpoints;

public static class PushNotificationEndpoints
{
    public static RouteGroupBuilder MapPushNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/push/public-key", GetPublicKey);
        api.MapPost("/push/subscriptions", UpsertAsync);
        api.MapDelete("/push/subscriptions", DeleteAsync);

        return api;
    }

    public static IResult GetPublicKey(IOptions<WebPushOptions> options)
    {
        var configured = options.Value.IsConfigured;
        return Results.Ok(new PushPublicKeyDto(configured ? options.Value.PublicKey : string.Empty, configured));
    }

    public static async Task<IResult> UpsertAsync(
        PushSubscriptionUpsertDto request,
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        PushSubscriptionService subscriptions,
        CancellationToken cancellationToken)
    {
        if (currentUser.IsAccessPreview) return Results.Forbid();
        var userId = await CurrentUserIdAsync(db, currentUser.AccountName, cancellationToken);
        if (userId is null) return Results.Forbid();

        var result = await subscriptions.UpsertAsync(userId.Value, request, cancellationToken);
        return result.Status switch
        {
            PushSubscriptionUpsertStatus.Saved => Results.NoContent(),
            PushSubscriptionUpsertStatus.Invalid => Results.ValidationProblem(result.Errors!),
            PushSubscriptionUpsertStatus.EndpointOwnedByAnotherUser => Results.Conflict(
                "This browser subscription is registered to another user. Sign in as that user and disable notifications before trying again."),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    public static async Task<IResult> DeleteAsync(
        [FromBody] PushSubscriptionDeleteDto request,
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        PushSubscriptionService subscriptions,
        CancellationToken cancellationToken)
    {
        if (currentUser.IsAccessPreview) return Results.Forbid();
        var userId = await CurrentUserIdAsync(db, currentUser.AccountName, cancellationToken);
        if (userId is null) return Results.Forbid();
        if (!PushSubscriptionValidation.IsValidEndpoint(request.Endpoint))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["endpoint"] = ["A valid HTTPS push-service endpoint is required."]
            });
        }
        await subscriptions.DeleteAsync(userId.Value, request.Endpoint, cancellationToken);
        return Results.NoContent();
    }

    private static Task<int?> CurrentUserIdAsync(
        ProjectTrackerDbContext db,
        string accountName,
        CancellationToken cancellationToken)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        return db.Users
            .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

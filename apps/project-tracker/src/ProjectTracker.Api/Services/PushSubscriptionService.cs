using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public enum PushSubscriptionUpsertStatus
{
    Saved,
    Invalid,
    EndpointOwnedByAnotherUser
}

public sealed record PushSubscriptionUpsertResult(
    PushSubscriptionUpsertStatus Status,
    Dictionary<string, string[]>? Errors = null);

public sealed class PushSubscriptionService(ProjectTrackerDbContext db)
{
    public async Task<PushSubscriptionUpsertResult> UpsertAsync(
        int userId,
        PushSubscriptionUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        var validation = PushSubscriptionValidation.Validate(request);
        if (validation.Count > 0)
        {
            return new PushSubscriptionUpsertResult(PushSubscriptionUpsertStatus.Invalid, validation);
        }

        var endpoint = request.Endpoint!.Trim();
        var existing = await db.PushSubscriptions
            .SingleOrDefaultAsync(subscription => subscription.Endpoint == endpoint, cancellationToken);
        if (existing is not null && existing.AppUserId != userId)
        {
            return new PushSubscriptionUpsertResult(PushSubscriptionUpsertStatus.EndpointOwnedByAnotherUser);
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? expiration = request.ExpirationTime is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.ExpirationTime.Value)
            : null;
        if (existing is null)
        {
            existing = new PushSubscriptionRecord
            {
                AppUserId = userId,
                Endpoint = endpoint,
                CreatedAt = now
            };
            db.PushSubscriptions.Add(existing);
        }

        existing.P256dh = request.Keys!.P256dh!.Trim();
        existing.Auth = request.Keys.Auth!.Trim();
        existing.ExpirationTime = expiration;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new PushSubscriptionUpsertResult(PushSubscriptionUpsertStatus.Saved);
    }

    public async Task DeleteAsync(int userId, string? endpoint, CancellationToken cancellationToken = default)
    {
        if (!PushSubscriptionValidation.IsValidEndpoint(endpoint)) return;

        await db.PushSubscriptions
            .Where(subscription => subscription.AppUserId == userId && subscription.Endpoint == endpoint!.Trim())
            .ExecuteDeleteAsync(cancellationToken);
    }
}

public static class PushSubscriptionValidation
{
    public const int MaximumEndpointLength = 2048;
    public const int MaximumP256dhLength = 256;
    public const int MaximumAuthLength = 128;

    public static Dictionary<string, string[]> Validate(PushSubscriptionUpsertDto request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!IsValidEndpoint(request.Endpoint))
        {
            errors["endpoint"] = ["A valid HTTPS push-service endpoint is required."];
        }
        if (!IsValidBase64Url(request.Keys?.P256dh, MaximumP256dhLength, expectedBytes: 65, requireUncompressedPoint: true))
        {
            errors["keys.p256dh"] = ["The browser push encryption key is invalid."];
        }
        if (!IsValidBase64Url(request.Keys?.Auth, MaximumAuthLength, expectedBytes: 16, requireUncompressedPoint: false))
        {
            errors["keys.auth"] = ["The browser push authentication secret is invalid."];
        }
        if (request.ExpirationTime is < 0)
        {
            errors["expirationTime"] = ["The subscription expiration time is invalid."];
        }
        else if (request.ExpirationTime is > 0)
        {
            try
            {
                _ = DateTimeOffset.FromUnixTimeMilliseconds(request.ExpirationTime.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                errors["expirationTime"] = ["The subscription expiration time is invalid."];
            }
        }
        return errors;
    }

    public static bool IsValidEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > MaximumEndpointLength) return false;
        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsValidBase64Url(
        string? value,
        int maximumLength,
        int expectedBytes,
        bool requireUncompressedPoint)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;
        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == expectedBytes && (!requireUncompressedPoint || bytes[0] == 4);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

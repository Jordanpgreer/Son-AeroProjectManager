using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using Portal.Api.Dtos;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public enum ArdaPushSubscriptionUpsertStatus
{
    Saved,
    Invalid,
    UnknownUser,
    EndpointOwnedByAnotherUser
}

public sealed record ArdaPushSubscriptionUpsertResult(
    ArdaPushSubscriptionUpsertStatus Status,
    Dictionary<string, string[]>? Errors = null);

public sealed class ArdaPushSubscriptionService(PortalRoleDbContext db)
{
    public async Task<ArdaPushSubscriptionUpsertResult> UpsertAsync(
        string accountName,
        ArdaPushSubscriptionUpsertDto request,
        CancellationToken cancellationToken = default)
    {
        var validation = ArdaPushSubscriptionValidation.Validate(request);
        if (validation.Count > 0)
            return new(ArdaPushSubscriptionUpsertStatus.Invalid, validation);

        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        var userId = await db.Users
            .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId is null) return new(ArdaPushSubscriptionUpsertStatus.UnknownUser);

        var endpoint = request.Endpoint!.Trim();
        var existing = await db.ArdaPushSubscriptions
            .SingleOrDefaultAsync(subscription => subscription.Endpoint == endpoint, cancellationToken);
        if (existing is not null && existing.AppUserId != userId.Value)
            return new(ArdaPushSubscriptionUpsertStatus.EndpointOwnedByAnotherUser);

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? expiration = request.ExpirationTime is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.ExpirationTime.Value)
            : null;
        if (existing is null)
        {
            existing = new ArdaPushSubscriptionRecord
            {
                AppUserId = userId.Value,
                Endpoint = endpoint,
                CreatedAt = now
            };
            db.ArdaPushSubscriptions.Add(existing);
        }

        existing.P256dh = request.Keys!.P256dh!.Trim();
        existing.Auth = request.Keys.Auth!.Trim();
        existing.ExpirationTime = expiration;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new(ArdaPushSubscriptionUpsertStatus.Saved);
    }

    public async Task<int> RegisteredDeviceCountAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        return await db.ArdaPushSubscriptions.CountAsync(subscription =>
            subscription.User.IsActive
            && lookupKeys.Contains(subscription.User.AccountName.ToUpper()), cancellationToken);
    }
}

public static class ArdaPushSubscriptionValidation
{
    public const int MaximumEndpointLength = 2048;
    private static readonly string[] ApprovedPushServiceHosts =
    [
        "fcm.googleapis.com",
        "push.services.mozilla.com",
        "updates.push.services.mozilla.com",
        "notify.windows.com"
    ];

    public static Dictionary<string, string[]> Validate(ArdaPushSubscriptionUpsertDto request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (!IsValidEndpoint(request.Endpoint))
            errors["endpoint"] = ["A valid HTTPS push-service endpoint is required."];
        if (!IsValidBase64Url(request.Keys?.P256dh, 256, 65, true))
            errors["keys.p256dh"] = ["The browser push encryption key is invalid."];
        if (!IsValidBase64Url(request.Keys?.Auth, 128, 16, false))
            errors["keys.auth"] = ["The browser push authentication secret is invalid."];
        if (request.ExpirationTime is < 0)
            errors["expirationTime"] = ["The subscription expiration time is invalid."];
        else if (request.ExpirationTime is > 0)
        {
            try { _ = DateTimeOffset.FromUnixTimeMilliseconds(request.ExpirationTime.Value); }
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
            && uri.Port == 443
            && string.IsNullOrEmpty(uri.UserInfo)
            && ApprovedPushServiceHosts.Any(approved =>
                string.Equals(uri.DnsSafeHost, approved, StringComparison.OrdinalIgnoreCase)
                || uri.DnsSafeHost.EndsWith('.' + approved, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidBase64Url(string? value, int maximumLength, int bytesExpected, bool uncompressed)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;
        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == bytesExpected && (!uncompressed || bytes[0] == 4);
        }
        catch (FormatException) { return false; }
    }
}

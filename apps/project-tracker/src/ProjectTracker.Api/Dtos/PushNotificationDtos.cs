namespace ProjectTracker.Api.Dtos;

public sealed record PushPublicKeyDto(string PublicKey, bool Enabled);

public sealed record PushSubscriptionKeysDto(string? P256dh, string? Auth);

public sealed record PushSubscriptionUpsertDto(
    string? Endpoint,
    long? ExpirationTime,
    PushSubscriptionKeysDto? Keys);

public sealed record PushSubscriptionDeleteDto(string? Endpoint);

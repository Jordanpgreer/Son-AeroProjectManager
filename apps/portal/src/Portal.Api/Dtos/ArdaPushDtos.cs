namespace Portal.Api.Dtos;

public sealed record ArdaPushPublicKeyDto(string PublicKey, bool Enabled);

public sealed record ArdaPushSubscriptionKeysDto(string? P256dh, string? Auth);

public sealed record ArdaPushSubscriptionUpsertDto(
    string? Endpoint,
    long? ExpirationTime,
    ArdaPushSubscriptionKeysDto? Keys);

public sealed record ArdaPushStatusDto(
    bool Enabled,
    bool SubscriptionRegistered,
    int RegisteredDeviceCount,
    string PermissionMode);

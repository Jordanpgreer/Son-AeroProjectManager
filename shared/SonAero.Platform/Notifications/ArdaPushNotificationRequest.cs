namespace SonAero.Platform.Notifications;

/// <summary>
/// Stable producer contract for a module notification delivered by the Portal Web Push broker.
/// The Portal resolves <see cref="TargetPath"/> against the configured URL for the authenticated
/// source module so producers cannot supply an arbitrary notification-click destination.
/// </summary>
public sealed record ArdaPushNotificationRequest(
    string? SourceNotificationKey,
    string? RecipientAccountName,
    string? Title,
    string? Body,
    string? TargetPath);

public sealed record ArdaPushPresenceRequest(
    string? ModuleId,
    string? ClientId,
    bool Visible);

public sealed record ArdaPushForegroundRequest(
    string? ModuleId,
    string? ClientId);

public sealed record ArdaPushForegroundNotification(
    long Id,
    string SourceModule,
    string Title,
    string Body,
    string TargetUrl,
    DateTimeOffset CreatedAt);

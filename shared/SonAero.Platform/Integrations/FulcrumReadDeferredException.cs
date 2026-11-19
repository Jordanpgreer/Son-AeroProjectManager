using System.Net;

namespace SonAero.Platform.Integrations;

public enum FulcrumReadDeferralReason
{
    Cooldown,
    CredentialRejected,
    PermissionRejected
}

// Describes a local admission decision, not a new request or failure at Fulcrum.
public sealed class FulcrumReadDeferredException(FulcrumReadDeferralReason reason, DateTimeOffset retryAt)
    : HttpRequestException("A previous Fulcrum failure is temporarily blocking this read. Retry after the indicated time.",
        null, reason == FulcrumReadDeferralReason.Cooldown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden)
{
    public FulcrumReadDeferralReason Reason { get; } = reason;
    public DateTimeOffset RetryAt { get; } = retryAt;
}

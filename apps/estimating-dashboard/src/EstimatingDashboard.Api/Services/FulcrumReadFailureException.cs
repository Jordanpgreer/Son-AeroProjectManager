using System.Net;
using System.Text.RegularExpressions;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Api.Services;

internal enum FulcrumReadFailureKind { UpstreamResponse, Network, Cooldown, CredentialRejected, PermissionRejected }

// Carries only an allowlisted operation and status, never request IDs, query values, bodies,
// credentials, or an underlying exception. Callers can safely report these fields to the user.
internal sealed class FulcrumReadFailureException : HttpRequestException
{
    private FulcrumReadFailureException(Operation operation, FulcrumReadFailureKind kind,
        HttpStatusCode? status, DateTimeOffset? retryAt = null)
        : base("The Fulcrum read could not complete.", null, status)
    {
        OperationLabel = operation.Label;
        EndpointTemplate = operation.Template;
        Kind = kind;
        RetryAt = retryAt;
    }

    public string OperationLabel { get; }
    public string EndpointTemplate { get; }
    public FulcrumReadFailureKind Kind { get; }
    public DateTimeOffset? RetryAt { get; }
    public bool IsDeferred => Kind is FulcrumReadFailureKind.Cooldown
        or FulcrumReadFailureKind.CredentialRejected or FulcrumReadFailureKind.PermissionRejected;
    public int? UpstreamStatusCode => Kind == FulcrumReadFailureKind.UpstreamResponse && StatusCode is { } status
        ? (int)status : null;

    public static FulcrumReadFailureException FromResponse(HttpMethod method, string path, HttpStatusCode status) =>
        new(Describe(method, path), FulcrumReadFailureKind.UpstreamResponse, status);

    public static FulcrumReadFailureException FromRequestFailure(HttpMethod method, string path, HttpRequestException error) =>
        new(Describe(method, path), error.StatusCode is null ? FulcrumReadFailureKind.Network : FulcrumReadFailureKind.UpstreamResponse,
            error.StatusCode);

    public static FulcrumReadFailureException FromDeferred(HttpMethod method, string path, FulcrumReadDeferredException error) =>
        new(Describe(method, path), error.Reason switch
        {
            FulcrumReadDeferralReason.CredentialRejected => FulcrumReadFailureKind.CredentialRejected,
            FulcrumReadDeferralReason.PermissionRejected => FulcrumReadFailureKind.PermissionRejected,
            _ => FulcrumReadFailureKind.Cooldown
        }, error.StatusCode, error.RetryAt);

    private static Operation Describe(HttpMethod method, string path)
    {
        var cleanPath = path.Split('?', '#')[0].TrimStart('/');
        return Operations.FirstOrDefault(operation => method.Method == operation.Method
            && Regex.IsMatch(cleanPath, operation.Pattern, RegexOptions.CultureInvariant)) ?? Unknown;
    }

    private sealed record Operation(string Method, string Pattern, string Label, string Template);
    private static readonly Operation Unknown = new("", "", "Fulcrum data", "Fulcrum API read");
    private static readonly Operation[] Operations =
    [
        new("GET", @"^api/quotes/[^/]+$", "quote details", "GET /api/quotes/{quoteId}"),
        new("POST", @"^api/quotes/[^/]+/part-line-items/list$", "quote lines", "POST /api/quotes/{quoteId}/part-line-items/list"),
        new("GET", @"^api/quotes/[^/]+/part-line-items/[^/]+/make-summary$", "quote planning", "GET /api/quotes/{quoteId}/part-line-items/{lineId}/make-summary"),
        new("POST", @"^api/quotes/[^/]+/part-line-items/[^/]+/routing/[^/]+/operations/list$", "quote routing operations", "POST /api/quotes/{quoteId}/part-line-items/{lineId}/routing/{routingId}/operations/list"),
        new("POST", @"^api/quotes/[^/]+/part-line-items/[^/]+/routing/[^/]+/input-items/list$", "quote routing inputs", "POST /api/quotes/{quoteId}/part-line-items/{lineId}/routing/{routingId}/input-items/list"),
        new("GET", @"^api/items/[^/]+$", "item details", "GET /api/items/{itemId}"),
        new("GET", @"^api/items/[^/]+/make-summary$", "item planning", "GET /api/items/{itemId}/make-summary"),
        new("GET", @"^api/items/[^/]+/purchase-history$", "item purchase history", "GET /api/items/{itemId}/purchase-history"),
        new("POST", @"^api/items/[^/]+/routing/operations/list$", "item routing operations", "POST /api/items/{itemId}/routing/operations/list"),
        new("POST", @"^api/items/[^/]+/routing/input-items/list$", "item routing inputs", "POST /api/items/{itemId}/routing/input-items/list"),
        new("POST", @"^api/items/[^/]+/routing/input-materials/list$", "item routing materials", "POST /api/items/{itemId}/routing/input-materials/list"),
        new("POST", @"^api/items/[^/]+/vendors/list$", "item vendors", "POST /api/items/{itemId}/vendors/list"),
        new("GET", @"^api/inventory/availableByItem$", "available inventory", "GET /api/inventory/availableByItem"),
        new("GET", @"^api/vendors/[^/]+$", "vendor details", "GET /api/vendors/{vendorId}"),
        new("POST", @"^api/vendors/[^/]+/contacts/list$", "vendor contacts", "POST /api/vendors/{vendorId}/contacts/list")
    ];
}

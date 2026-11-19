using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

internal static class PurchasingFailureResponse
{
    internal sealed record DiagnosticError(string Code, string Message, string Operation, string Endpoint,
        string FailureKind, int? UpstreamStatusCode, DateTimeOffset? RetryAt, string TraceIdentifier);

    public static IResult Create(HttpContext context, FulcrumReadFailureException failure)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        var operation = failure.OperationLabel;
        var status = failure.UpstreamStatusCode;
        var (code, message, responseStatus) = failure.Kind switch
        {
            FulcrumReadFailureKind.Cooldown => ("FulcrumReadPaused",
                $"Arda is waiting before retrying {operation}. No Fulcrum request was sent for this attempt. Try again after the pause.", 503),
            FulcrumReadFailureKind.CredentialRejected => ("FulcrumCredentialCheckPaused",
                $"Arda paused {operation} after an earlier Fulcrum authentication rejection. Verify the API credential and retry after the pause.", 503),
            FulcrumReadFailureKind.PermissionRejected => ("FulcrumPermissionCheckPaused",
                $"Arda paused {operation} after an earlier Fulcrum permission rejection. Verify access to this operation and retry after the pause.", 503),
            FulcrumReadFailureKind.Network => ("FulcrumConnectionFailed",
                $"The server could not complete the connection for {operation}. No Fulcrum HTTP response was received. Try again shortly.", 502),
            _ => status switch
            {
                401 => ("FulcrumAuthenticationRejected", $"Fulcrum rejected the API credential (HTTP 401) for {operation}. Verify the configured credential.", 502),
                403 => ("FulcrumPermissionDenied", $"Fulcrum denied access (HTTP 403) to {operation}. Verify the API credential's permissions for this operation.", 502),
                404 => ("FulcrumRecordNotFound", $"Fulcrum returned not found (HTTP 404) for {operation}. Verify the source record and the requested operation.", 502),
                429 => ("FulcrumRateLimited", $"Fulcrum is limiting requests (HTTP 429) for {operation}. Wait before retrying.", 503),
                503 => ("FulcrumTemporarilyUnavailable", $"Fulcrum is temporarily unavailable (HTTP 503) for {operation}. Try again shortly.", 503),
                >= 500 => ("FulcrumUpstreamFailure", $"Fulcrum could not complete {operation} (HTTP {status}). Try again shortly.", 502),
                _ => ("FulcrumRequestRejected", $"Fulcrum rejected the request for {operation} (HTTP {status}).", 502)
            }
        };
        Log(context, failure.Kind.ToString(), operation, failure.EndpointTemplate, status, failure.RetryAt);
        return Results.Json(new DiagnosticError(code, message, operation, failure.EndpointTemplate,
            failure.Kind.ToString(), status, failure.RetryAt, context.TraceIdentifier), statusCode: responseStatus);
    }

    public static IResult CoordinationUnavailable(HttpContext context, bool outsideProcessing)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        Log(context, "CoordinationUnavailable", "shared Fulcrum coordination", "not available", null, null);
        return Results.Json(new ErrorDto(outsideProcessing ? "OpDataUnavailable" : "MaterialDataUnavailable",
            outsideProcessing ? "Outside processing data is temporarily unavailable. Try again shortly."
                : "Material data is temporarily unavailable. Try again shortly."), statusCode: 503);
    }

    private static void Log(HttpContext context, string kind, string operation, string endpoint,
        int? upstreamStatus, DateTimeOffset? retryAt)
    {
        // Deliberately do not pass the exception, request URI, account, or provider response body.
        context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Arda.Fulcrum.Purchasing")
            .LogWarning(new EventId(4201, "FulcrumPurchasingReadFailed"),
                "Fulcrum purchasing read failed. Kind {FailureKind}; operation {Operation}; endpoint {Endpoint}; upstream status {UpstreamStatusCode}; retry at {RetryAt}; trace {TraceIdentifier}.",
                kind, operation, endpoint, upstreamStatus, retryAt, context.TraceIdentifier);
    }
}

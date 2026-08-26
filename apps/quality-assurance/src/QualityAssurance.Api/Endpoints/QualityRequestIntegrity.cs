namespace QualityAssurance.Api.Endpoints;

public static class QualityRequestIntegrity
{
    public const string RequestedWithHeader = "X-Requested-With";
    public const string RequestedWithValue = "XMLHttpRequest";

    public static bool IsTrustedAjaxRequest(HttpRequest request) =>
        string.Equals(
            request.Headers[RequestedWithHeader].ToString(),
            RequestedWithValue,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsTrustedMultipartAjaxRequest(HttpRequest request) =>
        request.HasFormContentType && IsTrustedAjaxRequest(request);
}

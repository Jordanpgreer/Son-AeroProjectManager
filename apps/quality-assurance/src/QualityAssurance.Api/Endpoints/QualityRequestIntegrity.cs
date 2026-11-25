using Microsoft.Net.Http.Headers;

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

    public static bool IsTrustedMultipartRequest(HttpRequest request, IEnumerable<string>? allowedOrigins = null) =>
        HasMediaType(request, "multipart/form-data") && HasTrustedOrigin(request, allowedOrigins);

    internal static bool HasMediaType(HttpRequest request, string mediaType) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
        && string.Equals(contentType.MediaType.Value, mediaType, StringComparison.OrdinalIgnoreCase);

    // A browser-controlled Origin protects writes that must work without an IIS preflight.
    internal static bool HasTrustedOrigin(HttpRequest request, IEnumerable<string>? allowedOrigins)
    {
        if (request.Headers.Origin.Count != 1) return false;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || origin.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var normalized = uri.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(origin, normalized, StringComparison.OrdinalIgnoreCase)) return false;
        var sameOrigin = $"{request.Scheme}://{request.Host}";
        return string.Equals(normalized, sameOrigin, StringComparison.OrdinalIgnoreCase)
            || (allowedOrigins ?? []).Any(allowed => string.Equals(normalized, allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}

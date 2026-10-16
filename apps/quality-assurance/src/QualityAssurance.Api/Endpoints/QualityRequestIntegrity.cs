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

    public static bool IsTrustedMultipartRequest(HttpRequest request, IEnumerable<string>? allowedOrigins = null)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase)) return false;
        // A browser-controlled Origin is the CSRF boundary for this one simple CORS
        // upload. A custom AJAX header would force unauthenticated OPTIONS through IIS.
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

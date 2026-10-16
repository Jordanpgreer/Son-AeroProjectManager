using Portal.Api.Services;

namespace Portal.Api.Endpoints;

internal static class ArdaInboxRequestIntegrity
{
    public static bool IsTrustedOrigin(HttpRequest request, ApplicationRegistry registry)
    {
        // Empty POSTs avoid CORS preflight at Windows-only IIS. The browser-controlled Origin
        // is mandatory for these mutations, including same-origin requests and legacy headers.
        if (request.Headers.Origin.Count != 1) return false;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || origin.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var normalized = uri.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(origin, normalized, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(normalized, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)) return true;
        return registry.All.Any(application => Uri.TryCreate(application.Url, UriKind.Absolute, out var allowed)
            && (allowed.Scheme == Uri.UriSchemeHttp || allowed.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(allowed.UserInfo)
            && string.Equals(normalized, allowed.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }
}

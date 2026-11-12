using Portal.Api.Services;

namespace Portal.Api.Endpoints;

internal static class ArdaInboxRequestIntegrity
{
    public static bool IsTrustedOrigin(HttpRequest request, ApplicationRegistry registry)
    {
        var normalized = ReadNormalizedOrigin(request);
        if (normalized is null) return false;
        if (IsPortalOrigin(request, normalized)) return true;
        return registry.All.Any(application => ApplicationOrigin(application.Url) is { } allowed
            && string.Equals(normalized, allowed, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTrustedModuleOrigin(
        HttpRequest request,
        ApplicationRegistry registry,
        string? moduleId)
    {
        var normalized = ReadNormalizedOrigin(request);
        if (normalized is null || string.IsNullOrWhiteSpace(moduleId)) return false;
        if (string.Equals(moduleId, "portal", StringComparison.OrdinalIgnoreCase))
            return IsPortalOrigin(request, normalized);

        return registry.All.Any(application =>
            string.Equals(application.Id, moduleId, StringComparison.OrdinalIgnoreCase)
            && ApplicationOrigin(application.Url) is { } allowed
            && string.Equals(normalized, allowed, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadNormalizedOrigin(HttpRequest request)
    {
        // CORS-safelisted POSTs avoid anonymous preflight at Windows-only IIS. The
        // browser-controlled Origin is mandatory before any authenticated mutation.
        if (request.Headers.Origin.Count != 1) return null;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || origin.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var normalized = uri.GetLeftPart(UriPartial.Authority);
        return string.Equals(origin, normalized, StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }

    private static bool IsPortalOrigin(HttpRequest request, string normalized) =>
        string.Equals(normalized, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);

    private static string? ApplicationOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var allowed)
        && (allowed.Scheme == Uri.UriSchemeHttp || allowed.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(allowed.UserInfo)
            ? allowed.GetLeftPart(UriPartial.Authority)
            : null;
}

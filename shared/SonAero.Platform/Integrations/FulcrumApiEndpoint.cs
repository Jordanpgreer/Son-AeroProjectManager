namespace SonAero.Platform.Integrations;

public static class FulcrumApiEndpoint
{
    public const string ItarBaseUrl = "https://api.fulcrumpro.us/";
    public const string AscendingSortDirection = "ascending";
    public const string DescendingSortDirection = "descending";

    private const string StandardApiHost = "api.fulcrumpro.com";
    private const string ItarApiHost = "api.fulcrumpro.us";

    public static Uri ResolveItarBaseUri(string? configuredBaseUrl, string settingName)
    {
        var value = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? ItarBaseUrl
            : configuredBaseUrl.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{settingName} must be an absolute HTTPS URL.");

        // Existing production settings are preserved during deployment, so upgrade the
        // standard Fulcrum host in memory rather than leaving an ITAR token on the .com API.
        if (string.Equals(baseUri.Host, StandardApiHost, StringComparison.OrdinalIgnoreCase))
            baseUri = new UriBuilder(baseUri) { Host = ItarApiHost }.Uri;

        return baseUri;
    }
}

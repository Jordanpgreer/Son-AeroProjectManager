namespace ProjectTracker.Api.Configuration;

using Microsoft.Extensions.Options;

public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public bool Enabled { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    public bool IsConfigured =>
        Enabled
        && IsBase64UrlKey(PublicKey, 65, requireUncompressedPoint: true)
        && IsBase64UrlKey(PrivateKey, 32, requireUncompressedPoint: false)
        && (Uri.TryCreate(Subject, UriKind.Absolute, out var subjectUri)
            && (subjectUri.Scheme is "mailto" or "https"));

    private static bool IsBase64UrlKey(string? value, int expectedLength, bool requireUncompressedPoint)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(normalized);
            return bytes.Length == expectedLength && (!requireUncompressedPoint || bytes[0] == 4);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class WebPushOptionsValidator : IValidateOptions<WebPushOptions>
{
    public ValidateOptionsResult Validate(string? name, WebPushOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        return options.IsConfigured
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "WebPush is enabled but its VAPID public key, private key, or subject is missing or invalid.");
    }
}

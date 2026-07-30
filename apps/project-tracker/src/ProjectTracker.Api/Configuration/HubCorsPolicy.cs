namespace ProjectTracker.Api.Configuration;

public static class HubCorsPolicy
{
    public const string Name = "HubAdmin";
    public const string OriginsConfigurationKey = "Cors:HubOrigins";

    public static IServiceCollection AddHubCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var origins = (configuration.GetSection(OriginsConfigurationKey).Get<string[]>() ?? [])
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(NormalizeOrigin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return services.AddCors(options =>
        {
            options.AddPolicy(Name, policy =>
            {
                if (origins.Length > 0)
                {
                    policy.WithOrigins(origins);
                }

                policy
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    private static string NormalizeOrigin(string configuredOrigin)
    {
        var origin = configuredOrigin.Trim();
        if (origin == "*")
        {
            throw new InvalidOperationException(
                $"{OriginsConfigurationKey} cannot contain '*' when credentialed requests are enabled.");
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"{OriginsConfigurationKey} contains invalid origin '{configuredOrigin}'. "
                + "Configure only an absolute HTTP(S) origin without a path, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}

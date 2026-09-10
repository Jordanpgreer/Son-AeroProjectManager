using System.IO.Compression;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Endpoints;

public static class OutlookConnectorEndpoints
{
    public static RouteGroupBuilder MapOutlookConnectorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vendor-quotes/connector", (
            HttpContext context, IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            var access = context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile;
            if (access is null || access.IsPreview) return Results.Forbid();

            // Do not derive the destination of a Windows-authenticated connector from a Host header.
            var baseUrl = configuration["OutlookConnector:BaseUrl"] ?? "https://estimating.hub.son4l.local";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address)
                || address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.UserInfo)
                || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
                return Results.Problem("The Outlook connector destination must be a configured HTTPS address.", statusCode: 503);

            var directory = Path.Combine(environment.ContentRootPath, "Assets", "OutlookConnector");
            if (!Directory.Exists(directory)) directory = Path.Combine(AppContext.BaseDirectory, "Assets", "OutlookConnector");
            if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "Start.cmd")))
                return Results.Json(new ErrorDto("ConnectorUnavailable", "The Outlook connector package is not included in this installation."), statusCode: 503);

            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("configuration.json", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".example", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetExtension(file).ToLowerInvariant() is not (".ps1" or ".psm1" or ".cmd" or ".md")) continue;
                    archive.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                }
                var entry = archive.CreateEntry("configuration.json");
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, new
                {
                    baseUrl = address.AbsoluteUri.TrimEnd('/'), mailbox = "",
                    pollIntervalSeconds = 120, initialLookbackDays = 30,
                    maxMessagesPerPoll = 100, maxRetriesPerPoll = 25, requestTimeoutSeconds = 60
                });
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(output.ToArray(), "application/zip", "Arda-Outlook-Connector.zip");
        }).RequireAuthorization(EstimatingPolicies.ViewHistory, EstimatingPolicies.Editor);
        return api;
    }
}

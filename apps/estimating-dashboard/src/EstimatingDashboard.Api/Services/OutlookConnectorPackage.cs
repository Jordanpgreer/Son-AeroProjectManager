using System.IO.Compression;
using System.Text.Json;

namespace EstimatingDashboard.Api.Services;

internal static class OutlookConnectorPackage
{
    internal const string DefaultBaseUrl = "https://estimating.hub.son4l.local";

    internal static bool TryGetDestination(string? configuredUrl, out Uri? address) =>
        Uri.TryCreate(configuredUrl ?? DefaultBaseUrl, UriKind.Absolute, out address)
        && address.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(address.UserInfo)
        && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment);

    internal static bool IsAvailable(string directory) => Directory.Exists(directory)
        && new[] { "Install.cmd", "Start.cmd", "Start-Background.cmd", "Stop.cmd", "Status.cmd", "Uninstall.cmd",
            "Start-OutlookEstimating.ps1", "Connector.Core.ps1", "Connector.Outlook.ps1",
            "Connector.Session.ps1", "Connector.Lifecycle.ps1", "Manage-Connector.ps1",
            "Test-Connector.ps1", "Test-Lifecycle.ps1", "Test-Environment.ps1", "README.md" }
            .All(name => File.Exists(Path.Combine(directory, name)));

    internal static byte[] Create(string directory, Uri address)
    {
        if (!TryGetDestination(address.AbsoluteUri, out _))
            throw new ArgumentException("The Outlook connector requires a configured HTTPS destination.", nameof(address));
        if (!IsAvailable(directory)) throw new DirectoryNotFoundException("The Outlook connector package is incomplete.");

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Order())
            {
                var name = Path.GetFileName(file);
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
        return output.ToArray();
    }
}

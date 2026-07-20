using System.Text.Json;
using Portal.Api.Dtos;

namespace Portal.Api.Services;

/// <summary>
/// Builds the Project Tracker card's "minimized dashboard" from the tracker's read-only
/// projects endpoint. Best-effort: any failure (tracker down, timeout) returns null and the
/// card falls back to a static preview.
/// </summary>
public sealed class TrackerPreviewService(
    HttpClient httpClient,
    ApplicationRegistry registry,
    ILogger<TrackerPreviewService> logger)
{
    public async Task<TrackerPreviewDto?> GetProjectTrackerAsync(CancellationToken cancellationToken)
    {
        var entry = registry.All.FirstOrDefault(application => application.Id == "project-tracker");
        if (entry is null || string.IsNullOrWhiteSpace(entry.Url) || string.IsNullOrWhiteSpace(entry.PreviewPath))
        {
            return null;
        }

        var url = entry.Url.TrimEnd('/') + entry.PreviewPath;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var json = await httpClient.GetStringAsync(url, timeout.Token);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var projects = new List<(string Name, double Progress, string Status, int Priority)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var status = element.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "" : "";
                var name = element.TryGetProperty("programName", out var nameValue) ? nameValue.GetString() ?? "" : "";
                var progress = element.TryGetProperty("progress", out var progressValue) && progressValue.TryGetDouble(out var parsedProgress)
                    ? parsedProgress
                    : 0d;
                var priority = element.TryGetProperty("priorityRank", out var priorityValue)
                    && priorityValue.ValueKind == JsonValueKind.Number
                    && priorityValue.TryGetInt32(out var parsedPriority)
                    ? parsedPriority
                    : int.MaxValue;
                projects.Add((name, progress, status, priority));
            }

            var active = projects
                .Where(project => !string.Equals(project.Status, "Complete", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var onTrack = active.Count(project => string.Equals(project.Status, "OnTrack", StringComparison.OrdinalIgnoreCase));
            var behind = active.Count(project => string.Equals(project.Status, "Behind", StringComparison.OrdinalIgnoreCase));
            var averageProgress = active.Count > 0 ? active.Average(project => project.Progress) : 0d;

            var rows = active
                .OrderBy(project => project.Priority)
                .ThenByDescending(project => project.Progress)
                .Take(5)
                .Select(project => new TrackerPreviewRow(project.Name, project.Progress, NormalizeStatus(project.Status)))
                .ToList();

            return new TrackerPreviewDto(active.Count, onTrack, behind, averageProgress, rows);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Project Tracker preview unavailable at {Url}.", url);
            return null;
        }
    }

    private static string NormalizeStatus(string status)
    {
        if (string.Equals(status, "OnTrack", StringComparison.OrdinalIgnoreCase)) return "onTrack";
        if (string.Equals(status, "Behind", StringComparison.OrdinalIgnoreCase)) return "behind";
        if (string.Equals(status, "Complete", StringComparison.OrdinalIgnoreCase)) return "complete";
        return "notStarted";
    }
}

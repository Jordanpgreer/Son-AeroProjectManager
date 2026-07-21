using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Portal.Api.Dtos;

namespace Portal.Api.Services;

/// <summary>
/// Builds the Project Tracker card's "minimized dashboard" from the tracker's read-only
/// projects endpoint. Best-effort: any failure (tracker down, timeout) returns null and the
/// card falls back to a static preview.
/// </summary>
public sealed class TrackerPreviewService(
    HttpClient httpClient,
    IMemoryCache cache,
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
            if (cache.TryGetValue(url, out TrackerPreviewDto? cached) && cached is not null)
            {
                return cached;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var snapshot = await httpClient.GetFromJsonAsync<TrackerPreviewDto>(url, timeout.Token);
            if (snapshot is null)
            {
                return null;
            }

            cache.Set(url, snapshot, TimeSpan.FromSeconds(15));
            return snapshot;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Project Tracker preview unavailable at {Url}.", url);
            return null;
        }
    }
}

using Portal.Api.Models;

namespace Portal.Api.Dtos;

public sealed record MeDto(string AccountName, string DisplayName, string Role);

public sealed record ApplicationDto(
    string Id,
    string Name,
    string Description,
    string Category,
    string Icon,
    string Url,
    int Order,
    ApplicationStatus Status,
    bool HasPreview);

public sealed record ApplicationNotificationDto(string ApplicationId, int UnreadCount);

/// <summary>Compact "minimized dashboard" snapshot rendered on an application card.</summary>
public sealed record TrackerPreviewDto(
    int ActiveProjects,
    int OnTrack,
    int Behind,
    double AverageProgress,
    IReadOnlyList<TrackerPreviewRow> Programs);

public sealed record TrackerPreviewRow(string Name, double Progress, string Status);

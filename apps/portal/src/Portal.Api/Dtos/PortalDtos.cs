using Portal.Api.Models;

namespace Portal.Api.Dtos;

public sealed record MeDto(
    string AccountName,
    string DisplayName,
    PortalAccountStatus AccountStatus,
    string? Role,
    IReadOnlyList<PortalModuleAccessDto> Modules);

public enum PortalAccountStatus
{
    Configured,
    PendingSetup,
    Inactive,
    Unavailable
}

public sealed record PortalModuleAccessDto(
    string ModuleKey,
    string Role,
    IReadOnlyList<string> Permissions);

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

public sealed record AdminAccessPreviewOverviewDto(
    IReadOnlyList<AdminAccessPreviewTargetDto> Users,
    IReadOnlyList<AdminAccessPreviewTargetDto> Groups);

public sealed record AdminAccessPreviewTargetDto(
    string Key,
    string Kind,
    string Title,
    string Subtitle,
    PortalAccountStatus AccountStatus,
    string? Role,
    IReadOnlyList<ApplicationDto> Applications);

public sealed record AdminAccessPreviewLaunchDto(
    string ActionUrl,
    string Token,
    DateTimeOffset ExpiresAt);

/// <summary>Compact "minimized dashboard" snapshot rendered on an application card.</summary>
public sealed record TrackerPreviewDto(
    int ActiveProjects,
    int OnTrack,
    int Behind,
    double AverageProgress,
    IReadOnlyList<TrackerPreviewRow> Programs);

public sealed record TrackerPreviewRow(string Name, double Progress, string Status);

public sealed record EstimatorSettingDto(
    string Estimator,
    bool IsActive,
    bool IsExplicitlyConfigured,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

public sealed record EstimatorSettingsOverviewDto(
    IReadOnlyList<EstimatorSettingDto> Estimators);

public sealed record EstimatorSettingUpdateDto(
    string Estimator,
    bool IsActive);

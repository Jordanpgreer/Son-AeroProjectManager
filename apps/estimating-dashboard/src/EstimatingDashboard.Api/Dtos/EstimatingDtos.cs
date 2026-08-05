namespace EstimatingDashboard.Api.Dtos;

public sealed record MeDto(
    string AccountName,
    string DisplayName,
    string ModuleKey,
    string Role,
    IReadOnlyList<string> Permissions,
    bool IsPreview = false,
    string? PreviewActorAccountName = null,
    string? PreviewTargetKey = null,
    string? PreviewTargetTitle = null);

public sealed record ErrorDto(string Code, string Message);

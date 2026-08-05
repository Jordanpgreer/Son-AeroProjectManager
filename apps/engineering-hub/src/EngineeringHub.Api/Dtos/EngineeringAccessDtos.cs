namespace EngineeringHub.Api.Dtos;

public sealed record EngineeringAccessUserDto(
    int Id,
    string AccountName,
    string DisplayName,
    bool IsActive,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<int> GroupIds);

public sealed record EngineeringAccessGroupDto(
    int Id,
    string Name,
    string? Description,
    bool IsSystemGroup,
    IReadOnlyList<string> Permissions,
    int UserCount);

public sealed record EngineeringPermissionDto(
    string Key,
    string Label,
    string Description,
    string Category);

public sealed record EngineeringAccessOverviewDto(
    IReadOnlyList<EngineeringAccessUserDto> Users,
    IReadOnlyList<EngineeringAccessGroupDto> Groups,
    IReadOnlyList<EngineeringPermissionDto> Permissions);

public sealed record EngineeringUserGroupsUpdateDto(IReadOnlyList<int> GroupIds);

public sealed record EngineeringGroupUpsertDto(
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions);

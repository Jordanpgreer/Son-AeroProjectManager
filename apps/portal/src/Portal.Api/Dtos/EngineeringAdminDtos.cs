namespace Portal.Api.Dtos;

public sealed record EngineeringAdminUserDto(
    int Id,
    string AccountName,
    string DisplayName,
    bool IsActive,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<int> GroupIds);

public sealed record EngineeringAdminGroupDto(
    int Id,
    string Name,
    string? Description,
    bool IsSystemGroup,
    IReadOnlyList<string> Permissions,
    int UserCount);

public sealed record EngineeringAdminPermissionDto(
    string Key,
    string Label,
    string Description,
    string Category);

public sealed record EngineeringAdminOverviewDto(
    IReadOnlyList<EngineeringAdminUserDto> Users,
    IReadOnlyList<EngineeringAdminGroupDto> Groups,
    IReadOnlyList<EngineeringAdminPermissionDto> Permissions,
    bool CanManageUsers,
    bool CanManageGroups);

public sealed record EngineeringAdminUserGroupsUpdateDto(IReadOnlyList<int> GroupIds);

public sealed record EngineeringAdminGroupUpsertDto(
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions);

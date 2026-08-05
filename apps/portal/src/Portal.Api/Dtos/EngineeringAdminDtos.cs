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

public sealed record EngineeringStorageOverviewDto(
    string RootPath,
    bool Configured,
    bool IsNetworkPath,
    bool Available,
    bool Writable,
    string Message,
    IReadOnlyList<string> DesignAuthorities,
    int PreviousRootCount,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    bool CanManageStorage);

public sealed record EngineeringStorageUpdateDto(string RootPath);

public sealed record EngineeringDesignAuthorityCreateDto(string Name);

public sealed record EngineeringAdminUserGroupsUpdateDto(IReadOnlyList<int> GroupIds);

public sealed record EngineeringAdminGroupUpsertDto(
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions);

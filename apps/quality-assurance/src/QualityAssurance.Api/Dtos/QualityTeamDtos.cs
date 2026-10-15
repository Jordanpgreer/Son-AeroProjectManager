namespace QualityAssurance.Api.Dtos;

public sealed record QualityTeamMemberDto(
    int UserId,
    string AccountName,
    string DisplayName,
    bool IsManager);

public sealed record QualityTeamDto(
    int Id,
    string Name,
    string? Description,
    bool IsActive,
    long Version,
    IReadOnlyList<QualityTeamMemberDto> Members);

public sealed record QualityTeamDirectoryUserDto(
    int Id,
    string AccountName,
    string DisplayName);

public sealed record QualityTeamAdminDto(
    IReadOnlyList<QualityTeamDto> Teams,
    IReadOnlyList<QualityTeamDirectoryUserDto> Users);

public sealed record QualityTeamCreateDto(
    string? Name,
    string? Description,
    IReadOnlyList<int>? MemberIds,
    IReadOnlyList<int>? ManagerIds);

public sealed record QualityTeamUpdateDto(
    string? Name,
    string? Description,
    IReadOnlyList<int>? MemberIds,
    IReadOnlyList<int>? ManagerIds,
    long Version,
    bool IsActive);

public sealed record QualityTeamContext(
    IReadOnlySet<int> TeamIds,
    IReadOnlySet<int> ManagedTeamIds)
{
    public static QualityTeamContext Empty { get; } = new(
        new HashSet<int>(),
        new HashSet<int>());

    public bool IsMember(int teamId) => TeamIds.Contains(teamId);
    public bool IsManager(int teamId) => ManagedTeamIds.Contains(teamId);
}

namespace Portal.Api.Dtos;

public sealed record RaidLogOverviewDto(
    IReadOnlyList<RaidLogAdminDto> Admins,
    IReadOnlyList<RaidLogGroupDto> Groups);

public sealed record RaidLogAdminDto(int Id, string AccountName, string DisplayName);

public sealed record RaidLogGroupDto(
    int Id,
    string Name,
    string? Description,
    int SortOrder,
    long Version,
    IReadOnlyList<RaidLogItemDto> Items);

public sealed record RaidLogItemDto(
    int Id,
    int GroupId,
    string Title,
    string? Description,
    string Kind,
    string Priority,
    int? AssignedToUserId,
    string? AssignedToDisplayName,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    long Version,
    IReadOnlyList<RaidLogNoteDto> Notes,
    IReadOnlyList<RaidLogActivityDto> Activity);

public sealed record RaidLogNoteDto(
    long Id,
    string Body,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string CreatedByDisplayName);

public sealed record RaidLogActivityDto(
    long Id,
    string Action,
    string Summary,
    DateTimeOffset OccurredAt,
    string Actor,
    string ActorDisplayName);

public sealed record RaidLogGroupCreateDto(string Name, string? Description);
public sealed record RaidLogGroupUpdateDto(string Name, string? Description, int SortOrder, long Version);
public sealed record RaidLogItemCreateDto(
    int GroupId,
    string Title,
    string? Description,
    string Kind,
    string Priority,
    int? AssignedToUserId);
public sealed record RaidLogItemUpdateDto(
    int GroupId,
    string Title,
    string? Description,
    string Kind,
    string Priority,
    int? AssignedToUserId,
    long Version);
public sealed record RaidLogCompletionDto(bool Completed, long Version);
public sealed record RaidLogNoteCreateDto(string Body);

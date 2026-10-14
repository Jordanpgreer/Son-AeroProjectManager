namespace Portal.Api.Dtos;

public sealed record ArdaGlobalInboxDto(
    IReadOnlyList<ArdaGlobalInboxItemDto> Items,
    int UnreadCount);

public sealed record ArdaGlobalInboxItemDto(
    string Id,
    string SourceModule,
    string Title,
    string Body,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

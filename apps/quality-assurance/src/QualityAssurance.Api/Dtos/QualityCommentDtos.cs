namespace QualityAssurance.Api.Dtos;

public sealed record QualityMentionableUserDto(
    int UserId,
    string AccountName,
    string DisplayName,
    string MentionHandle);

public sealed record QualityShipmentCommentDto(
    long Id,
    int ShipmentId,
    string Body,
    int AuthorUserId,
    string AuthorAccountName,
    string AuthorDisplayName,
    DateTimeOffset CreatedAt,
    bool IsLegacyImport);

public sealed record QualityShipmentCommentCreateDto(string? Body);

public sealed record QualityMentionNotificationDto(
    long Id,
    int ShipmentId,
    long CommentId,
    bool IsShipped,
    string ActorAccountName,
    string ActorDisplayName,
    string BodyPreview,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

namespace QualityAssurance.Api.Models;

public sealed class QualityShipmentComment
{
    public long Id { get; set; }
    public int ShipmentId { get; set; }
    public QualityShipment Shipment { get; set; } = null!;
    public string Body { get; set; } = string.Empty;
    public int AuthorUserId { get; set; }
    public string AuthorAccountName { get; set; } = string.Empty;
    public string AuthorDisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsLegacyImport { get; set; }
    public ICollection<QualityMentionNotification> MentionNotifications { get; set; } = [];
}

public sealed class QualityMentionNotification
{
    public long Id { get; set; }
    public int RecipientUserId { get; set; }
    public string RecipientAccountName { get; set; } = string.Empty;
    public int ShipmentId { get; set; }
    public long CommentId { get; set; }
    public QualityShipmentComment Comment { get; set; } = null!;
    public string ActorAccountName { get; set; } = string.Empty;
    public string ActorDisplayName { get; set; } = string.Empty;
    public string BodyPreview { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

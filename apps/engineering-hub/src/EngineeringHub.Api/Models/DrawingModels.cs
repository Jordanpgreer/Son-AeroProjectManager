namespace EngineeringHub.Api.Models;

public enum DrawingApprovalStatus { Draft, UnderReview, Approved, Obsolete }
public enum DrawingRevisionStatus { Draft, UnderReview, Approved, Superseded, Obsolete }
public enum DrawingDocumentKind { Specification, SupplementalDocument, WorkInstruction }
public enum MylarTransactionType { CheckedOut, Returned }

public sealed class Drawing
{
    public int Id { get; set; }
    public required string DrawingNumber { get; set; }
    public required string NormalizedDrawingNumber { get; set; }
    public required string Title { get; set; }
    public required string Customer { get; set; }
    public required string NormalizedCustomer { get; set; }
    public DrawingApprovalStatus ApprovalStatus { get; set; } = DrawingApprovalStatus.Draft;
    public DateTime? EffectiveDate { get; set; }
    public bool IsObsolete { get; set; }
    public string? FileLocation { get; set; }
    public string? Notes { get; set; }
    public string? PhysicalMylarLocation { get; set; }
    public bool IsMylarCheckedOut { get; set; }
    public string? MylarCheckedOutBy { get; set; }
    public DateTime? MylarCheckedOutAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public int? CurrentApprovedRevisionId { get; set; }
    public DrawingRevision? CurrentApprovedRevision { get; set; }
    public List<DrawingPart> Parts { get; set; } = [];
    public List<DrawingDocumentLink> DocumentLinks { get; set; } = [];
    public List<DrawingRevision> Revisions { get; set; } = [];
    public List<DrawingValidation> Validations { get; set; } = [];
    public List<MylarTransaction> MylarTransactions { get; set; } = [];
    public List<DrawingAuditEntry> AuditEntries { get; set; } = [];
}

public sealed class DrawingRevision
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public Drawing Drawing { get; set; } = null!;
    public required string RevisionNumber { get; set; }
    public DateTime RevisionDate { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ApprovalDate { get; set; }
    public required string ChangeDescription { get; set; }
    public DrawingRevisionStatus Status { get; set; } = DrawingRevisionStatus.Draft;
    public required string OriginalFileName { get; set; }
    public required string StoredFilePath { get; set; }
    public required string FileType { get; set; }
    public long FileSize { get; set; }
    public required string FileHash { get; set; }
    public string? SourceOriginalFileName { get; set; }
    public string? SourceStoredFilePath { get; set; }
    public required string UploadedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ApprovalComments { get; set; }
    public DateTime? SupersededOrObsoleteAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class DrawingPart
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public Drawing Drawing { get; set; } = null!;
    public required string PartNumber { get; set; }
}

public sealed class DrawingDocumentLink
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public Drawing Drawing { get; set; } = null!;
    public DrawingDocumentKind Kind { get; set; }
    public required string ReferenceNumber { get; set; }
    public string? Title { get; set; }
    public string? Location { get; set; }
}

public sealed class DrawingValidation
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public Drawing Drawing { get; set; } = null!;
    public required string ValidationType { get; set; }
    public required string Result { get; set; }
    public string? Notes { get; set; }
    public required string ValidatedBy { get; set; }
    public DateTime ValidatedAt { get; set; }
}

public sealed class MylarTransaction
{
    public int Id { get; set; }
    public int DrawingId { get; set; }
    public Drawing Drawing { get; set; } = null!;
    public MylarTransactionType Type { get; set; }
    public required string Person { get; set; }
    public string? Purpose { get; set; }
    public string? Location { get; set; }
    public required string RecordedBy { get; set; }
    public DateTime RecordedAt { get; set; }
}

public sealed class DrawingAuditEntry
{
    public long Id { get; set; }
    public int DrawingId { get; set; }
    public Drawing Drawing { get; set; } = null!;
    public string? RevisionNumber { get; set; }
    public required string Action { get; set; }
    public required string Details { get; set; }
    public required string Actor { get; set; }
    public DateTime OccurredAt { get; set; }
}

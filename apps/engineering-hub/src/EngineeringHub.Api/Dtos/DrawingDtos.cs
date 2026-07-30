namespace EngineeringHub.Api.Dtos;

public sealed record DrawingCreateDto(
    string DrawingNumber,
    string Title,
    string Customer,
    IReadOnlyList<string>? PartNumbers,
    string? Notes,
    string? PhysicalMylarLocation,
    IReadOnlyList<DrawingDocumentLinkCreateDto>? RelatedDocuments);

public sealed record DrawingDocumentLinkCreateDto(string Kind, string ReferenceNumber, string? Title, string? Location);
public sealed record DrawingUpdateDto(
    string Title,
    string Customer,
    IReadOnlyList<string>? PartNumbers,
    string? Notes,
    string? PhysicalMylarLocation,
    IReadOnlyList<DrawingDocumentLinkCreateDto>? RelatedDocuments);
public sealed record RevisionEditResultDto(int RevisionId, bool Created, bool HasPdf);
public sealed record RevisionStatusUpdateDto(string Status, string? Comments = null);
public sealed record RevisionApprovalDto(DateTime? EffectiveDate, string? Comments);
public sealed record RevisionDeleteDto(bool Confirmed);
public sealed record DrawingDeleteDto(bool Confirmed, string DrawingNumber);
public sealed record DrawingArchiveDto(string Reason);
public sealed record MylarRegisterDto(string MylarNumber, string Location, string? Note);
public sealed record MylarActionDto(string Location, string? Note);
public sealed record ValidationCreateDto(string ValidationType, string Result, string? Notes);
public sealed record DrawingReviewQueueDto(
    int RevisionId,
    int DrawingId,
    string DrawingNumber,
    string DrawingTitle,
    string Customer,
    string RevisionNumber,
    DateTime RevisionDate,
    DateTime UploadedAt,
    string UploadedBy,
    string ChangeDescription,
    string? Notes,
    bool HasPdf);

public sealed record DrawingListDto(
    int Id,
    string DrawingNumber,
    string Title,
    string Customer,
    IReadOnlyList<string> PartNumbers,
    string ApprovalStatus,
    string? CurrentRevision,
    DateTime? CurrentRevisionDate,
    DateTime? EffectiveDate,
    bool IsObsolete,
    string? PhysicalMylarLocation,
    bool IsMylarCheckedOut,
    int MylarCount,
    int CheckedOutMylarCount,
    DateTime CreatedAt,
    int RevisionCount,
    int? AttachmentRevisionId,
    string? AttachmentFileName,
    string? AttachmentStatus);

public sealed record DrawingDetailDto(
    int Id,
    string DrawingNumber,
    string Title,
    string Customer,
    IReadOnlyList<string> PartNumbers,
    string ApprovalStatus,
    string? CurrentRevision,
    DateTime? EffectiveDate,
    bool IsObsolete,
    string? FileLocation,
    string? Notes,
    string? PhysicalMylarLocation,
    bool IsMylarCheckedOut,
    string? MylarCheckedOutBy,
    DateTime? MylarCheckedOutAt,
    int MylarCount,
    int CheckedOutMylarCount,
    string CreatedBy,
    DateTime CreatedAt,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    int? CurrentApprovedRevisionId,
    IReadOnlyList<DrawingRevisionDto> Revisions,
    IReadOnlyList<DrawingDocumentLinkDto> RelatedDocuments,
    IReadOnlyList<DrawingValidationDto> Validations,
    IReadOnlyList<DrawingMylarDto> Mylars,
    IReadOnlyList<MylarTransactionDto> MylarHistory,
    IReadOnlyList<DrawingAuditDto> AuditHistory);

public sealed record DrawingRevisionDto(
    int Id, string RevisionNumber, DateTime RevisionDate, DateTime UploadedAt,
    DateTime? EffectiveDate, DateTime? ApprovalDate, string ChangeDescription, string Status,
    string OriginalFileName, string FileType, long FileSize, string FileHash, bool HasPdf, string? ControlledFilePath, bool HasSourceFile,
    string UploadedBy, string? ApprovedBy, string? ApprovalComments,
    DateTime? SupersededOrObsoleteAt, string? Notes);

public sealed record DrawingDocumentLinkDto(int Id, string Kind, string ReferenceNumber, string? Title, string? Location);
public sealed record DrawingValidationDto(int Id, string ValidationType, string Result, string? Notes, string ValidatedBy, DateTime ValidatedAt);
public sealed record DrawingMylarDto(
    int Id,
    string MylarNumber,
    bool IsCheckedOut,
    string? CurrentLocation,
    string? CheckedOutBy,
    DateTime? CheckedOutAt,
    string CreatedBy,
    DateTime CreatedAt,
    int MovementCount);
public sealed record MylarTransactionDto(
    int Id,
    int? MylarId,
    string MylarNumber,
    string Type,
    string Actor,
    string? Note,
    string? Location,
    DateTime RecordedAt);
public sealed record DrawingAuditDto(long Id, string? RevisionNumber, string Action, string Details, string Actor, DateTime OccurredAt);

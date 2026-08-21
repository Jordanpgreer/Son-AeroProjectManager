namespace EngineeringHub.Api.Models;

public enum ToolCustodyStatus { InStorage, CheckedOut, OutsideProcessing }
public enum ToolMovementType { Registered, CheckedOut, CheckedIn, SentToVendor, ReturnedFromVendor, Relocated }
public enum ToolDocumentKind { Receiving, Shipping }

public sealed class ToolRecord
{
    public int Id { get; set; }
    public required string ToolNumber { get; set; }
    public required string NormalizedToolNumber { get; set; }
    public required string Name { get; set; }
    public required string ToolType { get; set; }
    public required string Owner { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public ToolCustodyStatus CustodyStatus { get; set; } = ToolCustodyStatus.InStorage;
    public ToolHomeLocation? HomeLocationAssignment { get; set; }
    public int? CurrentLocationId { get; set; }
    public ToolLocation? CurrentLocation { get; set; }
    public string? CurrentHolder { get; set; }
    public string? CurrentVendor { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public DateTime? LastAuditDate { get; set; }
    public string? LastAuditBy { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }
    public List<ToolMovement> Movements { get; set; } = [];
    public List<ToolDocument> Documents { get; set; } = [];
    public List<ToolPartNumber> PartNumbers { get; set; } = [];
    public List<ToolAuditEntry> AuditEntries { get; set; } = [];
}

public sealed class ToolPartNumber
{
    public int ToolRecordId { get; set; }
    public ToolRecord Tool { get; set; } = null!;
    public required string PartNumber { get; set; }
    public required string NormalizedPartNumber { get; set; }
}

public sealed class ToolLocation
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string NormalizedCode { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ToolRecord> Tools { get; set; } = [];
    public List<ToolHomeLocation> HomeAssignments { get; set; } = [];
    public List<ToolMovement> Movements { get; set; } = [];
}

public sealed class ToolHomeLocation
{
    public int ToolRecordId { get; set; }
    public ToolRecord Tool { get; set; } = null!;
    public int LocationId { get; set; }
    public ToolLocation Location { get; set; } = null!;
}

public sealed class ToolMovement
{
    public long Id { get; set; }
    public int ToolRecordId { get; set; }
    public ToolRecord Tool { get; set; } = null!;
    public ToolMovementType Type { get; set; }
    public int? LocationId { get; set; }
    public ToolLocation? Location { get; set; }
    public string? LocationCode { get; set; }
    public string? Vendor { get; set; }
    public string? Person { get; set; }
    public string? Purpose { get; set; }
    public bool? InspectionConfirmed { get; set; }
    public string? InspectionNotes { get; set; }
    public required string SignedOffBy { get; set; }
    public DateTime RecordedAt { get; set; }
}

public sealed class ToolDocument
{
    public long Id { get; set; }
    public int ToolRecordId { get; set; }
    public ToolRecord Tool { get; set; } = null!;
    public ToolDocumentKind Kind { get; set; }
    public string? DocumentNumber { get; set; }
    public required string OriginalFileName { get; set; }
    public required string StoredFilePath { get; set; }
    public required string FileType { get; set; }
    public long FileSize { get; set; }
    public required string FileHash { get; set; }
    public string? Notes { get; set; }
    public DateTime DocumentDate { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}

public sealed class ToolAuditEntry
{
    public long Id { get; set; }
    public int ToolRecordId { get; set; }
    public ToolRecord Tool { get; set; } = null!;
    public required string Action { get; set; }
    public required string Details { get; set; }
    public required string Actor { get; set; }
    public DateTime OccurredAt { get; set; }
}

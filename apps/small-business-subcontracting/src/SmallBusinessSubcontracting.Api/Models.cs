namespace SmallBusinessSubcontracting.Api;

public sealed class VendorRecord
{
    public int Id { get; set; }
    public string FulcrumId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? VendorCode { get; set; }
    public bool Active { get; set; }
    public string? Website { get; set; }
    public string ContactsJson { get; set; } = "[]";
    public DateTimeOffset LastSyncedAt { get; set; }
    public DateOnly? LastCertificationDate { get; set; }
    public int Version { get; set; } = 1;
    public ICollection<VendorBusinessSizeTag> BusinessSizes { get; set; } = [];
    public ICollection<VendorDocument> Documents { get; set; } = [];
    public ICollection<VendorAuditEvent> AuditEvents { get; set; } = [];
}

public sealed class BusinessSizeTag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public ICollection<VendorBusinessSizeTag> Vendors { get; set; } = [];
}

public sealed class VendorBusinessSizeTag
{
    public int VendorId { get; set; }
    public VendorRecord Vendor { get; set; } = null!;
    public int BusinessSizeTagId { get; set; }
    public BusinessSizeTag BusinessSizeTag { get; set; } = null!;
}

public sealed class VendorDocument
{
    public Guid Id { get; set; }
    public int VendorId { get; set; }
    public VendorRecord Vendor { get; set; } = null!;
    public string OriginalFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public DateOnly DocumentDate { get; set; }
    public string? Notes { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
}

public sealed class VendorAuditEvent
{
    public long Id { get; set; }
    public int VendorId { get; set; }
    public VendorRecord Vendor { get; set; } = null!;
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed record VendorContactDto(string Id, string Name, string? Position, string? Phone, string? Email);
public sealed record BusinessSizeTagDto(int Id, string Name);
public sealed record VendorDocumentDto(Guid Id, string FileName, string DocumentType, DateOnly DocumentDate,
    string? Notes, long FileSize, string UploadedBy, DateTimeOffset UploadedAt);
public sealed record VendorAuditDto(long Id, string Kind, string Summary, string Actor, DateTimeOffset OccurredAt);
public sealed record VendorSummaryDto(int Id, string FulcrumId, string Name, string? VendorCode, bool Active,
    string? Website, IReadOnlyList<VendorContactDto> Contacts, IReadOnlyList<BusinessSizeTagDto> BusinessSizes,
    DateOnly? LastCertificationDate, int DocumentCount, DateTimeOffset LastSyncedAt, int Version);
public sealed record VendorDetailDto(VendorSummaryDto Vendor, IReadOnlyList<VendorDocumentDto> Documents,
    IReadOnlyList<VendorAuditDto> AuditHistory, IReadOnlyList<BusinessSizeTagDto> AvailableBusinessSizes);
public sealed record DashboardDto(IReadOnlyList<VendorSummaryDto> Vendors, IReadOnlyList<BusinessSizeTagDto> BusinessSizes,
    DateTimeOffset? LastFulcrumSyncAt);
public sealed record MeDto(string AccountName, string DisplayName, string Role, IReadOnlyList<string> Permissions);
public sealed record ComplianceUpdateDto(int ExpectedVersion, DateOnly? LastCertificationDate, IReadOnlyList<string>? BusinessSizes);
public sealed record FulcrumSyncResultDto(int Added, int Updated, int Unchanged, int ContactCount, DateTimeOffset CompletedAt);

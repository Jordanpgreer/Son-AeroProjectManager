using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace SmallBusinessSubcontracting.Api;

public sealed class VendorValidationException(string message) : Exception(message);
public sealed class VendorConflictException(string message) : Exception(message);

public sealed class VendorService(
    SubcontractingDbContext db,
    IFulcrumVendorClient fulcrum,
    VendorDocumentStore documentStore,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FulcrumSyncResultDto> SynchronizeAsync(
        string actor,
        CancellationToken cancellationToken)
    {
        var snapshots = await fulcrum.GetVendorsAsync(cancellationToken);
        var duplicate = snapshots.GroupBy(vendor => vendor.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Fulcrum returned duplicate vendor ID '{duplicate.Key}'.");

        var existing = await db.Vendors
            .ToDictionaryAsync(vendor => vendor.FulcrumId, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var contacts = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var snapshot in snapshots)
        {
            contacts += snapshot.Contacts.Count;
            var contactsJson = JsonSerializer.Serialize(snapshot.Contacts.Select(contact => new VendorContactDto(
                contact.Id,
                contact.Name,
                contact.Position,
                contact.Phone,
                contact.Email)), JsonOptions);
            if (!existing.TryGetValue(snapshot.Id, out var vendor))
            {
                vendor = new VendorRecord
                {
                    FulcrumId = snapshot.Id,
                    Name = snapshot.Name,
                    VendorCode = snapshot.VendorCode,
                    Active = snapshot.Active,
                    Website = snapshot.Website,
                    ContactsJson = contactsJson,
                    LastSyncedAt = now
                };
                vendor.AuditEvents.Add(Audit("Fulcrum sync", "Vendor imported from Fulcrum.", actor, now));
                db.Vendors.Add(vendor);
                added++;
                continue;
            }

            var changes = new List<string>();
            TrackChange(changes, "name", vendor.Name, snapshot.Name);
            TrackChange(changes, "vendor code", vendor.VendorCode, snapshot.VendorCode);
            if (vendor.Active != snapshot.Active) changes.Add("active status");
            TrackChange(changes, "website", vendor.Website, snapshot.Website);
            TrackChange(changes, "contacts", vendor.ContactsJson, contactsJson);
            vendor.Name = snapshot.Name;
            vendor.VendorCode = snapshot.VendorCode;
            vendor.Active = snapshot.Active;
            vendor.Website = snapshot.Website;
            vendor.ContactsJson = contactsJson;
            vendor.LastSyncedAt = now;
            if (changes.Count == 0)
            {
                unchanged++;
                continue;
            }

            vendor.Version++;
            vendor.AuditEvents.Add(Audit(
                "Fulcrum sync",
                $"Fulcrum refreshed: {string.Join(", ", changes)}.",
                actor,
                now));
            updated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FulcrumSyncResultDto(added, updated, unchanged, contacts, now);
    }

    public async Task<IReadOnlyList<VendorSummaryDto>> GetVendorsAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        var vendors = await LoadVendorsAsync(cancellationToken);
        var term = Clean(query);
        if (term is not null)
        {
            vendors = vendors.Where(vendor =>
                Contains(vendor.Name, term)
                || Contains(vendor.VendorCode, term)
                || vendor.BusinessSizes.Any(link => Contains(link.BusinessSizeTag.Name, term))
                || DeserializeContacts(vendor.ContactsJson).Any(contact =>
                    Contains(contact.Name, term) || Contains(contact.Email, term) || Contains(contact.Phone, term)))
                .ToList();
        }
        return vendors.OrderBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<DashboardDto> GetDashboardAsync(
        string? query,
        string? businessSize,
        DateOnly? certificationFrom,
        DateOnly? certificationTo,
        string? sortBy,
        string? sortDirection,
        CancellationToken cancellationToken)
    {
        if (certificationFrom is not null && certificationTo is not null && certificationFrom > certificationTo)
            throw new VendorValidationException("The certification start date cannot be after the end date.");
        var vendors = await LoadVendorsAsync(cancellationToken);
        var term = Clean(query);
        var size = Clean(businessSize);
        IEnumerable<VendorRecord> filtered = vendors;
        if (term is not null)
            filtered = filtered.Where(vendor =>
                Contains(vendor.Name, term)
                || vendor.BusinessSizes.Any(link => Contains(link.BusinessSizeTag.Name, term)));
        if (size is not null)
            filtered = filtered.Where(vendor => vendor.BusinessSizes.Any(link =>
                string.Equals(link.BusinessSizeTag.Name, size, StringComparison.OrdinalIgnoreCase)));
        if (certificationFrom is not null)
            filtered = filtered.Where(vendor => vendor.LastCertificationDate >= certificationFrom);
        if (certificationTo is not null)
            filtered = filtered.Where(vendor => vendor.LastCertificationDate <= certificationTo);

        var descending = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        filtered = (sortBy ?? "name").Trim().ToLowerInvariant() switch
        {
            "businesssize" => descending
                ? filtered.OrderByDescending(BusinessSizeSort, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderBy(BusinessSizeSort, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase),
            "certification" => descending
                ? filtered.OrderBy(vendor => vendor.LastCertificationDate is null)
                    .ThenByDescending(vendor => vendor.LastCertificationDate)
                    .ThenBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderBy(vendor => vendor.LastCertificationDate is null)
                    .ThenBy(vendor => vendor.LastCertificationDate)
                    .ThenBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? filtered.OrderByDescending(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderBy(vendor => vendor.Name, StringComparer.OrdinalIgnoreCase)
        };

        var tags = await db.BusinessSizeTags.AsNoTracking()
            .OrderBy(tag => tag.Name)
            .Select(tag => new BusinessSizeTagDto(tag.Id, tag.Name))
            .ToListAsync(cancellationToken);
        return new DashboardDto(
            filtered.Select(ToSummary).ToArray(),
            tags,
            vendors.Count == 0 ? null : vendors.Max(vendor => vendor.LastSyncedAt));
    }

    public async Task<VendorDetailDto?> GetVendorAsync(int id, CancellationToken cancellationToken)
    {
        var vendor = await db.Vendors.AsNoTracking()
            .AsSplitQuery()
            .Include(record => record.BusinessSizes).ThenInclude(link => link.BusinessSizeTag)
            .Include(record => record.Documents)
            .Include(record => record.AuditEvents)
            .SingleOrDefaultAsync(record => record.Id == id, cancellationToken);
        if (vendor is null) return null;
        var tags = await db.BusinessSizeTags.AsNoTracking()
            .OrderBy(tag => tag.Name)
            .Select(tag => new BusinessSizeTagDto(tag.Id, tag.Name))
            .ToListAsync(cancellationToken);
        return new VendorDetailDto(
            ToSummary(vendor),
            vendor.Documents.OrderByDescending(document => document.DocumentDate)
                .ThenByDescending(document => document.UploadedAt)
                .Select(ToDocument)
                .ToArray(),
            vendor.AuditEvents.OrderByDescending(audit => audit.OccurredAt)
                .Select(audit => new VendorAuditDto(audit.Id, audit.Kind, audit.Summary, audit.Actor, audit.OccurredAt))
                .ToArray(),
            tags);
    }

    public async Task<VendorDetailDto?> UpdateComplianceAsync(
        int id,
        ComplianceUpdateDto update,
        string actor,
        CancellationToken cancellationToken)
    {
        if (update.LastCertificationDate > DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime))
            throw new VendorValidationException("The certification date cannot be in the future.");
        var requestedNames = (update.BusinessSizes ?? [])
            .Select(Clean)
            .Where(name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedNames.Length > 20)
            throw new VendorValidationException("A vendor cannot have more than 20 business-size tags.");
        if (requestedNames.Any(name => name.Length > 80))
            throw new VendorValidationException("Business-size tags cannot exceed 80 characters.");

        var vendor = await db.Vendors
            .Include(record => record.BusinessSizes).ThenInclude(link => link.BusinessSizeTag)
            .SingleOrDefaultAsync(record => record.Id == id, cancellationToken);
        if (vendor is null) return null;
        if (vendor.Version != update.ExpectedVersion)
            throw new VendorConflictException("This vendor changed after you opened it. Refresh and try again.");

        var beforeDate = vendor.LastCertificationDate;
        var beforeTags = vendor.BusinessSizes.Select(link => link.BusinessSizeTag.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedNormalized = requestedNames.Select(NormalizeTag).ToHashSet(StringComparer.Ordinal);
        var knownTags = await db.BusinessSizeTags
            .Where(tag => requestedNormalized.Contains(tag.NormalizedName))
            .ToDictionaryAsync(tag => tag.NormalizedName, StringComparer.Ordinal, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var name in requestedNames)
        {
            var normalized = NormalizeTag(name);
            if (!knownTags.ContainsKey(normalized))
            {
                var tag = new BusinessSizeTag
                {
                    Name = name,
                    NormalizedName = normalized,
                    CreatedAt = now,
                    CreatedBy = actor
                };
                db.BusinessSizeTags.Add(tag);
                knownTags[normalized] = tag;
            }
        }

        vendor.BusinessSizes.Clear();
        foreach (var name in requestedNames)
            vendor.BusinessSizes.Add(new VendorBusinessSizeTag
            {
                Vendor = vendor,
                BusinessSizeTag = knownTags[NormalizeTag(name)]
            });
        vendor.LastCertificationDate = update.LastCertificationDate;
        vendor.Version++;
        var afterTags = requestedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var changes = new List<string>();
        if (beforeDate != update.LastCertificationDate) changes.Add("certification date");
        if (!beforeTags.SequenceEqual(afterTags, StringComparer.OrdinalIgnoreCase)) changes.Add("business-size tags");
        vendor.AuditEvents.Add(Audit(
            "Compliance updated",
            changes.Count == 0 ? "Compliance record saved with no value changes." : $"Updated {string.Join(" and ", changes)}.",
            actor,
            now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetVendorAsync(id, cancellationToken);
    }

    public async Task<VendorDetailDto?> AddDocumentAsync(
        int id,
        IFormFile file,
        string documentType,
        DateOnly documentDate,
        string? notes,
        string actor,
        CancellationToken cancellationToken)
    {
        documentType = Clean(documentType) ?? throw new VendorValidationException("Document type is required.");
        notes = Clean(notes);
        if (documentType.Length > 100) throw new VendorValidationException("Document type cannot exceed 100 characters.");
        if (notes?.Length > 1000) throw new VendorValidationException("Document notes cannot exceed 1,000 characters.");
        if (documentDate > DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime))
            throw new VendorValidationException("Document date cannot be in the future.");
        var vendor = await db.Vendors.SingleOrDefaultAsync(record => record.Id == id, cancellationToken);
        if (vendor is null) return null;

        var stored = await documentStore.SaveAsync(id, file, cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            vendor.Documents.Add(new VendorDocument
            {
                Id = Guid.NewGuid(),
                OriginalFileName = stored.OriginalFileName,
                RelativePath = stored.RelativePath,
                ContentType = stored.ContentType,
                FileSize = stored.FileSize,
                FileHash = stored.FileHash,
                DocumentType = documentType,
                DocumentDate = documentDate,
                Notes = notes,
                UploadedBy = actor,
                UploadedAt = now
            });
            vendor.Version++;
            vendor.AuditEvents.Add(Audit(
                "Document uploaded",
                $"Uploaded {stored.OriginalFileName} as {documentType}.",
                actor,
                now));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            documentStore.DeleteIfPresent(stored.RelativePath);
            throw;
        }
        return await GetVendorAsync(id, cancellationToken);
    }

    public Task<VendorDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken) =>
        db.VendorDocuments.AsNoTracking().SingleOrDefaultAsync(document => document.Id == id, cancellationToken);

    public FileStream OpenDocument(VendorDocument document) => documentStore.OpenRead(document.RelativePath);

    public async Task<byte[]> ExportDashboardAsync(
        string? query,
        string? businessSize,
        DateOnly? certificationFrom,
        DateOnly? certificationTo,
        string? sortBy,
        string? sortDirection,
        CancellationToken cancellationToken)
    {
        var data = await GetDashboardAsync(
            query,
            businessSize,
            certificationFrom,
            certificationTo,
            sortBy,
            sortDirection,
            cancellationToken);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Vendor Compliance");
        var headers = new[]
        {
            "Vendor Name", "Vendor Code", "Active", "Business Size", "Last Certification",
            "Primary Contact", "Phone", "Email", "Documents", "Last Fulcrum Sync"
        };
        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).Value = headers[column];
        for (var row = 0; row < data.Vendors.Count; row++)
        {
            var vendor = data.Vendors[row];
            var contact = vendor.Contacts.FirstOrDefault();
            sheet.Cell(row + 2, 1).Value = vendor.Name;
            sheet.Cell(row + 2, 2).Value = vendor.VendorCode ?? string.Empty;
            sheet.Cell(row + 2, 3).Value = vendor.Active ? "Yes" : "No";
            sheet.Cell(row + 2, 4).Value = string.Join(", ", vendor.BusinessSizes.Select(tag => tag.Name));
            if (vendor.LastCertificationDate is not null)
            {
                sheet.Cell(row + 2, 5).Value = vendor.LastCertificationDate.Value.ToDateTime(TimeOnly.MinValue);
                sheet.Cell(row + 2, 5).Style.DateFormat.Format = "mm/dd/yyyy";
            }
            sheet.Cell(row + 2, 6).Value = contact?.Name ?? string.Empty;
            sheet.Cell(row + 2, 7).Value = contact?.Phone ?? string.Empty;
            sheet.Cell(row + 2, 8).Value = contact?.Email ?? string.Empty;
            sheet.Cell(row + 2, 9).Value = vendor.DocumentCount;
            sheet.Cell(row + 2, 10).Value = vendor.LastSyncedAt.UtcDateTime;
            sheet.Cell(row + 2, 10).Style.DateFormat.Format = "mm/dd/yyyy h:mm AM/PM";
        }
        var tableRange = sheet.Range(1, 1, Math.Max(data.Vendors.Count + 1, 2), headers.Length);
        if (data.Vendors.Count == 0)
            sheet.Cell(2, 1).Value = "No matching vendors";
        tableRange.CreateTable("VendorComplianceExport");
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(8, 48);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<List<VendorRecord>> LoadVendorsAsync(CancellationToken cancellationToken) =>
        await db.Vendors.AsNoTracking()
            .AsSplitQuery()
            .Include(vendor => vendor.BusinessSizes).ThenInclude(link => link.BusinessSizeTag)
            .Include(vendor => vendor.Documents)
            .ToListAsync(cancellationToken);

    private static VendorSummaryDto ToSummary(VendorRecord vendor) => new(
        vendor.Id,
        vendor.FulcrumId,
        vendor.Name,
        vendor.VendorCode,
        vendor.Active,
        vendor.Website,
        DeserializeContacts(vendor.ContactsJson),
        vendor.BusinessSizes.Select(link => new BusinessSizeTagDto(
                link.BusinessSizeTag.Id,
                link.BusinessSizeTag.Name))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        vendor.LastCertificationDate,
        vendor.Documents.Count,
        vendor.LastSyncedAt,
        vendor.Version);

    private static VendorDocumentDto ToDocument(VendorDocument document) => new(
        document.Id,
        document.OriginalFileName,
        document.DocumentType,
        document.DocumentDate,
        document.Notes,
        document.FileSize,
        document.UploadedBy,
        document.UploadedAt);

    private static IReadOnlyList<VendorContactDto> DeserializeContacts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<VendorContactDto[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static VendorAuditEvent Audit(string kind, string summary, string actor, DateTimeOffset now) => new()
    {
        Kind = kind,
        Summary = summary,
        Actor = actor,
        OccurredAt = now
    };

    private static string BusinessSizeSort(VendorRecord vendor) =>
        vendor.BusinessSizes.Select(link => link.BusinessSizeTag.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "~";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeTag(string value) => value.Trim().ToUpperInvariant();
    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    private static void TrackChange(List<string> changes, string field, string? oldValue, string? newValue)
    {
        if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) changes.Add(field);
    }
}

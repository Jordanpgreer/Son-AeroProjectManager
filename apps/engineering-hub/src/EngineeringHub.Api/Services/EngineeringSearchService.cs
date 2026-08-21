using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringSearchService(EngineeringDbContext db)
{
    private static readonly IReadOnlyList<EngineeringSearchResultDto> Records =
    [
        new(
            "part-100014",
            "parts",
            "Parts",
            "Actuator Housing Assembly",
            "PN-100014",
            "Primary assembly record for the actuator housing family.",
            "Northwind Aerostructures",
            "SPEC-4418",
            "WO-24771",
            null,
            ["Part number", "Customer", "Specification", "Work order"],
            "Includes cross-reference to tooling set T-204 and release drawing DRW-100014-A."),
        new(
            "part-100287",
            "parts",
            "Parts",
            "High-Temp Seal Carrier",
            "PN-100287",
            "Engineering record for a compound-sensitive seal carrier.",
            "Helios Defense Systems",
            "SPEC-5220",
            "WO-25102",
            null,
            ["Part number", "Compound", "Work order"],
            "Keyword coverage includes seal, carrier, and nitrile note text for lookup."),
        new(
            "drawing-100014-a",
            "drawings",
            "Drawings",
            "Actuator Housing Machining Drawing",
            "DRW-100014-A",
            "Released machining drawing with current revision package.",
            "Northwind Aerostructures",
            "SPEC-4418",
            "WO-24771",
            null,
            ["Drawing number", "Revision", "Customer"],
            "Current revision references detail sheet 3 and inspection note 14."),
        new(
            "drawing-100287-b",
            "drawings",
            "Drawings",
            "Seal Carrier Detail Drawing",
            "DRW-100287-B",
            "Detail drawing tied to the high-temp seal carrier program.",
            "Helios Defense Systems",
            "SPEC-5220",
            "WO-25102",
            null,
            ["Drawing number", "Specification"],
            "Contains compound callout CMP-77 and note text for finish verification."),
        new(
            "tool-204",
            "tools",
            "Tools",
            "Housing Fixture Set",
            "TL-204",
            "Fixture set used for actuator housing milling and inspection.",
            "Northwind Aerostructures",
            "SPEC-4418",
            "WO-24771",
            null,
            ["Tool number", "Fixture", "Work order"],
            "Associated with cavity insert note and setup keyword fixturing."),
        new(
            "tool-319",
            "tools",
            "Tools",
            "Seal Carrier Trim Tool",
            "TL-319",
            "Trim tool for carrier edge cleanup after cure.",
            "Helios Defense Systems",
            "SPEC-5220",
            "WO-25102",
            null,
            ["Tool number", "Trim", "Compound"],
            "Notes include trim path adjustment and customer hold-point text."),
        new(
            "compound-cmp-77",
            "compounds",
            "Compounds",
            "Nitrile Blend 77",
            "CMP-77",
            "Approved nitrile compound used in seal carrier builds.",
            "Helios Defense Systems",
            "SPEC-5220",
            "WO-25102",
            null,
            ["Compound number", "Compound name", "Specification"],
            "Search supports number, compound name, certification keyword, and cure note text."),
        new(
            "compound-cmp-88",
            "compounds",
            "Compounds",
            "Fluorosilicone Blend 88",
            "CMP-88",
            "Compound record for elevated-temperature gasket programs.",
            "Skyreach Propulsion",
            "SPEC-6104",
            "WO-25498",
            null,
            ["Compound number", "Material", "Customer"],
            "Keywords include fluorosilicone, gasket, batch retention, and lab note text."),
        new(
            "report-rpt-9021",
            "test-reports",
            "Test reports",
            "Compression Set Validation",
            "RPT-9021",
            "Test report validating compression set performance for CMP-77.",
            "Helios Defense Systems",
            "SPEC-5220",
            "WO-25102",
            "RPT-9021",
            ["Report number", "Test report", "Compound"],
            "Includes oven cycle keyword, lab note, and outlier discussion for result review."),
        new(
            "report-rpt-9174",
            "test-reports",
            "Test reports",
            "Thermal Exposure Summary",
            "RPT-9174",
            "Thermal exposure results for fluorosilicone material screening.",
            "Skyreach Propulsion",
            "SPEC-6104",
            "WO-25498",
            "RPT-9174",
            ["Report number", "Thermal", "Qualification"],
            "Searchable by report number, work order, and note text mentioning post-bake observations."),
        new(
            "spec-4418",
            "specifications",
            "Specifications",
            "Housing Assembly Process Specification",
            "SPEC-4418",
            "Process and dimensional requirements for actuator housing work.",
            "Northwind Aerostructures",
            "SPEC-4418",
            "WO-24771",
            null,
            ["Specification number", "Process", "Customer"],
            "Contains note text on surface finish, tool verification, and inspection acceptance."),
        new(
            "spec-6104",
            "specifications",
            "Specifications",
            "Elastomer Qualification Specification",
            "SPEC-6104",
            "Qualification baseline for high-temperature elastomer compounds.",
            "Skyreach Propulsion",
            "SPEC-6104",
            "WO-25498",
            null,
            ["Specification number", "Qualification", "Compound"],
            "Keyword coverage includes bake cycle, elongation, and traceability notes."),
        new(
            "doc-pack-100014",
            "documents",
            "Documents",
            "Actuator Housing Release Packet",
            "DOC-100014-RP",
            "Release packet containing drawing, inspection, and customer routing documents.",
            "Northwind Aerostructures",
            "SPEC-4418",
            "WO-24771",
            null,
            ["Document", "Release packet", "Drawing"],
            "Linked packet supports searches by part number, customer name, and routing note keywords."),
        new(
            "doc-pack-77-cert",
            "documents",
            "Documents",
            "CMP-77 Certification Packet",
            "DOC-CMP77-CERT",
            "Certification and traceability package for nitrile compound lots.",
            "Helios Defense Systems",
            "SPEC-5220",
            "WO-25102",
            "RPT-9021",
            ["Document", "Certification", "Compound"],
            "Search supports compound number, report number, batch note text, and customer reference.")
    ];

    private static readonly (string Id, string Title)[] CategoryDefinitions =
    [
        ("parts", "Parts"),
        ("drawings", "Drawings"),
        ("tools", "Tools"),
        ("compounds", "Compounds"),
        ("test-reports", "Test reports"),
        ("specifications", "Specifications"),
        ("documents", "Documents")
    ];

    public async Task<EngineeringDashboardDto> GetDashboardAsync(
        string? query,
        string? category,
        string? customer,
        string? status,
        bool reviewQueue,
        bool canViewPending,
        bool canViewSpecifications,
        bool canViewSupportingDocuments,
        bool canViewMylar,
        bool canViewTooling,
        bool canViewCompoundData,
        CancellationToken cancellationToken)
    {
        var drawings = await db.Drawings.AsNoTracking()
            .Include(x => x.Parts)
            .Include(x => x.DocumentLinks)
            .Include(x => x.Revisions)
            .Include(x => x.Mylars)
            .AsSplitQuery()
            .OrderBy(x => x.DrawingNumber)
            .ToListAsync(cancellationToken);
        if (!canViewPending)
            drawings = drawings.Where(drawing => drawing.CurrentApprovedRevisionId.HasValue || drawing.IsObsolete).ToList();

        var liveRecords = drawings.SelectMany(drawing => ToSearchRecords(
            drawing,
            canViewPending,
            canViewSpecifications,
            canViewMylar)).ToList();
        var tools = canViewTooling
            ? await db.Tools.AsNoTracking()
                .Include(tool => tool.CurrentLocation)
                .Include(tool => tool.HomeLocationAssignment).ThenInclude(assignment => assignment!.Location)
                .Include(tool => tool.Documents)
                .Include(tool => tool.PartNumbers)
                .OrderBy(tool => tool.ToolNumber)
                .ToListAsync(cancellationToken)
            : [];
        var toolRecords = tools.Select(ToToolSearchRecord);
        var catalogRecords = Records
            .Where(x => x.Category != "drawings")
            .Where(x => x.Category != "tools")
            .Where(record => canViewSpecifications || record.Category != "specifications")
            .Where(record => canViewSupportingDocuments || record.Category != "documents")
            .Where(record => canViewTooling || record.Category != "tools")
            .Where(record => canViewCompoundData || record.Category is not ("compounds" or "test-reports"))
            .Select(record => canViewSpecifications ? record : record with { SpecificationNumber = null })
            .Select(record => record with { DrawingId = ResolveDrawingLink(record, drawings) });
        var records = catalogRecords.Concat(liveRecords).Concat(toolRecords).ToList();
        var normalized = query?.Trim();
        var filtered = records.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(normalized))
            filtered = filtered.Where(record => Matches(record, normalized));
        if (!string.IsNullOrWhiteSpace(category))
            filtered = filtered.Where(record => string.Equals(record.Category, category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(customer))
            filtered = filtered.Where(record => string.Equals(record.Customer, customer, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(status))
            filtered = filtered.Where(record => record.Tags.Any(tag => string.Equals(tag, status, StringComparison.OrdinalIgnoreCase)));
        if (reviewQueue)
            filtered = filtered.Where(record =>
                record.Category == "drawings" &&
                record.AttentionReasons is { Count: > 0 });

        var results = filtered.ToList();
        var categories = CategoryDefinitions
            .Where(definition => canViewSpecifications || definition.Id != "specifications")
            .Where(definition => canViewSupportingDocuments || definition.Id != "documents")
            .Where(definition => canViewTooling || definition.Id != "tools")
            .Where(definition => canViewCompoundData || definition.Id is not ("compounds" or "test-reports"))
            .Select(definition => new EngineeringSearchCategoryDto(
                definition.Id,
                definition.Title,
                records.Count(record => record.Category == definition.Id)))
            .ToList();

        var reviewQueueCount = canViewPending
            ? drawings.Count(drawing => BuildAttentionReasons(drawing).Count > 0)
            : 0;

        return new EngineeringDashboardDto(
            canViewSpecifications
                ? "Search by part number, tool number, drawing number, compound number or name, design authority, specification number, report number, or keyword / note text."
                : "Search by part number, drawing number, design authority, or keyword / note text.",
            categories,
            results,
            new EngineeringOperationalSummaryDto(
                drawings.Count,
                canViewPending ? drawings.Count(x => x.ApprovalStatus == DrawingApprovalStatus.Draft) : 0,
                reviewQueueCount,
                drawings.Count(x => x.ApprovalStatus == DrawingApprovalStatus.Approved),
                canViewMylar ? drawings.Sum(x => x.Mylars.Count(mylar => mylar.IsCheckedOut)) : 0),
            records.Where(x => !string.IsNullOrWhiteSpace(x.Customer))
                .Select(x => x.Customer!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList());
    }

    public Task<EngineeringDashboardDto> GetDashboardAsync(
        string? query,
        string? category,
        string? customer,
        string? status,
        bool reviewQueue,
        CancellationToken cancellationToken) =>
        GetDashboardAsync(
            query,
            category,
            customer,
            status,
            reviewQueue,
            canViewPending: true,
            canViewSpecifications: true,
            canViewSupportingDocuments: true,
            canViewMylar: true,
            canViewTooling: true,
            canViewCompoundData: true,
            cancellationToken);

    private static IEnumerable<EngineeringSearchResultDto> ToSearchRecords(
        Drawing drawing,
        bool canViewPending,
        bool canViewSpecifications,
        bool canViewMylar)
    {
        var specification = canViewSpecifications
            ? drawing.DocumentLinks.FirstOrDefault(x => x.Kind == DrawingDocumentKind.Specification)?.ReferenceNumber
            : null;
        var workOrder = drawing.DocumentLinks.FirstOrDefault(x => x.Kind == DrawingDocumentKind.WorkOrder)?.ReferenceNumber;
        var status = (canViewPending
            ? drawing.ApprovalStatus
            : drawing.IsObsolete
                ? DrawingApprovalStatus.Obsolete
                : drawing.CurrentApprovedRevisionId.HasValue
                    ? DrawingApprovalStatus.Approved
                    : DrawingApprovalStatus.Draft).ToString();
        var displayStatus = status == nameof(DrawingApprovalStatus.Obsolete) ? "Archived" : status;
        var attentionReasons = canViewPending ? BuildAttentionReasons(drawing) : [];
        var visibleRevisionCount = canViewPending
            ? drawing.Revisions.Count
            : drawing.CurrentApprovedRevisionId.HasValue ? 1 : 0;
        yield return new(
            $"drawing-{drawing.Id}",
            "drawings",
            "Drawings",
            drawing.Title,
            drawing.DrawingNumber,
            $"{displayStatus} controlled drawing with {visibleRevisionCount} visible revision record{(visibleRevisionCount == 1 ? string.Empty : "s")}.",
            drawing.Customer,
            specification,
            workOrder,
            null,
            ["Drawing number", status, .. drawing.Parts.Select(x => x.PartNumber), .. (canViewMylar ? drawing.Mylars.Select(x => x.MylarNumber) : [])],
            drawing.Notes ?? "No drawing notes recorded.",
            drawing.Id,
            attentionReasons);

        foreach (var part in drawing.Parts)
            yield return new(
                $"drawing-part-{part.Id}",
                "parts",
                "Parts",
                $"{part.PartNumber} drawing link",
                part.PartNumber,
                $"Part linked to drawing {drawing.DrawingNumber}.",
                drawing.Customer,
                specification,
                workOrder,
                null,
                ["Part number", status, "Drawing link"],
                drawing.Notes ?? $"Linked to {drawing.Title}.",
                drawing.Id);

        foreach (var link in drawing.DocumentLinks)
        {
            var category = link.Kind == DrawingDocumentKind.Specification ? "specifications" : "documents";
            yield return new(
                $"drawing-link-{link.Id}",
                category,
                category == "specifications" ? "Specifications" : "Documents",
                link.Title ?? $"{link.Kind} for {drawing.DrawingNumber}",
                link.ReferenceNumber,
                $"{link.Kind} linked to drawing {drawing.DrawingNumber}.",
                drawing.Customer,
                link.Kind == DrawingDocumentKind.Specification ? link.ReferenceNumber : specification,
                link.Kind == DrawingDocumentKind.WorkOrder ? link.ReferenceNumber : workOrder,
                null,
                [link.Kind.ToString(), status, "Drawing link"],
                link.Location ?? drawing.Notes ?? $"Linked to {drawing.Title}.",
                drawing.Id);
        }
    }

    private static EngineeringSearchResultDto ToToolSearchRecord(ToolRecord tool)
    {
        var destination = tool.CustodyStatus switch
        {
            ToolCustodyStatus.OutsideProcessing => $"Outside processing at {tool.CurrentVendor ?? "unspecified vendor"}",
            ToolCustodyStatus.CheckedOut => $"Checked out to {tool.CurrentLocation?.Code ?? tool.CurrentHolder ?? "unspecified location"}",
            _ => $"Stored at {tool.CurrentLocation?.Code ?? "unassigned location"}"
        };
        var documentTerms = tool.Documents.SelectMany(document => new[]
        {
            document.DocumentNumber ?? string.Empty,
            document.OriginalFileName,
            document.Notes ?? string.Empty
        });
        return new EngineeringSearchResultDto(
            $"tool-{tool.Id}",
            "tools",
            "Tools",
            tool.Name,
            tool.ToolNumber,
            $"{tool.ToolType}. {destination}. Default check-in location {tool.HomeLocationAssignment?.Location.Code ?? "not assigned"}.",
            tool.Owner,
            null,
            null,
            null,
            ["Tool number", tool.ToolType, tool.CustodyStatus.ToString(), tool.Owner,
                .. tool.PartNumbers.Select(part => part.PartNumber),
                tool.HomeLocationAssignment?.Location.Code ?? string.Empty, tool.HomeLocationAssignment?.Location.Description ?? string.Empty,
                tool.CurrentLocation?.Code ?? string.Empty, tool.CurrentLocation?.Description ?? string.Empty,
                .. documentTerms],
            tool.Notes ?? tool.Description ?? "No tooling notes recorded.",
            ToolId: tool.Id);
    }

    private static IReadOnlyList<string> BuildAttentionReasons(Drawing drawing)
    {
        var reasons = new List<string>();
        reasons.AddRange(drawing.Revisions
            .Where(revision => revision.Status == DrawingRevisionStatus.UnderReview)
            .Select(revision => $"Rev {revision.RevisionNumber} awaiting approval since {revision.UploadedAt:d}."));
        reasons.AddRange(drawing.Mylars
            .Where(mylar => mylar.IsCheckedOut)
            .Select(mylar => $"Mylar {mylar.MylarNumber} checked out to {mylar.CheckedOutBy ?? "an unrecorded holder"}."));
        if (drawing.Revisions.Count == 0)
            reasons.Add("Missing revision package and controlled drawing file.");
        reasons.AddRange(drawing.Revisions
            .Where(revision => revision.FileSize == 0 || string.IsNullOrWhiteSpace(revision.StoredFilePath))
            .Select(revision => $"Rev {revision.RevisionNumber} needs a controlled drawing file before approval."));
        if (drawing.EffectiveDate is not null &&
            drawing.EffectiveDate >= DateTime.UtcNow.Date &&
            drawing.EffectiveDate <= DateTime.UtcNow.Date.AddDays(30))
            reasons.Add($"Effective {drawing.EffectiveDate:d}.");
        return reasons;
    }

    private static int? ResolveDrawingLink(
        EngineeringSearchResultDto record,
        IReadOnlyList<Drawing> drawings)
    {
        if (record.Category == "parts")
            return drawings.FirstOrDefault(drawing =>
                drawing.Parts.Any(part =>
                    string.Equals(part.PartNumber, record.Identifier, StringComparison.OrdinalIgnoreCase)))?.Id;

        if (record.Category is "specifications" or "documents")
            return drawings.FirstOrDefault(drawing =>
                drawing.DocumentLinks.Any(link =>
                    string.Equals(link.ReferenceNumber, record.Identifier, StringComparison.OrdinalIgnoreCase)))?.Id;

        return null;
    }

    private static bool Matches(EngineeringSearchResultDto record, string query)
    {
        var haystack = string.Join(
            '\n',
            [
                record.CategoryLabel,
                record.Title,
                record.Identifier,
                record.Subtitle,
                record.Customer ?? string.Empty,
                record.SpecificationNumber ?? string.Empty,
                record.WorkOrder ?? string.Empty,
                record.ReportNumber ?? string.Empty,
                record.Note,
                .. record.Tags,
                .. (record.AttentionReasons ?? [])
            ]);

        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

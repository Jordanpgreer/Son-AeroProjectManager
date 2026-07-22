using EngineeringHub.Api.Dtos;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringSearchService
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

    private static readonly IReadOnlyList<EngineeringSearchCategoryDto> CategoryDefinitions =
    [
        new("parts", "Parts", Records.Count(record => record.Category == "parts")),
        new("drawings", "Drawings", Records.Count(record => record.Category == "drawings")),
        new("tools", "Tools", Records.Count(record => record.Category == "tools")),
        new("compounds", "Compounds", Records.Count(record => record.Category == "compounds")),
        new("test-reports", "Test reports", Records.Count(record => record.Category == "test-reports")),
        new("specifications", "Specifications", Records.Count(record => record.Category == "specifications")),
        new("documents", "Documents", Records.Count(record => record.Category == "documents"))
    ];

    public EngineeringDashboardDto GetDashboard(string? query)
    {
        var normalized = query?.Trim();
        var filtered = string.IsNullOrWhiteSpace(normalized)
            ? Records
            : Records.Where(record => Matches(record, normalized)).ToList();

        return new EngineeringDashboardDto(
            "Search by part number, tool number, drawing number, compound number or name, customer, specification number, work order, report number, or keyword / note text.",
            CategoryDefinitions,
            filtered);
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
                .. record.Tags
            ]);

        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

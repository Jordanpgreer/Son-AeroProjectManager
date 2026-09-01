namespace EstimatingDashboard.Api.Services;

public sealed record EstimatingRateReferenceSeed(
    string Key,
    string Category,
    int SourceRow,
    string Operation);

public sealed record EstimatingOperationMappingSeed(
    string FulcrumOperation,
    string RateReferenceKey);

public static class EstimatingRateReferenceCatalog
{
    public static readonly IReadOnlyList<EstimatingRateReferenceSeed> References =
    [
        Manufacturing(5, "Program"),
        Manufacturing(6, "Fixtures"),
        Manufacturing(7, "Metals - Mills"),
        Manufacturing(8, "Metals - Lathe"),
        Manufacturing(9, "Rubber Mold"),
        Manufacturing(10, "Plastic Injection Mold"),
        Manufacturing(11, "Plastic Compression Mold"),
        Manufacturing(12, "Assembly, Die Punch, Deburr"),
        Manufacturing(13, "Quality Inspection"),
        Manufacturing(14, "ID & Pack"),
        Manufacturing(15, "Mill/Turn"),
        Manufacturing(16, "Waterjet - Setup"),
        Manufacturing(17, "Waterjet - Operator"),
        Rubber(18, "Calendering"),
        Rubber(19, "Fabric Priming"),
        Rubber(20, "Hand Cutting"),
        Rubber(21, "CNC Cutting (Gunnar)"),
        Rubber(22, "Extruding"),
        Rubber(23, "Insert Prep (Sand/Degrease/Prime)"),
        Rubber(24, "Press Setup"),
        Rubber(25, "Layup"),
        Rubber(26, "Cure"),
        Rubber(27, "Detool + Chilling"),
        Rubber(28, "Deflash/Trim"),
        Rubber(29, "Setup (Supervisor)"),
        Rubber(30, "Testing"),
        Rubber(31, "Loading"),
        Rubber(32, "Die Punch"),
        Rubber(33, "Milling"),
        Rubber(34, "Admin/Setup"),
        Rubber(35, "Splicing"),
        Rubber(36, "Bond Room"),
        Rubber(37, "Quality"),
        Rubber(38, "Burn Holes"),
        Rubber(39, "Heat Seal"),
        Rubber(40, "Burn Holes"),
        Rubber(41, "Heat Seal"),
        Rubber(42, "Mold/Tooling"),
        Rubber(43, "Fixtures (Purchase)"),
        Rubber(44, "Rubber Assembly"),
        Rubber(45, "Tooling (In House)")
    ];

    public static readonly IReadOnlyList<EstimatingOperationMappingSeed> DefaultMappings =
    [
        new("Material prep rubber", "rubber-breakdown:34"),
        new("Rubber Mold Tool Check", "manufacturing:9"),
        new("Rubber Mold Set Up", "manufacturing:9"),
        new("QA First piece Inspection", "rubber-breakdown:37"),
        new("Rubber Mold Production", "manufacturing:9"),
        new("QA Production Inspection", "rubber-breakdown:37"),
        new("Trim & Deflash set up", "rubber-breakdown:28"),
        new("Trim & Deflash", "rubber-breakdown:28"),
        new("ID & Pack", "manufacturing:14"),
        new("QA Final Inspection", "rubber-breakdown:37")
    ];

    private static EstimatingRateReferenceSeed Manufacturing(int row, string operation) =>
        new($"manufacturing:{row}", "manufacturing", row, operation);

    private static EstimatingRateReferenceSeed Rubber(int row, string operation) =>
        new($"rubber-breakdown:{row}", "rubber-breakdown", row, operation);
}

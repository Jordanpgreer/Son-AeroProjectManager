using System.Text.Json;

namespace Portal.Api.Services.ApiCustomizer;

public sealed record ApiField(string Path, string Label, string Type, string Description, string[] Choices, string Availability = "available");
public sealed record ApiInput(string Key, string Label, string Type, string Description, bool Required,
    string[] Choices, ApiInput[] Children, string? Format = null);
public sealed record ApiSource(string Id, string Label, string Category, string Description, string Method,
    string Path, string Shape, string? DataPath, ApiField[] Fields, ApiInput[] Inputs,
    string[] Permissions, string? Skip, string? Take);
public sealed record ApiCatalog(string Version, IReadOnlyList<ApiSource> Sources);
public sealed record ReportFieldRef(string SheetId, string Path);
public sealed record ReportColumn(string SheetId, string Path, string Header, string Format = "text", int Width = 24);
public sealed class ReportSheet
{
    public string Id { get; set; } = "parts";
    public string Name { get; set; } = "Parts";
    public string SourceId { get; set; } = "";
    public string? ParentSheetId { get; set; }
    public Dictionary<string, JsonElement> Inputs { get; set; } = [];
    public Dictionary<string, ReportFieldRef> Bindings { get; set; } = [];
    public List<ReportColumn> Columns { get; set; } = [];
    public int StartRow { get; set; } = 1;
    public int StartColumn { get; set; } = 1;
    public int? SortColumn { get; set; }
    public bool SortDescending { get; set; }
    public bool IncludeEmptyParents { get; set; } = true;
}
public sealed class ReportDefinition
{
    public string Name { get; set; } = "Fulcrum Report";
    public string HeaderColor { get; set; } = "C65D21";
    public bool FreezeHeaders { get; set; } = true;
    public bool AutoFilter { get; set; } = true;
    public bool WrapText { get; set; }
    public int MaxRecords { get; set; } = 1000;
    public string OutputMode { get; set; } = "separate";
    public string? DetailSheetId { get; set; }
    public bool AutoSize { get; set; } = true;
    public int? OutputSortColumn { get; set; }
    public bool OutputSortDescending { get; set; }
    public List<ReportColumn> OutputColumns { get; set; } = [];
    public List<ReportSheet> Sheets { get; set; } = [];
}
public sealed record ReportRunRequest(ReportDefinition Definition, bool Sample = false);
public sealed record ReportSheetResult(string Id, string Name, List<ReportColumn> Columns, List<object?[]> Rows);
public sealed record ReportRunResult(Guid Id, string Name, bool Sample, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, int RequestCount, IReadOnlyList<ReportSheetResult> Sheets,
    IReadOnlyList<string> Warnings);
public sealed record SavedReportRequest(ReportDefinition Definition, int Version);
public sealed class CustomizerReportRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string DefinitionJson { get; set; } = "";
    public int Version { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class CustomizerAuditRecord
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string ReportName { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}
public sealed class ReportValidationException(string message) : Exception(message);

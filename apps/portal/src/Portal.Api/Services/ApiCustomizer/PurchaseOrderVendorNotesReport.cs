using System.Globalization;
using System.Text.Json;

namespace Portal.Api.Services.ApiCustomizer;

public static class PurchaseOrderVendorNotesReport
{
    public const string SourceId = "ARDA purchase-order-vendor-notes";
    public const string PurchaseOrders = "POST /api/purchase-orders/list";
    public const string PartLines = "POST /api/purchase-orders/{purchaseOrderId}/part-line-items/list";
    public const int MaxParentOrders = 10000;
    public const int MaxRows = 100000;
    public const int MaxRequests = 20100;
    public const int TimeoutMinutes = 30;

    private static ApiField Field(string path, string label, string type, string description) => new(path, label, type, description, []);

    public static readonly ApiField[] TemplateFields =
    [
        Field("purchaseOrderNumber", "PO Number", "string", "Fulcrum purchase-order number."),
        Field("lineItemNumber", "Line Item", "integer", "Part-line number within the purchase order."),
        Field("quantity", "Qty", "number", "Quantity on the purchase-order part line."),
        Field("createdDate", "Created Date", "string", "Purchase-order issue date, which Fulcrum defines as the PO creation date."),
        Field("vendorNote", "Vendor Note", "string", "Vendor note stored on this purchase-order part line.")
    ];

    public static ApiSource CreateSource(ApiCatalog catalog)
    {
        ApiSource Source(string id) => catalog.Sources.Single(s => s.Id == id);
        var extras = new[] { (PurchaseOrders, "purchaseOrder", "Purchase Order"), (PartLines, "lineItem", "Part Line") }
            .SelectMany(x => Source(x.Item1).Fields.Select(f => f with { Path = x.Item2 + "." + f.Path, Label = x.Item3 + " / " + f.Label }));
        return new(SourceId, "PO Vendor Notes By Line Item", "Ready Reports",
            "One row per purchase-order part line, with the line's vendor note and the parent PO creation date.",
            "COMPOSE", "/arda/reports/purchase-order-vendor-notes", "array", null,
            [.. TemplateFields, .. extras],
            [.. Source(PurchaseOrders).Inputs,
                new("report.poNumbers", "PO Numbers", "string", "Comma-separated or one per line. Leave blank for all matching purchase orders.", false, [], [])],
            new[] { PurchaseOrders, PartLines }.SelectMany(id => Source(id).Permissions).Distinct().ToArray(), null, null);
    }

    public static async Task<List<JsonElement>> ReadAsync(
        Dictionary<string, JsonElement> inputs, bool sample,
        Func<string, Dictionary<string, JsonElement>, Task<List<JsonElement>>> fetch,
        List<string> warnings, CancellationToken ct)
    {
        var orderInputs = inputs.Where(p => !p.Key.StartsWith("report.", StringComparison.Ordinal)).ToDictionary();
        var terms = inputs.GetValueOrDefault("report.poNumbers").ValueKind == JsonValueKind.String
            ? inputs["report.poNumbers"].GetString()!.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray() : [];
        if (terms.Length > 500) throw new ReportValidationException("Enter up to 500 PO numbers per report.");
        if (terms.Length > 0)
        {
            var numbers = new List<int>(terms.Length);
            foreach (var term in terms)
                if (!int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0)
                    throw new ReportValidationException($"PO number '{term}' is not a valid non-negative whole number.");
                else numbers.Add(number);
            orderInputs["body.numbers"] = JsonSerializer.SerializeToElement(numbers.Distinct());
        }

        var orders = sample ? SampleOrders() : await fetch(PurchaseOrders, orderInputs);
        if (orders.Count > MaxParentOrders)
            throw new ReportValidationException($"PO vendor notes supports up to {MaxParentOrders:N0} purchase orders per report. Filter PO numbers or status; no partial workbook was created.");

        var batches = new List<JsonElement>[orders.Count];
        var rowCount = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, orders.Count), new ParallelOptions { MaxDegreeOfParallelism = sample ? 1 : 4, CancellationToken = ct }, async (index, token) =>
        {
            var order = orders[index];
            var id = Text(order, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new ReportValidationException("A purchase order is missing its Fulcrum ID. No partial workbook was created.");
            var lines = sample ? SamplePartLines() : await fetch(PartLines,
                new() { ["path.purchaseOrderId"] = JsonSerializer.SerializeToElement(id) });
            token.ThrowIfCancellationRequested();
            var rows = Assemble(order, lines);
            if (Interlocked.Add(ref rowCount, rows.Count) > MaxRows)
                throw new ReportValidationException("PO vendor notes exceeds 100,000 rows. Narrow the purchase-order filters; no partial workbook was created.");
            batches[index] = rows;
        });

        warnings.Add("Vendor Note is the note stored on each PO part line. Blank line notes are retained. Other PO line types are omitted because Fulcrum does not expose a line-level vendor note on them.");
        return batches.SelectMany(batch => batch).ToList();
    }

    public static List<JsonElement> Assemble(JsonElement purchaseOrder, List<JsonElement> partLines) =>
        partLines.OrderBy(line => Number(line, "number") ?? decimal.MaxValue).Select(line => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["purchaseOrderNumber"] = ScalarText(purchaseOrder, "number"),
            ["lineItemNumber"] = Number(line, "number"),
            ["quantity"] = Number(line, "quantity"),
            ["createdDate"] = Text(purchaseOrder, "issueDate"),
            ["vendorNote"] = Text(line, "vendorNote"),
            ["purchaseOrder"] = purchaseOrder,
            ["lineItem"] = line
        })).ToList();

    private static string? ScalarText(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) switch
    {
        string text => text,
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => null
    };
    private static string? Text(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as string;
    private static decimal? Number(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as decimal?;
    private static List<JsonElement> Parse(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json)!;
    private static List<JsonElement> SampleOrders() => Parse("""[{"id":"demo-po-1","number":1042,"issueDate":"2026-09-18T14:30:00Z","status":"ordered"}]""");
    private static List<JsonElement> SamplePartLines() => Parse("""[{"id":"demo-line-1","number":1,"quantity":25,"vendorNote":"Include material certifications with shipment."},{"id":"demo-line-2","number":2,"quantity":10,"vendorNote":"Package separately and identify heat lot."}]""");
}

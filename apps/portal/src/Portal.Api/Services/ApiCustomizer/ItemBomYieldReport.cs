using System.Text.Json;

namespace Portal.Api.Services.ApiCustomizer;

public static class ItemBomYieldReport
{
    public const string SourceId = "ARDA item-bom-yield";
    public const string Items = InventoryBomReport.Items;
    public const string Operations = InventoryBomReport.Operations;
    public const string Components = InventoryBomReport.Components;
    public const int MaxParentItems = 10000;
    public const int MaxRows = 100000;
    public const int MaxRequests = 20100;
    public const int TimeoutMinutes = 30;

    private static ApiField Field(string path, string label, string type, string description) => new(path, label, type, description, []);

    public static readonly ApiField[] TemplateFields =
    [
        Field("parentItemNumber", "Parent Item Number", "string", "Item number whose routing contains the creates-basis child line."),
        Field("childNumber", "Child Number", "string", "Child item number on the creates-basis BOM line."),
        Field("revision", "Revision", "string", "Revision of the child item on the BOM line."),
        Field("operationName", "Operation Name", "string", "Name of the routing operation linked to the BOM line."),
        Field("operationNumber", "Operation Number", "integer", "Fulcrum routing order; the API has no separate operation-number field."),
        Field("createsQuantity", "Creates Quantity", "number", "BOM line quantity when Fulcrum's quantity basis is creates.")
    ];

    public static ApiSource CreateSource(ApiCatalog catalog)
    {
        ApiSource Source(string id) => catalog.Sources.Single(s => s.Id == id);
        var extras = new[] { (Items, "item", "Parent Item"), (Operations, "step", "Routing Step"), (Components, "component", "BOM Child") }
            .SelectMany(x => Source(x.Item1).Fields.Select(f => f with { Path = x.Item2 + "." + f.Path, Label = x.Item3 + " / " + f.Label }));
        return new(SourceId, "Item BOM Yield Report", "Ready Reports",
            "One row per creates-basis BOM child, with the child revision and its linked routing operation.",
            "COMPOSE", "/arda/reports/item-bom-yield", "array", null,
            [.. TemplateFields, .. extras],
            [.. Source(Items).Inputs,
                new("report.itemSearch", "Parent Item Numbers Or IDs", "string", "Comma-separated or one per line. Leave blank for all matching parent items.", false, [], []),
                new("report.searchBy", "Search By", "string", "Match parent item numbers or internal Fulcrum IDs.", false, ["number", "id"], []),
                new("report.matchMode", "Number Match", "string", "How parent item numbers are matched.", false, ["equal", "startsWith", "contains"], [])],
            new[] { Items, Operations, Components }.SelectMany(id => Source(id).Permissions).Distinct().ToArray(), null, null);
    }

    public static async Task<List<JsonElement>> ReadAsync(
        Dictionary<string, JsonElement> inputs, bool sample,
        Func<string, Dictionary<string, JsonElement>, Task<List<JsonElement>>> fetch,
        List<string> warnings, CancellationToken ct)
    {
        var itemInputs = inputs.Where(p => !p.Key.StartsWith("report.", StringComparison.Ordinal)).ToDictionary();
        var terms = inputs.GetValueOrDefault("report.itemSearch").ValueKind == JsonValueKind.String
            ? inputs["report.itemSearch"].GetString()!.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray() : [];
        if (terms.Length > 50) throw new ReportValidationException("Enter up to 50 parent item numbers or IDs, or use a number prefix to cover a larger group.");
        if (terms.Length > 0)
        {
            var byId = inputs.GetValueOrDefault("report.searchBy").ToString() == "id";
            var mode = inputs.GetValueOrDefault("report.matchMode").ToString();
            itemInputs[byId ? "body.itemIds" : "body.numbers"] = byId ? JsonSerializer.SerializeToElement(terms)
                : JsonSerializer.SerializeToElement(terms.Select(query => new { query, mode = string.IsNullOrEmpty(mode) ? "equal" : mode, casingOption = "caseInsensitive" }));
        }

        var items = sample ? SampleItems() : await fetch(Items, itemInputs);
        if (items.Count > MaxParentItems)
            throw new ReportValidationException($"Item BOM yield supports up to {MaxParentItems:N0} starting items. Filter item numbers or revisions; no partial workbook was created.");

        var batches = new List<JsonElement>[items.Count];
        var notices = new List<string>[items.Count];
        var rowCount = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = sample ? 1 : 3, CancellationToken = ct }, async (index, token) =>
        {
            var item = items[index];
            var id = Text(item, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new ReportValidationException("A parent item is missing its Fulcrum ID. No partial workbook was created.");
            var links = new Dictionary<string, JsonElement> { ["path.itemId"] = JsonSerializer.SerializeToElement(id) };
            var operations = sample ? SampleOperations() : await fetch(Operations, links);
            token.ThrowIfCancellationRequested();
            var components = sample ? SampleComponents() : await fetch(Components, links);
            var notes = new List<string>();
            var rows = Assemble(item, operations, components, notes);
            if (Interlocked.Add(ref rowCount, rows.Count) > MaxRows)
                throw new ReportValidationException("Item BOM yield exceeds 100,000 rows. Narrow the item filters; no partial workbook was created.");
            batches[index] = rows;
            notices[index] = notes;
        });

        warnings.AddRange(notices.SelectMany(note => note).Distinct());
        warnings.Add("Only BOM input-item lines whose Fulcrum quantity basis is creates are included. Creates Quantity is the configured valueTypeUnits value, not a calculated scrap percentage.");
        return batches.SelectMany(batch => batch).ToList();
    }

    public static List<JsonElement> Assemble(JsonElement item, List<JsonElement> operations,
        List<JsonElement> components, List<string> warnings)
    {
        var steps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var step in operations)
        {
            var id = Text(step, "id");
            if (string.IsNullOrWhiteSpace(id) || !steps.TryAdd(id, step))
                throw new ReportValidationException("An item routing contains a missing or duplicate step ID. No partial workbook was created.");
        }

        var rows = new List<JsonElement>();
        foreach (var component in components.Where(component => Text(component, "valueType") == "creates"))
        {
            var quantity = Number(component, "valueTypeUnits");
            if (quantity is null || quantity < 0)
                throw new ReportValidationException("A creates-basis BOM line has no valid quantity. No partial workbook was created.");
            var stepId = Text(component, "routingStepId");
            var step = stepId is not null && steps.TryGetValue(stepId, out var found) ? found : default;
            if (stepId is not null && step.ValueKind != JsonValueKind.Object)
                warnings.Add("Some creates-basis BOM lines reference routing steps that were not returned. Their operation columns are blank.");
            rows.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["parentItemNumber"] = Text(item, "number"),
                ["childNumber"] = Text(component, "number"),
                ["revision"] = Text(component, "revision"),
                ["operationName"] = Text(step, "name"),
                ["operationNumber"] = Number(step, "order"),
                ["createsQuantity"] = quantity,
                ["item"] = item,
                ["step"] = step.ValueKind == JsonValueKind.Object ? step : null,
                ["component"] = component
            }));
        }
        return rows;
    }

    private static string? Text(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as string;
    private static decimal? Number(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as decimal?;
    private static List<JsonElement> Parse(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json)!;
    private static List<JsonElement> SampleItems() => Parse("""[{"id":"demo-parent-1","number":"ASSY-100","revision":{"revision":"B"}}]""");
    private static List<JsonElement> SampleOperations() => Parse("""[{"id":"step-1","order":20,"name":"Machine Components"},{"id":"step-2","order":30,"name":"Final Assembly"}]""");
    private static List<JsonElement> SampleComponents() => Parse("""[{"id":"input-1","number":"PART-101","revision":"A","routingStepId":"step-1","valueType":"creates","valueTypeUnits":4},{"id":"input-2","number":"HARDWARE-200","revision":"NC","routingStepId":"step-2","valueType":"requires","valueTypeUnits":8}]""");
}

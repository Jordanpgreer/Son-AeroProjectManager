using System.Text.Json;

namespace Portal.Api.Services.ApiCustomizer;

public static class InventoryBomReport
{
    public const string SourceId = "ARDA inventory-bom";
    public const string Items = "POST /api/items/list/v2";
    public const string Operations = "POST /api/items/{itemId}/routing/operations/list";
    public const string Components = "POST /api/items/{itemId}/routing/input-items/list";
    public const int MaxParentItems = 10000;
    public const int MaxRows = 100000;
    public const int MaxRequests = 20100;
    public const int TimeoutMinutes = 30;

    public static readonly ApiField[] TemplateFields =
    [
        Blank("bomId", "BOM ID"),
        Field("revision", "Revision", "string", "Item revision."),
        Blank("hold", "Hold"),
        Field("inventoryId", "Inventory ID", "string", "Fulcrum item number, not its internal database ID."),
        Blank("subitem", "Subitem"), Blank("warehouse", "Warehouse"),
        Blank("startDate", "Start Date"), Blank("endDate", "End Date"),
        Field("description", "Description", "string", "Item description."),
        Field("operationNbr", "Operation Nbr", "integer", "Routing step order, not the system operation ID."),
        Field("operationDescr", "Operation Descr", "string", "Routing step name."),
        Blank("workCenter", "Work Center"),
        Field("setupTime", "Setup Time", "duration", "Setup time converted from Fulcrum's stated time basis to hours:minutes:seconds. Per-unit setup is left blank for review."),
        Field("runUnits", "Run Units", "number", "1 for per-unit labor time; the given rate for units per hour. Blank for fixed time."),
        Field("runTime", "Run Time", "duration", "Labor time in hours:minutes:seconds. Units-per-hour rates use one hour with the rate in Run Units."),
        Field("machineUnits", "Machine Units", "number", "1 for per-unit machine time; the given rate for units per hour. Blank for fixed time."),
        Field("machineTime", "Machine Time", "duration", "Machine time in hours:minutes:seconds, with its quantity in Machine Units."),
        Blank("queueTime", "Queue Time"), Blank("backflushLabor", "Backflush Labor"),
        Blank("scrapAction", "Scrap Action"),
        Field("materialInventoryId", "Matl Inventory ID", "string", "Input item number, including material-tied input items. Matched by routingStepId."),
        Blank("materialSubitem", "Matl Subitem"),
        Field("quantityRequired", "Qty Req", "number", "Input valueTypeUnits when valueType is requires. Creates-basis quantities remain blank; add Quantity Basis and Source Quantity to review them."),
        Blank("uom", "UOM"), Blank("unitCost", "Unit Cost"), Blank("materialType", "Material Type"),
        Blank("phantomRouting", "Phantom Routing"), Blank("backflush", "Backflush"),
        Blank("materialWarehouse", "Matl Warehouse"), Blank("location", "Location"),
        Blank("scrapFactor", "Scrap Factor"),
        Field("notes", "NOTES", "string", "Item internal notes and routing instructions.")
    ];

    private static ApiField Field(string path, string label, string type, string description) => new(path, label, type, description, []);
    private static ApiField Blank(string path, string label) => new(path, label, "string",
        "No verified equivalent in these Fulcrum sources. Left blank; choose another field or a Fulcrum custom field to map this column.", [], "unmapped");

    public static ApiSource CreateSource(ApiCatalog catalog)
    {
        ApiSource Source(string id) => catalog.Sources.Single(s => s.Id == id);
        var extras = new[] { (Items, "item", "Item"), (Operations, "step", "Routing Step"), (Components, "component", "BOM Input") }
            .SelectMany(x => Source(x.Item1).Fields.Select(f => f with { Path = x.Item2 + "." + f.Path, Label = x.Item3 + " / " + f.Label }));
        return new(SourceId, "Inventory BOM", "Ready Reports",
            "Item revisions, ordered routing steps, times and required inventory inputs in a customizable BOM table.",
            "COMPOSE", "/arda/reports/inventory-bom", "array", null,
            [.. TemplateFields, Field("itemId", "Fulcrum Item ID", "string", "Internal item ID for related lookups."),
                Field("matchStatus", "Material Match", "string", "Matched, No Material, Unassigned, or Missing Operation."),
                Field("quantityBasis", "Quantity Basis", "string", "Original requires/creates quantity basis."),
                Field("sourceQuantity", "Source Quantity", "number", "Original input valueTypeUnits without conversion."),
                .. extras],
            [.. Source(Items).Inputs,
                new("report.itemSearch", "Item Numbers Or IDs", "string", "Comma-separated or one per line. Leave blank for all matching items.", false, [], []),
                new("report.searchBy", "Search By", "string", "Match item numbers or internal Fulcrum IDs.", false, ["number", "id"], []),
                new("report.matchMode", "Number Match", "string", "How item numbers are matched.", false, ["equal", "startsWith", "contains"], []),
                new("report.repeatOperations", "Repeat Operation Details", "boolean", "Repeat operation details on every material line instead of showing them once per operation.", false, [], [])],
            new[] { Items, Operations, Components }.SelectMany(id => Source(id).Permissions).Distinct().ToArray(), null, null);
    }

    public static async Task<List<JsonElement>> ReadAsync(
        Dictionary<string, JsonElement> inputs, bool sample,
        Func<string, Dictionary<string, JsonElement>, Task<List<JsonElement>>> fetch,
        List<string> warnings, CancellationToken ct)
    {
        var repeat = inputs.TryGetValue("report.repeatOperations", out var value) && value.ValueKind == JsonValueKind.True;
        var itemInputs = inputs.Where(p => !p.Key.StartsWith("report.", StringComparison.Ordinal)).ToDictionary();
        var terms = inputs.GetValueOrDefault("report.itemSearch").ValueKind == JsonValueKind.String
            ? inputs["report.itemSearch"].GetString()!.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray() : [];
        if (terms.Length > 50) throw new ReportValidationException("Enter up to 50 item numbers or IDs, or use a number prefix to cover a larger group.");
        if (terms.Length > 0)
        {
            var byId = inputs.GetValueOrDefault("report.searchBy").ToString() == "id";
            var mode = inputs.GetValueOrDefault("report.matchMode").ToString();
            itemInputs[byId ? "body.itemIds" : "body.numbers"] = byId ? JsonSerializer.SerializeToElement(terms)
                : JsonSerializer.SerializeToElement(terms.Select(query => new { query, mode = string.IsNullOrEmpty(mode) ? "equal" : mode, casingOption = "caseInsensitive" }));
        }
        var items = sample ? SampleItems() : await fetch(Items, itemInputs);
        if (items.Count > MaxParentItems)
            throw new ReportValidationException($"Inventory BOM supports up to {MaxParentItems:N0} starting items per report. Filter item numbers or revisions and run smaller reports; no partial workbook was created.");
        var batches = new List<JsonElement>[items.Count];
        var notices = new List<string>[items.Count];
        var rowCount = 0;
        // Bound vendor traffic while allowing a full inventory report to finish without thousands of serial waits.
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = sample ? 1 : 3, CancellationToken = ct }, async (index, token) =>
        {
            var item = items[index];
            var id = Text(item, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new ReportValidationException("A BOM item is missing its Fulcrum ID. No partial workbook was created.");
            var links = new Dictionary<string, JsonElement> { ["path.itemId"] = JsonSerializer.SerializeToElement(id) };
            var operations = sample ? SampleOperations() : await fetch(Operations, links);
            token.ThrowIfCancellationRequested();
            var components = sample ? SampleComponents() : await fetch(Components, links);
            var notes = new List<string>();
            var rows = Assemble(item, operations, components, repeat, notes);
            if (Interlocked.Add(ref rowCount, rows.Count) > MaxRows)
                throw new ReportValidationException("The BOM report exceeds 100,000 rows. Narrow its item filters; no partial workbook was created.");
            batches[index] = rows;
            notices[index] = notes;
        });
        warnings.AddRange(notices.SelectMany(n => n).Distinct());
        warnings.Add("Inventory BOM uses routing input items, including material-tied lines. Raw material-shape and nesting definitions are not extra consumption lines. Unmapped template columns remain blank. This is a report, not a validated ERP import file.");
        if (!repeat) warnings.Add("Operation details appear only on the first material row of each operation. Item identifiers repeat for filtering.");
        return batches.SelectMany(b => b).ToList();
    }

    public static List<JsonElement> Assemble(JsonElement item, List<JsonElement> operations, List<JsonElement> components, bool repeat, List<string> warnings)
    {
        var result = new List<JsonElement>();
        var ids = new HashSet<string>();
        foreach (var operation in operations)
            if (string.IsNullOrWhiteSpace(Text(operation, "id")) || !ids.Add(Text(operation, "id")!))
                throw new ReportValidationException("A BOM routing contains a missing or duplicate step ID. No partial workbook was created.");
        var grouped = components.GroupBy(c => Text(c, "routingStepId") ?? "").ToDictionary(g => g.Key, g => g.ToList());
        foreach (var operation in operations.OrderBy(o => Number(o, "order") ?? decimal.MaxValue))
        {
            var linked = grouped.GetValueOrDefault(Text(operation, "id")!) ?? [];
            if (linked.Count == 0) Add(operation, default, true, "No Material");
            else for (var i = 0; i < linked.Count; i++) Add(operation, linked[i], repeat || i == 0, "Matched");
        }
        foreach (var component in components.Where(c => !ids.Contains(Text(c, "routingStepId") ?? "")))
        {
            var status = string.IsNullOrEmpty(Text(component, "routingStepId")) ? "Unassigned" : "Missing Operation";
            Add(default, component, false, status);
            if (status == "Missing Operation") warnings.Add("Some materials reference a missing routing step. They are retained with blank operation columns; add Material Match to identify them.");
        }
        if (result.Count == 0) Add(default, default, false, "No Material");
        return result;

        void Add(JsonElement operation, JsonElement component, bool showOperation, string status)
        {
            var setup = Time(operation, "operation.setupTime", warnings, true);
            var run = Time(operation, "operation.laborTime", warnings);
            var machine = Time(operation, "operation.machineTime", warnings);
            var basis = Text(component, "valueType");
            var amount = Number(component, "valueTypeUnits");
            if (component.ValueKind == JsonValueKind.Object && amount.HasValue && basis != "requires")
                warnings.Add("Some input quantities are not on a requires basis. Qty Req is left blank for those lines; add Quantity Basis and Source Quantity to review the original values.");
            var row = new Dictionary<string, object?>
            {
                ["revision"] = Text(item, "revision.revision"), ["inventoryId"] = Text(item, "number"),
                ["description"] = Text(item, "description"), ["itemId"] = Text(item, "id"),
                ["operationNbr"] = showOperation ? Number(operation, "order") : null,
                ["operationDescr"] = showOperation ? Text(operation, "name") : null,
                ["setupTime"] = showOperation ? setup.Duration : null,
                ["runUnits"] = showOperation ? run.Units : null, ["runTime"] = showOperation ? run.Duration : null,
                ["machineUnits"] = showOperation ? machine.Units : null, ["machineTime"] = showOperation ? machine.Duration : null,
                ["materialInventoryId"] = Text(component, "number"),
                ["quantityRequired"] = basis == "requires" ? amount : null,
                ["quantityBasis"] = basis, ["sourceQuantity"] = amount, ["matchStatus"] = status,
                ["notes"] = string.Join("\n", new[] { Text(item, "internalNotes"), showOperation ? Text(operation, "instructions") : null }.Where(s => !string.IsNullOrWhiteSpace(s))),
                ["item"] = item, ["step"] = operation.ValueKind == JsonValueKind.Object ? operation : null,
                ["component"] = component.ValueKind == JsonValueKind.Object ? component : null
            };
            result.Add(JsonSerializer.SerializeToElement(row));
        }
    }

    public static (decimal? Duration, decimal? Units) Time(JsonElement operation, string path, List<string> warnings, bool setup = false)
    {
        var time = Number(operation, path + ".time");
        var option = Text(operation, path + ".option");
        if (time is null) return (null, null);
        if (time < 0 || time > 100000000 || setup && option?.StartsWith("fixed", StringComparison.Ordinal) != true)
        {
            warnings.Add("Some operation times need review and were left blank. Add the original routing time and option fields to inspect them.");
            return (null, null);
        }
        decimal? days = option switch
        {
            "fixedSeconds" or "secondsPerUnit" => time / 86400m,
            "fixedMinutes" or "minutesPerUnit" => time / 1440m,
            "fixedHours" or "hoursPerUnit" => time / 24m,
            "fixedDays" or "daysPerUnit" => time,
            "unitsPerHour" when time > 0 => 1m / 24m,
            _ => null
        };
        if (days is null) warnings.Add("Some operation time bases are unsupported or invalid. Their converted times are blank; original time fields remain available.");
        return (days, days is null ? null : option == "unitsPerHour" ? time : option?.EndsWith("PerUnit", StringComparison.Ordinal) == true ? 1m : null);
    }

    private static string? Text(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as string;
    private static decimal? Number(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as decimal?;
    private static List<JsonElement> Parse(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json)!;
    private static List<JsonElement> SampleItems() => Parse("""[{"id":"demo-item-1","number":"DEMO-001","revision":{"revision":"A"},"description":"Demonstration assembly","internalNotes":"Invented sample data"}]""");
    private static List<JsonElement> SampleOperations() => Parse("""[{"id":"step-1","order":1,"name":"Prepare Material","operation":{"setupTime":{"time":15,"option":"fixedMinutes"},"laborTime":{"time":2,"option":"minutesPerUnit"}}},{"id":"step-2","order":2,"name":"Inspect And Pack","operation":{"laborTime":{"time":100,"option":"unitsPerHour"},"machineTime":{"time":45,"option":"secondsPerUnit"}}}]""");
    private static List<JsonElement> SampleComponents() => Parse("""[{"id":"input-1","number":"DEMO-MATERIAL-001","routingStepId":"step-1","valueType":"requires","valueTypeUnits":0.5,"isMaterialLine":true},{"id":"input-2","number":"DEMO-COMPONENT-002","routingStepId":"step-1","valueType":"requires","valueTypeUnits":2},{"id":"input-3","number":"DEMO-PACKAGING-003","valueType":"requires","valueTypeUnits":1}]""");
}

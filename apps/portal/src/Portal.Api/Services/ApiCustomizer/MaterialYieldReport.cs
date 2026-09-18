using System.Text.Json;

namespace Portal.Api.Services.ApiCustomizer;

public static class MaterialYieldReport
{
    public const string SourceId = "ARDA material-yield";
    public const string Items = InventoryBomReport.Items;
    public const string Operations = InventoryBomReport.Operations;
    public const string Materials = "POST /api/items/{itemId}/routing/input-materials/list";
    public const string Components = InventoryBomReport.Components;
    public const int MaxParentItems = 25000;
    public const int MaxRows = 100000;
    public const int MaxRequests = 100000;
    public const int TimeoutMinutes = 90;

    private static ApiField Field(string path, string label, string type, string description) => new(path, label, type, description, []);

    public static readonly ApiField[] TemplateFields =
    [
        Field("fromPartNumber", "From P/N", "string", "Material item number, only when its Fulcrum material ID and routing step match exactly."),
        Field("toPartNumber", "To P/N", "string", "Produced item number."),
        Field("produces", "Produces", "integer", "User-defined output quantity for this material nesting, not a scrap percentage."),
        Field("producesUom", "Produces UOM", "string", "Stocking unit of measure of the produced item."),
        Field("routingStep", "Routing Step", "string", "Routing order and step name."),
        Field("operationName", "Operation Name", "string", "Name of the item's routing operation."),
        Field("operationNumber", "Operation No. (Order)", "integer", "Fulcrum routing order; the API has no separate operation-number field."),
        Field("materialName", "Raw Material", "string", "Fulcrum raw-material name."),
        Field("materialId", "Raw Material ID", "string", "Fulcrum material master identifier."),
        Field("d2", "Nesting Length (D2)", "number", "Length of the nesting bounding box in Fulcrum's material dimensions."),
        Field("d3", "Nesting Width (D3)", "number", "Width of the nesting bounding box for sheets."),
        Field("useForEstimatedCosting", "Used For Estimated Costing", "boolean", "Whether this nesting is selected for estimated costing."),
        Field("toRevision", "To Revision", "string", "Produced item revision."),
        Field("fromRevision", "From Revision", "string", "Matched material item revision."),
        Field("fromUom", "From UOM", "string", "Matched material item's stocking unit of measure."),
        Field("fromMatchStatus", "From P/N Match", "string", "Matched, unavailable, or ambiguous source item."),
        Field("routingStepId", "Routing Step ID", "string", "Exact routing-step identifier on the raw-material line."),
        Field("systemOperationId", "System Operation ID", "string", "Fulcrum system-operation identifier when the routing step exists."),
        Field("inputMaterialId", "BOM Material Line ID", "string", "Exact item-routing raw-material line identifier."),
        Field("nestingId", "Nesting ID", "string", "Exact nesting record identifier."),
        Field("toItemId", "To Item ID", "string", "Fulcrum produced-item identifier."),
        Field("fromItemId", "From Item ID", "string", "Fulcrum material-item identifier when matched.")
    ];

    public static ApiSource CreateSource(ApiCatalog catalog)
    {
        ApiSource Source(string id) => catalog.Sources.Single(s => s.Id == id);
        var extras = new[] { (Items, "item", "To Item"), (Items, "fromItem", "From Item"),
                (Operations, "step", "Routing Step"), (Materials, "material", "Raw Material"),
                (Components, "component", "Input Item") }
            .SelectMany(x => Source(x.Item1).Fields.Select(f => f with { Path = x.Item2 + "." + f.Path, Label = x.Item3 + " / " + f.Label }));
        return new(SourceId, "Material Produces Yield", "Ready Reports",
            "One Excel row per configured raw-material nesting, from a verified material P/N to the produced P/N.",
            "COMPOSE", "/arda/reports/material-yield", "array", null,
            [.. TemplateFields, .. extras],
            [.. Source(Items).Inputs,
                new("report.itemSearch", "Produced Item Numbers Or IDs", "string", "Comma-separated or one per line. Leave blank for all matching items.", false, [], []),
                new("report.searchBy", "Search By", "string", "Match produced item numbers or internal Fulcrum IDs.", false, ["number", "id"], []),
                new("report.matchMode", "Number Match", "string", "How produced item numbers are matched.", false, ["equal", "startsWith", "contains"], [])],
            new[] { Items, Operations, Materials, Components }.SelectMany(id => Source(id).Permissions).Distinct().ToArray(), null, null);
    }

    private sealed record ItemRoutingData(JsonElement Item, List<JsonElement> Operations,
        List<JsonElement> Materials, List<JsonElement> Components);

    public static async Task<List<JsonElement>> ReadAsync(
        Dictionary<string, JsonElement> inputs, bool sample,
        Func<string, Dictionary<string, JsonElement>, Task<List<JsonElement>>> fetch,
        List<string> warnings, CancellationToken ct)
    {
        var itemInputs = inputs.Where(p => !p.Key.StartsWith("report.", StringComparison.Ordinal)).ToDictionary();
        var terms = inputs.GetValueOrDefault("report.itemSearch").ValueKind == JsonValueKind.String
            ? inputs["report.itemSearch"].GetString()!.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray() : [];
        if (terms.Length > 50) throw new ReportValidationException("Enter up to 50 produced item numbers or IDs, or use a number prefix to cover a larger group.");
        if (terms.Length > 0)
        {
            var byId = inputs.GetValueOrDefault("report.searchBy").ToString() == "id";
            var mode = inputs.GetValueOrDefault("report.matchMode").ToString();
            itemInputs[byId ? "body.itemIds" : "body.numbers"] = byId ? JsonSerializer.SerializeToElement(terms)
                : JsonSerializer.SerializeToElement(terms.Select(query => new { query, mode = string.IsNullOrEmpty(mode) ? "equal" : mode, casingOption = "caseInsensitive" }));
        }
        var items = sample ? SampleItems() : await fetch(Items, itemInputs);
        if (items.Count > MaxParentItems)
            throw new ReportValidationException($"Material yield supports up to {MaxParentItems:N0} starting items. Filter item numbers or revisions; no partial workbook was created.");

        var data = new ItemRoutingData[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = sample ? 1 : 3, CancellationToken = ct }, async (index, token) =>
        {
            var item = items[index];
            var id = Text(item, "id");
            if (string.IsNullOrWhiteSpace(id)) throw new ReportValidationException("A produced item is missing its Fulcrum ID. No partial workbook was created.");
            var links = new Dictionary<string, JsonElement> { ["path.itemId"] = JsonSerializer.SerializeToElement(id) };
            var materials = sample ? SampleMaterials() : await fetch(Materials, links);
            token.ThrowIfCancellationRequested();
            var hasNestings = materials.Any(m => m.TryGetProperty("nestings", out var nests) && nests.ValueKind == JsonValueKind.Array && nests.GetArrayLength() > 0);
            var operations = !hasNestings ? [] : sample ? SampleOperations() : await fetch(Operations, links);
            var components = !hasNestings ? [] : sample ? SampleComponents() : await fetch(Components, links);
            data[index] = new(item, operations, materials, components);
        });

        var sourceItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (sample)
        {
            foreach (var source in SampleSourceItems()) sourceItems.Add(Text(source, "id")!, source);
        }
        else
        {
            var ids = data.SelectMany(d => d.Components)
                .Where(c => c.TryGetProperty("isMaterialLine", out var flag) && flag.ValueKind == JsonValueKind.True)
                .Select(c => Text(c, "itemId")).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var batch in ids.Chunk(50))
            {
                ct.ThrowIfCancellationRequested();
                var found = await fetch(Items, new() { ["body.itemIds"] = JsonSerializer.SerializeToElement(batch) });
                foreach (var source in found)
                {
                    var id = Text(source, "id");
                    if (!string.IsNullOrWhiteSpace(id) && !sourceItems.TryAdd(id, source))
                        throw new ReportValidationException("Fulcrum returned duplicate material item IDs. No partial workbook was created.");
                }
            }
        }

        var rows = new List<JsonElement>();
        foreach (var entry in data)
        {
            rows.AddRange(Assemble(entry.Item, entry.Operations, entry.Materials, entry.Components, sourceItems, warnings));
            if (rows.Count > MaxRows)
                throw new ReportValidationException("Material yield exceeds 100,000 rows. Narrow the item filters; no partial workbook was created.");
        }
        if (data.Any(d => d.Materials.Any(m => !m.TryGetProperty("nestings", out var nests) || nests.ValueKind != JsonValueKind.Array || nests.GetArrayLength() == 0)))
            warnings.Add("Raw-material lines without a configured Produces nesting are omitted; this report does not infer a yield.");
        if (rows.Any(r => Text(r, "fromMatchStatus") != "Matched"))
            warnings.Add("Some From P/N values could not be matched unambiguously to a material item and are blank. Use the material name, IDs, and match-status columns to investigate.");
        warnings.Add("Produces is Fulcrum's configured quantity per nesting bounding box, not a measured scrap rate. Each nesting is a separate row.");
        return rows;
    }

    public static List<JsonElement> Assemble(JsonElement item, List<JsonElement> operations,
        List<JsonElement> materials, List<JsonElement> components,
        IReadOnlyDictionary<string, JsonElement> sourceItems, List<string> warnings)
    {
        var steps = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var step in operations)
        {
            var id = Text(step, "id");
            if (string.IsNullOrWhiteSpace(id) || !steps.TryAdd(id, step))
                throw new ReportValidationException("A routing contains a missing or duplicate step ID. No partial workbook was created.");
        }
        var rows = new List<JsonElement>();
        foreach (var material in materials)
        {
            if (!material.TryGetProperty("nestings", out var nestings) || nestings.ValueKind != JsonValueKind.Array) continue;
            var stepId = Text(material, "routingStepId");
            var step = stepId is not null && steps.TryGetValue(stepId, out var foundStep) ? foundStep : default;
            const string missingStep = "Some material lines reference routing steps that were not returned. Their operation columns are blank.";
            if (stepId is not null && step.ValueKind != JsonValueKind.Object && nestings.GetArrayLength() > 0 && !warnings.Contains(missingStep))
                warnings.Add(missingStep);
            var materialId = Text(material, "materialId");
            var candidates = components.Where(c => c.TryGetProperty("isMaterialLine", out var flag) && flag.ValueKind == JsonValueKind.True
                && Text(c, "routingStepId") == stepId
                && Text(c, "itemId") is { } id && sourceItems.TryGetValue(id, out var source)
                && Text(source, "materialDetails.materialId") == materialId)
                .DistinctBy(c => Text(c, "itemId")).ToArray();
            var status = string.IsNullOrWhiteSpace(materialId) ? "Missing material ID"
                : candidates.Length == 1 ? "Matched" : candidates.Length > 1 ? "Ambiguous source items" : "No exact source item";
            var component = candidates.Length == 1 ? candidates[0] : default;
            var fromItem = candidates.Length == 1 ? sourceItems[Text(component, "itemId")!] : default;
            if (status == "Matched" && string.IsNullOrWhiteSpace(Text(fromItem, "number"))) status = "Source item has no P/N";
            foreach (var nesting in nestings.EnumerateArray())
            {
                var produces = Number(nesting, "produces");
                if (string.IsNullOrWhiteSpace(Text(nesting, "id")) || produces is null || produces < 0 || produces != decimal.Truncate(produces.Value))
                    throw new ReportValidationException("A material nesting has no valid ID or Produces quantity. No partial workbook was created.");
                var number = Number(step, "order");
                var name = Text(step, "name");
                var row = new Dictionary<string, object?>
                {
                    ["fromPartNumber"] = Text(fromItem, "number"), ["toPartNumber"] = Text(item, "number"),
                    ["produces"] = produces, ["producesUom"] = Text(item, "unitOfMeasureName"),
                    ["routingStep"] = number is null ? name : $"{number} - {name}",
                    ["operationName"] = name, ["operationNumber"] = number,
                    ["materialName"] = Text(material, "materialName"), ["materialId"] = materialId,
                    ["d2"] = Number(nesting, "d2"), ["d3"] = Number(nesting, "d3"),
                    ["useForEstimatedCosting"] = Bool(nesting, "useForEstimatedCosting"),
                    ["toRevision"] = Text(item, "revision.revision"), ["fromRevision"] = Text(fromItem, "revision.revision"),
                    ["fromUom"] = Text(fromItem, "unitOfMeasureName"), ["fromMatchStatus"] = status,
                    ["routingStepId"] = stepId, ["systemOperationId"] = Text(step, "systemOperationId"),
                    ["inputMaterialId"] = Text(material, "id"), ["nestingId"] = Text(nesting, "id"),
                    ["toItemId"] = Text(item, "id"), ["fromItemId"] = Text(fromItem, "id"),
                    ["item"] = item, ["step"] = step.ValueKind == JsonValueKind.Object ? step : null,
                    ["material"] = material, ["component"] = component.ValueKind == JsonValueKind.Object ? component : null,
                    ["nesting"] = nesting, ["fromItem"] = fromItem.ValueKind == JsonValueKind.Object ? fromItem : null
                };
                rows.Add(JsonSerializer.SerializeToElement(row));
            }
        }
        return rows;
    }

    private static string? Text(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as string;
    private static decimal? Number(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as decimal?;
    private static bool? Bool(JsonElement row, string path) => FulcrumReportRunner.Read(row, path) as bool?;
    private static List<JsonElement> Parse(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json)!;
    private static List<JsonElement> SampleItems() => Parse("""[{"id":"demo-item-1","number":"DEMO-PART-001","revision":{"revision":"A"},"unitOfMeasureName":"Piece"}]""");
    private static List<JsonElement> SampleOperations() => Parse("""[{"id":"step-1","order":10,"name":"Cut","systemOperationId":"cut-op"}]""");
    private static List<JsonElement> SampleMaterials() => Parse("""[{"id":"material-line-1","materialId":"steel-sheet","materialName":"Steel Sheet","routingStepId":"step-1","nestings":[{"id":"nest-1","d2":12,"d3":8,"produces":4,"useForEstimatedCosting":true},{"id":"nest-2","d2":24,"d3":8,"produces":8,"useForEstimatedCosting":false}]}]""");
    private static List<JsonElement> SampleComponents() => Parse("""[{"id":"component-1","itemId":"demo-material-1","number":"DEMO-SHEET-001","routingStepId":"step-1","isMaterialLine":true}]""");
    private static List<JsonElement> SampleSourceItems() => Parse("""[{"id":"demo-material-1","number":"DEMO-SHEET-001","revision":{"revision":"NC"},"unitOfMeasureName":"Sheet","materialDetails":{"materialId":"steel-sheet"}}]""");
}

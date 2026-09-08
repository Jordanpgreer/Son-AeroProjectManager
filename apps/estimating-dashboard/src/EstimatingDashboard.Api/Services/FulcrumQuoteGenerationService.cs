using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

public sealed class FulcrumQuoteGenerationAlreadyRunningException : Exception;

internal sealed class FulcrumQuoteGenerationService(EstimatingAccessDbContext db,
    FulcrumQuoteGenerationClient client, EstimatingOperationMappingService mappings,
    IOptions<FulcrumQuoteSyncOptions> options, TimeProvider clock)
{
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);

    public async Task<FulcrumQuoteGenerationDto> GenerateAsync(int historyId, EstimatingAccessProfile access,
        CancellationToken cancellationToken)
    {
        if (!access.Permissions.Contains(EstimatingPermissions.ManageInputs, StringComparer.OrdinalIgnoreCase)
            || !access.Permissions.Contains(EstimatingPermissions.ManageQuotes, StringComparer.OrdinalIgnoreCase))
            throw new EstimatingQuoteWorkflowForbiddenException();
        var record = await db.QuoteHistory.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId, cancellationToken)
            ?? throw new EstimatingQuoteWorkflowNotFoundException();
        var estimators = await db.QuoteHistory.AsNoTracking().Select(x => x.EstimatingRep).Distinct().ToListAsync(cancellationToken);
        if (record.IsCompleted || !EstimatingEstimatorIdentity.MatchesUnambiguously(record.EstimatingRep, estimators, access))
            throw new EstimatingQuoteWorkflowForbiddenException();
        if (!await GenerationGate.WaitAsync(0, cancellationToken))
            throw new FulcrumQuoteGenerationAlreadyRunningException();
        try
        {
            await client.InitializeAsync(cancellationToken);
            var quoteId = FulcrumQuoteGenerationClient.Identifier(record.SourceId);
            var quote = await client.GetAsync($"api/quotes/{quoteId}", cancellationToken);
            if (Number(quote, "number") != record.QuoteNumber)
                throw new InvalidOperationException("The Fulcrum quote no longer matches this dashboard record. Refresh your quotes.");
            var rep = CustomText(quote, options.Value.CustomFields.EstimatingRep, "Estimator");
            if (!EstimatingEstimatorIdentity.MatchesUnambiguously(rep, estimators.Append(rep), access))
                throw new EstimatingQuoteWorkflowForbiddenException();
            if (Text(quote, "status").ToLowerInvariant() is "won" or "lost" or "cancelled"
                || !string.IsNullOrWhiteSpace(CustomText(quote, options.Value.CustomFields.EstimatingCompletionDate, "Estimating Complete Date")))
                throw new InvalidOperationException("This Fulcrum quote is no longer active for estimating. Refresh your quotes.");
            var lines = await client.ListAsync($"api/quotes/{quoteId}/part-line-items/list", false, cancellationToken);
            if (lines.Count == 0) throw new InvalidOperationException("This Fulcrum quote has no part lines to estimate.");
            var inventory = await client.GetAsync("api/inventory/availableByItem", cancellationToken);
            if (inventory.ValueKind != JsonValueKind.Object || inventory.EnumerateObject().Any(x =>
                x.Value.ValueKind != JsonValueKind.Number || !x.Value.TryGetDecimal(out _)))
                throw new InvalidOperationException("Fulcrum returned invalid available inventory; no stock figures were imported.");
            var catalog = await mappings.GetCatalogAsync(cancellationToken);
            var warnings = new List<string>();
            var context = new GenerationContext(client, inventory, catalog, warnings);
            var results = new List<FulcrumGeneratedQuoteLineDto>();
            foreach (var line in lines.OrderBy(x => Number(x, "number")))
            {
                if (string.IsNullOrWhiteSpace(Text(line, "id")))
                    throw new InvalidOperationException("Fulcrum returned a quote line without an identifier.");
                var quantity = Number(line, "quantity");
                if (quantity <= 0) throw new InvalidOperationException("A quote part has no positive quantity. Correct it in Fulcrum before generating.");
                var quantities = new List<decimal> { quantity };
                // Current public API omits quote price breaks. Accept an additive future field without substituting unrelated catalog prices.
                foreach (var priceBreak in Array(line, "priceBreaks"))
                {
                    var value = Number(priceBreak, "quantity");
                    if (value > 0 && !quantities.Contains(value)) quantities.Add(value);
                }
                if (quantities.Count == 1) warnings.Add("Fulcrum's public quote API does not expose additional quote pricing breaks. Verify the suggested quantities against the quote Pricing panel before sending.");
                var item = await context.AssemblyAsync(Text(line, "itemId"), 1, new HashSet<string>(), cancellationToken);
                results.Add(new(Text(line, "id"), quantity, quantities, item));
            }
            return new(historyId, record.QuoteNumber, record.Customer, clock.GetUtcNow(), results, warnings.Distinct().ToList());
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    private sealed class GenerationContext(FulcrumQuoteGenerationClient client, JsonElement inventory,
        EstimatingOperationMappingCatalogDto catalog, List<string> warnings)
    {
        private readonly Dictionary<string, JsonElement> items = new(StringComparer.Ordinal);
        private int nodes;
        private async Task<JsonElement> ItemAsync(string id, CancellationToken token)
        {
            if (!items.TryGetValue(id, out var item))
                items[id] = item = await client.GetAsync($"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}", token);
            return item;
        }

        public async Task<FulcrumGeneratedAssemblyDto> AssemblyAsync(string id, decimal usage,
            HashSet<string> ancestors, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!ancestors.Add(id)) throw new InvalidOperationException("Fulcrum contains a circular Make BOM. No partial estimate was generated.");
            if (ancestors.Count > 32 || ++nodes > 250) throw new InvalidOperationException("The Make BOM exceeds the safe import size. No partial estimate was generated.");
            var item = await ItemAsync(id, token);
            var number = Text(item, "number");
            if (string.IsNullOrWhiteSpace(number))
                throw new InvalidOperationException("Fulcrum returned an item without a part number.");
            var notes = new List<string> { Text(item, "internalNotes") };
            var operations = new List<FulcrumGeneratedOperationDto>();
            var materials = new List<FulcrumGeneratedMaterialDto>();
            var processes = new List<FulcrumGeneratedProcessDto>();
            var children = new List<FulcrumGeneratedAssemblyDto>();
            var path = $"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}/routing";
            var routing = await client.ListAsync(path + "/operations/list", true, token);
            foreach (var operation in routing.OrderBy(x => Number(x, "order")))
            {
                var name = Text(operation, "name");
                var instructions = Text(operation, "instructions");
                if (Boolean(operation, "isOutsideProcessing"))
                {
                    var outside = Field(operation, "outsideProcessingOperation");
                    var cost = Field(outside, "outsideProcessingCost");
                    var kind = Text(cost, "costOption");
                    processes.Add(new(Text(operation, "id"), name,
                        kind == "perUnit" ? NullableNumber(cost, "perUnitCost") : null,
                        kind == "fixed" ? NullableNumber(cost, "fixedCost") : null,
                        $"{instructions} Lead days: {Number(operation, "leadDays")}.".Trim()));
                    continue;
                }
                var normal = Field(operation, "operation");
                var setup = Time(Field(normal, "setupTime"));
                var labor = Time(Field(normal, "laborTime"));
                var machine = Time(Field(normal, "machineTime"));
                if (machine.Fixed != 0 || machine.PerUnit != 0)
                    instructions = $"{instructions} Machine time: {machine.Fixed:0.####} min/lot; {machine.PerUnit:0.####} min/unit (review machine rate; not added to labor).".Trim();
                var mapping = catalog.Rules.FirstOrDefault(rule => rule.IsActive &&
                    EstimatingOperationNames.Normalize(rule.FulcrumOperation) == EstimatingOperationNames.Normalize(name)
                    && catalog.RateReferences.Any(reference => reference.Key == rule.RateReferenceKey));
                if (mapping is null) warnings.Add($"{number}: '{name}' has no active Operation Rule; its Fulcrum title and times were preserved. Select a rate before finalizing.");
                operations.Add(new(Text(operation, "id"), (int)Number(operation, "order"), name,
                    mapping?.EstimatingOperation ?? name, mapping?.RateReferenceKey,
                    setup.Fixed + labor.Fixed, setup.PerUnit + labor.PerUnit, machine.PerUnit, instructions));
            }
            var inputs = await client.ListAsync(path + "/input-items/list", true, token);
            foreach (var input in inputs)
            {
                var childId = Text(input, "itemId");
                var child = await ItemAsync(childId, token);
                var amount = Number(input, "valueTypeUnits");
                if (amount <= 0) throw new InvalidOperationException($"{number}: BOM component quantity must be positive.");
                var kind = Text(input, "valueType");
                var units = kind switch
                {
                    "requires" => amount,
                    "creates" => 1 / amount,
                    _ => throw new InvalidOperationException($"{number}: unsupported BOM quantity basis '{kind}'.")
                };
                var origin = Text(child, "itemOrigin");
                if (origin is "make" or "makeOrBuy")
                {
                    if (origin == "makeOrBuy") warnings.Add($"{Text(child, "number")}: Make or Buy was imported as Make. Confirm the sourcing decision.");
                    children.Add(await AssemblyAsync(childId, units, new HashSet<string>(ancestors), token));
                }
                else
                {
                    var vendors = Array(child, "vendorDetails");
                    var vendor = vendors.FirstOrDefault(x => Boolean(x, "isPrimary"));
                    decimal? cost = NullableNumber(vendor, "price");
                    var vendorUnits = NullableNumber(vendor, "unitQuantity");
                    var inventoryUnits = NullableNumber(vendor, "inventoryUnitQuantity");
                    if (vendorUnits > 0 && inventoryUnits > 0) cost *= vendorUnits / inventoryUnits;
                    else if (cost.HasValue && Text(vendor, "unitOfMeasureName") is { Length: > 0 } vendorUom
                        && !string.Equals(vendorUom, Text(child, "unitOfMeasureName"), StringComparison.OrdinalIgnoreCase)) cost = null;
                    if (origin == "customerSupplied") cost = 0;
                    if (cost is null) warnings.Add($"{number}: review purchased cost for {Text(child, "number")} (no unambiguous primary-vendor unit cost).");
                    materials.Add(new(Text(input, "id"), Text(child, "number"), Text(child, "description"), units,
                        Text(child, "unitOfMeasureName"), cost, Text(child, "internalNotes")));
                }
            }
            var rawMaterials = await client.ListAsync(path + "/input-materials/list", true, token);
            foreach (var material in rawMaterials)
            {
                var nestings = Array(material, "nestings");
                var nesting = nestings.FirstOrDefault(x => Boolean(x, "useForEstimatedCosting"));
                var shape = Field(material, "materialShape");
                var detail = $"{Text(material, "materialName")}: {Text(shape, "specification")} {Text(shape, "dimension")}; costing {Text(material, "costing")}; nesting {Number(nesting, "d2")} x {Number(nesting, "d3")} produces {Number(nesting, "produces")}.";
                notes.Add(detail);
                warnings.Add($"{number}: review raw material '{Text(material, "materialName")}' against the purchased BOM rows; nesting and costing details are in notes, and unconfirmed raw-material pricing is not included in totals.");
                // A selected material also appears as an input item. Do not charge it twice.
                if (!inputs.Any(x => Boolean(x, "isMaterialLine")))
                {
                    materials.Add(new(Text(material, "id"), "", Text(material, "materialName"), 0, "", null, detail));
                    warnings.Add($"{number}: raw material '{Text(material, "materialName")}' needs purchasing units and cost entered; nesting details were retained in notes.");
                }
            }
            return new(id, number, Text(Field(item, "revision"), "revision"), Text(item, "description"),
                Number(inventory, id), string.Join("\n", notes.Where(x => !string.IsNullOrWhiteSpace(x))), usage,
                operations, materials, processes, children);
        }
    }

    internal static (decimal Fixed, decimal PerUnit) Time(JsonElement value)
    {
        var amount = Number(value, "time");
        if (amount < 0) throw new InvalidOperationException("Fulcrum routing has a negative time.");
        if (amount == 0) return (0, 0);
        return Text(value, "option") switch
        {
            "fixedSeconds" => (amount / 60, 0),
            "fixedMinutes" => (amount, 0),
            "fixedHours" => (amount * 60, 0),
            "fixedDays" => (amount * 1440, 0),
            "secondsPerUnit" => (0, amount / 60),
            "minutesPerUnit" => (0, amount),
            "hoursPerUnit" => (0, amount * 60),
            "daysPerUnit" => (0, amount * 1440),
            "unitsPerHour" => (0, 60 / amount),
            var option => throw new InvalidOperationException($"Unsupported Fulcrum routing time unit '{option}'.")
        };
    }
    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) ? field : default;
    private static string Text(JsonElement value, string name) => Field(value, name) is var field
        && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static decimal? NullableNumber(JsonElement value, string name) => Field(value, name) is var field
        && field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out var number) ? number : null;
    private static decimal Number(JsonElement value, string name) => NullableNumber(value, name) ?? 0;
    private static bool Boolean(JsonElement value, string name) => Field(value, name).ValueKind == JsonValueKind.True;
    private static IReadOnlyList<JsonElement> Array(JsonElement value, string name) => Field(value, name) is var field
        && field.ValueKind == JsonValueKind.Array ? field.EnumerateArray().ToList() : [];

    private static string CustomText(JsonElement quote, params string[] names)
    {
        var fields = Field(quote, "customFields");
        if (fields.ValueKind != JsonValueKind.Object) return "";
        static string Normalize(string value) => string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));
        var keys = names.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        foreach (var property in fields.EnumerateObject())
            if (keys.Contains(Normalize(property.Name))) return ScalarText(property.Value);
        return "";
    }

    private static string ScalarText(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString()?.Trim() ?? "";
        if (value.ValueKind == JsonValueKind.Array) return string.Join(", ", value.EnumerateArray().Select(ScalarText).Where(x => x.Length > 0));
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "value", "displayValue", "name" })
                if (value.TryGetProperty(key, out var nested)) return ScalarText(nested);
        return value.GetRawText();
    }
}

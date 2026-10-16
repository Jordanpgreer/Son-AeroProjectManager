using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using static EstimatingDashboard.Api.Services.FulcrumMaterialRequirementsPolicy;

namespace EstimatingDashboard.Api.Services;

internal interface IFulcrumMaterialRequirementsReader
{
    Task<MaterialRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null);
}

internal sealed class FulcrumMaterialRequirementsService(FulcrumQuoteGenerationClient client, TimeProvider clock)
    : IFulcrumMaterialRequirementsReader
{
    public async Task<MaterialRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides = null)
    {
        try { return await InspectCoreAsync(historyId, quoteNumber, sourceId, cancellationToken, quantityOverrides); }
        catch (OverflowException error)
        {
            throw new InvalidOperationException(
                "The material quantities exceed the supported calculation range. Verify the quote and BOM quantities in Fulcrum.", error);
        }
    }

    private async Task<MaterialRequirementsDto> InspectCoreAsync(int historyId, int quoteNumber, string sourceId,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, IReadOnlyList<decimal>>? quantityOverrides)
    {
        await client.InitializeAsync(cancellationToken, fresh: true);
        var quoteId = FulcrumQuoteGenerationClient.Identifier(sourceId);
        var quote = await client.GetAsync($"api/quotes/{quoteId}", cancellationToken);
        if (Number(quote, "number") != quoteNumber)
            throw new InvalidOperationException("The Fulcrum quote no longer matches this dashboard record. Refresh your quotes.");
        var lines = await client.ListAsync($"api/quotes/{quoteId}/part-line-items/list", false, cancellationToken);
        if (lines.Count == 0) throw new InvalidOperationException("This Fulcrum quote has no part lines to inspect.");
        if (lines.Count > 200) throw new InvalidOperationException("The quote exceeds the safe material inspection size.");
        if (quantityOverrides is not null && quantityOverrides.Any(pair =>
                pair.Value is null || pair.Value.Count is < 1 or > 2 || pair.Value.Any(quantity => quantity <= 0)))
            throw new InvalidOperationException("Quantity corrections must contain one or two positive quantities.");
        var currentItemIds = lines.Select(line => Text(line, "itemId")).ToHashSet(StringComparer.Ordinal);
        var hasStaleOverrides = quantityOverrides?.Keys.Any(id => !currentItemIds.Contains(id)) == true;
        var context = new Inspection(client, clock.GetUtcNow());
        var parts = new List<MaterialQuotePartDto>();
        foreach (var group in lines.GroupBy(line => Text(line, "itemId"), StringComparer.Ordinal))
        {
            var item = await context.ItemAsync(group.Key, cancellationToken);
            if (group.Any(line => Number(line, "quantity") is not > 0))
                throw new InvalidOperationException($"{Text(item, "number")}: every quote part line needs a positive quantity.");
            var quantities = group.Select(line => Number(line, "quantity")!.Value).Distinct().Order().ToArray();
            var overridden = quantityOverrides?.ContainsKey(group.Key) == true;
            if (overridden) quantities = quantityOverrides![group.Key].Distinct().Order().ToArray();
            var revision = Field(item, "revision");
            parts.Add(new(Text(group.First(), "id"), group.Key, Text(item, "number"),
                revision.ValueKind == JsonValueKind.String ? revision.GetString() : Text(revision, "revision"), quantities, overridden));
            await context.TraverseAsync(group.Key, Text(item, "number"), "", 0, 1, quantities[0], quantities[^1],
                quantities.Length > 1, [], [], cancellationToken);
        }
        var materials = await context.CompleteAsync(cancellationToken);
        var warnings = new List<string>();
        if (materials.Count > 0 && parts.Any(part => !part.QuantitiesOverridden))
            warnings.Add("Fulcrum's public API exposes quote line quantities, but not separate Pricing-panel quantity breaks. Confirm the displayed part quantities before opening a vendor draft.");
        if (hasStaleOverrides)
        {
            const string issue = "Saved quantities for removed parts were ignored; reset or confirm the current quoted quantities before opening a vendor draft.";
            warnings.Add(issue);
            materials = materials.Select(material => material with
                { Status = "review-required", Issues = material.Issues.Append(issue).ToArray() }).ToArray();
        }
        return new(historyId, quoteNumber, context.CheckedAt, parts, materials, warnings);
    }

    private sealed class Inspection(FulcrumQuoteGenerationClient client, DateTimeOffset checkedAt)
    {
        public DateTimeOffset CheckedAt { get; } = checkedAt;
        private readonly Dictionary<string, JsonElement> items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<JsonElement>> routing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> materials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Name, IReadOnlyList<string> Emails)> vendors = new(StringComparer.Ordinal);
        private int nodes;

        public async Task<JsonElement> ItemAsync(string id, CancellationToken token)
        {
            if (!items.TryGetValue(id, out var item))
            {
                item = await client.GetAsync($"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}", token);
                if (Text(item, "number").Length == 0 || Text(item, "number").Any(char.IsControl))
                    throw new InvalidOperationException("Fulcrum returned an item with a missing or invalid part number.");
                items[id] = item;
            }
            return item;
        }

        public async Task TraverseAsync(string id, string topPart, string parentPart, decimal? fixedQuantity,
            decimal? perUnit, decimal? minimum, decimal? maximum, bool hasAlternates,
            HashSet<string> ancestors, List<string> inheritedIssues, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!ancestors.Add(id))
                throw new InvalidOperationException("Fulcrum contains a circular BOM. Material requirements could not be verified.");
            if (++nodes > 500 || ancestors.Count > 32)
                throw new InvalidOperationException("The BOM exceeds the safe material inspection size.");
            var item = await ItemAsync(id, token);
            var number = Text(item, "number");
            var origin = Text(item, "itemOrigin");
            if (origin == "customerSupplied") return;
            var issues = new List<string>(inheritedIssues);
            if (origin == "makeOrBuy") issues.Add($"{number} is Make or Buy; confirm the sourcing decision before requesting material pricing.");
            if (origin is "make" or "makeOrBuy" or "kit")
            {
                if (!routing.TryGetValue(id, out var inputs))
                    routing[id] = inputs = await client.ListAsync(
                        $"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}/routing/input-items/list", true, token);
                foreach (var input in inputs)
                {
                    var usage = Usage(input);
                    var childIssues = new List<string>(issues);
                    if (!usage.Fixed.HasValue || !usage.PerUnit.HasValue)
                        childIssues.Add($"{number}: a BOM quantity is missing, invalid, or uses an unsupported basis.");
                    var childMinimum = Required(usage.Fixed, usage.PerUnit, minimum);
                    var childMaximum = Required(usage.Fixed, usage.PerUnit, maximum);
                    // A zero-use BOM row contributes no procurement demand.
                    if (childMaximum == 0) continue;
                    await TraverseAsync(Text(input, "itemId"), topPart, number, usage.Fixed, usage.PerUnit,
                        childMinimum, childMaximum, hasAlternates, new(ancestors, StringComparer.Ordinal), childIssues, token);
                }
                return;
            }
            if (origin != "buy") issues.Add($"{number}: the item's purchasing origin is not confirmed as Buy.");
            if (!materials.TryGetValue(id, out var material)) materials[id] = material = new(id, item);
            material.Minimum = material.Minimum.HasValue && minimum.HasValue ? checked(material.Minimum + minimum) : null;
            material.Maximum = material.Maximum.HasValue && maximum.HasValue ? checked(material.Maximum + maximum) : null;
            material.HasAlternates |= hasAlternates;
            material.Issues.AddRange(issues);
            material.Sources.Add(new(topPart, parentPart.Length == 0 ? topPart : parentPart,
                fixedQuantity, perUnit, minimum, maximum));
        }

        public async Task<IReadOnlyList<MaterialRequirementDto>> CompleteAsync(CancellationToken token)
        {
            JsonElement inventory = default;
            try { inventory = await client.GetAsync("api/inventory/availableByItem", token); }
            catch (Exception error) when (Recoverable(error, token)) { }
            var validInventory = inventory.ValueKind == JsonValueKind.Object && inventory.EnumerateObject()
                .All(property => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out _));
            var results = new List<MaterialRequirementDto>();
            foreach (var material in materials.Values.OrderBy(row => Text(row.Item, "number"), StringComparer.OrdinalIgnoreCase))
            {
                // This endpoint explicitly omits zero-stock IDs; no on-hand endpoint is used.
                decimal? available = validInventory ? Number(inventory, material.Id) ?? 0 : null;
                if (!validInventory) material.Issues.Add("Available inventory could not be verified. Refresh before requesting pricing.");
                var quantities = QuoteQuantities(material.Minimum, material.Maximum, material.HasAlternates);
                if (quantities.Count == 0) material.Issues.Add("The full material requirement could not be calculated.");
                var status = material.Issues.Count > 0 ? "review-required"
                    : available >= material.Maximum ? "stock-covered" : "needs-rfq";
                MaterialPurchaseOrderDto? purchase = null;
                string? vendorId = null;
                string? vendorName = null;
                IReadOnlyList<string> emails = [];
                if (status != "stock-covered")
                {
                    try
                    {
                        var history = await client.GetAsync(
                            $"api/items/{FulcrumQuoteGenerationClient.Identifier(material.Id)}/purchase-history?months=3", token);
                        if (!HasCompleteHistory(history))
                            material.Issues.Add("Recent purchase-order history is incomplete. Verify it in Fulcrum.");
                        else purchase = RecentPurchase(history, CheckedAt);
                    }
                    catch (Exception error) when (Recoverable(error, token))
                    { material.Issues.Add("Recent purchase-order history could not be verified. Refresh before requesting pricing."); }
                    if (material.Issues.Count == 0 && purchase is not null) status = "recent-po";
                    else
                    {
                        try
                        {
                            var links = await client.ListAsync($"api/items/{FulcrumQuoteGenerationClient.Identifier(material.Id)}/vendors/list", false, token);
                            var primary = links.Where(link => Boolean(link, "isPrimary")).ToArray();
                            if (primary.Length != 1 || Text(primary[0], "vendorId").Length == 0)
                                material.Issues.Add("Exactly one primary vendor must be configured for this item in Fulcrum.");
                            else
                            {
                                vendorId = Text(primary[0], "vendorId");
                                (vendorName, emails) = await VendorAsync(vendorId, token);
                                if (string.IsNullOrWhiteSpace(vendorName) || vendorName.Any(char.IsControl))
                                    material.Issues.Add("The primary vendor's name is missing or invalid in Fulcrum.");
                                if (emails.Count == 0) material.Issues.Add("The primary vendor has no usable contact email addresses.");
                            }
                        }
                        catch (Exception error) when (Recoverable(error, token))
                        { material.Issues.Add("The primary vendor or contact emails could not be verified. Refresh before requesting pricing."); }
                        var description = Text(material.Item, "description");
                        if (description.Length == 0 || description.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                            material.Issues.Add("The item's Details description is missing or contains invalid characters in Fulcrum.");
                        var unitOfMeasure = Text(material.Item, "unitOfMeasureName");
                        if (unitOfMeasure.Length == 0 || unitOfMeasure.Any(char.IsControl))
                            material.Issues.Add("The material's inventory unit of measure is missing or invalid in Fulcrum.");
                    }
                }
                if (material.Issues.Count > 0) status = "review-required";
                results.Add(new(material.Id, Text(material.Item, "number"), Text(material.Item, "description"),
                    Text(material.Item, "unitOfMeasureName"), material.Minimum, material.Maximum, available,
                    quantities, status, vendorId, vendorName, emails, purchase, material.Sources,
                    material.Issues.Distinct().ToArray()));
            }
            return results;
        }

        private async Task<(string Name, IReadOnlyList<string> Emails)> VendorAsync(string id, CancellationToken token)
        {
            if (vendors.TryGetValue(id, out var cached)) return cached;
            var path = $"api/vendors/{FulcrumQuoteGenerationClient.Identifier(id)}";
            var vendor = await client.GetAsync(path, token);
            if (Field(vendor, "active").ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("The primary vendor is inactive.");
            var contacts = await client.ListAsync(path + "/contacts/list", false, token);
            var emails = contacts.Select(contact => Text(contact, "email")).Where(ValidEmail)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return vendors[id] = (Text(vendor, "name"), emails);
        }

        private static bool Recoverable(Exception error, CancellationToken token) => !token.IsCancellationRequested
            && error is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException;

        private sealed class Material(string id, JsonElement item)
        {
            public string Id { get; } = id;
            public JsonElement Item { get; } = item;
            public decimal? Minimum { get; set; } = 0;
            public decimal? Maximum { get; set; } = 0;
            public bool HasAlternates { get; set; }
            public List<MaterialRequirementSourceDto> Sources { get; } = [];
            public List<string> Issues { get; } = [];
        }
    }
}

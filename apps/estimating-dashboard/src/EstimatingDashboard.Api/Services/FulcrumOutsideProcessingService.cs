using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using SonAero.Platform.Integrations;
using static EstimatingDashboard.Api.Services.FulcrumMaterialRequirementsPolicy;

namespace EstimatingDashboard.Api.Services;

internal interface IFulcrumOutsideProcessingReader
{
    Task<OutsideProcessingRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct);
}

internal sealed class FulcrumOutsideProcessingService(FulcrumQuoteGenerationClient client, TimeProvider clock)
    : IFulcrumOutsideProcessingReader
{
    public async Task<OutsideProcessingRequirementsDto> InspectAsync(int historyId, int quoteNumber, string sourceId,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct)
    {
        try { return await InspectCoreAsync(historyId, quoteNumber, sourceId, quantities, ct); }
        catch (OverflowException error)
        {
            throw new InvalidOperationException(
                "The OP quantities exceed the supported calculation range. Verify the quote and BOM quantities in Fulcrum.", error);
        }
    }

    private async Task<OutsideProcessingRequirementsDto> InspectCoreAsync(int historyId, int quoteNumber, string sourceId,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> quantities, CancellationToken ct)
    {
        await client.InitializeAsync(ct, fresh: true);
        var quoteId = FulcrumQuoteGenerationClient.Identifier(sourceId);
        var quote = await client.GetAsync($"api/quotes/{quoteId}", ct);
        if (Number(quote, "number") != quoteNumber)
            throw new InvalidOperationException("The Fulcrum quote no longer matches this Arda record. Refresh your quotes.");
        var lines = await client.ListAsync($"api/quotes/{quoteId}/part-line-items/list", false, ct);
        if (lines.Count is < 1 or > 200)
            throw new InvalidOperationException("The quote has no part lines or exceeds the safe OP inspection size.");
        var inspection = new Inspection(client);
        var warnings = new List<string>();
        var quantityWarningsByRoot = new Dictionary<string, string>(StringComparer.Ordinal);
        var partCount = 0;
        var industriosCount = 0;
        foreach (var group in lines.GroupBy(line => Text(line, "itemId"), StringComparer.Ordinal))
        {
            if (group.Key.Length == 0 || group.Key.Length > 100)
                throw new InvalidOperationException("A Fulcrum quote part has no valid item identifier.");
            var item = await inspection.ItemAsync(group.Key, ct);
            partCount++;
            var numbers = group.Select(line => Number(line, "quantity")).ToArray();
            if (numbers.Any(number => number is not > 0))
                throw new InvalidOperationException($"{Text(item, "number")}: each quote part needs a positive quantity.");
            var planning = await FulcrumPurchasingPlanning.InspectAsync(client, quoteId, group.Key, item,
                group.Select(line => Text(line, "id")), ct);
            if (planning.IsIndustrios) { industriosCount++; continue; }
            var (selected, confirmed) = QuoteTiers(group, numbers.Select(quantity => quantity!.Value),
                quantities.GetValueOrDefault(group.Key));
            if (group.Count() > 1)
                warnings.Add($"{Text(item, "number")}: multiple quote lines use this part. Confirm that their OP routing is identical before sending.");
            var lineId = Text(group.First(), "id");
            if (lineId.Length == 0) throw new InvalidOperationException("A Fulcrum quote part line has no routing identifier.");
            var hasOperations = await inspection.TraverseAsync(group.Key, group.Key, selected,
                planning.Routes.FirstOrDefault(), [], ct);
            if (hasOperations && !confirmed)
                quantityWarningsByRoot.Add(group.Key, $"{Text(item, "number")}: Fulcrum did not provide a complete list of Pricing-panel breaks. Confirm the quoted quantities for this part.");
        }
        var operations = await inspection.CompleteAsync(quantityWarningsByRoot, ct);
        warnings.AddRange(inspection.Warnings);
        return new(historyId, quoteNumber, clock.GetUtcNow(), operations, warnings.Distinct().ToArray(),
            inspection.HasUnknownPlanning ? PurchasingPlanningSources.Unknown
                : FulcrumPurchasingPlanning.Source(partCount, industriosCount),
            quantityWarningsByRoot.Values.Distinct().ToArray());
    }

    private static (decimal?[] Quantities, bool Confirmed) QuoteTiers(IEnumerable<JsonElement> lines,
        IEnumerable<decimal> lineQuantities, IReadOnlyList<decimal>? saved)
    {
        decimal[]? confirmed = null;
        if (saved is not null)
        {
            if (saved.Count is < 1 or > FulcrumQuoteQuantityTiers.MaximumCount || saved.Any(quantity => quantity <= 0))
                throw new InvalidOperationException($"Confirm between 1 and {FulcrumQuoteQuantityTiers.MaximumCount} positive quoted quantity tiers before preparing OP drafts.");
            confirmed = saved.Distinct().Order().ToArray();
            if (confirmed.Length != 2)
                return (confirmed.Select(quantity => (decimal?)quantity).ToArray(), true);
        }
        var quoted = FulcrumQuoteQuantityTiers.Read(lines, lineQuantities);
        var values = confirmed is null ? quoted.Quantities : FulcrumQuoteQuantityTiers.ApplyConfirmation(quoted, confirmed);
        return (values.Select(quantity => (decimal?)quantity).ToArray(), confirmed is not null || quoted.Complete);
    }

    private sealed class Inspection(FulcrumQuoteGenerationClient client)
    {
        private readonly Dictionary<string, JsonElement> items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Name, IReadOnlyList<string> Emails)> vendors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
        private int nodes;
        public List<string> Warnings { get; } = [];
        public bool HasUnknownPlanning { get; private set; }

        public async Task<JsonElement> ItemAsync(string id, CancellationToken ct)
        {
            if (!items.TryGetValue(id, out var item))
                items[id] = item = await client.GetAsync($"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}", ct);
            if (Text(item, "number").Length == 0 || Text(item, "number").Any(char.IsControl))
                throw new InvalidOperationException("Fulcrum returned an item without a usable part number.");
            return item;
        }

        public async Task<bool> TraverseAsync(string rootId, string itemId, decimal?[] quantities,
            FulcrumQuoteRouting? routing, HashSet<string> ancestors, CancellationToken ct)
        {
            if (quantities.All(quantity => quantity == 0)) return false;
            if (!ancestors.Add(itemId)) throw new InvalidOperationException("Fulcrum has a circular BOM. OP requirements cannot be verified.");
            if (++nodes > 500 || ancestors.Count > 32)
                throw new InvalidOperationException("The BOM exceeds the safe OP inspection size.");
            var item = await ItemAsync(itemId, ct);
            var number = Text(item, "number");
            var origin = Text(item, "itemOrigin");
            if (origin is "make" or "makeOrBuy" or "kit" && routing?.Contains(itemId) != true)
            {
                HasUnknownPlanning = true;
                Warnings.Add($"{number}: Fulcrum quote routing is missing; outside processing requirements could not be verified.");
                return false;
            }
            var hasOperations = false;
            var operations = routing is null ? [] : await routing.OperationsAsync(itemId, ct);
            foreach (var operation in operations)
            {
                var name = Text(operation, "name");
                if (!Boolean(operation, "isOutsideProcessing"))
                {
                    if (name.StartsWith("OP", StringComparison.OrdinalIgnoreCase)
                        && (name.Length == 2 || !char.IsLetterOrDigit(name[2])))
                        Warnings.Add($"{number}: {name} is named OP but is not flagged Outside Processing in Fulcrum.");
                    continue;
                }
                var operationId = Text(operation, "id");
                if (operationId.Length == 0) throw new InvalidOperationException($"{number}: an outside processing operation has no identifier.");
                var key = VendorQuoteService.Hash(itemId + "\n" + operationId);
                if (!candidates.TryGetValue(key, out var candidate))
                    candidates[key] = candidate = new(key, itemId, operationId, number, operation);
                candidate.Add(rootId, quantities);
                hasOperations = true;
            }
            if (origin is not ("make" or "makeOrBuy" or "kit")) return hasOperations;
            var inputs = await routing!.InputsAsync(itemId, ct);
            foreach (var input in inputs)
            {
                var childId = Text(input, "itemId");
                if (childId.Length == 0) continue;
                var usage = Usage(input);
                if (usage is (0, 0)) continue;
                var child = await ItemAsync(childId, ct);
                if (Text(child, "itemOrigin") is not ("make" or "makeOrBuy" or "kit")) continue;
                var childQuantities = quantities.Select(quantity => Required(usage.Fixed, usage.PerUnit, quantity)).ToArray();
                hasOperations |= await TraverseAsync(rootId, childId, childQuantities,
                    routing, new(ancestors, StringComparer.Ordinal), ct);
            }
            return hasOperations;
        }

        public async Task<IReadOnlyList<OutsideProcessingOperationDto>> CompleteAsync(
            IReadOnlyDictionary<string, string> quantityWarningsByRoot, CancellationToken ct)
        {
            var result = new List<OutsideProcessingOperationDto>();
            foreach (var candidate in candidates.Values.OrderBy(x => x.PartNumber).ThenBy(x => x.Name))
            {
                var issues = candidate.RootIds.Where(quantityWarningsByRoot.ContainsKey)
                    .Select(root => quantityWarningsByRoot[root]).Distinct().ToList();
                var vendorId = Text(Field(candidate.Operation, "outsideProcessingOperation"), "selectedVendorId");
                string? vendorName = null;
                IReadOnlyList<string> emails = [];
                if (candidate.Name.Length == 0 || candidate.Name.Any(char.IsControl))
                    issues.Add("The outside processing operation has no usable name.");
                var values = candidate.QuoteQuantities(issues);
                if (vendorId.Length == 0)
                    issues.Add("Select an outside processing vendor for this routing operation in Fulcrum.");
                else
                {
                    try
                    {
                        (vendorName, emails) = await VendorAsync(vendorId, ct);
                        if (string.IsNullOrWhiteSpace(vendorName) || vendorName.Any(char.IsControl))
                            issues.Add("The selected vendor has no usable name in Fulcrum.");
                        if (emails.Count == 0)
                            issues.Add("The selected vendor has no usable contact email in Fulcrum.");
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested
                        && error is not FulcrumCoordinationUnavailableException
                        && error is not FulcrumReadDeferredException and not FulcrumReadFailureException { IsDeferred: true }
                        && error is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
                    { issues.Add("The selected OP vendor or contacts could not be verified in Fulcrum."); }
                }
                var instructions = Text(candidate.Operation, "instructions");
                if (instructions.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                    issues.Add("The OP instructions contain invalid characters.");
                var unitOfMeasure = Text(items[candidate.ItemId], "unitOfMeasureName");
                result.Add(new(candidate.Key, candidate.ItemId, candidate.OperationId, candidate.PartNumber,
                    candidate.Name, instructions.Length == 0 ? null : instructions,
                    values.Length == 0 ? null : values[0], values.Length == 0 ? null : values[^1],
                    values, issues.Count == 0 ? "needs-rfq" : "review-required", vendorId.Length == 0 ? null : vendorId,
                    vendorName, emails, issues,
                    UnitOfMeasure: unitOfMeasure.Length > 0 && !unitOfMeasure.Any(char.IsControl) ? unitOfMeasure : null));
            }
            return result;
        }

        private async Task<(string Name, IReadOnlyList<string> Emails)> VendorAsync(string id, CancellationToken ct)
        {
            if (vendors.TryGetValue(id, out var vendor)) return vendor;
            var path = $"api/vendors/{FulcrumQuoteGenerationClient.Identifier(id)}";
            var details = await client.GetAsync(path, ct);
            if (Field(details, "active").ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("The outside processing vendor is inactive.");
            var contacts = await client.ListAsync(path + "/contacts/list", false, ct);
            return vendors[id] = (Text(details, "name"), contacts.Select(x => Text(x, "email"))
                .Where(ValidEmail).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private sealed class Candidate(string key, string itemId, string operationId, string partNumber, JsonElement operation)
        {
            private readonly Dictionary<string, decimal?[]> quantitiesByRoot = new(StringComparer.Ordinal);

            public string Key { get; } = key;
            public string ItemId { get; } = itemId;
            public string OperationId { get; } = operationId;
            public string PartNumber { get; } = partNumber;
            public JsonElement Operation { get; } = operation;
            public string Name => Text(Operation, "name");
            public IEnumerable<string> RootIds => quantitiesByRoot.Keys;
            public void Add(string rootId, decimal?[] quantities)
            {
                if (!quantitiesByRoot.TryGetValue(rootId, out var previous))
                {
                    quantitiesByRoot[rootId] = (decimal?[])quantities.Clone();
                    return;
                }
                for (var tier = 0; tier < previous.Length; tier++)
                    previous[tier] += quantities[tier];
            }

            public decimal[] QuoteQuantities(List<string> issues)
            {
                var contributors = quantitiesByRoot.Values;
                var tierCount = contributors.Max(tiers => tiers.Length);
                if (contributors.Any(tiers => tiers.Length != 1 && tiers.Length != tierCount))
                {
                    issues.Add("Quoted parts sharing this OP have different numbers of quantity tiers. Verify how their tiers pair before preparing a draft.");
                    return [];
                }
                // Keep each root's tiers aligned. A single-tier root contributes the same units to each tier.
                var totals = new decimal[tierCount];
                foreach (var tiers in contributors)
                {
                    for (var tier = 0; tier < totals.Length; tier++)
                    {
                        if (tiers[tiers.Length == 1 ? 0 : tier] is not { } quantity)
                        {
                            issues.Add("The OP quantity could not be calculated from the quote and BOM.");
                            return [];
                        }
                        totals[tier] += quantity;
                    }
                }
                if (totals.All(total => total > 0)) return totals.Distinct().Order().ToArray();
                issues.Add("The OP quantity could not be calculated from the quote and BOM.");
                return [];
            }
        }
    }
}

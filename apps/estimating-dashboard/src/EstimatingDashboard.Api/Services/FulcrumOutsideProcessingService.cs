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
        foreach (var group in lines.GroupBy(line => Text(line, "itemId"), StringComparer.Ordinal))
        {
            if (group.Key.Length == 0 || group.Key.Length > 100)
                throw new InvalidOperationException("A Fulcrum quote part has no valid item identifier.");
            var item = await inspection.ItemAsync(group.Key, ct);
            var numbers = group.Select(line => Number(line, "quantity")).ToArray();
            if (numbers.Any(number => number is not > 0))
                throw new InvalidOperationException($"{Text(item, "number")}: each quote part needs a positive quantity.");
            var selected = quantities.TryGetValue(group.Key, out var overrideValues) ? overrideValues :
                numbers.Select(number => number!.Value).Distinct().Order().ToArray();
            if (selected.Count is < 1 or > 2 || selected.Any(number => number <= 0))
                throw new InvalidOperationException("Confirm the quoted quantities before preparing OP drafts.");
            if (!quantities.ContainsKey(group.Key))
                warnings.Add($"{Text(item, "number")}: Fulcrum's API exposes line quantities but not Pricing-panel breaks. Confirm the quoted quantity before sending an OP request.");
            if (group.Count() > 1)
                warnings.Add($"{Text(item, "number")}: multiple quote lines use this part. Confirm that their OP routing is identical before sending.");
            var lineId = Text(group.First(), "id");
            if (lineId.Length == 0) throw new InvalidOperationException("A Fulcrum quote part line has no routing identifier.");
            await inspection.LoadQuoteRoutingAsync(quoteId, lineId, group.Key, ct);
            await inspection.TraverseAsync(group.Key, selected.Min(), selected.Max(), [], ct);
        }
        var operations = await inspection.CompleteAsync(ct);
        warnings.AddRange(inspection.Warnings);
        return new(historyId, quoteNumber, clock.GetUtcNow(), operations, warnings.Distinct().ToArray());
    }

    private sealed class Inspection(FulcrumQuoteGenerationClient client)
    {
        private readonly Dictionary<string, JsonElement> items = new(StringComparer.Ordinal);
        private FulcrumQuoteRouting routing = null!;
        private readonly Dictionary<string, (string Name, IReadOnlyList<string> Emails)> vendors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
        private int nodes;
        public List<string> Warnings { get; } = [];

        public async Task<JsonElement> ItemAsync(string id, CancellationToken ct)
        {
            if (!items.TryGetValue(id, out var item))
                items[id] = item = await client.GetAsync($"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}", ct);
            if (Text(item, "number").Length == 0 || Text(item, "number").Any(char.IsControl))
                throw new InvalidOperationException("Fulcrum returned an item without a usable part number.");
            return item;
        }

        public async Task LoadQuoteRoutingAsync(string quoteId, string lineId, string rootItemId, CancellationToken ct) =>
            routing = await FulcrumQuoteRouting.LoadAsync(client, quoteId, lineId, rootItemId, ct);

        public async Task TraverseAsync(string itemId, decimal? minimum, decimal? maximum,
            HashSet<string> ancestors, CancellationToken ct)
        {
            if (maximum == 0) return;
            if (!ancestors.Add(itemId)) throw new InvalidOperationException("Fulcrum has a circular BOM. OP requirements cannot be verified.");
            if (++nodes > 500 || ancestors.Count > 32)
                throw new InvalidOperationException("The BOM exceeds the safe OP inspection size.");
            var item = await ItemAsync(itemId, ct);
            var number = Text(item, "number");
            var origin = Text(item, "itemOrigin");
            if (origin is "make" or "makeOrBuy" or "kit" && !routing.Contains(itemId))
            {
                Warnings.Add($"{number}: Fulcrum quote routing is missing; outside processing requirements could not be verified.");
                return;
            }
            var operations = await routing.OperationsAsync(itemId, ct);
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
                candidate.Add(minimum, maximum);
            }
            if (origin is not ("make" or "makeOrBuy" or "kit")) return;
            var inputs = await routing.InputsAsync(itemId, ct);
            foreach (var input in inputs)
            {
                var childId = Text(input, "itemId");
                if (childId.Length == 0) continue;
                var usage = Usage(input);
                if (usage is (0, 0)) continue;
                var child = await ItemAsync(childId, ct);
                if (Text(child, "itemOrigin") is not ("make" or "makeOrBuy" or "kit")) continue;
                await TraverseAsync(childId, Required(usage.Fixed, usage.PerUnit, minimum),
                    Required(usage.Fixed, usage.PerUnit, maximum), new(ancestors, StringComparer.Ordinal), ct);
            }
        }

        public async Task<IReadOnlyList<OutsideProcessingOperationDto>> CompleteAsync(CancellationToken ct)
        {
            var result = new List<OutsideProcessingOperationDto>();
            foreach (var candidate in candidates.Values.OrderBy(x => x.PartNumber).ThenBy(x => x.Name))
            {
                var issues = new List<string>();
                var vendorId = Text(Field(candidate.Operation, "outsideProcessingOperation"), "selectedVendorId");
                string? vendorName = null;
                IReadOnlyList<string> emails = [];
                if (candidate.Name.Length == 0 || candidate.Name.Any(char.IsControl))
                    issues.Add("The outside processing operation has no usable name.");
                if (candidate.Minimum is not > 0 || candidate.Maximum is not > 0)
                    issues.Add("The OP quantity could not be calculated from the quote and BOM.");
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
                var values = candidate.Minimum is > 0 && candidate.Maximum is > 0
                    ? new[] { candidate.Minimum.Value, candidate.Maximum.Value }.Distinct().Order().ToArray() : [];
                var unitOfMeasure = Text(items[candidate.ItemId], "unitOfMeasureName");
                result.Add(new(candidate.Key, candidate.ItemId, candidate.OperationId, candidate.PartNumber,
                    candidate.Name, instructions.Length == 0 ? null : instructions, candidate.Minimum, candidate.Maximum,
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
            public string Key { get; } = key;
            public string ItemId { get; } = itemId;
            public string OperationId { get; } = operationId;
            public string PartNumber { get; } = partNumber;
            public JsonElement Operation { get; } = operation;
            public string Name => Text(Operation, "name");
            public decimal? Minimum { get; private set; } = 0;
            public decimal? Maximum { get; private set; } = 0;
            public void Add(decimal? minimum, decimal? maximum)
            {
                Minimum = Minimum.HasValue && minimum.HasValue ? checked(Minimum + minimum) : null;
                Maximum = Maximum.HasValue && maximum.HasValue ? checked(Maximum + maximum) : null;
            }
        }
    }
}

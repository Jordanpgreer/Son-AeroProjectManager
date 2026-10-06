using System.Collections.Concurrent;
using System.Text.Json;

namespace EstimatingDashboard.Api.Services;

internal sealed record FulcrumQuoteItemReference(string ItemId, string PartNumber, string? Revision);

internal sealed record FulcrumQuoteInspection(
    IReadOnlyList<FulcrumQuoteItemReference> Items,
    IReadOnlyList<string> OpOperations,
    int BuyItemCount,
    int MakeItemCount,
    DateTimeOffset InspectedAt);

internal sealed record FulcrumQuoteInspectionBatch(
    IReadOnlyDictionary<int, FulcrumQuoteInspection> Results,
    IReadOnlyList<string> Warnings);

internal sealed class FulcrumQuoteInspectionService(
    FulcrumQuoteGenerationClient client,
    TimeProvider clock,
    ILogger<FulcrumQuoteInspectionService> logger)
{
    private const int MaxNodesPerQuote = 500;

    public async Task<FulcrumQuoteInspectionBatch> InspectAsync(
        IReadOnlyList<FulcrumQuoteSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        await client.InitializeAsync(cancellationToken);
        var results = new ConcurrentDictionary<int, FulcrumQuoteInspection>();
        var warnings = new ConcurrentQueue<string>();
        await Parallel.ForEachAsync(
            snapshots,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (snapshot, token) =>
            {
                try
                {
                    results[snapshot.Quote.Number] = await InspectQuoteAsync(snapshot, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Fulcrum production inspection failed for quote {QuoteNumber}.", snapshot.Quote.Number);
                    warnings.Enqueue($"Quote {snapshot.Quote.Number}: production details could not be refreshed; the prior Arda warning summary was retained.");
                }
            });
        return new(results, warnings.ToList());
    }

    private async Task<FulcrumQuoteInspection> InspectQuoteAsync(
        FulcrumQuoteSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var quoteId = FulcrumQuoteGenerationClient.Identifier(snapshot.Quote.Id);
        var lines = await client.ListAsync($"api/quotes/{quoteId}/part-line-items/list", false, cancellationToken);
        var itemCache = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var items = new List<FulcrumQuoteItemReference>();
        var operations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buyCount = 0;
        var makeCount = 0;
        var nodeCount = 0;

        foreach (var line in lines.OrderBy(line => Number(line, "number")))
        {
            var itemId = Text(line, "itemId");
            if (string.IsNullOrWhiteSpace(itemId))
                throw new InvalidOperationException("A Fulcrum quote part line did not include an item identifier.");
            var item = await ItemAsync(itemId, itemCache, cancellationToken);
            var partNumber = Text(item, "number");
            if (string.IsNullOrWhiteSpace(partNumber))
                throw new InvalidOperationException("A Fulcrum quote item did not include a part number.");
            items.Add(new(itemId, partNumber, Revision(item)));
            await InspectRoutingAsync(itemId, partNumber, new HashSet<string>(StringComparer.Ordinal), itemCache,
                operations, count => buyCount += count, count => makeCount += count,
                () => ++nodeCount, cancellationToken);
        }

        if (nodeCount > MaxNodesPerQuote)
            throw new InvalidOperationException("The Fulcrum quote BOM exceeded the safe inspection size.");

        return new(
            items.GroupBy(item => (item.ItemId, item.Revision)).Select(group => group.First()).ToList(),
            operations.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            buyCount,
            makeCount,
            clock.GetUtcNow());
    }

    private async Task InspectRoutingAsync(
        string itemId,
        string partNumber,
        HashSet<string> ancestors,
        Dictionary<string, JsonElement> itemCache,
        ISet<string> opOperations,
        Action<int> addBuy,
        Action<int> addMake,
        Func<int> incrementNodes,
        CancellationToken cancellationToken)
    {
        if (!ancestors.Add(itemId)) return;
        if (ancestors.Count > 32 || incrementNodes() > MaxNodesPerQuote)
            throw new InvalidOperationException("The Fulcrum quote BOM exceeded the safe inspection size.");

        var identifier = FulcrumQuoteGenerationClient.Identifier(itemId);
        var routingPath = $"api/items/{identifier}/routing";
        var routing = await client.ListAsync(routingPath + "/operations/list", true, cancellationToken);
        foreach (var operation in routing)
        {
            var name = Text(operation, "name");
            if (StartsWithOp(name))
                opOperations.Add($"{partNumber}: {name}");
        }

        var inputs = await client.ListAsync(routingPath + "/input-items/list", true, cancellationToken);
        foreach (var input in inputs)
        {
            var childId = Text(input, "itemId");
            if (string.IsNullOrWhiteSpace(childId)) continue;
            var child = await ItemAsync(childId, itemCache, cancellationToken);
            var origin = Text(child, "itemOrigin");
            if (origin.Equals("make", StringComparison.OrdinalIgnoreCase)
                || origin.Equals("makeOrBuy", StringComparison.OrdinalIgnoreCase))
            {
                addMake(1);
                await InspectRoutingAsync(childId, Text(child, "number"), new HashSet<string>(ancestors, StringComparer.Ordinal),
                    itemCache, opOperations, addBuy, addMake, incrementNodes, cancellationToken);
            }
            else
            {
                addBuy(1);
            }
        }
    }

    private async Task<JsonElement> ItemAsync(
        string id,
        IDictionary<string, JsonElement> cache,
        CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(id, out var item))
        {
            item = await client.GetAsync($"api/items/{FulcrumQuoteGenerationClient.Identifier(id)}", cancellationToken);
            cache[id] = item;
        }
        return item;
    }

    private static bool StartsWithOp(string value) => value.Length >= 2
        && value.StartsWith("OP", StringComparison.OrdinalIgnoreCase)
        && (value.Length == 2 || !char.IsLetterOrDigit(value[2]));

    private static string? Revision(JsonElement item)
    {
        if (!item.TryGetProperty("revision", out var revision)) return null;
        if (revision.ValueKind == JsonValueKind.String) return Clean(revision.GetString());
        return revision.ValueKind == JsonValueKind.Object && revision.TryGetProperty("revision", out var value)
            ? Clean(value.ToString())
            : null;
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : value.ToString().Trim()
            : string.Empty;

    private static decimal Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.TryGetDecimal(out var number) ? number : 0;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

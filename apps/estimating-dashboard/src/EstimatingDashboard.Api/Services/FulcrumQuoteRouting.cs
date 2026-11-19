using System.Net;
using System.Text.Json;
using static EstimatingDashboard.Api.Services.FulcrumMaterialRequirementsPolicy;

namespace EstimatingDashboard.Api.Services;

internal sealed class FulcrumQuoteRouting(FulcrumQuoteGenerationClient client,
    IReadOnlyDictionary<string, string[]> pathsByItem, bool hasIncompleteSummary = false)
{
    public bool HasIncompleteSummary { get; } = hasIncompleteSummary;
    public bool HasRoutes => pathsByItem.Count > 0;
    private readonly Dictionary<(string ItemId, string Resource), IReadOnlyList<JsonElement>> cache = new();

    public static async Task<FulcrumQuoteRouting?> LoadIfAvailableAsync(FulcrumQuoteGenerationClient client,
        string quoteId, string lineId, string rootItemId, CancellationToken ct)
    {
        if (lineId.Length == 0) return null;
        try { return await LoadAsync(client, quoteId, lineId, rootItemId, ct); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public static async Task<FulcrumQuoteRouting> LoadAsync(FulcrumQuoteGenerationClient client,
        string quoteId, string lineId, string rootItemId, CancellationToken ct)
    {
        var path = $"api/quotes/{quoteId}/part-line-items/{FulcrumQuoteGenerationClient.Identifier(lineId)}";
        var summary = await client.GetAsync(path + "/make-summary", ct);
        if (summary.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Fulcrum returned an invalid quote routing summary.");

        var routes = new List<(string ItemId, string Path)>();
        var incomplete = false;
        foreach (var row in summary.EnumerateArray())
        {
            var itemId = Text(row, "itemId");
            if (itemId.Length == 0 && summary.GetArrayLength() == 1) itemId = rootItemId;
            var routingId = Text(row, "routingId");
            if (itemId.Length == 0 || routingId.Length == 0) { incomplete = true; continue; }
            routes.Add((itemId, $"{path}/routing/{FulcrumQuoteGenerationClient.Identifier(routingId)}"));
        }
        return new(client, routes.GroupBy(row => row.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Path).Distinct().ToArray(),
                StringComparer.Ordinal), incomplete);
    }

    public bool Contains(string itemId) => pathsByItem.ContainsKey(itemId);

    public Task<IReadOnlyList<JsonElement>> OperationsAsync(string itemId, CancellationToken ct) =>
        ListAsync(itemId, "operations", ct);

    public Task<IReadOnlyList<JsonElement>> InputsAsync(string itemId, CancellationToken ct) =>
        ListAsync(itemId, "input-items", ct);

    private async Task<IReadOnlyList<JsonElement>> ListAsync(string itemId, string resource, CancellationToken ct)
    {
        if (!pathsByItem.TryGetValue(itemId, out var paths)) return [];
        if (cache.TryGetValue((itemId, resource), out var cached)) return cached;
        var rows = new List<JsonElement>();
        foreach (var path in paths)
            rows.AddRange(await client.ListAsync($"{path}/{resource}/list", true, ct));
        return cache[(itemId, resource)] = rows.DistinctBy(row => Text(row, "id"), StringComparer.Ordinal).ToArray();
    }
}

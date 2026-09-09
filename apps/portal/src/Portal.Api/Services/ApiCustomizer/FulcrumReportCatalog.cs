using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Portal.Api.Services.ApiCustomizer;

public sealed class FulcrumReportCatalog
{
    public ApiCatalog Catalog { get; }
    public FulcrumReportCatalog()
    {
        using var stream = typeof(FulcrumReportCatalog).Assembly.GetManifestResourceStream("Portal.FulcrumSchema.json")
            ?? throw new InvalidOperationException("The bundled Fulcrum catalogue is missing.");
        var parsed = Parse(JsonNode.Parse(stream)!.AsObject());
        Catalog = parsed with { Sources = [InventoryBomReport.CreateSource(parsed), .. parsed.Sources] };
    }

    public static ApiCatalog Parse(JsonObject document)
    {
        JsonObject Resolve(JsonNode? node, int depth = 0)
        {
            if (depth > 12 || node is not JsonObject obj) return [];
            if (obj["$ref"]?.GetValue<string>() is { } reference)
                return Resolve(document["components"]?["schemas"]?[reference.Split('/').Last()], depth + 1);
            if (obj["allOf"] is JsonArray all)
            {
                var merged = new JsonObject();
                var properties = new JsonObject();
                foreach (var part in all.Select(x => Resolve(x, depth + 1)).Append(obj))
                {
                    foreach (var pair in part.Where(p => p.Key is not "allOf" and not "properties")) merged[pair.Key] = pair.Value?.DeepClone();
                    if (part["properties"] is JsonObject props)
                        foreach (var pair in props) properties[pair.Key] = pair.Value?.DeepClone();
                }
                merged["properties"] = properties;
                return merged;
            }
            return obj;
        }
        string[] Choices(JsonObject node) => node["enum"] is JsonArray values
            ? values.Select(v => v?.ToString() ?? "").ToArray() : [];
        List<ApiField> Fields(JsonNode? schema, string prefix = "", int depth = 0)
        {
            var fields = new List<ApiField>();
            if (depth > 6) return fields;
            var resolved = Resolve(schema);
            if (resolved["properties"] is not JsonObject props) return fields;
            foreach (var (key, value) in props)
            {
                var node = Resolve(value);
                var path = prefix + key;
                var type = node["type"]?.ToString() ?? "object";
                fields.Add(new(path, Label(path), type, node["description"]?.ToString() ?? "", Choices(node)));
                if (type == "object") fields.AddRange(Fields(node, path + ".", depth + 1));
                if (type == "array") fields.AddRange(Fields(node["items"], path + "[].", depth + 1));
            }
            return fields;
        }
        ApiInput Input(string key, JsonNode? schema, bool required, string? description = null, int depth = 0)
        {
            var node = Resolve(schema);
            var type = node["type"]?.ToString() ?? "object";
            ApiInput[] children = [];
            if (depth < 5 && type == "object" && node["properties"] is JsonObject props)
                children = props.Select(p => Input(p.Key, p.Value,
                    node["required"] is JsonArray req && req.Any(r => r?.ToString() == p.Key), depth: depth + 1)).ToArray();
            else if (depth < 5 && type == "array") children = [Input("value", node["items"], false, depth: depth + 1)];
            return new(key, Label(key.Replace("body.", "").Replace("query.", "").Replace("path.", "")),
                type, description ?? node["description"]?.ToString() ?? "", required, Choices(node), children,
                node["format"]?.ToString());
        }

        var sources = new List<ApiSource>();
        foreach (var (path, pathNode) in document["paths"]!.AsObject())
        foreach (var (method, operationNode) in pathNode!.AsObject())
        {
            if (operationNode is not JsonObject operation || operation["deprecated"]?.GetValue<bool>() == true) continue;
            // POST is permitted only for Fulcrum's documented, read-only list operations.
            if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.Contains("..")
                || !(method == "get" || method == "post" && Regex.IsMatch(path, @"/list(?:/v\d+)?$"))) continue;
            var response = operation["responses"]?["200"]?["content"]?["application/json"]?["schema"];
            if (response is null) continue;
            var schema = Resolve(response);
            var shape = "single";
            string? dataPath = null;
            if (schema["type"]?.ToString() == "array") { shape = "array"; schema = Resolve(schema["items"]); }
            else if (schema["properties"] is JsonObject props)
            {
                var data = props.FirstOrDefault(p => p.Key is "data" or "items" or "results" && Resolve(p.Value)["type"]?.ToString() == "array");
                if (data.Key is not null) { shape = "paged"; dataPath = data.Key; schema = Resolve(Resolve(data.Value)["items"]); }
            }
            else if (schema["additionalProperties"] is JsonObject) shape = "dictionary";
            var fields = Fields(schema);
            if (shape == "dictionary") fields = [new("key", "Key", "string", "Dictionary key / record ID", []), new("value", "Value", "object", "Value for this key", [])];
            if (fields.Count == 0) continue;
            var parameters = (operation["parameters"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (pathNode["parameters"] is JsonArray inherited) parameters.AddRange(inherited.OfType<JsonObject>());
            string? Paging(string name) => parameters.FirstOrDefault(p => p["in"]?.ToString() == "query" && p["name"]?.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) == true)?["name"]?.ToString();
            var inputs = parameters.Where(p => p["in"]?.ToString() is "query" or "path")
                .Where(p => p["name"]?.ToString().ToLowerInvariant() is not "skip" and not "take")
                .Select(p => Input(p["in"] + "." + p["name"], p["schema"], p["required"]?.GetValue<bool>() == true, p["description"]?.ToString())).ToList();
            var body = Resolve(operation["requestBody"]?["content"]?["application/json"]?["schema"]);
            if (body["properties"] is JsonObject bodyProps)
                inputs.AddRange(bodyProps.Select(p => Input("body." + p.Key, p.Value,
                    body["required"] is JsonArray req && req.Any(x => x?.ToString() == p.Key))));
            var category = operation["tags"]?[0]?.ToString() ?? "Other";
            var label = path == "/api/items/list/v2" ? "Parts / Items" : Label(path[5..].Replace("{", "").Replace("}", "").Replace("/list", "").Replace("/", " - "));
            sources.Add(new(method.ToUpperInvariant() + " " + path, label, category,
                operation["summary"]?.ToString() ?? label, method.ToUpperInvariant(), path, shape, dataPath,
                fields.DistinctBy(f => f.Path).ToArray(), inputs.ToArray(),
                (operation["x-c4-required-permissions"] as JsonArray ?? []).Select(p => p!.ToString()).ToArray(), Paging("skip"), Paging("take")));
        }
        return new("Fulcrum ITAR / 2026-09-09", sources.OrderBy(s => s.Category).ThenBy(s => s.Label).ToArray());
    }

    public ApiSource Source(string id) => Catalog.Sources.FirstOrDefault(s => s.Id == id)
        ?? throw new ReportValidationException("Choose a data source from the Fulcrum catalogue.");

    public static string Label(string value) => Regex.Replace(
        Regex.Replace(value.Replace("[]", " (List)").Replace(".", " / ").Replace("-", " "), "([a-z0-9])([A-Z])", "$1 $2"),
        @"\b[a-z]", match => match.Value.ToUpperInvariant());
}

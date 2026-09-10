using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Portal.Api.Data;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace Portal.Api.Services.ApiCustomizer;

public sealed record ReportSnapshot(string Actor, ReportDefinition Definition, ReportRunResult Result);

public sealed class FulcrumReportRunner(FulcrumReportCatalog catalog, HttpClient http,
    PortalRoleDbContext db, IIntegrationSecretProtector protector, IMemoryCache cache)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int MaxRecordsPerSource = 50000;
    private const int MaxRows = 100000;
    private const int MaxRequests = 1000;
    private const int PageSize = 500;

    public void Validate(ReportDefinition definition)
    {
        if (definition is null) throw new ReportValidationException("Supply a report layout.");
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 120)
            throw new ReportValidationException("Enter a report name of 1 to 120 characters.");
        if (definition.MaxRecords is < 1 or > MaxRecordsPerSource || definition.Sheets is null || definition.Sheets.Count is < 1 or > 8)
            throw new ReportValidationException($"Use 1 to 8 worksheets and a record limit between 1 and {MaxRecordsPerSource:N0}.");
        if (definition.Sheets.Any(sheet => sheet?.SourceId == InventoryBomReport.SourceId)
            && definition.MaxRecords > InventoryBomReport.MaxParentItems)
            throw new ReportValidationException($"Inventory BOM supports up to {InventoryBomReport.MaxParentItems:N0} starting items per report because each item requires related routing reads.");
        if (!Regex.IsMatch(definition.HeaderColor ?? "", "^[0-9A-Fa-f]{6}$"))
            throw new ReportValidationException("Select a valid header color.");
        if (definition.OutputMode is not ("combined" or "separate") || definition.OutputColumns is null)
            throw new ReportValidationException("Choose a supported report type.");
        var available = new Dictionary<string, ReportSheet>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in definition.Sheets)
        {
            if (sheet is null) throw new ReportValidationException("Supply a valid worksheet.");
            if (!Regex.IsMatch(sheet.Id ?? "", "^[a-zA-Z0-9_-]{1,40}$") || available.ContainsKey(sheet.Id!))
                throw new ReportValidationException("Each worksheet needs a unique identifier.");
            if (string.IsNullOrWhiteSpace(sheet.Name) || sheet.Name.Length > 31 || sheet.Name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0
                || sheet.Name.StartsWith('\'') || sheet.Name.EndsWith('\'') || !names.Add(sheet.Name)
                || sheet.Name.Equals("History", StringComparison.OrdinalIgnoreCase))
                throw new ReportValidationException("Use unique Excel sheet names of 1 to 31 characters without brackets, slashes, or : * ?.");
            var source = catalog.Source(sheet.SourceId);
            if (sheet.ParentSheetId is not null && !available.ContainsKey(sheet.ParentSheetId))
                throw new ReportValidationException("A linked sheet must follow its parent worksheet.");
            if (sheet.StartRow is < 1 or > 100 || sheet.StartColumn is < 1 or > 50
                || sheet.Columns is null || sheet.Columns.Count > 80
                || sheet.SortColumn is { } sort && (sort < 0 || sort >= sheet.Columns.Count))
                throw new ReportValidationException("Choose up to 80 columns and valid report options.");
            available.Add(sheet.Id!, sheet);
            bool IsAncestor(string id)
            {
                for (var cursor = sheet.ParentSheetId; cursor is not null; cursor = available[cursor].ParentSheetId)
                    if (cursor == id) return true;
                return false;
            }
            void CheckField(string id, string path, bool binding)
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path))
                    throw new ReportValidationException("Select a worksheet and field for each column or link.");
                if (!(id == sheet.Id && !binding || IsAncestor(id)))
                    throw new ReportValidationException("Columns and links may only use this worksheet or its parents.");
                var fields = catalog.Source(available[id].SourceId).Fields;
                if (path.Length > 240 || !(fields.Any(f => f.Path == path)
                    || fields.Any(f => f.Type == "object" && path.StartsWith(f.Path + ".", StringComparison.Ordinal)
                        && Regex.IsMatch(path[(f.Path.Length + 1)..], @"^[\w .-]{1,120}$"))))
                    throw new ReportValidationException($"The field '{path}' is not available in the selected catalogue source.");
                if (binding && path.Contains("[]"))
                    throw new ReportValidationException("Use a single ID field to connect a worksheet, not a list of values.");
            }
            foreach (var column in sheet.Columns)
            {
                if (column is null) throw new ReportValidationException("Supply a valid column.");
                CheckField(column.SheetId, column.Path, false);
                if (string.IsNullOrWhiteSpace(column.Header) || column.Header.Length > 120 || column.Width is < 4 or > 100
                    || column.Format is not ("text" or "number" or "currency" or "date" or "boolean" or "duration"))
                    throw new ReportValidationException("Every column needs a heading, width from 4 to 100, and a supported format.");
            }
            if (sheet.Inputs is null || sheet.Bindings is null) throw new ReportValidationException("Worksheet filters and links are required.");
            foreach (var (key, value) in sheet.Inputs)
                ValidateInput(source.Inputs.FirstOrDefault(x => x.Key == key)
                    ?? throw new ReportValidationException($"Unknown filter '{key}'."), value);
            foreach (var (key, field) in sheet.Bindings)
            {
                if (field is null) throw new ReportValidationException("Supply a valid record link.");
                if (!source.Inputs.Any(x => x.Key == key)) throw new ReportValidationException($"Unknown link input '{key}'.");
                CheckField(field.SheetId, field.Path, true);
            }
            foreach (var input in source.Inputs.Where(x => x.Required))
                if (!sheet.Bindings.ContainsKey(input.Key)
                    && (!sheet.Inputs.TryGetValue(input.Key, out var value) || value.ValueKind == JsonValueKind.Null || value.ToString().Length == 0))
                    throw new ReportValidationException($"{sheet.Name}: supply or link {input.Label}.");
        }
        if (definition.DetailSheetId is not null && !available.ContainsKey(definition.DetailSheetId))
            throw new ReportValidationException("Choose an available record type for report rows.");
        if (definition.OutputMode == "combined" && definition.Sheets.Count(s => s.ParentSheetId is null) != 1)
            throw new ReportValidationException("Combine related records, or choose separate tables for independent sources.");
        var outputColumns = ReportPresentation.Columns(definition);
        if (outputColumns.Count == 0 || definition.OutputMode == "combined" && outputColumns.Count > 80)
            throw new ReportValidationException("Select between 1 and 80 fields for the report.");
        if (definition.OutputSortColumn is { } outputSort && (outputSort < 0 || outputSort >= outputColumns.Count))
            throw new ReportValidationException("Choose an available sort field.");
        foreach (var column in outputColumns)
        {
            if (column is null || column.SheetId is null || !available.TryGetValue(column.SheetId, out var owner)
                || string.IsNullOrWhiteSpace(column.Path) || column.Path.Length > 240)
                throw new ReportValidationException("Select available fields for the report.");
            var fields = catalog.Source(owner.SourceId).Fields;
            if (!fields.Any(f => f.Path == column.Path || f.Type == "object" && column.Path.StartsWith(f.Path + ".", StringComparison.Ordinal)
                && Regex.IsMatch(column.Path[(f.Path.Length + 1)..], @"^[\w .-]{1,120}$"))
                || string.IsNullOrWhiteSpace(column.Header) || column.Header.Length > 120 || column.Width is < 4 or > 100
                || column.Format is not ("text" or "number" or "currency" or "date" or "boolean" or "duration"))
                throw new ReportValidationException("Choose valid fields, headings, and formats for the report.");
        }
        if (JsonSerializer.Serialize(definition, JsonOptions).Length > 200000)
            throw new ReportValidationException("This report layout is too large. Reduce its filters or columns.");
    }

    private static void ValidateInput(ApiInput input, JsonElement value, int depth = 0)
    {
        if (depth > 8 || value.GetRawText().Length > 20000) throw new ReportValidationException("A filter is too large.");
        if (value.ValueKind == JsonValueKind.Null && !input.Required) return;
        var valid = input.Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 2000,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 50,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!valid || input.Choices.Length > 0 && !input.Choices.Contains(value.ToString()))
            throw new ReportValidationException($"Enter a valid {input.Type} for {input.Label}.");
        if (input.Format is "date" or "date-time" && !DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ReportValidationException($"Enter a valid date for {input.Label}.");
        if (input.Type == "array" && input.Children.Length > 0)
            foreach (var element in value.EnumerateArray()) ValidateInput(input.Children[0], element, depth + 1);
        if (input.Type == "object")
        {
            foreach (var property in value.EnumerateObject())
                ValidateInput(input.Children.FirstOrDefault(x => x.Key == property.Name)
                    ?? throw new ReportValidationException($"Unknown option in {input.Label}: {property.Name}."), property.Value, depth + 1);
            foreach (var child in input.Children.Where(x => x.Required))
                if (!value.TryGetProperty(child.Key, out _)) throw new ReportValidationException($"{child.Label} is required in {input.Label}.");
        }
    }

    public async Task<ReportRunResult> RunAsync(ReportRunRequest run, string actor, CancellationToken cancellationToken)
    {
        Validate(run.Definition);
        var hasBom = run.Definition.Sheets.Any(s => s.SourceId == InventoryBomReport.SourceId);
        var rowLimit = hasBom ? InventoryBomReport.MaxRows : MaxRows;
        var requestLimit = hasBom ? InventoryBomReport.MaxRequests : MaxRequests;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(hasBom ? InventoryBomReport.TimeoutMinutes : run.Definition.MaxRecords > 5000 ? 10 : 3));
        var token = "";
        if (!run.Sample)
        {
            var credential = await db.IntegrationCredentials.AsNoTracking()
                .SingleOrDefaultAsync(c => c.CredentialKey == IntegrationCredentialNames.FulcrumPublicApi, cancellationToken);
            if (credential is null) throw new ReportValidationException("Save the Fulcrum Public API credential under Admin > API Keys before loading live data.");
            if (credential.ExpiresAt <= DateTimeOffset.UtcNow) throw new ReportValidationException("The Fulcrum token has expired. Replace it under API Keys.");
            try { token = protector.Unprotect(credential.EncryptedSecret); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or PlatformNotSupportedException)
            { throw new ReportValidationException("The saved Fulcrum token cannot be opened on this server. Replace it under API Keys."); }
        }
        var requests = 0;
        var allRows = 0;
        var data = new Dictionary<string, List<ReportSourceRow>>();
        var results = new List<ReportSheetResult>();
        var warnings = new List<string>();
        void CountRequest()
        {
            if (Interlocked.Increment(ref requests) > requestLimit)
                throw new ReportValidationException($"This report needs over {requestLimit:N0} API requests. Narrow the item filters; no partial workbook was created.");
        }
        foreach (var sheet in run.Definition.Sheets)
        {
            var source = catalog.Source(sheet.SourceId);
            var parents = sheet.ParentSheetId is null ? [new ReportSourceRow([], [])] : data[sheet.ParentSheetId];
            var contexts = new List<ReportSourceRow>();
            foreach (var parent in parents)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var inputs = new Dictionary<string, JsonElement>(sheet.Inputs);
                if (source.Id == InventoryBomReport.SourceId && (run.Definition.OutputSortColumn is not null || run.Definition.Sheets.Count > 1))
                {
                    inputs["report.repeatOperations"] = JsonSerializer.SerializeToElement(true);
                    warnings.Add("Operation details repeat on each BOM row because this report sorts rows or connects additional records.");
                }
                var emptyParent = sheet.ParentSheetId is not null
                    && !parent.Records[sheet.ParentSheetId].EnumerateObject().Any();
                foreach (var (key, field) in sheet.Bindings)
                {
                    if (emptyParent) break;
                    var value = Read(parent.Records[field.SheetId], field.Path);
                    if (value is null || string.IsNullOrWhiteSpace(value.ToString()))
                        throw new ReportValidationException($"{sheet.Name}: a parent record has no {field.Path}. Choose a populated relationship field or narrow the parent filter.");
                    var input = source.Inputs.Single(i => i.Key == key);
                    var element = JsonSerializer.SerializeToElement(input.Type == "array" ? new[] { value } : value);
                    ValidateInput(input, element);
                    inputs[key] = element;
                }
                var rows = emptyParent ? [] : source.Id == InventoryBomReport.SourceId
                    ? await InventoryBomReport.ReadAsync(inputs, run.Sample,
                        (id, filters) => FetchAsync(catalog.Source(id), filters, token, run.Definition.MaxRecords, CountRequest, timeout.Token), warnings, timeout.Token)
                    : run.Sample ? Sample(source, sheet.ParentSheetId is null ? 3 : 2)
                    : await FetchAsync(source, inputs, token, run.Definition.MaxRecords, CountRequest, timeout.Token);
                if (rows.Count == 0 && sheet.ParentSheetId is not null && sheet.IncludeEmptyParents)
                    rows.Add(JsonSerializer.SerializeToElement(new { }));
                foreach (var row in rows)
                {
                    if (++allRows > rowLimit) throw new ReportValidationException($"The report exceeds {rowLimit:N0} rows. Narrow the filters and run it again.");
                    contexts.Add(new(new(parent.Records) { [sheet.Id] = row }, new(parent.Keys) { [sheet.Id] = allRows }));
                }
            }
            data[sheet.Id] = contexts;
            var unmapped = ReportPresentation.Columns(run.Definition).Where(c => c.SheetId == sheet.Id
                && source.Fields.Any(f => f.Path == c.Path && f.Availability == "unmapped")).Select(c => c.Header).ToArray();
            if (unmapped.Length > 0) warnings.Add("Unmapped columns left blank: " + string.Join(", ", unmapped) + ". Use Customize Columns to map or remove them.");
            var cells = contexts.Select(context => sheet.Columns.Select(c => Read(context.Records[c.SheetId], c.Path)).ToArray()).ToList();
            if (cells.Any(row => row.Any(value => value is string text && text.Length > 32767)))
                throw new ReportValidationException($"{sheet.Name} contains text longer than Excel's 32,767-character cell limit. Select narrower fields instead of entire objects or lists.");
            if (sheet.SortColumn is { } sort)
            {
                var comparer = Comparer<object?>.Create(Compare);
                cells = (sheet.SortDescending ? cells.OrderByDescending(r => r[sort], comparer) : cells.OrderBy(r => r[sort], comparer)).ToList();
            }
            if (sheet.Columns.Count > 0) results.Add(new(sheet.Id, sheet.Name, sheet.Columns, cells));
        }
        if (run.Definition.OutputMode == "combined") results = [ReportPresentation.Combine(run.Definition, data)];
        else if (run.Definition.OutputSortColumn is { } outputSort)
        {
            var selectedColumn = ReportPresentation.Columns(run.Definition)[outputSort];
            results = results.Select(result =>
            {
                var index = result.Columns.FindIndex(c => c.SheetId == selectedColumn.SheetId && c.Path == selectedColumn.Path);
                if (index < 0) return result;
                var comparer = Comparer<object?>.Create(Compare);
                return result with { Rows = (run.Definition.OutputSortDescending
                    ? result.Rows.OrderByDescending(row => row[index], comparer) : result.Rows.OrderBy(row => row[index], comparer)).ToList() };
            }).ToList();
        }
        results = results.Select(result => ReportPresentation.Size(result, run.Definition.AutoSize)).ToList();
        if (run.Sample) warnings.Add("SAMPLE DATA: invented records for layout testing. Filters are illustrated, not applied to Fulcrum.");
        if (!hasBom) warnings.Add(run.Definition.OutputMode == "combined"
            ? "Related records outside the chosen row type are listed together in their cells, not multiplied or summed."
            : "Each table contains one row per source record. Related lists stay together in their cells.");
        var now = DateTimeOffset.UtcNow;
        var result = new ReportRunResult(Guid.NewGuid(), run.Definition.Name, run.Sample, now, now.AddMinutes(15), requests, results, warnings);
        if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > 20000000)
            throw new ReportValidationException("This report exceeds the 20 MB preview limit. Reduce its columns or records.");
        if (cache.TryGetValue<string>("customizer-last:" + actor, out var previous)) cache.Remove(previous!);
        var cacheKey = "customizer-run:" + result.Id;
        cache.Set(cacheKey, new ReportSnapshot(actor, run.Definition, result), TimeSpan.FromMinutes(15));
        cache.Set("customizer-last:" + actor, cacheKey, TimeSpan.FromMinutes(15));
        return result;
    }

    private async Task<List<JsonElement>> FetchAsync(ApiSource source, Dictionary<string, JsonElement> inputs,
        string token, int limit, Action count, CancellationToken cancellationToken)
    {
        if (source.Method is not ("GET" or "POST") || !source.Path.StartsWith("/api/", StringComparison.Ordinal))
            throw new ReportValidationException("Only documented Fulcrum reads can be sent to the API.");
        var rows = new List<JsonElement>();
        string? lastPage = null;
        for (var offset = 0; ; )
        {
            count();
            var path = source.Path;
            var query = new List<string>();
            var body = new JsonObject();
            foreach (var (key, value) in inputs.Where(p => p.Value.ValueKind != JsonValueKind.Null))
            {
                if (key.StartsWith("path."))
                {
                    var part = value.ToString();
                    if (!Regex.IsMatch(part, @"^[a-zA-Z0-9_-]{1,160}$")) throw new ReportValidationException("A linked record ID contains unsupported characters.");
                    path = path.Replace("{" + key[5..] + "}", Uri.EscapeDataString(part));
                }
                else if (key.StartsWith("query."))
                {
                    var values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
                    foreach (var item in values) query.Add(Uri.EscapeDataString(key[6..]) + "=" + Uri.EscapeDataString(item.ToString()));
                }
                else body[key[5..]] = JsonNode.Parse(value.GetRawText());
            }
            if (path.Contains('{')) throw new ReportValidationException($"Connect the required parent IDs for {source.Label}.");
            // Fulcrum's per-request cap is independent of Arda's total report limit.
            // Keep pages modest and continue with skip/take until the source is complete.
            var take = Math.Min(PageSize, limit + 1 - rows.Count);
            if (source.Skip is not null) query.Add(Uri.EscapeDataString(source.Skip) + "=" + offset);
            if (source.Take is not null) query.Add(Uri.EscapeDataString(source.Take) + "=" + take);
            var uri = new Uri(new Uri(FulcrumApiEndpoint.ItarBaseUrl), path.TrimStart('/') + (query.Count > 0 ? "?" + string.Join("&", query) : ""));
            using var request = new HttpRequestMessage(new HttpMethod(source.Method), uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (source.Method == "POST") request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ReportValidationException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Fulcrum rejected the saved token. Replace it under API Keys.",
                    HttpStatusCode.Forbidden => $"The Fulcrum token cannot read {source.Label}. Required permission: {string.Join(", ", source.Permissions)}.",
                    HttpStatusCode.TooManyRequests => "Fulcrum has reached its API rate limit. Wait and retry, or narrow the report.",
                    _ => $"Fulcrum could not read {source.Label} (HTTP {(int)response.StatusCode}). Check the filters and required inputs."
                });
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int length;
            while ((length = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + length > 20000000) throw new ReportValidationException("A Fulcrum response exceeds 20 MB. Narrow the report filters.");
                buffer.Write(chunk, 0, length);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            var page = Extract(source, root);
            if (rows.Count + page.Count > limit) throw new ReportValidationException($"{source.Label} exceeds the selected {limit:N0}-record limit. Increase the limit or narrow the filters; no partial workbook was created.");
            var fingerprint = page.Count > 0 ? string.Join("|", page.Select(x => x.GetRawText())) : null;
            if (fingerprint is not null && fingerprint == lastPage) throw new ReportValidationException("Fulcrum repeated a page of records. The report was stopped to avoid duplicate rows.");
            lastPage = fingerprint;
            rows.AddRange(page);
            var hasNext = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hasNextPage", out var next) && next.ValueKind == JsonValueKind.True;
            if (source.Shape == "paged" && root.TryGetProperty("totalCount", out var total)
                && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var totalCount))
                hasNext |= totalCount > rows.Count;
            if (source.Skip is null || source.Take is null)
            {
                if (hasNext || source.Take is not null && page.Count >= take) throw new ReportValidationException("This source does not expose complete pagination. Narrow its filters before exporting.");
                break;
            }
            if (page.Count == 0 && hasNext) throw new ReportValidationException("Fulcrum returned an empty page with more records pending. Retry the report.");
            if (page.Count < take && !hasNext) break;
            offset += page.Count;
        }
        return rows;
    }

    public static List<JsonElement> Extract(ApiSource source, JsonElement root)
    {
        var node = root;
        if (source.Shape == "paged" && (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(source.DataPath!, out node)))
            throw new ReportValidationException("Fulcrum's response no longer matches the catalogue. The report has been stopped.");
        if (source.Shape is "array" or "paged")
        {
            if (node.ValueKind != JsonValueKind.Array) throw new ReportValidationException("Fulcrum returned an unexpected record list.");
            return node.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        if (node.ValueKind != JsonValueKind.Object) throw new ReportValidationException("Fulcrum returned an unexpected record.");
        return source.Shape == "dictionary"
            ? node.EnumerateObject().Select(p => JsonSerializer.SerializeToElement(new { key = p.Name, value = p.Value })).ToList()
            : [node.Clone()];
    }

    public static object? Read(JsonElement root, string path)
    {
        var arrayIndex = path.IndexOf("[].", StringComparison.Ordinal);
        if (arrayIndex >= 0)
        {
            var array = Find(root, path[..arrayIndex]);
            return array.ValueKind == JsonValueKind.Array
                ? string.Join("; ", array.EnumerateArray().Select(x => Read(x, path[(arrayIndex + 3)..]))) : null;
        }
        var value = Find(root, path);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : value.ToString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null
        };
    }
    private static JsonElement Find(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object) return default;
        if (root.TryGetProperty(path, out var direct)) return direct;
        var split = path.IndexOf('.');
        return split >= 0 && root.TryGetProperty(path[..split], out var nested)
            ? Find(nested, path[(split + 1)..]) : default;
    }
    private static int Compare(object? left, object? right) => left is decimal l && right is decimal r
        ? l.CompareTo(r) : StringComparer.OrdinalIgnoreCase.Compare(left?.ToString(), right?.ToString());

    private static List<JsonElement> Sample(ApiSource source, int count) => Enumerable.Range(1, count).Select(index =>
    {
        var root = new JsonObject();
        foreach (var field in source.Fields.Where(f => f.Type is "string" or "integer" or "number" or "boolean" && !f.Path.Contains("[]")))
        {
            var parts = field.Path.Split('.');
            var current = root;
            foreach (var part in parts.SkipLast(1)) { current[part] ??= new JsonObject(); if (current[part] is not JsonObject nested) break; current = nested; }
            object value = field.Type switch
            {
                "integer" => index * 10,
                "number" => index * 12.5m,
                "boolean" => false,
                _ => field.Choices.FirstOrDefault() ?? (field.Path == "id" ? index.ToString("D24") : field.Path == "number" ? $"DEMO-PART-{index:000}" : field.Path.Contains("Utc") || field.Path.EndsWith("Date") ? "2026-09-09" : $"Sample {field.Label} {index}")
            };
            current[parts[^1]] = JsonSerializer.SerializeToNode(value);
        }
        return JsonSerializer.SerializeToElement(root);
    }).ToList();
}

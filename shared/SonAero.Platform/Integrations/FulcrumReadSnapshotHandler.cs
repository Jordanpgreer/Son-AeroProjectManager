using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SonAero.Platform.Integrations;

public sealed class FulcrumReadSnapshotHandler(FulcrumSnapshotStore store,
    IOptions<UniversalFulcrumSyncOptions> options, TimeProvider clock,
    ILogger<FulcrumReadSnapshotHandler> logger) : DelegatingHandler
{
    // Cost generation, RFQ drafts, and current assignment/identity verification must use live data.
    public static readonly HttpRequestOptionsKey<bool> RequireFresh = new("Arda.Fulcrum.RequireFresh");
    // Only a read-only consumer that displays freshness may opt in. Ingestions retain their DB data on failure.
    public static readonly HttpRequestOptionsKey<bool> AllowStale = new("Arda.Fulcrum.AllowStale");
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(6);
    private static long nextPruneTicks;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsSupportedRead(request)) return await base.SendAsync(request, ct);
        var settings = options.Value;
        var fresh = request.Options.TryGetValue(RequireFresh, out var requireFresh) && requireFresh;
        var allowStale = !fresh && request.Options.TryGetValue(AllowStale, out var requestedStale) && requestedStale;
        var credential = FulcrumSnapshotStore.Hash(request.RequestUri!.GetLeftPart(UriPartial.Authority)
            + "\n" + request.Headers.Authorization);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var key = FulcrumSnapshotStore.Hash(credential + "\n" + request.Method + "\n"
            + CanonicalUrl(request.RequestUri) + "\n" + CanonicalBody(body));
        var owner = Guid.NewGuid().ToString("N");
        var throttleKey = FulcrumSnapshotStore.Hash(credential + ":cooldown");
        var deniedKey = FulcrumSnapshotStore.Hash(credential + ":denied");
        var forbiddenKey = FulcrumSnapshotStore.Hash(credential + ":forbidden:" + PermissionScope(request));
        var denied = await store.ReadAsync(deniedKey, ct);
        var forbidden = await store.ReadAsync(forbiddenKey, ct);
        if (denied?.RetryAt > clock.GetUtcNow() || forbidden?.RetryAt > clock.GetUtcNow())
            throw new HttpRequestException("Fulcrum rejected this credential. Check the shared API credential in Admin Hub.", null, HttpStatusCode.Forbidden);
        FulcrumReadSnapshot? snapshot = null;
        if (!fresh)
        {
            while (true)
            {
                var now = clock.GetUtcNow();
                snapshot = await store.ReadAsync(key, ct);
                if (snapshot?.Body is not null && snapshot.ExpiresAt > now)
                    return FromSnapshot(request, snapshot, false);
                var cooldown = await store.ReadAsync(throttleKey, ct);
                if (snapshot?.RetryAt > now || cooldown?.RetryAt > now)
                    return StaleOrThrow(request, snapshot, settings, allowStale, "The Fulcrum connection is waiting before retrying.");
                if (await store.TryAcquireAsync(key, owner, now, LeaseDuration, ct)) break;
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, ct);
            }
        }

        string? slot = null;
        try
        {
            slot = await AdmitAsync(credential, owner, throttleKey, settings, ct);
            using var response = await base.SendAsync(request, ct);
            var now = clock.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                await store.DeferAsync(deniedKey, now.AddMinutes(5), ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                await store.DeferAsync(forbiddenKey, now.AddMinutes(5), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var retry = RetryAt(response, now);
                await store.DeferAsync(throttleKey, retry, ct);
                if (!fresh) await store.DeferAsync(key, retry, ct);
                if (allowStale && CanUseStale(snapshot, settings))
                    return FromSnapshot(request, snapshot!, true);
                return await CopyResponseAsync(response, request, settings.MaximumResponseBytes, ct);
            }
            var result = await CopyResponseAsync(response, request, settings.MaximumResponseBytes, ct);
            if (!fresh && response.StatusCode == HttpStatusCode.OK
                && result.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var bytes = await result.Content.ReadAsByteArrayAsync(ct);
                // A malformed success must never replace a usable snapshot.
                using var parsed = JsonDocument.Parse(bytes);
                await store.PublishAsync(key, owner, bytes, result.Content.Headers.ContentType.ToString(),
                    now, settings.NextBoundary(now), ct);
            }
            if (now.UtcTicks > Interlocked.Read(ref nextPruneTicks))
            {
                Interlocked.Exchange(ref nextPruneTicks, now.AddHours(1).UtcTicks);
                await store.PruneAsync(now, ct);
            }
            return result;
        }
        catch (HttpRequestException exception) when (!ct.IsCancellationRequested
            && (exception.StatusCode is null || exception.StatusCode == HttpStatusCode.TooManyRequests
                || (int)exception.StatusCode >= 500))
        {
            if (!fresh) await store.DeferAsync(key, clock.GetUtcNow().AddSeconds(30), ct);
            await store.DeferAsync(throttleKey, clock.GetUtcNow().AddSeconds(30), ct);
            if (allowStale && CanUseStale(snapshot, settings)) return FromSnapshot(request, snapshot!, true);
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeouts arrive as cancellation. Avoid a burst from other module callers.
            await store.DeferAsync(throttleKey, clock.GetUtcNow().AddSeconds(30), CancellationToken.None);
            throw;
        }
        finally
        {
            if (slot is not null) await store.ReleaseAsync(slot, owner, CancellationToken.None);
            if (!fresh) await store.ReleaseAsync(key, owner, CancellationToken.None);
        }
    }

    private async Task<string> AdmitAsync(string credential, string owner, string throttleKey,
        UniversalFulcrumSyncOptions settings, CancellationToken ct)
    {
        var paceKey = FulcrumSnapshotStore.Hash(credential + ":pace");
        string? slot = null;
        try
        {
            while (slot is null)
            {
                var now = clock.GetUtcNow();
                var cooldown = await store.ReadAsync(throttleKey, ct);
                if (cooldown?.RetryAt > now)
                    throw new HttpRequestException("Fulcrum requested a pause before the next API read.", null,
                        HttpStatusCode.ServiceUnavailable);
                for (var i = 0; i < settings.MaximumConcurrentRequests; i++)
                {
                    var candidate = FulcrumSnapshotStore.Hash(credential + ":slot:" + i);
                    if (!await store.TryAcquireAsync(candidate, owner, now, LeaseDuration, ct, false)) continue;
                    slot = candidate;
                    break;
                }
                if (slot is null) await Task.Delay(TimeSpan.FromMilliseconds(100), clock, ct);
            }
            while (!await store.TryAcquireAsync(paceKey, owner, clock.GetUtcNow(), LeaseDuration, ct, false))
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, ct);
            try
            {
                await store.DeferAsync(paceKey,
                    clock.GetUtcNow().AddMilliseconds(settings.MinimumRequestSpacingMilliseconds), ct);
            }
            finally { await store.ReleaseAsync(paceKey, owner, CancellationToken.None); }
            return slot;
        }
        catch
        {
            if (slot is not null) await store.ReleaseAsync(slot, owner, CancellationToken.None);
            throw;
        }
    }

    private HttpResponseMessage StaleOrThrow(HttpRequestMessage request, FulcrumReadSnapshot? snapshot,
        UniversalFulcrumSyncOptions settings, bool allowStale, string message) => allowStale && CanUseStale(snapshot, settings)
        ? FromSnapshot(request, snapshot!, true)
        : throw new HttpRequestException(message, null, HttpStatusCode.ServiceUnavailable);

    private bool CanUseStale(FulcrumReadSnapshot? snapshot, UniversalFulcrumSyncOptions settings) =>
        snapshot?.Body is not null && settings.MaximumStaleMinutes > 0
        && snapshot.FetchedAt >= clock.GetUtcNow().AddMinutes(-settings.MaximumStaleMinutes);

    private HttpResponseMessage FromSnapshot(HttpRequestMessage request, FulcrumReadSnapshot snapshot, bool stale)
    {
        if (stale) logger.LogWarning("Fulcrum is unavailable; retaining a shared read snapshot from {FetchedAt}.", snapshot.FetchedAt);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(snapshot.Body!)
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", snapshot.ContentType ?? "application/json");
        response.Headers.Add("X-Arda-Fulcrum-Snapshot", stale ? "stale" : "shared");
        response.Headers.Add("X-Arda-Fulcrum-Fetched-At", snapshot.FetchedAt.ToString("O"));
        return response;
    }

    private static DateTimeOffset RetryAt(HttpResponseMessage response, DateTimeOffset now)
    {
        var retry = response.Headers.RetryAfter?.Date
            ?? (response.Headers.RetryAfter?.Delta is { } delay ? now + delay : now.AddSeconds(30));
        return retry > now ? retry : now.AddSeconds(1);
    }

    private static async Task<HttpResponseMessage> CopyResponseAsync(HttpResponseMessage source,
        HttpRequestMessage request, int maximumBytes, CancellationToken ct)
    {
        if (source.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidOperationException("Fulcrum returned a response exceeding the configured safe size.");
        await using var stream = await source.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + count > maximumBytes)
                throw new InvalidOperationException("Fulcrum returned a response exceeding the configured safe size.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var result = new HttpResponseMessage(source.StatusCode)
        {
            RequestMessage = request, ReasonPhrase = source.ReasonPhrase, Version = source.Version,
            Content = new ByteArrayContent(bytes.ToArray())
        };
        foreach (var header in source.Headers) result.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in source.Content.Headers) result.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return result;
    }

    public static bool IsSupportedRead(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "api.fulcrumpro.us" || request.Headers.Authorization?.Scheme != "Bearer"
            || string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter)) return false;
        var path = uri.AbsolutePath;
        return request.Method == HttpMethod.Get
            ? Regex.IsMatch(path, @"^/api/(quotes/[^/]+|items/[^/]+(?:/purchase-history)?|vendors/[^/]+|inventory/availableByItem)$")
            : request.Method == HttpMethod.Post && Regex.IsMatch(path,
                @"^/api/(?:quotes|items|jobs|sales-orders|shipments|shipment-line-items|job-tracking-timers|vendors|reporting/(?:quote|shipping|sales-order-lines))(?:/[^/]+(?:/(?:routing))?/(?:part-line-items|operations|input-items|input-materials|contacts|vendors))?/list(?:/v2)?$");
    }

    private static string CanonicalUrl(Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(pair => Uri.EscapeDataString(Uri.UnescapeDataString(pair[0]).ToLowerInvariant()) + "="
                + (pair.Length == 2 ? Uri.EscapeDataString(Uri.UnescapeDataString(pair[1])) : ""))
            .Order(StringComparer.Ordinal);
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', query);
    }

    private static string PermissionScope(HttpRequestMessage request)
    {
        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        // Record IDs share permissions, while list/detail/routing/contact endpoints can require different grants.
        if (segments.Length > 2 && segments[1] != "reporting" && segments[1] != "inventory" && segments[2] != "list")
            segments[2] = "{id}";
        return request.Method + ":" + string.Join('/', segments);
    }

    private static string CanonicalBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        using var json = JsonDocument.Parse(body);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(json.RootElement, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var child in value.EnumerateArray()) Write(child, writer); writer.WriteEndArray(); }
            else value.WriteTo(writer);
        }
    }
}

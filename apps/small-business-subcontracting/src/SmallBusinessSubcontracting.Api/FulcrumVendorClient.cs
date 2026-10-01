using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace SmallBusinessSubcontracting.Api;

public sealed record FulcrumVendorContactSnapshot(
    string Id,
    string Name,
    string? Position,
    string? Phone,
    string? Email);

public sealed record FulcrumVendorSnapshot(
    string Id,
    string Name,
    string? VendorCode,
    bool Active,
    string? Website,
    IReadOnlyList<FulcrumVendorContactSnapshot> Contacts);

public interface IFulcrumVendorClient
{
    Task<IReadOnlyList<FulcrumVendorSnapshot>> GetVendorsAsync(CancellationToken cancellationToken);
}

public sealed class FulcrumVendorClient(
    HttpClient httpClient,
    RoleStoreDbContext roleStore,
    IIntegrationSecretProtector protector,
    IConfiguration configuration,
    ILogger<FulcrumVendorClient> logger) : IFulcrumVendorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<FulcrumVendorSnapshot>> GetVendorsAsync(
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        var baseUri = FulcrumApiEndpoint.ResolveItarBaseUri(
            configuration["Fulcrum:BaseUrl"],
            "Fulcrum:BaseUrl");
        var pageSize = Math.Clamp(configuration.GetValue("Fulcrum:PageSize", 500), 1, 5000);
        var maxRecords = Math.Clamp(configuration.GetValue("Fulcrum:MaximumVendors", 20000), 1, 100000);
        var contactConcurrency = Math.Clamp(configuration.GetValue("Fulcrum:ContactConcurrency", 4), 1, 12);
        httpClient.BaseAddress = baseUri;

        var vendors = new List<FulcrumVendorDto>();
        var skip = 0;
        while (vendors.Count < maxRecords)
        {
            var take = Math.Min(pageSize, maxRecords - vendors.Count);
            var page = await PostAsync<IReadOnlyList<FulcrumVendorDto>>(
                $"api/vendors/list?Skip={skip}&Take={take}&Sort.Field=Name&Sort.Dir={FulcrumApiEndpoint.AscendingSortDirection}",
                token,
                cancellationToken);
            vendors.AddRange(page);
            if (page.Count < take || page.Count == 0) break;
            skip += page.Count;
        }

        if (vendors.Count == maxRecords)
            logger.LogWarning(
                "Fulcrum vendor synchronization reached the configured {Maximum} record safety limit.",
                maxRecords);

        using var gate = new SemaphoreSlim(contactConcurrency);
        var snapshots = new FulcrumVendorSnapshot[vendors.Count];
        await Task.WhenAll(vendors.Select(async (vendor, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var contacts = await PostAsync<IReadOnlyList<FulcrumVendorContactDto>>(
                    $"api/vendors/{Uri.EscapeDataString(vendor.Id)}/contacts/list",
                    token,
                    cancellationToken);
                snapshots[index] = new FulcrumVendorSnapshot(
                    vendor.Id,
                    vendor.Name.Trim(),
                    Clean(vendor.VendorCode),
                    vendor.Active,
                    Clean(vendor.Url),
                    contacts.Select(ToSnapshot).ToArray());
            }
            finally
            {
                gate.Release();
            }
        }));
        return snapshots;
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var credential = await roleStore.IntegrationCredentials.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.CredentialKey == IntegrationCredentialNames.FulcrumPublicApi,
                cancellationToken);
        if (credential is null)
            throw new InvalidOperationException(
                "The Fulcrum Public API credential is not configured. Add it in Admin Hub under API Keys.");
        if (credential.ExpiresAt is not null && credential.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException(
                "The Fulcrum Public API credential has expired. Replace it in Admin Hub under API Keys.");

        try
        {
            var token = protector.Unprotect(credential.EncryptedSecret).Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = token["Bearer ".Length..].Trim();
            return token;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                "The saved Fulcrum credential could not be decrypted on this application server. Replace it in Admin Hub.",
                exception);
        }
    }

    private async Task<T> PostAsync<T>(
        string relativeUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("SonAero-SmallBusinessSubcontracting/1.0");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Fulcrum returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for {relativeUrl}.",
                null,
                response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Fulcrum returned an empty response for {relativeUrl}.");
    }

    private static FulcrumVendorContactSnapshot ToSnapshot(FulcrumVendorContactDto contact)
    {
        var name = string.Join(' ', new[] { Clean(contact.FirstName), Clean(contact.LastName) }
            .Where(value => value is not null));
        return new FulcrumVendorContactSnapshot(
            contact.Id,
            name,
            Clean(contact.Position),
            Clean(contact.Phone) ?? Clean(contact.CellPhone),
            Clean(contact.Email));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record FulcrumVendorDto(
        string Id,
        string Name,
        string? VendorCode,
        bool Active,
        string? Url);

    private sealed record FulcrumVendorContactDto(
        string Id,
        string FirstName,
        string LastName,
        string? Position,
        string? CellPhone,
        string? Phone,
        string? Email);
}

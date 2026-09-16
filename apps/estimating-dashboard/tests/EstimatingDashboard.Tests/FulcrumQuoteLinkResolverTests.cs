using System.Net;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteLinkResolverTests
{
    private const string Template = "https://son-aero.fulcrumpro.com/ui/quotes/{id}/details";

    [Fact]
    public async Task Exact_Fulcrum_ID_and_quote_number_returns_browser_link()
    {
        using var handler = new QuoteHandler("""{"id":"fulcrum-4445","number":4445}""");
        using var http = new HttpClient(handler);
        var resolver = CreateResolver(http);

        var url = await resolver.ResolveAsync("fulcrum-4445", 4445, default);

        Assert.Equal("https://son-aero.fulcrumpro.com/ui/quotes/fulcrum-4445/details", url);
        Assert.Equal("/api/quotes/fulcrum-4445", handler.Path);
        Assert.Equal("Bearer test-token", handler.Authorization);
        Assert.Equal("api.fulcrumpro.us", handler.Host);
    }

    [Theory]
    [InlineData("""{"id":"fulcrum-4445","number":4446}""")]
    [InlineData("""{"id":"other-id","number":4445}""")]
    [InlineData("""{"id":"fulcrum-4445","number":"4445"}""")]
    public async Task Mismatched_or_invalid_Fulcrum_quote_stays_plain_text(string response)
    {
        using var handler = new QuoteHandler(response);
        using var http = new HttpClient(handler);
        Assert.Null(await CreateResolver(http).ResolveAsync("fulcrum-4445", 4445, default));
    }

    [Fact]
    public async Task Missing_quote_or_non_Fulcrum_provider_stays_plain_text()
    {
        using var missing = new QuoteHandler("{}", HttpStatusCode.NotFound);
        using var missingHttp = new HttpClient(missing);
        Assert.Null(await CreateResolver(missingHttp).ResolveAsync("fulcrum-4445", 4445, default));

        using var other = new QuoteHandler("""{"id":"fulcrum-4445","number":4445}""");
        using var otherHttp = new HttpClient(other);
        Assert.Null(await CreateResolver(otherHttp, provider: EnterpriseProviderNames.Acumatica)
            .ResolveAsync("fulcrum-4445", 4445, default));
        Assert.Null(other.Path);
    }

    [Fact]
    public async Task Unconfigured_or_unsafe_browser_template_stays_plain_text_without_calling_Fulcrum()
    {
        using var handler = new QuoteHandler("""{"id":"fulcrum-4445","number":4445}""");
        using var http = new HttpClient(handler);
        Assert.Null(await CreateResolver(http, template: "").ResolveAsync("fulcrum-4445", 4445, default));
        Assert.Null(await CreateResolver(http, template: "javascript:alert({id})").ResolveAsync("fulcrum-4445", 4445, default));
        Assert.Null(await CreateResolver(http, template: "https://tenant.fulcrumpro.us/quotes")
            .ResolveAsync("fulcrum-4445", 4445, default));
        Assert.Null(await CreateResolver(http, template: "https://tenant.fulcrumpro.us/quotes/{id}/{unknown}")
            .ResolveAsync("fulcrum-4445", 4445, default));
        Assert.Null(handler.Path);
    }

    [Fact]
    public void Browser_URL_encodes_identifiers_and_rejects_non_https_or_embedded_credentials()
    {
        Assert.Equal("https://son-aero.fulcrumpro.com/ui/quotes/0123456789abcdef01234567/details",
            FulcrumQuoteLinkResolver.BuildRecordUrl(Template, "0123456789abcdef01234567", 4445));
        Assert.Equal("https://tenant.fulcrumpro.us/quotes/id%2Fwith%20space?number=4445",
            FulcrumQuoteLinkResolver.BuildRecordUrl(
                "https://tenant.fulcrumpro.us/quotes/{id}?number={quoteNumber}", "id/with space", 4445));
        Assert.Null(FulcrumQuoteLinkResolver.BuildRecordUrl("http://tenant.fulcrumpro.us/quotes/{id}", "id", 4445));
        Assert.Null(FulcrumQuoteLinkResolver.BuildRecordUrl("https://user@tenant.fulcrumpro.us/quotes/{id}", "id", 4445));
    }

    private static FulcrumQuoteLinkResolver CreateResolver(HttpClient http, string? template = Template,
        string provider = EnterpriseProviderNames.Fulcrum)
    {
        var options = Options.Create(new FulcrumQuoteSyncOptions { QuoteUrlTemplate = template ?? string.Empty });
        var client = new FulcrumQuoteGenerationClient(http, options, new Credentials());
        return new(client, new Provider(provider), options, NullLogger<FulcrumQuoteLinkResolver>.Instance);
    }

    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class Provider(string name) : IEnterpriseProviderSource
    {
        public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) => Task.FromResult(name);
    }

    private sealed class QuoteHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Host { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Host = request.RequestUri?.Host;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response) });
        }
    }
}

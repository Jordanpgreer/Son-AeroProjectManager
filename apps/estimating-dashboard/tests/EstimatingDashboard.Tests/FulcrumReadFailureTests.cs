using System.Data.Common;
using System.Net;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumReadFailureTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Upstream_status_and_fixed_endpoint_survive_without_private_data(int status)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("private-response-body") }));
        var client = Client(http);
        await client.InitializeAsync(default);

        var error = await Assert.ThrowsAsync<FulcrumReadFailureException>(() =>
            client.GetAsync("api/quotes/private-record-id?private-query=private-value", default));

        Assert.Equal(status, error.UpstreamStatusCode);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(FulcrumReadFailureKind.UpstreamResponse, error.Kind);
        Assert.Equal("quote details", error.OperationLabel);
        Assert.Equal("GET /api/quotes/{quoteId}", error.EndpointTemplate);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Theory]
    [InlineData(FulcrumReadDeferralReason.Cooldown, "Cooldown")]
    [InlineData(FulcrumReadDeferralReason.CredentialRejected, "CredentialRejected")]
    [InlineData(FulcrumReadDeferralReason.PermissionRejected, "PermissionRejected")]
    public async Task Local_deferral_is_distinguished_from_an_upstream_response(
        FulcrumReadDeferralReason reason, string kind)
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
        using var http = new HttpClient(new Handler(_ => throw new FulcrumReadDeferredException(reason, retryAt)));
        var client = Client(http);
        await client.InitializeAsync(default);

        var error = await Assert.ThrowsAsync<FulcrumReadFailureException>(() => client.GetAsync("api/quotes/private-id", default));

        Assert.True(error.IsDeferred);
        Assert.Equal(kind, error.Kind.ToString());
        Assert.Equal(retryAt, error.RetryAt);
        Assert.Null(error.UpstreamStatusCode);
        Assert.Null(error.InnerException);
        Assert.Equal("GET /api/quotes/{quoteId}", error.EndpointTemplate);
    }

    [Fact]
    public async Task Network_failure_does_not_copy_the_raw_exception()
    {
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("private host and credential")));
        var client = Client(http);
        await client.InitializeAsync(default);

        var error = await Assert.ThrowsAsync<FulcrumReadFailureException>(() => client.GetAsync("api/vendors/private-id", default));

        Assert.Equal(FulcrumReadFailureKind.Network, error.Kind);
        Assert.Null(error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task Coordination_database_exception_remains_distinct()
    {
        var original = new FulcrumCoordinationUnavailableException(new DatabaseFailure());
        using var http = new HttpClient(new Handler(_ => throw original));
        var client = Client(http);
        await client.InitializeAsync(default);

        var error = await Assert.ThrowsAsync<FulcrumCoordinationUnavailableException>(() => client.GetAsync("api/quotes/id", default));

        Assert.Same(original, error);
    }

    [Fact]
    public async Task Other_database_errors_are_not_reclassified_as_upstream_failures()
    {
        var original = new DatabaseFailure();
        using var http = new HttpClient(new Handler(_ => throw original));
        var client = Client(http);
        await client.InitializeAsync(default);

        var error = await Assert.ThrowsAsync<DatabaseFailure>(() => client.GetAsync("api/quotes/id", default));

        Assert.Same(original, error);
    }

    [Fact]
    public async Task Caller_cancellation_wins_over_local_deferral()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(_ =>
        {
            cancellation.Cancel();
            throw new FulcrumReadDeferredException(FulcrumReadDeferralReason.Cooldown, DateTimeOffset.UtcNow.AddSeconds(30));
        }));
        var client = Client(http);
        await client.InitializeAsync(default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("api/quotes/id", cancellation.Token));
    }

    [Theory]
    [InlineData("api/quotes/private-quote/part-line-items/private-line/make-summary", "GET /api/quotes/{quoteId}/part-line-items/{lineId}/make-summary")]
    [InlineData("api/items/private-item/purchase-history?months=60", "GET /api/items/{itemId}/purchase-history")]
    [InlineData("api/unknown/private-path?secret=value", "Fulcrum API read")]
    public void Endpoint_labels_are_allowlisted_and_never_echo_unknown_paths(string path, string expected)
    {
        var error = FulcrumReadFailureException.FromResponse(HttpMethod.Get, path, HttpStatusCode.Forbidden);
        Assert.Equal(expected, error.EndpointTemplate);
        Assert.DoesNotContain("private", error.EndpointTemplate);
        Assert.DoesNotContain("secret", error.EndpointTemplate);
    }

    private static FulcrumQuoteGenerationClient Client(HttpClient http) =>
        new(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials());

    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("synthetic-secret");
    }
    private sealed class DatabaseFailure() : DbException("private database error");
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}

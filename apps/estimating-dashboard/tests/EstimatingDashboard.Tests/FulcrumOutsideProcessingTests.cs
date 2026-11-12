using System.Net;
using System.Net.Http.Json;
using System.Text;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumOutsideProcessingTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"itemId\":\"top\"}]")]
    public async Task Missing_make_routing_cannot_prove_that_no_outside_processing_is_required(string summary)
    {
        using var http = new HttpClient(new Handler { Summary = summary });
        var review = await Service(http).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [100] }, default);

        Assert.Empty(review.Operations);
        Assert.Contains(review.Warnings, warning => warning.Contains("routing is missing"));
        Assert.False(QuoteRfqReadiness.OutsideProcessingSent(review, null, [], out _));
    }

    [Fact]
    public async Task Missing_nested_make_routing_also_blocks_automatic_readiness()
    {
        using var http = new HttpClient(new Handler { Nested = true });
        var review = await Service(http).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [100] }, default);

        Assert.Contains(review.Warnings, warning => warning.StartsWith("CHILD:") && warning.Contains("routing is missing"));
        Assert.False(QuoteRfqReadiness.OutsideProcessingSent(review, null, [], out _));
    }

    [Fact]
    public async Task Verified_routing_with_no_op_operations_remains_valid()
    {
        using var http = new HttpClient(new Handler());
        var review = await Service(http).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [100] }, default);

        Assert.Empty(review.Operations);
        Assert.Empty(review.Warnings);
        Assert.True(QuoteRfqReadiness.OutsideProcessingSent(review, null, [], out var required));
        Assert.Equal(0, required);
    }

    private static FulcrumOutsideProcessingService Service(HttpClient http) =>
        new(new(http, Options.Create(new FulcrumQuoteSyncOptions()), new Credentials()), TimeProvider.System);

    private sealed class Credentials : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken ct) =>
            Task.FromResult<string?>("fixture-key");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Summary { get; init; } = "[{\"itemId\":\"top\",\"routingId\":\"route\"}]";
        public bool Nested { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string line = "/api/quotes/quote/part-line-items/line";
            var path = request.RequestUri!.AbsolutePath;
            if (path == line + "/make-summary")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(Summary, Encoding.UTF8, "application/json") });
            object body = path switch
            {
                "/api/quotes/quote" => new { number = 123 },
                "/api/quotes/quote/part-line-items/list" => new[] { new { id = "line", itemId = "top", quantity = 100 } },
                "/api/items/top" => new { number = "TOP", itemOrigin = "make" },
                "/api/items/child" => new { number = "CHILD", itemOrigin = "make" },
                line + "/routing/route/operations/list" => Array.Empty<object>(),
                line + "/routing/route/input-items/list" => Nested
                    ? new object[] { new { id = "input", itemId = "child", valueType = "requires", valueTypeUnits = 1 } } : [],
                _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}

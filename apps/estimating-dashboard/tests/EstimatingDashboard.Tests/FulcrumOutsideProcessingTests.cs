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
            new Dictionary<string, IReadOnlyList<decimal>>(), default);

        Assert.Empty(review.Operations);
        Assert.Empty(review.Warnings);
        Assert.True(QuoteRfqReadiness.OutsideProcessingSent(review, null, [], out var required));
        Assert.Equal(0, required);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_pricing_tiers_reach_top_level_and_subassembly_email_drafts(bool legacyConfirmation)
    {
        using var http = new HttpClient(TierHandler(QuoteLine("line", "top", 100, [500, 200, 100])));
        var selections = new Dictionary<string, IReadOnlyList<decimal>>();
        if (legacyConfirmation) selections["top"] = [100, 500];

        var review = await Service(http).InspectAsync(7, 123, "quote", selections, default);

        Assert.Empty(review.Warnings);
        var main = Assert.Single(review.Operations, operation => operation.ItemId == "top");
        var child = Assert.Single(review.Operations, operation => operation.ItemId == "child");
        Assert.Equal(new[] { 100m, 200m, 500m }, main.QuoteQuantities);
        Assert.Equal(new[] { 50m, 100m, 250m }, child.QuoteQuantities);
        Assert.Equal(50m, child.RequiredMinimum);
        Assert.Equal(250m, child.RequiredMaximum);
        var draft = OutsideProcessingRfqDraft.Create(123, child, new string('b', 32));
        Assert.Contains("Quantity: 50 / 100 / 250", draft.PlainText);
    }

    [Fact]
    public async Task Saved_tiers_replace_incomplete_Fulcrum_data_and_keep_fractional_totals()
    {
        using var http = new HttpClient(TierHandler(QuoteLine("line", "top", 100), childUsage: .25m, fixedUnits: .2m));
        var service = Service(http);
        var unconfirmed = await service.InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>>(), default);
        Assert.Empty(unconfirmed.Warnings);
        var warning = Assert.Single(unconfirmed.QuantityWarnings!);
        Assert.Contains("Pricing-panel breaks", warning);
        Assert.All(unconfirmed.Operations, operation =>
        {
            Assert.Equal("review-required", operation.Status);
            Assert.Contains(warning, operation.Issues);
        });

        var confirmed = await service.InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>> { ["top"] = [500, 101, 200, 101] }, default);

        Assert.Empty(confirmed.Warnings);
        var child = Assert.Single(confirmed.Operations, operation => operation.ItemId == "child");
        Assert.Equal(new[] { 25.45m, 50.2m, 125.2m }, child.QuoteQuantities);
    }

    [Fact]
    public async Task Missing_pricing_tiers_only_block_operations_that_use_the_unconfirmed_root()
    {
        using var http = new HttpClient(TierHandler(QuoteLine("line", "top", 100, [200, 500]),
            QuoteLine("second", "second", 10)));

        var review = await Service(http).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>>(), default);

        Assert.Empty(review.Warnings);
        var warning = Assert.Single(review.QuantityWarnings!);
        Assert.StartsWith("SECOND:", warning);
        var ready = Assert.Single(review.Operations, operation => operation.ItemId == "top");
        Assert.Equal("needs-rfq", ready.Status);
        Assert.Empty(ready.Issues);
        var shared = Assert.Single(review.Operations, operation => operation.ItemId == "child");
        Assert.Equal("review-required", shared.Status);
        Assert.Equal(warning, Assert.Single(shared.Issues));
    }

    [Fact]
    public async Task Shared_child_OP_sums_repeat_paths_and_aligns_each_root_by_tier()
    {
        using var http = new HttpClient(TierHandler(QuoteLine("line", "top", 100, [200, 500]),
            QuoteLine("second", "second", 1, [2, 3]), repeatedChild: true));
        var review = await Service(http).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>>(), default);

        Assert.Empty(review.Warnings);
        var child = Assert.Single(review.Operations, operation => operation.ItemId == "child");
        Assert.Equal(new[] { 75.5m, 151m, 376.5m }, child.QuoteQuantities);
    }

    [Fact]
    public async Task A_shared_OP_broadcasts_a_single_tier_but_requires_review_for_mismatched_multi_tier_roots()
    {
        var first = QuoteLine("line", "top", 100, [200, 500]);
        using var singleHttp = new HttpClient(TierHandler(first, QuoteLine("second", "second", 10, [])));
        var single = await Service(singleHttp).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>>(), default);
        Assert.Equal(new[] { 55m, 105m, 255m },
            Assert.Single(single.Operations, operation => operation.ItemId == "child").QuoteQuantities);

        using var mismatchedHttp = new HttpClient(TierHandler(first, QuoteLine("second", "second", 10, [20])));
        var mismatch = await Service(mismatchedHttp).InspectAsync(7, 123, "quote",
            new Dictionary<string, IReadOnlyList<decimal>>(), default);
        var child = Assert.Single(mismatch.Operations, operation => operation.ItemId == "child");
        Assert.Equal("review-required", child.Status);
        Assert.Empty(child.QuoteQuantities);
        Assert.Contains("different numbers of quantity tiers", Assert.Single(child.Issues));
    }

    private static object QuoteLine(string id, string itemId, decimal quantity, decimal[]? breaks = null) =>
        breaks is null ? new { id, itemId, quantity }
            : new { id, itemId, quantity, priceBreaks = breaks.Select(value => new { quantity = value }).ToArray() };

    private static Handler TierHandler(object firstLine, object? secondLine = null,
        decimal childUsage = .5m, decimal fixedUnits = 0, bool repeatedChild = false)
    {
        const string quoteLines = "/api/quotes/quote/part-line-items/";
        var operation = new { id = "outside", name = "OP Heat Treat", isOutsideProcessing = true,
            outsideProcessingOperation = new { selectedVendorId = "vendor" } };
        var primaryInputs = new List<object> { new { id = "input", itemId = "child", valueType = "requires",
            valueTypeUnits = childUsage, fixedUnits } };
        if (repeatedChild)
            primaryInputs.Add(new { id = "additional", itemId = "child", valueType = "requires", valueTypeUnits = .25m });
        var responses = new Dictionary<string, object> {
            [quoteLines + "list"] = secondLine is null ? new[] { firstLine } : new[] { firstLine, secondLine },
            [quoteLines + "line/make-summary"] = new[] { new { itemId = "top", routingId = "route" }, new { itemId = "child", routingId = "childRoute" } },
            [quoteLines + "line/routing/route/operations/list"] = new[] { operation },
            [quoteLines + "line/routing/route/input-items/list"] = primaryInputs,
            [quoteLines + "line/routing/childRoute/operations/list"] = new[] { operation },
            [quoteLines + "line/routing/childRoute/input-items/list"] = Array.Empty<object>(),
            ["/api/vendors/vendor"] = new { active = true, name = "OP Vendor" },
            ["/api/vendors/vendor/contacts/list"] = new[] { new { email = "op@example.com" } }
        };
        if (secondLine is not null)
        {
            responses["/api/items/second"] = new { number = "SECOND", itemOrigin = "make" };
            responses[quoteLines + "second/make-summary"] = new[] {
                new { itemId = "second", routingId = "rootRoute" }, new { itemId = "child", routingId = "sharedRoute" } };
            responses[quoteLines + "second/routing/rootRoute/operations/list"] = Array.Empty<object>();
            responses[quoteLines + "second/routing/rootRoute/input-items/list"] = new[] {
                new { id = "second-input", itemId = "child", valueType = "requires", valueTypeUnits = .5m } };
            responses[quoteLines + "second/routing/sharedRoute/operations/list"] = new[] { operation };
            responses[quoteLines + "second/routing/sharedRoute/input-items/list"] = Array.Empty<object>();
        }
        return new Handler { Responses = responses };
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
        public IReadOnlyDictionary<string, object> Responses { get; init; } = new Dictionary<string, object>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string line = "/api/quotes/quote/part-line-items/line";
            var path = request.RequestUri!.AbsolutePath;
            if (Responses.TryGetValue(path, out var response))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
            if (path == line + "/make-summary")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(Summary, Encoding.UTF8, "application/json") });
            object body = path switch
            {
                "/api/quotes/quote" => new { number = 123 },
                "/api/quotes/quote/part-line-items/list" => new[] { new { id = "line", itemId = "top", quantity = 100 } },
                "/api/items/top" => new { number = "TOP", itemOrigin = "make" },
                "/api/items/top/routing/operations/list" => new[] { new { id = "cut", name = "Cut" } },
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

using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Integrations;

namespace ProjectTracker.Tests;

public sealed class ProjectQuantityProviderTests
{
    [Fact]
    public void Adapter_selector_uses_provider_and_data_route()
    {
        IProjectQuantityProvider[] providers =
        [
            new StubQuantityProvider(EnterpriseProviderNames.Fulcrum),
            new StubQuantityProvider(EnterpriseProviderNames.Acumatica)
        ];

        var selected = EnterpriseAdapterSelector.Select(
            providers,
            "acumatica",
            EnterpriseDataRoutes.ProjectQuantities);

        Assert.Equal(EnterpriseProviderNames.Acumatica, selected.ProviderName);
    }

    [Fact]
    public async Task Acumatica_slot_fails_safely_until_mapping_is_configured()
    {
        var provider = new AcumaticaProjectQuantityProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.PullAsync(new Project(), default));

        Assert.Contains("not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PullAsync_UsesLinkedFulcrumJobAndSalesOrderLineQuantities()
    {
        var handler = new FulcrumHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/items/list/v2")
            {
                return Json("""
                    [{"id":"item-id","number":"PN-100","description":"Test part","isArchived":false}]
                    """);
            }
            if (request.RequestUri!.AbsolutePath == "/api/jobs/list")
            {
                return Json("""
                    [{
                      "id": "job-id",
                      "number": 123,
                      "name": "Job 123",
                      "quantityToMake": 8.5,
                      "salesOrderId": "sales-order-id",
                      "salesOrderLineItemId": "line-id",
                      "status": "engineering"
                    }]
                    """);
            }
            if (request.RequestUri.AbsolutePath == "/api/sales-orders/list")
            {
                return Json("""
                    [{"id":"sales-order-id","number":456,"status":"inProgress"}]
                    """);
            }
            if (request.RequestUri.AbsolutePath == "/api/sales-orders/sales-order-id/part-line-items/line-id")
            {
                return Json("""
                    {"id":"line-id","name":"PN-100","quantity":10.25,"customerPartNumber":null}
                    """);
            }
            if (request.RequestUri.AbsolutePath == "/api/jobs/job-id/operations/list")
            {
                return Json("""
                    [
                      {"itemToMake":{"id":"top-level","itemId":"item-id","depth":0},"operation":{"id":"op-inspect","order":20,"name":"Final Inspection"}},
                      {"itemToMake":{"id":"subassembly","itemId":"other-item-id","depth":1},"operation":{"id":"op-sub","order":5,"name":"Subassembly Step"}},
                      {"itemToMake":{"id":"top-level","itemId":"item-id","depth":0},"operation":{"id":"op-saw","order":10,"name":"Saw"}}
                    ]
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var provider = CreateProvider(handler, "saved-token");
        var project = new Project
        {
            ProgramName = "PN-100",
            JobNumber = "123",
            SalesOrderNumber = "456"
        };

        var result = await provider.PullAsync(project, CancellationToken.None);

        Assert.Equal(8.5m, result.JobQuantity);
        Assert.Equal(10.25m, result.RequiredQuantity);
        Assert.True(result.MatchConfirmed);
        Assert.Empty(result.Warnings);
        Assert.Collection(
            result.ConfirmedRoutingSteps,
            step =>
            {
                Assert.Equal("op-saw", step.ExternalId);
                Assert.Equal(10, step.Sequence);
                Assert.Equal("Saw", step.Name);
            },
            step =>
            {
                Assert.Equal("op-inspect", step.ExternalId);
                Assert.Equal(20, step.Sequence);
                Assert.Equal("Final Inspection", step.Name);
            });
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer saved-token", request.Authorization));
        Assert.All(handler.Requests, request => Assert.Equal("api.fulcrumpro.us", request.RequestUri.Host));
        Assert.All(handler.Requests, request => Assert.DoesNotContain("Sort.", request.RequestUri.Query, StringComparison.Ordinal));
        Assert.Contains("\"numbers\":[123]", handler.Requests.Single(request => request.RequestUri.AbsolutePath == "/api/jobs/list").Body, StringComparison.Ordinal);
        Assert.Contains("\"numbers\":[456]", handler.Requests.Single(request => request.RequestUri.AbsolutePath == "/api/sales-orders/list").Body, StringComparison.Ordinal);
        var routingRequest = handler.Requests.Single(request => request.RequestUri.AbsolutePath == "/api/jobs/job-id/operations/list");
        Assert.Equal(HttpMethod.Post, routingRequest.Method);
        Assert.Equal(string.Empty, routingRequest.Body);
    }

    [Fact]
    public async Task PullAsync_DoesNotReturnQuantitiesWhenJobAndSalesOrderAreNotLinked()
    {
        var handler = new FulcrumHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/items/list/v2" => Json("""
                [{"id":"item-id","number":"PN-100","description":"Test part","isArchived":false}]
                """),
            "/api/jobs/list" => Json("""
                [{"id":"job-id","number":123,"name":"PN-100","quantityToMake":8.5,"salesOrderId":"different-order-id","salesOrderLineItemId":"line-id","status":"engineering"}]
                """),
            "/api/sales-orders/list" => Json("""
                [{"id":"sales-order-id","number":456,"status":"inProgress"}]
                """),
            _ => throw new InvalidOperationException("The linked line must not be requested for a mismatched sales order.")
        });
        var provider = CreateProvider(handler, "saved-token");

        var result = await provider.PullAsync(new Project
        {
            ProgramName = "PN-100",
            JobNumber = "123",
            SalesOrderNumber = "456"
        }, default);

        Assert.False(result.MatchConfirmed);
        Assert.Null(result.JobQuantity);
        Assert.Null(result.RequiredQuantity);
        Assert.Contains(result.Warnings, warning => warning.Contains("not linked", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PullAsync_DoesNotReturnQuantitiesWhenLinkedLineHasAnotherPartNumber()
    {
        var handler = new FulcrumHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/items/list/v2" => Json("""
                [{"id":"item-id","number":"PN-100","description":"Test part","isArchived":false}]
                """),
            "/api/jobs/list" => Json("""
                [{"id":"job-id","number":123,"name":"PN-OTHER","quantityToMake":8.5,"salesOrderId":"sales-order-id","salesOrderLineItemId":"line-id","status":"engineering"}]
                """),
            "/api/sales-orders/list" => Json("""
                [{"id":"sales-order-id","number":456,"status":"inProgress"}]
                """),
            "/api/sales-orders/sales-order-id/part-line-items/line-id" => Json("""
                {"id":"line-id","name":"PN-OTHER","quantity":10.25,"customerPartNumber":null}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var provider = CreateProvider(handler, "saved-token");

        var result = await provider.PullAsync(new Project
        {
            ProgramName = "PN-100",
            JobNumber = "123",
            SalesOrderNumber = "456"
        }, default);

        Assert.False(result.MatchConfirmed);
        Assert.Null(result.JobQuantity);
        Assert.Null(result.RequiredQuantity);
        Assert.Contains(result.Warnings, warning => warning.Contains("does not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PullAsync_ReturnsWarningsAndNoValuesWhenProjectIdentifiersAreMissing()
    {
        var handler = new FulcrumHandler(_ => throw new InvalidOperationException("No request was expected."));
        var provider = CreateProvider(handler, "saved-token");

        var result = await provider.PullAsync(
            new Project { ProgramName = "PN-100" },
            CancellationToken.None);

        Assert.Null(result.JobQuantity);
        Assert.Null(result.RequiredQuantity);
        Assert.False(result.MatchConfirmed);
        Assert.Contains(result.Warnings, warning => warning.Contains("job number", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("sales order number", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PullAsync_ReportsMissingSavedCredentialBeforeCallingFulcrum()
    {
        var handler = new FulcrumHandler(_ => throw new InvalidOperationException("No request was expected."));
        var provider = CreateProvider(handler, null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PullAsync(
            new Project { ProgramName = "PN-100", JobNumber = "123", SalesOrderNumber = "456" },
            CancellationToken.None));

        Assert.Contains("Admin Hub", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchAsync_ReturnsPartialActiveJobsWithLinkedItemAndSalesOrder()
    {
        var handler = new FulcrumHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/jobs/list" => Json("""
                [
                  {"id":"job-id","number":1234,"name":"Alpha production","quantityToMake":8.5,"parentItemId":"item-id","salesOrderId":"sales-order-id","salesOrderLineItemId":"line-id","status":"engineering"},
                  {"id":"other-job-id","number":456,"name":"PN-OTHER","status":"complete"}
                ]
                """),
            "/api/items/list/v2" => Json("""
                [{"id":"item-id","number":"PN-100","description":"Test part","isArchived":false}]
                """),
            "/api/sales-orders/list" => Json("""
                [{"id":"sales-order-id","number":7890,"status":"inProgress"}]
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var provider = CreateProvider(handler, "saved-token");

        var result = await provider.SearchAsync(ProjectQuantityLookupKind.Job, "PN-10", default);

        var match = Assert.Single(result);
        Assert.Equal("job-id", match.ExternalId);
        Assert.Equal("1234", match.Number);
        Assert.Equal("PN-100", match.PartNumber);
        Assert.Equal("7890", match.SalesOrderNumber);
        Assert.Equal(8.5m, match.JobQuantity);
        Assert.Equal("engineering", match.Status);
        var jobRequest = handler.Requests.Single(request => request.RequestUri.AbsolutePath == "/api/jobs/list");
        Assert.DoesNotContain("Sort.", jobRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("\"statuses\"", jobRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_DoesNotOfferCompletedSalesOrders()
    {
        var handler = new FulcrumHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/sales-orders/list" => Json("""
                [{"id":"sales-order-id","number":456,"status":"complete"}]
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var provider = CreateProvider(handler, "saved-token");

        var result = await provider.SearchAsync(ProjectQuantityLookupKind.SalesOrder, "456", default);

        Assert.Empty(result);
        Assert.DoesNotContain("Sort.", handler.Requests[0].RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ReturnsPartialItemMatchesFromTheCachedCatalog()
    {
        var handler = new FulcrumHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/items/list/v2" => Json("""
                [
                  {"id":"item-id","number":"PN-100","description":"Main bracket","isArchived":false},
                  {"id":"archived-id","number":"PN-101","description":"Old bracket","isArchived":true}
                ]
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var provider = CreateProvider(handler, "saved-token");

        var first = await provider.SearchAsync(ProjectQuantityLookupKind.Item, "brack", default);
        var second = await provider.SearchAsync(ProjectQuantityLookupKind.Item, "PN-1", default);

        var match = Assert.Single(first);
        Assert.Equal("PN-100", match.PartNumber);
        Assert.Single(second);
        Assert.Single(handler.Requests);
    }

    private static FulcrumProjectQuantityProvider CreateProvider(FulcrumHandler handler, string? token) =>
        new(
            new HttpClient(handler),
            Options.Create(new ProjectQuantitySyncOptions()),
            new CredentialReader(token),
            new FulcrumProjectLookupCatalog());

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CredentialReader(string? token) : IProjectTrackerIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken) =>
            Task.FromResult(token);
    }

    private sealed class StubQuantityProvider(string providerName) : IProjectQuantityProvider
    {
        public string ProviderName { get; } = providerName;
        public string RouteName => EnterpriseDataRoutes.ProjectQuantities;

        public Task<ProjectQuantitySnapshot> PullAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectQuantitySnapshot(null, null, []));

        public Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(
            ProjectQuantityLookupKind kind,
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectQuantityLookupOption>>([]);
    }

    private sealed class FulcrumHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri RequestUri,
        string? Authorization,
        string Body);
}

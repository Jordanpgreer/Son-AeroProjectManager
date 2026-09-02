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
            if (request.RequestUri!.AbsolutePath == "/api/jobs/list")
            {
                return Json("""
                    [{
                      "id": "job-id",
                      "number": 123,
                      "name": "Job 123",
                      "quantityToMake": 8.5,
                      "salesOrderId": "sales-order-id",
                      "salesOrderLineItemId": "line-id"
                    }]
                    """);
            }
            if (request.RequestUri.AbsolutePath == "/api/sales-orders/sales-order-id/part-line-items/line-id")
            {
                return Json("""
                    {"id":"line-id","name":"PN-100","quantity":10.25,"customerPartNumber":null}
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
        Assert.Empty(result.Warnings);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer saved-token", request.Authorization));
        Assert.All(handler.Requests, request => Assert.Equal("api.fulcrumpro.us", request.RequestUri.Host));
        Assert.Contains("\"numbers\":[123]", handler.Requests[0].Body, StringComparison.Ordinal);
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
            new Project { ProgramName = "PN-100", JobNumber = "123" },
            CancellationToken.None));

        Assert.Contains("Admin Hub", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private static FulcrumProjectQuantityProvider CreateProvider(FulcrumHandler handler, string? token) =>
        new(
            new HttpClient(handler),
            Options.Create(new ProjectQuantitySyncOptions()),
            new CredentialReader(token));

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

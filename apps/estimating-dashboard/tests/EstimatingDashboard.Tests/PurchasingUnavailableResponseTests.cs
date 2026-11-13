using System.Data.Common;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class PurchasingUnavailableResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordination_outage_returns_safe_temporary_response(bool outsideProcessing)
    {
        var context = Context();
        var failure = new FulcrumCoordinationUnavailableException(new DatabaseFailure());

        var result = await Filter(outsideProcessing)(context, _ => throw failure);

        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var error = Assert.IsType<ErrorDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(outsideProcessing ? "OpDataUnavailable" : "MaterialDataUnavailable", error.Code);
        Assert.Contains("temporarily unavailable", error.Message);
        Assert.DoesNotContain("private database details", error.Message);
        Assert.DoesNotContain("permissions", error.Message);
        Assert.Equal("private, no-store", context.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_upstream_failure_keeps_existing_bad_gateway_response(bool outsideProcessing)
    {
        var result = await Filter(outsideProcessing)(Context(), _ => throw new HttpRequestException());
        Assert.Equal(502, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Business_database_failures_are_not_suppressed_or_mislabeled(bool outsideProcessing)
    {
        var failure = new DatabaseFailure();
        var actual = await Assert.ThrowsAsync<DatabaseFailure>(async () =>
            await Filter(outsideProcessing)(Context(), _ => throw failure));
        Assert.Same(failure, actual);

        var saveFailure = new DbUpdateException("Business save failed", failure);
        var actualSave = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await Filter(outsideProcessing)(Context(), _ => throw saveFailure));
        Assert.Same(saveFailure, actualSave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_remains_cancellation(bool outsideProcessing)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = Context();
        context.HttpContext.RequestAborted = cancellation.Token;
        var failure = new OperationCanceledException(cancellation.Token);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Filter(outsideProcessing)(context, _ => throw failure));
        Assert.Same(failure, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_header_guard_still_precedes_provider_access(bool outsideProcessing)
    {
        var context = Context();
        context.HttpContext.Request.Method = "POST";
        var called = false;
        var result = await Filter(outsideProcessing)(context, _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(null);
        });
        Assert.False(called);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private static DefaultEndpointFilterInvocationContext Context()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        return new(http);
    }

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Filter(bool outsideProcessing)
        => outsideProcessing ? OutsideProcessingEndpoints.ReviewFilter : MaterialPurchasingEndpoints.ReviewFilter;

    private sealed class DatabaseFailure() : DbException("private database details");
}

using System.Data.Common;
using System.Net;
using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    public static IEnumerable<object[]> UpstreamResponses()
    {
        var cases = new[]
        {
            (400, "FulcrumRequestRejected", 502),
            (401, "FulcrumAuthenticationRejected", 502),
            (403, "FulcrumPermissionDenied", 502),
            (404, "FulcrumRecordNotFound", 502),
            (429, "FulcrumRateLimited", 503),
            (500, "FulcrumUpstreamFailure", 502),
            (503, "FulcrumTemporarilyUnavailable", 503)
        };
        foreach (var outsideProcessing in new[] { false, true })
        foreach (var (status, code, responseStatus) in cases)
            yield return new object[] { outsideProcessing, status, code, responseStatus };
    }

    [Theory]
    [MemberData(nameof(UpstreamResponses))]
    public async Task Upstream_status_is_preserved_in_safe_response_and_warning(
        bool outsideProcessing, int upstreamStatus, string code, int responseStatus)
    {
        var logger = new CaptureLogger();
        using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logger)).BuildServiceProvider();
        var context = Context();
        context.HttpContext.RequestServices = services;
        context.HttpContext.TraceIdentifier = "synthetic-trace";
        var failure = FulcrumReadFailureException.FromResponse(HttpMethod.Get,
            "api/quotes/private-record-id?private-query=secret", (HttpStatusCode)upstreamStatus);

        var result = await Filter(outsideProcessing)(context, _ => throw failure);

        Assert.Equal(responseStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var error = Assert.IsType<PurchasingFailureResponse.DiagnosticError>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(code, error.Code);
        Assert.Contains($"HTTP {upstreamStatus}", error.Message);
        Assert.Equal(upstreamStatus, error.UpstreamStatusCode);
        Assert.Equal("quote details", error.Operation);
        Assert.Equal("GET /api/quotes/{quoteId}", error.Endpoint);
        Assert.Equal("UpstreamResponse", error.FailureKind);
        Assert.Equal("synthetic-trace", error.TraceIdentifier);
        Assert.Null(error.RetryAt);
        Assert.DoesNotContain("private-record-id", JsonSerializer.Serialize(error));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(error));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Arda.Fulcrum.Purchasing", entry.Category);
        Assert.Equal(4201, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("GET /api/quotes/{quoteId}", entry.Message);
        Assert.Contains($"upstream status {upstreamStatus}", entry.Message);
        Assert.Contains("synthetic-trace", entry.Message);
        Assert.DoesNotContain("private-record-id", entry.Message);
        Assert.DoesNotContain("secret", entry.Message);
    }

    [Theory]
    [InlineData(false, FulcrumReadDeferralReason.Cooldown, "FulcrumReadPaused")]
    [InlineData(true, FulcrumReadDeferralReason.Cooldown, "FulcrumReadPaused")]
    [InlineData(false, FulcrumReadDeferralReason.CredentialRejected, "FulcrumCredentialCheckPaused")]
    [InlineData(true, FulcrumReadDeferralReason.CredentialRejected, "FulcrumCredentialCheckPaused")]
    [InlineData(false, FulcrumReadDeferralReason.PermissionRejected, "FulcrumPermissionCheckPaused")]
    [InlineData(true, FulcrumReadDeferralReason.PermissionRejected, "FulcrumPermissionCheckPaused")]
    public async Task Local_pause_is_not_reported_as_a_new_upstream_rejection(
        bool outsideProcessing, FulcrumReadDeferralReason reason, string code)
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
        var failure = FulcrumReadFailureException.FromDeferred(HttpMethod.Get, "api/quotes/private-id",
            new FulcrumReadDeferredException(reason, retryAt));

        var result = await Filter(outsideProcessing)(Context(), _ => throw failure);

        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var error = Assert.IsType<PurchasingFailureResponse.DiagnosticError>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(code, error.Code);
        Assert.Equal(reason.ToString(), error.FailureKind);
        Assert.Null(error.UpstreamStatusCode);
        Assert.Equal(retryAt, error.RetryAt);
        Assert.DoesNotContain("HTTP", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_details_are_never_copied_into_response_or_warning(bool outsideProcessing)
    {
        var logger = new CaptureLogger();
        using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logger)).BuildServiceProvider();
        var context = Context();
        context.HttpContext.RequestServices = services;

        var result = await Filter(outsideProcessing)(context, _ => throw new HttpRequestException("private-host secret-token"));

        var error = Assert.IsType<PurchasingFailureResponse.DiagnosticError>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal("FulcrumConnectionFailed", error.Code);
        Assert.Equal("Network", error.FailureKind);
        Assert.Null(error.UpstreamStatusCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("private-host", JsonSerializer.Serialize(error) + entry.Message);
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(error) + entry.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordination_warning_does_not_include_private_database_error(bool outsideProcessing)
    {
        var logger = new CaptureLogger();
        using var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logger)).BuildServiceProvider();
        var context = Context();
        context.HttpContext.RequestServices = services;

        await Filter(outsideProcessing)(context, _ => throw new FulcrumCoordinationUnavailableException(new DatabaseFailure()));

        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains("CoordinationUnavailable", entry.Message);
        Assert.DoesNotContain("private database details", entry.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requested_cancellation_wins_over_a_returned_failure(bool outsideProcessing)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = Context();
        context.HttpContext.RequestAborted = cancellation.Token;
        var failure = FulcrumReadFailureException.FromResponse(HttpMethod.Get, "api/quotes/id", HttpStatusCode.Forbidden);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Filter(outsideProcessing)(context, _ => throw failure));
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

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);
    private sealed class CaptureLogger : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }
        private sealed class Logger(string category, List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Add(new(category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}

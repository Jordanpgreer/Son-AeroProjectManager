using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SonAero.Platform.Diagnostics;

public sealed record ArdaErrorTrackingOptions(string ApplicationId, string ApplicationName);

public interface IArdaErrorSink
{
    Task WriteAsync(ArdaErrorEvent error, CancellationToken cancellationToken);
}

internal sealed class EfArdaErrorSink<TContext>(IServiceScopeFactory scopeFactory) : IArdaErrorSink
    where TContext : DbContext
{
    public async Task WriteAsync(ArdaErrorEvent error, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Set<ArdaErrorEvent>().Add(error);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed partial class ArdaErrorTrackingMiddleware(
    RequestDelegate next,
    IArdaErrorSink sink,
    ArdaErrorTrackingOptions options,
    ILogger<ArdaErrorTrackingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var descriptor = Describe(exception);
            await TryRecordAsync(context, StatusCodes.Status500InternalServerError, descriptor, exception);
            throw;
        }

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await TryRecordAsync(context, context.Response.StatusCode, Describe(context.Response.StatusCode), null);
        }
    }

    private async Task TryRecordAsync(
        HttpContext context,
        int statusCode,
        ErrorDescriptor descriptor,
        Exception? exception)
    {
        var path = ResolvePath(context);
        var error = new ArdaErrorEvent
        {
            ApplicationId = options.ApplicationId,
            ApplicationName = options.ApplicationName,
            ErrorCode = descriptor.Code,
            Explanation = descriptor.Explanation,
            AffectedArea = DescribeAffectedArea(options.ApplicationName, path),
            OccurredAtUtc = DateTime.UtcNow,
            HttpStatusCode = statusCode,
            RequestMethod = Limit(context.Request.Method.ToUpperInvariant(), 12),
            RequestPath = Limit(path, 500),
            TraceIdentifier = Limit(context.TraceIdentifier, 160),
            AccountName = LimitOptional(context.User.Identity?.Name, 160),
            ExceptionType = LimitOptional(exception?.GetType().FullName, 240),
        };

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await sink.WriteAsync(error, timeout.Token);
        }
        catch (Exception writeException)
        {
            logger.LogWarning(
                writeException,
                "Arda error {ErrorCode} for {ApplicationId} could not be written to the error tracker.",
                error.ErrorCode,
                error.ApplicationId);
        }
    }

    private static ErrorDescriptor Describe(int statusCode) => statusCode switch
    {
        StatusCodes.Status500InternalServerError => new(
            "SERVER_ERROR",
            "The application could not complete the request because of an internal server problem."),
        StatusCodes.Status502BadGateway => new(
            "CONNECTED_SERVICE_FAILURE",
            "A connected system or API returned an unusable response."),
        StatusCodes.Status503ServiceUnavailable => new(
            "SERVICE_UNAVAILABLE",
            "A required application or service was temporarily unavailable."),
        StatusCodes.Status504GatewayTimeout => new(
            "OPERATION_TIMEOUT",
            "A connected system took too long to respond."),
        _ => new(
            $"HTTP_{statusCode.ToString(CultureInfo.InvariantCulture)}",
            "The application returned a server error while processing this request."),
    };

    private static ErrorDescriptor Describe(Exception exception) => exception switch
    {
        TimeoutException or TaskCanceledException => new(
            "OPERATION_TIMEOUT",
            "A required operation took too long to complete."),
        HttpRequestException => new(
            "CONNECTED_SERVICE_FAILURE",
            "A connected system or API could not complete the request."),
        DbUpdateException => new(
            "DATABASE_WRITE_FAILURE",
            "The application could not save a requested database change."),
        _ => new(
            "UNHANDLED_EXCEPTION",
            "The application encountered an unexpected internal problem."),
    };

    private static string SanitizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "/";
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => long.TryParse(segment, out _)
                || Guid.TryParse(segment, out _)
                || segment.Length > 24
                    ? "{id}"
                    : segment);
        return "/" + string.Join('/', segments);
    }

    private static string ResolvePath(HttpContext context)
    {
        var routePattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrWhiteSpace(routePattern)) return SanitizePath(context.Request.Path.Value);
        var segments = routePattern.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.StartsWith('{') && segment.EndsWith('}') ? "{id}" : segment);
        return "/" + string.Join('/', segments);
    }

    private static string DescribeAffectedArea(string applicationName, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !string.Equals(segment, "api", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .Select(segment => segment == "{id}" ? "record" : Humanize(segment))
            .ToArray();
        var area = segments.Length == 0 ? "application request" : string.Join(" / ", segments);
        return Limit($"{applicationName} - {area}", 240);
    }

    private static string Humanize(string value)
    {
        var words = SeparatorRegex().Replace(value, " ").Trim();
        if (words.Length == 0) return "request";
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? LimitOptional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value.Trim(), maxLength);

    [GeneratedRegex("[-_]+")]
    private static partial Regex SeparatorRegex();

    private sealed record ErrorDescriptor(string Code, string Explanation);
}

public static class ArdaErrorTrackingExtensions
{
    public static IServiceCollection AddArdaErrorTracking<TContext>(
        this IServiceCollection services,
        string applicationId,
        string applicationName)
        where TContext : DbContext
    {
        services.AddSingleton(new ArdaErrorTrackingOptions(applicationId, applicationName));
        services.AddSingleton<IArdaErrorSink, EfArdaErrorSink<TContext>>();
        services.AddScoped<ArdaErrorSchemaInitializer<TContext>>();
        return services;
    }

    public static IApplicationBuilder UseArdaErrorTracking(this IApplicationBuilder app) =>
        app.UseMiddleware<ArdaErrorTrackingMiddleware>();
}

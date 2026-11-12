using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using SonAero.Platform.Diagnostics;

namespace Portal.Tests;

public sealed class ErrorTrackerTests
{
    [Fact]
    public void Error_tracker_route_requires_an_authenticated_user()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<PortalRoleDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapErrorTrackerEndpoints();

        var route = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/admin/errors") == true);

        Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public void Portal_context_maps_error_events_and_diagnostic_indexes()
    {
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new PortalRoleDbContext(options);
        var entity = db.Model.FindEntityType(typeof(ArdaErrorEvent))!;

        Assert.Equal("ArdaErrorEvents", entity.GetTableName());
        Assert.Equal(nameof(ArdaErrorEvent.Id), entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Contains(entity.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ArdaErrorEvent.ApplicationId), nameof(ArdaErrorEvent.OccurredAtUtc)]));
        Assert.Contains(entity.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ArdaErrorEvent.ErrorCode), nameof(ArdaErrorEvent.OccurredAtUtc)]));
    }

    [Fact]
    public async Task Error_schema_initializer_creates_the_shared_sqlite_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);

        await new ArdaErrorSchemaInitializer<PortalRoleDbContext>(db).InitializeAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND (name = 'ArdaErrorEvents' OR name LIKE 'IX_ArdaErrorEvents%');";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        Assert.Contains("ArdaErrorEvents", names);
        Assert.Contains("IX_ArdaErrorEvents_OccurredAtUtc", names);
        Assert.Contains("IX_ArdaErrorEvents_ApplicationId_OccurredAtUtc", names);
        Assert.Contains("IX_ArdaErrorEvents_ErrorCode_OccurredAtUtc", names);
    }

    [Fact]
    public async Task Middleware_records_server_failures_without_query_strings_or_request_bodies()
    {
        var sink = new RecordingSink();
        var middleware = new ArdaErrorTrackingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return Task.CompletedTask;
            },
            sink,
            new ArdaErrorTrackingOptions("project-tracker", "Project Tracker"),
            NullLogger<ArdaErrorTrackingMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-123",
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/projects/42/operations";
        context.Request.QueryString = new QueryString("?token=do-not-store");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "SON-AERO\\Admin")],
            "test"));

        await middleware.InvokeAsync(context);

        var recorded = Assert.Single(sink.Errors);
        Assert.Equal("CONNECTED_SERVICE_FAILURE", recorded.ErrorCode);
        Assert.Equal("A connected system or API returned an unusable response.", recorded.Explanation);
        Assert.Equal("Project Tracker - Projects / record / Operations", recorded.AffectedArea);
        Assert.Equal("/api/projects/{id}/operations", recorded.RequestPath);
        Assert.DoesNotContain("token", recorded.RequestPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SON-AERO\\Admin", recorded.AccountName);
        Assert.Equal("trace-123", recorded.TraceIdentifier);
        Assert.InRange(recorded.OccurredAtUtc, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow);
    }

    private sealed class RecordingSink : IArdaErrorSink
    {
        public List<ArdaErrorEvent> Errors { get; } = [];

        public Task WriteAsync(ArdaErrorEvent error, CancellationToken cancellationToken)
        {
            Errors.Add(error);
            return Task.CompletedTask;
        }
    }
}

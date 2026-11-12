using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Data;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class ErrorTrackerEndpoints
{
    public static void MapErrorTrackerEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/admin/errors").RequireAuthorization();
        routes.MapGet("", GetErrorsAsync);
    }

    private static async Task<IResult> GetErrorsAsync(
        string? applicationId,
        string? errorCode,
        string? search,
        int? limit,
        [FromServices] PortalUserService users,
        [FromServices] PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var currentUser = await users.CurrentAsync(cancellationToken);
        if (!string.Equals(currentUser.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Administrator access required",
                detail: "The Error Tracker is available only to Arda administrators.");
        }

        var query = db.ArdaErrorEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(applicationId))
            query = query.Where(error => error.ApplicationId == applicationId.Trim());
        if (!string.IsNullOrWhiteSpace(errorCode))
            query = query.Where(error => error.ErrorCode == errorCode.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(error =>
                error.ErrorCode.Contains(term)
                || error.Explanation.Contains(term)
                || error.AffectedArea.Contains(term)
                || error.RequestPath.Contains(term)
                || (error.AccountName != null && error.AccountName.Contains(term))
                || error.TraceIdentifier.Contains(term));
        }

        var since = DateTime.UtcNow.AddHours(-24);
        var take = Math.Clamp(limit ?? 200, 1, 500);
        var rows = await query
            .OrderByDescending(error => error.OccurredAtUtc)
            .ThenByDescending(error => error.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        var items = rows
            .Select(error => new ErrorTrackerItemDto(
                error.Id,
                error.ApplicationId,
                error.ApplicationName,
                error.ErrorCode,
                error.Explanation,
                error.AffectedArea,
                new DateTimeOffset(DateTime.SpecifyKind(error.OccurredAtUtc, DateTimeKind.Utc)),
                error.HttpStatusCode,
                error.RequestMethod,
                error.RequestPath,
                error.TraceIdentifier,
                error.AccountName,
                error.ExceptionType))
            .ToList();

        var totalRecorded = await db.ArdaErrorEvents.LongCountAsync(cancellationToken);
        var last24Hours = await db.ArdaErrorEvents.LongCountAsync(
            error => error.OccurredAtUtc >= since,
            cancellationToken);
        var affectedModules = await db.ArdaErrorEvents
            .Where(error => error.OccurredAtUtc >= since)
            .Select(error => error.ApplicationId)
            .Distinct()
            .CountAsync(cancellationToken);

        return Results.Ok(new ErrorTrackerOverviewDto(
            DateTime.UtcNow,
            totalRecorded,
            last24Hours,
            affectedModules,
            items));
    }

    private sealed record ErrorTrackerOverviewDto(
        DateTime GeneratedAtUtc,
        long TotalRecorded,
        long Last24Hours,
        int AffectedModules,
        IReadOnlyList<ErrorTrackerItemDto> Items);

    private sealed record ErrorTrackerItemDto(
        long Id,
        string ApplicationId,
        string ApplicationName,
        string ErrorCode,
        string Explanation,
        string AffectedArea,
        DateTimeOffset OccurredAtUtc,
        int HttpStatusCode,
        string RequestMethod,
        string RequestPath,
        string TraceIdentifier,
        string? AccountName,
        string? ExceptionType);
}

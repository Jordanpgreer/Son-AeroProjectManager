using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using Portal.Api.Dtos;
using SonAero.Platform.Estimating;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class EstimatingAdminEndpoints
{
    private const string ManageSettingsPermission = "estimating.settings.admin";

    public static void MapEstimatingAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/estimating/estimators", GetEstimatorsAsync).RequireAuthorization();
        api.MapPut("/admin/estimating/estimators", UpdateEstimatorAsync).RequireAuthorization();
    }

    private static async Task<IResult> GetEstimatorsAsync(
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, cancellationToken)) return AccessDenied();

        var names = await db.EstimatingQuoteHistory
            .AsNoTracking()
            .Select(record => record.EstimatingRep)
            .ToListAsync(cancellationToken);
        var settings = await db.EstimatorSettings.AsNoTracking().ToListAsync(cancellationToken);
        var settingsByKey = settings.ToDictionary(
            setting => setting.EstimatorKey,
            StringComparer.OrdinalIgnoreCase);
        var estimators = names
            .Concat(settings.Select(setting => setting.EstimatorName))
            .Where(EstimatorSettings.IsEligible)
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                settingsByKey.TryGetValue(EstimatorSettings.NormalizeKey(name), out var setting);
                return new EstimatorSettingDto(
                    setting?.EstimatorName ?? name,
                    setting?.IsActive ?? EstimatorSettings.IsActiveByDefault(name),
                    setting is not null,
                    setting?.UpdatedAt,
                    setting?.UpdatedBy);
            })
            .ToList();
        return Results.Ok(new EstimatorSettingsOverviewDto(estimators));
    }

    private static async Task<IResult> UpdateEstimatorAsync(
        EstimatorSettingUpdateDto dto,
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, cancellationToken)) return AccessDenied();

        var estimator = dto.Estimator?.Trim() ?? string.Empty;
        if (!EstimatorSettings.IsEligible(estimator))
            return Results.BadRequest(new { detail = "Choose a named estimator from Estimating Logs." });
        if (estimator.Length > EstimatorSettings.NameMaxLength)
            return Results.BadRequest(new { detail = $"Estimator names cannot exceed {EstimatorSettings.NameMaxLength} characters." });

        var key = EstimatorSettings.NormalizeKey(estimator);
        var knownInHistory = await db.EstimatingQuoteHistory
            .AsNoTracking()
            .AnyAsync(record => record.EstimatingRep.ToUpper() == key, cancellationToken);
        var setting = await db.EstimatorSettings
            .SingleOrDefaultAsync(candidate => candidate.EstimatorKey == key, cancellationToken);
        if (!knownInHistory && setting is null)
            return Results.NotFound(new { detail = "That estimator no longer appears in Estimating Logs." });

        setting ??= new PortalEstimatorSettingRecord { EstimatorKey = key };
        setting.EstimatorName = estimator;
        setting.IsActive = dto.IsActive;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        setting.UpdatedBy = WindowsAccountNames.Normalize(http.User.Identity?.Name) ?? "Unknown";
        if (db.Entry(setting).State == EntityState.Detached) db.EstimatorSettings.Add(setting);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new EstimatorSettingDto(
            setting.EstimatorName,
            setting.IsActive,
            true,
            setting.UpdatedAt,
            setting.UpdatedBy));
    }

    private static async Task<bool> HasPermissionAsync(
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var accountName = WindowsAccountNames.Normalize(http.User.Identity?.Name);
        if (accountName is null) return false;
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        return await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
            .SelectMany(user => user.ProjectTrackerGroupMemberships)
            .SelectMany(membership => membership.Group.Permissions)
            .AnyAsync(permission => permission.PermissionKey == ManageSettingsPermission, cancellationToken);
    }

    private static IResult AccessDenied() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Estimating administration access denied",
        detail: "Your groups do not grant permission to administer Estimating settings.");
}

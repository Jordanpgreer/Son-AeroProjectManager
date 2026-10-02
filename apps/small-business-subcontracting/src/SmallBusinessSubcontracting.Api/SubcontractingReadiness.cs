using Microsoft.EntityFrameworkCore;

namespace SmallBusinessSubcontracting.Api;

public sealed record SubcontractingHealth(string Status, string DatabaseProvider,
    string Migrations, string RoleStore, string DocumentStorage);

public static class SubcontractingReadiness
{
    public static async Task<IResult> CheckAsync(SubcontractingDbContext db, RoleStoreDbContext roles,
        VendorDocumentStore documents, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var health = await ReadAsync(db, roles, documents,
            loggerFactory.CreateLogger("SubcontractingReadiness"), cancellationToken);
        return Results.Json(health, statusCode: health.Status == "ok" ? 200 : 503);
    }

    public static async Task<SubcontractingHealth> ReadAsync(SubcontractingDbContext db, RoleStoreDbContext roles,
        VendorDocumentStore documents, ILogger logger, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.IsSqlServer() ? "SqlServer" : db.Database.IsSqlite() ? "Sqlite" : "Unknown";
        var migrations = "unavailable";
        var roleStore = "unavailable";
        var storage = "unavailable";
        try
        {
            if (!(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                _ = await db.Vendors.Select(row => row.Id).Take(1).ToListAsync(cancellationToken);
                _ = await db.BusinessSizeTags.Select(row => row.Id).Take(1).ToListAsync(cancellationToken);
                _ = await db.VendorBusinessSizeTags.Select(row => row.VendorId).Take(1).ToListAsync(cancellationToken);
                _ = await db.VendorDocuments.Select(row => row.Id).Take(1).ToListAsync(cancellationToken);
                _ = await db.VendorAuditEvents.Select(row => row.Id).Take(1).ToListAsync(cancellationToken);
                migrations = "ready";
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Subcontracting database readiness check failed.");
        }
        try
        {
            // Read the exact tables used by access resolution and integration credentials.
            // Do not migrate or write the shared ProjectTracker database here.
            _ = await roles.Users.Select(row => row.Id).Take(1).ToListAsync(cancellationToken);
            _ = await roles.Set<RoleModuleAssignment>().Select(row => row.AppUserId).Take(1).ToListAsync(cancellationToken);
            _ = await roles.Groups.Select(row => row.Id).Take(1).ToListAsync(cancellationToken);
            _ = await roles.UserGroupMemberships.Select(row => row.AppUserId).Take(1).ToListAsync(cancellationToken);
            _ = await roles.GroupPermissions.Select(row => row.PermissionKey).Take(1).ToListAsync(cancellationToken);
            _ = await roles.IntegrationCredentials.Select(row => row.CredentialKey).Take(1).ToListAsync(cancellationToken);
            roleStore = "ready";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Shared role store readiness check failed.");
        }
        try
        {
            documents.VerifyAccessible();
            storage = "ready";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Vendor document storage readiness check failed.");
        }
        var status = migrations == "ready" && roleStore == "ready" && storage == "ready" ? "ok" : "unavailable";
        return new SubcontractingHealth(status, provider, migrations, roleStore, storage);
    }
}

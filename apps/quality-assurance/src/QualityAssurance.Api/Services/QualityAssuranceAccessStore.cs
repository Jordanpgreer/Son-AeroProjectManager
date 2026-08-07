using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public interface IQualityAssuranceAccessStore
{
    Task<QualityAssuranceAccessProfile?> FindAdministratorAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}

public sealed class QualityAssuranceAccessStore(
    QualityAssuranceAccessDbContext db,
    ILogger<QualityAssuranceAccessStore> logger) : IQualityAssuranceAccessStore
{
    public async Task<QualityAssuranceAccessProfile?> FindAdministratorAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var record = await db.Users
                .AsNoTracking()
                .Where(user =>
                    user.IsActive
                    && lookupKeys.Contains(user.AccountName.ToUpper())
                    && user.GroupMemberships.Any(membership =>
                        membership.Group.Permissions.Any(permission =>
                            permission.PermissionKey == QualityAssurancePermissions.View)))
                .Select(user => new
                {
                    AppUserId = user.Id,
                    user.AccountName,
                    user.DisplayName
                })
                .SingleOrDefaultAsync(cancellationToken);

            return record is null
                ? null
                : new QualityAssuranceAccessProfile(
                    record.AppUserId,
                    WindowsAccountNames.Normalize(record.AccountName) ?? record.AccountName,
                    record.DisplayName,
                    ApplicationRoles.Admin);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "The shared Quality Assurance access store is unavailable. Access is denied.");
            return null;
        }
    }
}

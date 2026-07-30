using System.Data.Common;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public interface IEstimatingAccessStore
{
    Task<EstimatingAccessProfile?> FindEnabledAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}

public sealed class EstimatingAccessStore(
    EstimatingAccessDbContext db,
    ILogger<EstimatingAccessStore> logger) : IEstimatingAccessStore
{
    public async Task<EstimatingAccessProfile?> FindEnabledAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = accountName.Trim().ToUpper();
            var record = await db.UserModuleAccess
                .AsNoTracking()
                .Where(access =>
                    access.ModuleKey == EstimatingModule.Key
                    && access.User.IsActive
                    && access.User.AccountName.ToUpper() == normalized)
                .Select(access => new
                {
                    access.AppUserId,
                    access.User.AccountName,
                    access.User.DisplayName,
                    access.Role
                })
                .SingleOrDefaultAsync(cancellationToken);

            var role = EstimatingRoles.Normalize(record?.Role);
            return record is null || role is null
                ? null
                : new EstimatingAccessProfile(
                    record.AppUserId,
                    record.AccountName,
                    record.DisplayName,
                    role,
                    true);
        }
        catch (Exception exception) when (
            exception is DbException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "The shared Estimating module access store is unavailable. Access is denied.");
            return null;
        }
    }
}

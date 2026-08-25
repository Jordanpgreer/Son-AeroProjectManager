using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Estimating;

namespace EstimatingDashboard.Api.Services;

public sealed class EstimatingEstimatorSettingsService(EstimatingAccessDbContext db)
{
    public async Task<HashSet<string>> GetActiveEstimatorNamesAsync(
        IEnumerable<string> estimatorNames,
        CancellationToken cancellationToken = default)
    {
        var names = estimatorNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var overrides = await db.EstimatorSettings
            .AsNoTracking()
            .ToDictionaryAsync(
                setting => setting.EstimatorKey,
                setting => setting.IsActive,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        return names
            .Where(name => overrides.GetValueOrDefault(
                EstimatorSettings.NormalizeKey(name),
                EstimatorSettings.IsActiveByDefault(name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

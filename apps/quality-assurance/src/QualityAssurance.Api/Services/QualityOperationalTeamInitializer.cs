using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityOperationalTeamInitializer(
    QualityAssuranceDbContext db,
    IConfiguration configuration,
    ILogger<QualityOperationalTeamInitializer> logger)
{
    private const string SystemAccount = "ARDA\\QualityTeamInitializer";

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var shippingTeamName = QualityOperationalTeamNames.Shipping(configuration);
        var normalizedName = shippingTeamName.ToUpper();
        var existing = await db.Teams
            .AsNoTracking()
            .SingleOrDefaultAsync(team => team.Name.ToUpper() == normalizedName, cancellationToken);
        if (existing is not null)
        {
            if (!existing.IsActive)
                logger.LogWarning(
                    "The required Quality shipping team {TeamName} is inactive; QA Complete will remain unavailable until an administrator reactivates it.",
                    existing.Name);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var team = new QualityTeam
        {
            Name = shippingTeamName,
            Description = "Receives shipments after Quality Assurance completion.",
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            CreatedBy = SystemAccount,
            UpdatedAt = now,
            UpdatedBy = SystemAccount
        };
        db.Teams.Add(team);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created the required Quality shipping team {TeamName}.", shippingTeamName);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(team).State = EntityState.Detached;
            if (await db.Teams.AsNoTracking().AnyAsync(
                    candidate => candidate.Name.ToUpper() == normalizedName,
                    cancellationToken))
                return false;
            throw;
        }
    }
}

public static class QualityOperationalTeamNames
{
    public static string Shipping(IConfiguration? configuration)
    {
        var configuredName = configuration?["QualityWorkflow:ShippingGroupName"]?.Trim();
        return string.IsNullOrWhiteSpace(configuredName)
            || string.Equals(configuredName, "Shipping", StringComparison.OrdinalIgnoreCase)
                ? ApplicationGroups.Shipper
                : configuredName;
    }
}

public sealed class QualityWorkflowConfigurationException(string message)
    : InvalidOperationException(message);

using System.Security.Cryptography;
using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

internal interface IIntegrationCredentialReader
{
    Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken);
}

internal sealed class EstimatingEnterpriseProviderSource(EstimatingAccessDbContext db)
    : IEnterpriseProviderSource
{
    public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) =>
        EnterpriseIntegrationStore.ReadActiveProviderAsync(
            db.Database.GetDbConnection(),
            cancellationToken);
}

internal sealed class IntegrationCredentialReader(
    EstimatingAccessDbContext db,
    IIntegrationSecretProtector protector,
    TimeProvider timeProvider) : IIntegrationCredentialReader
{
    public async Task<string?> GetSecretAsync(
        string credentialKey,
        CancellationToken cancellationToken)
    {
        var credential = await db.IntegrationCredentials
            .SingleOrDefaultAsync(
                candidate => candidate.CredentialKey == credentialKey,
                cancellationToken);
        if (credential is null) return null;
        if (credential.ExpiresAt is { } expiry && expiry <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("The saved integration credential has expired. Replace it in Admin Hub.");

        string secret;
        try
        {
            secret = protector.Unprotect(credential.EncryptedSecret);
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or FormatException
            or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                $"The saved '{credential.DisplayName}' credential could not be decrypted on this application server. Replace it in Admin Hub.",
                exception);
        }

        credential.LastUsedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return secret;
    }
}

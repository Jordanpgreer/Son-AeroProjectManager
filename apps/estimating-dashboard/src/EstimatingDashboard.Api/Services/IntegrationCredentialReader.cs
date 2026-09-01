using System.Security.Cryptography;
using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

internal interface IIntegrationCredentialReader
{
    Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken);
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

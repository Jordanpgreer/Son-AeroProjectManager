using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Data;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public interface IQualityIntegrationCredentialReader
{
    Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken);
}

public sealed class QualityEnterpriseProviderSource(QualityAssuranceAccessDbContext db)
    : IEnterpriseProviderSource
{
    public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) =>
        EnterpriseIntegrationStore.ReadActiveProviderAsync(
            db.Database.GetDbConnection(),
            cancellationToken);
}

public sealed class QualityIntegrationCredentialReader(
    QualityAssuranceAccessDbContext db,
    IIntegrationSecretProtector protector,
    TimeProvider timeProvider) : IQualityIntegrationCredentialReader
{
    public async Task<string?> GetSecretAsync(
        string credentialKey,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(cancellationToken);

        try
        {
            string? encryptedSecret = null;
            string? displayName = null;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT EncryptedSecret, DisplayName FROM IntegrationCredentials WHERE CredentialKey = @credentialKey";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@credentialKey";
                parameter.Value = credentialKey;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    encryptedSecret = reader.GetString(0);
                    displayName = reader.GetString(1);
                }
            }

            if (encryptedSecret is null) return null;
            string secret;
            try
            {
                secret = protector.Unprotect(encryptedSecret);
            }
            catch (Exception exception) when (
                exception is CryptographicException
                or FormatException
                or PlatformNotSupportedException)
            {
                throw new InvalidOperationException(
                    $"The saved '{displayName ?? credentialKey}' credential could not be decrypted on this application server. Replace it in Admin Hub.",
                    exception);
            }

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE IntegrationCredentials SET LastUsedAt = @lastUsedAt WHERE CredentialKey = @credentialKey";
            var usedParameter = update.CreateParameter();
            usedParameter.ParameterName = "@lastUsedAt";
            usedParameter.Value = timeProvider.GetUtcNow();
            update.Parameters.Add(usedParameter);
            var keyParameter = update.CreateParameter();
            keyParameter.ParameterName = "@credentialKey";
            keyParameter.Value = credentialKey;
            update.Parameters.Add(keyParameter);
            await update.ExecuteNonQueryAsync(cancellationToken);
            return secret;
        }
        finally
        {
            if (closeConnection && connection.State != ConnectionState.Closed)
                await connection.CloseAsync();
        }
    }
}

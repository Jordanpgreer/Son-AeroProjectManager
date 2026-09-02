using System.Security.Cryptography;
using System.Text;

namespace SonAero.Platform.Security;

public static class IntegrationCredentialNames
{
    public const string FulcrumPublicApi = "fulcrum-public-api";
    public const string AcumaticaApi = "acumatica-api";
    public const int KeyMaxLength = 120;
    public const int DisplayNameMaxLength = 160;
    public const int SecretMaxLength = 16_000;

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().TrimEnd('-');
    }
}

public static class IntegrationCredentialSchema
{
    public const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "IntegrationCredentials" (
            "CredentialKey" TEXT NOT NULL CONSTRAINT "PK_IntegrationCredentials" PRIMARY KEY,
            "DisplayName" TEXT NOT NULL,
            "EncryptedSecret" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL,
            "ExpiresAt" TEXT NULL,
            "LastUsedAt" TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS "IntegrationCredentialTests" (
            "CredentialKey" TEXT NOT NULL CONSTRAINT "PK_IntegrationCredentialTests" PRIMARY KEY,
            "TestedAt" TEXT NOT NULL,
            "Succeeded" INTEGER NOT NULL,
            "Message" TEXT NOT NULL,
            "HttpStatusCode" INTEGER NULL,
            "TestedBy" TEXT NOT NULL
        );
        """;

    public const string SqlServer = """
        IF OBJECT_ID(N'[IntegrationCredentials]', N'U') IS NULL
        BEGIN
            CREATE TABLE [IntegrationCredentials] (
                [CredentialKey] nvarchar(120) NOT NULL CONSTRAINT [PK_IntegrationCredentials] PRIMARY KEY,
                [DisplayName] nvarchar(160) NOT NULL,
                [EncryptedSecret] nvarchar(max) NOT NULL,
                [CreatedAt] datetimeoffset NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL,
                [ExpiresAt] datetimeoffset NULL,
                [LastUsedAt] datetimeoffset NULL
            );
        END;

        IF OBJECT_ID(N'[IntegrationCredentialTests]', N'U') IS NULL
        BEGIN
            CREATE TABLE [IntegrationCredentialTests] (
                [CredentialKey] nvarchar(120) NOT NULL CONSTRAINT [PK_IntegrationCredentialTests] PRIMARY KEY,
                [TestedAt] datetimeoffset NOT NULL,
                [Succeeded] bit NOT NULL,
                [Message] nvarchar(500) NOT NULL,
                [HttpStatusCode] int NULL,
                [TestedBy] nvarchar(160) NOT NULL
            );
        END;
        """;
}

public interface IIntegrationSecretProtector
{
    string Protect(string secret);
    string Unprotect(string encryptedSecret);
}

public sealed class MachineIntegrationSecretProtector : IIntegrationSecretProtector
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("SonAero.Platform.IntegrationCredentials.v1"));

    public string Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Integration credential encryption requires the Windows application server.");
        var clearBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                clearBytes,
                Entropy,
                DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public string Unprotect(string encryptedSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedSecret);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Integration credential encryption requires the Windows application server.");
        var protectedBytes = Convert.FromBase64String(encryptedSecret);
        var clearBytes = ProtectedData.Unprotect(
            protectedBytes,
            Entropy,
            DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }
}

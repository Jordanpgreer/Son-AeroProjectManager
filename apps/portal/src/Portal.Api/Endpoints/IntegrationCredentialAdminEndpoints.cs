using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Data;
using Portal.Api.Services;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class IntegrationCredentialAdminEndpoints
{
    public static void MapIntegrationCredentialAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/integration-credentials", GetAsync).RequireAuthorization();
        api.MapPut("/admin/integration-credentials/{credentialKey}", SaveAsync).RequireAuthorization();
        api.MapPost("/admin/integration-credentials/{credentialKey}/test", TestAsync).RequireAuthorization();
        api.MapDelete("/admin/integration-credentials/{credentialKey}", DeleteAsync).RequireAuthorization();
        api.MapPut("/admin/integration-provider", SetActiveProviderAsync).RequireAuthorization();
    }

    private static async Task<IResult> GetAsync(
        [FromServices] PortalUserService users,
        [FromServices] PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(users, cancellationToken)) return AccessDenied();

        var records = await db.IntegrationCredentials
            .AsNoTracking()
            .OrderBy(credential => credential.DisplayName)
            .ToListAsync(cancellationToken);
        var tests = await db.IntegrationCredentialTests
            .AsNoTracking()
            .ToDictionaryAsync(test => test.CredentialKey, cancellationToken);
        var credentials = records
            .Select(credential => ToDto(
                credential,
                tests.GetValueOrDefault(credential.CredentialKey)))
            .ToList();
        var setting = await db.EnterpriseIntegrationSettings
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == 1, cancellationToken);
        var activeProvider = EnterpriseProviderNames.Normalize(setting.ActiveProvider);
        return Results.Ok(new IntegrationCredentialOverviewDto(
            credentials,
            activeProvider.Length > 0 ? activeProvider : EnterpriseProviderNames.Fulcrum,
            EnterpriseProviderNames.All));
    }

    private static async Task<IResult> SetActiveProviderAsync(
        [FromBody] EnterpriseIntegrationProviderUpdateDto dto,
        [FromServices] PortalUserService users,
        [FromServices] PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (!string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
            return AccessDenied();

        var provider = EnterpriseProviderNames.Normalize(dto.Provider);
        if (provider.Length == 0)
            return Results.BadRequest(new
            {
                detail = $"Choose one of the supported providers: {string.Join(", ", EnterpriseProviderNames.All)}."
            });

        var setting = await db.EnterpriseIntegrationSettings
            .SingleAsync(candidate => candidate.Id == 1, cancellationToken);
        var previousProvider = EnterpriseProviderNames.Normalize(setting.ActiveProvider);
        if (previousProvider.Length == 0) previousProvider = EnterpriseProviderNames.Fulcrum;
        if (string.Equals(previousProvider, provider, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(new EnterpriseIntegrationProviderDto(
                previousProvider,
                setting.UpdatedAt,
                setting.UpdatedBy));

        var now = DateTimeOffset.UtcNow;
        db.EnterpriseIntegrationSettingAudits.Add(new PortalEnterpriseIntegrationSettingAuditRecord
        {
            PreviousProvider = previousProvider,
            NewProvider = provider,
            ChangedAt = now,
            ChangedBy = user.AccountName
        });
        setting.ActiveProvider = provider;
        setting.UpdatedAt = now;
        setting.UpdatedBy = user.AccountName;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new EnterpriseIntegrationProviderDto(
            setting.ActiveProvider,
            setting.UpdatedAt,
            setting.UpdatedBy));
    }

    private static async Task<IResult> SaveAsync(
        string credentialKey,
        [FromBody] IntegrationCredentialUpdateDto dto,
        [FromServices] PortalUserService users,
        [FromServices] PortalRoleDbContext db,
        [FromServices] IIntegrationSecretProtector protector,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (!string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
            return AccessDenied();

        var normalizedKey = IntegrationCredentialNames.NormalizeKey(credentialKey);
        if (normalizedKey.Length == 0
            || normalizedKey.Length > IntegrationCredentialNames.KeyMaxLength
            || !string.Equals(normalizedKey, credentialKey, StringComparison.Ordinal))
            return Results.BadRequest(new { detail = "Use a lowercase key identifier containing only letters, numbers, and hyphens." });

        var displayName = dto.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0 || displayName.Length > IntegrationCredentialNames.DisplayNameMaxLength)
            return Results.BadRequest(new { detail = $"Credential names are required and cannot exceed {IntegrationCredentialNames.DisplayNameMaxLength} characters." });

        var secret = NormalizeSecret(dto.Secret);
        if (secret.Length == 0 || secret.Length > IntegrationCredentialNames.SecretMaxLength)
            return Results.BadRequest(new { detail = $"Enter an API key or token no longer than {IntegrationCredentialNames.SecretMaxLength:N0} characters." });

        var now = DateTimeOffset.UtcNow;
        var credential = await db.IntegrationCredentials
            .SingleOrDefaultAsync(candidate => candidate.CredentialKey == normalizedKey, cancellationToken);
        if (credential is null)
        {
            credential = new PortalIntegrationCredentialRecord
            {
                CredentialKey = normalizedKey,
                CreatedAt = now
            };
            db.IntegrationCredentials.Add(credential);
        }

        credential.DisplayName = displayName;
        credential.EncryptedSecret = protector.Protect(secret);
        credential.UpdatedAt = now;
        credential.UpdatedBy = user.AccountName;
        credential.ExpiresAt = TryReadJwtExpiry(secret);
        var previousTest = await db.IntegrationCredentialTests
            .SingleOrDefaultAsync(test => test.CredentialKey == normalizedKey, cancellationToken);
        if (previousTest is not null) db.IntegrationCredentialTests.Remove(previousTest);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToDto(credential, null));
    }

    private static async Task<IResult> TestAsync(
        string credentialKey,
        [FromServices] PortalUserService users,
        [FromServices] PortalRoleDbContext db,
        [FromServices] IIntegrationSecretProtector protector,
        [FromServices] FulcrumCredentialTester fulcrumTester,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        if (!string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
            return AccessDenied();

        var normalizedKey = IntegrationCredentialNames.NormalizeKey(credentialKey);
        if (!string.Equals(normalizedKey, credentialKey, StringComparison.Ordinal))
            return Results.BadRequest(new { detail = "The credential key identifier is invalid." });
        if (!string.Equals(
            normalizedKey,
            IntegrationCredentialNames.FulcrumPublicApi,
            StringComparison.Ordinal))
            return Results.BadRequest(new { detail = "A connection test is not defined for this named credential." });

        var credential = await db.IntegrationCredentials
            .SingleOrDefaultAsync(candidate => candidate.CredentialKey == normalizedKey, cancellationToken);
        if (credential is null)
            return Results.NotFound(new { detail = "Save the Fulcrum Public API token before testing the connection." });

        IntegrationCredentialTestResult result;
        try
        {
            var secret = protector.Unprotect(credential.EncryptedSecret);
            result = await fulcrumTester.TestAsync(secret, cancellationToken);
        }
        catch (Exception exception) when (
            exception is System.Security.Cryptography.CryptographicException
            or FormatException
            or PlatformNotSupportedException)
        {
            result = new(
                false,
                "The saved token could not be decrypted on this application server. Replace it and test again.",
                null);
        }

        var now = DateTimeOffset.UtcNow;
        var test = await db.IntegrationCredentialTests
            .SingleOrDefaultAsync(candidate => candidate.CredentialKey == normalizedKey, cancellationToken);
        if (test is null)
        {
            test = new PortalIntegrationCredentialTestRecord { CredentialKey = normalizedKey };
            db.IntegrationCredentialTests.Add(test);
        }
        test.TestedAt = now;
        test.Succeeded = result.Succeeded;
        test.Message = result.Message;
        test.HttpStatusCode = result.HttpStatusCode;
        test.TestedBy = user.AccountName;
        credential.LastUsedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToDto(credential, test));
    }

    private static async Task<IResult> DeleteAsync(
        string credentialKey,
        [FromServices] PortalUserService users,
        [FromServices] PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(users, cancellationToken)) return AccessDenied();

        var normalizedKey = IntegrationCredentialNames.NormalizeKey(credentialKey);
        if (!string.Equals(normalizedKey, credentialKey, StringComparison.Ordinal))
            return Results.BadRequest(new { detail = "The credential key identifier is invalid." });
        var credential = await db.IntegrationCredentials
            .SingleOrDefaultAsync(candidate => candidate.CredentialKey == normalizedKey, cancellationToken);
        if (credential is null) return Results.NoContent();

        var test = await db.IntegrationCredentialTests
            .SingleOrDefaultAsync(candidate => candidate.CredentialKey == normalizedKey, cancellationToken);
        if (test is not null) db.IntegrationCredentialTests.Remove(test);
        db.IntegrationCredentials.Remove(credential);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static string NormalizeSecret(string? value)
    {
        var secret = value?.Trim() ?? string.Empty;
        return secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? secret["Bearer ".Length..].Trim()
            : secret;
    }

    private static DateTimeOffset? TryReadJwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var expiration)
                && expiration.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;
        }
        catch (Exception exception) when (
            exception is FormatException
            or JsonException
            or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IntegrationCredentialDto ToDto(
        PortalIntegrationCredentialRecord credential,
        PortalIntegrationCredentialTestRecord? test) => new(
        credential.CredentialKey,
        credential.DisplayName,
        true,
        credential.CreatedAt,
        credential.UpdatedAt,
        credential.UpdatedBy,
        credential.ExpiresAt,
        credential.LastUsedAt,
        test?.TestedAt,
        test?.Succeeded,
        test?.Message,
        test?.HttpStatusCode,
        test?.TestedBy);

    private static async Task<bool> IsAdministratorAsync(
        PortalUserService users,
        CancellationToken cancellationToken)
    {
        var user = await users.CurrentAsync(cancellationToken);
        return string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult AccessDenied() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Integration credential access denied",
        detail: "Only Arda administrators can manage enterprise integrations.");
}

public sealed record IntegrationCredentialUpdateDto(string? DisplayName, string? Secret);
public sealed record EnterpriseIntegrationProviderUpdateDto(string? Provider);
public sealed record EnterpriseIntegrationProviderDto(
    string ActiveProvider,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record IntegrationCredentialDto(
    string CredentialKey,
    string DisplayName,
    bool IsConfigured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    int? LastTestHttpStatusCode,
    string? LastTestedBy);

public sealed record IntegrationCredentialOverviewDto(
    IReadOnlyList<IntegrationCredentialDto> Credentials,
    string ActiveProvider,
    IReadOnlyList<string> SupportedProviders);

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Data;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class IntegrationCredentialAdminEndpoints
{
    public static void MapIntegrationCredentialAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/integration-credentials", GetAsync).RequireAuthorization();
        api.MapPut("/admin/integration-credentials/{credentialKey}", SaveAsync).RequireAuthorization();
        api.MapDelete("/admin/integration-credentials/{credentialKey}", DeleteAsync).RequireAuthorization();
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
        var credentials = records.Select(ToDto).ToList();
        return Results.Ok(new IntegrationCredentialOverviewDto(credentials));
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
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToDto(credential));
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

    private static IntegrationCredentialDto ToDto(PortalIntegrationCredentialRecord credential) => new(
        credential.CredentialKey,
        credential.DisplayName,
        true,
        credential.CreatedAt,
        credential.UpdatedAt,
        credential.UpdatedBy,
        credential.ExpiresAt,
        credential.LastUsedAt);

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
        detail: "Only Arda administrators can manage integration API keys.");
}

public sealed record IntegrationCredentialUpdateDto(string? DisplayName, string? Secret);

public sealed record IntegrationCredentialDto(
    string CredentialKey,
    string DisplayName,
    bool IsConfigured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);

public sealed record IntegrationCredentialOverviewDto(
    IReadOnlyList<IntegrationCredentialDto> Credentials);

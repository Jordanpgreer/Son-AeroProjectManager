using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Api.Configuration;
using Portal.Api.Data;
using SonAero.Platform.Notifications;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public enum ArdaPushEnqueueStatus
{
    Accepted,
    Invalid,
    Unauthorized,
    UnknownModule
}

public sealed record ArdaPushEnqueueResult(
    ArdaPushEnqueueStatus Status,
    long? NotificationId = null,
    Dictionary<string, string[]>? Errors = null);

public sealed class ArdaPushProducerService(
    PortalRoleDbContext db,
    ApplicationRegistry applications,
    IOptions<WebPushOptions> options)
{
    public async Task<ArdaPushEnqueueResult> EnqueueAsync(
        string sourceModule,
        string? producerKey,
        ArdaPushNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuredKey = options.Value.ProducerKeyFor(sourceModule);
        if (string.IsNullOrWhiteSpace(configuredKey) || !KeysEqual(configuredKey, producerKey))
            return new(ArdaPushEnqueueStatus.Unauthorized);

        var application = applications.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceModule, StringComparison.OrdinalIgnoreCase));
        if (application is null || string.Equals(application.Id, ApplicationRegistry.AdminConsoleApplicationId,
                StringComparison.OrdinalIgnoreCase))
            return new(ArdaPushEnqueueStatus.UnknownModule);

        var errors = Validate(request);
        var targetUrl = ResolveTargetUrl(application.Url, request.TargetPath);
        if (targetUrl is null) errors["targetPath"] = ["A safe application-relative target path is required."];
        if (errors.Count > 0) return new(ArdaPushEnqueueStatus.Invalid, Errors: errors);

        var normalizedAccount = WindowsAccountNames.Normalize(request.RecipientAccountName)!;
        var moduleId = application.Id.Trim().ToLowerInvariant();
        var sourceKey = request.SourceNotificationKey!.Trim();
        var existingId = await db.ArdaPushNotifications
            .Where(item => item.SourceModule == moduleId && item.SourceNotificationKey == sourceKey)
            .Select(item => (long?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingId is not null)
            return new(ArdaPushEnqueueStatus.Accepted, existingId.Value);

        var now = DateTimeOffset.UtcNow;
        var notification = new ArdaPushNotificationRecord
        {
            RecipientAccountName = normalizedAccount,
            SourceModule = moduleId,
            SourceNotificationKey = sourceKey,
            Title = request.Title!.Trim(),
            Body = request.Body!.Trim(),
            TargetUrl = targetUrl!,
            CreatedAt = now,
            NextAttemptAt = now
        };
        db.ArdaPushNotifications.Add(notification);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(ArdaPushEnqueueStatus.Accepted, notification.Id);
        }
        catch (DbUpdateException)
        {
            db.Entry(notification).State = EntityState.Detached;
            var concurrentId = await db.ArdaPushNotifications
                .Where(item => item.SourceModule == moduleId && item.SourceNotificationKey == sourceKey)
                .Select(item => (long?)item.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (concurrentId is not null)
                return new(ArdaPushEnqueueStatus.Accepted, concurrentId.Value);
            throw;
        }
    }

    private static Dictionary<string, string[]> Validate(ArdaPushNotificationRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(request.SourceNotificationKey) || request.SourceNotificationKey.Length > 160)
            errors["sourceNotificationKey"] = ["A source notification key of at most 160 characters is required."];
        var account = WindowsAccountNames.Normalize(request.RecipientAccountName);
        if (account is null || account.Length > 160)
            errors["recipientAccountName"] = ["A valid recipient Windows account is required."];
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 160)
            errors["title"] = ["A notification title of at most 160 characters is required."];
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 500)
            errors["body"] = ["A notification body of at most 500 characters is required."];
        return errors;
    }

    internal static string? ResolveTargetUrl(string applicationUrl, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)
            || !targetPath.StartsWith('/')
            || targetPath.StartsWith("//", StringComparison.Ordinal)
            || targetPath.Any(char.IsControl)
            || !Uri.TryCreate(applicationUrl, UriKind.Absolute, out var applicationUri)
            || applicationUri.Scheme is not ("http" or "https"))
            return null;

        var origin = new Uri(applicationUri.GetLeftPart(UriPartial.Authority));
        if (!Uri.TryCreate(origin, targetPath, out var targetUri) || targetUri.Host != origin.Host
            || targetUri.Scheme != origin.Scheme || targetUri.Port != origin.Port)
            return null;
        return targetUri.AbsoluteUri;
    }

    internal static bool KeysEqual(string expected, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

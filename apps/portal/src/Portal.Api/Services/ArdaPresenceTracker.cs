using System.Collections.Concurrent;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public sealed class ArdaPresenceTracker(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan VisibleClientLifetime = TimeSpan.FromSeconds(45);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, PresenceEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string accountName, string moduleId, string clientId, bool visible)
    {
        var account = WindowsAccountNames.Normalize(accountName)
            ?? throw new ArgumentException("A valid Windows account is required.", nameof(accountName));
        var key = $"{account}\n{moduleId.Trim()}\n{clientId.Trim()}";
        if (!visible)
        {
            entries.TryRemove(key, out _);
            return;
        }

        entries[key] = new PresenceEntry(account, clock.GetUtcNow().Add(VisibleClientLifetime));
    }

    public bool HasVisibleClient(string accountName)
    {
        var account = WindowsAccountNames.Normalize(accountName);
        if (account is null) return false;

        var now = clock.GetUtcNow();
        var found = false;
        foreach (var pair in entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                entries.TryRemove(pair.Key, out _);
                continue;
            }
            if (WindowsAccountNames.Equals(pair.Value.AccountName, account)) found = true;
        }
        return found;
    }

    public bool IsVisibleClient(string accountName, string moduleId, string clientId)
    {
        var account = WindowsAccountNames.Normalize(accountName);
        if (account is null) return false;
        var key = $"{account}\n{moduleId.Trim()}\n{clientId.Trim()}";
        if (!entries.TryGetValue(key, out var entry)) return false;
        if (entry.ExpiresAt > clock.GetUtcNow()) return true;
        entries.TryRemove(key, out _);
        return false;
    }

    private sealed record PresenceEntry(string AccountName, DateTimeOffset ExpiresAt);
}

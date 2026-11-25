namespace EstimatingDashboard.Api.Services;

// This queue requests an interactive Outlook window. The synced email record, not job status,
// controls material follow-up. A restart can lose an open request without marking any email sent.
internal sealed class MaterialFollowUpDraftQueue(TimeProvider clock)
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private const int Capacity = 2000;
    private const string TimeoutMessage = "Outlook did not confirm opening the draft. Open Classic Outlook and check that the Arda Outlook connector is running and up to date.";
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> jobs = [];

    internal record Job(Guid Id, string Account, string Mailbox, int QuoteId, string ItemId, string SourceMessageId);
    internal record Status(Guid JobId, string State, string? Message);
    private record Entry(Job Job, DateTimeOffset QueuedAt, Status Status);

    public Status Enqueue(string account, string mailbox, int quoteId, string itemId, string sourceMessageId)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in jobs.Where(item => now - item.Value.QueuedAt >= Retention).Select(x => x.Key).ToArray())
                jobs.Remove(key);
            var existing = jobs.Values.FirstOrDefault(entry => Pending(entry, now)
                && IsOwner(entry.Job, account, mailbox) && entry.Job.QuoteId == quoteId
                && entry.Job.ItemId == itemId && entry.Job.SourceMessageId == sourceMessageId);
            if (existing is not null) return existing.Status;
            if (jobs.Count >= Capacity)
                throw new VendorQuoteException(429, "Too many Outlook draft requests are pending. Try again shortly.");
            var job = new Job(Guid.NewGuid(), account, mailbox, quoteId, itemId, sourceMessageId);
            var status = new Status(job.Id, "queued", null);
            jobs.Add(job.Id, new(job, now, status));
            return status;
        }
    }

    public Job? Claim(string account, string mailbox)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var entry = jobs.Values.Where(entry => entry.Status.State == "queued" && Pending(entry, now)
                && IsOwner(entry.Job, account, mailbox)).MinBy(entry => entry.QueuedAt);
            if (entry is null) return null;
            jobs[entry.Job.Id] = entry with { Status = entry.Status with { State = "claimed" } };
            return entry.Job;
        }
    }

    public Status? Find(Guid id, string account, int quoteId, string itemId)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (!jobs.TryGetValue(id, out var entry) || now - entry.QueuedAt >= Retention
                || !Same(entry.Job.Account, account) || entry.Job.QuoteId != quoteId || entry.Job.ItemId != itemId)
                return null;
            return !Pending(entry, now) && (entry.Status.State is "queued" or "claimed")
                ? entry.Status with { State = "failed", Message = TimeoutMessage } : entry.Status;
        }
    }

    public bool IsClaimed(Guid id)
    {
        lock (gate)
            return jobs.TryGetValue(id, out var entry) && entry.Status.State == "claimed" && Pending(entry, clock.GetUtcNow());
    }

    public bool Complete(Guid id, string account, string mailbox, bool opened, string? message)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var entry) || !IsOwner(entry.Job, account, mailbox)) return false;
            if (entry.Status.State is "opened" or "failed") return true;
            if (entry.Status.State != "claimed" || !Pending(entry, clock.GetUtcNow())) return false;
            jobs[id] = entry with { Status = entry.Status with { State = opened ? "opened" : "failed", Message = message } };
            return true;
        }
    }

    private static bool Pending(Entry entry, DateTimeOffset now) => (entry.Status.State is "queued" or "claimed")
        && now - entry.QueuedAt < OpenWindow;
    private static bool IsOwner(Job job, string account, string mailbox) => Same(job.Account, account) && Same(job.Mailbox, mailbox);
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

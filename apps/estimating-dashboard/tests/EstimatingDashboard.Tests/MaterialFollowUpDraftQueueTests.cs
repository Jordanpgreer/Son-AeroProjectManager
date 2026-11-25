using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Tests;

public sealed class MaterialFollowUpDraftQueueTests
{
    [Fact]
    public void Only_the_requesting_windows_account_and_mailbox_can_claim_and_report()
    {
        var queue = new MaterialFollowUpDraftQueue(new Clock());
        var waiting = queue.Enqueue(@"son\estimator", "estimator@example.test", 12, "item-1", "original");
        Assert.Equal(waiting.JobId, queue.Enqueue(@"SON\ESTIMATOR", "estimator@example.test", 12, "item-1", "original").JobId);
        Assert.Null(queue.Claim(@"son\other", "estimator@example.test"));
        Assert.Null(queue.Claim(@"son\estimator", "other@example.test"));
        Assert.Null(queue.Find(waiting.JobId, @"son\other", 12, "item-1"));
        Assert.Null(queue.Find(waiting.JobId, @"son\estimator", 13, "item-1"));
        Assert.Null(queue.Find(waiting.JobId, @"son\estimator", 12, "item-2"));
        Assert.False(queue.Complete(waiting.JobId, @"son\estimator", "estimator@example.test", true, null));

        var claimed = Assert.IsType<MaterialFollowUpDraftQueue.Job>(queue.Claim(@"SON\ESTIMATOR", "Estimator@example.test"));
        Assert.Equal(waiting.JobId, claimed.Id);
        Assert.Equal("original", claimed.SourceMessageId);
        Assert.True(queue.IsClaimed(claimed.Id));
        Assert.Null(queue.Claim(@"son\estimator", "estimator@example.test"));
        Assert.False(queue.Complete(claimed.Id, @"son\other", "estimator@example.test", true, null));
        Assert.True(queue.Complete(claimed.Id, @"son\estimator", "estimator@example.test", true, null));
        Assert.True(queue.Complete(claimed.Id, @"son\estimator", "estimator@example.test", false, "late result"));
        Assert.Equal("opened", queue.Find(claimed.Id, @"son\estimator", 12, "item-1")?.State);
    }

    [Fact]
    public void Expired_windows_cannot_open_late_and_can_be_retried()
    {
        var clock = new Clock();
        var queue = new MaterialFollowUpDraftQueue(clock);
        var first = queue.Enqueue("estimator", "estimator@example.test", 12, "item-1", "original");
        Assert.NotNull(queue.Claim("estimator", "estimator@example.test"));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.False(queue.IsClaimed(first.JobId));
        Assert.False(queue.Complete(first.JobId, "estimator", "estimator@example.test", true, null));
        Assert.Equal("failed", queue.Find(first.JobId, "estimator", 12, "item-1")?.State);
        var retry = queue.Enqueue("estimator", "estimator@example.test", 12, "item-1", "original");
        Assert.NotEqual(first.JobId, retry.JobId);
        Assert.Equal(retry.JobId, queue.Claim("estimator", "estimator@example.test")?.Id);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 25, 17, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

using NwsAlertBot.Services;

namespace NwsAlertBot.Tests;

public class RateLimitTrackerTests : IDisposable
{
    private readonly string _stateFilePath = Path.Combine(Path.GetTempPath(), $"ratelimit_test_{Guid.NewGuid()}.txt");

    public void Dispose()
    {
        if (File.Exists(_stateFilePath)) File.Delete(_stateFilePath);
    }

    [Fact]
    public void TryAcquire_AllowsUpToLimitThenBlocks()
    {
        var tracker = new RateLimitTracker(_stateFilePath, limit: 3, window: TimeSpan.FromDays(1));

        Assert.True(tracker.TryAcquire());
        Assert.True(tracker.TryAcquire());
        Assert.True(tracker.TryAcquire());
        Assert.False(tracker.TryAcquire());
        Assert.False(tracker.TryAcquire()); // stays blocked, doesn't error or over-consume
    }

    [Fact]
    public void TryAcquire_ZeroLimitMeansUnlimited()
    {
        var tracker = new RateLimitTracker(_stateFilePath, limit: 0, window: TimeSpan.FromDays(1));

        for (int i = 0; i < 50; i++)
            Assert.True(tracker.TryAcquire());
    }

    [Fact]
    public void TryAcquire_ResetsAfterWindowExpires()
    {
        // A fake, manually-advanced clock instead of a real Thread.Sleep -- keeps this
        // deterministic regardless of test-runner scheduling/parallelism jitter.
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var tracker = new RateLimitTracker(_stateFilePath, limit: 1, window: TimeSpan.FromHours(1), clock: () => now);

        Assert.True(tracker.TryAcquire());
        Assert.False(tracker.TryAcquire()); // limit reached within the window

        now = now.AddHours(2); // past the window

        Assert.True(tracker.TryAcquire()); // window rolled over, count reset
    }

    [Fact]
    public void TryAcquire_PersistsAcrossNewInstances()
    {
        // A fresh RateLimitTracker instance pointed at the same state file must pick up the
        // already-consumed count rather than starting over -- this is the entire point of using
        // a persisted counter instead of an in-memory-only limiter (see class remarks).
        var first = new RateLimitTracker(_stateFilePath, limit: 2, window: TimeSpan.FromDays(1));
        Assert.True(first.TryAcquire());
        Assert.True(first.TryAcquire());

        var second = new RateLimitTracker(_stateFilePath, limit: 2, window: TimeSpan.FromDays(1));
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void GetStatus_ReflectsCountWithoutConsumingIt()
    {
        var tracker = new RateLimitTracker(_stateFilePath, limit: 5, window: TimeSpan.FromDays(1));
        tracker.TryAcquire();
        tracker.TryAcquire();

        var (count1, limit1) = tracker.GetStatus();
        var (count2, limit2) = tracker.GetStatus();

        Assert.Equal(2, count1);
        Assert.Equal(5, limit1);
        Assert.Equal(count1, count2); // calling GetStatus repeatedly doesn't itself consume
        Assert.Equal(limit1, limit2);
    }

    [Fact]
    public void TryAcquire_MissingStateFileStartsFromZero()
    {
        // No file has been written yet -- should behave as an empty/fresh window, not throw.
        var tracker = new RateLimitTracker(_stateFilePath, limit: 1, window: TimeSpan.FromDays(1));
        Assert.True(tracker.TryAcquire());
    }
}

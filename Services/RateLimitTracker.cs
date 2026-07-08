namespace NwsAlertBot.Services;

/// <summary>
/// Persisted fixed-window counter (e.g. "N per day", "N per 30 days") used as a quota/cost guard
/// for X's monthly post limit and Twilio's per-SMS cost. Deliberately hand-rolled rather than
/// wrapping System.Threading.RateLimiting's built-in limiters: this bot redeploys on every push to
/// master (see deploy.yml), and the BCL limiters are in-memory only with no way to reconstruct
/// their internal state from external storage — an in-memory counter would reset far too often to
/// meaningfully guard a monthly/daily cap. This class persists the window start and count to a
/// small state file instead, mirroring the existing posted_alerts.txt/confirmed_platforms.txt
/// convention.
/// </summary>
internal class RateLimitTracker
{
    private readonly string _stateFilePath;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly object _lock = new();

    public RateLimitTracker(string stateFilePath, int limit, TimeSpan window)
    {
        _stateFilePath = stateFilePath;
        _limit = limit;
        _window = window;
    }

    /// <summary>
    /// Attempts to consume one unit of the limit for the current window, persisting the
    /// incremented count. Returns true if under the limit (and the unit was consumed), false if
    /// the limit has already been reached for this window. A limit of 0 always returns true
    /// (disabled).
    /// </summary>
    public bool TryAcquire()
    {
        if (_limit <= 0) return true;

        lock (_lock)
        {
            var (windowStart, count) = Load();
            var now = DateTimeOffset.UtcNow;

            if (now - windowStart >= _window)
            {
                windowStart = now;
                count = 0;
            }

            if (count >= _limit)
            {
                Save(windowStart, count);
                return false;
            }

            count++;
            Save(windowStart, count);
            return true;
        }
    }

    /// <summary>Current count and configured limit for the active window, for logging.</summary>
    public (int Count, int Limit) GetStatus()
    {
        lock (_lock)
        {
            var (windowStart, count) = Load();
            if (DateTimeOffset.UtcNow - windowStart >= _window) count = 0;
            return (count, _limit);
        }
    }

    private (DateTimeOffset WindowStart, int Count) Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return (DateTimeOffset.UtcNow, 0);

            var parts = File.ReadAllText(_stateFilePath).Trim().Split('|');
            if (parts.Length != 2) return (DateTimeOffset.UtcNow, 0);
            if (!DateTimeOffset.TryParse(parts[0], out var windowStart)) return (DateTimeOffset.UtcNow, 0);
            if (!int.TryParse(parts[1], out var count)) return (DateTimeOffset.UtcNow, 0);

            return (windowStart, count);
        }
        catch
        {
            return (DateTimeOffset.UtcNow, 0);
        }
    }

    private void Save(DateTimeOffset windowStart, int count)
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_stateFilePath, $"{windowStart:o}|{count}");
        }
        catch
        {
            // Best-effort persistence -- a failed write just means the next check re-derives
            // from whatever was last saved (or starts a fresh window), never throws.
        }
    }
}

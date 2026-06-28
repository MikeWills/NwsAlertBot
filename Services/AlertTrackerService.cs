using Microsoft.Extensions.Logging;

namespace NwsAlertBot.Services;

/// <summary>
/// Tracks which alert IDs have already been posted so we don't duplicate.
/// Persists to a tab-separated text file (date\tid) so state survives restarts.
/// Entries older than MaxAgeDays are pruned automatically — NWS alerts expire within
/// hours to a day, so there is never a reason to remember an ID from a week ago.
/// </summary>
public class AlertTrackerService
{
    public const int MaxAgeDays = 7;

    private readonly string _filePath;
    private readonly ILogger<AlertTrackerService> _logger;

    // List preserves insertion order; set provides O(1) lookup.
    private readonly List<(DateOnly Date, string Id)> _entries = new();
    private readonly HashSet<string> _idSet = new();
    private readonly object _lock = new();

    public AlertTrackerService(ILogger<AlertTrackerService> logger, string filePath = "posted_alerts.txt")
    {
        _filePath = filePath;
        _logger = logger;
        Load();
        Prune();
    }

    public bool HasBeenPosted(string alertId)
    {
        lock (_lock)
            return _idSet.Contains(alertId);
    }

    public void MarkPosted(string alertId)
    {
        lock (_lock)
        {
            if (_idSet.Add(alertId))
                _entries.Add((DateOnly.FromDateTime(DateTime.UtcNow), alertId));
            Prune();
            Save();
        }
    }

    private void Prune()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-MaxAgeDays);
        int before = _entries.Count;
        int removeCount = _entries.TakeWhile(e => e.Date < cutoff).Count();
        if (removeCount == 0) return;

        for (int i = 0; i < removeCount; i++)
            _idSet.Remove(_entries[i].Id);
        _entries.RemoveRange(0, removeCount);

        _logger.LogInformation("Alert tracker: pruned {Removed} entries older than {Days} days ({Remaining} remaining).",
            removeCount, MaxAgeDays, _entries.Count);
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                DateOnly date;
                string id;

                // New format: "YYYY-MM-DD\t{id}"
                // Old format: "{id}" — assign today so existing entries survive MaxAgeDays more days
                var tab = trimmed.IndexOf('\t');
                if (tab > 0 && DateOnly.TryParse(trimmed[..tab], out var parsed))
                {
                    date = parsed;
                    id   = trimmed[(tab + 1)..];
                }
                else
                {
                    date = today;
                    id   = trimmed;
                }

                if (!string.IsNullOrWhiteSpace(id) && _idSet.Add(id))
                    _entries.Add((date, id));
            }
            _logger.LogInformation("Loaded {Count} previously posted alert IDs.", _entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load posted alert tracking file.");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllLines(_filePath, _entries.Select(e => $"{e.Date:yyyy-MM-dd}\t{e.Id}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save posted alert tracking file.");
        }
    }
}

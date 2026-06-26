using Microsoft.Extensions.Logging;

namespace NwsAlertBot.Services;

/// <summary>
/// Tracks which alert IDs have already been posted so we don't duplicate.
/// Persists to a simple text file so state survives restarts.
/// </summary>
public class AlertTrackerService
{
    private readonly string _filePath;
    private readonly ILogger<AlertTrackerService> _logger;

    // List preserves insertion order for deterministic pruning; set provides O(1) lookup.
    private readonly List<string> _orderedIds = new();
    private readonly HashSet<string> _idSet = new();
    private readonly object _lock = new();

    public AlertTrackerService(ILogger<AlertTrackerService> logger, string filePath = "posted_alerts.txt")
    {
        _filePath = filePath;
        _logger = logger;
        Load();
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
                _orderedIds.Add(alertId);
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var id = line.Trim();
                if (!string.IsNullOrWhiteSpace(id) && _idSet.Add(id))
                    _orderedIds.Add(id);
            }
            _logger.LogInformation("Loaded {Count} previously posted alert IDs.", _orderedIds.Count);
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
            File.WriteAllLines(_filePath, _orderedIds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save posted alert tracking file.");
        }
    }

    /// <summary>
    /// Removes the oldest half of tracked IDs when the count exceeds maxEntries.
    /// Uses insertion order so recently-posted IDs are always retained.
    /// </summary>
    public void PruneOldEntries(int maxEntries = 10000)
    {
        lock (_lock)
        {
            if (_orderedIds.Count > maxEntries)
            {
                int removeCount = _orderedIds.Count - maxEntries / 2;
                for (int i = 0; i < removeCount; i++)
                    _idSet.Remove(_orderedIds[i]);
                _orderedIds.RemoveRange(0, removeCount);
                Save();
                _logger.LogInformation("Pruned alert tracker to {Count} entries.", _orderedIds.Count);
            }
        }
    }
}

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
    private readonly HashSet<string> _postedIds = new();
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
            return _postedIds.Contains(alertId);
    }

    public void MarkPosted(string alertId)
    {
        lock (_lock)
        {
            _postedIds.Add(alertId);
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
                if (!string.IsNullOrWhiteSpace(line))
                    _postedIds.Add(line.Trim());
            }
            _logger.LogInformation("Loaded {Count} previously posted alert IDs.", _postedIds.Count);
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
            File.WriteAllLines(_filePath, _postedIds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save posted alert tracking file.");
        }
    }

    /// <summary>
    /// Prune old entries. Call periodically to keep the file from growing forever.
    /// Only keeps IDs from the last 7 days based on file line count (simple heuristic).
    /// </summary>
    public void PruneOldEntries(int maxEntries = 10000)
    {
        lock (_lock)
        {
            if (_postedIds.Count > maxEntries)
            {
                var trimmed = _postedIds.TakeLast(maxEntries / 2).ToHashSet();
                _postedIds.Clear();
                foreach (var id in trimmed) _postedIds.Add(id);
                Save();
                _logger.LogInformation("Pruned alert tracker to {Count} entries.", _postedIds.Count);
            }
        }
    }
}

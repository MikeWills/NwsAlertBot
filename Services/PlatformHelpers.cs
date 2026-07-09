using NwsAlertBot.Models;

namespace NwsAlertBot.Services;

/// <summary>Shared formatting/URL helpers used across multiple platform services.</summary>
internal static class PlatformHelpers
{
    /// <summary>Truncates to maxLength, appending "..." if truncated (reserves 3 chars for it).</summary>
    internal static string TruncateWithEllipsis(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

    /// <summary>Appends a cache-busting query parameter so platforms don't serve a stale cached image.</summary>
    internal static string? CacheBust(string? url, string alertId)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string sep = url.Contains('?') ? "&" : "?";
        return url + $"{sep}_cb={Uri.EscapeDataString(alertId)}";
    }

    /// <summary>
    /// Builds an SMS body, fitting it within maxLength one field at a time: the header always
    /// appears, and each of area/until/instruction is included only if it fits whole -- never
    /// truncated mid-field (which would otherwise show a useless fragment like "Until: ...").
    /// The details link is reserved for last and always kept if it fits at all.
    /// </summary>
    internal static string BuildSmsText(NwsAlert alert, int maxLength)
    {
        string header = $"NWS ALERT: {alert.Event}";

        // SPC MCD/Outlook embed the details link at the end of Instruction so platforms that
        // only render Instruction (no separate DetailsUrl line) still show it. SMS appends the
        // link separately below, so strip a trailing duplicate here rather than showing it twice.
        string instruction = alert.Instruction;
        if (!string.IsNullOrWhiteSpace(alert.DetailsUrl) && !string.IsNullOrWhiteSpace(instruction))
        {
            string suffix = "\n" + alert.DetailsUrl;
            if (instruction.EndsWith(suffix, StringComparison.Ordinal))
                instruction = instruction[..^suffix.Length];
        }

        var expiresAt = alert.Ends ?? alert.Expires;
        string detailsLine = !string.IsNullOrWhiteSpace(alert.DetailsUrl) ? $"\nDetails: {alert.DetailsUrl}" : "";

        string[] optionalLines =
        {
            !string.IsNullOrWhiteSpace(alert.AreaDesc) ? $"\n{alert.AreaDesc}" : "",
            expiresAt.HasValue ? $"\nUntil: {expiresAt.Value.ToLocalTime():ddd h:mm tt zzz}" : "",
            !string.IsNullOrWhiteSpace(instruction) ? $"\n{instruction}" : "",
        };

        string body = header;
        int budget = maxLength - detailsLine.Length;
        foreach (var line in optionalLines)
        {
            if (line.Length > 0 && body.Length + line.Length <= budget)
                body += line;
        }

        return TruncateWithEllipsis(body + detailsLine, maxLength);
    }

    /// <summary>Maps NWS severity to a Discord embed color (hex int); shared by Discord + DiscordDm.</summary>
    internal static int DiscordSeverityColor(string? severity) => severity?.ToLower() switch
    {
        "extreme"  => 0xE53935, // red
        "severe"   => 0xFB8C00, // orange
        "moderate" => 0xFDD835, // yellow
        "minor"    => 0x43A047, // green
        _          => 0x757575  // grey
    };

    /// <summary>
    /// Sends to every recipient concurrently and reports overall success only if all sends
    /// succeeded. Callers validate the recipient list themselves first (empty/missing-config
    /// guard clauses differ per platform).
    /// </summary>
    internal static async Task<bool> FanOutAsync<T>(IEnumerable<T> recipients, Func<T, Task<bool>> sendOne)
    {
        var tasks = recipients.Select(sendOne);
        var results = await Task.WhenAll(tasks);
        return results.All(r => r);
    }
}

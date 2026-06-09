namespace NwsAlertBot.Models;

public class NwsAlert
{
    public string Id { get; set; } = "";
    public string Event { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string AreaDesc { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Urgency { get; set; } = "";
    public string Certainty { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string MessageType { get; set; } = "";
    public DateTimeOffset Sent { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public DateTimeOffset? Ends { get; set; }

    /// <summary>
    /// Formats a social media post. Truncates to fit within the given character limit.
    /// </summary>
    public string FormatPost(int maxLength = 500)
    {
        // Use headline if available, otherwise build from parts
        string header = !string.IsNullOrWhiteSpace(Headline)
            ? Headline
            : $"{Event} issued for {AreaDesc}";

        string expiresLine = "";
        var expiresAt = Ends ?? Expires;
        if (expiresAt.HasValue)
            expiresLine = $"\nExpires: {expiresAt.Value.ToLocalTime():ddd MMM d h:mm tt zzz}";

        string issuedBy = !string.IsNullOrWhiteSpace(SenderName)
            ? $"\nIssued by: {SenderName}"
            : "";

        string body = $"⚠️ {header}{expiresLine}{issuedBy}";

        // Append instruction if it fits
        if (!string.IsNullOrWhiteSpace(Instruction))
        {
            string withInstruction = body + $"\n\n{Instruction}";
            if (withInstruction.Length <= maxLength)
                body = withInstruction;
        }

        // Truncate with ellipsis if still over limit
        if (body.Length > maxLength)
            body = body[..(maxLength - 3)] + "...";

        return body;
    }

    public int SeverityRank => Severity?.ToLower() switch
    {
        "extreme" => 4,
        "severe"  => 3,
        "moderate"=> 2,
        "minor"   => 1,
        _         => 0
    };
}

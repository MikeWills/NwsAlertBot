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

    /// <summary>Raw GeoJSON geometry string from the NWS feature. Null when the alert carries no polygon.</summary>
    public string? GeometryJson { get; set; }

    /// <summary>UGC zone/county codes from the alert's geocode block (e.g. ["MNZ083", "MNC013"]).</summary>
    public List<string> GeocodeUgc { get; set; } = new();

    /// <summary>Mapbox static map image URL populated by MapService before posting. Null when map generation is disabled or geometry is unavailable.</summary>
    public string? MapImageUrl { get; set; }

    /// <summary>True when this alert was synthesized by SpcOutlookService rather than fetched from the NWS API.</summary>
    public bool IsSpcOutlook { get; set; }

    private static readonly TimeZoneInfo CentralTime =
        TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

    /// <summary>
    /// Formats a social media post. Truncates to fit within the given character limit.
    /// </summary>
    public string FormatPost(int maxLength = 500)
    {
        // Use headline if available, otherwise build from parts
        string header = !string.IsNullOrWhiteSpace(Headline)
            ? Headline
            : $"{Event} issued for {AreaDesc}";

        string issuedLine;
        string expiresLine = "";
        var expiresAt = Ends ?? Expires;

        if (IsSpcOutlook)
        {
            issuedLine = $"\nValid: {TimeZoneInfo.ConvertTime(Sent, CentralTime):ddd MMM d h:mm tt zzz}";
            if (expiresAt.HasValue)
                expiresLine = $"\nExpires: {TimeZoneInfo.ConvertTime(expiresAt.Value, CentralTime):ddd MMM d h:mm tt zzz}";
        }
        else
        {
            issuedLine = $"\nIssued: {Sent.ToLocalTime():ddd MMM d h:mm tt zzz}";
            if (expiresAt.HasValue)
                expiresLine = $"\nExpires: {expiresAt.Value.ToLocalTime():ddd MMM d h:mm tt zzz}";
        }

        string issuedBy = !string.IsNullOrWhiteSpace(SenderName)
            ? $"\nIssued by: {SenderName}"
            : "";

        string prefix = MessageType?.ToLower() switch
        {
            "cancel" => "✅ CANCELLED: ",
            "update" => "🔄 UPDATE: ",
            _        => "⚠️ "
        };

        string body = $"{prefix}{header}{issuedLine}{expiresLine}{issuedBy}";

        // Append instruction if it fits
        if (!string.IsNullOrWhiteSpace(Instruction))
        {
            string withInstruction = body + $"\n\n{Instruction}";
            if (withInstruction.Length <= maxLength)
                body = withInstruction;
        }

        // Append IEM attribution for SPC outlook map images
        if (IsSpcOutlook)
        {
            const string attribution = "\n\nImages generated via Iowa State University (https://mesonet.agron.iastate.edu/)";
            string withAttribution = body + attribution;
            if (withAttribution.Length <= maxLength)
                body = withAttribution;
        }

        // Truncate with ellipsis if still over limit
        if (body.Length > maxLength)
            body = body[..(maxLength - 3)] + "...";

        return body;
    }

}

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

    /// <summary>VTEC action code parsed from parameters.VTEC (e.g. "NEW", "CON", "EXT", "CAN", "EXP"). Null when no VTEC string is present.</summary>
    public string? VtecAction { get; set; }

    /// <summary>3-letter WFO code from the VTEC string, K-prefix stripped (e.g. "MPX"). Null when no VTEC string is present.</summary>
    public string? VtecWfo { get; set; }

    /// <summary>2-letter VTEC phenomena code (e.g. "HT" for heat, "TO" for tornado, "SV" for severe thunderstorm). Null when no VTEC string is present.</summary>
    public string? VtecPhenomena { get; set; }

    /// <summary>1-letter VTEC significance code (e.g. "W" for warning, "A" for watch, "Y" for advisory). Null when no VTEC string is present.</summary>
    public string? VtecSignificance { get; set; }

    /// <summary>VTEC event tracking number. Null when no VTEC string is present.</summary>
    public int? VtecEtn { get; set; }

    /// <summary>AFOS PIL from parameters.AWIPSidentifier (e.g. "SPSMPX"). Used to build IEM autoplot #217 image URLs for non-VTEC products like SPS.</summary>
    public string? AfosId { get; set; }

    /// <summary>WMO identifier from parameters.WMOidentifier (e.g. "WWUS83 KMPX 011045"). First 6 chars are the WMO routing code used in IEM product IDs.</summary>
    public string? WmoIdentifier { get; set; }

    /// <summary>Mapbox static map image URL populated by MapService before posting. Null when map generation is disabled or geometry is unavailable.</summary>
    public string? MapImageUrl { get; set; }

    /// <summary>Downloaded bytes of the map image, populated by SocialMediaOrchestrator once before all platforms post. Null when no image URL or download failed.</summary>
    public byte[]? MapImageBytes { get; set; }

    /// <summary>Link to the full alert detail record. Null when no such link is available. SMS services (Twilio/VoIP.ms) always include this and preserve it when truncating.</summary>
    public string? DetailsUrl { get; set; }

    /// <summary>True when this alert was synthesized by SpcOutlookService rather than fetched from the NWS API.</summary>
    public bool IsSpcOutlook { get; set; }

    /// <summary>True when this alert was synthesized by SpcMcdService (SPC Mesoscale Discussion).</summary>
    public bool IsSpcMcd { get; set; }

    /// <summary>True when this alert was synthesized by HwoService (Hazardous Weather Outlook). Text-only — no map image.</summary>
    public bool IsHwo { get; set; }

    /// <summary>Pre-cleaned full HWO product text (teletype header/UGC/line-wrap formatting stripped). Only set when IsHwo is true.</summary>
    public string? HwoText { get; set; }

    /// <summary>True when this alert was synthesized by WpcEroService (WPC Excessive Rainfall Outlook).</summary>
    public bool IsEro { get; set; }

    /// <summary>Time zone used to format Valid/Expires on SPC outlook posts. Set from Spc.TimeZone config.</summary>
    public TimeZoneInfo? DisplayTimeZone { get; set; }

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

        if (DisplayTimeZone != null)
        {
            issuedLine = $"\nIssued: {TimeZoneInfo.ConvertTime(Sent, DisplayTimeZone):ddd MMM d h:mm tt}";
            if (expiresAt.HasValue)
            {
                if (IsSpcOutlook)
                {
                    expiresLine = $"\nValid: {TimeZoneInfo.ConvertTime(Sent, DisplayTimeZone):ddd MMM d h:mm tt}" +
                                  $"\nExpires: {TimeZoneInfo.ConvertTime(expiresAt.Value, DisplayTimeZone):ddd MMM d h:mm tt}";
                }
                else
                {
                    expiresLine = $"\nExpires: {TimeZoneInfo.ConvertTime(expiresAt.Value, DisplayTimeZone):ddd MMM d h:mm tt}";
                }
            }
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

        string prefix = IsHwo
            ? "📋 "
            : MessageType?.ToLower() switch
            {
                "cancel" => "✅ CANCELLED: ",
                "update" => "🔄 UPDATE: ",
                _        => "⚠️ "
            };

        string body = $"{prefix}{header}{issuedLine}{expiresLine}{issuedBy}";

        if (IsHwo)
        {
            // HWO has no separate "instruction" field — the full cleaned product text
            // is the entire payload of the post. Always include it; the truncation
            // step below handles platforms with a character limit.
            if (!string.IsNullOrWhiteSpace(HwoText))
                body += $"\n\n{HwoText}";
        }
        else
        {
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
        }

        // Truncate with ellipsis if still over limit
        if (body.Length > maxLength)
            body = body[..(maxLength - 3)] + "...";

        return body;
    }

}

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NwsAlertBot.Services;

/// <summary>
/// Fetches zone/county geometry from the NWS zones API.
/// Shared by MapService (bounding-box fallback) and SpcOutlookService (location resolution)
/// so the fetch + zone-type derivation logic lives in exactly one place.
/// </summary>
public class NwsZoneService
{
    private readonly HttpClient _http;
    private readonly ILogger<NwsZoneService> _logger;

    public NwsZoneService(HttpClient http, ILogger<NwsZoneService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Returns the GeoJSON geometry for a zone/county code (format {ST}Z{###} or {ST}C{###}),
    /// or null if the code is malformed or the fetch fails.
    /// </summary>
    public async Task<JsonElement?> GetGeometryAsync(string code)
    {
        if (code.Length < 3) return null;
        var zoneType = code[2] == 'C' ? "county" : "forecast";

        try
        {
            var resp = await _http.GetAsync($"https://api.weather.gov/zones/{zoneType}/{code}");
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            if (!doc.RootElement.TryGetProperty("geometry", out var geo) ||
                geo.ValueKind == JsonValueKind.Null) return null;

            // Clone so the element survives disposal of its parent JsonDocument.
            return geo.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NwsZone: Failed to fetch geometry for {Code}.", code);
            return null;
        }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NwsAlertBot.Services;

/// <summary>
/// Fetches zone/county geometry and metadata from the NWS zones API.
/// Shared by MapService (bounding-box fallback) and SpcOutlookService (location resolution
/// and outlook image URLs) so the fetch + zone-type derivation logic lives in exactly one place.
/// Results are cached in memory for the lifetime of the process.
/// </summary>
public class NwsZoneService
{
    private readonly HttpClient _http;
    private readonly ILogger<NwsZoneService> _logger;
    private readonly ConcurrentDictionary<string, ZoneInfo?> _cache = new();

    public NwsZoneService(HttpClient http, ILogger<NwsZoneService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Geometry plus the responsible forecast office (CWA) and state for a zone/county.</summary>
    public record ZoneInfo(JsonElement Geometry, string? Cwa, string? State);

    /// <summary>
    /// Returns the GeoJSON geometry for a zone/county code (format {ST}Z{###} or {ST}C{###}),
    /// or null if the code is malformed or the fetch fails.
    /// </summary>
    public async Task<JsonElement?> GetGeometryAsync(string code) =>
        (await GetZoneInfoAsync(code))?.Geometry;

    /// <summary>
    /// Returns geometry plus the responsible WFO (CWA) code and state for a zone/county code
    /// (format {ST}Z{###} or {ST}C{###}), or null if the code is malformed or the fetch fails.
    /// </summary>
    public async Task<ZoneInfo?> GetZoneInfoAsync(string code)
    {
        if (code.Length < 3) return null;
        code = code.ToUpperInvariant();

        if (_cache.TryGetValue(code, out var cached)) return cached;

        var zoneType = code[2] == 'C' ? "county" : "forecast";

        try
        {
            var resp = await _http.GetAsync($"https://api.weather.gov/zones/{zoneType}/{code}");
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("geometry", out var geo) || geo.ValueKind == JsonValueKind.Null)
                return null;

            string? cwa = null, state = null;
            if (root.TryGetProperty("properties", out var props))
            {
                if (props.TryGetProperty("cwa", out var cwaEl) &&
                    cwaEl.ValueKind == JsonValueKind.Array && cwaEl.GetArrayLength() > 0)
                    cwa = cwaEl[0].GetString();

                if (props.TryGetProperty("state", out var stateEl))
                    state = stateEl.GetString();
            }

            // Clone so the element survives disposal of its parent JsonDocument.
            var info = new ZoneInfo(geo.Clone(), cwa, state);
            _cache[code] = info;
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NwsZone: Failed to fetch zone info for {Code}.", code);
            return null;
        }
    }
}

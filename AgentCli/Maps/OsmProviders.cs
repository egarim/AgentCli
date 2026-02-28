using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace AgentCli;

// ─── Nominatim (OSM) — free geocoder ─────────────────────────────────────────

/// <summary>
/// OpenStreetMap Nominatim geocoder.
/// Free, no API key required.
/// Rate limit: 1 request/second (enforced by OSM fair-use policy).
/// Do NOT use for high-volume production — self-host or use a paid provider instead.
/// Env var: none required. Optional NOMINATIM_BASE_URL to point at self-hosted instance.
/// </summary>
public sealed class NominatimGeocoder : IGeocoder
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;
    private DateTime            _lastRequest = DateTime.MinValue;

    public string Id          => "nominatim";
    public string DisplayName => "Nominatim (OSM, free)";

    public NominatimGeocoder(HttpClient http, string? baseUrl = null)
    {
        _http    = http;
        _baseUrl = (baseUrl ?? "https://nominatim.openstreetmap.org").TrimEnd('/');
    }

    public async Task<NormalizedLocation?> GeocodeAsync(
        string query, CancellationToken ct = default)
    {
        await RespectRateLimitAsync(ct);
        var url  = $"{_baseUrl}/search?q={Uri.EscapeDataString(query)}&format=jsonv2&limit=1&addressdetails=1";
        var req  = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", "AgentCli/1.0 (https://github.com/egarim/AgentCli)");
        var res  = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: ct);
        if (json == null || json.Length == 0) return null;

        var item = json[0];
        return new NormalizedLocation(
            Latitude:  item.GetProperty("lat").GetDouble(),
            Longitude: item.GetProperty("lon").GetDouble(),
            Name:      item.TryGetProperty("display_name", out var dn) ? dn.GetString() : null,
            Address:   item.TryGetProperty("display_name", out var dn2) ? dn2.GetString() : null,
            Source:    "geocoded"
        );
    }

    public async Task<NormalizedLocation?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        await RespectRateLimitAsync(ct);
        var url = $"{_baseUrl}/reverse?lat={latitude}&lon={longitude}&format=jsonv2&addressdetails=1";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", "AgentCli/1.0 (https://github.com/egarim/AgentCli)");
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var item = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (item.ValueKind == JsonValueKind.Null) return null;

        var displayName = item.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
        var name        = item.TryGetProperty("name", out var n) ? n.GetString() : null;

        return new NormalizedLocation(
            Latitude:  latitude,
            Longitude: longitude,
            Name:      name ?? displayName?.Split(',')[0].Trim(),
            Address:   displayName,
            Source:    "geocoded"
        );
    }

    // Nominatim fair-use: max 1 req/s
    private async Task RespectRateLimitAsync(CancellationToken ct)
    {
        var elapsed = DateTime.UtcNow - _lastRequest;
        if (elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, ct);
        _lastRequest = DateTime.UtcNow;
    }
}

// ─── OSM Overpass — free place search ────────────────────────────────────────

/// <summary>
/// OpenStreetMap Overpass API for nearby place search.
/// Free, no API key. Rate limited at overpass-api.de.
/// For production: self-host overpass or use Google/Azure.
/// Env var: optional OVERPASS_BASE_URL to point at self-hosted instance.
/// </summary>
public sealed class OsmOverpassPlaceSearch : IPlaceSearch
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    public string Id          => "osm-overpass";
    public string DisplayName => "OpenStreetMap Overpass (free)";

    // Maps common English search terms to OSM amenity tags
    private static readonly Dictionary<string, string> _osmTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["coffee"]      = "amenity=cafe",
        ["cafe"]        = "amenity=cafe",
        ["restaurant"]  = "amenity=restaurant",
        ["food"]        = "amenity=restaurant",
        ["bar"]         = "amenity=bar",
        ["pharmacy"]    = "amenity=pharmacy",
        ["hospital"]    = "amenity=hospital",
        ["atm"]         = "amenity=atm",
        ["bank"]        = "amenity=bank",
        ["hotel"]       = "tourism=hotel",
        ["gas station"] = "amenity=fuel",
        ["fuel"]        = "amenity=fuel",
        ["supermarket"] = "shop=supermarket",
        ["grocery"]     = "shop=supermarket",
        ["parking"]     = "amenity=parking",
    };

    public OsmOverpassPlaceSearch(HttpClient http, string? baseUrl = null)
    {
        _http    = http;
        _baseUrl = (baseUrl ?? "https://overpass-api.de/api").TrimEnd('/');
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(
        string             query,
        NormalizedLocation near,
        int                maxResults   = 5,
        double?            radiusMeters = null,
        CancellationToken  ct = default)
    {
        var radius = (int)(radiusMeters ?? 1000);
        var tag    = _osmTagMap.TryGetValue(query, out var t) ? t : $"name~\"{query}\",i";
        var ql     = $"""
            [out:json][timeout:10];
            (
              node[{tag}](around:{radius},{near.Latitude},{near.Longitude});
              way[{tag}](around:{radius},{near.Latitude},{near.Longitude});
            );
            out center {maxResults};
            """;

        var res  = await _http.PostAsync($"{_baseUrl}/interpreter",
            new FormUrlEncodedContent([new("data", ql)]), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var results = new List<PlaceResult>();
        if (!json.TryGetProperty("elements", out var elements)) return results;

        foreach (var el in elements.EnumerateArray())
        {
            var tags = el.TryGetProperty("tags", out var t2) ? t2 : default;
            var name = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("name", out var n)
                ? n.GetString() : null;
            if (name == null) continue;

            double lat, lon;
            if (el.TryGetProperty("lat", out var latEl))
            {
                lat = latEl.GetDouble();
                lon = el.GetProperty("lon").GetDouble();
            }
            else if (el.TryGetProperty("center", out var center))
            {
                lat = center.GetProperty("lat").GetDouble();
                lon = center.GetProperty("lon").GetDouble();
            }
            else continue;

            var addr = tags.ValueKind == JsonValueKind.Object
                ? BuildOsmAddress(tags) : null;

            results.Add(new PlaceResult(
                PlaceId:   $"osm:{el.GetProperty("type").GetString()}:{el.GetProperty("id").GetInt64()}",
                Name:      name,
                Address:   addr,
                Location:  new NormalizedLocation(lat, lon, Name: name, Address: addr, Source: "place"),
                Rating:    null,
                PriceLevel: null,
                IsOpen:    null,
                Types:     ResolveOsmTypes(tags),
                ProviderId: Id
            ));

            if (results.Count >= maxResults) break;
        }

        return results;
    }

    public Task<PlaceDetails?> GetDetailsAsync(string placeId, CancellationToken ct = default)
        // Overpass doesn't have a details endpoint — return null, caller uses summary
        => Task.FromResult<PlaceDetails?>(null);

    private static string? BuildOsmAddress(JsonElement tags)
    {
        var parts = new[] { "addr:housenumber", "addr:street", "addr:city", "addr:country" }
            .Select(k => tags.TryGetProperty(k, out var v) ? v.GetString() : null)
            .Where(s => s != null)
            .ToArray();
        return parts.Length > 0 ? string.Join(", ", parts) : null;
    }

    private static string[] ResolveOsmTypes(JsonElement tags)
    {
        if (tags.ValueKind != JsonValueKind.Object) return [];
        var types = new List<string>();
        if (tags.TryGetProperty("amenity",  out var a)) types.Add(a.GetString()!);
        if (tags.TryGetProperty("shop",     out var s)) types.Add(s.GetString()!);
        if (tags.TryGetProperty("tourism",  out var t)) types.Add(t.GetString()!);
        if (tags.TryGetProperty("leisure",  out var l)) types.Add(l.GetString()!);
        return types.ToArray();
    }
}

// ─── OSM Static Map — free renderer ──────────────────────────────────────────

/// <summary>
/// Free static map images via staticmap.openstreetmap.de.
/// No API key required. Attribution required: "© OpenStreetMap contributors".
/// Max size: 1024×1024. For production: self-host with staticmap or use paid provider.
/// Env var: optional OSM_STATIC_BASE_URL to point at self-hosted instance.
/// </summary>
public sealed class OsmStaticMapRenderer : IStaticMapRenderer
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    public string Id          => "osm-static";
    public string DisplayName => "OpenStreetMap Static Maps (free)";

    public OsmStaticMapRenderer(HttpClient http, string? baseUrl = null)
    {
        _http    = http;
        _baseUrl = (baseUrl ?? "https://staticmap.openstreetmap.de").TrimEnd('/');
    }

    public async Task<MapImage> RenderAsync(
        NormalizedLocation center,
        int  zoom   = 15,
        int  width  = 600,
        int  height = 400,
        CancellationToken ct = default)
    {
        // Clamp to service limits
        width  = Math.Clamp(width,  100, 1024);
        height = Math.Clamp(height, 100, 1024);
        zoom   = Math.Clamp(zoom,   1,   19);

        var url = $"{_baseUrl}/staticmap.php" +
                  $"?center={center.Latitude},{center.Longitude}" +
                  $"&zoom={zoom}" +
                  $"&size={width}x{height}" +
                  $"&markers={center.Latitude},{center.Longitude},red-pushpin";

        var res = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return new MapImage(
            Bytes:           bytes,
            MimeType:        "image/png",
            Width:           width,
            Height:          height,
            ProviderId:      Id,
            AttributionText: "© OpenStreetMap contributors"
        );
    }
}

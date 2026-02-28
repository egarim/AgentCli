namespace AgentCli;

// ─── Core location types ──────────────────────────────────────────────────────

/// <summary>
/// Normalized location — the single currency that flows through the whole Maps subsystem.
/// Built from inbound channel messages, geocoder results, or place search results.
/// </summary>
public sealed record NormalizedLocation(
    double  Latitude,
    double  Longitude,
    double? AccuracyMeters = null,
    string? Name           = null,   // "Starbucks"
    string? Address        = null,   // "Mariahilfer Str. 1, 1060 Vienna"
    string? PlaceId        = null,   // provider-specific place ID (for detail lookups)
    bool    IsLive         = false,  // live GPS tracking location
    string  Source         = "pin"   // "pin" | "place" | "live" | "geocoded"
)
{
    /// <summary>Human-readable text shown to the agent as message Body.</summary>
    public string ToText()
    {
        var coords = $"{Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}";
        var acc    = AccuracyMeters.HasValue ? $" (~{(int)Math.Ceiling(AccuracyMeters.Value)}m)" : "";

        if (IsLive || Source == "live")
            return $"🛰 Live location: {coords}{acc}";

        if (Name != null || Address != null)
        {
            var label = new[] { Name, Address }.Where(s => s != null).Aggregate((a, b) => $"{a} — {b}");
            return $"📍 {label} ({coords}{acc})";
        }

        return $"📍 {coords}{acc}";
    }

    /// <summary>OpenStreetMap browse URL for this point.</summary>
    public string ToOsmUrl()
    {
        var lat = Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var lon = Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        return $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=15/{lat}/{lon}";
    }

    /// <summary>Google Maps URL for this point.</summary>
    public string ToGoogleMapsUrl()
    {
        var lat = Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var lon = Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        return Name != null
            ? $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(Name)}&query_place_id={Uri.EscapeDataString(PlaceId ?? "")}"
            : $"https://www.google.com/maps/search/?api=1&query={lat},{lon}";
    }
}

/// <summary>A place result from a search query.</summary>
public sealed record PlaceResult(
    string           PlaceId,        // provider-specific ID
    string           Name,
    string?          Address,
    NormalizedLocation Location,
    double?          Rating,         // 0.0–5.0 if available
    int?             PriceLevel,     // 0–4 (free → very expensive)
    bool?            IsOpen,         // null = unknown
    string[]         Types,          // ["restaurant", "food"]
    string           ProviderId
);

/// <summary>Detailed place info — extended PlaceResult.</summary>
public sealed record PlaceDetails(
    PlaceResult      Summary,
    string?          PhoneNumber,
    string?          Website,
    string?          OpeningHours,   // formatted multi-line string
    string[]         Photos,         // photo reference IDs or URLs
    string?          GoogleMapsUrl
);

/// <summary>A rendered map image.</summary>
public sealed record MapImage(
    byte[]  Bytes,
    string  MimeType,               // "image/png" or "image/jpeg"
    int     Width,
    int     Height,
    string  ProviderId,
    string? AttributionText        // e.g. "© OpenStreetMap contributors"
);

// ─── Interfaces ───────────────────────────────────────────────────────────────

/// <summary>
/// Forward + reverse geocoding.
/// Forward: address string → NormalizedLocation
/// Reverse: lat/lon → NormalizedLocation with name + address filled
///
/// Providers: Nominatim (free, OSM), Google Geocoding ($0.005/req), Azure Maps (free 250k/month)
/// </summary>
public interface IGeocoder
{
    string Id          { get; }
    string DisplayName { get; }

    /// <summary>Address/place name → location. Returns null if not found.</summary>
    Task<NormalizedLocation?> GeocodeAsync(
        string query, CancellationToken ct = default);

    /// <summary>Coordinates → address + name. Returns null if reverse geocode fails.</summary>
    Task<NormalizedLocation?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default);
}

/// <summary>
/// Nearby place search + place detail lookup.
///
/// Providers: Google Places New ($0.032/req text search, $0.017/req nearby),
///            OpenStreetMap Overpass (free), Azure Maps POI (free 250k/month)
/// </summary>
public interface IPlaceSearch
{
    string Id          { get; }
    string DisplayName { get; }

    /// <summary>
    /// Text search near a location.
    /// e.g. query="coffee", near=Vienna → top coffee shops.
    /// </summary>
    Task<IReadOnlyList<PlaceResult>> SearchAsync(
        string             query,
        NormalizedLocation near,
        int                maxResults = 5,
        double?            radiusMeters = null,
        CancellationToken  ct = default);

    /// <summary>Fetch extended details for a place by its provider-specific ID.</summary>
    Task<PlaceDetails?> GetDetailsAsync(
        string placeId, CancellationToken ct = default);
}

/// <summary>
/// Renders a static map image for a location.
///
/// Providers:
///   osm-static      — free, no key, OpenStreetMap tiles via staticmap.openstreetmap.de
///   google-static   — $0.002/map, requires GOOGLE_MAPS_API_KEY
///   azure-maps      — free 250k/month, requires AZURE_MAPS_KEY
///   mapbox-static   — 50k free/month, requires MAPBOX_TOKEN
/// </summary>
public interface IStaticMapRenderer
{
    string Id          { get; }
    string DisplayName { get; }

    /// <summary>Render a map image centered on a location with optional marker.</summary>
    Task<MapImage> RenderAsync(
        NormalizedLocation center,
        int                zoom   = 15,
        int                width  = 600,
        int                height = 400,
        CancellationToken  ct     = default);
}

// ─── Options ──────────────────────────────────────────────────────────────────

/// <summary>Runtime configuration for the Maps subsystem.</summary>
public sealed class MapOptions
{
    /// <summary>Provider ID for geocoding. Default: "nominatim" (free OSM).</summary>
    public string  GeocoderProvider        { get; set; } = "nominatim";

    /// <summary>Fallback geocoder if primary fails. Null = no fallback.</summary>
    public string? GeocoderFallback        { get; set; } = null;

    /// <summary>Provider ID for place search. Default: "osm-overpass" (free OSM).</summary>
    public string  PlaceSearchProvider     { get; set; } = "osm-overpass";

    /// <summary>Fallback place search provider. Null = no fallback.</summary>
    public string? PlaceSearchFallback     { get; set; } = null;

    /// <summary>Provider ID for static map rendering. Default: "osm-static" (free).</summary>
    public string  MapRenderProvider       { get; set; } = "osm-static";

    /// <summary>Fallback map render provider. Null = no fallback.</summary>
    public string? MapRenderFallback       { get; set; } = null;

    /// <summary>Whether to include a map image in replies when a location is shared. Default: true.</summary>
    public bool    RenderMapOnInbound      { get; set; } = true;

    /// <summary>Whether to include a map link (OSM/Google) in replies. Default: true.</summary>
    public bool    IncludeMapLink          { get; set; } = true;

    /// <summary>
    /// Whether to reverse-geocode inbound pin locations to enrich them with name+address.
    /// Costs one geocoder request per inbound location. Default: false (to avoid surprise costs).
    /// </summary>
    public bool    ReverseGeocodeInbound   { get; set; } = false;

    /// <summary>Default map zoom level (1=world, 18=building). Default: 15.</summary>
    public int     DefaultZoom             { get; set; } = 15;

    /// <summary>Default rendered map width px. Default: 600.</summary>
    public int     DefaultWidth            { get; set; } = 600;

    /// <summary>Default rendered map height px. Default: 400.</summary>
    public int     DefaultHeight           { get; set; } = 400;

    /// <summary>Format string for map reply prefix. {0} = location text. Default: location text as-is.</summary>
    public string  LocationFormat          { get; set; } = "{0}";

    /// <summary>Fully free tier — no API keys, no costs. OSM Nominatim + Overpass + static tiles.</summary>
    public static MapOptions Free => new()
    {
        GeocoderProvider    = "nominatim",
        PlaceSearchProvider = "osm-overpass",
        MapRenderProvider   = "osm-static",
    };

    /// <summary>Google everywhere — best quality, higher cost.</summary>
    public static MapOptions Google => new()
    {
        GeocoderProvider    = "google-geocoding",
        GeocoderFallback    = "nominatim",
        PlaceSearchProvider = "google-places",
        PlaceSearchFallback = "osm-overpass",
        MapRenderProvider   = "google-static",
        MapRenderFallback   = "osm-static",
    };

    /// <summary>Azure Maps for geocoding+rendering (free 250k/month), Google for places search.</summary>
    public static MapOptions AzureWithGooglePlaces => new()
    {
        GeocoderProvider    = "azure-maps",
        GeocoderFallback    = "nominatim",
        PlaceSearchProvider = "google-places",
        PlaceSearchFallback = "osm-overpass",
        MapRenderProvider   = "azure-maps",
        MapRenderFallback   = "osm-static",
    };
}

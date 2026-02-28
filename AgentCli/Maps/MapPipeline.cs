namespace AgentCli;

// ─── MapRegistry ──────────────────────────────────────────────────────────────

/// <summary>
/// Holds all registered map providers, separated by capability.
/// Each capability (geocoding, place search, map rendering) is independent —
/// you can mix providers freely to minimize cost.
///
/// Auto-registration via Build() reads environment variables in priority order:
///   Free (no key):  nominatim, osm-overpass, osm-static
///   Cheap:          azure-maps (free 250k/month) — AZURE_MAPS_KEY
///   Quality:        google-geocoding, google-places, google-static — GOOGLE_MAPS_API_KEY
///   Tiles:          mapbox-static (50k free/month) — MAPBOX_TOKEN
/// </summary>
public sealed class MapRegistry
{
    private readonly Dictionary<string, IGeocoder>         _geocoders   = new();
    private readonly Dictionary<string, IPlaceSearch>      _searchers   = new();
    private readonly Dictionary<string, IStaticMapRenderer> _renderers  = new();

    public void RegisterGeocoder(IGeocoder g)         => _geocoders[g.Id]  = g;
    public void RegisterPlaceSearch(IPlaceSearch s)   => _searchers[s.Id]  = s;
    public void RegisterRenderer(IStaticMapRenderer r) => _renderers[r.Id] = r;

    public IGeocoder?          GetGeocoder(string id)  => _geocoders.GetValueOrDefault(id);
    public IPlaceSearch?       GetSearcher(string id)  => _searchers.GetValueOrDefault(id);
    public IStaticMapRenderer? GetRenderer(string id)  => _renderers.GetValueOrDefault(id);

    public IReadOnlyList<IGeocoder>          AllGeocoders  => _geocoders.Values.ToList();
    public IReadOnlyList<IPlaceSearch>       AllSearchers  => _searchers.Values.ToList();
    public IReadOnlyList<IStaticMapRenderer> AllRenderers  => _renderers.Values.ToList();

    /// <summary>
    /// Build registry from environment variables + options.
    /// Always registers free OSM providers as baseline.
    /// Adds paid providers when their env vars are present.
    /// </summary>
    public static MapRegistry Build(MapOptions options, HttpClient http)
    {
        var reg = new MapRegistry();

        // ── Always register free OSM providers ────────────────────────────────
        var osmBase = Environment.GetEnvironmentVariable("NOMINATIM_BASE_URL");
        reg.RegisterGeocoder(new NominatimGeocoder(http, osmBase));

        var overpassBase = Environment.GetEnvironmentVariable("OVERPASS_BASE_URL");
        reg.RegisterPlaceSearch(new OsmOverpassPlaceSearch(http, overpassBase));

        var osmStaticBase = Environment.GetEnvironmentVariable("OSM_STATIC_BASE_URL");
        reg.RegisterRenderer(new OsmStaticMapRenderer(http, osmStaticBase));

        // ── Azure Maps (free 250k/month) ──────────────────────────────────────
        var azureKey = Environment.GetEnvironmentVariable("AZURE_MAPS_KEY");
        if (!string.IsNullOrEmpty(azureKey))
        {
            var azure = new AzureMapsProvider(http, azureKey);
            reg.RegisterGeocoder(azure);    // IGeocoder
            reg.RegisterRenderer(azure);    // IStaticMapRenderer
        }

        // ── Google Maps (pay-per-use) ─────────────────────────────────────────
        var googleKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
        if (!string.IsNullOrEmpty(googleKey))
        {
            reg.RegisterGeocoder(new GoogleGeocodingProvider(http, googleKey));
            reg.RegisterPlaceSearch(new GooglePlacesProvider(http, googleKey));
            reg.RegisterRenderer(new GoogleStaticMapRenderer(http, googleKey));
        }

        return reg;
    }
}

// ─── MapPipeline ──────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates geocoding, place search, and map rendering.
/// All methods support primary + fallback provider per capability.
/// Mirrors the AudioPipeline pattern: inject into InboundMessageRouter.
///
/// Typical inbound flow:
///   1. Channel delivers NormalizedLocation (from Telegram location message)
///   2. MapPipeline.EnrichAsync() — optional reverse geocode to add name+address
///   3. MapPipeline.RenderAsync() — generate static map image
///   4. Reply: location text + map image + OSM/Google link
///
/// Typical agent tool flow:
///   1. Agent calls SearchNearbyAsync("coffee", near: userLocation)
///   2. Returns PlaceResult[] — agent formats and replies
///   3. Agent optionally calls RenderAsync() to show map
/// </summary>
public sealed class MapPipeline
{
    private readonly MapRegistry _registry;
    private readonly MapOptions  _options;

    public MapPipeline(MapRegistry registry, MapOptions options)
    {
        _registry = registry;
        _options  = options;
    }

    // ─── Geocoding ────────────────────────────────────────────────────────────

    /// <summary>
    /// Forward geocode: address/place name → NormalizedLocation.
    /// Uses primary geocoder, falls back if configured.
    /// </summary>
    public async Task<NormalizedLocation?> GeocodeAsync(
        string query, CancellationToken ct = default)
        => await WithFallbackAsync(
            primaryId:  _options.GeocoderProvider,
            fallbackId: _options.GeocoderFallback,
            getProvider: id => _registry.GetGeocoder(id),
            call: g => g.GeocodeAsync(query, ct),
            capabilityName: "geocoder");

    /// <summary>
    /// Reverse geocode: lat/lon → NormalizedLocation with name+address.
    /// </summary>
    public async Task<NormalizedLocation?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default)
        => await WithFallbackAsync(
            primaryId:  _options.GeocoderProvider,
            fallbackId: _options.GeocoderFallback,
            getProvider: id => _registry.GetGeocoder(id),
            call: g => g.ReverseGeocodeAsync(latitude, longitude, ct),
            capabilityName: "geocoder");

    /// <summary>
    /// Enrich an inbound location with reverse-geocoded name+address.
    /// Only runs when MapOptions.ReverseGeocodeInbound = true.
    /// Returns original location unchanged if disabled or geocoder fails.
    /// </summary>
    public async Task<NormalizedLocation> EnrichAsync(
        NormalizedLocation location, CancellationToken ct = default)
    {
        if (!_options.ReverseGeocodeInbound) return location;
        if (location.Name != null && location.Address != null) return location; // already enriched

        try
        {
            var enriched = await ReverseGeocodeAsync(location.Latitude, location.Longitude, ct);
            if (enriched == null) return location;
            return location with
            {
                Name    = location.Name    ?? enriched.Name,
                Address = location.Address ?? enriched.Address,
            };
        }
        catch { return location; } // geocoder failure is non-fatal
    }

    // ─── Place search ─────────────────────────────────────────────────────────

    /// <summary>
    /// Search for places near a location.
    /// e.g. query="coffee shops", near=userLocation, maxResults=5
    /// </summary>
    public async Task<IReadOnlyList<PlaceResult>> SearchNearbyAsync(
        string             query,
        NormalizedLocation near,
        int                maxResults   = 5,
        double?            radiusMeters = null,
        CancellationToken  ct = default)
    {
        var primary = _registry.GetSearcher(_options.PlaceSearchProvider);
        if (primary == null)
        {
            var fallback = _options.PlaceSearchFallback != null
                ? _registry.GetSearcher(_options.PlaceSearchFallback) : null;
            if (fallback == null)
                throw new MapPipelineException("place-search",
                    $"No place search provider available. Configured: '{_options.PlaceSearchProvider}'.");
            return await fallback.SearchAsync(query, near, maxResults, radiusMeters, ct);
        }

        try
        {
            return await primary.SearchAsync(query, near, maxResults, radiusMeters, ct);
        }
        catch when (_options.PlaceSearchFallback != null)
        {
            var fallback = _registry.GetSearcher(_options.PlaceSearchFallback!);
            if (fallback != null)
                return await fallback.SearchAsync(query, near, maxResults, radiusMeters, ct);
            throw;
        }
    }

    /// <summary>Get extended details for a place by its provider-specific ID.</summary>
    public async Task<PlaceDetails?> GetPlaceDetailsAsync(
        string placeId, CancellationToken ct = default)
    {
        var provider = _registry.GetSearcher(_options.PlaceSearchProvider)
                    ?? (_options.PlaceSearchFallback != null
                        ? _registry.GetSearcher(_options.PlaceSearchFallback!)
                        : null);
        if (provider == null) return null;
        return await provider.GetDetailsAsync(placeId, ct);
    }

    // ─── Map rendering ────────────────────────────────────────────────────────

    /// <summary>
    /// Render a static map image for a location.
    /// Returns null if no renderer is available (non-fatal — callers omit the image).
    /// </summary>
    public async Task<MapImage?> RenderAsync(
        NormalizedLocation center,
        int?               zoom   = null,
        int?               width  = null,
        int?               height = null,
        CancellationToken  ct = default)
    {
        if (!_options.RenderMapOnInbound) return null;

        try
        {
            return await WithFallbackAsync(
                primaryId:      _options.MapRenderProvider,
                fallbackId:     _options.MapRenderFallback,
                getProvider:    id => _registry.GetRenderer(id),
                call:           r  => r.RenderAsync(center,
                                    zoom   ?? _options.DefaultZoom,
                                    width  ?? _options.DefaultWidth,
                                    height ?? _options.DefaultHeight, ct),
                capabilityName: "map-renderer");
        }
        catch
        {
            // Map rendering failure is always non-fatal — reply with text only
            return null;
        }
    }

    // ─── Formatting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Build the text portion of a location reply.
    /// Includes location text + optional map link.
    /// </summary>
    public string FormatLocationReply(NormalizedLocation location)
    {
        var text = string.Format(_options.LocationFormat, location.ToText());

        if (_options.IncludeMapLink)
            text += $"\n🗺 {location.ToOsmUrl()}";

        return text;
    }

    // ─── Provider status ──────────────────────────────────────────────────────

    public bool HasGeocoder    => _registry.GetGeocoder(_options.GeocoderProvider)    != null;
    public bool HasPlaceSearch => _registry.GetSearcher(_options.PlaceSearchProvider) != null;
    public bool HasMapRenderer => _registry.GetRenderer(_options.MapRenderProvider)   != null;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<T?> WithFallbackAsync<TProvider, T>(
        string               primaryId,
        string?              fallbackId,
        Func<string, TProvider?> getProvider,
        Func<TProvider, Task<T?>> call,
        string               capabilityName)
        where TProvider : class
        where T : class
    {
        var primary = getProvider(primaryId);
        if (primary == null && fallbackId == null)
            throw new MapPipelineException(capabilityName,
                $"No {capabilityName} available. Configured: '{primaryId}'.");

        if (primary != null)
        {
            try { return await call(primary); }
            catch when (fallbackId != null) { /* fall through */ }
        }

        if (fallbackId == null) return null;
        var fallback = getProvider(fallbackId)
            ?? throw new MapPipelineException(capabilityName,
                $"Fallback {capabilityName} '{fallbackId}' not found in registry.");
        return await call(fallback);
    }
}

/// <summary>Thrown when a map pipeline operation fails on all configured providers.</summary>
public sealed class MapPipelineException : Exception
{
    public string Capability { get; }
    public MapPipelineException(string capability, string message, Exception? inner = null)
        : base(message, inner) => Capability = capability;
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCli;

// ─── Google Geocoding ─────────────────────────────────────────────────────────

/// <summary>
/// Google Geocoding API — forward + reverse geocoding.
/// Cost: $0.005 per request (after free $200/month credit).
/// Env var: GOOGLE_MAPS_API_KEY (same key used for Places and Static Maps).
/// Docs: https://developers.google.com/maps/documentation/geocoding
/// </summary>
public sealed class GoogleGeocodingProvider : IGeocoder
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;

    public string Id          => "google-geocoding";
    public string DisplayName => "Google Geocoding ($0.005/req)";

    public GoogleGeocodingProvider(HttpClient http, string apiKey)
    {
        _http   = http;
        _apiKey = apiKey;
    }

    public async Task<NormalizedLocation?> GeocodeAsync(
        string query, CancellationToken ct = default)
    {
        var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                  $"?address={Uri.EscapeDataString(query)}&key={_apiKey}";

        var res  = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (json.GetProperty("status").GetString() != "OK") return null;
        var result = json.GetProperty("results")[0];
        return ParseGoogleResult(result);
    }

    public async Task<NormalizedLocation?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                  $"?latlng={latitude},{longitude}&key={_apiKey}";

        var res  = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (json.GetProperty("status").GetString() != "OK") return null;
        var result = json.GetProperty("results")[0];
        return ParseGoogleResult(result) with
        {
            Latitude  = latitude,
            Longitude = longitude
        };
    }

    internal static NormalizedLocation ParseGoogleResult(JsonElement result)
    {
        var loc     = result.GetProperty("geometry").GetProperty("location");
        var lat     = loc.GetProperty("lat").GetDouble();
        var lng     = loc.GetProperty("lng").GetDouble();
        var address = result.TryGetProperty("formatted_address", out var fa) ? fa.GetString() : null;
        var placeId = result.TryGetProperty("place_id", out var pid) ? pid.GetString() : null;

        // Extract short name from address_components
        string? name = null;
        if (result.TryGetProperty("address_components", out var comps))
        {
            foreach (var comp in comps.EnumerateArray())
            {
                if (comp.TryGetProperty("types", out var types))
                {
                    var hasName = types.EnumerateArray()
                        .Any(t => t.GetString() is "establishment" or "point_of_interest");
                    if (hasName)
                    {
                        name = comp.GetProperty("long_name").GetString();
                        break;
                    }
                }
            }
        }

        return new NormalizedLocation(lat, lng,
            Name:    name,
            Address: address,
            PlaceId: placeId,
            Source:  "geocoded");
    }
}

// ─── Google Places (New API) ──────────────────────────────────────────────────

/// <summary>
/// Google Places API (New) — text search + place details.
/// Cost: $0.032/req text search, $0.017/req nearby, $0.017/req details.
/// Env var: GOOGLE_MAPS_API_KEY
/// Docs: https://developers.google.com/maps/documentation/places/web-service
/// </summary>
public sealed class GooglePlacesProvider : IPlaceSearch
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;

    public string Id          => "google-places";
    public string DisplayName => "Google Places New ($0.032/req text, $0.017/req nearby)";

    public GooglePlacesProvider(HttpClient http, string apiKey)
    {
        _http   = http;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(
        string             query,
        NormalizedLocation near,
        int                maxResults   = 5,
        double?            radiusMeters = null,
        CancellationToken  ct = default)
    {
        var radius = (int)(radiusMeters ?? 1000);

        // Use Text Search (New) with location bias
        var body = new
        {
            textQuery         = query,
            maxResultCount    = maxResults,
            locationBias      = new
            {
                circle = new
                {
                    center = new { latitude = near.Latitude, longitude = near.Longitude },
                    radius = (double)radius
                }
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText");
        req.Headers.Add("X-Goog-Api-Key", _apiKey);
        req.Headers.Add("X-Goog-FieldMask",
            "places.id,places.displayName,places.formattedAddress," +
            "places.location,places.rating,places.priceLevel," +
            "places.regularOpeningHours.openNow,places.types");
        req.Content = JsonContent.Create(body);

        var res  = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var results = new List<PlaceResult>();
        if (!json.TryGetProperty("places", out var places)) return results;

        foreach (var place in places.EnumerateArray())
        {
            var placeId = place.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            var name    = place.TryGetProperty("displayName", out var dn)
                          && dn.TryGetProperty("text", out var dnt) ? dnt.GetString() ?? "" : "";
            var address = place.TryGetProperty("formattedAddress", out var fa) ? fa.GetString() : null;

            double lat = 0, lon = 0;
            if (place.TryGetProperty("location", out var loc))
            {
                lat = loc.GetProperty("latitude").GetDouble();
                lon = loc.GetProperty("longitude").GetDouble();
            }

            double? rating = null;
            if (place.TryGetProperty("rating", out var r)) rating = r.GetDouble();

            int? priceLevel = null;
            if (place.TryGetProperty("priceLevel", out var pl))
                priceLevel = pl.GetString() switch
                {
                    "PRICE_LEVEL_FREE"             => 0,
                    "PRICE_LEVEL_INEXPENSIVE"      => 1,
                    "PRICE_LEVEL_MODERATE"         => 2,
                    "PRICE_LEVEL_EXPENSIVE"        => 3,
                    "PRICE_LEVEL_VERY_EXPENSIVE"   => 4,
                    _ => null
                };

            bool? isOpen = null;
            if (place.TryGetProperty("regularOpeningHours", out var hours)
                && hours.TryGetProperty("openNow", out var on))
                isOpen = on.GetBoolean();

            var types = place.TryGetProperty("types", out var t)
                ? t.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : Array.Empty<string>();

            results.Add(new PlaceResult(
                PlaceId:    placeId,
                Name:       name,
                Address:    address,
                Location:   new NormalizedLocation(lat, lon, Name: name, Address: address,
                                PlaceId: placeId, Source: "place"),
                Rating:     rating,
                PriceLevel: priceLevel,
                IsOpen:     isOpen,
                Types:      types,
                ProviderId: Id
            ));
        }

        return results;
    }

    public async Task<PlaceDetails?> GetDetailsAsync(
        string placeId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(placeId)}");
        req.Headers.Add("X-Goog-Api-Key", _apiKey);
        req.Headers.Add("X-Goog-FieldMask",
            "id,displayName,formattedAddress,location,rating,priceLevel," +
            "nationalPhoneNumber,websiteUri,regularOpeningHours.weekdayDescriptions," +
            "photos.name,googleMapsUri,types");

        var res  = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var place = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var name    = place.TryGetProperty("displayName", out var dn)
                      && dn.TryGetProperty("text", out var dnt) ? dnt.GetString() ?? "" : "";
        var address = place.TryGetProperty("formattedAddress", out var fa) ? fa.GetString() : null;

        double lat = 0, lon = 0;
        if (place.TryGetProperty("location", out var loc))
        {
            lat = loc.GetProperty("latitude").GetDouble();
            lon = loc.GetProperty("longitude").GetDouble();
        }

        double? rating = null;
        if (place.TryGetProperty("rating", out var r)) rating = r.GetDouble();

        var phone     = place.TryGetProperty("nationalPhoneNumber", out var ph) ? ph.GetString() : null;
        var website   = place.TryGetProperty("websiteUri", out var ws) ? ws.GetString() : null;
        var mapsUri   = place.TryGetProperty("googleMapsUri", out var gm) ? gm.GetString() : null;
        var types     = place.TryGetProperty("types", out var t)
            ? t.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : Array.Empty<string>();

        string? openingHours = null;
        if (place.TryGetProperty("regularOpeningHours", out var hours)
            && hours.TryGetProperty("weekdayDescriptions", out var wd))
            openingHours = string.Join("\n", wd.EnumerateArray().Select(x => x.GetString()));

        string[] photos = place.TryGetProperty("photos", out var ph2)
            ? ph2.EnumerateArray()
                 .Take(3)
                 .Select(p => p.TryGetProperty("name", out var n) ? n.GetString()! : "")
                 .Where(s => s.Length > 0)
                 .ToArray()
            : [];

        var summary = new PlaceResult(
            PlaceId:    placeId,
            Name:       name,
            Address:    address,
            Location:   new NormalizedLocation(lat, lon, Name: name, Address: address,
                            PlaceId: placeId, Source: "place"),
            Rating:     rating,
            PriceLevel: null,
            IsOpen:     null,
            Types:      types,
            ProviderId: Id
        );

        return new PlaceDetails(summary, phone, website, openingHours, photos, mapsUri);
    }
}

// ─── Google Static Maps ───────────────────────────────────────────────────────

/// <summary>
/// Google Static Maps API — renders map images.
/// Cost: $0.002 per map (after free $200/month credit).
/// Env var: GOOGLE_MAPS_API_KEY
/// Docs: https://developers.google.com/maps/documentation/maps-static
/// </summary>
public sealed class GoogleStaticMapRenderer : IStaticMapRenderer
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;

    public string Id          => "google-static";
    public string DisplayName => "Google Static Maps ($0.002/map)";

    public GoogleStaticMapRenderer(HttpClient http, string apiKey)
    {
        _http   = http;
        _apiKey = apiKey;
    }

    public async Task<MapImage> RenderAsync(
        NormalizedLocation center,
        int  zoom   = 15,
        int  width  = 600,
        int  height = 400,
        CancellationToken ct = default)
    {
        // Clamp to Google's limits: max 640x640 on free, 2048x2048 with premium
        width  = Math.Clamp(width,  100, 640);
        height = Math.Clamp(height, 100, 640);
        zoom   = Math.Clamp(zoom,   1,   21);

        var url = "https://maps.googleapis.com/maps/api/staticmap" +
                  $"?center={center.Latitude},{center.Longitude}" +
                  $"&zoom={zoom}" +
                  $"&size={width}x{height}" +
                  $"&markers=color:red%7C{center.Latitude},{center.Longitude}" +
                  $"&key={_apiKey}";

        var res   = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);

        return new MapImage(
            Bytes:           bytes,
            MimeType:        "image/png",
            Width:           width,
            Height:          height,
            ProviderId:      Id,
            AttributionText: null  // baked into the image
        );
    }
}

// ─── Azure Maps ───────────────────────────────────────────────────────────────

/// <summary>
/// Azure Maps — geocoding + static map rendering.
/// Free tier: 250,000 transactions/month included.
/// You already have AZURE_MAPS_KEY in the primusmaps resource group.
/// Env var: AZURE_MAPS_KEY
/// Docs: https://docs.microsoft.com/en-us/azure/azure-maps/
/// </summary>
public sealed class AzureMapsProvider : IGeocoder, IStaticMapRenderer
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private const    string     BaseUrl = "https://atlas.microsoft.com";
    private const    string     ApiVersion = "1.0";

    public string Id          => "azure-maps";
    public string DisplayName => "Azure Maps (free 250k/month)";

    public AzureMapsProvider(HttpClient http, string apiKey)
    {
        _http   = http;
        _apiKey = apiKey;
    }

    // ─── IGeocoder ────────────────────────────────────────────────────────────

    public async Task<NormalizedLocation?> GeocodeAsync(
        string query, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/search/address/json" +
                  $"?api-version={ApiVersion}" +
                  $"&query={Uri.EscapeDataString(query)}" +
                  $"&subscription-key={_apiKey}" +
                  $"&limit=1";

        var res  = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (!json.TryGetProperty("results", out var results)) return null;
        var arr = results.EnumerateArray().ToList();
        if (arr.Count == 0) return null;

        return ParseAzureResult(arr[0]);
    }

    public async Task<NormalizedLocation?> ReverseGeocodeAsync(
        double latitude, double longitude, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/search/address/reverse/json" +
                  $"?api-version={ApiVersion}" +
                  $"&query={latitude},{longitude}" +
                  $"&subscription-key={_apiKey}";

        var res  = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        if (!json.TryGetProperty("addresses", out var addresses)) return null;
        var arr = addresses.EnumerateArray().ToList();
        if (arr.Count == 0) return null;

        var addr    = arr[0];
        var address = addr.TryGetProperty("address", out var a)
            && a.TryGetProperty("freeformAddress", out var ff) ? ff.GetString() : null;

        return new NormalizedLocation(latitude, longitude,
            Address: address, Source: "geocoded");
    }

    // ─── IStaticMapRenderer ───────────────────────────────────────────────────

    public async Task<MapImage> RenderAsync(
        NormalizedLocation center,
        int  zoom   = 15,
        int  width  = 600,
        int  height = 400,
        CancellationToken ct = default)
    {
        width  = Math.Clamp(width,  100, 8192);
        height = Math.Clamp(height, 100, 8192);
        zoom   = Math.Clamp(zoom,   1,   20);

        // Azure Maps Render v2 static image
        var url = $"{BaseUrl}/map/static/png" +
                  $"?api-version=2.1" +
                  $"&center={center.Longitude},{center.Latitude}" +
                  $"&zoom={zoom}" +
                  $"&width={width}&height={height}" +
                  $"&subscription-key={_apiKey}" +
                  $"&pins=default|la15+50||{center.Longitude} {center.Latitude}";

        var res   = await _http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);

        return new MapImage(
            Bytes:           bytes,
            MimeType:        "image/png",
            Width:           width,
            Height:          height,
            ProviderId:      Id,
            AttributionText: "© Microsoft Azure Maps"
        );
    }

    private static NormalizedLocation ParseAzureResult(JsonElement result)
    {
        var pos = result.GetProperty("position");
        var lat = pos.GetProperty("lat").GetDouble();
        var lon = pos.GetProperty("lon").GetDouble();
        var address = result.TryGetProperty("address", out var a)
            && a.TryGetProperty("freeformAddress", out var ff) ? ff.GetString() : null;
        var name = result.TryGetProperty("poi", out var poi)
            && poi.TryGetProperty("name", out var n) ? n.GetString() : null;

        return new NormalizedLocation(lat, lon,
            Name:    name,
            Address: address,
            Source:  "geocoded");
    }
}

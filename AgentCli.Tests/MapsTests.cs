using AgentCli;
using Xunit;

namespace AgentCli.Tests;

/// <summary>
/// Unit tests for the Maps subsystem.
/// All tests here are always-pass — no external API calls.
/// Integration tests (real HTTP) gated behind AGENTCLI_TEST_MAPS env var.
/// </summary>
public class MapsTests
{
    // ─── NormalizedLocation ───────────────────────────────────────────────────

    [Fact]
    public void Location_ToText_Pin_ShowsCoords()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080);
        var text = loc.ToText();
        Assert.Contains("48.208490", text);
        Assert.Contains("16.372080", text);
        Assert.StartsWith("📍", text);
    }

    [Fact]
    public void Location_ToText_Place_ShowsNameAndAddress()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080,
            Name: "Starbucks", Address: "Mariahilfer Str. 1, Vienna");
        var text = loc.ToText();
        Assert.Contains("Starbucks", text);
        Assert.Contains("Mariahilfer Str. 1", text);
        Assert.Contains("48.208490", text);
    }

    [Fact]
    public void Location_ToText_Live_ShowsSatelliteEmoji()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080, IsLive: true);
        Assert.StartsWith("🛰", loc.ToText());
    }

    [Fact]
    public void Location_ToText_WithAccuracy_ShowsMeters()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080, AccuracyMeters: 12.5);
        Assert.Contains("~13m", loc.ToText());
    }

    [Fact]
    public void Location_ToOsmUrl_ContainsCoords()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080);
        var url = loc.ToOsmUrl();
        Assert.Contains("openstreetmap.org", url);
        Assert.Contains("48.208490", url);
    }

    [Fact]
    public void Location_ToGoogleMapsUrl_ContainsCoords()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080);
        var url = loc.ToGoogleMapsUrl();
        Assert.Contains("google.com/maps", url);
        Assert.Contains("48.208490", url);
    }

    [Fact]
    public void Location_ToGoogleMapsUrl_WithName_UsesSearchQuery()
    {
        var loc = new NormalizedLocation(48.208490, 16.372080,
            Name: "Starbucks", PlaceId: "ChIJ123");
        var url = loc.ToGoogleMapsUrl();
        Assert.Contains("Starbucks", url);
        Assert.Contains("ChIJ123", url);
    }

    // ─── MapOptions ───────────────────────────────────────────────────────────

    [Fact]
    public void MapOptions_Free_UsesOsmProviders()
    {
        var opts = MapOptions.Free;
        Assert.Equal("nominatim",    opts.GeocoderProvider);
        Assert.Equal("osm-overpass", opts.PlaceSearchProvider);
        Assert.Equal("osm-static",   opts.MapRenderProvider);
    }

    [Fact]
    public void MapOptions_Google_UsesGoogleProviders()
    {
        var opts = MapOptions.Google;
        Assert.Equal("google-geocoding", opts.GeocoderProvider);
        Assert.Equal("google-places",    opts.PlaceSearchProvider);
        Assert.Equal("google-static",    opts.MapRenderProvider);
        // Free fallbacks
        Assert.Equal("nominatim",    opts.GeocoderFallback);
        Assert.Equal("osm-overpass", opts.PlaceSearchFallback);
        Assert.Equal("osm-static",   opts.MapRenderFallback);
    }

    [Fact]
    public void MapOptions_AzureWithGooglePlaces_MixesProviders()
    {
        var opts = MapOptions.AzureWithGooglePlaces;
        Assert.Equal("azure-maps",   opts.GeocoderProvider);
        Assert.Equal("google-places", opts.PlaceSearchProvider);
        Assert.Equal("azure-maps",   opts.MapRenderProvider);
    }

    // ─── MapRegistry ──────────────────────────────────────────────────────────

    [Fact]
    public void Registry_RegisterAndResolve_Geocoder()
    {
        var reg = new MapRegistry();
        var http = new HttpClient();
        reg.RegisterGeocoder(new NominatimGeocoder(http));
        Assert.NotNull(reg.GetGeocoder("nominatim"));
        Assert.Null(reg.GetGeocoder("google-geocoding"));
    }

    [Fact]
    public void Registry_RegisterAndResolve_Renderer()
    {
        var reg = new MapRegistry();
        var http = new HttpClient();
        reg.RegisterRenderer(new OsmStaticMapRenderer(http));
        Assert.NotNull(reg.GetRenderer("osm-static"));
        Assert.Null(reg.GetRenderer("google-static"));
    }

    [Fact]
    public void Registry_RegisterAndResolve_PlaceSearch()
    {
        var reg = new MapRegistry();
        var http = new HttpClient();
        reg.RegisterPlaceSearch(new OsmOverpassPlaceSearch(http));
        Assert.NotNull(reg.GetSearcher("osm-overpass"));
        Assert.Null(reg.GetSearcher("google-places"));
    }

    [Fact]
    public void Registry_AllCollections_ReturnRegistered()
    {
        var reg  = new MapRegistry();
        var http = new HttpClient();
        reg.RegisterGeocoder(new NominatimGeocoder(http));
        reg.RegisterPlaceSearch(new OsmOverpassPlaceSearch(http));
        reg.RegisterRenderer(new OsmStaticMapRenderer(http));

        Assert.Single(reg.AllGeocoders);
        Assert.Single(reg.AllSearchers);
        Assert.Single(reg.AllRenderers);
    }

    [Fact]
    public void Registry_Build_AlwaysRegistersOsmProviders()
    {
        var reg = MapRegistry.Build(MapOptions.Free, new HttpClient());
        Assert.NotNull(reg.GetGeocoder("nominatim"));
        Assert.NotNull(reg.GetSearcher("osm-overpass"));
        Assert.NotNull(reg.GetRenderer("osm-static"));
    }

    [Fact]
    public void Registry_Build_DoesNotRegisterGoogle_WithoutApiKey()
    {
        // Only if GOOGLE_MAPS_API_KEY is not set in test environment
        var key = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
        if (key != null) return; // skip if key present in test env

        var reg = MapRegistry.Build(MapOptions.Free, new HttpClient());
        Assert.Null(reg.GetGeocoder("google-geocoding"));
        Assert.Null(reg.GetSearcher("google-places"));
        Assert.Null(reg.GetRenderer("google-static"));
    }

    // ─── MapPipeline ──────────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_HasProviders_ReflectsRegistry()
    {
        var reg  = MapRegistry.Build(MapOptions.Free, new HttpClient());
        var pipe = new MapPipeline(reg, MapOptions.Free);
        Assert.True(pipe.HasGeocoder);
        Assert.True(pipe.HasPlaceSearch);
        Assert.True(pipe.HasMapRenderer);
    }

    [Fact]
    public void Pipeline_HasProviders_FalseWhenMissing()
    {
        var reg  = new MapRegistry();  // empty
        var pipe = new MapPipeline(reg, MapOptions.Free);
        Assert.False(pipe.HasGeocoder);
        Assert.False(pipe.HasPlaceSearch);
        Assert.False(pipe.HasMapRenderer);
    }

    [Fact]
    public async Task Pipeline_RenderAsync_ReturnsNull_WhenRenderingDisabled()
    {
        var opts = new MapOptions { RenderMapOnInbound = false };
        var reg  = MapRegistry.Build(opts, new HttpClient());
        var pipe = new MapPipeline(reg, opts);
        var loc  = new NormalizedLocation(48.208490, 16.372080);
        var img  = await pipe.RenderAsync(loc);
        Assert.Null(img);
    }

    [Fact]
    public async Task Pipeline_EnrichAsync_ReturnsOriginal_WhenReverseGeocodeDisabled()
    {
        var opts = new MapOptions { ReverseGeocodeInbound = false };
        var reg  = MapRegistry.Build(opts, new HttpClient());
        var pipe = new MapPipeline(reg, opts);
        var loc  = new NormalizedLocation(48.208490, 16.372080);
        var enriched = await pipe.EnrichAsync(loc);
        // Should return original unchanged — no HTTP call made
        Assert.Equal(loc, enriched);
    }

    [Fact]
    public async Task Pipeline_EnrichAsync_ReturnsOriginal_WhenAlreadyHasNameAndAddress()
    {
        var opts = new MapOptions { ReverseGeocodeInbound = true };
        var reg  = MapRegistry.Build(opts, new HttpClient());
        var pipe = new MapPipeline(reg, opts);
        var loc  = new NormalizedLocation(48.208490, 16.372080,
            Name: "Starbucks", Address: "Mariahilfer Str. 1");
        var enriched = await pipe.EnrichAsync(loc);
        // Already enriched — no HTTP call needed
        Assert.Equal(loc, enriched);
    }

    [Fact]
    public void Pipeline_FormatLocationReply_IncludesOsmLink()
    {
        var opts = new MapOptions { IncludeMapLink = true };
        var reg  = MapRegistry.Build(opts, new HttpClient());
        var pipe = new MapPipeline(reg, opts);
        var loc  = new NormalizedLocation(48.208490, 16.372080);
        var text = pipe.FormatLocationReply(loc);
        Assert.Contains("openstreetmap.org", text);
        Assert.Contains("48.208490", text);
    }

    [Fact]
    public void Pipeline_FormatLocationReply_NoLink_WhenDisabled()
    {
        var opts = new MapOptions { IncludeMapLink = false };
        var reg  = MapRegistry.Build(opts, new HttpClient());
        var pipe = new MapPipeline(reg, opts);
        var loc  = new NormalizedLocation(48.208490, 16.372080);
        var text = pipe.FormatLocationReply(loc);
        Assert.DoesNotContain("openstreetmap.org", text);
    }

    [Fact]
    public async Task Pipeline_SearchNearby_ThrowsMapPipelineException_WhenNoProvider()
    {
        var opts = new MapOptions { PlaceSearchProvider = "nonexistent", PlaceSearchFallback = null };
        var reg  = new MapRegistry(); // empty — no providers
        var pipe = new MapPipeline(reg, opts);
        var loc  = new NormalizedLocation(48.208490, 16.372080);
        await Assert.ThrowsAsync<MapPipelineException>(() =>
            pipe.SearchNearbyAsync("coffee", loc).AsTask());
    }

    // ─── Provider IDs / DisplayNames ─────────────────────────────────────────

    [Fact]
    public void OsmProviders_HaveCorrectIds()
    {
        var http = new HttpClient();
        Assert.Equal("nominatim",    new NominatimGeocoder(http).Id);
        Assert.Equal("osm-overpass", new OsmOverpassPlaceSearch(http).Id);
        Assert.Equal("osm-static",   new OsmStaticMapRenderer(http).Id);
    }

    [Fact]
    public void PaidProviders_HaveCorrectIds()
    {
        var http = new HttpClient();
        Assert.Equal("google-geocoding", new GoogleGeocodingProvider(http, "key").Id);
        Assert.Equal("google-places",    new GooglePlacesProvider(http, "key").Id);
        Assert.Equal("google-static",    new GoogleStaticMapRenderer(http, "key").Id);
        Assert.Equal("azure-maps",       new AzureMapsProvider(http, "key").Id);
    }

    // ─── Integration tests (real HTTP) ───────────────────────────────────────

    [SkippableFact]
    public async Task Integration_Nominatim_Geocode_Vienna()
    {
        Skip.If(Environment.GetEnvironmentVariable("AGENTCLI_TEST_MAPS") == null,
            "AGENTCLI_TEST_MAPS not set");

        var geocoder = new NominatimGeocoder(new HttpClient());
        var result   = await geocoder.GeocodeAsync("Vienna, Austria");
        Assert.NotNull(result);
        Assert.True(result!.Latitude  > 47 && result.Latitude  < 49);
        Assert.True(result.Longitude > 15 && result.Longitude < 18);
    }

    [SkippableFact]
    public async Task Integration_Nominatim_ReverseGeocode_Vienna()
    {
        Skip.If(Environment.GetEnvironmentVariable("AGENTCLI_TEST_MAPS") == null,
            "AGENTCLI_TEST_MAPS not set");

        var geocoder = new NominatimGeocoder(new HttpClient());
        var result   = await geocoder.ReverseGeocodeAsync(48.208490, 16.372080);
        Assert.NotNull(result);
        Assert.NotNull(result!.Address);
        Assert.Contains("Wien", result.Address!);
    }

    [SkippableFact]
    public async Task Integration_OsmStatic_RendersPng()
    {
        Skip.If(Environment.GetEnvironmentVariable("AGENTCLI_TEST_MAPS") == null,
            "AGENTCLI_TEST_MAPS not set");

        var renderer = new OsmStaticMapRenderer(new HttpClient());
        var loc      = new NormalizedLocation(48.208490, 16.372080);
        var image    = await renderer.RenderAsync(loc, zoom: 14, width: 400, height: 300);

        Assert.NotNull(image);
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Bytes.Length > 1000);
        Assert.Equal("© OpenStreetMap contributors", image.AttributionText);
    }
}

// Helper to make IAsyncEnumerable.ThrowsAsync work
file static class AsyncEnumerableExtensions
{
    public static Task AsTask(this Task<IReadOnlyList<PlaceResult>> t) => t;
}

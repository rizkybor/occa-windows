namespace Occa.Core.Basemap;

/// <summary>
/// Esri World Imagery through ArcGIS Location Platform (the basemap decided in BRD §6 BR-11). The API key comes from
/// configuration (<c>appsettings.json</c> or the <c>OCCA_ESRI_API_KEY</c> environment variable); it is never
/// hard-coded and never shown in the UI. See <c>docs/esri-offline-imagery.md</c> for Esri's terms.
/// </summary>
public sealed class EsriWorldImagery
{
    /// <summary>Raster tile endpoint of the basemap layer service (billed per tile against the API key).</summary>
    public const string DefaultTileTemplate = "https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

    /// <summary>Credit shown on the map and on exports that include the basemap, as the Esri terms require.</summary>
    public const string Attribution = "Esri, Maxar, Earthstar Geographics, and the GIS User Community";

    public const string ApiKeyEnvironmentVariable = "OCCA_ESRI_API_KEY";

    /// <summary>World Imagery is served down to level 23 in most places; higher levels return no tile.</summary>
    public const int MaxZoom = 23;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _template;

    public EsriWorldImagery(HttpClient http, string apiKey, string? tileTemplate = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An Esri API key is required.", nameof(apiKey));
        _http = http;
        _apiKey = apiKey.Trim();
        _template = tileTemplate ?? DefaultTileTemplate;
    }

    /// <summary>Reads the key from configuration: the explicit value when set, otherwise the environment variable.</summary>
    public static string? ResolveApiKey(string? configuredKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey)) return configuredKey.Trim();
        var env = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>Tile URL with the key as the <c>token</c> query parameter.</summary>
    public Uri TileUri(int x, int y, int zoom)
    {
        if (zoom is < 0 or > MaxZoom) throw new ArgumentOutOfRangeException(nameof(zoom));
        int n = 1 << zoom;
        if (x < 0 || x >= n || y < 0 || y >= n) throw new ArgumentOutOfRangeException(nameof(x), "Tile outside the zoom level.");
        var url = _template.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
        return new Uri(url + (url.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(_apiKey));
    }

    /// <summary>Downloads one tile (JPEG). Throws <see cref="HttpRequestException"/> on a non-success status.</summary>
    public async Task<ImageryTile> FetchTileAsync(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(TileUri(x, y, zoom), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new ImageryTile(x, y, zoom, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", bytes,
            response.Headers.CacheControl?.MaxAge, response.Content.Headers.Expires);
    }
}

/// <summary>One downloaded basemap tile with the caching headers Esri allows the app to honour.</summary>
public sealed record ImageryTile(int X, int Y, int Zoom, string MediaType, byte[] Bytes, TimeSpan? CacheMaxAge, DateTimeOffset? Expires);

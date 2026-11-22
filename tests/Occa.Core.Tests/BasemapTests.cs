using Occa.Core.Basemap;

namespace Occa.Core.Tests;

public class BasemapTests
{
    [Fact]
    public void TileArithmeticRoundTrips()
    {
        // Jakarta, zoom 15.
        var (x, y) = WebMercator.TileFor(-6.2, 106.8167, 15);
        Assert.Equal((26106, 16949), (x, y));
        var (lat, lon) = WebMercator.TileOrigin(x, y, 15);
        Assert.InRange(lat, -6.21, -6.19);
        Assert.InRange(lon, 106.80, 106.82);
        Assert.InRange(WebMercator.MetersPerPixel(-6.2, 15), 4.7, 4.8);
    }

    [Fact]
    public void BuildsTileUrlWithTokenFromConfiguration()
    {
        var imagery = new EsriWorldImagery(new HttpClient(), "  test-key  ");

        var uri = imagery.TileUri(26106, 16949, 15);

        Assert.Equal("https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/15/16949/26106?token=test-key", uri.ToString());
    }

    [Fact]
    public void RequiresAnApiKey()
    {
        Assert.Throws<ArgumentException>(() => new EsriWorldImagery(new HttpClient(), ""));
        Assert.Null(EsriWorldImagery.ResolveApiKey(null));
        Assert.Equal("k", EsriWorldImagery.ResolveApiKey(" k "));
    }

    /// <summary>Live check against Esri; runs only when OCCA_ESRI_API_KEY is set (locally or as a CI secret).</summary>
    [SkippableFact]
    public async Task FetchesALiveTileWhenAKeyIsConfigured()
    {
        var key = EsriWorldImagery.ResolveApiKey(null);
        Skip.If(key is null, "OCCA_ESRI_API_KEY is not set.");

        var imagery = new EsriWorldImagery(new HttpClient(), key!);
        var (x, y) = WebMercator.TileFor(-6.2, 106.8167, 15);
        var tile = await imagery.FetchTileAsync(x, y, 15);

        Assert.StartsWith("image/", tile.MediaType);
        Assert.True(tile.Bytes.Length > 1000, "tile should be a real image");
    }
}

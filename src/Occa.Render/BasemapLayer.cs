using System.Numerics;
using Microsoft.Graphics.Canvas;
using Occa.Core.Basemap;
using Occa.Core.Geometry;
using Occa.Core.Project;
using Windows.Storage.Streams;

namespace Occa.Render;

/// <summary>
/// Draws the basemap under the map: Esri World Imagery tiles (Web Mercator) placed in the project's UTM frame, or a
/// georeferenced image (GeoTIFF). Each tile is positioned by an affine fit of its corners, which is exact to well
/// under a pixel over one tile. Bitmaps are decoded once per device and kept while the view is open.
/// While a tile is loading (or after it failed) the nearest cached ancestor tile is drawn in its place, so the
/// imagery never has holes when zooming; a failed tile is retried after a while (network hiccups, HTTP 429 bursts)
/// instead of staying blank for the whole session.
/// </summary>
public sealed class BasemapLayer : IDisposable
{
    private readonly Dictionary<(int, int, int), CanvasBitmap?> _tiles = new();
    private readonly Dictionary<(int, int, int), DateTimeOffset> _failed = new();
    private readonly HashSet<(int, int, int)> _loading = new();
    private CanvasBitmap? _image;
    private string? _imagePath;
    private readonly TileCache? _cache;
    private readonly UtmZone _zone;

    /// <summary>A failed tile is asked for again after this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(8);

    /// <summary>Raised on a background thread when a tile has arrived; the host requests a redraw.</summary>
    public event Action? TileLoaded;

    public BasemapLayer(TileCache? cache, UtmZone zone)
    {
        _cache = cache;
        _zone = zone;
    }

    public bool HasTileSource => _cache is not null;
    public int PendingTiles { get { lock (_tiles) return _loading.Count; } }

    // MARK: Esri tiles

    public void DrawTiles(CanvasDrawingSession ds, MapTransform view, int maxZoom = EsriWorldImagery.MaxTileZoom)
    {
        if (_cache is null) return;
        var visible = view.Visible;
        var centerLatLon = _zone.Unproject(visible.Center);
        double metersPerPixel = 1 / view.PixelsPerMeter;
        int zoom = WebMercatorMeters.ZoomFor(metersPerPixel, centerLatLon.Lat, maxZoom);
        var (x0, x1, y0, y1) = TileRange(visible, zoom);
        int n = 1 << zoom;
        // Zoomed out so far that the view needs hundreds of tiles: drop one level until it is reasonable.
        while (zoom > 0 && (long)(x1 - x0 + 1) * (y1 - y0 + 1) > 256) { zoom--; (x0, x1, y0, y1) = TileRange(visible, zoom); n = 1 << zoom; }
        double size = WebMercatorMeters.TileSizeMeters(zoom);
        var saved = ds.Transform;
        for (int y = Math.Max(0, y0); y <= Math.Min(n - 1, y1); y++)
        {
            for (int x = Math.Max(0, x0); x <= Math.Min(n - 1, x1); x++)
            {
                var bitmap = Tile(ds, x, y, zoom);
                // Not there yet: the nearest cached ancestor covers the tile's square until it arrives.
                int ax = x, ay = y, az = zoom;
                var source = new Windows.Foundation.Rect(0, 0, 256, 256);
                while (bitmap is null && az > 0)
                {
                    az--; ax >>= 1; ay >>= 1;
                    lock (_tiles) _tiles.TryGetValue((ax, ay, az), out bitmap);
                    if (bitmap is not null)
                    {
                        int span = 1 << (zoom - az);
                        double part = bitmap.SizeInPixels.Width / (double)span;
                        source = new Windows.Foundation.Rect((x - (ax << (zoom - az))) * part, (y - (ay << (zoom - az))) * part, part, part);
                    }
                }
                if (bitmap is null) continue;
                // Tile square (0,0)…(1,1) → UTM metres through three corners.
                var nw = WebMercatorMeters.TileOriginMeters(x, y, zoom);
                Point2D Utm(double u, double v) => _zone.Project(WebMercatorMeters.ToLatLon(new Point2D(nw.X + u * size, nw.Y - v * size)));
                var tileToUtm = AffineFromCorners(Utm(0, 0), Utm(1, 0), Utm(0, 1), (float)source.Width, (float)source.Height);
                ds.Transform = tileToUtm * view.ToPixels * saved;
                ds.DrawImage(bitmap, new Windows.Foundation.Rect(0, 0, source.Width, source.Height), source, 1, CanvasImageInterpolation.HighQualityCubic);
            }
        }
        ds.Transform = saved;
    }

    private (int X0, int X1, int Y0, int Y1) TileRange(Rect2D visible, int zoom)
    {
        // Mercator bounds of the visible UTM rectangle (its four corners).
        var corners = new[] { new Point2D(visible.MinX, visible.MinY), new Point2D(visible.MaxX, visible.MinY), new Point2D(visible.MinX, visible.MaxY), new Point2D(visible.MaxX, visible.MaxY) }
            .Select(p => WebMercatorMeters.FromLatLon(_zone.Unproject(p))).ToArray();
        double size = WebMercatorMeters.TileSizeMeters(zoom);
        int x0 = (int)Math.Floor((corners.Min(c => c.X) + WebMercatorMeters.HalfWorld) / size);
        int x1 = (int)Math.Floor((corners.Max(c => c.X) + WebMercatorMeters.HalfWorld) / size);
        int y0 = (int)Math.Floor((WebMercatorMeters.HalfWorld - corners.Max(c => c.Y)) / size);
        int y1 = (int)Math.Floor((WebMercatorMeters.HalfWorld - corners.Min(c => c.Y)) / size);
        return (x0, x1, y0, y1);
    }

    /// <summary>Affine mapping pixel (0,0)/(w,0)/(0,h) to the three UTM points.</summary>
    private static Matrix3x2 AffineFromCorners(Point2D p00, Point2D p10, Point2D p01, float w, float h)
    {
        var ex = new Vector2((float)((p10.X - p00.X) / w), (float)((p10.Y - p00.Y) / w));
        var ey = new Vector2((float)((p01.X - p00.X) / h), (float)((p01.Y - p00.Y) / h));
        return new Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, (float)p00.X, (float)p00.Y);
    }

    private CanvasBitmap? Tile(ICanvasResourceCreator rc, int x, int y, int z)
    {
        var key = (x, y, z);
        bool start;
        lock (_tiles)
        {
            if (_tiles.TryGetValue(key, out var bmp)) return bmp;
            if (_failed.TryGetValue(key, out var at) && DateTimeOffset.UtcNow - at < RetryAfter) return null;
            start = _loading.Add(key);
        }
        if (start) _ = LoadTileAsync(rc, key);
        return null;
    }

    private async Task LoadTileAsync(ICanvasResourceCreator rc, (int X, int Y, int Z) key)
    {
        CanvasBitmap? bitmap = null;
        try
        {
            var tile = await _cache!.GetAsync(key.X, key.Y, key.Z).ConfigureAwait(false);
            if (tile is not null)
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(tile.Bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                bitmap = await CanvasBitmap.LoadAsync(rc, stream);
            }
        }
        catch (Exception)
        {
            bitmap = null;
        }
        lock (_tiles)
        {
            if (bitmap is not null) { _tiles[key] = bitmap; _failed.Remove(key); }
            else _failed[key] = DateTimeOffset.UtcNow;   // retried later, never blank for good
            _loading.Remove(key);
        }
        TileLoaded?.Invoke();
    }

    // MARK: GeoTIFF basemap

    public async Task LoadImageAsync(ICanvasResourceCreator rc, string path)
    {
        if (_imagePath == path && _image is not null) return;
        _image?.Dispose();
        _image = null;
        _imagePath = path;
        try { _image = await CanvasBitmap.LoadAsync(rc, path); }
        catch (Exception) { _image = null; }
        TileLoaded?.Invoke();
    }

    public void DrawImage(CanvasDrawingSession ds, MapTransform view, BasemapImage basemap)
    {
        if (_image is null) return;
        var c = basemap.Corners(_zone);   // UL, UR, LR, LL
        var m = AffineFromCorners(c[0], c[1], c[3], _image.SizeInPixels.Width, _image.SizeInPixels.Height);
        var saved = ds.Transform;
        ds.Transform = m * view.ToPixels * saved;
        ds.DrawImage(_image, 0, 0, new Windows.Foundation.Rect(0, 0, _image.SizeInPixels.Width, _image.SizeInPixels.Height), (float)basemap.OpacityValue, CanvasImageInterpolation.HighQualityCubic);
        ds.Transform = saved;
    }

    public void Dispose()
    {
        lock (_tiles)
        {
            foreach (var b in _tiles.Values) b?.Dispose();
            _tiles.Clear();
            _failed.Clear();
        }
        _image?.Dispose();
    }
}

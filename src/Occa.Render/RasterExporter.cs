using Microsoft.Graphics.Canvas;
using Occa.Core.Export;
using Occa.Core.Render;

namespace Occa.Render;

/// <summary>Result of a raster export: the image and its sidecar files.</summary>
public sealed record RasterExportResult(string ImagePath, string WorldFilePath, string? PrjPath, int PixelWidth, int PixelHeight)
{
    public IEnumerable<string> AllFiles => new[] { ImagePath, WorldFilePath }.Concat(PrjPath is null ? Array.Empty<string>() : new[] { PrjPath });
}

/// <summary>
/// Renders the map to a north-up raster the size of the frame at the requested dpi (Win2D render target), writes it as
/// PNG / TIFF / JPEG and the world file + .prj next to it (FR-6.2). Same drawing as the screen (MapRenderer), so the
/// export matches what the mapper sees: contours under the objects, the course overprint in the colour order (lower
/// purple below black, blue and brown 100 %; upper purple on top), like the PDF. The basemap is not included.
/// </summary>
public static class RasterExporter
{
    public static async Task<RasterExportResult> ExportAsync(RasterExportRequest req, string directory, MapScene scene, IReadOnlyDictionary<string, CanvasBitmap>? images = null)
    {
        var (w, h) = req.PixelDimensions;
        if (w <= 0 || h <= 0 || (long)w * h > 400_000_000) throw new InvalidOperationException($"Not enough memory for a {w} × {h} px raster.");
        Directory.CreateDirectory(directory);
        var device = CanvasDevice.GetSharedDevice();
        // The render target is sized in DIPs at the export dpi, so w × h device pixels = (w, h) × 96 / dpi DIPs.
        double dipsW = w * 96.0 / req.Dpi, dipsH = h * 96.0 / req.Dpi;
        using var target = new CanvasRenderTarget(device, (float)dipsW, (float)dipsH, req.Dpi);
        var view = new MapTransform { ViewWidth = dipsW, ViewHeight = dipsH, Center = req.Frame.Center, PixelsPerMeter = dipsW / req.Frame.WidthM };
        var levels = req.Overprint?.Levels;
        using (var ds = target.CreateDrawingSession())
        {
            if (!req.Format.SupportsTransparency()) ds.Clear(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            else ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (req.Contours is { Count: > 0 } lines && req.ContourStyle is { } style) new ContourRenderer().Draw(ds, lines, style, req.Sheet, view, minLinePx: 0);
            if (req.Grid is { IsEnabled: true, IncludeInExport: true }) MapRenderer.DrawFrame(ds, view, req.Frame, req.Grid, req.Sheet);
            // No minimum line width: print sizes are exact at the export dpi.
            Action? lower = levels is { Lower.IsEmpty: false } lv ? () => CourseOverprintRenderer.Draw(ds, lv.Lower, view, 0, 1, images) : null;
            new MapRenderer().Draw(ds, scene, view, req.Sheet, minLinePx: 0, req.OverprintLowerPriority, lower);
            if (levels is { Upper.IsEmpty: false } up) CourseOverprintRenderer.Draw(ds, up.Upper, view, 0, 1, images);
        }
        var imagePath = req.ImagePath(directory);
        var tmp = imagePath + ".tmp";
        var format = req.Format switch { RasterFormat.Tiff => CanvasBitmapFileFormat.Tiff, RasterFormat.Jpeg => CanvasBitmapFileFormat.Jpeg, _ => CanvasBitmapFileFormat.Png };
        await target.SaveAsync(tmp, format, req.Format == RasterFormat.Jpeg ? 0.92f : 1f);
        File.Move(tmp, imagePath, overwrite: true);
        var sidecar = req.WriteSidecar(directory);
        return new RasterExportResult(imagePath, sidecar[0], sidecar.Count > 1 ? sidecar[1] : null, (int)target.SizeInPixels.Width, (int)target.SizeInPixels.Height);
    }
}

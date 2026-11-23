using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Occa.Core.Basemap;
using Occa.Core.Export;
using Occa.Core.Vector;
using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Render;
using Occa.Render;
using Windows.Foundation;
using Windows.System;

namespace Occa.App.Views;

/// <summary>
/// The map of one project, read-only in M1: objects drawn by <see cref="MapRenderer"/> over the Esri basemap or a
/// GeoTIFF, with the A4 frame and north lines. Mouse: drag or middle button pans, wheel pans, Ctrl+wheel zooms
/// (FR-W5); touch: two fingers pan, pinch zooms (FR-W6); Ctrl+0 fits the map area.
/// </summary>
public sealed partial class MapView : UserControl
{
    public event EventHandler? BackRequested;

    private readonly MapRenderer _renderer = new();
    private readonly MapTransform _view = new();
    private MapScene? _scene;
    private ProjectDocument? _doc;
    private string? _folder;
    private BasemapLayer? _basemap;
    private TileCache? _tileCache;
    private UtmZone? _zone;
    private Rect2D? _fitRect;
    private double _basemapOpacity = 1;
    private bool _showBasemap = true;
    private Point? _dragLast;
    private uint? _dragPointer;
    private bool _fitPending;

    public MapView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Close();
    }

    /// <summary>Shows a project package folder.</summary>
    public async Task OpenAsync(ProjectRepository.Entry entry)
    {
        Close();
        _folder = entry.Folder;
        TitleText.Text = entry.Project.Name;
        SummaryText.Text = entry.Project.TechnicalSummary;
        MapStatus.IsOpen = false;
        try
        {
            var folder = entry.Folder;
            var doc = await Task.Run(() => ProjectPackage.Read(folder));
            var scene = await Task.Run(() => MapScene.Build(doc.Features, doc.Strokes, doc.Project.Sheet, doc.Project.AreaOpacityValue));
            _doc = doc;
            _scene = scene;
            _zone = doc.Project.Zone;
            _basemapOpacity = doc.Project.BasemapOpacityValue;
            OpacitySlider.Value = _basemapOpacity * 100;
            _fitRect = doc.Project.Frame?.Extent
                       ?? doc.Features.Select(f => f.Bounds).Where(b => b.HasValue).Select(b => b!.Value).Aggregate((Rect2D?)null, (a, b) => a is null ? b : a.Value.Union(b));
            SetUpBasemap(doc);
            _fitPending = true;
            Canvas.Invalidate();
            if (!entry.HasThumbnail && _fitRect is { } fit) _ = RenderThumbnailAsync(entry.ThumbnailPath, scene, doc, fit);
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Cannot open project", ex.Message);
        }
    }

    private void SetUpBasemap(ProjectDocument doc)
    {
        _basemap?.Dispose();
        _basemap = null;
        if (_zone is not { } zone)
        {
            Show(InfoBarSeverity.Informational, "No map area yet", "This project has no locked area, so there is no basemap. Objects are shown in project coordinates.");
            return;
        }
        var key = App.Settings.EsriApiKey;
        if (key is not null)
        {
            _tileCache ??= new TileCache(Path.Combine(UserSettings.Folder, "TileCache"), new EsriWorldImagery(App.Http, key, App.Settings.Esri.TileTemplate));
            _basemap = new BasemapLayer(_tileCache, zone);
            AttributionText.Text = EsriWorldImagery.Attribution;
        }
        else
        {
            _basemap = new BasemapLayer(null, zone);
            AttributionText.Text = "";
            Show(InfoBarSeverity.Warning, "No Esri API key", $"Set {EsriWorldImagery.ApiKeyEnvironmentVariable} or appsettings.local.json to show the satellite basemap.");
        }
        _basemap.TileLoaded += () => DispatcherQueue.TryEnqueue(() => Canvas.Invalidate());
        BasemapToggle.IsEnabled = key is not null;
        BasemapToggle.IsChecked = _showBasemap = key is not null;
        if (doc.Project.BasemapImage is { IsVisible: true } img && img.HasSafeFileName && _folder is not null)
        {
            var path = Path.Combine(_folder, img.FileName);
            if (File.Exists(path)) _ = _basemap.LoadImageAsync(Canvas, path);
        }
    }

    /// <summary>A 640×480 JPEG preview of the map objects on paper for the project list (no basemap: that is a per-device cache).</summary>
    private static async Task RenderThumbnailAsync(string path, MapScene scene, ProjectDocument doc, Rect2D fit)
    {
        try
        {
            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, 640, 480, 96);
            var view = new MapTransform { ViewWidth = 640, ViewHeight = 480 };
            view.Fit(fit, 8);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(MapRenderer.PaperColor);
                if (doc.Project.Frame is { } frame) MapRenderer.DrawFrame(ds, view, frame, null, doc.Project.Sheet);
                new MapRenderer().Draw(ds, scene, view, doc.Project.Sheet, minLinePx: 0.6);
            }
            var tmp = path + ".tmp";
            await target.SaveAsync(tmp, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Jpeg, 0.85f);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // A missing preview is cosmetic; the next open tries again.
        }
    }

    private void Close()
    {
        _basemap?.Dispose();
        _basemap = null;
        _scene = null;
        _doc = null;
        _renderer.InvalidateCache();
    }

    // MARK: Drawing

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) => _renderer.InvalidateCache();

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _view.ViewWidth = e.NewSize.Width;
        _view.ViewHeight = e.NewSize.Height;
        if (_fitRect is { } r && _fitPending) { _view.Fit(r); _fitPending = false; }
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        _view.ViewWidth = sender.ActualWidth;
        _view.ViewHeight = sender.ActualHeight;
        if (_fitPending && _fitRect is { } r && _view.ViewWidth > 0) { _view.Fit(r); _fitPending = false; }
        ds.Clear(MapRenderer.PaperColor);
        if (_doc is null || _scene is null) return;

        if (_basemap is not null)
        {
            if (_showBasemap && _basemap.HasTileSource) _basemap.DrawTiles(ds, _view);
            if (_doc.Project.BasemapImage is { IsVisible: true } img) _basemap.DrawImage(ds, _view, img);
            MapRenderer.DrawVeil(ds, _view, _basemapOpacity);
        }
        if (_doc.Project.Frame is { } frame) MapRenderer.DrawFrame(ds, _view, frame, _doc.Project.NorthGrid, _doc.Project.Sheet);
        _renderer.Draw(ds, _scene, _view, _doc.Project.Sheet);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_doc is null) return;
        var c = _view.Center;
        string coords = _zone is { } z ? $"{z.Label}  E {c.X:F0}  N {c.Y:F0}" : $"x {c.X:F0}  y {c.Y:F0}";
        // Screen scale: metres per CSS pixel at 96 dpi expressed as a map scale.
        double screenScale = 1 / _view.PixelsPerMeter / 0.0254 * 96;
        StatusText.Text = $"{coords}   ·   1:{_doc.Project.Sheet.ScaleDenominator} map, screen ≈ 1:{screenScale:F0}   ·   {_doc.ObjectCount} objects"
                          + (_basemap is { PendingTiles: > 0 } b ? $"   ·   loading {b.PendingTiles} tiles" : "");
    }

    // MARK: Input

    private (double Min, double Max) ZoomRange => (_fitRect is { } r && _view.ViewWidth > 0 ? Math.Min(_view.ViewWidth / r.Width, _view.ViewHeight / r.Height) * 0.2 : 0.01, 400);

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        if (p.PointerDeviceType == PointerDeviceType.Touch) return;   // touch goes through manipulation
        if (p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed || p.PointerDeviceType == PointerDeviceType.Pen)
        {
            _dragLast = p.Position;
            _dragPointer = p.PointerId;
            Canvas.CapturePointer(e.Pointer);
            Focus(FocusState.Pointer);
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        if (_dragLast is { } last && p.PointerId == _dragPointer)
        {
            _view.PanByPixels(p.Position.X - last.X, p.Position.Y - last.Y);
            _dragLast = p.Position;
            Canvas.Invalidate();
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _dragPointer)
        {
            _dragLast = null;
            _dragPointer = null;
            Canvas.ReleasePointerCapture(e.Pointer);
        }
    }

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        int delta = p.Properties.MouseWheelDelta;
        bool ctrl = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (ctrl || p.PointerDeviceType == PointerDeviceType.Touch)
        {
            var (min, max) = ZoomRange;
            _view.ZoomAt(p.Position.X, p.Position.Y, Math.Pow(1.0015, delta), min, max);
        }
        else if (shift || p.Properties.IsHorizontalMouseWheel)
        {
            _view.PanByPixels(delta * 0.5, 0);
        }
        else
        {
            _view.PanByPixels(0, delta * 0.5);
        }
        e.Handled = true;
        Canvas.Invalidate();
    }

    private void Canvas_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        _view.PanByPixels(e.Delta.Translation.X, e.Delta.Translation.Y);
        if (Math.Abs(e.Delta.Scale - 1) > 1e-4)
        {
            var (min, max) = ZoomRange;
            _view.ZoomAt(e.Position.X, e.Position.Y, e.Delta.Scale, min, max);
        }
        Canvas.Invalidate();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void FitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { Fit(); args.Handled = true; }

    private void Fit()
    {
        if (_fitRect is { } r) { _view.Fit(r); Canvas.Invalidate(); }
    }

    private void Basemap_Click(object sender, RoutedEventArgs e)
    {
        _showBasemap = BasemapToggle.IsChecked == true;
        Canvas.Invalidate();
    }

    private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _basemapOpacity = e.NewValue / 100;
        Canvas.Invalidate();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>M3: PDF (CMYK, Geospatial PDF), PNG/TIFF/JPEG + world file + .prj, OMAP, DXF.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _scene is null) return;
        if (_doc.Project.Frame is not { } frame || _doc.Project.Zone is not { } zone)
        {
            Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (frame and UTM zone) before exporting.");
            return;
        }
        var dialog = new ExportDialog(WindowHandle, _doc.Project.Name, _doc.Project.NorthGrid is { IsEnabled: true }) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var baseName = dialog.FileName.Length == 0 ? "map" : dialog.FileName;
        var folder = dialog.Folder;
        var grid = dialog.IncludeGrid ? _doc.Project.NorthGrid : null;
        var features = _doc.Features.WithColorPriorities(StandardSymbolSets.LookupFor(_doc.Project.Sheet));
        try
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            switch (dialog.SelectedKind)
            {
                case ExportDialog.Kind.Pdf:
                {
                    var req = new GeoPdfRequest { Title = _doc.Project.Name, Frame = frame, Sheet = _doc.Project.Sheet, Zone = zone, Strokes = _doc.Strokes, Features = features, Grid = grid };
                    var path = Path.Combine(folder, baseName + ".pdf");
                    await Task.Run(() => GeoPdfExporter.Export(req, path));
                    written.Add(path);
                    break;
                }
                case ExportDialog.Kind.Png:
                case ExportDialog.Kind.Tiff:
                case ExportDialog.Kind.Jpeg:
                {
                    var req = new RasterExportRequest { BaseName = baseName, Frame = frame, Sheet = _doc.Project.Sheet, Zone = zone, Dpi = dialog.Dpi, Format = dialog.RasterFormat, Strokes = _doc.Strokes, Features = features, Grid = grid };
                    var result = await RasterExporter.ExportAsync(req, folder, _scene);
                    written.AddRange(result.AllFiles);
                    break;
                }
                default:
                {
                    var req = new VectorExportRequest { BaseName = baseName, Frame = frame, Sheet = _doc.Project.Sheet, Zone = zone, Strokes = _doc.Strokes, Features = features, Grid = grid };
                    var format = dialog.SelectedKind == ExportDialog.Kind.Omap ? VectorFormat.Omap : VectorFormat.Dxf;
                    written.AddRange(await Task.Run(() => VectorExporter.Export(req, new HashSet<VectorFormat> { format }, folder)));
                    break;
                }
            }
            Show(InfoBarSeverity.Success, "Exported", string.Join(", ", written.Select(Path.GetFileName)) + $" → {folder}");
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Export failed", ex.Message);
        }
    }

    /// <summary>Handle of the window hosting this view (for pickers).</summary>
    public nint WindowHandle { get; set; }

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        MapStatus.Severity = severity;
        MapStatus.Title = title;
        MapStatus.Message = message;
        MapStatus.IsOpen = true;
    }
}

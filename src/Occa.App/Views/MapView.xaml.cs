using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Occa.Core.Basemap;
using Occa.Core.Contour;
using Occa.Core.Courses;
using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Import;
using Occa.Core.Project;
using Occa.Core.Render;
using Occa.Core.Vector;
using Occa.Render;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace Occa.App.Views;

/// <summary>
/// The map of one project: objects drawn by <see cref="MapRenderer"/> over the Esri basemap or a GeoTIFF, with the A4
/// frame and north lines, contours from elevation data (M4) and the purple course overprint (M4). Mouse: drag or middle
/// button pans, wheel pans, Ctrl+wheel zooms (FR-W5); touch: two fingers pan, pinch zooms (FR-W6); Ctrl+0 fits the map
/// area. With the Courses panel open, a click places or selects controls and dragging moves them.
/// </summary>
public sealed partial class MapView : UserControl
{
    public event EventHandler? BackRequested;

    private readonly MapRenderer _renderer = new();
    private readonly ContourRenderer _contourRenderer = new();
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

    // M4: course planner + contours
    private CourseOverprint? _overprint;
    private readonly Dictionary<string, CanvasBitmap> _courseImages = new();
    private Guid? _dragControl;
    private bool _dragMoved;
    private DemGrid? _dem;
    private List<ContourLine>? _importedContours;
    private List<ContourLine>? _contourLines;
    private CancellationTokenSource? _contourJob;
    private DispatcherQueueTimer? _saveTimer;
    private const double OverprintScreenAlpha = 0.9;

    public MapView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Close();
        Courses.Changed += (_, _) => { RebuildOverprint(); Canvas.Invalidate(); ScheduleSave(); };
        Courses.SelectionChanged += (_, _) => { RebuildOverprint(); Canvas.Invalidate(); };
        Courses.CloseRequested += (_, _) => { CoursesToggle.IsChecked = false; UpdatePanels(); };
        Courses.ImportRequested += async (_, _) => await ImportCourseAsync();
        Courses.ExportIofRequested += async (_, _) => await ExportIofAsync();
        Contours.SettingsChanged += (_, _) => ContourSettingsChanged();
        Contours.CloseRequested += (_, _) => { ContoursToggle.IsChecked = false; UpdatePanels(); };
        Contours.OpenDemRequested += async (_, _) => await OpenDemAsync();
        Contours.DownloadTerrainRequested += async (_, _) => await DownloadTerrainAsync();
        Contours.RemoveDemRequested += (_, _) => RemoveDem();
        Contours.ExportRequested += async (_, _) => await ExportContoursAsync();
        Contours.ConvertRequested += (_, _) => ConvertContours();
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
            var dem = await Task.Run(() => { try { return DemGrid.Read(folder); } catch (Exception) { return null; } });
            var imported = await Task.Run(() => ContourLine.ReadImported(folder));
            _doc = doc;
            _scene = scene;
            _zone = doc.Project.Zone;
            _basemapOpacity = doc.Project.BasemapOpacityValue;
            OpacitySlider.Value = _basemapOpacity * 100;
            _fitRect = doc.Project.Frame?.Extent
                       ?? doc.Features.Select(f => f.Bounds).Where(b => b.HasValue).Select(b => b!.Value).Aggregate((Rect2D?)null, (a, b) => a is null ? b : a.Value.Union(b));
            SetUpBasemap(doc);
            _dem = dem;
            _importedContours = imported;
            Courses.Bind(doc.Courses, doc.Project.Sheet);
            Contours.Bind(doc.Project.Contours, doc.Project.Sheet, canDownload: _zone is not null && doc.Project.Frame is not null);
            Contours.ShowDem(_dem, _importedContours?.Count ?? 0);
            RebuildOverprint();
            _ = LoadCourseImagesAsync();
            RegenerateContours();
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
            var device = CanvasDevice.GetSharedDevice();
            using var target = new CanvasRenderTarget(device, 640, 480, 96);
            var view = new MapTransform { ViewWidth = 640, ViewHeight = 480 };
            view.Fit(fit, 8);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(MapRenderer.PaperColor);
                if (doc.Project.Frame is { } frame) MapRenderer.DrawFrame(ds, view, frame, null, doc.Project.Sheet);
                new MapRenderer().Draw(ds, scene, view, doc.Project.Sheet, minLinePx: 0.6);
            }
            var tmp = path + ".tmp";
            await target.SaveAsync(tmp, CanvasBitmapFileFormat.Jpeg, 0.85f);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // A missing preview is cosmetic; the next open tries again.
        }
    }

    private void Close()
    {
        // Pending edits of the project being closed are written now.
        if (_saveTimer is { IsRunning: true }) { _saveTimer.Stop(); SaveNow(); }
        _contourJob?.Cancel();
        _basemap?.Dispose();
        _basemap = null;
        _scene = null;
        _doc = null;
        _overprint = null;
        _dem = null;
        _importedContours = null;
        _contourLines = null;
        foreach (var b in _courseImages.Values) b.Dispose();
        _courseImages.Clear();
        _renderer.InvalidateCache();
        _contourRenderer.InvalidateCache();
    }

    // MARK: Saving

    /// <summary>Writes the package shortly after the last edit (debounced), like the Mac's autosave.</summary>
    private void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveNow();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        if (_doc is null || _folder is null) return;
        try
        {
            _doc.Project.UpdatedAt = DateTime.UtcNow;
            var files = ProjectPackage.Serialize(_doc);
            var folder = _folder;
            bool dropCourses = _doc.Courses.IsEmpty && _doc.Courses.EventTitle.Length == 0;
            _ = Task.Run(() =>
            {
                foreach (var (name, json) in files) ProjectPackage.WriteAtomic(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes(json));
                if (dropCourses) File.Delete(Path.Combine(folder, ProjectPackage.CoursesFile));
            });
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Project not saved", ex.Message);
        }
    }

    // MARK: Drawing

    private void Canvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _renderer.InvalidateCache();
        _contourRenderer.InvalidateCache();
        foreach (var b in _courseImages.Values) b.Dispose();
        _courseImages.Clear();
        _ = LoadCourseImagesAsync();
    }

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
        if (_contourLines is { Count: > 0 } lines && _doc.Project.Contours.IsEnabled) _contourRenderer.Draw(ds, lines, _doc.Project.Contours, _doc.Project.Sheet, _view);
        if (_doc.Project.Frame is { } frame) MapRenderer.DrawFrame(ds, _view, frame, _doc.Project.NorthGrid, _doc.Project.Sheet);
        _renderer.Draw(ds, _scene, _view, _doc.Project.Sheet);
        if (_overprint is { IsEmpty: false } o) CourseOverprintRenderer.Draw(ds, o, _view, minLinePx: 1, OverprintScreenAlpha, _courseImages);
        DrawCourseSelection(ds);
        UpdateStatus();
    }

    /// <summary>Selected control: a thin blue ring around its symbol.</summary>
    private void DrawCourseSelection(CanvasDrawingSession ds)
    {
        if (_doc is null || Courses.Visibility != Visibility.Visible || Courses.SelectedControlId is not { } id || _doc.Courses.Control(id) is not { } c) return;
        float r = (float)(ControlRadiusM(c) * _view.PixelsPerMeter + 6);
        ds.DrawCircle(_view.Project(c.Position), r, Windows.UI.Color.FromArgb(220, 0x31, 0x5E, 0x85), 1.5f);
    }

    private void UpdateStatus()
    {
        if (_doc is null) return;
        var c = _view.Center;
        string coords = _zone is { } z ? $"{z.Label}  E {c.X:F0}  N {c.Y:F0}" : $"x {c.X:F0}  y {c.Y:F0}";
        // Screen scale: metres per CSS pixel at 96 dpi expressed as a map scale.
        double screenScale = 1 / _view.PixelsPerMeter / 0.0254 * 96;
        var mode = Courses.Visibility == Visibility.Visible && Courses.PlaceKind is { } k ? $"   ·   click to place {k.Label().ToLowerInvariant()}" : "";
        StatusText.Text = $"{coords}   ·   1:{_doc.Project.Sheet.ScaleDenominator} map, screen ≈ 1:{screenScale:F0}   ·   {_doc.ObjectCount} objects"
                          + (_basemap is { PendingTiles: > 0 } b ? $"   ·   loading {b.PendingTiles} tiles" : "") + mode;
    }

    // MARK: Panels

    private void Panels_Click(object sender, RoutedEventArgs e)
    {
        // One side panel at a time.
        if (ReferenceEquals(sender, CoursesToggle) && CoursesToggle.IsChecked == true) ContoursToggle.IsChecked = false;
        if (ReferenceEquals(sender, ContoursToggle) && ContoursToggle.IsChecked == true) CoursesToggle.IsChecked = false;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        Courses.Visibility = CoursesToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Contours.Visibility = ContoursToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (Courses.Visibility != Visibility.Visible) Courses.ClearPlacement();
        Canvas.Invalidate();
    }

    // MARK: Courses

    private void RebuildOverprint()
    {
        if (_doc is null) { _overprint = null; return; }
        var set = _doc.Courses;
        _overprint = set.IsEmpty ? null : CourseOverprint.Make(set, Courses.SelectedCourse, _doc.Project.Sheet);
    }

    private async Task LoadCourseImagesAsync()
    {
        if (_doc is null || _folder is null) return;
        foreach (var item in _doc.Courses.AllItems)
        {
            if (item.ItemKind != CourseItem.Kind.Image || item.ImageName is not { } name || _courseImages.ContainsKey(name) || !CourseImages.IsValidName(name)) continue;
            var path = CourseImages.PathOf(name, _folder);
            if (!File.Exists(path)) continue;
            try { _courseImages[name] = await CanvasBitmap.LoadAsync(Canvas, path); }
            catch (Exception) { /* a missing or unreadable image is skipped */ }
        }
        Canvas.Invalidate();
    }

    private double ControlRadiusM(ControlPoint c)
    {
        if (_doc is null) return 0;
        var style = CourseStyle.Standard(_doc.Project.Sheet);
        var look = _doc.Courses.EffectiveAppearance;
        double mm = c.Kind switch { ControlKind.Start => style.StartSideMM / Math.Sqrt(3), ControlKind.Finish => style.FinishOuterMM / 2, _ => style.ControlDiameterMM / 2 };
        return _doc.Project.Sheet.GroundMeters(mm * look.ControlScale * c.EffectiveSize);
    }

    private ControlPoint? ControlAt(Point px)
    {
        if (_doc is null) return null;
        var p = _view.Unproject(px.X, px.Y);
        ControlPoint? best = null;
        double bestD = double.PositiveInfinity;
        foreach (var c in _doc.Courses.Controls)
        {
            double d = c.Position.DistanceTo(p);
            double reach = Math.Max(ControlRadiusM(c) * 1.2, 12 / _view.PixelsPerMeter);
            if (d <= reach && d < bestD) { best = c; bestD = d; }
        }
        return best;
    }

    /// <summary>Adds a control to a course in the IOF order: start first, finish last, controls before the finish.</summary>
    private static void AddToCourse(Course course, ControlPoint c)
    {
        var ids = course.ControlIds;
        if (ids.Contains(c.Id) && c.Kind != ControlKind.Control) return;
        switch (c.Kind)
        {
            case ControlKind.Start:
                ids.RemoveAll(id => id == c.Id);
                ids.Insert(0, c.Id);
                break;
            case ControlKind.Finish:
                ids.RemoveAll(id => id == c.Id);
                ids.Add(c.Id);
                break;
            default:
                ids.Add(c.Id);
                break;
        }
    }

    private void PlaceControl(ControlKind kind, Point2D position)
    {
        if (_doc is null) return;
        var set = _doc.Courses;
        var control = new ControlPoint(kind, position, kind == ControlKind.Control ? set.NextFreeCode() : null);
        set.Controls.Add(control);
        if (Courses.SelectedCourse is { } course)
        {
            // Controls go before the finish so the course order stays start → controls → finish.
            if (kind == ControlKind.Control && course.ControlIds.Count > 0 && set.Control(course.ControlIds[^1])?.Kind == ControlKind.Finish)
                course.ControlIds.Insert(course.ControlIds.Count - 1, control.Id);
            else AddToCourse(course, control);
        }
        Courses.Refresh();
        Courses.SelectControl(control.Id);
        if (kind != ControlKind.Control) Courses.ClearPlacement();
        RebuildOverprint();
        Canvas.Invalidate();
        ScheduleSave();
    }

    private async Task ImportCourseAsync()
    {
        if (_doc is null) return;
        if (_zone is not { } zone) { Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (UTM zone) before importing courses."); return; }
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (var ext in CourseFileImport.FileExtensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var bytes = await File.ReadAllBytesAsync(file.Path);
            var result = await Task.Run(() => CourseFileImport.Read(bytes, Path.GetExtension(file.Path), zone));
            _doc.Courses.Controls.AddRange(result.Controls);
            _doc.Courses.Courses.AddRange(result.Courses);
            Courses.Refresh();
            RebuildOverprint();
            Canvas.Invalidate();
            ScheduleSave();
            var notes = new List<string>();
            if (result.SkippedDescriptions > 0) notes.Add($"{result.SkippedDescriptions} OCAD descriptions not converted");
            if (result.MissingReferences > 0) notes.Add($"{result.MissingReferences} missing control references");
            Show(InfoBarSeverity.Success, "Course imported", $"{result.Controls.Count} controls, {result.Courses.Count} courses" + (notes.Count > 0 ? $" · {string.Join(", ", notes)}" : ""));
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Import failed", ex.Message);
        }
    }

    private async Task ExportIofAsync()
    {
        if (_doc is null) return;
        if (_doc.Project.Frame is not { } frame || _zone is not { } zone) { Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (frame and UTM zone) before exporting IOF XML."); return; }
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = $"{_doc.Project.Name}_IOF3" };
            picker.FileTypeChoices.Add("IOF XML 3.0", new List<string> { ".xml" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var data = IofXmlExporter.CourseData(_doc.Courses, frame, _doc.Project.Sheet, zone);
            await File.WriteAllBytesAsync(file.Path, data);
            Show(InfoBarSeverity.Success, "IOF XML exported", file.Path);
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Export failed", ex.Message);
        }
    }

    // MARK: Contours

    private void ContourSettingsChanged()
    {
        if (_doc is null) return;
        var before = _doc.Project.ContourSettings?.Geometry;
        _doc.Project.ContourSettings = Contours.Settings;
        if (before != Contours.Settings.Geometry || _contourLines is null) RegenerateContours();
        Canvas.Invalidate();
        ScheduleSave();
    }

    /// <summary>Recomputes the contour lines in the background from the DEM (or reclassifies imported lines).</summary>
    private void RegenerateContours()
    {
        _contourJob?.Cancel();
        if (_doc is null || (_dem is null && _importedContours is null)) { _contourLines = null; Contours.ShowResult(0, null); return; }
        var settings = _doc.Project.Contours with { };
        var sheet = _doc.Project.Sheet;
        var dem = _dem; var imported = _importedContours;
        var job = new CancellationTokenSource();
        _contourJob = job;
        Contours.SetBusy(true, "Computing contours…");
        _ = Task.Run(() => dem is not null ? ContourGenerator.Generate(dem, settings, sheet).Lines : ContourLine.Classify(imported!, settings), job.Token)
            .ContinueWith(t =>
            {
                if (job.IsCancellationRequested) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_contourJob, job)) return;
                    Contours.SetBusy(false);
                    if (t.IsFaulted) { Show(InfoBarSeverity.Error, "Contours failed", t.Exception?.GetBaseException().Message ?? "unknown error"); return; }
                    _contourLines = t.Result;
                    _contourRenderer.InvalidateCache();
                    Contours.ShowResult(_contourLines.Count, dem is null ? null : settings.IntervalWarning(dem.Head.NativeResolutionM));
                    Canvas.Invalidate();
                });
            }, TaskScheduler.Default);
    }

    private async Task OpenDemAsync()
    {
        if (_doc is null || _folder is null) return;
        if (_zone is not { } zone) { Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (UTM zone) before loading elevation data."); return; }
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (var ext in new[] { ".tif", ".tiff", ".asc", ".geojson", ".json" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            Contours.SetBusy(true, "Reading elevation data…");
            var path = file.Path;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var extent = (_doc.Project.Frame?.Extent ?? _fitRect ?? new Rect2D(0, 0, 1000, 1000)).InsetBy(-200);
            var folder = _folder;
            if (ext is ".geojson" or ".json")
            {
                var lines = await Task.Run(() => ContourGeoJson.Read(File.ReadAllBytes(path), zone)
                    .Select(l => new ContourLine(l.Elevation, ContourLine.Kind.Contour, l.Points, l.Points.Count > 2 && l.Points[0] == l.Points[^1])).ToList());
                await Task.Run(() => { DemGrid.Remove(folder); ContourLine.WriteImported(lines, folder); });
                _dem = null;
                _importedContours = lines;
            }
            else
            {
                var grid = await Task.Run(() =>
                {
                    var bytes = File.ReadAllBytes(path);
                    var prjPath = Path.ChangeExtension(path, ".prj");
                    var raster = ext == ".asc" ? AsciiGridReader.Read(bytes, File.Exists(prjPath) ? File.ReadAllText(prjPath) : null, zone) : GeoTiffReader.Read(bytes, zone);
                    var g = raster.Grid(extent, zone, Path.GetFileName(path));
                    ContourLine.RemoveImported(folder);
                    g.Write(folder);
                    return g;
                });
                _dem = grid;
                _importedContours = null;
                if (grid.Range is null) Show(InfoBarSeverity.Warning, "No elevation in the map area", "The DEM does not cover this map area (check its coordinate system).");
            }
            Contours.Settings.IsEnabled = true;
            _doc.Project.ContourSettings = Contours.Settings;
            Contours.Bind(Contours.Settings, _doc.Project.Sheet, canDownload: _doc.Project.Frame is not null);
            Contours.ShowDem(_dem, _importedContours?.Count ?? 0);
            RegenerateContours();
            ScheduleSave();
        }
        catch (Exception ex)
        {
            Contours.SetBusy(false);
            Show(InfoBarSeverity.Error, "Cannot read elevation data", ex.Message);
        }
    }

    /// <summary>Terrain Tiles on AWS (Terrarium PNG): zoom 15 for sprint maps, 14 otherwise, resampled to the project grid.</summary>
    private async Task DownloadTerrainAsync()
    {
        if (_doc is null || _folder is null) return;
        if (_zone is not { } zone || _doc.Project.Frame is not { } frame) { Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (frame and UTM zone) before downloading terrain."); return; }
        var extent = frame.Extent.InsetBy(-Math.Max(frame.WidthM, frame.HeightM) * 0.1);
        int z = _doc.Project.Sheet.ScaleDenominator <= 5000 ? 15 : 14;
        var tiles = TerrainTiles.Tiles(extent, zone, z);
        if (tiles.Count > 120) { Show(InfoBarSeverity.Warning, "Area too large", $"{tiles.Count} terrain tiles would be needed; use a DEM file for areas this large."); return; }
        try
        {
            Contours.SetBusy(true, $"Downloading {tiles.Count} terrain tiles…");
            var mosaic = new TerrainMosaic(z);
            int failed = 0;
            foreach (var (x, y) in tiles)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, TerrainTiles.Url(z, x, y));
                    request.Headers.UserAgent.ParseAdd("OCCA/1.0");
                    using var response = await App.Http.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    var png = await response.Content.ReadAsByteArrayAsync();
                    var values = TerrainMosaic.DecodeTile(png);
                    if (values is null) failed++; else mosaic.Tiles[TerrainMosaic.Key(x, y)] = values;
                }
                catch (HttpRequestException) { failed++; }
            }
            if (mosaic.Tiles.Count == 0) throw new InvalidOperationException("No terrain tiles could be downloaded (offline?).");
            var lat = zone.Unproject(extent.Center).Lat;
            double res = TerrainTiles.ResolutionM(z, lat);
            var folder = _folder;
            var grid = await Task.Run(() =>
            {
                var g = DemGrid.Resample(extent, res, $"Terrain Tiles (AWS) z{z}", res, p => mosaic.Sample(zone.Unproject(p)));
                ContourLine.RemoveImported(folder);
                g.Write(folder);
                return g;
            });
            _dem = grid;
            _importedContours = null;
            Contours.Settings.IsEnabled = true;
            _doc.Project.ContourSettings = Contours.Settings;
            Contours.Bind(Contours.Settings, _doc.Project.Sheet, canDownload: true);
            Contours.ShowDem(_dem, 0);
            RegenerateContours();
            ScheduleSave();
            if (failed > 0) Show(InfoBarSeverity.Warning, "Some tiles missing", $"{failed} of {tiles.Count} terrain tiles could not be downloaded.");
        }
        catch (Exception ex)
        {
            Contours.SetBusy(false);
            Show(InfoBarSeverity.Error, "Terrain download failed", ex.Message);
        }
    }

    private void RemoveDem()
    {
        if (_folder is null) return;
        _contourJob?.Cancel();
        try { DemGrid.Remove(_folder); ContourLine.RemoveImported(_folder); } catch (IOException ex) { Show(InfoBarSeverity.Error, "Cannot remove elevation data", ex.Message); }
        _dem = null;
        _importedContours = null;
        _contourLines = null;
        _contourRenderer.InvalidateCache();
        Contours.ShowDem(null, 0);
        Contours.ShowResult(0, null);
        Canvas.Invalidate();
    }

    private async Task ExportContoursAsync()
    {
        if (_doc is null || _contourLines is not { Count: > 0 } lines) { Show(InfoBarSeverity.Informational, "No contours", "Generate contours first (enable them in the Contours panel)."); return; }
        if (_zone is not { } zone) { Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (UTM zone) before exporting contours."); return; }
        var shp = new CheckBox { Content = "Shapefile (.shp, project UTM)", IsChecked = true };
        var kml = new CheckBox { Content = "KML (.kml, WGS84)" };
        var kmz = new CheckBox { Content = "KMZ (.kmz, WGS84)" };
        var dialog = new ContentDialog
        {
            Title = "Export contours", PrimaryButtonText = "Choose folder…", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Content = new StackPanel { Spacing = 8, Children = { shp, kml, kmz } },
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var formats = new HashSet<ContourExportFormat>();
        if (shp.IsChecked == true) formats.Add(ContourExportFormat.Shp);
        if (kml.IsChecked == true) formats.Add(ContourExportFormat.Kml);
        if (kmz.IsChecked == true) formats.Add(ContourExportFormat.Kmz);
        if (formats.Count == 0) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var settings = _doc.Project.Contours;
            var baseName = string.Join("_", _doc.Project.Name.Split(Path.GetInvalidFileNameChars())) + "_contours";
            var title = _doc.Project.Name;
            var paths = await Task.Run(() => ContourExporter.Export(lines, formats, zone, settings, baseName, title, folder.Path));
            Show(InfoBarSeverity.Success, "Contours exported", string.Join(", ", paths.Select(Path.GetFileName)) + $" → {folder.Path}");
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "Export failed", ex.Message);
        }
    }

    /// <summary>"Convert to vector objects": the contour lines become ISOM 101/102/103 line features; the live contours are hidden.</summary>
    private void ConvertContours()
    {
        if (_doc is null || _contourLines is not { Count: > 0 } lines) { Show(InfoBarSeverity.Informational, "No contours", "Generate contours first."); return; }
        var settings = _doc.Project.Contours;
        _doc.Features.AddRange(lines.Select(l => l.ToFeature(settings)));
        _scene = MapScene.Build(_doc.Features, _doc.Strokes, _doc.Project.Sheet, _doc.Project.AreaOpacityValue);
        _renderer.InvalidateCache();
        Contours.Settings.IsEnabled = false;
        _doc.Project.ContourSettings = Contours.Settings;
        Contours.Bind(Contours.Settings, _doc.Project.Sheet, canDownload: _zone is not null && _doc.Project.Frame is not null);
        Canvas.Invalidate();
        ScheduleSave();
        Show(InfoBarSeverity.Success, "Contours converted", $"{lines.Count} contour lines added as map objects.");
    }

    // MARK: Input

    private (double Min, double Max) ZoomRange => (_fitRect is { } r && _view.ViewWidth > 0 ? Math.Min(_view.ViewWidth / r.Width, _view.ViewHeight / r.Height) * 0.2 : 0.01, 400);

    private bool CourseEditing => _doc is not null && Courses.Visibility == Visibility.Visible;

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        if (p.PointerDeviceType == PointerDeviceType.Touch) return;   // touch goes through manipulation
        bool left = p.Properties.IsLeftButtonPressed || p.PointerDeviceType == PointerDeviceType.Pen;
        if (left || p.Properties.IsMiddleButtonPressed)
        {
            _dragLast = p.Position;
            _dragPointer = p.PointerId;
            _dragMoved = false;
            _dragControl = left && CourseEditing ? ControlAt(p.Position)?.Id : null;
            Canvas.CapturePointer(e.Pointer);
            Focus(FocusState.Pointer);
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        if (_dragLast is not { } last || p.PointerId != _dragPointer) return;
        double dx = p.Position.X - last.X, dy = p.Position.Y - last.Y;
        if (!_dragMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
        _dragMoved = true;
        if (_dragControl is { } id && _doc?.Courses.Control(id) is { } c)
        {
            c.Position = _view.Unproject(p.Position.X, p.Position.Y);
            RebuildOverprint();
        }
        else _view.PanByPixels(dx, dy);
        _dragLast = p.Position;
        Canvas.Invalidate();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointer) return;
        var p = e.GetCurrentPoint(Canvas);
        if (CourseEditing && _doc is not null)
        {
            if (_dragControl is { } id)
            {
                if (_dragMoved) { Courses.Refresh(); ScheduleSave(); }
                else if (_doc.Courses.Control(id) is { } c)
                {
                    // Click on an existing control: select it; with a course chosen, add it to that course.
                    if (Courses.SelectedCourse is { } course && !course.ControlIds.Contains(c.Id)) { AddToCourse(course, c); Courses.Refresh(); ScheduleSave(); }
                    Courses.SelectControl(c.Id);
                    RebuildOverprint();
                }
            }
            else if (!_dragMoved && Courses.PlaceKind is { } kind && (_dragLast is not null))
            {
                PlaceControl(kind, _view.Unproject(p.Position.X, p.Position.Y));
            }
            else if (!_dragMoved) Courses.SelectControl(null);
            Canvas.Invalidate();
        }
        _dragLast = null;
        _dragPointer = null;
        _dragControl = null;
        Canvas.ReleasePointerCapture(e.Pointer);
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

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _saveTimer?.Stop();
        SaveNow();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>M3/M4: PDF (CMYK, Geospatial PDF, course + descriptions), PNG/TIFF/JPEG + world file + .prj, OMAP, DXF.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _scene is null) return;
        if (_doc.Project.Frame is not { } frame || _doc.Project.Zone is not { } zone)
        {
            Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (frame and UTM zone) before exporting.");
            return;
        }
        bool courseAvailable = !_doc.Courses.IsEmpty;
        bool contoursAvailable = _contourLines is { Count: > 0 } && _doc.Project.Contours.IncludeInExport;
        var dialog = new ExportDialog(WindowHandle, _doc.Project.Name, _doc.Project.NorthGrid is { IsEnabled: true }, courseAvailable, contoursAvailable) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var baseName = dialog.FileName.Length == 0 ? "map" : dialog.FileName;
        var folder = dialog.Folder;
        var grid = dialog.IncludeGrid ? _doc.Project.NorthGrid : null;
        var features = _doc.Features.WithColorPriorities(StandardSymbolSets.LookupFor(_doc.Project.Sheet));
        var set = _doc.Courses;
        var course = Courses.SelectedCourse;
        var overprint = dialog.IncludeCourse ? CourseOverprint.Make(set, course, _doc.Project.Sheet) : null;
        var descriptions = dialog.IncludeCourse && course is not null ? new DescriptionSheet(set, course) : null;
        var contours = dialog.IncludeContours ? _contourLines : null;
        try
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            switch (dialog.SelectedKind)
            {
                case ExportDialog.Kind.Pdf:
                {
                    // Contours go into the PDF as ISOM line objects, like "Convert to vector objects" would.
                    var pdfFeatures = contours is null ? features : features.Concat(contours.Select(l => l.ToFeature(_doc.Project.Contours))).ToList().WithColorPriorities(StandardSymbolSets.LookupFor(_doc.Project.Sheet));
                    var images = new Dictionary<string, byte[]>();
                    if (overprint is not null && _folder is not null)
                        foreach (var name in overprint.Marks.OfType<CourseMark.Image>().Select(m => m.Name).Distinct())
                            if (CourseImages.Load(name, _folder) is { } data) images[name] = data;
                    var req = new GeoPdfRequest
                    {
                        Title = set.EventTitle.Length > 0 && overprint is not null ? set.EventTitle : _doc.Project.Name, Frame = frame, Sheet = _doc.Project.Sheet, Zone = zone,
                        Strokes = _doc.Strokes, Features = pdfFeatures, Grid = grid, Overprint = overprint, Descriptions = descriptions,
                        PrintLayout = descriptions is null ? null : set.EffectivePrintLayout, ContourIntervalM = contours is null ? null : _doc.Project.Contours.IntervalM, Images = images,
                        OverprintLowerPriority = ColorHierarchy.LowerPurplePriority(_doc.Project.Sheet.MapStandard),
                    };
                    var path = Path.Combine(folder, baseName + ".pdf");
                    await Task.Run(() => GeoPdfExporter.Export(req, path));
                    written.Add(path);
                    break;
                }
                case ExportDialog.Kind.Png:
                case ExportDialog.Kind.Tiff:
                case ExportDialog.Kind.Jpeg:
                {
                    var req = new RasterExportRequest
                    {
                        BaseName = baseName, Frame = frame, Sheet = _doc.Project.Sheet, Zone = zone, Dpi = dialog.Dpi, Format = dialog.RasterFormat, Strokes = _doc.Strokes, Features = features, Grid = grid,
                        Overprint = overprint, OverprintLowerPriority = ColorHierarchy.LowerPurplePriority(_doc.Project.Sheet.MapStandard),
                        Contours = contours, ContourStyle = contours is null ? null : _doc.Project.Contours,
                    };
                    var result = await RasterExporter.ExportAsync(req, folder, _scene, _courseImages);
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

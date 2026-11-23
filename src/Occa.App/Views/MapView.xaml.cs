using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Occa.App.License;
using Occa.Core;
using Occa.Core.Basemap;
using Occa.Core.Contour;
using Occa.Core.Courses;
using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Import;
using Occa.Core.License;
using Occa.Core.Project;
using Occa.Core.Render;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using Occa.Render;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace Occa.App.Views;

/// <summary>
/// The map of one project: objects drawn by <see cref="MapRenderer"/> over the Esri basemap or a GeoTIFF, with the A4
/// frame and north lines, contours (M4) and the purple course overprint (M4). Editing (M2 UI): Select Area mode for a new
/// project, the tool rail (Navigate, Move, Node, Pen, Freehand, Rectangle, Ellipse, Point, Sketch, Eraser) with the
/// Mac's tool keys, selection with move/duplicate/delete, undo/redo and autosave. Mouse: drag or middle button pans,
/// wheel pans, Ctrl+wheel zooms (FR-W5); touch: two fingers pan, pinch zooms (FR-W6); Ctrl+0 fits the map area.
/// </summary>
public sealed partial class MapView : UserControl
{
    public event EventHandler? BackRequested;

    private enum Tool { Navigate, Select, Node, Pen, Freehand, Rectangle, Ellipse, Point, Sketch, Eraser }
    private enum Drag { None, Pan, MoveFeatures, MoveNode, MoveHandleIn, MoveHandleOut, Box, Freehand, Sketch, Shape, MoveControl }

    /// <summary>One undo step: the whole object and sketch lists before and after (value copies).</summary>
    private sealed record DocState(List<Feature> Features, List<Stroke> Strokes);

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
    private Point? _dragStart;
    private uint? _dragPointer;
    private bool _fitPending;

    // Editing
    private Tool _tool = Tool.Navigate;
    private Drag _drag = Drag.None;
    private readonly EditHistory<DocState> _history = new();
    private readonly HashSet<Guid> _selected = new();
    private int? _activeNode;
    private DocState? _dragBefore;
    private readonly List<Point2D> _draft = new();       // Pen tool points
    private readonly List<Point2D> _freehand = new();    // Freehand / Sketch points while dragging
    private Point2D? _cursor;
    private Point2D? _shapeStart;
    private Point2D? _shapeEnd;
    private bool _dragMoved;

    // M4: course planner + contours
    private CourseOverprint? _overprint;
    private readonly Dictionary<string, CanvasBitmap> _courseImages = new();
    private Guid? _dragControl;
    private DemGrid? _dem;
    private List<ContourLine>? _importedContours;
    private List<ContourLine>? _contourLines;
    private CancellationTokenSource? _contourJob;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
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
        Symbols.SymbolChosen += (_, _) => SymbolChosen();
        Symbols.ApplyRequested += (_, _) => ApplySymbolToSelection();
        Symbols.CloseRequested += (_, _) => { SymbolsToggle.IsChecked = false; UpdatePanels(); };
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
            Symbols.Bind(doc.Project.Sheet);
            RebuildOverprint();
            _ = LoadCourseImagesAsync();
            RegenerateContours();
            _history.Clear();
            _selected.Clear();
            _activeNode = null;
            SetTool(doc.Project.AreaLocked ? Tool.Select : Tool.Navigate);
            UpdateAreaMode();
            _fitPending = true;
            Canvas.Invalidate();
            if (!entry.HasThumbnail && _fitRect is { } fit && doc.Project.AreaLocked) _ = RenderThumbnailAsync(entry.ThumbnailPath, scene, doc, fit);
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

    /// <summary>Writes a pending autosave at once (window closing, back to the list).</summary>
    public void FlushSave()
    {
        if (_saveTimer is { IsRunning: true }) { _saveTimer.Stop(); SaveNow(); }
    }

    private void Close()
    {
        // Pending edits of the project being closed are written now.
        FlushSave();
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
            // The list preview follows the drawing.
            if (_scene is not null && _fitRect is { } fit && _doc.Project.AreaLocked) _ = RenderThumbnailAsync(Path.Combine(folder, ProjectPackage.ThumbnailFile), _scene, _doc, fit);
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
        if (_doc.Project.Frame is { } frame)
        {
            if (IsSelectingArea) SelectionRenderer.DrawAreaFrame(ds, _view, frame);
            else MapRenderer.DrawFrame(ds, _view, frame, _doc.Project.NorthGrid, _doc.Project.Sheet);
        }
        _renderer.Draw(ds, _scene, _view, _doc.Project.Sheet);
        if (_overprint is { IsEmpty: false } o) CourseOverprintRenderer.Draw(ds, o, _view, minLinePx: 1, OverprintScreenAlpha, _courseImages);
        DrawEditing(ds);
        DrawCourseSelection(ds);
        UpdateStatus();
    }

    private void DrawEditing(CanvasDrawingSession ds)
    {
        if (_doc is null) return;
        foreach (var id in _selected)
        {
            var f = _doc.Features.FirstOrDefault(x => x.Id == id);
            if (f is null) continue;
            bool single = _selected.Count == 1;
            SelectionRenderer.DrawFeature(ds, f, _view, showNodes: single && _tool is Tool.Node or Tool.Select, _tool == Tool.Node ? _activeNode : null, showHandles: _tool == Tool.Node);
        }
        var color = Symbols.ActiveSymbol is { } s ? MapRenderer.ToColor(s.ColorHex) : SelectionRenderer.Accent;
        float widthPx = (float)((Symbols.ActiveSymbol?.DrawingWidthMM ?? 0.25) * MetersPerMM * _view.PixelsPerMeter);
        if (_draft.Count > 0) SelectionRenderer.DrawDraft(ds, _draft, _cursor, _view, color, widthPx, closed: ActiveKind == MapSymbol.Kind.Area);
        if (_freehand.Count > 1) SelectionRenderer.DrawDraft(ds, _freehand, null, _view, _tool == Tool.Sketch ? MapRenderer.ToColor(SketchColor) : color, _tool == Tool.Sketch ? (float)(SketchWidthMM * MetersPerMM * _view.PixelsPerMeter) : widthPx, closed: false);
        if (_shapeStart is { } a && _shapeEnd is { } b) SelectionRenderer.DrawBox(ds, a, b, _view, ellipse: _tool == Tool.Ellipse);
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
        var mode = IsSelectingArea ? "   ·   select area" : $"   ·   {ToolLabel(_tool)}" + (Symbols.ActiveSymbol is { } s && _tool is Tool.Pen or Tool.Freehand or Tool.Rectangle or Tool.Ellipse or Tool.Point ? $" · {s.Code} {s.Name}" : "");
        if (CourseEditing) mode = Courses.PlaceKind is { } k ? $"   ·   click to place {k.Label().ToLowerInvariant()}" : "   ·   course planning: click a control to select, drag to move";
        var selection = _selected.Count > 0 ? $"   ·   {_selected.Count} selected" : "";
        StatusText.Text = $"{coords}   ·   1:{_doc.Project.Sheet.ScaleDenominator} map, screen ≈ 1:{screenScale:F0}   ·   {_doc.ObjectCount} objects{selection}"
                          + (_basemap is { PendingTiles: > 0 } b ? $"   ·   loading {b.PendingTiles} tiles" : "") + mode;
    }

    // MARK: Panels

    private void Panels_Click(object sender, RoutedEventArgs e)
    {
        // Contours need a license key (ProFeature.Contours); the panel stays closed on Starter.
        if (ReferenceEquals(sender, ContoursToggle) && ContoursToggle.IsChecked == true && !AppLicense.Shared.Require(ProFeature.Contours)) { ContoursToggle.IsChecked = false; return; }
        // One side panel at a time.
        foreach (var t in new[] { SymbolsToggle, CoursesToggle, ContoursToggle })
            if (!ReferenceEquals(t, sender) && ((ToggleButton)sender).IsChecked == true) t.IsChecked = false;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        Symbols.Visibility = SymbolsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Courses.Visibility = CoursesToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Contours.Visibility = ContoursToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (Courses.Visibility != Visibility.Visible) Courses.ClearPlacement();
        // With the Courses panel open, clicks on the map place / move controls; the drawing tools pause.
        ToolRail.IsEnabled = !CourseEditing;
        ToolRail.Opacity = CourseEditing ? 0.5 : 1;
        if (!CourseEditing) Canvas.Focus(FocusState.Programmatic);
        Canvas.Invalidate();
    }

    // MARK: Area selection (new project)

    private bool IsSelectingArea => _doc is not null && !_doc.Project.AreaLocked;

    private void UpdateAreaMode()
    {
        bool selecting = IsSelectingArea;
        AreaBar.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        ToolRail.Visibility = selecting ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = !selecting;
        foreach (var t in new[] { SymbolsToggle, CoursesToggle, ContoursToggle }) { t.IsEnabled = !selecting; if (selecting) t.IsChecked = false; }
        if (selecting) UpdatePanels();
        if (selecting && _doc is not null) { _tool = Tool.Navigate; SyncToolButtons(); }
        if (selecting && _zone is { } z && _doc?.Project.Frame is { } frame) GoToBox.Text = Format(z.Unproject(frame.Center));
    }

    private static string Format(LatLon ll) => $"{ll.Lat.ToString("0.#####", CultureInfo.InvariantCulture)}, {ll.Lon.ToString("0.#####", CultureInfo.InvariantCulture)}";

    /// <summary>Select Area mode: after a pan, the frame follows the view centre and the UTM zone follows the location (FR-1.2).</summary>
    private void FrameFollowsView()
    {
        if (_doc is null || !IsSelectingArea || _zone is not { } zone) return;
        var ll = zone.Unproject(_view.Center);
        var next = UtmZone.Containing(ll);
        if (next != zone)
        {
            // Everything is in metres of the current zone: re-express the view centre and switch the basemap.
            _zone = next;
            _doc.Project.CrsEpsg = next.Epsg;
            _view.Center = next.Project(ll);
            SetUpBasemap(_doc);
        }
        _doc.Project.FrameOrigin = MapFrame.Centered(_view.Center, _doc.Project.Sheet).SnappedToStep(_doc.Project.SnapStepM).Origin;
        _fitRect = _doc.Project.Frame?.Extent;
        GoToBox.Text = Format(ll);
        ScheduleSave();
    }

    private void GoTo_Click(object sender, RoutedEventArgs e) => GoTo();
    private void GoToBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { GoTo(); e.Handled = true; } }

    private void GoTo()
    {
        var parts = GoToBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || Math.Abs(lat) > 84 || Math.Abs(lon) > 180)
        {
            Show(InfoBarSeverity.Warning, "Location", "Enter latitude and longitude in decimal degrees, e.g. -6.1754, 106.8272.");
            return;
        }
        if (_zone is { } zone) _view.Center = zone.Project(new LatLon(lat, lon));
        FrameFollowsView();
        if (_fitRect is { } r) _view.Fit(r);
        Canvas.Invalidate();
    }

    /// <summary>Lock the area: the UTM zone and the frame are fixed; drawing starts (FR-1.3).</summary>
    private void LockArea_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _doc.Project.Frame is null) return;
        if (!AppLicense.Shared.Require(ProFeature.AllScales, overLimit: !FreeLimits.AllowsScale(AppLicense.Shared.IsPro, _doc.Project.Sheet.ScaleDenominator))) return;
        _doc.Project.AreaLocked = true;
        _doc.Project.AreaBackup = null;
        SummaryText.Text = _doc.Project.TechnicalSummary;
        UpdateAreaMode();
        SetTool(Tool.Pen);
        SymbolsToggle.IsChecked = true;
        UpdatePanels();
        Fit();
        ScheduleSave();
        Show(InfoBarSeverity.Success, "Area locked", "Choose a symbol in the Symbols panel and draw with the Pen (P), Freehand (F), Rectangle (M), Ellipse (L) or Point (T) tool.");
    }

    // MARK: Tools

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tag = ((ToggleButton)sender).Tag as string ?? "Navigate";
        SetTool(Enum.TryParse<Tool>(tag, out var t) ? t : Tool.Navigate);
        // Keys (Esc, Enter, Delete, the tool letters) go to the map, not to the clicked button.
        Canvas.Focus(FocusState.Programmatic);
    }

    private void SetTool(Tool tool)
    {
        if (_tool == Tool.Pen && tool != Tool.Pen) FinishDraft();
        _tool = tool;
        if (tool != Tool.Node) _activeNode = null;
        SyncToolButtons();
        Canvas.Invalidate();
    }

    private void SyncToolButtons()
    {
        foreach (var b in new[] { ToolNavigate, ToolSelect, ToolNode, ToolPen, ToolFreehand, ToolRectangle, ToolEllipse, ToolPoint, ToolSketch, ToolEraser })
            b.IsChecked = (b.Tag as string) == _tool.ToString();
        DeleteButton.IsEnabled = _selected.Count > 0;
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        Symbols.SetSelection(_selected.Count > 0);
    }

    private static string ToolLabel(Tool t) => t switch
    {
        Tool.Navigate => "Navigate (H)", Tool.Select => "Move Tool (V)", Tool.Node => "Node Tool (A)", Tool.Pen => "Pen (P)", Tool.Freehand => "Freehand (F)",
        Tool.Rectangle => "Rectangle (M)", Tool.Ellipse => "Ellipse (L)", Tool.Point => "Point symbol (T)", Tool.Sketch => "Sketch pencil (N)", _ => "Stroke eraser (E)",
    };

    /// <summary>Choosing a symbol switches to a tool that draws its kind (the Mac's Symbols panel behaviour).</summary>
    private void SymbolChosen()
    {
        if (Symbols.ActiveSymbol is not { } s || IsSelectingArea) { Canvas.Invalidate(); return; }
        switch (s.SymbolKind)
        {
            case MapSymbol.Kind.Point: SetTool(Tool.Point); break;
            case MapSymbol.Kind.Line: if (_tool is not (Tool.Pen or Tool.Freehand)) SetTool(Tool.Pen); break;
            case MapSymbol.Kind.Area: if (_tool is not (Tool.Pen or Tool.Freehand or Tool.Rectangle or Tool.Ellipse)) SetTool(Tool.Pen); break;
        }
        Canvas.Invalidate();
    }

    private MapSymbol.Kind? ActiveKind => Symbols.ActiveSymbol?.SymbolKind;
    private double MetersPerMM => _doc?.Project.Sheet.GroundMeters(1) ?? 1;
    private const string SketchColor = "#000000";
    private const double SketchWidthMM = 0.25;

    /// <summary>True while a text field (panel, Go to box) has the keyboard: tool keys and Ctrl shortcuts then belong to it.</summary>
    private bool TextInputFocused => FocusManager.GetFocusedElement(XamlRoot) is TextBox or RichEditBox or AutoSuggestBox or NumberBox or ComboBox or PasswordBox;

    /// <summary>
    /// Tool keys work wherever the focus is inside the map view (a clicked tool button, the panel background, the
    /// canvas), as on the Mac, except while typing in a field.
    /// </summary>
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_doc is null || TextInputFocused) return;
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrl) return;   // Ctrl shortcuts are accelerators
        if (CourseEditing && e.Key != VirtualKey.Escape) return;   // controls are deleted from the panel, tools pause
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (_draft.Count > 0) { _draft.Clear(); Canvas.Invalidate(); }
                else { _selected.Clear(); _activeNode = null; SyncToolButtons(); Canvas.Invalidate(); }
                break;
            case VirtualKey.Enter: if (_tool == Tool.Pen) FinishDraft(); break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                if (_tool == Tool.Node && _activeNode is { } ni) DeleteNode(ni); else DeleteSelection();
                break;
            case VirtualKey.H: if (!IsSelectingArea) SetTool(Tool.Navigate); break;
            case VirtualKey.V: if (!IsSelectingArea) SetTool(Tool.Select); break;
            case VirtualKey.A: if (!IsSelectingArea) SetTool(Tool.Node); break;
            case VirtualKey.P: if (!IsSelectingArea) SetTool(Tool.Pen); break;
            case VirtualKey.F: if (!IsSelectingArea) SetTool(Tool.Freehand); break;
            case VirtualKey.M: if (!IsSelectingArea) SetTool(Tool.Rectangle); break;
            case VirtualKey.L: if (!IsSelectingArea) SetTool(Tool.Ellipse); break;
            case VirtualKey.T: if (!IsSelectingArea) SetTool(Tool.Point); break;
            case VirtualKey.N: if (!IsSelectingArea) SetTool(Tool.Sketch); break;
            case VirtualKey.E: if (!IsSelectingArea) SetTool(Tool.Eraser); break;
            default: e.Handled = false; break;
        }
    }

    // MARK: Edits + history

    private DocState Snapshot() => new(_doc!.Features.Select(f => f.Clone()).ToList(), new List<Stroke>(_doc.Strokes));

    /// <summary>Applies an edit of the object/sketch lists as one undo step, then redraws and saves.</summary>
    private void Commit(string label, Action edit)
    {
        if (_doc is null) return;
        var before = Snapshot();
        edit();
        Record(label, before);
    }

    private void Record(string label, DocState before)
    {
        if (_doc is null) return;
        _history.Record(label, before, Snapshot());
        AfterEdit();
    }

    private void AfterEdit()
    {
        if (_doc is null) return;
        _scene = MapScene.Build(_doc.Features, _doc.Strokes, _doc.Project.Sheet, _doc.Project.AreaOpacityValue);
        _renderer.InvalidateCache();
        _selected.RemoveWhere(id => _doc.Features.All(f => f.Id != id));
        SyncToolButtons();
        Canvas.Invalidate();
        ScheduleSave();
    }

    private void Restore(DocState? state)
    {
        if (_doc is null || state is null) return;
        _doc.Features = state.Features.Select(f => f.Clone()).ToList();
        _doc.Strokes = new List<Stroke>(state.Strokes);
        _activeNode = null;
        AfterEdit();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Restore(_history.Undo());
    private void Redo_Click(object sender, RoutedEventArgs e) => Restore(_history.Redo());
    // Ctrl+Z/Y/D/A inside a text field edit the text, not the map (the accelerator is left unhandled so the field gets it).
    private void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (TextInputFocused) return; Restore(_history.Undo()); args.Handled = true; }
    private void Redo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (TextInputFocused) return; Restore(_history.Redo()); args.Handled = true; }
    private void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { _saveTimer?.Stop(); SaveNow(); Show(InfoBarSeverity.Success, "Saved", "The project was saved."); args.Handled = true; }
    private void Duplicate_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (TextInputFocused) return; DuplicateSelection(); args.Handled = true; }
    private void SelectAll_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_doc is null || IsSelectingArea || TextInputFocused || CourseEditing) return;
        _selected.Clear();
        foreach (var f in _doc.Features.Where(f => f.IsVisible)) _selected.Add(f.Id);
        if (_tool is not (Tool.Select or Tool.Node)) SetTool(Tool.Select); else { SyncToolButtons(); Canvas.Invalidate(); }
        args.Handled = true;
    }

    private void AddFeature(Feature f)
    {
        if (_doc is null) return;
        if (f.Name.Length == 0) f.Name = SymbolFeatures.DefaultName(_doc.Features, f);
        Commit("Add object", () => _doc.Features.Add(f));
        _selected.Clear();
        _selected.Add(f.Id);
        SyncToolButtons();
    }

    private void DeleteSelection()
    {
        if (_doc is null || _selected.Count == 0) return;
        var ids = _selected.ToHashSet();
        Commit("Delete", () => _doc.Features.RemoveAll(f => ids.Contains(f.Id)));
        _selected.Clear();
        SyncToolButtons();
        Canvas.Invalidate();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DuplicateSelection()
    {
        if (_doc is null || _selected.Count == 0) return;
        double d = _doc.Project.Sheet.GroundMeters(2);
        var copies = _doc.Features.Where(f => _selected.Contains(f.Id)).Select(f => { var c = f.Clone(); c.Id = Guid.NewGuid(); c.Name = ""; return c.MovedBy(new Point2D(d, -d)); }).ToList();
        foreach (var c in copies) c.Name = SymbolFeatures.DefaultName(_doc.Features, c);
        Commit("Duplicate", () => _doc.Features.AddRange(copies));
        _selected.Clear();
        foreach (var c in copies) _selected.Add(c.Id);
        SyncToolButtons();
        Canvas.Invalidate();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelection();

    private void ApplySymbol_Click(object sender, RoutedEventArgs e) => ApplySymbolToSelection();

    /// <summary>"Apply to selection": the selected objects take the active symbol (<c>FeatureEdit.SwitchSymbol</c>).</summary>
    private void ApplySymbolToSelection()
    {
        if (_doc is null || Symbols.ActiveSymbol is not { } s || _selected.Count == 0) return;
        var targets = _doc.Features.Where(f => _selected.Contains(f.Id)).ToList();
        if (!targets.Any(f => f.CanSwitch(s))) { Show(InfoBarSeverity.Informational, "Change symbol", $"{s.Code} {s.Name} does not fit the selected object kind."); return; }
        Commit("Change symbol", () => { foreach (var f in targets) f.SwitchSymbol(s); });
    }

    private void Reverse_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected.Count == 0) return;
        Commit("Reverse", () => { foreach (var f in _doc.Features.Where(f => _selected.Contains(f.Id) && f.Kind == FeatureKind.Line)) f.Reverse(); });
    }

    private void CloseLine_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected.Count == 0) return;
        Commit("Close line", () =>
        {
            foreach (var f in _doc.Features.Where(f => _selected.Contains(f.Id) && f.Kind == FeatureKind.Line && f.Nodes.Count >= 3))
            {
                if (f.IsRingLine) f.Nodes.RemoveAt(f.Nodes.Count - 1);
                f.Kind = FeatureKind.Area;
            }
        });
    }

    private void DeleteNode(int index)
    {
        if (_doc is null || _selected.Count != 1) return;
        var f = _doc.Features.FirstOrDefault(x => x.Id == _selected.First());
        if (f is null) return;
        var all = f.AllNodes();
        int min = f.Kind == FeatureKind.Area ? 3 : 2;
        if (all.Count <= min) { DeleteSelection(); return; }
        Commit("Delete point", () => { all.RemoveAt(index); f.SetAllNodes(all); });
        _activeNode = null;
    }

    private void FinishDraft()
    {
        if (_doc is null) { _draft.Clear(); return; }
        var pts = _draft.ToList();
        _draft.Clear();
        _cursor = null;
        if (pts.Count < 2) { Canvas.Invalidate(); return; }
        var nodes = pts.Select(p => new BezierNode(p)).ToList();
        AddFeature(MakeVector(nodes, forceLine: pts.Count < 3));
    }

    /// <summary>A line or area from drawn nodes with the active symbol (or the plain pen when none is chosen).</summary>
    private Feature MakeVector(List<BezierNode> nodes, bool forceLine)
    {
        var s = Symbols.ActiveSymbol;
        bool area = !forceLine && s?.SymbolKind == MapSymbol.Kind.Area;
        if (s is null || s.SymbolKind == MapSymbol.Kind.Point) return SymbolFeatures.Sketch(area ? FeatureKind.Area : FeatureKind.Line, nodes, SketchColor, SketchWidthMM);
        return area ? SymbolFeatures.Area(s, nodes) : SymbolFeatures.Line(s, nodes, Symbols.SizeFactor);
    }

    private Feature? FeatureAt(Point2D p)
    {
        if (_doc is null) return null;
        double tol = 7 / _view.PixelsPerMeter;
        Feature? best = null;
        double bestD = double.PositiveInfinity;
        foreach (var f in _doc.Features.Where(f => f.IsVisible))
        {
            if (f.Bounds is { } b && !b.InsetBy(-tol - f.PointRadiusM(_doc.Project.Sheet)).Contains(p)) continue;
            double d = f.Kind == FeatureKind.Point ? Math.Max(0, p.DistanceTo(f.Nodes[0].Point) - f.PointRadiusM(_doc.Project.Sheet)) : f.DistanceTo(p, tol * 0.2);
            if (f.Kind == FeatureKind.Area && d > tol && f.ContainsPoint(p, tol * 0.2)) d = tol * 0.9;   // inside an area: hit, but outlines win
            if (d <= tol && d < bestD) { best = f; bestD = d; }
        }
        return best;
    }

    private int? NodeAt(Feature f, Point2D p)
    {
        double tol = 7 / _view.PixelsPerMeter;
        var all = f.AllNodes();
        for (int i = 0; i < all.Count; i++) if (all[i].Point.DistanceTo(p) <= tol) return i;
        return null;
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
            case ControlKind.Start: ids.RemoveAll(id => id == c.Id); ids.Insert(0, c.Id); break;
            case ControlKind.Finish: ids.RemoveAll(id => id == c.Id); ids.Add(c.Id); break;
            default: ids.Add(c.Id); break;
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
        if (_doc is null || !AppLicense.Shared.Require(ProFeature.ImportMaps)) return;
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
        if (_doc is null || !AppLicense.Shared.Require(ProFeature.CourseExport)) return;
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
            foreach (var filter in new[] { ".tif", ".tiff", ".asc", ".geojson", ".json" }) picker.FileTypeFilter.Add(filter);
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

    /// <summary>"Convert to vector objects": the contour lines become ISOM 101/102/103 line features (one undo step); the live contours are hidden.</summary>
    private void ConvertContours()
    {
        if (_doc is null || _contourLines is not { Count: > 0 } lines) { Show(InfoBarSeverity.Informational, "No contours", "Generate contours first."); return; }
        var settings = _doc.Project.Contours;
        Commit("Convert contours", () => _doc.Features.AddRange(lines.Select(l => l.ToFeature(settings))));
        Contours.Settings.IsEnabled = false;
        _doc.Project.ContourSettings = Contours.Settings;
        Contours.Bind(Contours.Settings, _doc.Project.Sheet, canDownload: _zone is not null && _doc.Project.Frame is not null);
        Canvas.Invalidate();
        Show(InfoBarSeverity.Success, "Contours converted", $"{lines.Count} contour lines added as map objects.");
    }

    // MARK: Input

    private (double Min, double Max) ZoomRange => (_fitRect is { } r && _view.ViewWidth > 0 ? Math.Min(_view.ViewWidth / r.Width, _view.ViewHeight / r.Height) * 0.2 : 0.01, 400);

    private bool CourseEditing => _doc is not null && Courses.Visibility == Visibility.Visible;

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        if (p.PointerDeviceType == PointerDeviceType.Touch) return;   // touch goes through manipulation
        Focus(FocusState.Pointer);
        bool left = p.Properties.IsLeftButtonPressed || p.PointerDeviceType == PointerDeviceType.Pen;
        bool middle = p.Properties.IsMiddleButtonPressed;
        bool space = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (!left && !middle) return;
        _dragLast = _dragStart = p.Position;
        _dragPointer = p.PointerId;
        _dragMoved = false;
        _dragControl = null;
        var m = _view.Unproject(p.Position.X, p.Position.Y);
        Canvas.CapturePointer(e.Pointer);
        if (middle || space || _doc is null || IsSelectingArea || _tool == Tool.Navigate && !CourseEditing) { _drag = Drag.Pan; return; }

        if (CourseEditing)
        {
            // The Courses panel is open: clicks and drags handle controls, the rest pans (as before M2 UI).
            _dragControl = ControlAt(p.Position)?.Id;
            _drag = _dragControl is not null ? Drag.MoveControl : Drag.Pan;
            return;
        }

        switch (_tool)
        {
            case Tool.Select:
            {
                var hit = FeatureAt(m);
                if (hit is null) { if (!shift) _selected.Clear(); _drag = Drag.Box; _shapeStart = m; _shapeEnd = m; }
                else
                {
                    if (shift) { if (!_selected.Add(hit.Id)) _selected.Remove(hit.Id); }
                    else if (!_selected.Contains(hit.Id)) { _selected.Clear(); _selected.Add(hit.Id); }
                    _drag = Drag.MoveFeatures;
                    _dragBefore = Snapshot();
                }
                break;
            }
            case Tool.Node:
            {
                Feature? f = _selected.Count == 1 ? _doc.Features.FirstOrDefault(x => x.Id == _selected.First()) : null;
                if (f is not null && _activeNode is { } active && active < f.AllNodes().Count)
                {
                    var node = f.AllNodes()[active];
                    double htol = 6 / _view.PixelsPerMeter;
                    if (node.InHandle is { } hin && hin.DistanceTo(m) <= htol) { _drag = Drag.MoveHandleIn; _dragBefore = Snapshot(); break; }
                    if (node.OutHandle is { } hout && hout.DistanceTo(m) <= htol) { _drag = Drag.MoveHandleOut; _dragBefore = Snapshot(); break; }
                }
                if (f is not null && NodeAt(f, m) is { } ni) { _activeNode = ni; _drag = Drag.MoveNode; _dragBefore = Snapshot(); break; }
                if (f is not null && f.DistanceTo(m, 1 / _view.PixelsPerMeter) <= 7 / _view.PixelsPerMeter && f.NearestPathPosition(m) is { } near)
                {
                    // Click on a segment: insert a point there and start dragging it.
                    _dragBefore = Snapshot();
                    _activeNode = f.InsertNode(near.Position);
                    _drag = _activeNode is null ? Drag.None : Drag.MoveNode;
                    break;
                }
                var hit = FeatureAt(m);
                _selected.Clear();
                _activeNode = null;
                if (hit is not null) _selected.Add(hit.Id);
                _drag = Drag.None;
                break;
            }
            case Tool.Pen:
                _drag = Drag.None;
                break;
            case Tool.Freehand:
            case Tool.Sketch:
                _freehand.Clear();
                _freehand.Add(m);
                _drag = _tool == Tool.Sketch ? Drag.Sketch : Drag.Freehand;
                break;
            case Tool.Rectangle:
            case Tool.Ellipse:
                _shapeStart = _shapeEnd = m;
                _drag = Drag.Shape;
                break;
            case Tool.Point:
            case Tool.Eraser:
                _drag = Drag.None;
                break;
        }
        SyncToolButtons();
        Canvas.Invalidate();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        var m = _view.Unproject(p.Position.X, p.Position.Y);
        if (_tool == Tool.Pen && _draft.Count > 0) { _cursor = m; Canvas.Invalidate(); }
        if (_dragLast is not { } last || p.PointerId != _dragPointer) return;
        double dx = p.Position.X - last.X, dy = p.Position.Y - last.Y;
        if (!_dragMoved && _dragStart is { } start && Math.Abs(p.Position.X - start.X) < 3 && Math.Abs(p.Position.Y - start.Y) < 3 && _drag is not (Drag.Freehand or Drag.Sketch)) return;
        _dragMoved = true;
        var dm = new Point2D(dx / _view.PixelsPerMeter, -dy / _view.PixelsPerMeter);
        switch (_drag)
        {
            case Drag.Pan:
                _view.PanByPixels(dx, dy);
                break;
            case Drag.MoveControl:
                if (_dragControl is { } id && _doc?.Courses.Control(id) is { } c) { c.Position = m; RebuildOverprint(); }
                break;
            case Drag.MoveFeatures:
                if (_doc is not null)
                    for (int i = 0; i < _doc.Features.Count; i++)
                        if (_selected.Contains(_doc.Features[i].Id)) _doc.Features[i] = _doc.Features[i].MovedBy(dm);
                RebuildSceneLive();
                break;
            case Drag.MoveNode:
                if (SelectedFeature() is { } f && _activeNode is { } ni)
                {
                    var all = f.AllNodes();
                    all[ni] = all[ni].MovedBy(dm);
                    f.SetAllNodes(all);
                    RebuildSceneLive();
                }
                break;
            case Drag.MoveHandleIn:
            case Drag.MoveHandleOut:
                if (SelectedFeature() is { } g && _activeNode is { } hi)
                {
                    g.MoveHandle(hi, _drag == Drag.MoveHandleIn, m);
                    RebuildSceneLive();
                }
                break;
            case Drag.Box:
            case Drag.Shape:
                _shapeEnd = m;
                break;
            case Drag.Freehand:
            case Drag.Sketch:
                if (_freehand.Count == 0 || _freehand[^1].DistanceTo(m) > 1.5 / _view.PixelsPerMeter) _freehand.Add(m);
                break;
        }
        _dragLast = p.Position;
        Canvas.Invalidate();
    }

    private Feature? SelectedFeature() => _doc is not null && _selected.Count == 1 ? _doc.Features.FirstOrDefault(x => x.Id == _selected.First()) : null;

    /// <summary>During a drag the scene is rebuilt without an undo step; <see cref="Record"/> runs once at the end.</summary>
    private void RebuildSceneLive()
    {
        if (_doc is null) return;
        _scene = MapScene.Build(_doc.Features, _doc.Strokes, _doc.Project.Sheet, _doc.Project.AreaOpacityValue);
        _renderer.InvalidateCache();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointer) return;
        var p = e.GetCurrentPoint(Canvas);
        var m = _view.Unproject(p.Position.X, p.Position.Y);
        var drag = _drag;
        _drag = Drag.None;
        _dragLast = null;
        _dragPointer = null;
        Canvas.ReleasePointerCapture(e.Pointer);
        if (_doc is null) return;

        if (drag == Drag.Pan && IsSelectingArea && _dragMoved) FrameFollowsView();

        if (CourseEditing)
        {
            if (_dragControl is { } id)
            {
                if (_dragMoved) { Courses.Refresh(); ScheduleSave(); }
                else if (_doc.Courses.Control(id) is { } c)
                {
                    if (Courses.SelectedCourse is { } course && !course.ControlIds.Contains(c.Id)) { AddToCourse(course, c); Courses.Refresh(); ScheduleSave(); }
                    Courses.SelectControl(c.Id);
                    RebuildOverprint();
                }
            }
            else if (!_dragMoved && Courses.PlaceKind is { } kind) PlaceControl(kind, m);
            else if (!_dragMoved) Courses.SelectControl(null);
            _dragControl = null;
            Canvas.Invalidate();
            return;
        }

        switch (drag)
        {
            case Drag.MoveFeatures:
            case Drag.MoveNode:
            case Drag.MoveHandleIn:
            case Drag.MoveHandleOut:
                if (_dragMoved && _dragBefore is { } before) Record(drag == Drag.MoveFeatures ? "Move" : "Edit point", before);
                else if (drag == Drag.MoveNode && _dragBefore is { } b2 && SelectedFeature() is { } sf && b2.Features.FirstOrDefault(x => x.Id == sf.Id) is { } old && sf.AllNodes().Count != old.AllNodes().Count) Record("Insert point", b2);
                _dragBefore = null;
                break;
            case Drag.Box:
                if (_shapeStart is { } a && _shapeEnd is { } b && _dragMoved)
                {
                    var box = new Rect2D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    foreach (var f in _doc.Features.Where(f => f.IsVisible && f.Bounds is { } fb && box.Contains(fb))) _selected.Add(f.Id);
                }
                _shapeStart = _shapeEnd = null;
                break;
            case Drag.Shape:
                if (_shapeStart is { } s0 && _shapeEnd is { } s1 && _dragMoved && s0.DistanceTo(s1) > 2 / _view.PixelsPerMeter)
                {
                    var corners = new[] { new Point2D(Math.Min(s0.X, s1.X), Math.Max(s0.Y, s1.Y)), new Point2D(Math.Max(s0.X, s1.X), Math.Max(s0.Y, s1.Y)),
                                          new Point2D(Math.Max(s0.X, s1.X), Math.Min(s0.Y, s1.Y)), new Point2D(Math.Min(s0.X, s1.X), Math.Min(s0.Y, s1.Y)) };
                    var sym = Symbols.ActiveSymbol;
                    var color = sym?.ColorHex ?? "#C8C8C8";
                    var f = _tool == Tool.Ellipse ? FeatureEdit.Ellipse(corners, color, sym?.DrawingWidthMM ?? 0.1) : FeatureEdit.Rectangle(corners, color, sym?.DrawingWidthMM ?? 0.1);
                    if (sym is { SymbolKind: MapSymbol.Kind.Area }) { f.SymbolCode = sym.Code; f.SymbolName = sym.Name; }
                    AddFeature(f);
                }
                _shapeStart = _shapeEnd = null;
                break;
            case Drag.Freehand:
            {
                var pts = PolylineSimplify.Simplify(_freehand, 1.5 / _view.PixelsPerMeter);
                _freehand.Clear();
                if (pts.Count >= 2)
                {
                    bool closes = pts.Count >= 3 && pts[0].DistanceTo(pts[^1]) < 12 / _view.PixelsPerMeter;
                    if (closes) pts = pts.Take(pts.Count - 1).ToList();
                    AddFeature(MakeVector(pts.Select(q => new BezierNode(q)).ToList(), forceLine: pts.Count < 3 || (!closes && ActiveKind != MapSymbol.Kind.Area)));
                }
                break;
            }
            case Drag.Sketch:
            {
                var pts = PolylineSimplify.Simplify(_freehand, 0.5 / _view.PixelsPerMeter);
                _freehand.Clear();
                if (pts.Count >= 1) Commit("Sketch", () => _doc.Strokes.Add(new Stroke { ColorHex = SketchColor, WidthMM = SketchWidthMM, Points = pts }));
                break;
            }
            case Drag.None when !_dragMoved:
                Click(m);
                break;
        }
        SyncToolButtons();
        Canvas.Invalidate();
    }

    /// <summary>A click (no drag) with the current tool.</summary>
    private void Click(Point2D m)
    {
        if (_doc is null) return;
        switch (_tool)
        {
            case Tool.Pen:
                if (_draft.Count > 0 && _draft[^1].DistanceTo(m) < 2 / _view.PixelsPerMeter) return;
                _draft.Add(m);
                _cursor = m;
                break;
            case Tool.Point:
            {
                if (Symbols.ActiveSymbol is not { SymbolKind: MapSymbol.Kind.Point } s)
                {
                    Show(InfoBarSeverity.Informational, "Point symbol", "Select a point symbol in the Symbols panel first (e.g. 204.0 Boulder).");
                    SymbolsToggle.IsChecked = true; UpdatePanels();
                    return;
                }
                if (SymbolFeatures.Point(s, m, Symbols.SizeFactor) is { } f) AddFeature(f);
                break;
            }
            case Tool.Eraser:
            {
                double tol = 8 / _view.PixelsPerMeter;
                var hit = _doc.Strokes.Select(s => (Stroke: s, D: s.DistanceTo(m))).Where(x => x.D <= tol + x.Stroke.WidthMM * MetersPerMM).OrderBy(x => x.D).FirstOrDefault().Stroke;
                if (hit is not null) Commit("Erase stroke", () => _doc.Strokes.Remove(hit));
                break;
            }
        }
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_tool != Tool.Pen || _draft.Count == 0) return;
        // The double-click's two clicks added one point twice: drop the duplicate, then finish.
        if (_draft.Count >= 2 && _draft[^1].DistanceTo(_draft[^2]) < 4 / _view.PixelsPerMeter) _draft.RemoveAt(_draft.Count - 1);
        FinishDraft();
        e.Handled = true;
    }

    /// <summary>Right-click: select the object under the pointer and show the menu; no menu in Select Area mode, course planning or on empty paper.</summary>
    private void Canvas_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_doc is null || IsSelectingArea || CourseEditing) { e.Handled = true; return; }
        if (e.TryGetPosition(Canvas, out var pos))
        {
            var m = _view.Unproject(pos.X, pos.Y);
            var hit = FeatureAt(m);
            if (hit is not null && !_selected.Contains(hit.Id)) { _selected.Clear(); _selected.Add(hit.Id); SyncToolButtons(); Canvas.Invalidate(); }
        }
        if (_selected.Count == 0) e.Handled = true;   // nothing to act on: no menu
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
        else if (shift || p.Properties.IsHorizontalMouseWheel) _view.PanByPixels(delta * 0.5, 0);
        else _view.PanByPixels(0, delta * 0.5);
        if (IsSelectingArea) FrameFollowsView();
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
        if (IsSelectingArea) FrameFollowsView();
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
        if (_doc is not null && Math.Abs(_doc.Project.BasemapOpacityValue - _basemapOpacity) > 1e-6) { _doc.Project.BasemapOpacity = _basemapOpacity; ScheduleSave(); }
        Canvas.Invalidate();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_tool == Tool.Pen) FinishDraft();
        _saveTimer?.Stop();
        SaveNow();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>M3/M4: PDF (CMYK, Geospatial PDF, course + descriptions), PNG/TIFF/JPEG + world file + .prj, OMAP, DXF.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _scene is null) return;
        if (_doc.Project.Frame is not { } frame || _doc.Project.Zone is not { } zone || IsSelectingArea)
        {
            Show(InfoBarSeverity.Warning, "No map area", "Lock a map area (frame and UTM zone) before exporting.");
            return;
        }
        bool courseAvailable = !_doc.Courses.IsEmpty;
        bool contoursAvailable = _contourLines is { Count: > 0 } && _doc.Project.Contours.IncludeInExport;
        var dialog = new ExportDialog(WindowHandle, _doc.Project.Name, _doc.Project.NorthGrid is { IsEnabled: true }, courseAvailable, contoursAvailable) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        // Starter exports PDF (with the OCCA credit); raster and vector exports need a license key.
        var license = AppLicense.Shared;
        if (dialog.SelectedKind is ExportDialog.Kind.Png or ExportDialog.Kind.Tiff or ExportDialog.Kind.Jpeg && !license.Require(ProFeature.RasterExport)) return;
        if (dialog.SelectedKind is ExportDialog.Kind.Omap or ExportDialog.Kind.Dxf && !license.Require(ProFeature.VectorExport)) return;
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
                        Watermark = license.IsPro ? null : $"Made with {AppInfo.Name} · occa.jcdigital.co.id",
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

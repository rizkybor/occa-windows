using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Occa.App.License;
using Occa.Core;
using Occa.Core.Basemap;
using Occa.Core.License;
using Occa.Core.Project;
using Windows.Storage.Pickers;

namespace Occa.App;

/// <summary>Main window: the project list (%LOCALAPPDATA%\OCCA\Projects) and, when a project is open, its map.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>FR-W9: projects live in %LOCALAPPDATA%\OCCA\Projects in the same package layout as Mac.</summary>
    public static string ProjectsFolder => Path.Combine(UserSettings.Folder, "Projects");

    private readonly UserSettings _settings = UserSettings.Load();
    private readonly ProjectRepository _repository = new(ProjectsFolder);
    private List<ProjectListItem> _projects = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/occa.ico");
        // FR-W4: start at 1280×820 (physical pixels) or where the window was last time, never larger than the display's
        // work area and never off-screen (a monitor may have been unplugged since). Sizes are clamped so a 1366×768
        // laptop at 125 % gets a window that fits with the taskbar.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int minW = Math.Min(1000, work.Width), minH = Math.Min(640, work.Height);
        if (_settings is { WindowWidth: int w, WindowHeight: int h, WindowX: int x, WindowY: int y } && w >= minW && h >= minH
            && DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(x, y, w, h), DisplayAreaFallback.None) is { } area)
        {
            var wa = area.WorkArea;
            w = Math.Min(w, wa.Width); h = Math.Min(h, wa.Height);
            x = Math.Clamp(x, wa.X, Math.Max(wa.X, wa.X + wa.Width - w)); y = Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - h));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }
        else
        {
            int dw = Math.Min(1280, work.Width - 40), dh = Math.Min(820, work.Height - 40);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - dw) / 2, work.Y + (work.Height - dh) / 2, dw, dh));
        }
        AppWindow.Changed += (_, e) =>
        {
            if ((!e.DidSizeChange && !e.DidPositionChange) || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
            _settings.WindowX = AppWindow.Position.X; _settings.WindowY = AppWindow.Position.Y;
            _settings.WindowWidth = AppWindow.Size.Width; _settings.WindowHeight = AppWindow.Size.Height;
            ScheduleSettingsSave();
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minW;
            presenter.PreferredMinimumHeight = minH;
        }
        // Pending autosave of the open project is written before the process ends.
        Closed += (_, _) => { Map.FlushSave(); try { _settings.Save(); } catch (Exception) { } };
        TaglineText.Text = AppInfo.Tagline;
        Map.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        CreditText.Text = $"{AppInfo.Name} {AppInfo.Year} · {AppInfo.Country} · Supported by";
        ApplyAppearance();
        LoadProjects();
        // M5: licensing (HRIS keys, offline token) and the update check.
        _license.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshPlan);
        RefreshPlan();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            _license.Attach(Content.XamlRoot, RootGrid.RequestedTheme);
            _license.Engine.Recheck();
        };
        _ = StartLicenseAsync();
    }

    private readonly AppLicense _license = AppLicense.Shared;
    private UpdateChecker.Result? _update;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settingsTimer;

    /// <summary>Window bounds are saved a moment after the last move/resize.</summary>
    private void ScheduleSettingsSave()
    {
        if (_settingsTimer is null)
        {
            _settingsTimer = DispatcherQueue.CreateTimer();
            _settingsTimer.Interval = TimeSpan.FromMilliseconds(800);
            _settingsTimer.IsRepeating = false;
            _settingsTimer.Tick += (_, _) => { try { _settings.Save(); } catch (Exception) { } };
        }
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    /// <summary>Opens a project file handed over by Explorer (file association, FR-W8) or the command line (MSI install).</summary>
    public async Task OpenFileFromActivationAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            await ImportAndOpenAsync(path);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot open project file", ex.Message);
        }
    }

    /// <summary>New project (M2): dialog, then the map in Select Area mode.</summary>
    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!_license.Require(ProFeature.UnlimitedProjects, overLimit: _projects.Count >= FreeLimits.Projects)) return;
        var dialog = new Views.NewProjectDialog(_settings) { XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var project = dialog.Build();
        if (project is null) return;
        try
        {
            var entry = await Task.Run(() => _repository.Create(new ProjectDocument { Project = project }));
            LoadProjects();
            await OpenProject(entry);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot create project", ex.Message);
        }
    }

    private async Task StartLicenseAsync()
    {
        _license.Attach(Content.XamlRoot, RootGrid.RequestedTheme);
        await _license.StartAsync(DispatcherQueue);
        var checker = new UpdateChecker(_license.Api, WindowsDevice.AppVersion);
        _update = await checker.CheckAsync(_license.Engine.Payload?.Key);
        if (_update is { } u)
        {
            UpdateBar.Severity = u.Required ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            UpdateBar.Title = u.Required ? "Update required" : "Update available";
            UpdateBar.Message = u.Summary + (u.Notes.Length > 0 ? "\n" + u.Notes : "");
            UpdateButton.Content = u.IsPackaged ? "Open Microsoft Store" : "Download";
            UpdateButton.IsEnabled = u.Target is not null;
            UpdateBar.IsOpen = true;
        }
    }

    /// <summary>Plan chip (header) and the Starter / Trial watermark follow the license.</summary>
    private void RefreshPlan()
    {
        var engine = _license.Engine;
        PlanChipText.Text = engine.Plan.Title() + (engine.Source == LicenseSource.Developer ? " · dev" : "");
        var (fill, border, strong) = engine.Plan switch
        {
            LicensePlan.Trial => ("CpButterFill", "CpSandBorder", "CpButterStrong"),
            LicensePlan.Professional => ("CpSageFill", "CpSageBorder", "CpSageStrong"),
            LicensePlan.Lifetime => ("CpRoseFill", "CpSandBorder", "CpRoseStrong"),
            LicensePlan.Organization => ("CpSkyFill", "CpSandBorder", "CpSkyStrong"),
            _ => ("CpSandFill", "CpSandBorder", "CpSandStrong"),
        };
        PlanChipBorder.Background = ThemeBrushes.Get(fill, RootGrid.ActualTheme);
        PlanChipBorder.BorderBrush = ThemeBrushes.Get(border, RootGrid.ActualTheme);
        PlanChipText.Foreground = ThemeBrushes.Get(strong, RootGrid.ActualTheme);
        var label = engine.WatermarkLabel;
        Watermark.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        WatermarkTitle.Text = $"{AppInfo.Name} {engine.Plan.Title()}";
        WatermarkDetail.Text = engine.WatermarkDetail;
    }

    private async void PlanChip_Click(object sender, RoutedEventArgs e)
    {
        _license.Attach(Content.XamlRoot, RootGrid.RequestedTheme);
        await _license.ShowAsync(null);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update?.Target is { } target) await Windows.System.Launcher.LaunchUriAsync(target);
    }

    // MARK: Appearance

    private void ApplyAppearance()
    {
        RootGrid.RequestedTheme = _settings.Appearance switch
        {
            AppearanceMode.Light => ElementTheme.Light,
            AppearanceMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // The system caption buttons (minimise / maximise / close) sit on our title bar: transparent background, ink
        // of the active theme; otherwise they keep a light box in dark mode.
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = AppWindow.TitleBar;
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            var ink = dark ? Windows.UI.Color.FromArgb(255, 0xF1, 0xEC, 0xE4) : Windows.UI.Color.FromArgb(255, 0x2B, 0x2A, 0x28);
            var muted = dark ? Windows.UI.Color.FromArgb(255, 0xA8, 0xA1, 0x96) : Windows.UI.Color.FromArgb(255, 0x6B, 0x66, 0x5E);
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonForegroundColor = ink;
            tb.ButtonInactiveForegroundColor = muted;
            tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 0x3A, 0x37, 0x33) : Windows.UI.Color.FromArgb(255, 0xE3, 0xDD, 0xD2);
            tb.ButtonHoverForegroundColor = ink;
            tb.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 0x4A, 0x46, 0x41) : Windows.UI.Color.FromArgb(255, 0xD5, 0xCE, 0xC2);
            tb.ButtonPressedForegroundColor = ink;
        }
        AppearanceIcon.Glyph = _settings.Appearance.Glyph();
        ToolTipService.SetToolTip(AppearanceButton, $"Appearance: {_settings.Appearance.Label()} · click for {_settings.Appearance.Next().Label()}");
    }

    /// <summary>One click switches to the next mode (Light → Dark → System → Light), saved for the next launch.</summary>
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        _settings.Appearance = _settings.Appearance.Next();
        ApplyAppearance();
        RefreshPlan();   // the chip's brushes are code-created and follow the element theme
        _license.Attach(Content.XamlRoot, RootGrid.RequestedTheme);
        try { _settings.Save(); } catch (Exception ex) { ShowStatus(InfoBarSeverity.Warning, "Settings not saved", ex.Message); }
    }

    private void Splash_Finished(object? sender, EventArgs e) => RootGrid.Children.Remove(Splash);

    /// <summary>
    /// Page width and centring from code (FR-W4): 1280 up to 1700 effective px, 1600 up to 2300, 2000 beyond, centred in
    /// the window. Set explicitly (Width + left margin) so an over-wide child can never shift the page off centre.
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double available = e.NewSize.Width;
        if (available <= 0) return;
        double max = available >= 2300 ? 2000 : available >= 1700 ? 1600 : 1280;
        double width = Math.Min(max, available);
        Page.Width = width;
        Page.Margin = new Thickness(Math.Max(0, (available - width) / 2), 0, 0, 0);
    }

    // MARK: Projects

    private void LoadProjects()
    {
        try
        {
            _projects = _repository.Load().Select(ProjectListItem.From).ToList();
        }
        catch (Exception ex)
        {
            _projects = new();
            ShowStatus(InfoBarSeverity.Error, "Cannot read the projects folder", ex.Message);
        }
        EmptyStateDetail.Text = ProjectsFolder;
        ShowProjects();
    }

    private void ShowProjects()
    {
        bool any = _projects.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ProjectGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = $"{_projects.Count} {(_projects.Count == 1 ? "project" : "projects")}";
        var query = SearchBox.Text?.Trim() ?? "";
        ProjectGrid.ItemsSource = query.Length == 0 ? _projects : _projects.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ShowProjects();

    private async void ProjectGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectListItem item) await OpenProject(item.Entry);
    }

    private async Task OpenProject(ProjectRepository.Entry entry)
    {
        Map.Visibility = Visibility.Visible;
        AppTitleText.Text = $"{AppInfo.Name} · {entry.Project.Name}";
        await Map.OpenAsync(entry);
    }

    private void Map_BackRequested(object? sender, EventArgs e)
    {
        Map.Visibility = Visibility.Collapsed;
        AppTitleText.Text = AppInfo.Name;
        LoadProjects();
    }

    /// <summary>Open file: a Mac/iPad .occa is imported into the projects folder, then opened.</summary>
    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(ProjectArchive.FileExtension);
            foreach (var ext in ProjectArchive.LegacyFileExtensions) picker.FileTypeFilter.Add(ext);
            foreach (var ext in Occa.Core.Import.MapFileImport.FileExtensions) picker.FileTypeFilter.Add("." + ext);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await ImportAndOpenAsync(file.Path);
        }
        catch (Exception ex)
        {
            // The picker itself can fail too (for example when OCCA runs elevated), so it is inside the try.
            ShowStatus(InfoBarSeverity.Error, "Cannot open project file", ex.Message);
        }
    }

    private async Task ImportAndOpenAsync(string filePath)
    {
        {
            var file = new { Path = filePath };
            var bytes = await File.ReadAllBytesAsync(file.Path);
            var extension = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();
            ProjectRepository.Entry entry;
            // Starter: up to FreeLimits.Projects projects; existing ones are never locked (FR-W12 limits as on Mac).
            if (!_license.Require(ProFeature.UnlimitedProjects, overLimit: _projects.Count >= FreeLimits.Projects)) return;
            if (Occa.Core.Import.MapFileImport.FileExtensions.Contains(extension))
            {
                if (!_license.Require(ProFeature.ImportMaps)) return;
                // OCAD / OOM map → a new project (M3). OCAD files do not store their UTM zone: ask for it.
                var content = await Task.Run(() => Occa.Core.Import.MapFileImport.Read(bytes, extension));
                var zone = content.KnownZone ?? await AskZoneAsync();
                if (zone is not { } z) return;
                var doc = Occa.Core.Import.MapFileImport.Document(Path.GetFileNameWithoutExtension(file.Path), content, z);
                entry = await Task.Run(() => _repository.Create(doc));
                LoadProjects();
                ShowStatus(InfoBarSeverity.Success, "Map imported", $"{content.Report.Imported} objects imported" + (content.Report.SkippedText + content.Report.SkippedPoints > 0 ? $", {content.Report.SkippedText} text and {content.Report.SkippedPoints} point objects skipped." : "."));
                await OpenProject(entry);
                return;
            }
            entry = await Task.Run(() => _repository.Import(bytes));
            LoadProjects();
            ShowStatus(InfoBarSeverity.Success, "Project imported", $"\"{entry.Project.Name}\" was added to your projects.");
            await OpenProject(entry);
        }
    }

    /// <summary>UTM zone for an OCAD map (which stores projected metres but not the zone).</summary>
    private async Task<Core.Geometry.UtmZone?> AskZoneAsync()
    {
        var zones = Enumerable.Range(1, 60).SelectMany(n => new[] { Core.Geometry.UtmZone.Create(n, false)!.Value, Core.Geometry.UtmZone.Create(n, true)!.Value }).ToList();
        var box = new ComboBox { ItemsSource = zones.Select(z => z.Label).ToList(), SelectedIndex = zones.FindIndex(z => z.Number == 48 && !z.IsNorth), HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog
        {
            Title = "UTM zone of the OCAD map",
            Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "OCAD files keep projected coordinates but not their UTM zone. Choose the zone of the map area (Indonesia: 46–54, S south of the equator).", TextWrapping = TextWrapping.Wrap }, box } },
            PrimaryButtonText = "Import", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.SelectedIndex >= 0 ? zones[box.SelectedIndex] : null;
    }

    // MARK: Dialogs

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        await ShowDialog($"About {AppInfo.Name}",
            $"{AppInfo.Name} · {AppInfo.FullName}\n{AppInfo.VersionText}\n\nDeveloper: {AppInfo.Developer}\nSupported by {AppInfo.Supporter}\n{AppInfo.SupporterUrl}\n\n"
            + $"Satellite imagery: {EsriWorldImagery.Attribution}.");
    }

    private async Task ShowDialog(string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 480 },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
        };
        await dialog.ShowAsync();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}

/// <summary>One card of the project list (Mac's ProjectCard).</summary>
public sealed class ProjectListItem
{
    public required ProjectRepository.Entry Entry { get; init; }
    public string Name => Entry.Project.Name;
    public string Summary => Entry.Project.TechnicalSummary;
    public string ScaleLabel => $"1:{Entry.Project.Sheet.ScaleDenominator}";
    public string Dates => $"Created {Entry.Project.CreatedAt.ToLocalTime():d MMM yyyy} · Modified {Entry.Project.UpdatedAt.ToLocalTime():d MMM yyyy}";
    public BitmapImage? Thumbnail => Entry.HasThumbnail ? new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, UriSource = new Uri(Entry.ThumbnailPath) } : null;
    public string PreviewLabel => Entry.Project.AreaLocked ? "No preview yet" : "Select a map area or upload a GeoTIFF basemap";
    public Visibility PreviewLabelVisibility => Entry.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;

    public static ProjectListItem From(ProjectRepository.Entry entry) => new() { Entry = entry };
}

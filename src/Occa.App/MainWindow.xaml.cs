using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Occa.Core;
using Occa.Core.Basemap;
using Occa.Core.Project;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

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
        // FR-W4: the main window needs at least 1100×760; start a little larger.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1100;
            presenter.PreferredMinimumHeight = 760;
        }
        TaglineText.Text = AppInfo.Tagline;
        Map.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        CreditText.Text = $"{AppInfo.Name} {AppInfo.Year} · {AppInfo.Country} · Supported by";
        ApplyAppearance();
        LoadProjects();
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
        AppearanceIcon.Glyph = _settings.Appearance.Glyph();
        ToolTipService.SetToolTip(AppearanceButton, $"Appearance: {_settings.Appearance.Label()} · click for {_settings.Appearance.Next().Label()}");
    }

    /// <summary>One click switches to the next mode (Light → Dark → System → Light), saved for the next launch.</summary>
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        _settings.Appearance = _settings.Appearance.Next();
        ApplyAppearance();
        try { _settings.Save(); } catch (Exception ex) { ShowStatus(InfoBarSeverity.Warning, "Settings not saved", ex.Message); }
    }

    private void Splash_Finished(object? sender, EventArgs e) => RootGrid.Children.Remove(Splash);

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

            var bytes = await File.ReadAllBytesAsync(file.Path);
            var extension = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();
            ProjectRepository.Entry entry;
            if (Occa.Core.Import.MapFileImport.FileExtensions.Contains(extension))
            {
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
        catch (Exception ex)
        {
            // The picker itself can fail too (for example when OCCA runs elevated), so it is inside the try.
            ShowStatus(InfoBarSeverity.Error, "Cannot open project file", ex.Message);
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

    // MARK: Spikes and dialogs

    /// <summary>M0 spike: download one Esri World Imagery tile with the configured API key.</summary>
    private async void BasemapTest_Click(object sender, RoutedEventArgs e)
    {
        var key = App.Settings.EsriApiKey;
        if (key is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Esri API key",
                $"Put the key in appsettings.local.json next to OCCA.exe or set {EsriWorldImagery.ApiKeyEnvironmentVariable}.");
            return;
        }
        try
        {
            var imagery = new EsriWorldImagery(App.Http, key, App.Settings.Esri.TileTemplate);
            // Monas, Jakarta at zoom 17.
            var (x, y) = WebMercator.TileFor(-6.1754, 106.8272, 17);
            var tile = await imagery.FetchTileAsync(x, y, 17);

            var bitmap = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(tile.Bytes.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
            }
            var dialog = new ContentDialog
            {
                Title = $"Esri World Imagery tile {tile.Zoom}/{tile.Y}/{tile.X}",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new Image { Source = bitmap, Width = 256, Height = 256 },
                        new TextBlock { Text = $"{tile.MediaType}, {tile.Bytes.Length:N0} bytes, cache max-age {(tile.CacheMaxAge?.ToString() ?? "n/a")}" },
                        new TextBlock { Text = EsriWorldImagery.Attribution, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                    },
                },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Tile download failed", ex.Message);
        }
    }

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
    public BitmapImage? Thumbnail => Entry.HasThumbnail ? new BitmapImage(new Uri(Entry.ThumbnailPath)) : null;
    public string PreviewLabel => Entry.Project.AreaLocked ? "No preview yet" : "Select a map area or upload a GeoTIFF basemap";
    public Visibility PreviewLabelVisibility => Entry.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;

    public static ProjectListItem From(ProjectRepository.Entry entry) => new() { Entry = entry };
}

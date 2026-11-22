using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Occa.Core.Basemap;
using Occa.Core.Project;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Occa.App;

/// <summary>Main window: the project list (M0: empty, reads %LOCALAPPDATA%\OCCA\Projects) and the M0 spike actions.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>FR-W9: projects live in %LOCALAPPDATA%\OCCA\Projects in the same package layout as Mac.</summary>
    public static string ProjectsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OCCA", "Projects");

    private static readonly HttpClient Http = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/occa.ico");
        // FR-W4: the main window needs at least 1100×760; start a little larger.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 1100;
        if (AppWindow.Presenter is OverlappedPresenter p2) p2.PreferredMinimumHeight = 760;
        LoadProjects();
    }

    private void LoadProjects()
    {
        var items = new List<ProjectListItem>();
        if (Directory.Exists(ProjectsFolder))
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsFolder, "*.cakra"))
            {
                var projectFile = Path.Combine(dir, ProjectArchive.ProjectFileName);
                if (!File.Exists(projectFile)) continue;
                try
                {
                    var entries = new Dictionary<string, byte[]> { [ProjectArchive.ProjectFileName] = File.ReadAllBytes(projectFile) };
                    var summary = ProjectSummary.FromEntries(entries);
                    items.Add(new ProjectListItem(summary.Name, $"1:{summary.ScaleDenominator}" + (summary.CrsEpsg is int epsg ? $" · EPSG:{epsg}" : "")));
                }
                catch (Exception)
                {
                    items.Add(new ProjectListItem(Path.GetFileNameWithoutExtension(dir), "Cannot read project.json"));
                }
            }
        }
        ProjectList.ItemsSource = items.OrderBy(i => i.Name).ToList();
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateDetail.Text = $"Projects you create or import appear here.\n{ProjectsFolder}";
    }

    /// <summary>M0 spike: read a Mac/iPad .occa (LZFSE) and show what it contains.</summary>
    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(ProjectArchive.FileExtension);
        foreach (var ext in ProjectArchive.LegacyFileExtensions) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(file.Path);
            var summary = await Task.Run(() => ProjectSummary.FromEntries(ProjectArchive.ReadEntries(bytes)));
            var text = $"Name: {summary.Name}\nScale: 1:{summary.ScaleDenominator}\nCRS: {(summary.CrsEpsg is int epsg ? $"EPSG:{epsg}" : "not set")}\n\n"
                       + $"Objects: {summary.ObjectCount} ({summary.FeatureCount} features, {summary.StrokeCount} sketch strokes)\n"
                       + $"Controls: {summary.ControlCount}\nCourses: {summary.CourseCount}"
                       + (summary.EventTitle.Length > 0 ? $" (event \"{summary.EventTitle}\")" : "")
                       + $"\n\nFiles in archive:\n  {string.Join("\n  ", summary.Files)}";
            await ShowDialog(file.Name, text);
        }
        catch (ProjectArchiveException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot open project file", ex.Message);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot open project file", ex.Message);
        }
    }

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
            var imagery = new EsriWorldImagery(Http, key, App.Settings.Esri.TileTemplate);
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
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Tile download failed", ex.Message);
        }
    }

    private async Task ShowDialog(string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 480 },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
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

/// <summary>One row of the project list.</summary>
public sealed record ProjectListItem(string Name, string Summary);

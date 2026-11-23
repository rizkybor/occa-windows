using Microsoft.UI.Xaml;

namespace Occa.App;

/// <summary>OCCA for Windows. The shell is a single main window (FR-W1) that starts on the project list.</summary>
public partial class App : Application
{
    private Window? _window;

    public static AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>One client for tiles and the license API (connection reuse).</summary>
    public static HttpClient Http { get; } = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        window.Activate();
        // FR-W8: a .occa / .occafield / .occasym double-clicked in Explorer. MSIX: file activation; MSI: the path on the command line.
        var path = ActivatedFilePath();
        if (path is not null) _ = window.DispatcherQueue.TryEnqueue(async () => await window.OpenFileFromActivationAsync(path));
    }

    private static string? ActivatedFilePath()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File && activation.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs files)
                return files.Files.OfType<Windows.Storage.IStorageFile>().FirstOrDefault()?.Path;
        }
        catch (Exception) { /* unpackaged (MSI) or no activation info */ }
        var args = Environment.GetCommandLineArgs();
        return args.Skip(1).FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal) && File.Exists(a));
    }
}

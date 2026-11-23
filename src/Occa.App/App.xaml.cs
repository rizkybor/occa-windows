using Microsoft.UI.Xaml;

namespace Occa.App;

/// <summary>OCCA for Windows. The shell is a single main window (FR-W1) that starts on the project list.</summary>
public partial class App : Application
{
    private Window? _window;

    public static AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>One client for tiles and the license API (connection reuse).</summary>
    public static HttpClient Http { get; } = new();

    /// <summary>Startup and crash log for testers: %LOCALAPPDATA%\OCCA\logs\occa.log (see docs/install-msi.md §7).</summary>
    public static string LogPath => Path.Combine(UserSettings.Folder, "logs", "occa.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* logging must never fail the app */ }
    }

    public App()
    {
        // Every unhandled exception is written to the log (a silent crash at startup is otherwise invisible to testers).
        UnhandledException += (_, e) =>
        {
            Log($"Unhandled XAML exception: {e.Exception}");
            e.Handled = _window is not null;   // once the window exists, keep running; before that let the process end
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"Unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log($"Unobserved task exception: {e.Exception}"); e.SetObserved(); };
        try { InitializeComponent(); }
        catch (Exception ex) { Log($"App.InitializeComponent failed: {ex}"); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log($"Launch {Core.AppInfo.VersionText} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} · packaged={License.WindowsDevice.IsPackaged}");
        MainWindow window;
        try
        {
            window = new MainWindow();
            _window = window;
            window.Activate();
        }
        catch (Exception ex)
        {
            Log($"Main window failed to start: {ex}");
            throw;
        }
        Log("Main window shown");
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

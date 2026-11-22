using Microsoft.UI.Xaml;

namespace Occa.App;

/// <summary>OCCA for Windows. The shell is a single main window (FR-W1) that starts on the project list.</summary>
public partial class App : Application
{
    private Window? _window;

    public static AppSettings Settings { get; } = AppSettings.Load();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

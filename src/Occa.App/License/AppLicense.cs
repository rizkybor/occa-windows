using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Occa.App.Views;
using Occa.Core.License;

namespace Occa.App.License;

/// <summary>
/// The app's single license: the <see cref="LicenseEngine"/> with DPAPI storage, the Windows device id, the HRIS API and
/// the re-check timer, plus the gate that opens the License screen (<c>LicenseManager</c> + <c>LicensePresenter</c> on Mac).
/// Developer builds (Debug) are Lifetime unless started with <c>--license key</c>; <c>--license starter|trial|professional|
/// lifetime|organization</c> fixes a plan, <c>--license-api &lt;url&gt;</c> (or <c>OCCA_LICENSE_API</c>) picks the server.
/// </summary>
public sealed class AppLicense
{
    public static AppLicense Shared { get; } = new();

    public LicenseEngine Engine { get; }
    public LicenseApi Api { get; }
    private DispatcherQueueTimer? _recheck;
    private XamlRoot? _xamlRoot;
    private ElementTheme _theme;
    private bool _dialogOpen;

    /// <summary>Raised on the UI thread after any change (plan, message, busy).</summary>
    public event EventHandler? Changed;

    private AppLicense()
    {
        var store = new DpapiLicenseStore();
#if DEBUG
        var apiBase = Environment.GetEnvironmentVariable("OCCA_LICENSE_API") ?? ArgumentAfter("--license-api") ?? App.Settings.License.ApiBaseUrl;
#else
        var apiBase = App.Settings.License.ApiBaseUrl;
#endif
        Api = new LicenseApi(App.Http, apiBase);
        Engine = new LicenseEngine(store, Api, WindowsDevice.ResolveDeviceId(store), WindowsDevice.DeviceName, WindowsDevice.AppVersion);
        Engine.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        var fixedPlan = FixedPlanFromArguments();
        if (fixedPlan is { } p) Engine.SetFixedPlan(p);
        else Engine.Load();
    }

    private static string? ArgumentAfter(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Debug builds only (NFR-6: Release builds have no overrides and always use keys).</summary>
    private static LicensePlan? FixedPlanFromArguments()
    {
#if DEBUG
        var arg = ArgumentAfter("--license")?.ToLowerInvariant();
        // Debug builds behave like Lifetime unless keys are asked for (as the Mac's developer builds).
        if (arg is null || arg == "developer") return LicensePlan.Lifetime;
        return arg switch
        {
            "key" => null,
            "free" or "starter" => LicensePlan.Starter,
            "trial" => LicensePlan.Trial,
            "pro" or "professional" => LicensePlan.Professional,
            "lifetime" => LicensePlan.Lifetime,
            "organization" => LicensePlan.Organization,
            _ => null,
        };
#else
        return null;
#endif
    }

    public bool IsPro => Engine.IsPro;
    public LicensePlan Plan => Engine.Plan;

    /// <summary>Where dialogs open (the main window); call once the window content exists.</summary>
    public void Attach(XamlRoot root, ElementTheme theme)
    {
        _xamlRoot = root;
        _theme = theme;
    }

    /// <summary>At launch and when the window is activated again: pending release, restore after a reinstall, weekly refresh, re-check timer.</summary>
    public async Task StartAsync(DispatcherQueue dispatcher)
    {
        ScheduleRecheck(dispatcher);
        try { await Engine.StartAsync(); }
        catch (Exception) { /* network trouble keeps the current state */ }
        ScheduleRecheck(dispatcher);
        if (Engine.NeedsNewKey) await ShowAsync(null);
    }

    /// <summary>While the app stays open: apply the token again when its end date or offline limit arrives.</summary>
    private void ScheduleRecheck(DispatcherQueue dispatcher)
    {
        _recheck?.Stop();
        if (Engine.NextChange is not { } at) return;
        var delay = at - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        if (delay > TimeSpan.FromDays(20)) return;   // beyond the timer range that matters; the launch check covers it
        if (_recheck is null)
        {
            _recheck = dispatcher.CreateTimer();
            _recheck.IsRepeating = false;
            _recheck.Tick += (_, _) => { Engine.Recheck(); ScheduleRecheck(dispatcher); };
        }
        _recheck.Interval = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        _recheck.Start();
    }

    /// <summary>Paid plan: true. Starter: shows the License screen for this feature and returns false.</summary>
    public bool Require(ProFeature feature)
    {
        if (Engine.IsPro) return true;
        _ = ShowAsync(feature);
        return false;
    }

    /// <summary>For Starter limits: true while under the limit or on a paid plan; otherwise shows the License screen.</summary>
    public bool Require(ProFeature feature, bool overLimit) => !overLimit || Require(feature);

    /// <summary>Opens the License screen (once at a time).</summary>
    public async Task ShowAsync(ProFeature? feature)
    {
        if (_dialogOpen || _xamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            var dialog = new LicenseDialog(this, feature) { XamlRoot = _xamlRoot, RequestedTheme = _theme };
            await dialog.ShowAsync();
        }
        finally { _dialogOpen = false; }
    }
}

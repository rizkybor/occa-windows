using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Occa.App.License;
using Occa.Core;
using Occa.Core.License;

namespace Occa.App.Views;

/// <summary>
/// The License screen (Mac's <c>LicenseView</c>): current plan with its details, Check now / Release this PC, the key
/// field with Activate, the plans and the feature list. Opened from the plan chip, from a locked feature, or when a
/// dead key needs replacing.
/// </summary>
public sealed partial class LicenseDialog : ContentDialog
{
    private readonly AppLicense _license;
    private readonly ProFeature? _feature;
    private bool _formatting;

    public LicenseDialog(AppLicense license, ProFeature? feature)
    {
        InitializeComponent();
        _license = license;
        _feature = feature;
        _license.Changed += License_Changed;
        Closed += (_, _) => _license.Changed -= License_Changed;
        BuildPlans();
        BuildFeatures();
        if (feature is { } f)
        {
            LockedNotice.Title = f.Title();
            LockedNotice.Message = "Needs a license key: Trial, Professional, Lifetime, or Organization.";
            LockedNotice.IsOpen = true;
        }
        DeviceText.Text = $"Device id {_license.Engine.DeviceId} · {_license.Engine.DeviceName} · {AppInfo.VersionText}";
        Refresh();
    }

    private void License_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var engine = _license.Engine;
        var plan = engine.Plan;
        PlanText.Text = plan.Title();
        var (fill, border, strong) = plan switch
        {
            LicensePlan.Trial => ("CpButterFill", "CpSandBorder", "CpButterStrong"),
            LicensePlan.Professional => ("CpSageFill", "CpSageBorder", "CpSageStrong"),
            LicensePlan.Lifetime => ("CpRoseFill", "CpSandBorder", "CpRoseStrong"),
            LicensePlan.Organization => ("CpSkyFill", "CpSandBorder", "CpSkyStrong"),
            _ => ("CpSandFill", "CpSandBorder", "CpSandStrong"),
        };
        PlanBadge.Background = ThemeBrushes.Get(fill);
        PlanBadge.BorderBrush = ThemeBrushes.Get(border);
        PlanText.Foreground = ThemeBrushes.Get(strong);
        PlanSubtitle.Text = engine.Source switch
        {
            LicenseSource.Developer => $"{AppInfo.Name} · Developer build",
            LicenseSource.Key => engine.Payload?.Licensee is { Length: > 0 } who ? $"Licensed to {who}" : $"{AppInfo.Name} {plan.Title()}",
            _ => plan == LicensePlan.Starter ? "Free · drawing, courses and PDF with limits" : AppInfo.Name,
        };

        var p = engine.Payload;
        Stat1.Text = p is null ? $"Projects: up to {FreeLimits.Projects} at {FreeLimits.ScalesLabel}" : $"Key {LicenseKey.Masked(p.Key)}";
        Stat2.Text = p is null ? $"Courses: up to {FreeLimits.Courses} per project"
            : p.ExpiresAt is { } end ? $"{(engine.DaysLeft is int d && d > 0 ? $"{d} days left · " : "")}ends {end.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : p.UpdatesMajor is int major ? $"Lifetime · covers {AppInfo.Name} {major}.x" : "Lifetime · no end date";
        Stat3.Text = p is null ? "No fieldwork, contours or live basemap" : $"Checked {p.IssuedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)} · next refresh {p.RefreshAfter.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture)}";
        Stat4.Text = p is null ? "" : LicenseKey.ReferenceOf(p.Key) ?? "";
        SeatActions.Visibility = engine.UsesKeys ? Visibility.Visible : Visibility.Collapsed;
        ReleaseButton.IsEnabled = p is not null && !engine.Busy;
        CheckButton.IsEnabled = !engine.Busy;
        Busy.IsActive = engine.Busy;
        ActivateButton.IsEnabled = !engine.Busy && LicenseKey.IsComplete(LicenseKey.Format(KeyBox.Text));
        DevicesText.Text = engine.Info is { DevicesUsed: int used, MaxDevices: int max }
            ? $"{used} of {max} device{(max == 1 ? "" : "s")} in use" + (engine.Info.IsPerDevice && engine.Info.KeyPosition is int pos ? $" · key {pos} of {engine.Info.KeysTotal}" : "")
            : "";
        KeyCard.Visibility = engine.UsesKeys ? Visibility.Visible : Visibility.Collapsed;
        if (engine.Message is { Length: > 0 } message)
        {
            MessageBar.Severity = engine.IsPro && engine.Validity is { IsActive: true } ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            MessageBar.Message = message;
            MessageBar.IsOpen = true;
        }
        foreach (var child in PlansPanel.Children.OfType<Border>())
            if (child.Tag is LicensePlan tilePlan) child.BorderThickness = new Thickness(tilePlan == plan ? 2 : 1);
    }

    private void BuildPlans()
    {
        void Tile(LicensePlan plan, string price, string note)
        {
            var tile = new Border
            {
                Tag = plan, CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
                Background = ThemeBrushes.Get("CpPaper2"),
                BorderBrush = ThemeBrushes.Get("CpLine"), BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = plan.Title(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = price, Opacity = 0.7 } } },
                        new TextBlock { Text = note, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            PlansPanel.Children.Add(tile);
        }
        Tile(LicensePlan.Starter, "Free", $"{FreeLimits.Projects} projects at 1:4000 or 1:10000, up to {FreeLimits.Courses} courses; no fieldwork, contours, or live basemap");
        Tile(LicensePlan.Trial, "Limited time", $"Every feature, to try {AppInfo.Name} out");
        Tile(LicensePlan.Professional, "Paid per period", "Every feature, for one mapper or course setter");
        Tile(LicensePlan.Lifetime, "One-time payment", "Every feature, no end date (covers one major version)");
        Tile(LicensePlan.Organization, "Paid per period", "Teams, communities, clubs, and federations · one key for several devices");
    }

    private void BuildFeatures()
    {
        foreach (var f in ProFeatureExtensions.Highlights)
        {
            FeaturesPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new FontIcon { Glyph = f.Glyph(), FontSize = 14, Foreground = ThemeBrushes.Get("CpSageStrong"), Width = 24 },
                    new TextBlock { Text = f.Title(), FontSize = 13, TextWrapping = TextWrapping.Wrap },
                },
            });
        }
    }

    private void KeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_formatting) return;
        _formatting = true;
        var formatted = LicenseKey.Format(KeyBox.Text);
        if (formatted != KeyBox.Text) { KeyBox.Text = formatted; KeyBox.SelectionStart = formatted.Length; }
        _formatting = false;
        var complete = LicenseKey.IsComplete(formatted);
        ActivateButton.IsEnabled = complete && !_license.Engine.Busy;
        KeyHint.Text = !complete ? ""
            : LicenseKey.PlatformOf(formatted) is { } platform && platform != LicenseKey.Current ? LicenseKey.WrongPlatformMessage(platform)
            : LicenseKey.PlanOf(formatted) is { } plan ? $"{plan.Title()} key · {LicenseKey.Current.Label()}" : "Unknown key format.";
    }

    private async void KeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ActivateButton.IsEnabled) await ActivateAsync();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e) => await ActivateAsync();

    private async Task ActivateAsync()
    {
        MessageBar.IsOpen = false;
        await _license.Engine.ActivateAsync(KeyBox.Text);
        if (_license.IsPro) KeyBox.Text = "";
        Refresh();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        MessageBar.IsOpen = false;
        await _license.Engine.RefreshAsync();
        Refresh();
    }

    private async void Release_Click(object sender, RoutedEventArgs e)
    {
        var perDevice = _license.Engine.Info?.IsPerDevice == true;
        var confirm = new ContentDialog
        {
            Title = "Release this PC?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = perDevice
                    ? "This key is bound to this PC. Releasing it frees the seat, but the key itself can never be used again; you will need a new key from your license certificate for another device."
                    : "This PC goes back to Starter and its seat is freed, so the key can be activated on another device.",
            },
            PrimaryButtonText = "Release", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = RequestedTheme,
        };
        Hide();
        if (await confirm.ShowAsync() == ContentDialogResult.Primary) await _license.Engine.DeactivateAsync();
        await _license.ShowAsync(null);
    }

    private async void Terms_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        var terms = new ContentDialog
        {
            Title = $"{AppInfo.Name} License Terms",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                    Text = $"{AppInfo.Name} is licensed, not sold. A license key from PT. Jendela Cakra Digital unlocks the plan it was issued for, on the platform it was issued for (Windows keys start with OC7), on up to the number of devices in your license certificate.\n\n"
                           + "• Trial, Professional and Organization licenses run until their end date; Lifetime licenses cover one major version and its updates.\n"
                           + "• A device can be released from the key at any time; a per-device key becomes unusable after its device is released.\n"
                           + "• The app checks the license with the License Manager about weekly and needs to go online at least every 30 days.\n"
                           + "• Your map projects are yours. On Starter everything still opens and exports to PDF.\n"
                           + "• The license key may not be shared, resold or used to circumvent these terms.\n\n"
                           + "Support: PT. Jendela Cakra Digital · https://jcdigital.co.id",
                },
            },
            CloseButtonText = "Close", XamlRoot = XamlRoot, RequestedTheme = RequestedTheme,
        };
        Hide();
        await terms.ShowAsync();
        await _license.ShowAsync(_feature);
    }
}

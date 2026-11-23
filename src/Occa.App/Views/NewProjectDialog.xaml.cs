using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Occa.App.License;
using Occa.Core.Geometry;
using Occa.Core.License;
using Occa.Core.Project;
using Occa.Core.Sheet;

namespace Occa.App.Views;

/// <summary>
/// New project (FR-1.1 – FR-1.3 as on Mac): name, scale, A4 orientation and a start location. The project opens in
/// Select Area mode with the frame centred on that location; the UTM zone follows the location (FR-1.2). Starter
/// offers only its scales; the others are listed but open the License screen.
/// </summary>
public sealed partial class NewProjectDialog : ContentDialog
{
    private readonly UserSettings _settings;

    public NewProjectDialog(UserSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        bool pro = AppLicense.Shared.IsPro;
        foreach (var scale in MapPresets.Scales)
            ScaleBox.Items.Add(new ComboBoxItem { Content = $"1:{scale}" + (pro || FreeLimits.Scales.Contains(scale) ? "" : "  · license key"), Tag = scale });
        int index = MapPresets.Scales.ToList().IndexOf(settings.LastScale);
        ScaleBox.SelectedIndex = index < 0 ? MapPresets.Scales.ToList().IndexOf(4000) : index;
        OrientationBox.SelectedIndex = settings.LastLandscape ? 1 : 0;
        LatBox.Text = settings.LastLat.ToString("0.#####", CultureInfo.InvariantCulture);
        LonBox.Text = settings.LastLon.ToString("0.#####", CultureInfo.InvariantCulture);
        Validate();
    }

    public int Scale => ScaleBox.SelectedItem is ComboBoxItem { Tag: int s } ? s : 4000;
    public bool Landscape => OrientationBox.SelectedIndex == 1;
    public string ProjectName => NameBox.Text.Trim();
    public LatLon? Location => Parse(LatBox.Text) is double lat && Parse(LonBox.Text) is double lon && Math.Abs(lat) <= 84 && Math.Abs(lon) <= 180 ? new LatLon(lat, lon) : null;

    private static double? Parse(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void ScaleBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Validate();
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var standard = MapStandardExtensions.ForScale(Scale);
        StandardText.Text = $"{standard.Label()} symbols · {ContourSettings_DefaultInterval(Scale)} m contours · A4 covers about {Math.Round(new SheetSpec { ScaleDenominator = Scale }.GroundWidthM)} × {Math.Round(new SheetSpec { ScaleDenominator = Scale }.GroundHeightM)} m";
        bool allowed = FreeLimits.AllowsScale(AppLicense.Shared.IsPro, Scale);
        LimitText.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        LimitText.Text = $"Starter offers {FreeLimits.ScalesLabel}; other scales need a license key.";
        ZoneText.Text = Location is { } ll ? $"UTM {UtmZone.Containing(ll).Label} (EPSG:{UtmZone.Containing(ll).Epsg})" : "Enter a valid latitude and longitude.";
        IsPrimaryButtonEnabled = ProjectName.Length > 0 && Location is not null;
    }

    private static double ContourSettings_DefaultInterval(int scale) => Core.Contour.ContourSettings.DefaultInterval(scale);

    /// <summary>The project to create (area not locked yet); null when the scale needs a license key (the License screen opened).</summary>
    public Project? Build()
    {
        if (!AppLicense.Shared.Require(ProFeature.AllScales, overLimit: !FreeLimits.AllowsScale(AppLicense.Shared.IsPro, Scale))) return null;
        if (Location is not { } ll) return null;
        var zone = UtmZone.Containing(ll);
        var project = new Project { Name = ProjectName, CrsEpsg = zone.Epsg, AreaLocked = false };
        project.Sheet.ScaleDenominator = Scale;
        project.Sheet.Orientation = Landscape ? SheetOrientation.Landscape : SheetOrientation.Portrait;
        project.FrameOrigin = MapFrame.Centered(zone.Project(ll), project.Sheet).SnappedToStep(project.SnapStepM).Origin;
        _settings.LastScale = Scale;
        _settings.LastLandscape = Landscape;
        _settings.LastLat = ll.Lat;
        _settings.LastLon = ll.Lon;
        try { _settings.Save(); } catch (Exception) { /* remembering the last choice is a convenience only */ }
        return project;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Occa.Core.Contour;
using Occa.Core.Sheet;

namespace Occa.App.Views;

/// <summary>
/// Contours side panel (M4, FR-8): elevation data source (DEM file or Terrain Tiles), contour settings and tools.
/// The settings are the project's own <see cref="ContourSettings"/> instance; the map view regenerates and saves on
/// <see cref="SettingsChanged"/> and performs the data actions the panel requests.
/// </summary>
public sealed partial class ContourPanel : UserControl
{
    public event EventHandler? SettingsChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler? OpenDemRequested;
    public event EventHandler? DownloadTerrainRequested;
    public event EventHandler? RemoveDemRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? ConvertRequested;

    private ContourSettings _settings = new();
    private SheetSpec _sheet = new();
    // True until Bind(): XAML raises ValueChanged while the controls are still being created (a slider's Minimum coerces its
    // Value), and the handler must not touch sibling controls then.
    private bool _updating = true;

    public ContourSettings Settings => _settings;

    public ContourPanel()
    {
        InitializeComponent();
    }

    public void Bind(ContourSettings settings, SheetSpec sheet, bool canDownload)
    {
        _settings = settings;
        _sheet = sheet;
        _updating = true;
        EnabledSwitch.IsOn = settings.IsEnabled;
        IntervalBox.Value = settings.IntervalM;
        IndexEveryBox.Value = settings.IndexEvery;
        BaseOffsetBox.Value = settings.BaseOffsetM;
        MinLengthBox.Value = settings.MinLengthMM;
        SmoothingSlider.Value = settings.Smoothing;
        OpacitySlider.Value = settings.Opacity * 100;
        FormLinesCheck.IsChecked = settings.ShowFormLines;
        ExportCheck.IsChecked = settings.IncludeInExport;
        DownloadButton.IsEnabled = canDownload;
        HintText.Text = ContourSettings.IntervalHint(settings.IntervalM, sheet.MapStandard) ?? "";
        _updating = false;
    }

    /// <summary>Describes the loaded elevation data (null = none).</summary>
    public void ShowDem(DemGrid? dem, int importedLines)
    {
        if (dem is null && importedLines == 0)
        {
            DemText.Text = "No elevation data yet. Open a DEM file or download terrain tiles for the map area.";
            RemoveDemButton.IsEnabled = false;
            ExportButton.IsEnabled = ConvertButton.IsEnabled = false;
            ResultText.Text = "";
            return;
        }
        if (dem is not null)
        {
            var range = dem.Range;
            var relief = range is { } r ? $", {r.Min:0}–{r.Max:0} m" : "";
            DemText.Text = $"{dem.Head.Source} · {dem.Cols} × {dem.Rows} cells of {dem.CellSize:0.#} m{relief}";
        }
        else DemText.Text = $"Imported contour lines: {importedLines}";
        RemoveDemButton.IsEnabled = true;
        ExportButton.IsEnabled = ConvertButton.IsEnabled = true;
    }

    public void ShowResult(int lineCount, string? warning)
    {
        ResultText.Text = lineCount == 0 ? "" : $"{lineCount} contour lines";
        WarningText.Text = warning ?? "";
    }

    public void SetBusy(bool busy, string? text = null)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Visibility = busy && text is not null ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? "";
        DownloadButton.IsEnabled = !busy;
    }

    private void Settings_Changed(object sender, object e)
    {
        if (_updating || EnabledSwitch is null || IntervalBox is null) return;
        var s = _settings;
        s.IsEnabled = EnabledSwitch.IsOn;
        if (!double.IsNaN(IntervalBox.Value)) s.IntervalM = Math.Clamp(IntervalBox.Value, ContourSettings.IntervalRangeM.Min, ContourSettings.IntervalRangeM.Max);
        if (!double.IsNaN(IndexEveryBox.Value)) s.IndexEvery = (int)Math.Clamp(IndexEveryBox.Value, 2, 10);
        if (!double.IsNaN(BaseOffsetBox.Value)) s.BaseOffsetM = BaseOffsetBox.Value;
        if (!double.IsNaN(MinLengthBox.Value)) s.MinLengthMM = Math.Max(0, MinLengthBox.Value);
        s.Smoothing = (int)Math.Round(SmoothingSlider.Value);
        s.Opacity = Math.Clamp(OpacitySlider.Value / 100, ContourSettings.OpacityRange.Min, ContourSettings.OpacityRange.Max);
        s.ShowFormLines = FormLinesCheck.IsChecked == true;
        s.IncludeInExport = ExportCheck.IsChecked == true;
        HintText.Text = ContourSettings.IntervalHint(s.IntervalM, _sheet.MapStandard) ?? "";
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenDem_Click(object sender, RoutedEventArgs e) => OpenDemRequested?.Invoke(this, EventArgs.Empty);
    private void Download_Click(object sender, RoutedEventArgs e) => DownloadTerrainRequested?.Invoke(this, EventArgs.Empty);
    private void RemoveDem_Click(object sender, RoutedEventArgs e) => RemoveDemRequested?.Invoke(this, EventArgs.Empty);
    private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
    private void Convert_Click(object sender, RoutedEventArgs e) => ConvertRequested?.Invoke(this, EventArgs.Empty);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}

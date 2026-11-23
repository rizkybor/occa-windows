using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Occa.Core.Export;
using Windows.Storage.Pickers;

namespace Occa.App.Views;

/// <summary>Export choices (FR-W10): format, resolution, file name and location (default Documents\OCCA\Exports).</summary>
public sealed partial class ExportDialog : ContentDialog
{
    public enum Kind { Pdf, Png, Tiff, Jpeg, Omap, Dxf }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OCCA", "Exports");

    private readonly nint _hwnd;

    public ExportDialog(nint windowHandle, string baseName, bool gridAvailable)
    {
        InitializeComponent();
        _hwnd = windowHandle;
        FileNameBox.Text = baseName;
        FolderText.Text = DefaultFolder;
        GridCheck.IsEnabled = gridAvailable;
        GridCheck.IsChecked = gridAvailable;
        FormatButtons.SelectionChanged += (_, _) => DpiRow.Visibility = SelectedKind is Kind.Png or Kind.Tiff or Kind.Jpeg ? Visibility.Visible : Visibility.Collapsed;
        DpiRow.Visibility = Visibility.Collapsed;
    }

    public Kind SelectedKind => (Kind)Math.Max(FormatButtons.SelectedIndex, 0);
    public int Dpi => DpiBox.SelectedIndex switch { 1 => 600, 2 => 900, _ => 300 };
    public string FileName => string.Join("_", FileNameBox.Text.Trim().Split(Path.GetInvalidFileNameChars()));
    public string Folder => FolderText.Text;
    public bool IncludeGrid => GridCheck.IsChecked == true;
    public RasterFormat RasterFormat => SelectedKind switch { Kind.Tiff => RasterFormat.Tiff, Kind.Jpeg => RasterFormat.Jpeg, _ => RasterFormat.Png };

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) FolderText.Text = folder.Path;
    }
}

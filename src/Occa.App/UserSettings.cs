using System.Text.Json;

namespace Occa.App;

/// <summary>
/// Light / Dark / System appearance (default System), saved per user like <c>AppearanceMode</c> on Mac. Stored in
/// <c>%LOCALAPPDATA%\OCCA\settings.json</c>, which works the same for the MSIX and the MSI install.
/// </summary>
public enum AppearanceMode { System, Light, Dark }

public static class AppearanceModeExtensions
{
    /// <summary>The appearance button cycles on each click: Light → Dark → System → Light.</summary>
    public static AppearanceMode Next(this AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => AppearanceMode.Dark,
        AppearanceMode.Dark => AppearanceMode.System,
        _ => AppearanceMode.Light,
    };

    public static string Label(this AppearanceMode mode) => mode.ToString();

    /// <summary>Segoe Fluent Icons glyph: half-filled circle (system), sun (light), moon (dark).</summary>
    public static string Glyph(this AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => "",
        AppearanceMode.Dark => "",
        _ => "",
    };
}

public sealed class UserSettings
{
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OCCA");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Options) ?? new UserSettings();
        }
        catch (Exception)
        {
            // A damaged settings file falls back to defaults; it is rewritten on the next save.
        }
        return new UserSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}

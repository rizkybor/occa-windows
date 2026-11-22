using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Basemap;

namespace Occa.App;

/// <summary>
/// Settings shipped next to the executable (<c>appsettings.json</c>), overridden by <c>appsettings.local.json</c>
/// (git-ignored, for a developer's own keys) and then by environment variables. Secrets such as the Esri API key are
/// never committed: the CI build injects the key from a repository secret into the packaged <c>appsettings.json</c>.
/// </summary>
public sealed class AppSettings
{
    public EsriSettings Esri { get; set; } = new();
    public LicenseSettings License { get; set; } = new();

    public sealed class EsriSettings
    {
        public string? ApiKey { get; set; }
        public string? TileTemplate { get; set; }
    }

    public sealed class LicenseSettings
    {
        public string? ApiBaseUrl { get; set; }
    }

    /// <summary>The effective Esri key, or null when none is configured (the basemap is then unavailable).</summary>
    [JsonIgnore]
    public string? EsriApiKey => EsriWorldImagery.ResolveApiKey(Esri.ApiKey);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        foreach (var name in new[] { "appsettings.json", "appsettings.local.json" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) continue;
            var layer = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            if (layer is null) continue;
            if (!string.IsNullOrWhiteSpace(layer.Esri.ApiKey)) settings.Esri.ApiKey = layer.Esri.ApiKey;
            if (!string.IsNullOrWhiteSpace(layer.Esri.TileTemplate)) settings.Esri.TileTemplate = layer.Esri.TileTemplate;
            if (!string.IsNullOrWhiteSpace(layer.License.ApiBaseUrl)) settings.License.ApiBaseUrl = layer.License.ApiBaseUrl;
        }
        return settings;
    }
}

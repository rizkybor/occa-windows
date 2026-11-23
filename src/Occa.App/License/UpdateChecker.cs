using Occa.Core.License;

namespace Occa.App.License;

/// <summary>
/// Update check against HRIS (<c>releases/latest?version=&amp;platform=Windows&amp;key=</c>, PRD §7), at most once a day.
/// Store installs update through the Store and only show "What's new"; MSI installs get the download link from HRIS.
/// Critical releases are flagged so the shell can insist.
/// </summary>
public sealed class UpdateChecker
{
    public sealed record Result(UpdateCheck Check, bool IsPackaged)
    {
        public ReleaseInfo? Update => Check.Update;
        public bool Available => Check.UpdateAvailable && Check.Update is not null;
        public bool Required => Check.Required;
        /// <summary>Where "Update" leads: the Store for MSIX installs, the HRIS download for MSI installs.</summary>
        public Uri? Target => IsPackaged ? new Uri("ms-windows-store://pdp/?productid=" + StoreProductId) : Check.Update?.DownloadUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null;
        public string Summary => Update is null ? "" : $"{AppInfoName} {Update.Version}" + (Update.Title is { Length: > 0 } t ? $" · {t}" : "") + (Update.IsCritical ? " · critical update" : "");
        public string Notes => Update?.Changes is { Count: > 0 } c ? string.Join("\n", c.Select(x => $"• {x.Text}")) : "";
        private static string AppInfoName => Core.AppInfo.Name;
    }

    /// <summary>Microsoft Store product id (set after the Store listing exists; until then the Store link opens the search page).</summary>
    public static string StoreProductId { get; set; } = "";

    private readonly ILicenseApi _api;
    private readonly string _version;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public UpdateChecker(ILicenseApi api, string version)
    {
        _api = api;
        _version = version;
    }

    /// <summary>Null when nothing new, when the check ran recently, or when the server cannot be reached.</summary>
    public async Task<Result?> CheckAsync(string? key, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastCheck < TimeSpan.FromHours(24)) return null;
        _lastCheck = DateTimeOffset.UtcNow;
        try
        {
            var check = await _api.LatestReleaseAsync(_version, key);
            return check.UpdateAvailable && check.Update is not null ? new Result(check, WindowsDevice.IsPackaged) : null;
        }
        catch (LicenseApiException)
        {
            return null;
        }
    }
}

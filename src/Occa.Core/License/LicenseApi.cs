using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Occa.Core.License;

/// <summary>Error from the License Manager: a server answer with an error code, or no answer (offline).</summary>
public sealed class LicenseApiException : Exception
{
    /// <summary>The server's <c>error</c> code, or <c>http_&lt;status&gt;</c>; null for a network failure.</summary>
    public string? Code { get; }
    public bool IsNetwork => Code is null;

    public LicenseApiException(string? code, string message) : base(message) => Code = code;

    public static LicenseApiException Network(string message) => new(null, message);
}

/// <summary>Devices used / allowed and key details, from the last activation or refresh (not in the token).</summary>
public sealed record LicenseInfo
{
    [JsonPropertyName("max_devices")] public int? MaxDevices { get; init; }
    [JsonPropertyName("devices_used")] public int? DevicesUsed { get; init; }
    [JsonPropertyName("key_mode")] public string? KeyMode { get; init; }
    [JsonPropertyName("key_position")] public int? KeyPosition { get; init; }
    [JsonPropertyName("keys_total")] public int? KeysTotal { get; init; }
    [JsonPropertyName("licensee")] public string? Licensee { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore] public bool IsPerDevice => KeyMode == "per_device";
}

public sealed record TokenResponse([property: JsonPropertyName("token")] string Token, [property: JsonPropertyName("license")] LicenseInfo? License);

/// <summary>One published release (HRIS Versions page) as returned by <c>releases/latest</c>.</summary>
public sealed record ReleaseInfo
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("build")] public string? Build { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("released_at")] public string? ReleasedAt { get; init; }
    [JsonPropertyName("is_critical")] public bool IsCritical { get; init; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; init; }
    [JsonPropertyName("changes")] public List<ReleaseChange>? Changes { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public sealed record ReleaseChange([property: JsonPropertyName("type")] string? Type, [property: JsonPropertyName("area")] string? Area, [property: JsonPropertyName("text")] string? Text);
}

/// <summary>Answer of <c>GET releases/latest</c> (the update check, PRD §7).</summary>
public sealed record UpdateCheck
{
    [JsonPropertyName("latest")] public ReleaseInfo? Latest { get; init; }
    /// <summary>Newest release this license may install, when newer than the running version; else null.</summary>
    [JsonPropertyName("update")] public ReleaseInfo? Update { get; init; }
    [JsonPropertyName("update_available")] public bool UpdateAvailable { get; init; }
    /// <summary>Version of a newer major the license does not cover (a paid upgrade), else null.</summary>
    [JsonPropertyName("upgrade_available")] public string? UpgradeAvailable { get; init; }
    /// <summary>A critical release is newer than the running version: the app should update.</summary>
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("updates_major")] public int? UpdatesMajor { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The License Manager calls the app makes (so the engine can be tested with a fake).</summary>
public interface ILicenseApi
{
    Task<TokenResponse> ActivateAsync(string key, string deviceId, string deviceName, string appVersion, CancellationToken ct = default);
    Task<TokenResponse> RefreshAsync(string key, string deviceId, string appVersion, CancellationToken ct = default);
    Task<TokenResponse> RestoreAsync(string deviceId, string deviceName, string appVersion, CancellationToken ct = default);
    Task<TokenResponse> RebindAsync(string key, string oldDeviceId, string deviceId, string deviceName, string appVersion, CancellationToken ct = default);
    Task DeactivateAsync(string key, string deviceId, CancellationToken ct = default);
    Task<UpdateCheck> LatestReleaseAsync(string version, string? key, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for the License Manager public API, scoped to this product: <c>/api/licenses/occa/{activate,refresh,
/// restore,rebind,deactivate}</c> and <c>releases/latest</c>. Requests send <c>platform: "Windows"</c> (the OS name, like
/// "macOS" from the Mac). Error answers are <c>{error, message}</c>.
/// </summary>
public sealed class LicenseApi : ILicenseApi
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public LicenseApi(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        // Accepts the server root (https://api.jcdigital.co.id) or the product path (…/api/licenses/occa) from older settings.
        var root = (baseUrl ?? LicenseToken.ProductionApi).TrimEnd('/');
        int i = root.IndexOf("/api/licenses", StringComparison.OrdinalIgnoreCase);
        if (i > 0) root = root[..i];
        _base = new Uri(root + "/");
    }

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error, [property: JsonPropertyName("message")] string? Message);

    private async Task<T> PostAsync<T>(string path, Dictionary<string, string> body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, $"api/licenses/{LicenseToken.ProductCode}/{path}"));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd($"OCCA-Windows/{AppInfo.VersionText.Replace("Version ", "")}");
            request.Content = JsonContent.Create(body);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw LicenseApiException.Network(e is HttpRequestException ? e.Message : "The license server did not answer in time.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            int status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
            {
                ErrorResponse? e = null;
                try { e = JsonSerializer.Deserialize<ErrorResponse>(text, Options); } catch (JsonException) { }
                throw new LicenseApiException(e?.Error ?? $"http_{status}", e?.Message ?? $"The license server answered {status}.");
            }
            try { return JsonSerializer.Deserialize<T>(text, Options) ?? throw new LicenseApiException("bad_response", "The license server's answer was empty."); }
            catch (JsonException) { throw new LicenseApiException("bad_response", "The license server's answer couldn't be read."); }
        }
    }

    private static string Platform => LicenseKey.Current.OsName();

    public Task<TokenResponse> ActivateAsync(string key, string deviceId, string deviceName, string appVersion, CancellationToken ct = default)
        => PostAsync<TokenResponse>("activate", new() { ["key"] = key, ["device_id"] = deviceId, ["device_name"] = deviceName, ["platform"] = Platform, ["app_version"] = appVersion }, ct);

    public Task<TokenResponse> RefreshAsync(string key, string deviceId, string appVersion, CancellationToken ct = default)
        => PostAsync<TokenResponse>("refresh", new() { ["key"] = key, ["device_id"] = deviceId, ["platform"] = Platform, ["app_version"] = appVersion }, ct);

    public Task<TokenResponse> RestoreAsync(string deviceId, string deviceName, string appVersion, CancellationToken ct = default)
        => PostAsync<TokenResponse>("restore", new() { ["device_id"] = deviceId, ["device_name"] = deviceName, ["platform"] = Platform, ["app_version"] = appVersion }, ct);

    public Task<TokenResponse> RebindAsync(string key, string oldDeviceId, string deviceId, string deviceName, string appVersion, CancellationToken ct = default)
        => PostAsync<TokenResponse>("rebind", new() { ["key"] = key, ["old_device_id"] = oldDeviceId, ["device_id"] = deviceId, ["device_name"] = deviceName, ["platform"] = Platform, ["app_version"] = appVersion }, ct);

    public async Task DeactivateAsync(string key, string deviceId, CancellationToken ct = default)
        => await PostAsync<JsonElement>("deactivate", new() { ["key"] = key, ["device_id"] = deviceId }, ct);

    public async Task<UpdateCheck> LatestReleaseAsync(string version, string? key, CancellationToken ct = default)
    {
        var query = $"version={Uri.EscapeDataString(version)}&platform={Uri.EscapeDataString(Platform)}" + (key is null ? "" : $"&key={Uri.EscapeDataString(key)}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, $"api/licenses/{LicenseToken.ProductCode}/releases/latest?{query}"));
            request.Headers.Accept.ParseAdd("application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new LicenseApiException($"http_{(int)response.StatusCode}", $"The update server answered {(int)response.StatusCode}.");
            return JsonSerializer.Deserialize<UpdateCheck>(text, Options) ?? new UpdateCheck();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            throw LicenseApiException.Network(e.Message);
        }
    }

    // MARK: Messages

    /// <summary>Refresh answers that end this device's activation.</summary>
    public static readonly HashSet<string> SignOutCodes = new() { "inactive", "expired", "invalid_key", "not_activated", "wrong_platform", "key_replaced", "key_used" };
    /// <summary>Of those, the ones where the user needs a new key (the License screen opens).</summary>
    public static readonly HashSet<string> NewKeyCodes = new() { "key_replaced", "key_used" };

    /// <summary>A sentence for the License screen, like the Mac's <c>describe</c>.</summary>
    public static string Describe(Exception error)
    {
        if (error is not LicenseApiException e) return error.Message;
        if (e.IsNetwork) return $"Couldn't reach the license server: {e.Message}";
        bool generic = e.Message.StartsWith("The license server answered", StringComparison.Ordinal);
        return e.Code switch
        {
            "invalid_key" => "This license key doesn't exist. Check it and try again.",
            "inactive" => "This license key is switched off. Contact Jendela Cakra Digital.",
            "expired" => "This license key has expired.",
            "wrong_platform" => LicenseKey.WrongPlatformMessage(LicensePlatform.Mac).Replace("Mac license key", "Mac or iPad license key"),
            "device_limit" => "This key is already used on the maximum number of devices. Release one first.",
            "product_inactive" => $"License activation for {AppInfo.Name} is paused. Contact Jendela Cakra Digital.",
            "invalid_product" => $"This key isn't for {AppInfo.Name}.",
            "not_activated" => "This device isn't activated with this key any more. Enter the key again.",
            "invalid_request" => "Check the license key and try again.",
            "bad_token" => "The license server's answer couldn't be verified.",
            "key_replaced" => generic ? "This license key was replaced after a plan change. Enter the new key from your license certificate." : e.Message,
            "key_used" => generic ? "This license key was already used on a released device and can't be activated again. Enter a new key." : e.Message,
            "key_in_use" => generic ? "This license key is already activated on another device." : e.Message,
            _ => e.Message,
        };
    }
}

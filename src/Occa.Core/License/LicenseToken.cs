using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Occa.Core.License;

/// <summary>The signed token's contents (HRIS <c>docs/license-manager-api.md</c>, "Token format"); snake_case keys as sent.</summary>
public sealed record LicensePayload
{
    [JsonPropertyName("v")] public int V { get; init; }
    /// <summary>Product code in the License Manager (one back office for several apps); older tokens have none.</summary>
    [JsonPropertyName("product")] public string? Product { get; init; }
    [JsonPropertyName("lid")] public int Lid { get; init; }
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("plan")] public string Plan { get; init; } = "";
    /// <summary>"mac" | "ipad" | "windows": the platform the key was issued for; older tokens have none.</summary>
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    /// <summary>Lifetime: the major version the license covers (OCCA 1.x = 1); null = every version while it runs.</summary>
    [JsonPropertyName("updates_major")] public int? UpdatesMajor { get; init; }
    [JsonPropertyName("licensee")] public string? Licensee { get; init; }
    [JsonPropertyName("expires_at")] public string? ExpiresAtText { get; init; }
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("iat")] public long Iat { get; init; }
    [JsonPropertyName("refresh_after")] public long RefreshAfterSeconds { get; init; }
    [JsonPropertyName("offline_until")] public long OfflineUntilSeconds { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore] public bool IsLifetime => Type == "lifetime" || ExpiresAtText is null;
    [JsonIgnore] public LicensePlan PlanValue => LicensePlanExtensions.FromToken(Plan);
    [JsonIgnore] public DateTimeOffset? ExpiresAt => ExpiresAtText is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    [JsonIgnore] public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeSeconds(Iat);
    [JsonIgnore] public DateTimeOffset RefreshAfter => DateTimeOffset.FromUnixTimeSeconds(RefreshAfterSeconds);
    [JsonIgnore] public DateTimeOffset OfflineUntil => DateTimeOffset.FromUnixTimeSeconds(OfflineUntilSeconds);
}

/// <summary>
/// License tokens: <c>base64url(payload) + "." + base64url(Ed25519 signature over the payload text)</c>. Verified offline
/// with the back office's public key (FR-W12); the secret key is only in the back office's <c>.env</c>.
/// </summary>
public static class LicenseToken
{
    /// <summary>Ed25519 public key of the back office (base64), the same for every OCCA platform.</summary>
    public const string PublicKeyBase64 = "BQ071uZquqpGTjCLIIefOpQ7BbV1dco0jyO2oa1vqcc=";
    public const string ProductionApi = "https://api.jcdigital.co.id";
    /// <summary>This app's product code in the License Manager: tokens for other products are refused.</summary>
    public const string ProductCode = "occa";

    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    /// <summary>
    /// Check the token's signature and that it belongs to this device, this product and this platform. Returns the
    /// payload, or null for anything that does not check out.
    /// </summary>
    public static LicensePayload? Verify(string token, string deviceId, string? publicKeyBase64 = null, LicensePlatform platform = LicenseKey.Current)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        byte[] key, signature, json;
        try
        {
            key = Convert.FromBase64String(publicKeyBase64 ?? PublicKeyBase64);
            signature = FromBase64Url(parts[1]);
            json = FromBase64Url(parts[0]);
        }
        catch (FormatException) { return null; }
        if (!Ed25519.Verify(key, Encoding.ASCII.GetBytes(parts[0]), signature)) return null;
        LicensePayload? payload;
        try { payload = JsonSerializer.Deserialize<LicensePayload>(json, Options); }
        catch (JsonException) { return null; }
        if (payload is null || payload.DeviceId != deviceId) return null;
        if (payload.Product is not null && payload.Product != ProductCode) return null;
        if (payload.Platform is not null && payload.Platform != platform.RawValue()) return null;
        return payload;
    }

    /// <summary>base64url without padding (RFC 4648 §5).</summary>
    public static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        while (b.Length % 4 != 0) b += "=";
        return Convert.FromBase64String(b);
    }

    public static string ToBase64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Builds a signed token from a payload JSON text and an Ed25519 seed (tests and tooling; the back office signs real tokens).</summary>
    public static string Sign(string payloadJson, ReadOnlySpan<byte> seed)
    {
        var payload = ToBase64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signature = Ed25519.Sign(seed, Encoding.ASCII.GetBytes(payload));
        return payload + "." + ToBase64Url(signature);
    }
}

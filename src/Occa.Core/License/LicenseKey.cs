namespace Occa.Core.License;

/// <summary>OCCA's license plan, from a license key issued in the Jendela Cakra Digital back office (HRIS › License Manager).</summary>
public enum LicensePlan { Starter, Trial, Professional, Lifetime, Organization }

public static class LicensePlanExtensions
{
    /// <summary>Value of the token's <c>plan</c>; "pro" is the old name of Professional, "enterprise" of Lifetime.</summary>
    public static LicensePlan FromToken(string token) => token.ToLowerInvariant() switch
    {
        "trial" => LicensePlan.Trial,
        "lifetime" or "enterprise" => LicensePlan.Lifetime,
        "organization" or "organisation" => LicensePlan.Organization,
        "starter" or "free" => LicensePlan.Starter,
        _ => LicensePlan.Professional,
    };

    public static string Title(this LicensePlan p) => p switch
    {
        LicensePlan.Starter => "Starter",
        LicensePlan.Trial => "Trial",
        LicensePlan.Professional => "Professional",
        LicensePlan.Lifetime => "Lifetime",
        _ => "Organization",
    };

    public static string RawValue(this LicensePlan p) => p.ToString().ToLowerInvariant();

    /// <summary>Any key plan (Trial, Professional, Lifetime, Organization): every feature, no limits.</summary>
    public static bool IsPaid(this LicensePlan p) => p != LicensePlan.Starter;
}

/// <summary>The platform a license key was issued for.</summary>
public enum LicensePlatform { Mac, Ipad, Windows }

public static class LicensePlatformExtensions
{
    public static string Label(this LicensePlatform p) => p switch { LicensePlatform.Mac => "Mac", LicensePlatform.Ipad => "iPad", _ => "Windows" };
    /// <summary>Value of the token's <c>platform</c> and of the license in the back office.</summary>
    public static string RawValue(this LicensePlatform p) => p switch { LicensePlatform.Mac => "mac", LicensePlatform.Ipad => "ipad", _ => "windows" };
    /// <summary>The OS name sent in API requests (<c>platform</c>): "macOS", "iPadOS", "Windows".</summary>
    public static string OsName(this LicensePlatform p) => p switch { LicensePlatform.Mac => "macOS", LicensePlatform.Ipad => "iPadOS", _ => "Windows" };
    /// <summary>Platform digit of the key: 3 Mac, 5 iPad, 7 Windows (PRD §7).</summary>
    public static char Digit(this LicensePlatform p) => p switch { LicensePlatform.Mac => '3', LicensePlatform.Ipad => '5', _ => '7' };
}

/// <summary>
/// OCCA license keys: <c>OC{platform}{plan}-XXXX-XXXX-XXXX-{sequence}</c>, e.g. <c>OC72-7KQM-2XRT-9HZP-0001</c>.
/// Platform digit: 3 Mac, 5 iPad, 7 Windows; plan digit: 1 Trial, 2 Professional, 3 Lifetime, 4 Organization. A key
/// only activates on its own platform (FR-W11). The last group is the license's number within its platform (also the
/// certificate reference <c>LIC-OCCA-WINDOWS-0001</c>). Port of <c>LicenseKey</c> on Mac.
/// </summary>
public static class LicenseKey
{
    /// <summary>The platform this app runs on.</summary>
    public const LicensePlatform Current = LicensePlatform.Windows;

    public static readonly IReadOnlyDictionary<char, LicensePlatform> PlatformCodes = new Dictionary<char, LicensePlatform>
    {
        ['3'] = LicensePlatform.Mac, ['5'] = LicensePlatform.Ipad, ['7'] = LicensePlatform.Windows,
    };

    public static readonly IReadOnlyDictionary<char, LicensePlan> PlanDigits = new Dictionary<char, LicensePlan>
    {
        ['1'] = LicensePlan.Trial, ['2'] = LicensePlan.Professional, ['3'] = LicensePlan.Lifetime, ['4'] = LicensePlan.Organization,
    };

    /// <summary>Keys from the back office are always 5 groups of 4: 20 characters.</summary>
    public const int GroupLength = 4;
    public const int GroupCount = 5;
    public const int Length = GroupLength * GroupCount;

    /// <summary>Uppercase, drop spaces and stray characters, keep at most 20 characters, and group them 4-4-4-4-4 while typing or after a paste.</summary>
    public static string Format(string raw)
    {
        var chars = new string(raw.ToUpperInvariant().Where(c => c < 128 && char.IsLetterOrDigit(c)).Take(Length).ToArray());
        var groups = new List<string>();
        for (int i = 0; i < chars.Length; i += GroupLength) groups.Add(chars.Substring(i, Math.Min(GroupLength, chars.Length - i)));
        return string.Join("-", groups);
    }

    /// <summary>Exactly five groups of 4 characters.</summary>
    public static bool IsComplete(string key)
    {
        var g = key.Split('-');
        return g.Length == GroupCount && g.All(x => x.Length == GroupLength);
    }

    /// <summary>The two-digit code of a key (<c>OC72-…</c> → '7','2'), null when the key doesn't start with OC + two characters.</summary>
    private static (char Platform, char Plan)? Code(string key)
    {
        var first = key.Split('-')[0];
        if (first.Length != 4 || !first.StartsWith("OC", StringComparison.Ordinal)) return null;
        return (first[2], first[3]);
    }

    /// <summary>The platform a key was issued for (<c>OC3…</c> Mac, <c>OC5…</c> iPad, <c>OC7…</c> Windows).</summary>
    public static LicensePlatform? PlatformOf(string key) => Code(key) is { } c && PlatformCodes.TryGetValue(c.Platform, out var p) ? p : null;

    /// <summary>The plan a key was issued for (<c>OC72-…</c> Professional); null for unknown codes (iPad has no Trial).</summary>
    public static LicensePlan? PlanOf(string key)
    {
        if (Code(key) is not { } c || !PlatformCodes.TryGetValue(c.Platform, out var platform) || !PlanDigits.TryGetValue(c.Plan, out var plan)) return null;
        return platform == LicensePlatform.Ipad && plan == LicensePlan.Trial ? null : plan;
    }

    /// <summary>Certificate reference: <c>LIC-OCCA-WINDOWS-0001</c>.</summary>
    public static string? ReferenceOf(string key)
    {
        if (PlatformOf(key) is not { } platform || PlanOf(key) is null) return null;
        var last = key.Split('-')[^1];
        if (last.Length == 0 || !last.All(char.IsDigit)) return null;
        return $"LIC-OCCA-{platform.RawValue().ToUpperInvariant()}-{last}";
    }

    /// <summary>"OC72-7KQM-…" → "OC72-••••-••••-••••-0001": the key is shown masked on the License screen.</summary>
    public static string Masked(string key)
    {
        var g = key.Split('-');
        if (g.Length != GroupCount) return key;
        return $"{g[0]}-••••-••••-••••-{g[^1]}";
    }

    /// <summary>A Mac or iPad key on Windows.</summary>
    public static string WrongPlatformMessage(LicensePlatform keyPlatform)
        => $"This is a {keyPlatform.Label()} license key. On this {Current.Label()} PC, use your {Current.Label()} key (OC{Current.Digit()}…).";
}

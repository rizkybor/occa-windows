using System.Globalization;

namespace Occa.Core.License;

/// <summary>Where the engine keeps its few strings (token, last time seen, pending release). The app uses DPAPI (FR-W15).</summary>
public interface ILicenseStore
{
    string? Read(string item);
    void Write(string item, string value);
    void Delete(string item);
}

/// <summary>In-memory store (tests, developer builds).</summary>
public sealed class MemoryLicenseStore : ILicenseStore
{
    private readonly Dictionary<string, string> _items = new();
    public string? Read(string item) => _items.TryGetValue(item, out var v) ? v : null;
    public void Write(string item, string value) => _items[item] = value;
    public void Delete(string item) => _items.Remove(item);
}

public enum LicenseSource { None, Key, Developer }

/// <summary>
/// The license state machine, ported from the Mac's <c>LicenseManager</c> without the UI and storage: which plan applies
/// now, from a stored token verified offline, and the API flows (activate, weekly refresh, release, restore after a
/// reinstall). The app wraps it with DPAPI storage, the Windows device id, timers and the License screen.
/// Every method runs on the caller's thread; the app calls it from the UI thread.
/// </summary>
public sealed class LicenseEngine
{
    public const string TokenItem = "occa.license.token";
    public const string LastSeenItem = "occa.license.lastSeen";
    /// <summary>"key|device id" of a release that couldn't reach the server (offline): sent again later, and no restore until then.</summary>
    public const string PendingReleaseItem = "occa.license.pendingRelease";

    private readonly ILicenseStore _store;
    private readonly ILicenseApi _api;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _publicKey;
    private LicensePlan? _fixedPlan;
    private bool _restoreTried;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public string AppVersion { get; }

    public LicensePlan Plan { get; private set; } = LicensePlan.Starter;
    public LicenseSource Source { get; private set; } = LicenseSource.None;
    public LicensePayload? Payload { get; private set; }
    /// <summary>Details of the last activation/refresh answer (devices used, key mode); not in the token.</summary>
    public LicenseInfo? Info { get; private set; }
    /// <summary>Message for the License screen (activation failed, key switched off, trial ended…).</summary>
    public string? Message { get; set; }
    public bool Busy { get; private set; }
    /// <summary>The last applied validity (null without a token).</summary>
    public LicenseValidity? Validity { get; private set; }
    /// <summary>The License screen should open (a dead key needs replacing).</summary>
    public bool NeedsNewKey { get; private set; }

    /// <summary>Raised after any change of plan, payload, message or busy state.</summary>
    public event EventHandler? Changed;

    public LicenseEngine(ILicenseStore store, ILicenseApi api, string deviceId, string deviceName, string appVersion,
                         Func<DateTimeOffset>? clock = null, string? publicKeyBase64 = null)
    {
        _store = store;
        _api = api;
        DeviceId = deviceId;
        DeviceName = deviceName;
        AppVersion = appVersion;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _publicKey = publicKeyBase64;
    }

    /// <summary>Any key plan (Trial, Professional, Lifetime, Organization): every feature, no limits.</summary>
    public bool IsPro => Plan.IsPaid();
    public bool UsesKeys => _fixedPlan is null;

    /// <summary>Major version of this app (1 for 1.4.2).</summary>
    public int AppMajor => int.TryParse(AppVersion.Split('.')[0], out var m) ? m : 1;

    /// <summary>Developer builds: a fixed plan instead of keys (null = use keys).</summary>
    public void SetFixedPlan(LicensePlan? plan)
    {
        _fixedPlan = plan;
        if (plan is { } p)
        {
            Plan = p;
            Source = p.IsPaid() ? LicenseSource.Developer : LicenseSource.None;
            Payload = null;
            Validity = null;
        }
        else Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the stored token (if any) for this device.</summary>
    public void Load()
    {
        if (!UsesKeys) return;
        var token = _store.Read(TokenItem);
        var p = token is null ? null : LicenseToken.Verify(token, DeviceId, _publicKey);
        if (p is null) { Plan = LicensePlan.Starter; Source = LicenseSource.None; Payload = null; Validity = null; }
        else Apply(p);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>When the app becomes active again (e.g. after a sleep past the end date): re-apply the stored token.</summary>
    public void Recheck()
    {
        if (!UsesKeys || Payload is not { } p) return;
        Apply(p);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The next moment the plan can change by itself (end date or offline limit), for a timer while the app runs.</summary>
    public DateTimeOffset? NextChange => Payload is { } p && Plan.IsPaid() ? LicenseValidity.NextChange(p.ExpiresAt, p.OfflineUntil, _clock()) : null;

    /// <summary>Latest time OCCA saw on this device: the clock turned back can't stretch the offline time or a trial.</summary>
    private DateTimeOffset? LastSeen(DateTimeOffset now)
    {
        DateTimeOffset? last = long.TryParse(_store.Read(LastSeenItem), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
        if (last is null || now > last) _store.Write(LastSeenItem, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return last;
    }

    /// <summary>Applies a verified token: its plan only while every date check passes, otherwise Starter with a message.</summary>
    private void Apply(LicensePayload p)
    {
        Payload = p;
        var now = _clock();
        var v = LicenseValidity.Evaluate(p.ExpiresAt, p.OfflineUntil, p.IssuedAt, LastSeen(now), p.UpdatesMajor, AppMajor, now);
        Validity = v;
        var tokenPlan = p.PlanValue;
        Plan = v.IsActive ? tokenPlan : LicensePlan.Starter;
        Source = Plan.IsPaid() ? LicenseSource.Key : LicenseSource.None;
        string End() => p.ExpiresAt is { } e ? e.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : "";
        if (v.MajorNotCovered && p.UpdatesMajor is int major)
            Message = $"Your {tokenPlan.Title()} license covers {AppInfo.Name} {major}.x. Upgrade it to use {AppInfo.Name} {AppMajor}, or keep using {AppInfo.Name} {major}.";
        else if (v.Expired && tokenPlan == LicensePlan.Trial) Message = $"Your {AppInfo.Name} trial ended on {End()}. You're on Starter now; projects stay as they are.";
        else if (v.Expired) Message = $"Your OCCA license expired on {End()}.";
        else if (v.ClockTurnedBack) Message = "This PC's date is earlier than before. Set the correct date and time, then connect to the internet so OCCA can check your license.";
        else if (v.OfflineTooLong) Message = "Connect to the internet once so OCCA can check your license.";
    }

    private void SetBusy(bool busy) { Busy = busy; Changed?.Invoke(this, EventArgs.Empty); }

    /// <summary>Verify a token answer, store it and apply it.</summary>
    private void Receive(TokenResponse r)
    {
        var p = LicenseToken.Verify(r.Token, DeviceId, _publicKey) ?? throw new LicenseApiException("bad_token", "The license server's answer couldn't be verified.");
        _store.Write(TokenItem, r.Token);
        _store.Write(LastSeenItem, _clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        if (r.License is not null) Info = r.License;
        NeedsNewKey = false;
        Apply(p);
    }

    /// <summary>
    /// At launch: finish a release sent offline, take back this device's license after a reinstall (once per launch),
    /// and refresh the token when it's due (online only).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        Recheck();
        if (!UsesKeys) return;
        await SendPendingReleaseAsync(ct);
        if (!_restoreTried) _restoreTried = await RestoreAsync(userInitiated: false, ct);
        await RefreshIfDueAsync(ct);
    }

    /// <summary>Activate a license key on this device (FR-W11: a Mac/iPad key gets the wrong-platform message).</summary>
    public async Task ActivateAsync(string rawKey, CancellationToken ct = default)
    {
        var key = LicenseKey.Format(rawKey);
        if (key.Length == 0) return;
        if (LicenseKey.PlatformOf(key) is { } platform && platform != LicenseKey.Current)
        {
            Message = LicenseKey.WrongPlatformMessage(platform);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        SetBusy(true);
        try
        {
            // A release sent offline goes first, so it can't end this new activation later.
            await SendPendingReleaseAsync(ct);
            Receive(await _api.ActivateAsync(key, DeviceId, DeviceName, AppVersion, ct));
            if (IsPro) Message = $"{AppInfo.Name} {Plan.Title()} is active on this PC.";
        }
        catch (LicenseApiException e) { Message = LicenseApi.Describe(e); }
        finally { SetBusy(false); }
    }

    /// <summary>Ask the back office again (weekly, or from "Check now"). Network trouble keeps the current token. Without a token, "Check now" tries restore.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!UsesKeys) return;
        if (Payload?.Key is not { } key) { await RestoreAsync(userInitiated: true, ct); return; }
        SetBusy(true);
        try
        {
            Receive(await _api.RefreshAsync(key, DeviceId, AppVersion, ct));
        }
        catch (LicenseApiException e) when (e.Code is { } code && LicenseApi.SignOutCodes.Contains(code))
        {
            // Switched off, expired, removed or replaced in the back office: back to Starter.
            _store.Delete(TokenItem);
            Payload = null; Plan = LicensePlan.Starter; Source = LicenseSource.None; Info = null; Validity = null;
            Message = LicenseApi.Describe(e);
            NeedsNewKey = LicenseApi.NewKeyCodes.Contains(code);
        }
        catch (LicenseApiException e)
        {
            if (Payload is { } p && p.OfflineUntil <= _clock()) Message = LicenseApi.Describe(e);
        }
        finally { SetBusy(false); }
    }

    public async Task RefreshIfDueAsync(CancellationToken ct = default)
    {
        if (!UsesKeys || Payload is not { } p || _clock() < p.RefreshAfter) return;
        await RefreshAsync(ct);
    }

    /// <summary>Release this device's seat (e.g. before moving to another PC) and go back to Starter. Offline, the release is sent again later.</summary>
    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        if (Payload?.Key is not { } key) return;
        SetBusy(true);
        try
        {
            _store.Write(PendingReleaseItem, $"{key}|{DeviceId}");
            _store.Delete(TokenItem);
            Payload = null; Plan = LicensePlan.Starter; Source = LicenseSource.None; Info = null; Validity = null;
            await SendPendingReleaseAsync(ct);
            Message = "This PC was released and is back on Starter. The key can now be used on another device.";
        }
        finally { SetBusy(false); }
    }

    private async Task SendPendingReleaseAsync(CancellationToken ct)
    {
        if (_store.Read(PendingReleaseItem) is not { } pending) return;
        var parts = pending.Split('|', 2);
        if (parts.Length != 2) { _store.Delete(PendingReleaseItem); return; }
        // Any answer from the server (deactivate is always 200, also for unknown keys) ends it; network trouble keeps it.
        try
        {
            await _api.DeactivateAsync(parts[0], parts[1], ct);
            _store.Delete(PendingReleaseItem);
        }
        catch (LicenseApiException e) when (!e.IsNetwork) { _store.Delete(PendingReleaseItem); }
        catch (LicenseApiException) { }
    }

    /// <summary>
    /// After a reinstall (no valid token): if the back office still has an activation for this device id, take the
    /// license back (no key needed, never a new seat). Returns false when the server couldn't be reached.
    /// </summary>
    public async Task<bool> RestoreAsync(bool userInitiated, CancellationToken ct = default)
    {
        if (!UsesKeys || Payload is not null || _store.Read(PendingReleaseItem) is not null) return true;
        if (userInitiated) SetBusy(true);
        try
        {
            Receive(await _api.RestoreAsync(DeviceId, DeviceName, AppVersion, ct));
            if (userInitiated && IsPro) Message = "License restored on this PC.";
        }
        catch (LicenseApiException e) when (e.Code == "not_activated")
        {
            if (userInitiated) Message = "This PC has no active license. Enter your license key to activate it.";
        }
        catch (LicenseApiException e) when (e.IsNetwork)
        {
            if (userInitiated) Message = LicenseApi.Describe(e);
            return false;
        }
        catch (LicenseApiException e)
        {
            if (userInitiated) Message = LicenseApi.Describe(e);
        }
        finally { if (userInitiated) SetBusy(false); else Changed?.Invoke(this, EventArgs.Empty); }
        return true;
    }

    /// <summary>Starter limits: true while under the limit or on a paid plan.</summary>
    public bool Allows(ProFeature feature) => IsPro;
    public bool Allows(ProFeature feature, bool overLimit) => IsPro || !overLimit;

    /// <summary>"STARTER" / "TRIAL" for the corner watermark; null on paid plans.</summary>
    public string? WatermarkLabel => Plan switch { LicensePlan.Trial => "TRIAL", LicensePlan.Starter => "STARTER", _ => null };

    /// <summary>Second line of the watermark: what to do (Starter) or when the trial ends.</summary>
    public string WatermarkDetail
    {
        get
        {
            if (Plan == LicensePlan.Trial && Payload?.ExpiresAt is { } end) return $"Trial ends {end.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.";
            return Plan == LicensePlan.Trial ? "Activate a license key to keep every feature." : "Activate a license key to unlock every feature.";
        }
    }

    /// <summary>Days left on a term license (0 when ended); null for Lifetime or no license.</summary>
    public int? DaysLeft => Payload?.ExpiresAt is { } end ? Math.Max(0, (int)Math.Ceiling((end - _clock()).TotalDays)) : null;
}

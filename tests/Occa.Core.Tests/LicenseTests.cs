using System.Security.Cryptography;
using System.Text;
using Occa.Core.License;

namespace Occa.Core.Tests;

/// <summary>Ed25519 against the RFC 8032 §7.1 test vectors, plus a round trip of our own signer.</summary>
public class Ed25519Tests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void Rfc8032Test1EmptyMessage()
    {
        var seed = Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var pub = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        Assert.Equal(pub, Ed25519.PublicKeyFromSeed(seed));
        Assert.Equal(sig, Ed25519.Sign(seed, Array.Empty<byte>()));
        Assert.True(Ed25519.Verify(pub, Array.Empty<byte>(), sig));
        Assert.False(Ed25519.Verify(pub, new byte[] { 1 }, sig));
        sig[0] ^= 1;
        Assert.False(Ed25519.Verify(pub, Array.Empty<byte>(), sig));
    }

    [Fact]
    public void Rfc8032Test2OneByte()
    {
        var seed = Hex("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        var pub = Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var sig = Hex("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        var msg = new byte[] { 0x72 };
        Assert.Equal(pub, Ed25519.PublicKeyFromSeed(seed));
        Assert.Equal(sig, Ed25519.Sign(seed, msg));
        Assert.True(Ed25519.Verify(pub, msg, sig));
    }

    [Fact]
    public void Rfc8032Test1024Bytes()
    {
        // RFC 8032 §7.1 TEST 1024: a long message (the digest of the message is checked indirectly via the signature).
        var seed = Hex("f5e5767cf153319517630f226876b86c8160cc583bc013744c6bf255f5cc0ee5");
        var pub = Hex("278117fc144c72340f67d0f2316e8386ceffbf2b2428c9c51fef7c597f1d426e");
        var sig = Hex("0aab4c900501b3e24d7cdf4663326a3a87df5e4843b2cbdb67cbf6e460fec350aa5371b1508f9f4528ecea23c436d94b5e8fcd4f681e30a6ac00a9704a188a03");
        var msg = Hex("08b8b2b733424243760fe426a4b54908632110a66c2f6591eabd3345e3e4eb98fa6e264bf09efe12ee50f8f54e9f77b1e355f6c50544e23fb1433ddf73be84d879de7c0046dc4996d9e773f4bc9efe5738829adb26c81b37c93a1b270b20329d658675fc6ea534e0810a4432826bf58c941efb65d57a338bbd2e26640f89ffbc1a858efcb8550ee3a5e1998bd177e93a7363c344fe6b199ee5d02e82d522c4feba15452f80288a821a579116ec6dad2b3b310da903401aa62100ab5d1a36553e06203b33890cc9b832f79ef80560ccb9a39ce767967ed628c6ad573cb116dbefefd75499da96bd68a8a97b928a8bbc103b6621fcde2beca1231d206be6cd9ec7aff6f6c94fcd7204ed3455c68c83f4a41da4af2b74ef5c53f1d8ac70bdcb7ed185ce81bd84359d44254d95629e9855a94a7c1958d1f8ada5d0532ed8a5aa3fb2d17ba70eb6248e594e1a2297acbbb39d502f1a8c6eb6f1ce22b3de1a1f40cc24554119a831a9aad6079cad88425de6bde1a9187ebb6092cf67bf2b13fd65f27088d78b7e883c8759d2c4f5c65adb7553878ad575f9fad878e80a0c9ba63bcbcc2732e69485bbc9c90bfbd62481d9089beccf80cfe2df16a2cf65bd92dd597b0707e0917af48bbb75fed413d238f5555a7a569d80c3414a8d0859dc65a46128bab27af87a71314f318c782b23ebfe808b82b0ce26401d2e22f04d83d1255dc51addd3b75a2b1ae0784504df543af8969be3ea7082ff7fc9888c144da2af58429ec96031dbcad3dad9af0dcbaaaf268cb8fcffead94f3c7ca495e056a9b47acdb751fb73e666c6c655ade8297297d07ad1ba5e43f1bca32301651339e22904cc8c42f58c30c04aafdb038dda0847dd988dcda6f3bfd15c4b4c4525004aa06eeff8ca61783aacec57fb3d1f92b0fe2fd1a85f6724517b65e614ad6808d6f6ee34dff7310fdc82aebfd904b01e1dc54b2927094b2db68d6f903b68401adebf5a7e08d78ff4ef5d63653a65040cf9bfd4aca7984a74d37145986780fc0b16ac451649de6188a7dbdf191f64b5fc5e2ab47b57f7f7276cd419c17a3ca8e1b939ae49e488acba6b965610b5480109c8b17b80e1b7b750dfc7598d5d5011fd2dcc5600a32ef5b52a1ecc820e308aa342721aac0943bf6686b64b2579376504ccc493d97e6aed3fb0f9cd71a43dd497f01f17c0e2cb3797aa2a2f256656168e6c496afc5fb93246f6b1116398a346f1a641f3b041e989f7914f90cc2c7fff357876e506b50d334ba77c225bc307ba537152f3f1610e4eafe595f6d9d90d11faa933a15ef1369546868a7f3a45a96768d40fd9d03412c091c6315cf4fde7cb68606937380db2eaaa707b4c4185c32eddcdd306705e4dc1ffc872eeee475a64dfac86aba41c0618983f8741c5ef68d3a101e8a3b8cac60c905c15fc910840b94c00a0b9d0");
        Assert.Equal(pub, Ed25519.PublicKeyFromSeed(seed));
        Assert.True(Ed25519.Verify(pub, msg, sig));
        Assert.Equal(sig, Ed25519.Sign(seed, msg));
    }

    [Fact]
    public void BackOfficePublicKeyIsAValidPoint()
    {
        var key = Convert.FromBase64String(LicenseToken.PublicKeyBase64);
        Assert.Equal(32, key.Length);
        // A random signature must not verify, but the key itself must decode (no exception, a clean false).
        Assert.False(Ed25519.Verify(key, "x"u8, new byte[64]));
    }
}

/// <summary>Token verification, validity, device id and key rules (ports of LicenseValidityTests and LicenseDeviceIDTests).</summary>
public class LicenseTokenTests
{
    internal static readonly byte[] Seed = SHA256.HashData("occa-test-seed"u8);
    internal static readonly string PublicKey = Convert.ToBase64String(Ed25519.PublicKeyFromSeed(Seed));
    internal const string Device = "5B0E7C1A-3F55-4C8B-9E1D-2A6F0D9B7C44";

    internal static string Token(string device = Device, string plan = "professional", string? expires = "2027-05-09T00:00:00Z", long iat = 1_794_239_819,
                                 string platform = "windows", string product = "occa", int? updatesMajor = null, long? offlineUntil = null)
    {
        var exp = expires is null ? "null" : $"\"{expires}\"";
        var major = updatesMajor is null ? "null" : updatesMajor.ToString();
        var json = $"{{\"v\":1,\"product\":\"{product}\",\"lid\":12,\"key\":\"OC72-7KQM-2XRT-9HZP-0001\",\"plan\":\"{plan}\",\"platform\":\"{platform}\",\"type\":\"{(expires is null ? "lifetime" : "term")}\",\"updates_major\":{major},\"licensee\":\"Jane Orienteer\",\"expires_at\":{exp},\"device_id\":\"{device}\",\"iat\":{iat},\"refresh_after\":{iat + 604800},\"offline_until\":{offlineUntil ?? iat + 2592000}}}";
        return LicenseToken.Sign(json, Seed);
    }

    [Fact]
    public void VerifiesOnlyMatchingDeviceProductAndPlatform()
    {
        var p = LicenseToken.Verify(Token(), Device, PublicKey);
        Assert.NotNull(p);
        Assert.Equal("OC72-7KQM-2XRT-9HZP-0001", p!.Key);
        Assert.Equal(LicensePlan.Professional, p.PlanValue);
        Assert.Equal(new DateTimeOffset(2027, 5, 9, 0, 0, 0, TimeSpan.Zero), p.ExpiresAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_794_239_819 + 604800), p.RefreshAfter);
        Assert.Null(LicenseToken.Verify(Token(), "OTHER-DEVICE", PublicKey));
        Assert.Null(LicenseToken.Verify(Token(product: "other"), Device, PublicKey));
        Assert.Null(LicenseToken.Verify(Token(platform: "mac"), Device, PublicKey));
        Assert.Null(LicenseToken.Verify(Token(), Device));   // the production key did not sign it
        // Editing the payload (plan professional → lifetime) while keeping the signature must fail.
        var original = Token().Split('.');
        var edited = Encoding.UTF8.GetString(LicenseToken.FromBase64Url(original[0])).Replace("\"plan\":\"professional\"", "\"plan\":\"lifetime\"");
        var tampered = LicenseToken.ToBase64Url(Encoding.UTF8.GetBytes(edited)) + "." + original[1];
        Assert.Null(LicenseToken.Verify(tampered, Device, PublicKey));
        var payload = original[0];
        Assert.Null(LicenseToken.Verify(payload, Device, PublicKey));
        Assert.Null(LicenseToken.Verify(payload + ".%%%", Device, PublicKey));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static LicenseValidity Check(TimeSpan? expiresIn, TimeSpan? offlineIn = null, TimeSpan? issuedAgo = null, TimeSpan? lastSeenAgo = null,
                                         bool noLastSeen = false, int? updatesMajor = null, int appMajor = 1)
        => LicenseValidity.Evaluate(expiresIn is { } e ? Now + e : null, Now + (offlineIn ?? 20 * Day), Now - (issuedAgo ?? Day),
                                    noLastSeen ? null : Now - (lastSeenAgo ?? TimeSpan.FromHours(1)), updatesMajor, appMajor, Now);

    [Fact]
    public void TermLicenseActiveUntilItsEndDate()
    {
        Assert.True(Check(14 * Day).IsActive);
        Assert.True(Check(TimeSpan.FromSeconds(60)).IsActive);
        var ended = Check(TimeSpan.Zero);
        Assert.True(ended.Expired);
        Assert.False(ended.IsActive);
        Assert.False(Check(-Day).IsActive);
    }

    [Fact]
    public void LifetimeNeverExpiresAndOfflineLimit()
    {
        Assert.True(Check(null).IsActive);
        Assert.False(Check(null).Expired);
        Assert.True(Check(null, offlineIn: Day).IsActive);
        var tooLong = Check(null, offlineIn: TimeSpan.FromSeconds(-1));
        Assert.True(tooLong.OfflineTooLong);
        Assert.False(tooLong.IsActive);
    }

    [Fact]
    public void ClockTurnedBack()
    {
        Assert.True(Check(null, issuedAgo: -2 * Day).ClockTurnedBack);
        Assert.True(Check(null, lastSeenAgo: -3 * Day).ClockTurnedBack);
        Assert.False(Check(null, issuedAgo: TimeSpan.FromHours(-1), lastSeenAgo: TimeSpan.FromHours(-1)).ClockTurnedBack);
        Assert.False(Check(null, lastSeenAgo: -3 * Day).IsActive);
        Assert.False(Check(null, noLastSeen: true).ClockTurnedBack);
    }

    [Fact]
    public void LifetimeCoversItsMajorVersionOnlyAndNextChange()
    {
        Assert.True(Check(null, updatesMajor: 1, appMajor: 1).IsActive);
        Assert.True(Check(null, updatesMajor: 2, appMajor: 1).IsActive);
        Assert.True(Check(null, updatesMajor: 1, appMajor: 2).MajorNotCovered);
        Assert.True(Check(30 * Day, updatesMajor: null, appMajor: 5).IsActive);
        var end = Now + 2 * Day; var offline = Now + 5 * Day;
        Assert.Equal(end, LicenseValidity.NextChange(end, offline, Now));
        Assert.Equal(offline, LicenseValidity.NextChange(null, offline, Now));
        Assert.Null(LicenseValidity.NextChange(Now - TimeSpan.FromSeconds(1), Now - TimeSpan.FromSeconds(1), Now));
    }

    private const string Hardware = "8F3A2C1E-5B7D-4E9F-A0B1-C2D3E4F5A6B7";

    [Fact]
    public void DeviceIdStableSaltedAndUuidShaped()
    {
        var id = LicenseDeviceId.Derive(Hardware)!;
        Assert.Equal(id, LicenseDeviceId.Derive(Hardware.ToLowerInvariant()));
        Assert.Equal(id, LicenseDeviceId.Derive($" {Hardware}\n"));
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, id.ToUpperInvariant());
        Assert.NotEqual(Hardware, id);
        Assert.Equal('5', id[14]);
        Assert.Contains(id[19], "89AB");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("occa-device:" + Hardware)));
        var hex = id.Replace("-", "");
        Assert.Equal(digest[..12], hex[..12]);
        Assert.Equal(digest[13..16], hex[13..16]);
        Assert.Equal(digest[18..32], hex[18..32]);
        Assert.NotEqual(id, LicenseDeviceId.Derive("00000000-0000-0000-0000-000000000001"));
        Assert.Null(LicenseDeviceId.Derive("  "));
    }

    [Fact]
    public void KeysFormatAndPlatform()
    {
        Assert.Equal("OC72-7KQM-2XRT-9HZP-0001", LicenseKey.Format(" oc72 7kqm-2xrt 9hzp0001extra "));
        Assert.True(LicenseKey.IsComplete("OC72-7KQM-2XRT-9HZP-0001"));
        Assert.False(LicenseKey.IsComplete("OC72-7KQM"));
        Assert.Equal(LicensePlatform.Windows, LicenseKey.PlatformOf("OC72-7KQM-2XRT-9HZP-0001"));
        Assert.Equal(LicensePlatform.Mac, LicenseKey.PlatformOf("OC32-7KQM-2XRT-9HZP-0001"));
        Assert.Equal(LicensePlan.Professional, LicenseKey.PlanOf("OC72-7KQM-2XRT-9HZP-0001"));
        Assert.Equal(LicensePlan.Trial, LicenseKey.PlanOf("OC71-7KQM-2XRT-9HZP-0001"));
        Assert.Null(LicenseKey.PlanOf("OC51-7KQM-2XRT-9HZP-0001"));
        Assert.Null(LicenseKey.PlatformOf("XX72-7KQM-2XRT-9HZP-0001"));
        Assert.Equal("LIC-OCCA-WINDOWS-0001", LicenseKey.ReferenceOf("OC73-7KQM-2XRT-9HZP-0001"));
        Assert.Equal("OC72-••••-••••-••••-0001", LicenseKey.Masked("OC72-7KQM-2XRT-9HZP-0001"));
        Assert.Contains("OC7", LicenseKey.WrongPlatformMessage(LicensePlatform.Mac));
        Assert.Equal(new[] { 4000, 10000 }, FreeLimits.PickableScales(isPro: false));
        Assert.Equal(new[] { 4000, 7500, 10000 }, FreeLimits.PickableScales(isPro: false, current: 7500));
        Assert.True(FreeLimits.PickableScales(isPro: true).Count > 10);
    }
}

/// <summary>The license engine with a fake back office and in-memory storage: activate, refresh, sign-out codes, release, restore.</summary>
public class LicenseEngineTests
{
    private sealed class FakeApi : ILicenseApi
    {
        public Func<string, string, TokenResponse>? OnActivate;
        public Func<string, string, TokenResponse>? OnRefresh;
        public Func<string, TokenResponse>? OnRestore;
        public List<string> Calls = new();
        public bool Offline;
        public int Deactivations;

        private T Guard<T>(string name, Func<T> f)
        {
            Calls.Add(name);
            if (Offline) throw LicenseApiException.Network("offline");
            return f();
        }

        public Task<TokenResponse> ActivateAsync(string key, string deviceId, string deviceName, string appVersion, CancellationToken ct = default)
            => Task.FromResult(Guard("activate", () => OnActivate?.Invoke(key, deviceId) ?? throw new LicenseApiException("invalid_key", "no")));
        public Task<TokenResponse> RefreshAsync(string key, string deviceId, string appVersion, CancellationToken ct = default)
            => Task.FromResult(Guard("refresh", () => OnRefresh?.Invoke(key, deviceId) ?? throw new LicenseApiException("not_activated", "no")));
        public Task<TokenResponse> RestoreAsync(string deviceId, string deviceName, string appVersion, CancellationToken ct = default)
            => Task.FromResult(Guard("restore", () => OnRestore?.Invoke(deviceId) ?? throw new LicenseApiException("not_activated", "This device has no license activation to restore.")));
        public Task<TokenResponse> RebindAsync(string key, string oldDeviceId, string deviceId, string deviceName, string appVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeactivateAsync(string key, string deviceId, CancellationToken ct = default) { Guard("deactivate", () => { Deactivations++; return 0; }); return Task.CompletedTask; }
        public Task<UpdateCheck> LatestReleaseAsync(string version, string? key, CancellationToken ct = default) => Task.FromResult(new UpdateCheck());
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_794_239_819 + 3600);

    private static (LicenseEngine Engine, FakeApi Api, MemoryLicenseStore Store) Make(DateTimeOffset? now = null)
    {
        var api = new FakeApi();
        var store = new MemoryLicenseStore();
        var engine = new LicenseEngine(store, api, LicenseTokenTests.Device, "Test PC", "1.0.0", () => now ?? Now, LicenseTokenTests.PublicKey);
        return (engine, api, store);
    }

    [Fact]
    public async Task ActivateStoresTokenAndUnlocks()
    {
        var (engine, api, store) = Make();
        api.OnActivate = (key, device) => new TokenResponse(LicenseTokenTests.Token(device), new LicenseInfo { MaxDevices = 2, DevicesUsed = 1, KeyMode = "per_device" });
        engine.Load();
        Assert.Equal(LicensePlan.Starter, engine.Plan);
        await engine.ActivateAsync("oc72 7kqm 2xrt 9hzp 0001");
        Assert.True(engine.IsPro);
        Assert.Equal(LicensePlan.Professional, engine.Plan);
        Assert.Equal(LicenseSource.Key, engine.Source);
        Assert.NotNull(store.Read(LicenseEngine.TokenItem));
        Assert.Equal(2, engine.Info?.MaxDevices);
        Assert.Contains("Professional is active", engine.Message);
        // A new engine (relaunch) reads the stored token offline.
        var again = new LicenseEngine(store, api, LicenseTokenTests.Device, "Test PC", "1.0.0", () => Now, LicenseTokenTests.PublicKey);
        again.Load();
        Assert.True(again.IsPro);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_794_239_819 + 2592000), again.NextChange?.ToUniversalTime());
    }

    [Fact]
    public async Task WrongPlatformKeyNeverReachesTheServer()
    {
        var (engine, api, _) = Make();
        await engine.ActivateAsync("OC32-7KQM-2XRT-9HZP-0001");
        Assert.Empty(api.Calls);
        Assert.Contains("Mac license key", engine.Message);
        Assert.False(engine.IsPro);
    }

    [Fact]
    public async Task RefreshSignOutCodesFallBackToStarter()
    {
        var (engine, api, store) = Make();
        store.Write(LicenseEngine.TokenItem, LicenseTokenTests.Token());
        engine.Load();
        Assert.True(engine.IsPro);
        api.OnRefresh = (_, _) => throw new LicenseApiException("key_replaced", "The license server answered 403.");
        await engine.RefreshAsync();
        Assert.False(engine.IsPro);
        Assert.Null(store.Read(LicenseEngine.TokenItem));
        Assert.True(engine.NeedsNewKey);
        Assert.Contains("replaced after a plan change", engine.Message);
    }

    [Fact]
    public async Task OfflineRefreshKeepsTheToken()
    {
        var (engine, api, store) = Make();
        store.Write(LicenseEngine.TokenItem, LicenseTokenTests.Token());
        engine.Load();
        api.Offline = true;
        await engine.RefreshAsync();
        Assert.True(engine.IsPro);
        Assert.NotNull(store.Read(LicenseEngine.TokenItem));
    }

    [Fact]
    public async Task RefreshIsDueAfterAWeek()
    {
        var later = DateTimeOffset.FromUnixTimeSeconds(1_794_239_819 + 8 * 86400);
        var (engine, api, store) = Make(later);
        store.Write(LicenseEngine.TokenItem, LicenseTokenTests.Token());
        api.OnRefresh = (_, device) => new TokenResponse(LicenseTokenTests.Token(device, iat: later.ToUnixTimeSeconds()), null);
        engine.Load();
        await engine.StartAsync();
        Assert.Contains("refresh", api.Calls);
        Assert.DoesNotContain("restore", api.Calls);
        Assert.Equal(later.ToUnixTimeSeconds(), engine.Payload!.Iat);
    }

    [Fact]
    public async Task ReleaseSendsDeactivateAndBlocksRestore()
    {
        var (engine, api, store) = Make();
        store.Write(LicenseEngine.TokenItem, LicenseTokenTests.Token());
        engine.Load();
        api.Offline = true;
        await engine.DeactivateAsync();
        Assert.False(engine.IsPro);
        Assert.NotNull(store.Read(LicenseEngine.PendingReleaseItem));
        // Offline: restore must not bring the license back while the release is pending.
        api.OnRestore = device => new TokenResponse(LicenseTokenTests.Token(device), null);
        await engine.StartAsync();
        Assert.False(engine.IsPro);
        // Online again: the pending release goes out, then restore says not_activated (the seat is gone).
        api.Offline = false;
        api.OnRestore = null;
        await engine.StartAsync();
        Assert.Equal(1, api.Deactivations);
        Assert.Null(store.Read(LicenseEngine.PendingReleaseItem));
        Assert.False(engine.IsPro);
    }

    [Fact]
    public async Task RestoreAfterReinstallTakesTheLicenseBack()
    {
        var (engine, api, _) = Make();
        api.OnRestore = device => new TokenResponse(LicenseTokenTests.Token(device, plan: "lifetime", expires: null), new LicenseInfo { MaxDevices = 1, DevicesUsed = 1 });
        engine.Load();
        await engine.StartAsync();
        Assert.Equal(LicensePlan.Lifetime, engine.Plan);
        Assert.Null(engine.DaysLeft);
        Assert.Null(engine.WatermarkLabel);
    }

    [Fact]
    public void ExpiredTrialShowsStarterWithMessage()
    {
        var (engine, _, store) = Make();
        store.Write(LicenseEngine.TokenItem, LicenseTokenTests.Token(plan: "trial", expires: "2026-01-01T00:00:00Z", offlineUntil: 1_900_000_000));
        engine.Load();
        Assert.Equal(LicensePlan.Starter, engine.Plan);
        Assert.Contains("trial ended", engine.Message);
        Assert.Equal("STARTER", engine.WatermarkLabel);
        Assert.True(engine.Validity?.Expired);
    }

    [Fact]
    public void DeveloperFixedPlan()
    {
        var (engine, _, _) = Make();
        engine.SetFixedPlan(LicensePlan.Lifetime);
        Assert.True(engine.IsPro);
        Assert.Equal(LicenseSource.Developer, engine.Source);
        Assert.False(engine.UsesKeys);
        engine.SetFixedPlan(null);
        Assert.False(engine.IsPro);
    }
}

namespace Occa.Core.License;

/// <summary>
/// Whether a verified license token unlocks the app right now, from its dates alone (the signature and device are
/// checked before). Trial, Professional and Organization licenses end at <c>ExpiresAt</c>; Lifetime has none. A token is
/// also only good until <c>OfflineUntil</c> (the server caps it at the end date) and not when the device clock was turned
/// back. The app re-checks this at launch, when it becomes active, and when the earliest of these moments arrives.
/// Port of <c>LicenseValidity</c> (CakraCore).
/// </summary>
public readonly record struct LicenseValidity(bool Expired, bool OfflineTooLong, bool ClockTurnedBack, bool MajorNotCovered)
{
    /// <summary>Every check passed: the license's plan applies.</summary>
    public bool IsActive => !Expired && !OfflineTooLong && !ClockTurnedBack && !MajorNotCovered;

    /// <summary>Clock corrections (time zone, network time) up to this much are fine.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromHours(6);

    public static LicenseValidity Evaluate(DateTimeOffset? expiresAt, DateTimeOffset offlineUntil, DateTimeOffset issuedAt, DateTimeOffset? lastSeen,
                                           int? updatesMajor, int appMajor, DateTimeOffset now)
    {
        bool turnedBack = now < issuedAt - ClockTolerance || (lastSeen is { } seen && now < seen - ClockTolerance);
        return new LicenseValidity(
            Expired: expiresAt is { } end && end <= now,
            OfflineTooLong: offlineUntil <= now,
            ClockTurnedBack: turnedBack,
            MajorNotCovered: updatesMajor is int major && appMajor > major);
    }

    /// <summary>The next moment the answer can change by itself (end date or offline limit), for a re-check while the app runs.</summary>
    public static DateTimeOffset? NextChange(DateTimeOffset? expiresAt, DateTimeOffset offlineUntil, DateTimeOffset now)
    {
        var candidates = new List<DateTimeOffset>();
        if (expiresAt is { } e && e > now) candidates.Add(e);
        if (offlineUntil > now) candidates.Add(offlineUntil);
        return candidates.Count == 0 ? null : candidates.Min();
    }
}

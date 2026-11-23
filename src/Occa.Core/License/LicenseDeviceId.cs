using System.Security.Cryptography;
using System.Text;

namespace Occa.Core.License;

/// <summary>
/// The device id OCCA sends to the License Manager, derived from a hardware identifier so it stays the same after the
/// app and its data are deleted and installed again (FR-W13). On Windows the identifier is the machine GUID
/// (<c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>); on a Mac it is the IOKit hardware UUID. The raw value
/// never leaves the PC: the id is SHA-256 of <c>"occa-device:" + value</c> (uppercased), its first 16 bytes formatted as a
/// UUID string (version 5 / RFC 4122 variant bits set, so it looks like any other device id). Same algorithm as
/// <c>LicenseDeviceID</c> on Mac.
/// </summary>
public static class LicenseDeviceId
{
    public const string Salt = "occa-device:";

    /// <summary>null for an empty or blank hardware identifier.</summary>
    public static string? Derive(string hardwareId)
    {
        var normalized = hardwareId.Trim().ToUpperInvariant();
        if (normalized.Length == 0) return null;
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + normalized)).AsSpan(0, 16).ToArray();
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        // Format the 16 bytes in RFC 4122 order (big-endian groups), like Foundation's UUID(uuid:) does.
        var hex = Convert.ToHexString(b);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}

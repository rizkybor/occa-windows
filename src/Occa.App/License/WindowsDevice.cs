using Microsoft.Win32;
using Occa.Core;
using Occa.Core.License;

namespace Occa.App.License;

/// <summary>
/// This PC's identity for the License Manager (FR-W13): the device id is a salted SHA-256 of the machine GUID
/// (<c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>, readable by every user, stable across reinstalls); the raw
/// GUID never leaves the PC. When the registry value cannot be read, a random id is kept in the DPAPI store instead.
/// </summary>
public static class WindowsDevice
{
    private const string DeviceItem = "occa.license.device";

    /// <summary>The License Manager device id derived from the machine GUID, or null if it cannot be read.</summary>
    public static string? HardwareDeviceId
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid") is string guid ? LicenseDeviceId.Derive(guid) : null;
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The id to use: hardware-derived, else a random id remembered in the store (so it survives relaunches).</summary>
    public static string ResolveDeviceId(ILicenseStore store)
    {
        if (HardwareDeviceId is { } hardware)
        {
            if (store.Read(DeviceItem) != hardware) store.Write(DeviceItem, hardware);
            return hardware;
        }
        if (store.Read(DeviceItem) is { } stored) return stored;
        var random = Guid.NewGuid().ToString().ToUpperInvariant();
        store.Write(DeviceItem, random);
        return random;
    }

    /// <summary>"DESKTOP-ABC123" as shown in the back office's device list.</summary>
    public static string DeviceName => Environment.MachineName;

    /// <summary>"0.1.6" from the assembly's informational version (CI-stamped).</summary>
    public static string AppVersion => AppInfo.VersionText.Replace("Version ", "");

    /// <summary>True when OCCA runs from an MSIX package (Microsoft Store / sideload); false for the MSI install.</summary>
    public static bool IsPackaged
    {
        get
        {
            try { return Windows.ApplicationModel.Package.Current is not null; }
            catch (InvalidOperationException) { return false; }
        }
    }
}

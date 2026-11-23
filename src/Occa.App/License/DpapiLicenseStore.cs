using System.Security.Cryptography;
using System.Text;
using Occa.Core.License;

namespace Occa.App.License;

/// <summary>
/// License items (token, last time seen, pending release, device id) encrypted with Windows DPAPI for the current user
/// (FR-W15) in <c>%LOCALAPPDATA%\OCCA\license\</c>. The same folder serves the MSIX and the MSI install, and it survives
/// uninstalling, so a reinstall finds the token again (restore is only the fallback).
/// </summary>
public sealed class DpapiLicenseStore : ILicenseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("occa-license-v1");
    public string Folder { get; }

    public DpapiLicenseStore(string? folder = null) => Folder = folder ?? Path.Combine(UserSettings.Folder, "license");

    private string PathOf(string item) => Path.Combine(Folder, string.Join("_", item.Split(Path.GetInvalidFileNameChars())) + ".bin");

    public string? Read(string item)
    {
        try
        {
            var path = PathOf(item);
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Another user's or a damaged item reads as absent; the next write replaces it.
            return null;
        }
    }

    public void Write(string item, string value)
    {
        Directory.CreateDirectory(Folder);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        Core.Project.ProjectPackage.WriteAtomic(PathOf(item), data);
    }

    public void Delete(string item)
    {
        var path = PathOf(item);
        if (File.Exists(path)) File.Delete(path);
    }
}

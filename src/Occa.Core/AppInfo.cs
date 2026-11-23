namespace Occa.Core;

/// <summary>Product names and credits, the same as <c>AppInfo</c> in CakraCore so every platform reads alike.</summary>
public static class AppInfo
{
    public const string Name = "OCCA";
    public const string FullName = "Orienteering Cakra CAD Application";
    public const string Tagline = "Draw, edit, and print ISOM/ISSprOM orienteering maps — on Windows, iPad and Mac.";
    public const string Developer = "Rizky Ajie Kurniawan";
    public const string Supporter = "PT. Jendela Cakra Digital";
    public const string SupporterUrl = "https://jcdigital.co.id";
    public const string Country = "Indonesia";
    public const int Year = 2026;

    /// <summary>"Version 0.1.6" from the assembly's informational version (set by Directory.Build.props / CI stamping).</summary>
    public static string VersionText
    {
        get
        {
            var asm = typeof(AppInfo).Assembly;
            var informational = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm))?.InformationalVersion;
            var version = informational?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
            return $"Version {version}";
        }
    }
}

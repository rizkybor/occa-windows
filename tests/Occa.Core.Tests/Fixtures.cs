namespace Occa.Core.Tests;

internal static class Fixtures
{
    public static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", relative);

    public static byte[] Bytes(string relative) => File.ReadAllBytes(Path(relative));
}

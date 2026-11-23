using System.Globalization;
using Occa.Core.Geometry;
using Occa.Core.Sheet;

namespace Occa.Core.Export;

/// <summary>6-line world file (ESRI): A, D, B, E, C, F. C, F = centre of the upper-left pixel (as OCAD and OOM read it).</summary>
public readonly record struct WorldFile(double PixelSizeX, double RotationY, double RotationX, double PixelSizeY, double UpperLeftX, double UpperLeftY)
{
    /// <summary>World file for a north-up raster whose upper-left corner = the frame's upper-left corner.</summary>
    public static WorldFile For(MapFrame frame, double pixelSize)
        => new(pixelSize, 0, 0, -pixelSize, frame.Extent.MinX + pixelSize / 2, frame.Extent.MaxY - pixelSize / 2);

    public string Text => string.Join("\n", new[] { PixelSizeX, RotationY, RotationX, PixelSizeY, UpperLeftX, UpperLeftY }.Select(v => v.ToString("F10", CultureInfo.InvariantCulture))) + "\n";

    public static WorldFile? Parse(string text)
    {
        var values = text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (double?)v : null).ToList();
        if (values.Count != 6 || values.Any(v => v is null || !double.IsFinite(v.Value)) || values[0] == 0 || values[3] == 0) return null;
        return new WorldFile(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value, values[4]!.Value, values[5]!.Value);
    }

    /// <summary>World coordinate for a pixel position (column, row); (0,0) = centre of the upper-left pixel.</summary>
    public Point2D World(double col, double row) => new(PixelSizeX * col + RotationX * row + UpperLeftX, RotationY * col + PixelSizeY * row + UpperLeftY);
}

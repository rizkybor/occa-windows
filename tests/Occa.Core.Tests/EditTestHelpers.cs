using Occa.Core.Geometry;
using Occa.Core.Vector;

namespace Occa.Core.Tests;

internal static class E
{
    public static Point2D P(double x, double y) => new(x, y);
    public static List<BezierNode> Ring(params (double, double)[] pts) => pts.Select(p => new BezierNode(P(p.Item1, p.Item2))).ToList();
    public static List<BezierNode> Rect(double x0, double y0, double x1, double y1) => Ring((x0, y0), (x1, y0), (x1, y1), (x0, y1));
    public static List<BezierNode> Reversed(List<BezierNode> ring) => ring.AsEnumerable().Reverse().Select(n => n.Reversed()).ToList();

    public static Feature Area(List<BezierNode> nodes, string? code = "406.0", string color = "#3CB54A", string name = "Forest")
        => new() { Name = name, Kind = FeatureKind.Area, ColorHex = color, WidthMM = 0, Nodes = nodes, SymbolCode = code, SymbolName = code is null ? null : "Forest" };

    public static Feature Line(List<BezierNode> nodes, string? code = "506.0", string color = "#000000")
        => new() { Name = "Path", Kind = FeatureKind.Line, ColorHex = color, WidthMM = 0.25, Nodes = nodes, SymbolCode = code, SymbolName = code is null ? null : "Small footpath" };

    public static double PolygonArea(Feature f) => Math.Abs(ShapeCut.SignedArea(f.Flattened(0.0005)));
    public static double Filled(Feature f) => f.FilledArea(0.0005);

    public static List<Point2D> Points(Feature f) => f.Nodes.Select(n => n.Point).ToList();

    public static void AssertClose(Point2D? a, Point2D b, double accuracy = 1e-6)
    {
        Assert.NotNull(a);
        Assert.Equal(b.X, a!.Value.X, accuracy);
        Assert.Equal(b.Y, a.Value.Y, accuracy);
    }

    public static Feature Donut()
    {
        var f = Area(Rect(0, 0, 20, 20));
        f.Holes = new List<List<BezierNode>> { Rect(5, 5, 15, 15) };
        f.NormalizeHoles();
        return f;
    }
}

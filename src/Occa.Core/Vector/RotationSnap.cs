using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>Precision rotation (Shift): the angle snaps when a straight side becomes parallel/perpendicular to a nearby object's side (or grid north), else to multiples of a step.</summary>
public static class RotationSnap
{
    public readonly record struct Edge(double Bearing, Point2D A, Point2D B);
    public readonly record struct Result(double Degrees, Edge? Reference, bool IsStep);

    /// <summary>Direction of line a→b as a bearing 0–180 (line direction, not a vector).</summary>
    public static double Bearing(Point2D a, Point2D b)
    {
        double d = Math.Atan2(b.X - a.X, b.Y - a.Y) * 180 / Math.PI;
        double r = d % 180;
        return r < 0 ? r + 180 : r;
    }

    internal static double Difference(double a, double b, double period)
    {
        double d = (a - b) % period;
        if (d > period / 2) d -= period;
        if (d < -period / 2) d += period;
        return d;
    }

    public static Result Snap(double raw, double baseAngle, IReadOnlyList<Edge> edges, IReadOnlyList<Edge> references, bool includeNorth = true, double threshold = 4, double step = 15)
    {
        var own = edges.Count == 0 ? new List<double> { 0 } : edges.Select(e => e.Bearing).ToList();
        var refs = references.Select(r => (r.Bearing, (Edge?)r)).ToList();
        if (includeNorth) refs.Add((0, null));
        (double Degrees, double Gap, Edge? Ref)? best = null;
        foreach (var e in own)
        {
            double current = e + (raw - baseAngle);
            foreach (var (r, edge) in refs)
            {
                double d = Difference(r, current, 90);
                if (Math.Abs(d) > threshold) continue;
                if (best is null || Math.Abs(d) < best.Value.Gap - 1e-9) best = (raw + d, Math.Abs(d), edge);
            }
        }
        if (best is { } b) return new Result(Feature.NormalizedDegrees(b.Degrees), b.Ref, false);
        return new Result(Feature.NormalizedDegrees(Math.Round(raw / step) * step), null, true);
    }

    /// <summary>Straight sides (non-curved segments) at least minLength metres long.</summary>
    public static List<Edge> StraightEdges(this Feature f, double minLength)
    {
        if (f.Kind == FeatureKind.Point) return new List<Edge>();
        return f.RingFeatures.SelectMany(r => r.Segments).Where(s => !s.IsCurve && s.P0.DistanceTo(s.P3) >= minLength)
            .Select(s => new Edge(Bearing(s.P0, s.P3), s.P0, s.P3)).ToList();
    }
}

using System.Text;
using Occa.Core.Export;
using Occa.Core.Geometry;

namespace Occa.Core.Courses;

/// <summary>
/// IOF control description pictograms (columns C–H) as simple vector drawings in a unit box (0…1, y up). Full-width
/// rows (14.1, 15.x, 16.x) use a box 8 units wide and 1 unit high. One definition is used for the PDF, raster and the
/// on-screen preview, so they look the same. Port of <c>IOFPictogram</c>; shapes are simplified from the IOF 2024
/// specification so they stay legible in 5–7 mm cells.
/// </summary>
public static class IofPictogram
{
    public abstract record Shape
    {
        /// <summary>Polyline (not closed).</summary>
        public sealed record Line(IReadOnlyList<Point2D> Points) : Shape;
        /// <summary>Closed polygon; <c>Fill</c> = solid fill.</summary>
        public sealed record Poly(IReadOnlyList<Point2D> Points, bool Fill) : Shape;
        public sealed record Circle(Point2D Center, double R, bool Fill) : Shape;
        /// <summary>Arc from angle A0 to A1 (radians, counterclockwise).</summary>
        public sealed record Arc(Point2D Center, double R, double A0, double A1) : Shape;
        /// <summary>Small filled dots.</summary>
        public sealed record Dots(IReadOnlyList<Point2D> Points, double R) : Shape;
        /// <summary>Text centred in the box (column F dimensions), <c>Size</c> relative to the box height.</summary>
        public sealed record Text(string Value, Point2D At, double Size) : Shape;
        /// <summary>Dashed polyline; dash and gap lengths in box units.</summary>
        public sealed record Dashed(IReadOnlyList<Point2D> Points, double Dash, double Gap) : Shape;

        /// <summary>Stable text form (for comparisons in tests and caching).</summary>
        public string Describe()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string F(double v) => v.ToString("0.######", inv);
            string P(Point2D p) => $"({F(p.X)},{F(p.Y)})";
            string Ps(IReadOnlyList<Point2D> ps) => string.Join(" ", ps.Select(P));
            return this switch
            {
                Line l => $"line {Ps(l.Points)}",
                Poly p => $"poly{(p.Fill ? "F" : "")} {Ps(p.Points)}",
                Circle c => $"circle{(c.Fill ? "F" : "")} {P(c.Center)} {F(c.R)}",
                Arc a => $"arc {P(a.Center)} {F(a.R)} {F(a.A0)} {F(a.A1)}",
                Dots d => $"dots {Ps(d.Points)} {F(d.R)}",
                Text t => $"text {t.Value} {P(t.At)} {F(t.Size)}",
                Dashed d => $"dashed {Ps(d.Points)} {F(d.Dash)} {F(d.Gap)}",
                _ => "",
            };
        }
    }

    /// <summary>Line width relative to the box side.</summary>
    public const double LineWidth = 0.055;

    /// <summary>Shapes for one symbol; null if the id is unknown.</summary>
    public static List<Shape>? Shapes(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Table.Value.TryGetValue(id, out var s)) return s;
        if (id.StartsWith("0.1", StringComparison.Ordinal) && Angle(id[3..]) is double a0) return Arrow(a0);
        var (b, dir) = IofSymbols.SplitDirection(id);
        if (dir is not null && Angle(dir) is double a && IofSymbols.IsDirectional(b))
        {
            if (b == "12.12") return FootWithDirection(a);
            return DirectionalNorth.TryGetValue(b, out var north) ? Rotated(north, a - Math.PI / 2) : null;
        }
        // A directional base without direction (picker preview) points north.
        if (IofSymbols.IsDirectional(id)) return Shapes(id + "N");
        return null;
    }

    /// <summary>Typed column F dimensions ("1.5", "8x4", "1.0/2.5") as text; symbols 10.x as drawings.</summary>
    public static List<Shape>? DimensionShapes(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (Shapes(v) is { } s && IofSymbols.Symbol(v)?.Column == IofColumn.F) return s;
        // "0.5/3" (9.3 height on slope) and "2/3" (9.4 heights of two features): two numbers split by a diagonal.
        var parts = v.Split('/').Select(p => p.Trim()).ToList();
        if (parts.Count == 2 && parts[0].Length > 0 && parts[1].Length > 0)
        {
            double size = Math.Max(parts[0].Length, parts[1].Length) > 3 ? 0.26 : 0.32;
            return new List<Shape> { new Shape.Text(parts[0], P(0.32, 0.7), size), new Shape.Line(new[] { P(0.2, 0.15), P(0.8, 0.85) }), new Shape.Text(parts[1], P(0.68, 0.3), size) };
        }
        var t = v.Replace('x', '×').Replace('X', '×');
        return new List<Shape> { new Shape.Text(t, P(0.5, 0.5), t.Length > 4 ? 0.3 : 0.4) };
    }

    private static Point2D P(double x, double y) => new(x, y);

    private static double? Angle(string dir) => dir switch
    {
        "E" => 0, "NE" => 45, "N" => 90, "NW" => 135, "W" => 180, "SW" => 225, "S" => 270, "SE" => 315, _ => (double?)null,
    } is double d ? d * Math.PI / 180 : null;

    /// <summary>Arrow from the centre toward direction <paramref name="a"/> (column C: northern, etc.).</summary>
    private static List<Shape> Arrow(double a)
    {
        var tip = P(0.5 + 0.36 * Math.Cos(a), 0.5 + 0.36 * Math.Sin(a));
        var tail = P(0.5 - 0.36 * Math.Cos(a), 0.5 - 0.36 * Math.Sin(a));
        var l = P(tip.X - 0.2 * Math.Cos(a - 0.5), tip.Y - 0.2 * Math.Sin(a - 0.5));
        var r = P(tip.X - 0.2 * Math.Cos(a + 0.5), tip.Y - 0.2 * Math.Sin(a + 0.5));
        return new List<Shape> { new Shape.Line(new[] { tail, tip }), new Shape.Line(new[] { l, tip, r }) };
    }

    /// <summary>Rotates shapes around the box centre by <paramref name="a"/> radians (counterclockwise); text stays upright.</summary>
    internal static List<Shape> Rotated(List<Shape> shapes, double a)
    {
        Point2D R(Point2D p)
        {
            double dx = p.X - 0.5, dy = p.Y - 0.5;
            return P(0.5 + dx * Math.Cos(a) - dy * Math.Sin(a), 0.5 + dx * Math.Sin(a) + dy * Math.Cos(a));
        }
        return shapes.Select<Shape, Shape>(s => s switch
        {
            Shape.Line l => new Shape.Line(l.Points.Select(R).ToList()),
            Shape.Poly p => new Shape.Poly(p.Points.Select(R).ToList(), p.Fill),
            Shape.Circle c => new Shape.Circle(R(c.Center), c.R, c.Fill),
            Shape.Arc arc => new Shape.Arc(R(arc.Center), arc.R, arc.A0 + a, arc.A1 + a),
            Shape.Dots d => new Shape.Dots(d.Points.Select(R).ToList(), d.R),
            Shape.Text t => new Shape.Text(t.Value, R(t.At), t.Size),
            Shape.Dashed d => new Shape.Dashed(d.Points.Select(R).ToList(), d.Dash, d.Gap),
            _ => s,
        }).ToList();
    }

    /// <summary>Column G symbols 12.1–12.7 drawn pointing north; other directions are rotations of these.</summary>
    private static readonly Dictionary<string, List<Shape>> DirectionalNorth = new()
    {
        // 12.1 side: circle (feature) with the flag dot outside.
        ["12.1"] = new() { new Shape.Circle(P(0.5, 0.42), 0.24, false), new Shape.Dots(new[] { P(0.5, 0.81) }, 0.065) },
        // 12.2 edge: circle with a short tick across its edge.
        ["12.2"] = new() { new Shape.Circle(P(0.5, 0.46), 0.27, false), new Shape.Line(new[] { P(0.5, 0.62), P(0.5, 0.84) }) },
        // 12.3 part: circle with the dot inside, near the edge.
        ["12.3"] = new() { new Shape.Circle(P(0.5, 0.5), 0.32, false), new Shape.Dots(new[] { P(0.5, 0.68) }, 0.065) },
        // 12.4 corner (inside): the corner points north, dot inside the angle.
        ["12.4"] = new() { new Shape.Line(new[] { P(0.22, 0.48), P(0.5, 0.76), P(0.78, 0.48) }), new Shape.Dots(new[] { P(0.5, 0.58) }, 0.065) },
        // 12.5 corner (outside): dot outside the tip of the angle.
        ["12.5"] = new() { new Shape.Line(new[] { P(0.22, 0.3), P(0.5, 0.58), P(0.78, 0.3) }), new Shape.Dots(new[] { P(0.5, 0.76) }, 0.065) },
        // 12.6 tip: narrow angle (< 45°), dot just beyond the tip.
        ["12.6"] = new() { new Shape.Line(new[] { P(0.36, 0.16), P(0.5, 0.64), P(0.64, 0.16) }), new Shape.Dots(new[] { P(0.5, 0.8) }, 0.065) },
        // 12.7 end: line ending with a short cross bar.
        ["12.7"] = new() { new Shape.Line(new[] { P(0.5, 0.16), P(0.5, 0.78) }), new Shape.Line(new[] { P(0.3, 0.78), P(0.7, 0.78) }) },
    };

    /// <summary>12.12 foot with direction: circle (feature) and an upright foot mark "L" on the side of direction <paramref name="a"/>.</summary>
    private static List<Shape> FootWithDirection(double a)
    {
        var c = P(0.5 - 0.12 * Math.Cos(a), 0.5 - 0.12 * Math.Sin(a));
        var m = P(0.5 + 0.3 * Math.Cos(a), 0.5 + 0.3 * Math.Sin(a));
        return new List<Shape>
        {
            new Shape.Circle(c, 0.24, false),
            new Shape.Line(new[] { P(m.X - 0.05, m.Y + 0.12), P(m.X - 0.05, m.Y - 0.06), P(m.X + 0.08, m.Y - 0.06) }),
        };
    }

    /// <summary>Quadratic Bézier as a polyline.</summary>
    private static List<Point2D> Curve(Point2D p0, Point2D c, Point2D p1, int steps = 12)
        => Enumerable.Range(0, steps + 1).Select(i =>
        {
            double t = (double)i / steps, u = 1 - t;
            return P(u * u * p0.X + 2 * u * t * c.X + t * t * p1.X, u * u * p0.Y + 2 * u * t * c.Y + t * t * p1.Y);
        }).ToList();

    private static Shape Box(double x0, double y0, double x1, double y1, bool fill = false)
        => new Shape.Poly(new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) }, fill);

    private static Shape Triangle(double cx, double cy, double s)
        => new Shape.Poly(new[] { P(cx - s, cy - s * 0.8), P(cx + s, cy - s * 0.8), P(cx, cy + s * 0.9) }, true);

    private static IEnumerable<Shape> Dashes(double y, double x0 = 0.15, double x1 = 0.85, int count = 3)
    {
        double step = (x1 - x0) / (count * 2 - 1);
        return Enumerable.Range(0, count).Select(i => (Shape)new Shape.Line(new[] { P(x0 + i * 2 * step, y), P(x0 + (i * 2 + 1) * step, y) }));
    }

    private static IEnumerable<Shape> Grid(double x0, double y0, double x1, double y1, int n = 4)
        => Enumerable.Range(0, n + 1).SelectMany(i =>
        {
            double t = (double)i / n;
            return new Shape[] { new Shape.Line(new[] { P(x0 + (x1 - x0) * t, y0), P(x0 + (x1 - x0) * t, y1) }), new Shape.Line(new[] { P(x0, y0 + (y1 - y0) * t), P(x1, y0 + (y1 - y0) * t) }) };
        });

    private static List<Point2D> Scatter(double x0, double y0, double x1, double y1, int n)
        => Enumerable.Range(0, n).SelectMany(i => Enumerable.Range(0, n).Select(j =>
            P(x0 + (x1 - x0) * (i + (j % 2 == 0 ? 0.25 : 0.75)) / n, y0 + (y1 - y0) * (j + 0.5) / n))).ToList();

    private static IEnumerable<Shape> Tree(double cx, double cy, double r)
        => new Shape[] { new Shape.Circle(P(cx, cy + r * 0.4), r, false), new Shape.Line(new[] { P(cx, cy - r * 0.6), P(cx, cy - r * 1.8) }) };

    private static List<Shape> L(params Shape[] shapes) => shapes.ToList();
    private static List<Shape> L(IEnumerable<Shape> a, params Shape[] b) => a.Concat(b).ToList();

    private static readonly Lazy<Dictionary<string, List<Shape>>> Table = new(BuildTable);

    private static Dictionary<string, List<Shape>> BuildTable()
    {
        var t = new Dictionary<string, List<Shape>>();
        var c = P(0.5, 0.5);
        Shape Ln(params Point2D[] pts) => new Shape.Line(pts);
        Shape Dot(Point2D p, double r) => new Shape.Dots(new[] { p }, r);

        // C: which
        t["0.2"] = L(Ln(P(0.15, 0.68), P(0.85, 0.68)), Ln(P(0.15, 0.32), P(0.85, 0.32)), Dot(P(0.5, 0.68), 0.09));
        t["0.3"] = L(Ln(P(0.15, 0.68), P(0.85, 0.68)), Ln(P(0.15, 0.32), P(0.85, 0.32)), Dot(P(0.5, 0.32), 0.09));
        t["0.4"] = L(Ln(P(0.15, 0.78), P(0.85, 0.78)), Ln(P(0.15, 0.5), P(0.85, 0.5)), Ln(P(0.15, 0.22), P(0.85, 0.22)), Dot(P(0.5, 0.5), 0.09));

        // D: landforms
        t["1.1"] = L(Ln(P(0.12, 0.3), P(0.42, 0.3), P(0.58, 0.7), P(0.88, 0.7)));
        t["1.2"] = L(new Shape.Arc(P(0.5, 0.3), 0.32, 0, Math.PI));
        t["1.3"] = L(new Shape.Arc(P(0.5, 0.7), 0.32, Math.PI, 2 * Math.PI));
        t["1.4"] = L(new[] { Ln(P(0.12, 0.65), P(0.88, 0.65)) }.Concat(new[] { 0.22, 0.42, 0.62, 0.82 }.Select(x => Ln(P(x, 0.65), P(x, 0.4)))));
        t["1.5"] = L(new Shape[] { new Shape.Arc(c, 0.32, Math.PI * 0.15, Math.PI * 0.85), new Shape.Arc(c, 0.32, Math.PI * 1.15, Math.PI * 1.85) }
            .Concat(new[] { 0.3, 0.5, 0.7 }.Select(x => Ln(P(x, 0.82 - Math.Abs(x - 0.5) * 0.4), P(x, 0.62)))));
        t["1.6"] = L(new[] { Ln(P(0.12, 0.5), P(0.88, 0.5)) }.Concat(new[] { 0.25, 0.5, 0.75 }.SelectMany(x => new[] { Ln(P(x, 0.5), P(x, 0.72)), Ln(P(x, 0.5), P(x, 0.28)) })));
        t["1.7"] = L(Ln(P(0.15, 0.78), P(0.85, 0.5), P(0.15, 0.22)));
        t["1.8"] = L(new Shape.Dots(new[] { 0.18, 0.34, 0.5, 0.66, 0.82 }.Select(x => P(x, 0.5)).ToList(), 0.05));
        t["1.9"] = L(new Shape.Circle(c, 0.34, false));
        t["1.10"] = L(new Shape.Circle(c, 0.16, true));
        t["1.11"] = L(new Shape.Arc(P(0.05, 0.5), 0.3, -Math.PI / 2.6, Math.PI / 2.6), new Shape.Arc(P(0.95, 0.5), 0.3, Math.PI - Math.PI / 2.6, Math.PI + Math.PI / 2.6));
        t["1.12"] = L(new Shape[] { new Shape.Circle(c, 0.32, false) }.Concat(new[] { 0.0, 0.5, 1.0, 1.5 }.Select(k =>
        {
            double a = k * Math.PI;
            return Ln(P(0.5 + 0.32 * Math.Cos(a), 0.5 + 0.32 * Math.Sin(a)), P(0.5 + 0.18 * Math.Cos(a), 0.5 + 0.18 * Math.Sin(a)));
        })));
        t["1.13"] = L(new Shape.Arc(P(0.5, 0.62), 0.26, Math.PI, 2 * Math.PI));
        t["1.14"] = L(Ln(P(0.25, 0.75), P(0.5, 0.25), P(0.75, 0.75)));
        t["1.15"] = L(Ln(P(0.15, 0.7), P(0.25, 0.5), P(0.35, 0.7)), Ln(P(0.6, 0.45), P(0.7, 0.25), P(0.8, 0.45)),
                      new Shape.Dots(new[] { P(0.7, 0.75), P(0.25, 0.25), P(0.5, 0.5) }, 0.06));
        t["1.16"] = Enumerable.Range(0, 4).Select(k =>
        {
            double a = k * Math.PI / 4;
            return Ln(P(0.5 - 0.28 * Math.Cos(a), 0.5 - 0.28 * Math.Sin(a)), P(0.5 + 0.28 * Math.Cos(a), 0.5 + 0.28 * Math.Sin(a)));
        }).ToList();

        // D: rock and boulders
        t["2.1"] = L(Ln(P(0.12, 0.7), P(0.88, 0.7)), new Shape.Poly(new[] { P(0.2, 0.7), P(0.3, 0.7), P(0.25, 0.32) }, true),
                     new Shape.Poly(new[] { P(0.45, 0.7), P(0.55, 0.7), P(0.5, 0.32) }, true), new Shape.Poly(new[] { P(0.7, 0.7), P(0.8, 0.7), P(0.75, 0.32) }, true));
        t["2.2"] = L(new Shape.Poly(new[] { P(0.38, 0.15), P(0.62, 0.15), P(0.58, 0.85), P(0.42, 0.85) }, true));
        t["2.3"] = L(new Shape.Poly(new[] { P(0.2, 0.5), P(0.8, 0.8), P(0.8, 0.2) }, true));
        t["2.4"] = L(Triangle(0.5, 0.47, 0.3));
        t["2.5"] = L(Triangle(0.3, 0.7, 0.15), Triangle(0.7, 0.68, 0.13), Triangle(0.32, 0.3, 0.13), Triangle(0.72, 0.28, 0.15));
        t["2.6"] = L(Triangle(0.35, 0.36, 0.17), Triangle(0.65, 0.36, 0.17), Triangle(0.5, 0.66, 0.17));
        t["2.7"] = L(Triangle(0.25, 0.72, 0.08), Triangle(0.7, 0.75, 0.08), Triangle(0.48, 0.5, 0.08), Triangle(0.25, 0.26, 0.08), Triangle(0.75, 0.3, 0.08));
        t["2.8"] = L(Box(0.18, 0.18, 0.82, 0.82), new Shape.Dots(Scatter(0.18, 0.18, 0.82, 0.82, 3), 0.04));
        t["2.9"] = L(new[] { Ln(P(0.3, 0.12), P(0.3, 0.88)), Ln(P(0.7, 0.12), P(0.7, 0.88)) }
            .Concat(new[] { 0.25, 0.5, 0.75 }.SelectMany(y => new[] { Ln(P(0.3, y), P(0.14, y)), Ln(P(0.7, y), P(0.86, y)) })));
        t["2.10"] = L(Ln(P(0.12, 0.6), P(0.88, 0.6)), Ln(P(0.12, 0.4), P(0.88, 0.4)), Ln(P(0.12, 0.6), P(0.12, 0.4)));

        // D: water and marsh
        t["3.1"] = L(new Shape.Circle(c, 0.36, false), Ln(P(0.26, 0.56), P(0.74, 0.56)), Ln(P(0.26, 0.42), P(0.74, 0.42)));
        t["3.2"] = L(new Shape.Circle(c, 0.26, false), Ln(P(0.36, 0.5), P(0.64, 0.5)));
        t["3.3"] = L(new Shape.Arc(P(0.5, 0.6), 0.3, Math.PI, 2 * Math.PI), Ln(P(0.3, 0.45), P(0.7, 0.45)));
        t["3.4"] = L(new Shape.Line(Enumerable.Range(0, 13).Select(i => P(0.12 + i * 0.76 / 12, 0.5 + 0.12 * Math.Sin(i * Math.PI / 3))).ToList()));
        t["3.5"] = Dashes(0.5, count: 4).ToList();
        t["3.6"] = L(Dashes(0.5, count: 4), new Shape.Dots(new[] { 0.25, 0.5, 0.75 }.Select(x => P(x, 0.32)).ToList(), 0.04));
        t["3.7"] = Dashes(0.72).Concat(Dashes(0.5, 0.25, 0.75, 2)).Concat(Dashes(0.28)).ToList();
        t["3.8"] = L(Dashes(0.8).Concat(Dashes(0.2)), new Shape.Circle(c, 0.16, false));
        t["3.9"] = L(new Shape[] { new Shape.Circle(c, 0.18, false) }.Concat(new[] { 0.25, 0.75, 1.25, 1.75 }.Select(k =>
        {
            double a = k * Math.PI;
            return Ln(P(0.5 + 0.18 * Math.Cos(a), 0.5 + 0.18 * Math.Sin(a)), P(0.5 + 0.36 * Math.Cos(a), 0.5 + 0.36 * Math.Sin(a)));
        })));
        t["3.10"] = L(new Shape.Arc(P(0.38, 0.5), 0.18, Math.PI / 2, 1.5 * Math.PI), Ln(P(0.38, 0.68), P(0.38, 0.32)), Ln(P(0.38, 0.5), P(0.86, 0.5)));
        t["3.11"] = L(Ln(P(0.22, 0.78), P(0.22, 0.25), P(0.78, 0.25), P(0.78, 0.78)), Ln(P(0.22, 0.6), P(0.78, 0.6)));

        // D: vegetation
        t["4.1"] = L(new Shape.Circle(c, 0.32, false));
        t["4.2"] = L(new Shape.Circle(c, 0.32, false), new Shape.Dots(Scatter(0.32, 0.32, 0.68, 0.68, 2), 0.04));
        t["4.3"] = L(Ln(P(0.18, 0.18), P(0.18, 0.82), P(0.82, 0.82)), new Shape.Dots(Scatter(0.24, 0.24, 0.84, 0.76, 3), 0.04));
        t["4.4"] = L(new Shape.Circle(c, 0.32, false), new Shape.Dots(Scatter(0.14, 0.82, 0.86, 0.95, 3).Concat(Scatter(0.14, 0.05, 0.86, 0.18, 3)).ToList(), 0.03));
        t["4.5"] = Grid(0.2, 0.2, 0.8, 0.8).ToList();
        t["4.6"] = Grid(0.12, 0.38, 0.88, 0.62, 3).ToList();
        t["4.7"] = L(new Shape.Dots(new[] { 0.15, 0.29, 0.43, 0.57, 0.71, 0.85 }.Select(x => P(x, 0.5)).ToList(), 0.045));
        t["4.8"] = L(new Shape.Circle(P(0.36, 0.42), 0.18, false), new Shape.Circle(P(0.64, 0.42), 0.18, false), new Shape.Circle(P(0.5, 0.66), 0.18, false));
        t["4.9"] = Tree(0.5, 0.6, 0.22).ToList();
        t["4.10"] = L(Ln(P(0.5, 0.75), P(0.5, 0.42)), Ln(P(0.25, 0.22), P(0.5, 0.42), P(0.75, 0.22)), Ln(P(0.5, 0.42), P(0.5, 0.2)));

        // D: man-made features
        t["5.1"] = L(Ln(P(0.12, 0.62), P(0.88, 0.62)), Ln(P(0.12, 0.38), P(0.88, 0.38)));
        t["5.2"] = Dashes(0.5, count: 3).ToList();
        t["5.3"] = Dashes(0.5, count: 5).ToList();
        t["5.4"] = L(Ln(P(0.15, 0.78), P(0.3, 0.64), P(0.7, 0.64), P(0.85, 0.78)), Ln(P(0.15, 0.22), P(0.3, 0.36), P(0.7, 0.36), P(0.85, 0.22)));
        t["5.5"] = L(Ln(P(0.1, 0.5), P(0.9, 0.5)), Ln(P(0.3, 0.38), P(0.3, 0.62)), Ln(P(0.7, 0.38), P(0.7, 0.62)));
        t["5.6"] = L(Ln(P(0.25, 0.15), P(0.5, 0.85), P(0.75, 0.15)), Ln(P(0.34, 0.4), P(0.66, 0.4)), Ln(P(0.2, 0.68), P(0.8, 0.68)));
        t["5.7"] = L(new Shape.Arc(P(0.5, 0.3), 0.3, 0, Math.PI), Ln(P(0.2, 0.3), P(0.2, 0.18)), Ln(P(0.8, 0.3), P(0.8, 0.18)), Ln(P(0.1, 0.18), P(0.9, 0.18)));
        t["5.8"] = L(Ln(P(0.12, 0.5), P(0.88, 0.5)), new Shape.Dots(new[] { 0.25, 0.5, 0.75 }.Select(x => P(x, 0.5)).ToList(), 0.08));
        t["5.9"] = L(new[] { Ln(P(0.12, 0.5), P(0.88, 0.5)) }.Concat(new[] { 0.28, 0.5, 0.72 }.SelectMany(x => new[] { Ln(P(x - 0.07, 0.4), P(x + 0.07, 0.6)), Ln(P(x - 0.07, 0.6), P(x + 0.07, 0.4)) })));
        t["5.10"] = L(Ln(P(0.1, 0.5), P(0.38, 0.5)), Ln(P(0.62, 0.5), P(0.9, 0.5)), Ln(P(0.38, 0.32), P(0.38, 0.68)), Ln(P(0.62, 0.32), P(0.62, 0.68)));
        t["5.11"] = L(Box(0.22, 0.22, 0.78, 0.78, true));
        t["5.12"] = L(Box(0.18, 0.18, 0.82, 0.82));
        t["5.13"] = L(Dashes(0.2, 0.2, 0.8, 2).Concat(Dashes(0.8, 0.2, 0.8, 2)),
                      Ln(P(0.2, 0.2), P(0.2, 0.4)), Ln(P(0.2, 0.6), P(0.2, 0.8)), Ln(P(0.8, 0.2), P(0.8, 0.4)), Ln(P(0.8, 0.6), P(0.8, 0.8)));
        t["5.14"] = L(Ln(P(0.1, 0.5), P(0.9, 0.5)), new Shape.Circle(P(0.3, 0.5), 0.08, false), new Shape.Circle(P(0.7, 0.5), 0.08, false));
        t["5.15"] = L(Ln(P(0.3, 0.15), P(0.42, 0.7)), Ln(P(0.7, 0.15), P(0.58, 0.7)), Box(0.34, 0.7, 0.66, 0.85));
        t["5.16"] = L(Ln(P(0.3, 0.15), P(0.3, 0.6), P(0.7, 0.6), P(0.7, 0.15)), Ln(P(0.3, 0.6), P(0.3, 0.85)));
        t["5.17"] = L(new Shape.Circle(c, 0.2, false), Dot(c, 0.06));
        t["5.18"] = L(Ln(P(0.4, 0.15), P(0.4, 0.85)), Ln(P(0.4, 0.75), P(0.68, 0.6)), Ln(P(0.4, 0.55), P(0.68, 0.4)));
        t["5.19"] = L(new Shape.Circle(c, 0.3, false), Dot(c, 0.12));
        t["5.20"] = L(Box(0.4, 0.3, 0.6, 0.8, true), Ln(P(0.2, 0.22), P(0.8, 0.22)));
        // 5.21 canopy: a roof on two posts.
        t["5.21"] = L(Ln(P(0.18, 0.78), P(0.82, 0.78)), Ln(P(0.34, 0.78), P(0.34, 0.18)), Ln(P(0.66, 0.78), P(0.66, 0.18)));
        t["5.22"] = L(Ln(P(0.15, 0.2), P(0.15, 0.35), P(0.35, 0.35), P(0.35, 0.5), P(0.55, 0.5), P(0.55, 0.65), P(0.75, 0.65), P(0.75, 0.8)));
        // 5.23 out of bounds area: a flower bed (square with a flower).
        t["5.23"] = L(new Shape[] { Box(0.17, 0.17, 0.83, 0.83), new Shape.Circle(c, 0.065, false) }.Concat(Enumerable.Range(0, 5).Select(k =>
        {
            double a = Math.PI / 2 + k * 2 * Math.PI / 5;
            return (Shape)new Shape.Circle(P(0.5 + 0.15 * Math.Cos(a), 0.5 + 0.15 * Math.Sin(a)), 0.085, false);
        })));
        // 5.24 railway: a track with sleepers.
        {
            var a = P(0.14, 0.24); var b = P(0.86, 0.76);
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)), ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
            t["5.24"] = L(new[] { Ln(a, b) }.Concat(Enumerable.Range(1, 6).Select(i =>
            {
                double tt = i / 7.0;
                var m = P(a.X + (b.X - a.X) * tt, a.Y + (b.Y - a.Y) * tt);
                return Ln(P(m.X - uy * 0.11, m.Y + ux * 0.11), P(m.X + uy * 0.11, m.Y - ux * 0.11));
            })));
        }
        t["6.1"] = L(Ln(P(0.25, 0.25), P(0.75, 0.75)), Ln(P(0.25, 0.75), P(0.75, 0.25)));
        t["6.2"] = L(new Shape.Circle(c, 0.26, false));

        // D: indoor (ISInOM 6.7.3), traced from the Purple Pen figures (200-unit box, y up).
        List<Point2D> Pp(params (double X, double Y)[] xy) => xy.Select(p => P(0.5 + p.X / 200, 0.5 + p.Y / 200)).ToList();
        Shape PpFill(params (double, double)[] xy) => new Shape.Poly(Pp(xy), true);
        Shape PpLine(params (double, double)[] xy) => new Shape.Line(Pp(xy));
        // 7.119 Pillar: shaft with a wider base and capital.
        t["7.119"] = L(
            PpFill((-12.3, 39), (-12.3, -45.6), (11.7, -45.6), (11.7, 39)),
            PpFill((-28.2, -57.3), (27.6, -57.3), (18.6, -45.6), (-19.5, -45.6)),
            PpFill((-27.9, 51), (27.9, 51), (18.9, 39), (-19.2, 39)));
        // 7.132 Toilet: cistern, bowl and seat ring (seen from the side).
        t["7.132"] = L(
            PpFill((-19.8, -41.4), (-3.9, -44.4), (7.2, -42.6), (19.5, -36.9), (22.2, -55.8), (16.2, -60.3), (-1.8, -66), (-15.6, -66.3), (-26.4, -60.9), (-19.5, -41.7)),
            PpFill((8.7, 11.1), (33.9, 0.9), (39.9, 15.6), (41.4, 27.6), (41.7, 39.9), (40.5, 51.3), (37.8, 61.8), (34.2, 69), (30, 74.1), (21.9, 78), (12.6, 74.1), (8.7, 69), (5.4, 60.9), (3.9, 49.2), (4.2, 35.7)),
            PpFill((31.5, -9.9), (19.5, -17.4), (3.9, -22.8), (-12.3, -24), (-23.7, -22.8), (-33.3, -19.8), (-30.9, -26.1), (-26.4, -32.7), (-18, -38.7), (-7.5, -41.7), (1.5, -41.1), (10.8, -37.8), (18.9, -33.6), (24.3, -28.8), (28.5, -22.5), (31.8, -9.9)),
            // Seat ring: outer edge, then the opening in the opposite direction (a hole with the non-zero fill rule).
            PpFill((33, -2.1), (28.8, -6.9), (14.7, -15.6), (1.2, -19.5), (-8.1, -20.1), (-18, -19.8), (-31.8, -16.5), (-37.5, -12), (-37.5, -4.8), (-28.2, 3), (-14.7, 6.9), (-2.7, 8.7), (6.9, 9.3), (33.3, -1.5), (33, -2.1),
                   (-4.2, 3.3), (-6.3, 3), (-18.6, 0.3), (-24.6, -2.4), (-27, -7.8), (-24.3, -10.5), (-12.9, -13.2), (-3, -13.8), (7.2, -11.7), (10.8, -10.8), (17.4, -6.3), (18, -3), (15, 0.6)));
        // 7.133 Shower: pipe with a shower head and water drops.
        t["7.133"] = L(
            PpLine((-15.9, 21.3), (17.1, 39.6)),
            PpLine((7.7, 16.2), (9.9, 11.4)), PpLine((19.5, 23.1), (24.6, 18.9)), PpLine((-4.2, 9.3), (-3.3, 3)),
            PpLine((10.2, -5.4), (11.7, -10.5)), PpLine((40.5, 17.1), (45.6, 12.9)), PpLine((-7.2, -12.3), (-6.3, -18.3)),
            PpLine((26.1, 4.8), (30, 0.9)),
            PpLine((-42.3, -50.8), (-42, 38.4), (-38.7, 45.6), (-30, 52.2), (-18.9, 52.8), (-3.3, 43.5)),
            PpFill((15.6, 42.6), (11.4, 47.4), (3.9, 49.5), (-8.7, 47.1), (-18.3, 36.3), (-19.5, 29.7), (-17.7, 37.7)));
        // 7.134 Sink: basin with a swan-neck tap and two handles.
        t["7.134"] = L(
            PpFill((-8.7, -4.8), (-6.6, 53.7), (0.6, 62.1), (18.9, 67.8), (29.4, 67.5), (39.3, 64.5), (48, 58.5), (54.3, 49.5), (52.8, 45.9), (44.1, 42.9), (41.4, 42.9), (36, 49.8), (27.3, 54.3), (16.8, 53.7), (9, 49.2), (7.8, -4.8)),
            PpFill((-11.4, 13.8), (-15, 9), (-14.4, -0.3), (-11.4, -2.7), (-8.4, 1.5), (-8.1, 8.1)),
            PpFill((44.1, 34.8), (39.9, 20.7), (41.1, 15.3), (47.1, 13.2), (51.9, 16.2), (52.2, 21), (45.3, 34.8)),
            PpFill((-63.3, -23.1), (-46.5, -17.7), (-0.9, -14.1), (38.1, -16.8), (54, -20.1), (63.9, -25.2), (65.1, -21.6), (59.7, -16.8), (45.9, -12.6), (0, -8.7), (-49.5, -12.6), (-63.9, -19.2), (-65.1, -21.6), (-62.4, -25.2)),
            PpFill((-64.5, -21), (-60.9, -35.1), (-48.6, -50.7), (-30, -62.1), (-3.3, -67.5), (18.9, -65.4), (40.8, -56.4), (56.4, -42), (62.1, -32.1), (64.8, -21.3), (64.2, -21.6), (54.9, -25.8), (16.8, -30.9), (-27, -30.9), (-48.9, -27.3), (-63.3, -21.6)),
            PpFill((10.8, 13.8), (7.5, 8.1), (7.2, 1.5), (10.8, -2.7), (14.4, 1.5), (14.4, 8.4)));
        // 7.141 Table or bench: table top on legs, two stools.
        t["7.141"] = L(
            PpFill((-29.1, 0.6), (-18.3, 0.6), (-18.3, -1.8), (-20.1, -5.7), (-24.3, -6.9), (-28.2, -4.8)),
            PpFill((-45.9, 0.6), (-45.9, -26.7), (-44.4, -30.9), (-39.9, -32.4), (-35.7, -30.9), (-33.6, -26.7), (-33.6, 0.6)),
            PpFill((35.1, 0.6), (35.1, -26.7), (36.6, -30.9), (40.8, -32.4), (45.3, -30.9), (47.1, -26.7), (47.1, 0.6)),
            PpFill((20.4, 0.6), (31.5, 0.6), (31.5, -1.8), (29.7, -5.7), (25.2, -6.6), (21, -4.5)),
            PpFill((-30.9, 35.7), (35.1, 35.7), (57.6, 4.8), (-57.6, 4.8)));
        // 7.142 Cupboard / shelf: cabinet with a door and shelves.
        t["7.142"] = L(
            PpLine((-48.3, 73.5), (-48.3, -69.6), (48.3, -69.6), (48.3, 73.5)), PpLine((-48.3, 73.5), (48.3, 73.5)),
            PpLine((-1.2, 69), (-1.2, -69)), PpLine((-25.5, 10.8), (-17.1, 10.8)),
            PpLine((-0.9, 47.4), (47.7, 47.4)), PpLine((-0.3, 23.1), (48.3, 23.1)),
            PpLine((-0.9, -8.1), (47.7, -8.1)), PpLine((-0.3, -41.1), (48.3, -41.1)));
        // 7.143 Couch / sofa: back, seat cushions, arms and feet.
        t["7.143"] = L(
            PpFill((-43.5, 20.7), (-34.5, 16.2), (-32.1, 6.6), (32.1, 6.6), (34.8, 17.7), (45, 21), (44.7, 30.9), (40.8, 40.2), (33, 43.5), (-34.2, 43.5), (-38.7, 42.3), (-43.8, 35.1)),
            PpFill((-31.8, 2.4), (-3, 2.4), (-3, -7.2), (-31.8, -7.2)),
            PpFill((-42.3, 3.9), (-42.9, 8.1), (-46.2, 12.9), (-51.6, 13.5), (-56.4, 10.8), (-59.1, 3), (-58.8, -29.1), (58.8, -29.1), (58.8, 3), (57, 11.4), (54, 14.1), (48, 14.1), (45, 12), (43, 3.6), (42.5, -11.4), (-42, -11.4), (-42, 3.6)),
            PpFill((39.9, -21.3), (39.9, -38.7), (52.5, -38.7), (52.5, -22.2)),
            PpFill((3.3, 2.4), (32.1, 2.4), (32.1, -7.2), (3.3, -7.2)),
            PpFill((-52.5, -21.3), (-52.5, -38.7), (-39.9, -38.7), (-39.9, -22.2)));

        // E: appearance
        t["8.1"] = L(new Shape.Arc(P(0.5, 0.2), 0.42, Math.PI * 0.22, Math.PI * 0.78));
        t["8.2"] = L(new Shape.Arc(P(0.5, 0.8), 0.42, Math.PI * 1.22, Math.PI * 1.78));
        t["8.3"] = L(Ln(P(0.25, 0.85), P(0.25, 0.4)), new Shape.Arc(P(0.5, 0.4), 0.25, Math.PI, 2 * Math.PI), Ln(P(0.75, 0.4), P(0.75, 0.85)));
        t["8.4"] = Grid(0.2, 0.2, 0.8, 0.8).ToList();
        t["8.5"] = L(new Shape.Dots(Enumerable.Range(0, 16).Select(i =>
        {
            double k = (i % 4) / 3.0;
            return (i / 4) switch { 0 => P(0.2 + 0.6 * k, 0.2), 1 => P(0.8, 0.2 + 0.6 * k), 2 => P(0.8 - 0.6 * k, 0.8), _ => P(0.2, 0.8 - 0.6 * k) };
        }).ToList(), 0.035));
        t["8.6"] = L(Triangle(0.3, 0.68, 0.1), Triangle(0.7, 0.68, 0.1), Triangle(0.5, 0.3, 0.1));
        t["8.7"] = Dashes(0.72, count: 2).Concat(Dashes(0.5, count: 2)).Concat(Dashes(0.28, count: 2)).ToList();
        t["8.8"] = L(new Shape.Dots(Scatter(0.18, 0.18, 0.82, 0.82, 4), 0.035));
        t["8.9"] = L(new Shape.Poly(new[] { P(0.5, 0.88), P(0.28, 0.42), P(0.72, 0.42) }, false), Ln(P(0.5, 0.42), P(0.5, 0.12)));
        t["8.10"] = Tree(0.5, 0.56, 0.24).ToList();
        t["8.11"] = L(Ln(P(0.25, 0.2), P(0.25, 0.6)), new Shape.Arc(P(0.5, 0.6), 0.25, Math.PI * 0.45, Math.PI), Ln(P(0.75, 0.2), P(0.75, 0.45)), Ln(P(0.62, 0.62), P(0.86, 0.2)));

        // F: combinations and bend
        t["10.1"] = L(Ln(P(0.2, 0.2), P(0.8, 0.8)), Ln(P(0.2, 0.8), P(0.8, 0.2)));
        t["10.2"] = L(Ln(P(0.2, 0.82), P(0.5, 0.5), P(0.8, 0.82)), Ln(P(0.5, 0.5), P(0.5, 0.15)));
        t["11.1"] = L(new Shape.Line(Curve(P(0.72, 0.84), P(-0.1, 0.5), P(0.72, 0.16))));

        // G: flag location (feature = line/shape, flag = dot); 12.1–12.7 and 12.12 are directional (see Shapes).
        t["12.8"] = L(Ln(P(0.34, 0.14), P(0.34, 0.86)), Ln(P(0.66, 0.14), P(0.66, 0.86)), Dot(P(0.5, 0.72), 0.065));
        t["12.9"] = L(Ln(P(0.34, 0.14), P(0.34, 0.86)), Ln(P(0.66, 0.14), P(0.66, 0.86)), Dot(P(0.5, 0.28), 0.065));
        t["12.10"] = L(Ln(P(0.32, 0.14), P(0.32, 0.6), P(0.68, 0.6), P(0.68, 0.14)), Dot(P(0.5, 0.8), 0.065));
        t["12.11"] = L(Ln(P(0.36, 0.86), P(0.36, 0.2), P(0.76, 0.2)), Dot(P(0.54, 0.34), 0.065));
        t["12.13"] = L(Ln(P(0.22, 0.5), P(0.22, 0.74), P(0.78, 0.74), P(0.78, 0.5)), Dot(P(0.5, 0.54), 0.065));
        t["12.14"] = L(Ln(P(0.24, 0.74), P(0.76, 0.74)), Ln(P(0.24, 0.26), P(0.76, 0.26)), Dot(c, 0.065));

        // H: other information
        t["13.1"] = L(Box(0.4, 0.15, 0.6, 0.85, true), Box(0.15, 0.4, 0.85, 0.6, true));
        t["13.2"] = L(new Shape.Poly(new[] { P(0.25, 0.82), P(0.75, 0.82), P(0.65, 0.18), P(0.35, 0.18) }, false), Ln(P(0.29, 0.6), P(0.71, 0.6)));
        t["13.3"] = L(new Shape.Circle(P(0.5, 0.8), 0.08, false), Ln(P(0.5, 0.72), P(0.5, 0.4)), Ln(P(0.28, 0.58), P(0.72, 0.58)), Ln(P(0.32, 0.12), P(0.5, 0.4), P(0.68, 0.12)));

        // Legacy (IOF 2018) symbols kept for old projects, drawn as before.
        t[IofSymbols.Legacy.Barrier] = L(Ln(P(0.15, 0.5), P(0.85, 0.5)), Ln(P(0.15, 0.32), P(0.15, 0.68)), Ln(P(0.85, 0.32), P(0.85, 0.68)), Box(0.42, 0.42, 0.58, 0.58, true));
        t[IofSymbols.Legacy.RubbishBin] = L(new Shape.Poly(new[] { P(0.3, 0.78), P(0.7, 0.78), P(0.64, 0.2), P(0.36, 0.2) }, false), Ln(P(0.24, 0.78), P(0.76, 0.78)));
        t[IofSymbols.Legacy.TopEdge] = L(new Shape.Arc(P(0.5, 0.25), 0.36, Math.PI * 0.1, Math.PI * 0.9), Dot(P(0.5, 0.61), 0.07));
        t[IofSymbols.Legacy.Beginning] = L(Ln(P(0.35, 0.65), P(0.88, 0.12)), Dot(P(0.22, 0.78), 0.07));
        t[IofSymbols.Legacy.Bend] = L(Ln(P(0.75, 0.88), P(0.35, 0.5), P(0.75, 0.12)), Dot(P(0.2, 0.5), 0.07));
        t[IofSymbols.Legacy.Radio] = L(Ln(P(0.62, 0.9), P(0.38, 0.5), P(0.62, 0.5), P(0.38, 0.1)), Ln(P(0.3, 0.2), P(0.38, 0.1), P(0.46, 0.2)));
        return t;
    }

    // MARK: Full-width rows (14.1, 15.x, 16.x)

    /// <summary>Width of a full-width row in box units (columns A–H).</summary>
    public const double RowUnits = 8.0;

    /// <summary>Shapes for a full-width row in a box 8 units wide and 1 unit high; null for ordinary control rows.</summary>
    public static List<Shape>? RowShapes(DescriptionSheet.Row row)
    {
        if (row.SpecialRow is { } sp)
        {
            switch (sp)
            {
                case DescriptionSheet.Special.StartDistance sd:
                    return Route(RowEnd.None, $"{sd.Meters} m", RowEnd.StartTriangle);
                case DescriptionSheet.Special.Instruction ins:
                {
                    var label = ins.Meters is int m ? $"{m} m" : null;
                    switch (ins.Kind)
                    {
                        case SpecialInstructionKind.TapedAway: return Route(RowEnd.Control, label, RowEnd.ArrowOnly);
                        case SpecialInstructionKind.TapedBetween: return Route(RowEnd.Control, label, RowEnd.Control);
                        case SpecialInstructionKind.TapedToMapExchange: return Route(RowEnd.Control, label, RowEnd.StartTriangle);
                        case SpecialInstructionKind.CrossingPoint:
                            return NavigateEnds(RowEnd.Control).Concat(new Shape[]
                            {
                                new Shape.Arc(P(4, 1.45), 0.83, Math.PI * 1.3, Math.PI * 1.7), new Shape.Arc(P(4, -0.45), 0.83, Math.PI * 0.3, Math.PI * 0.7),
                            }).ToList();
                        case SpecialInstructionKind.OutOfBoundsPassage:
                            return NavigateEnds(RowEnd.Control).Concat(new Shape[]
                            {
                                new Shape.Line(new[] { P(3.1, 0.84), P(3.32, 0.66), P(4.68, 0.66), P(4.9, 0.84) }),
                                new Shape.Line(new[] { P(3.1, 0.16), P(3.32, 0.34), P(4.68, 0.34), P(4.9, 0.16) }),
                            }).ToList();
                        default:
                            // A map (rectangle) with a curved arrow: turn the map over.
                            return new List<Shape>
                            {
                                new Shape.Poly(new[] { P(3.4, 0.2), P(4.5, 0.2), P(4.5, 0.74), P(3.4, 0.74) }, false),
                                new Shape.Line(Curve(P(4.62, 0.2), P(4.98, 0.84), P(4.3, 0.8))),
                                new Shape.Poly(new[] { P(4.1, 0.8), P(4.34, 0.95), P(4.34, 0.65) }, true),
                            };
                    }
                }
            }
        }
        if (row.Kind != ControlKind.Finish) return null;
        var finishLabel = row.FinishMeters is int fm ? $"{fm} m" : null;
        switch (row.FinishRoute)
        {
            case FinishRoute.Taped: return Route(RowEnd.Control, finishLabel, RowEnd.Finish);
            case FinishRoute.Funnel:
                return new Shape[] { new Shape.Circle(P(0.42, 0.5), 0.26, false), new Shape.Line(new[] { P(0.95, 0.74), P(1.2, 0.5), P(0.95, 0.26) }) }
                    .Concat(DashedLine(1.2, 7.3, finishLabel)).Concat(ArrowHead(7.32)).Concat(Target(RowEnd.Finish)).ToList();
            default:
                return NavigateEnds(RowEnd.Finish).Concat(finishLabel is null ? Enumerable.Empty<Shape>() : new Shape[] { new Shape.Text(finishLabel, P(4, 0.5), LabelSize) }).ToList();
        }
    }

    private enum RowEnd { None, Control, Finish, StartTriangle, ArrowOnly }

    private const double LabelSize = 0.42;

    /// <summary>Dashed route with the distance in the middle and an arrow into the target on the right.</summary>
    private static List<Shape> Route(RowEnd start, string? label, RowEnd end)
    {
        double x0 = start == RowEnd.Control ? 0.68 : 0.3;
        double tip = end == RowEnd.ArrowOnly ? 7.62 : 7.32;
        var result = new List<Shape>();
        if (start == RowEnd.Control) result.Add(new Shape.Circle(P(0.42, 0.5), 0.26, false));
        result.AddRange(DashedLine(x0, tip - 0.02, label));
        result.AddRange(ArrowHead(tip));
        result.AddRange(Target(end));
        return result;
    }

    private static List<Shape> DashedLine(double x0, double x1, string? label)
    {
        const double dash = 0.34, gap = 0.18;
        // Whole dashes that fill the run exactly from a to b: the dash and gap are stretched slightly so both ends land
        // on a dash. Both runs then stop exactly the same distance from the text (symmetric gaps).
        Shape Run(double a, double b)
        {
            double length = Math.Abs(b - a);
            int n = Math.Max(1, (int)Math.Round((length + gap) / (dash + gap), MidpointRounding.AwayFromZero));
            double k = n > 1 ? length / (n * dash + (n - 1) * gap) : 1;
            return new Shape.Dashed(new[] { P(a, 0.5), P(b, 0.5) }, n > 1 ? dash * k : length, gap * k);
        }
        if (label is null) return new List<Shape> { Run(x0, x1) };
        double half = label.Length * LabelSize * 0.28 + 0.22;
        return new List<Shape> { Run(x0, 4 - half), Run(x1, 4 + half), new Shape.Text(label, P(4, 0.5), LabelSize) };
    }

    private static List<Shape> ArrowHead(double tip) => new() { new Shape.Line(new[] { P(tip - 0.24, 0.68), P(tip, 0.5), P(tip - 0.24, 0.32) }) };

    /// <summary>Target symbol on the right end (centre x 7.58).</summary>
    private static List<Shape> Target(RowEnd end)
    {
        const double cx = 7.58;
        switch (end)
        {
            case RowEnd.Finish: return new() { new Shape.Circle(P(cx, 0.5), 0.26, false), new Shape.Circle(P(cx, 0.5), 0.16, false) };
            case RowEnd.Control: return new() { new Shape.Circle(P(cx, 0.5), 0.26, false) };
            case RowEnd.StartTriangle:
                return new()
                {
                    new Shape.Poly(Enumerable.Range(0, 3).Select(i =>
                    {
                        double a = Math.PI / 2 + i * 2 * Math.PI / 3;
                        return P(cx + 0.3 * Math.Cos(a), 0.44 + 0.3 * Math.Sin(a));
                    }).ToList(), false),
                };
            default: return new();
        }
    }

    /// <summary>"Navigate" ends: control circle with an opening angle on the left, angle into the target on the right.</summary>
    private static List<Shape> NavigateEnds(RowEnd end)
    {
        var result = new List<Shape>
        {
            new Shape.Circle(P(0.42, 0.5), 0.26, false), new Shape.Line(new[] { P(0.98, 0.76), P(0.68, 0.5), P(0.98, 0.24) }),
            new Shape.Line(new[] { P(7.02, 0.76), P(7.32, 0.5), P(7.02, 0.24) }),
        };
        result.AddRange(Target(end));
        return result;
    }

    // MARK: PDF operators

    /// <summary>PDF operators for shapes in the box (x0, y0, size) in pt (y up). Colour follows the graphics state.</summary>
    public static string Pdf(IReadOnlyList<Shape> shapes, double x0, double y0, double size, Func<string, double, double> textWidth)
    {
        string N(double v) => GeoPdfExporter.N(v, 2);
        string Pt(Point2D p) => $"{N(x0 + p.X * size)} {N(y0 + p.Y * size)}";
        var c = new StringBuilder($"q {N(LineWidth * size)} w 1 J 1 j\n");
        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case Shape.Line l when l.Points.Count > 0:
                    c.Append($"{Pt(l.Points[0])} m ").Append(string.Concat(l.Points.Skip(1).Select(p => $"{Pt(p)} l "))).Append("S\n");
                    break;
                case Shape.Poly p when p.Points.Count > 0:
                    c.Append($"{Pt(p.Points[0])} m ").Append(string.Concat(p.Points.Skip(1).Select(q => $"{Pt(q)} l "))).Append(p.Fill ? "h f\n" : "h S\n");
                    break;
                case Shape.Circle ci:
                    c.Append(GeoPdfExporter.CirclePath(Point2D.Zero, ci.R * size, q => $"{N(x0 + ci.Center.X * size + q.X)} {N(y0 + ci.Center.Y * size + q.Y)}")).Append(ci.Fill ? "f\n" : "S\n");
                    break;
                case Shape.Arc a:
                {
                    const int steps = 16;
                    for (int i = 0; i <= steps; i++)
                    {
                        double ang = a.A0 + (a.A1 - a.A0) * i / steps;
                        c.Append(Pt(P(a.Center.X + a.R * Math.Cos(ang), a.Center.Y + a.R * Math.Sin(ang)))).Append(i == 0 ? " m " : " l ");
                    }
                    c.Append("S\n");
                    break;
                }
                case Shape.Dots d:
                    foreach (var dot in d.Points)
                        c.Append(GeoPdfExporter.CirclePath(Point2D.Zero, d.R * size, q => $"{N(x0 + dot.X * size + q.X)} {N(y0 + dot.Y * size + q.Y)}")).Append("f\n");
                    break;
                case Shape.Dashed ds when ds.Points.Count > 0:
                    c.Append($"[{N(ds.Dash * size)} {N(ds.Gap * size)}] 0 d 0 J {Pt(ds.Points[0])} m ").Append(string.Concat(ds.Points.Skip(1).Select(p => $"{Pt(p)} l "))).Append("S [] 0 d 1 J\n");
                    break;
                case Shape.Text t:
                {
                    double fs = t.Size * size;
                    double w = textWidth(t.Value, fs);
                    c.Append($"BT /F1 {N(fs)} Tf {N(x0 + t.At.X * size - w / 2)} {N(y0 + t.At.Y * size - fs * 0.35)} Td ({GeoPdfExporter.PdfString(t.Value)}) Tj ET\n");
                    break;
                }
            }
        }
        return c.Append("Q\n").ToString();
    }
}

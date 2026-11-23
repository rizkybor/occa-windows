using Occa.Core.Geometry;
using Occa.Core.Sheet;

namespace Occa.Core.Courses;

/// <summary>Course symbol (purple) sizes on paper, per ISOM 2017-2 (forest) or ISSprOM 2019-2 (sprint). Port of <c>CourseStyle</c>.</summary>
public readonly record struct CourseStyle(string ColorHex, double LineWidthMM, double ControlDiameterMM, double StartSideMM,
                                          double FinishInnerMM, double FinishOuterMM, double NumberHeightMM)
{
    /// <summary>ISOM 2017-2 §3.7 at 1:15000: control circle ø5.0, start triangle side 6.0, finish ø4.0 / ø6.0, line 0.35, control number Arial 4.0 mm.</summary>
    public static readonly CourseStyle Isom = new("#A626FF", 0.35, 5, 6, 4, 6, 4);
    /// <summary>ISSprOM 2019-2 §4.7 at 1:4000: control circle ø6.0, start 7.0, finish ø5.0 / ø7.0, line 0.35, number 4.0 mm.</summary>
    public static readonly CourseStyle Issprom = new("#A626FF", 0.35, 6, 7, 5, 7, 4);
    /// <summary>ISInOM 6.7.3 (indoor) course print at 1:700: the ISSprOM sizes on paper, enlarged from the 1:700 base scale (§3.1).</summary>
    public static readonly CourseStyle Isinom = Issprom;

    /// <summary>Sprint scales (≤ 1:5000) use ISSprOM, others ISOM; enlarged maps enlarge the course symbols proportionally.</summary>
    public static CourseStyle Standard(int scale) => Standard(scale, MapStandardExtensions.ForScale(scale));

    /// <summary>Course symbols for a map sheet, of the sheet's standard (ISInOM on indoor maps).</summary>
    public static CourseStyle Standard(SheetSpec sheet) => Standard(sheet.ScaleDenominator, sheet.MapStandard);

    /// <summary>Course symbols of <paramref name="standard"/> at the map <paramref name="scale"/>, enlarged from the standard's base scale (never reduced).</summary>
    public static CourseStyle Standard(int scale, MapStandard standard)
    {
        var b = standard switch { MapStandard.Isom => Isom, MapStandard.Issprom => Issprom, _ => Isinom };
        return b.ScaledBy(standard.Enlargement(scale));
    }

    /// <summary>All sizes × k (colour unchanged).</summary>
    public CourseStyle ScaledBy(double k) => new(ColorHex, LineWidthMM * k, ControlDiameterMM * k, StartSideMM * k, FinishInnerMM * k, FinishOuterMM * k, NumberHeightMM * k);

    /// <summary>The control number size is a font size (Arial 4.0 mm); digits are ≈ 0.716 of it tall.</summary>
    public const double DigitHeightPerFontSize = 0.716;
}

/// <summary>
/// Where course overprint shapes go in the map's colour order ("IOF Printing and Colour Definitions" Rev 4 §4, p.6):
/// lower purple below black, blue and brown 100 % (so they don't hide map detail), upper purple on top.
/// </summary>
public enum CourseLevel { Lower, Upper }

/// <summary>Overprint shapes in ground metres (UTM), ready to draw on the map, PDF, or raster (<c>CourseMark</c> on Mac).</summary>
public abstract record CourseMark
{
    public sealed record Circle(Point2D Center, double RadiusM) : CourseMark;
    public sealed record Polyline(IReadOnlyList<Point2D> Points, bool Closed) : CourseMark;
    /// <summary>Text centred at <c>Center</c>, letter height in ground metres.</summary>
    public sealed record Text(string Value, Point2D Center, double HeightM) : CourseMark;
    /// <summary>Polygon filled with the overprint colour at <c>Opacity</c> (out-of-bounds areas etc.).</summary>
    public sealed record Fill(IReadOnlyList<Point2D> Points, double Opacity) : CourseMark;
    /// <summary>Image (file name in the project package) centred at <c>Center</c>, size on the ground. Not rotated (grid north up).</summary>
    public sealed record Image(string Name, Point2D Center, double WidthM, double HeightM) : CourseMark;
    /// <summary>Change colour for the following shapes ("#RRGGBB"); null = back to the overprint colour.</summary>
    public sealed record Color(string? Hex) : CourseMark;
    /// <summary>Font style for the following text (Helvetica / Bold / Oblique / BoldOblique).</summary>
    public sealed record Font(bool Bold, bool Italic) : CourseMark;
    /// <summary>Line width for the following shapes in ground metres (IOF symbols: 708 boundary, 712 cross…); null = the course line.</summary>
    public sealed record Width(double? WidthM) : CourseMark;
    /// <summary>Colour level of the following shapes in the IOF colour order (upper / lower purple); null = back to lower purple.</summary>
    public sealed record Level(CourseLevel? Value) : CourseMark;

    /// <summary>State marks (colour, font, width) change the drawing style; level marks change the colour order; the rest are drawn.</summary>
    public bool IsState => this is Color or Font or Width;
}

/// <summary>The purple course overprint of one course as drawable marks (<c>CourseOverprint</c> on Mac).</summary>
public sealed class CourseOverprint
{
    public List<CourseMark> Marks { get; }
    public double LineWidthM { get; }
    public string ColorHex { get; }

    public CourseOverprint(List<CourseMark> marks, double lineWidthM, string colorHex)
    {
        Marks = marks;
        LineWidthM = lineWidthM;
        ColorHex = colorHex;
    }

    public bool IsEmpty => Marks.Count == 0;

    /// <summary>Gap between a course line and the control circles (paper mm, at the standard 0.35 mm line).</summary>
    public const double LegGapMM = 0.3;

    /// <summary>Bounding box of all shapes (for zoom/dirty rect).</summary>
    public Rect2D? Bounds => Rect2D.FromPoints(Marks.SelectMany(m => m switch
    {
        CourseMark.Circle c => new[] { new Point2D(c.Center.X - c.RadiusM, c.Center.Y - c.RadiusM), new Point2D(c.Center.X + c.RadiusM, c.Center.Y + c.RadiusM) },
        CourseMark.Polyline p => p.Points,
        CourseMark.Text t => TextBox(t),
        CourseMark.Fill f => f.Points,
        CourseMark.Image i => new[] { new Point2D(i.Center.X - i.WidthM / 2, i.Center.Y - i.HeightM / 2), new Point2D(i.Center.X + i.WidthM / 2, i.Center.Y + i.HeightM / 2) },
        _ => Enumerable.Empty<Point2D>(),
    }));

    private static IEnumerable<Point2D> TextBox(CourseMark.Text t)
    {
        double w = Math.Max(t.Value.Length * t.HeightM * 0.35, t.HeightM);
        return new[] { new Point2D(t.Center.X - w, t.Center.Y - t.HeightM), new Point2D(t.Center.X + w, t.Center.Y + t.HeightM) };
    }

    /// <summary>Overprint of one course; <paramref name="course"/> == null = all controls (label = code, no legs), like "All controls".</summary>
    public static CourseOverprint Make(CourseSet set, Course? course, SheetSpec sheet, CourseStyle? style = null)
    {
        var st = style ?? CourseStyle.Standard(sheet);
        // User-chosen sizes (global) on top of the IOF standard; per control multiplied again by sizeFactor.
        var look = set.EffectiveAppearance;
        st = st with
        {
            ControlDiameterMM = st.ControlDiameterMM * look.ControlScale, StartSideMM = st.StartSideMM * look.ControlScale,
            FinishInnerMM = st.FinishInnerMM * look.ControlScale, FinishOuterMM = st.FinishOuterMM * look.ControlScale,
            NumberHeightMM = st.NumberHeightMM * look.NumberScale, LineWidthMM = st.LineWidthMM * look.LineScale,
        };
        double M(double mm) => sheet.GroundMeters(mm);
        var marks = new List<CourseMark>();

        List<ControlPoint> list;
        bool legs;
        var labels = new Dictionary<Guid, string>();
        if (course is not null)
        {
            list = set.ControlsOf(course);
            legs = course.Kind == CourseKind.Normal;
            if (course.Kind == CourseKind.Normal)
            {
                // Controls visited more than once: "3/7".
                int n = 0;
                foreach (var c in list.Where(c => c.Kind == ControlKind.Control))
                {
                    n++;
                    labels[c.Id] = labels.TryGetValue(c.Id, out var prev) ? $"{prev}/{n}" : n.ToString();
                }
            }
            else
            {
                foreach (var c in list.Where(c => c.Kind == ControlKind.Control)) labels[c.Id] = c.Code?.ToString() ?? "?";
            }
        }
        else
        {
            list = set.Controls;
            legs = false;
            foreach (var c in list.Where(c => c.Kind == ControlKind.Control)) labels[c.Id] = c.Code?.ToString() ?? "?";
        }

        // ISOM 705 / ISSprOM: a small gap between the course line and the circles (0.3 mm on paper, enlarged with the map).
        double legGap = M(LegGapMM * st.LineWidthMM / 0.35);
        double Trim(ControlPoint c)
        {
            double k = c.EffectiveSize;
            return c.Kind switch
            {
                ControlKind.Control => M(st.ControlDiameterMM / 2 * k) + legGap,
                ControlKind.Start => M(st.StartSideMM / Math.Sqrt(3) * k) + legGap,
                _ => M(st.FinishOuterMM / 2 * k) + legGap,
            };
        }

        // Incoming/outgoing leg directions per control (for start triangle direction and number placement).
        var directions = new Dictionary<Guid, List<double>>();
        if (legs)
        {
            for (int i = 0; i + 1 < list.Count; i++)
            {
                var a = list[i]; var b = list[i + 1];
                if (a.Id == b.Id) continue;
                double ang = Math.Atan2(b.Position.Y - a.Position.Y, b.Position.X - a.Position.X);
                Dir(directions, a.Id).Add(ang);
                Dir(directions, b.Id).Add(ang + Math.PI);
            }
        }

        var drawn = new HashSet<Guid>();
        foreach (var c in list)
        {
            if (!drawn.Add(c.Id)) continue;
            var p = c.Position;
            double k = c.EffectiveSize;
            switch (c.Kind)
            {
                case ControlKind.Control:
                    marks.Add(new CourseMark.Circle(p, M(st.ControlDiameterMM / 2 * k)));
                    break;
                case ControlKind.Finish:
                    marks.Add(new CourseMark.Circle(p, M(st.FinishInnerMM / 2 * k)));
                    marks.Add(new CourseMark.Circle(p, M(st.FinishOuterMM / 2 * k)));
                    break;
                default:
                {
                    // Equilateral triangle, one corner pointing to the first control (IOF); without legs: north.
                    double heading = directions.TryGetValue(c.Id, out var ds) && ds.Count > 0 ? ds[0] : Math.PI / 2;
                    double r = M(st.StartSideMM / Math.Sqrt(3) * k);
                    var pts = Enumerable.Range(0, 3).Select(i =>
                    {
                        double a = heading + i * 2 * Math.PI / 3;
                        return new Point2D(p.X + r * Math.Cos(a), p.Y + r * Math.Sin(a));
                    }).ToList();
                    marks.Add(new CourseMark.Polyline(pts, true));
                    break;
                }
            }
        }

        if (legs)
        {
            var gaps = course?.LegGaps ?? new List<LegGap>();
            for (int i = 0; i + 1 < list.Count; i++)
            {
                var a = list[i]; var b = list[i + 1];
                if (a.Id == b.Id) continue;
                double d = a.Position.DistanceTo(b.Position);
                double ra = Trim(a), rb = Trim(b);
                if (d <= ra + rb) continue;
                double ux = (b.Position.X - a.Position.X) / d, uy = (b.Position.Y - a.Position.Y) / d;
                Point2D At(double s) => new(a.Position.X + ux * s, a.Position.Y + uy * s);
                // The drawn part [ra, d − rb], minus the leg's gaps (distances from a).
                var pieces = new List<(double A, double B)> { (ra, d - rb) };
                foreach (var g in gaps.Where(g => g.From == a.Id && g.To == b.Id))
                {
                    double centre = g.At * d, half = M(g.LengthMM) / 2;
                    pieces = pieces.SelectMany(pc =>
                    {
                        if (!(centre + half > pc.A && centre - half < pc.B)) return new[] { pc };
                        return new[] { (pc.A, centre - half), (centre + half, pc.B) }.Where(x => x.Item2 - x.Item1 > 1e-6);
                    }).ToList();
                }
                foreach (var pc in pieces) marks.Add(new CourseMark.Polyline(new[] { At(pc.A), At(pc.B) }, false));
            }
        }

        // Number: outside the circle, in the direction farthest from the legs (starting north-east).
        // NumberHeightMM is the font size; text marks carry the digit height.
        double h = M(st.NumberHeightMM * CourseStyle.DigitHeightPerFontSize);
        // 704: lower purple in ISOM 2017-2, upper purple in ISSprOM 2019-2 (and ISInOM).
        marks.Add(new CourseMark.Level(sheet.MapStandard == MapStandard.Isom ? CourseLevel.Lower : CourseLevel.Upper));
        foreach (var c in set.Controls)
        {
            if (!labels.TryGetValue(c.Id, out var text)) continue;
            var used = directions.TryGetValue(c.Id, out var u) ? u : new List<double>();
            var candidates = Enumerable.Range(0, 8).Select(i => Math.PI / 4 - i * Math.PI / 4).ToList();
            double Clearance(double a) => used.Count == 0 ? Math.PI : used.Min(x => Math.Abs(Math.Atan2(Math.Sin(x - a), Math.Cos(x - a))));
            double best = candidates[0];
            double bestClearance = Clearance(best);
            foreach (var cand in candidates.Skip(1))
            {
                double cl = Clearance(cand);
                if (cl > bestClearance) { best = cand; bestClearance = cl; }
            }
            // Text width ≈ 0.6 × height per digit; offset so it does not touch the circle.
            double halfW = text.Length * h * 0.3;
            double reach = Trim(c) + h * 0.25 + Math.Max(Math.Abs(Math.Cos(best)) * halfW, Math.Abs(Math.Sin(best)) * h / 2);
            marks.Add(new CourseMark.Text(text, new Point2D(c.Position.X + reach * Math.Cos(best), c.Position.Y + reach * Math.Sin(best)), h));
        }

        // Extra objects (text, shapes, images): images at the bottom so the course lines stay visible on top.
        var extra = set.ItemsFor(course).SelectMany(i => i.Marks(sheet)).ToList();
        var images = extra.Where(m => m is CourseMark.Image).ToList();
        var others = extra.Where(m => m is not CourseMark.Image).ToList();
        var all = new List<CourseMark>(images.Count + marks.Count + 1 + others.Count);
        all.AddRange(images);
        all.AddRange(marks);
        all.Add(new CourseMark.Level(null));
        all.AddRange(others);
        return new CourseOverprint(all, M(st.LineWidthMM), st.ColorHex);
    }

    private static List<double> Dir(Dictionary<Guid, List<double>> d, Guid id)
    {
        if (!d.TryGetValue(id, out var list)) d[id] = list = new List<double>();
        return list;
    }

    /// <summary>
    /// The overprint split by colour level (see <see cref="CourseLevel"/>): the lower purple part is painted inside the map's colour
    /// order, below black, blue and brown 100 %; the upper purple part on top of the map. This is how the IOF wants a course
    /// printed without a real overprint ("IOF Printing and Colour Definitions" Rev 4 §4). State marks (colour, font, width)
    /// go to both parts, so each part draws with the same styles as the whole. Port of <c>CourseOverprintLevels.swift</c>.
    /// </summary>
    public (CourseOverprint Lower, CourseOverprint Upper) Levels
    {
        get
        {
            var lower = new List<CourseMark>();
            var upper = new List<CourseMark>();
            var level = CourseLevel.Lower;
            foreach (var m in Marks)
            {
                switch (m)
                {
                    case CourseMark.Level l: level = l.Value ?? CourseLevel.Lower; break;
                    case CourseMark.Color or CourseMark.Font or CourseMark.Width:
                        lower.Add(m);
                        upper.Add(m);
                        break;
                    default:
                        (level == CourseLevel.Upper ? upper : lower).Add(m);
                        break;
                }
            }
            List<CourseMark> Drawn(List<CourseMark> list) => list.Any(x => !x.IsState) ? list : new List<CourseMark>();
            return (new CourseOverprint(Drawn(lower), LineWidthM, ColorHex), new CourseOverprint(Drawn(upper), LineWidthM, ColorHex));
        }
    }
}

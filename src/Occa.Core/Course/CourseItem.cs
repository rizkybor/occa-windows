using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Courses;

/// <summary>
/// Extra object on the course map (like Purple Pen / OCAD Course Setting "special items"): text, shapes (rectangle,
/// ellipse, line), JPG/PNG images (logos, sponsors, notes) and the IOF course symbols 702–715. Positioned in UTM, so
/// it moves with the map; text/image size in paper mm. Port of <c>CourseItem</c> + <c>CourseIOFSymbols.swift</c>.
/// </summary>
public sealed class CourseItem
{
    [JsonConverter(typeof(CamelEnumConverter<Kind>))]
    public enum Kind
    {
        Text, Rectangle, Ellipse, Line, Image,
        /// <summary>IOF course symbols (ISOM 2017-2 §3.7 / ISSprOM 2019-2 §4.7), drawn like OCCA's course symbol set.</summary>
        MapIssue, MarkedRoute, OutOfBoundsBoundary, OutOfBoundsArea, CrossingPoint, OutOfBoundsRoute, FirstAid, Refreshment, MapExchange,
    }

    public enum Placement { Tap, Line, Area }

    /// <summary>The IOF course symbols, in code order (for menus).</summary>
    public static readonly Kind[] IofSymbolKinds =
    {
        Kind.MapIssue, Kind.MarkedRoute, Kind.OutOfBoundsBoundary, Kind.OutOfBoundsArea, Kind.CrossingPoint, Kind.OutOfBoundsRoute,
        Kind.FirstAid, Kind.Refreshment, Kind.MapExchange,
    };

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public Kind ItemKind { get; set; }
    /// <summary>Text/image: [centre]. Rectangle/ellipse: [corner, opposite corner]. Line: points (≥ 2).</summary>
    [JsonPropertyName("points")] public List<Point2D> Points { get; set; } = new();
    [JsonPropertyName("text")] public string? Text { get; set; }
    /// <summary>Letter height on paper (mm).</summary>
    [JsonPropertyName("textHeightMM")] public double? TextHeightMM { get; set; }
    /// <summary>Rectangle/ellipse filled with transparent purple (e.g. out-of-bounds area).</summary>
    [JsonPropertyName("filled")] public bool? Filled { get; set; }
    /// <summary>File name in the project package's image folder.</summary>
    [JsonPropertyName("imageName")] public string? ImageName { get; set; }
    /// <summary>Image height / width.</summary>
    [JsonPropertyName("imageAspect")] public double? ImageAspect { get; set; }
    /// <summary>Image width on paper (mm).</summary>
    [JsonPropertyName("widthMM")] public double? WidthMM { get; set; }
    /// <summary>Old version: a single course. Used when <see cref="CourseIds"/> is empty.</summary>
    [JsonPropertyName("courseID")] public Guid? CourseId { get; set; }
    /// <summary>Shown only on these courses; null (and <see cref="CourseId"/> null) = all courses.</summary>
    [JsonPropertyName("courseIDs")] public List<Guid>? CourseIds { get; set; }
    /// <summary>Object colour "#RRGGBB"; null = course colour (purple).</summary>
    [JsonPropertyName("colorHex")] public string? ColorHex { get; set; }
    /// <summary>Text: bold / italic (null = regular).</summary>
    [JsonPropertyName("bold")] public bool? Bold { get; set; }
    [JsonPropertyName("italic")] public bool? Italic { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public CourseItem() { }

    public CourseItem(Kind kind, IEnumerable<Point2D> points, string? text = null, double? textHeightMM = null, bool? filled = null,
                      string? imageName = null, double? imageAspect = null, double? widthMM = null, Guid? courseId = null)
    {
        ItemKind = kind;
        Points = points.ToList();
        Text = text;
        TextHeightMM = textHeightMM;
        Filled = filled;
        ImageName = imageName;
        ImageAspect = imageAspect;
        WidthMM = widthMM;
        CourseId = courseId;
    }

    public const double DefaultTextHeightMM = 4.0;
    /// <summary>Content of new text before the user types.</summary>
    public const string PlaceholderText = "Text";
    public static readonly (double Min, double Max) TextHeightRangeMM = (1.5, 20);
    public const double DefaultImageWidthMM = 30.0;
    public static readonly (double Min, double Max) ImageWidthRangeMM = (5, 200);
    /// <summary>Quick colours for layout design (besides the free colour picker).</summary>
    public static readonly string[] Swatches = { "#A626FF", "#000000", "#E8202A", "#0072CE", "#00A651", "#F7941D", "#FFFFFF" };

    [JsonIgnore] public double EffectiveTextHeightMM => TextHeightMM ?? DefaultTextHeightMM;
    [JsonIgnore] public bool IsFilled => Filled ?? false;

    /// <summary>Font size (pt) for letter height <paramref name="mm"/> (cap height ≈ 0.72 em, 1 pt = 0.3528 mm).</summary>
    public static double PointsForTextHeightMM(double mm) => mm / 0.72 / (25.4 / 72);

    /// <summary>Courses on which the object is shown; null = all courses.</summary>
    [JsonIgnore]
    public List<Guid>? Scope
    {
        get
        {
            if (CourseIds is { Count: > 0 } ids) return ids;
            return CourseId is Guid c ? new List<Guid> { c } : null;
        }
    }

    /// <summary>Set the courses on which the object is shown (null or empty = all courses).</summary>
    public void SetScope(List<Guid>? ids)
    {
        CourseId = null;
        CourseIds = ids is { Count: > 0 } ? ids : null;
    }

    /// <summary>Shown on <paramref name="course"/>? (null = the all-controls view: all objects are shown.)</summary>
    public bool Shows(Course? course) => course is null || Scope is null || Scope.Contains(course.Id);

    [JsonIgnore] public string DisplayName => ItemKind == Kind.Text ? (string.IsNullOrEmpty(Text) ? "Text" : Text!) : ItemKind.Label();

    public CourseItem MovedBy(Point2D d)
    {
        var c = Clone();
        c.Points = Points.Select(p => p + d).ToList();
        return c;
    }

    public CourseItem Clone()
    {
        var c = (CourseItem)MemberwiseClone();
        c.Points = new List<Point2D>(Points);
        c.CourseIds = CourseIds is null ? null : new List<Guid>(CourseIds);
        return c;
    }

    /// <summary>Text/image box size on the ground (m): (width, height).</summary>
    public (double W, double H) BoxSizeM(SheetSpec sheet)
    {
        switch (ItemKind)
        {
            case Kind.Text:
            {
                double h = sheet.GroundMeters(EffectiveTextHeightMM);
                var lines = (Text ?? "").Split('\n');
                int longest = lines.Length == 0 ? 1 : lines.Max(l => l.Length);
                return (Math.Max(longest, 1) * h * 0.6, h * 1.25 * Math.Max(lines.Length, 1));
            }
            case Kind.Image:
            {
                double w = sheet.GroundMeters(WidthMM ?? DefaultImageWidthMM);
                return (w, w * (ImageAspect ?? 1));
            }
            default:
                return (0, 0);
        }
    }

    /// <summary>Outline points of a rectangle/ellipse/line (ellipse: 72 points).</summary>
    [JsonIgnore]
    public List<Point2D> Outline
    {
        get
        {
            switch (ItemKind)
            {
                case Kind.Rectangle:
                case Kind.OutOfBoundsArea:
                {
                    if (Points.Count < 2) return new List<Point2D>();
                    var a = Points[0]; var b = Points[1];
                    return new List<Point2D> { a, new(b.X, a.Y), b, new(a.X, b.Y) };
                }
                case Kind.Ellipse:
                {
                    if (Points.Count < 2) return new List<Point2D>();
                    var a = Points[0]; var b = Points[1];
                    var c = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    double rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                    return Enumerable.Range(0, 72).Select(i =>
                    {
                        double t = i / 72.0 * 2 * Math.PI;
                        return new Point2D(c.X + rx * Math.Cos(t), c.Y + ry * Math.Sin(t));
                    }).ToList();
                }
                case Kind.Line:
                case Kind.MarkedRoute:
                case Kind.OutOfBoundsBoundary:
                case Kind.OutOfBoundsRoute:
                    return new List<Point2D>(Points);
                default:
                    return new List<Point2D>();
            }
        }
    }

    public Rect2D? Bounds(SheetSpec sheet)
    {
        switch (ItemKind)
        {
            case Kind.Text:
            case Kind.Image:
            {
                if (Points.Count == 0) return null;
                var c = Points[0];
                var s = BoxSizeM(sheet);
                return new Rect2D(c.X - s.W / 2, c.Y - s.H / 2, c.X + s.W / 2, c.Y + s.H / 2);
            }
            case Kind.MapIssue:
            case Kind.CrossingPoint:
            case Kind.FirstAid:
            case Kind.Refreshment:
            case Kind.MapExchange:
            {
                if (Points.Count == 0) return null;
                var c = Points[0];
                double r = IofSymbolRadiusM(sheet);
                return new Rect2D(c.X - r, c.Y - r, c.X + r, c.Y + r);
            }
            default:
                return Rect2D.FromPoints(Outline);
        }
    }

    /// <summary>Hit distance (m): 0 inside text/images/filled shapes, otherwise the distance to the outline.</summary>
    public double HitDistance(Point2D p, SheetSpec sheet)
    {
        switch (ItemKind)
        {
            case Kind.Text:
            case Kind.Image:
            {
                if (Bounds(sheet) is not { } b) return double.PositiveInfinity;
                double dx = Math.Max(Math.Max(b.MinX - p.X, 0), p.X - b.MaxX), dy = Math.Max(Math.Max(b.MinY - p.Y, 0), p.Y - b.MaxY);
                return Math.Sqrt(dx * dx + dy * dy);
            }
            case Kind.MapIssue:
            case Kind.CrossingPoint:
            case Kind.FirstAid:
            case Kind.Refreshment:
            case Kind.MapExchange:
                if (Points.Count == 0) return double.PositiveInfinity;
                return Math.Max(0, p.DistanceTo(Points[0]) - IofSymbolRadiusM(sheet));
            default:
            {
                var pts = Outline;
                if (pts.Count < 2) return double.PositiveInfinity;
                bool closed = ItemKind.Placement() == Placement.Area;
                if (closed && (IsFilled || ItemKind == Kind.OutOfBoundsArea) && Contains(pts, p)) return 0;
                double best = double.PositiveInfinity;
                int n = closed ? pts.Count : pts.Count - 1;
                for (int i = 0; i < n; i++) best = Math.Min(best, p.DistanceToSegment(pts[i], pts[(i + 1) % pts.Count]));
                return best;
            }
        }
    }

    /// <summary>Even-odd point in polygon.</summary>
    public static bool Contains(IReadOnlyList<Point2D> poly, Point2D p)
    {
        bool inside = false;
        int j = poly.Count - 1;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            j = i;
        }
        return inside;
    }

    /// <summary>
    /// Overprint shapes of this object (with its own colour if set). IOF course symbols carry their own upper / lower
    /// purple levels; text, shapes and images are layout objects drawn on top of the map (upper level).
    /// </summary>
    public List<CourseMark> Marks(SheetSpec sheet)
    {
        var body = ShapeMarks(sheet);
        if (body.Count > 0 && !ItemKind.IsIofSymbol())
        {
            body.Insert(0, new CourseMark.Level(CourseLevel.Upper));
            body.Add(new CourseMark.Level(null));
        }
        if (ItemKind == Kind.Text && body.Count > 0 && (Bold == true || Italic == true))
        {
            body.Insert(0, new CourseMark.Font(Bold == true, Italic == true));
            body.Add(new CourseMark.Font(false, false));
        }
        if (ColorHex is null || body.Count == 0 || ItemKind == Kind.Image) return body;
        body.Insert(0, new CourseMark.Color(ColorHex));
        body.Add(new CourseMark.Color(null));
        return body;
    }

    private List<CourseMark> ShapeMarks(SheetSpec sheet)
    {
        switch (ItemKind)
        {
            case Kind.Text:
            {
                if (Points.Count == 0 || string.IsNullOrEmpty(Text)) return new List<CourseMark>();
                var c = Points[0];
                double h = sheet.GroundMeters(EffectiveTextHeightMM);
                var lines = Text.Split('\n');
                // Lines spaced 1.25 × letter height, block centred on the point.
                double top = c.Y + (lines.Length - 1) * h * 1.25 / 2;
                var marks = new List<CourseMark>();
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Length > 0) marks.Add(new CourseMark.Text(lines[i], new Point2D(c.X, top - i * h * 1.25), h));
                return marks;
            }
            case Kind.Image:
            {
                if (Points.Count == 0 || ImageName is null) return new List<CourseMark>();
                var s = BoxSizeM(sheet);
                return new List<CourseMark> { new CourseMark.Image(ImageName, Points[0], s.W, s.H) };
            }
            case Kind.Rectangle:
            case Kind.Ellipse:
            {
                var pts = Outline;
                if (pts.Count < 3) return new List<CourseMark>();
                var marks = new List<CourseMark>();
                if (IsFilled) marks.Add(new CourseMark.Fill(pts, 0.35));
                marks.Add(new CourseMark.Polyline(pts, true));
                return marks;
            }
            case Kind.Line:
                return Points.Count >= 2 ? new List<CourseMark> { new CourseMark.Polyline(new List<Point2D>(Points), false) } : new List<CourseMark>();
            default:
                return IofMarks(sheet);
        }
    }

    // MARK: IOF course symbols (CourseIOFSymbols.swift)

    private static readonly Lazy<Dictionary<string, MapSymbol>> IsomSymbols = new(() => Table(StandardSymbolSets.Isom));
    private static readonly Lazy<Dictionary<string, MapSymbol>> SprintSymbols = new(() => Table(StandardSymbolSets.Issprom));
    private static readonly Lazy<Dictionary<string, MapSymbol>> IndoorSymbols = new(() => Table(StandardSymbolSets.Isinom));

    private static Dictionary<string, MapSymbol> Table(OccaSymbolFile file)
    {
        var d = new Dictionary<string, MapSymbol>();
        foreach (var s in file.Symbols) d.TryAdd(s.Code, s);
        return d;
    }

    /// <summary>OCCA's course symbols per standard; ISSprOM / ISInOM lack some (711–713), the ISOM shapes are used there.</summary>
    internal static MapSymbol? IofSymbol(IReadOnlyList<string> codes, MapStandard standard)
    {
        var tables = standard switch
        {
            MapStandard.Isom => new[] { IsomSymbols.Value },
            MapStandard.Issprom => new[] { SprintSymbols.Value, IsomSymbols.Value },
            _ => new[] { IndoorSymbols.Value, SprintSymbols.Value, IsomSymbols.Value },
        };
        foreach (var t in tables) foreach (var code in codes) if (t.TryGetValue(code, out var s)) return s;
        return null;
    }

    /// <summary>The symbol drawn for an IOF course object kind on a map with <paramref name="sheet"/> (for previews).</summary>
    public static MapSymbol? IofSymbolFor(Kind kind, SheetSpec sheet)
    {
        var codes = IofCodes(kind);
        return codes.Length == 0 ? null : IofSymbol(codes, sheet.MapStandard);
    }

    private static string[] IofCodes(Kind kind) => kind switch
    {
        Kind.MapIssue => new[] { "702.0" },
        Kind.MarkedRoute => new[] { "707.0" },
        Kind.OutOfBoundsBoundary => new[] { "708.0" },
        Kind.OutOfBoundsArea => new[] { "709.0" },
        Kind.CrossingPoint => new[] { "710.0", "710.1" },
        Kind.OutOfBoundsRoute => new[] { "711.0" },
        Kind.FirstAid => new[] { "712.0" },
        Kind.Refreshment => new[] { "713.0" },
        Kind.MapExchange => new[] { "715.0" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Ground metres per paper mm for course symbols (map scale × the sheet standard's enlargement; indoor 1:700 = 1).</summary>
    internal static double CourseMetersPerMM(SheetSpec sheet) => sheet.GroundMeters(1) * sheet.SymbolEnlargement;

    /// <summary>Half the size of a point course symbol on the ground (for selection).</summary>
    internal double IofSymbolRadiusM(SheetSpec sheet)
    {
        double d = IofSymbol(IofCodes(ItemKind), sheet.MapStandard)?.PointElements?.DiameterMM() ?? 4;
        return Math.Max(d, 2) / 2 * CourseMetersPerMM(sheet);
    }

    /// <summary>Upper / lower purple level of a colour priority in OCCA's course symbols of <paramref name="standard"/>.</summary>
    internal static CourseLevel LevelOf(int? priority, MapStandard standard)
    {
        if (priority is not int p) return CourseLevel.Lower;
        if (standard == MapStandard.Isinom)
        {
            // ISInOM: everything above lower purple (n52) in its table is drawn on top.
            return p < ColorHierarchy.LowerPurplePriority(MapStandard.Isinom) ? CourseLevel.Upper : CourseLevel.Lower;
        }
        return p == ColorHierarchy.UpperPurplePriority() ? CourseLevel.Upper : CourseLevel.Lower;
    }

    private List<CourseMark> IofMarks(SheetSpec sheet)
    {
        double mpm = CourseMetersPerMM(sheet);
        var standard = sheet.MapStandard;
        var sym = IofSymbol(IofCodes(ItemKind), standard);
        if (sym is null) return new List<CourseMark>();
        var lvl = new CourseMark.Level(LevelOf(sym.ColorPriority, standard));
        switch (ItemKind.Placement())
        {
            case Placement.Tap:
            {
                if (Points.Count == 0) return new List<CourseMark>();
                var marks = PointMarks(sym.PointElements ?? new List<PointElement>(), Points[0], mpm, standard);
                marks.Add(new CourseMark.Level(null));
                return marks;
            }
            case Placement.Line:
            {
                if (Points.Count < 2) return new List<CourseMark>();
                if (ItemKind == Kind.OutOfBoundsRoute && IofSymbol(new[] { "711.0" }, standard)?.PointElements is { } cross)
                {
                    // Crosses 3.0 mm every 5 mm along the route (ISOM: 4–6 mm apart); no line.
                    var marks = Positions(Points, 5 * mpm).SelectMany(p => PointMarks(cross, p, mpm, standard)).ToList();
                    marks.Add(new CourseMark.Level(null));
                    return marks;
                }
                double w = (sym.LineStyle?.WidthMM ?? sym.DrawingWidthMM) * mpm;
                if (Feature.ValidDash(sym.LineStyle?.DashMM) is { } dash)
                {
                    var marks = new List<CourseMark> { lvl, new CourseMark.Width(w) };
                    marks.AddRange(Dashes(Points, dash.Select(v => v * mpm).ToList()).Select(d => (CourseMark)new CourseMark.Polyline(d, false)));
                    marks.Add(new CourseMark.Width(null));
                    marks.Add(new CourseMark.Level(null));
                    return marks;
                }
                return new List<CourseMark> { lvl, new CourseMark.Width(w), new CourseMark.Polyline(new List<Point2D>(Points), false), new CourseMark.Width(null), new CourseMark.Level(null) };
            }
            default:
            {
                var poly = Outline;
                if (poly.Count < 3 || Rect2D.FromPoints(poly) is not { } b) return new List<CourseMark>();
                var marks = new List<CourseMark> { lvl };
                // Cross-hatching 0.2 @ 1.2 mm at ±45°, clipped to the area outline, then the solid boundary line (709.1).
                foreach (var p in sym.Patterns ?? new List<AreaPattern>())
                {
                    var lines = SymbolOrnaments.PatternMarks(p, b, mpm, yUp: true, maxMarks: 5000);
                    if (lines is null) continue;
                    marks.Add(new CourseMark.Width(p.SizeMM * mpm));
                    foreach (var m in lines)
                    {
                        if (m is not OrnamentMark.Line l) continue;
                        foreach (var seg in ClipToPolygon(l.A, l.B, poly)) marks.Add(new CourseMark.Polyline(new[] { seg.A, seg.B }, false));
                    }
                }
                if (IofSymbol(new[] { "709.1", "709.4" }, standard) is { } edge)
                {
                    marks.Add(new CourseMark.Level(LevelOf(edge.ColorPriority, standard)));
                    marks.Add(new CourseMark.Width((edge.LineStyle?.WidthMM ?? 0.25) * mpm));
                    marks.Add(new CourseMark.Polyline(poly, true));
                }
                marks.Add(new CourseMark.Width(null));
                marks.Add(new CourseMark.Level(null));
                return marks;
            }
        }
    }

    /// <summary>Point symbol elements (paper mm, y up) as overprint marks at <paramref name="c"/>, each at its upper / lower purple level.</summary>
    internal static List<CourseMark> PointMarks(IReadOnlyList<PointElement> elements, Point2D c, double mpm, MapStandard standard = MapStandard.Isom)
    {
        var result = new List<CourseMark>();
        Point2D At(Point2D p) => new(c.X + p.X * mpm, c.Y + p.Y * mpm);
        foreach (var e in elements.DrawingOrder())
        {
            var o = e.Points.Count > 0 ? e.Points[0] : Point2D.Zero;
            result.Add(new CourseMark.Level(LevelOf(e.Priority, standard)));
            switch (e.ElementKind)
            {
                case PointElement.Kind.Line:
                    result.Add(new CourseMark.Width(e.LineWidthMM * mpm));
                    result.Add(new CourseMark.Polyline(e.Points.Select(At).ToList(), false));
                    break;
                case PointElement.Kind.Circle:
                    result.Add(new CourseMark.Width(e.LineWidthMM * mpm));
                    result.Add(new CourseMark.Circle(At(o), e.DiameterMM / 2 * mpm));
                    break;
                case PointElement.Kind.Dot:
                {
                    double r = e.DiameterMM / 2 * mpm;
                    var cc = At(o);
                    result.Add(new CourseMark.Fill(Enumerable.Range(0, 24).Select(i =>
                    {
                        double t = i / 24.0 * 2 * Math.PI;
                        return new Point2D(cc.X + r * Math.Cos(t), cc.Y + r * Math.Sin(t));
                    }).ToList(), 1));
                    break;
                }
                default:
                    result.Add(new CourseMark.Fill(e.Points.Select(At).ToList(), 1));
                    break;
            }
        }
        result.Add(new CourseMark.Width(null));
        return result;
    }

    /// <summary>Positions every <paramref name="step"/> along a polyline, starting half a step in.</summary>
    internal static List<Point2D> Positions(IReadOnlyList<Point2D> pts, double step)
    {
        var result = new List<Point2D>();
        if (step <= 0) return result;
        double next = step / 2, walked = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double l = a.DistanceTo(b);
            if (l <= 0) continue;
            while (next <= walked + l && result.Count < 2000)
            {
                double t = (next - walked) / l;
                result.Add(new Point2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                next += step;
            }
            walked += l;
        }
        return result;
    }

    /// <summary>A polyline cut into dashes (<paramref name="dash"/> on, <paramref name="gap"/> off).</summary>
    internal static List<List<Point2D>> Dashes(IReadOnlyList<Point2D> pts, double dash, double gap)
        => dash > 0 && gap > 0 ? Dashes(pts, new List<double> { dash, gap }) : new List<List<Point2D>> { new(pts) };

    /// <summary>A polyline cut into dashes by a dash pattern [dash, gap, dash, gap…] (2 or 4 values), in the polyline's units.</summary>
    internal static List<List<Point2D>> Dashes(IReadOnlyList<Point2D> pts, List<double> pattern)
    {
        if (Feature.ValidDash(pattern) is null) return new List<List<Point2D>> { new(pts) };
        var result = new List<List<Point2D>>();
        var current = new List<Point2D>();
        int k = 0;
        double left = pattern[0];
        bool On() => k % 2 == 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            var p = a;
            double l = a.DistanceTo(b);
            if (l <= 0) continue;
            double ux = (b.X - a.X) / l, uy = (b.Y - a.Y) / l;
            if (On() && current.Count == 0) current = new List<Point2D> { p };
            while (l >= left && result.Count < 5000)
            {
                p = new Point2D(p.X + ux * left, p.Y + uy * left);
                l -= left;
                if (On()) { current.Add(p); result.Add(current); current = new List<Point2D>(); }
                else current = new List<Point2D> { p };
                k = (k + 1) % pattern.Count;
                left = pattern[k];
            }
            left -= l;
            if (On()) { if (current.Count == 0) current = new List<Point2D> { p }; current.Add(b); }
        }
        if (On() && current.Count >= 2) result.Add(current);
        // A route ending exactly at the end of a gap leaves a zero-length dash.
        return result.Where(d => Enumerable.Range(0, d.Count - 1).Sum(i => d[i].DistanceTo(d[i + 1])) > 1e-9).ToList();
    }

    /// <summary>The parts of segment a–b inside polygon <paramref name="poly"/> (even-odd rule): 709 hatching follows the area outline.</summary>
    internal static List<(Point2D A, Point2D B)> ClipToPolygon(Point2D a, Point2D b, IReadOnlyList<Point2D> poly)
    {
        var result = new List<(Point2D, Point2D)>();
        if (poly.Count < 3) return result;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var ts = new List<double> { 0, 1 };
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Count];
            double ex = q.X - p.X, ey = q.Y - p.Y;
            double den = dx * ey - dy * ex;
            if (Math.Abs(den) <= 1e-15) continue;
            double t = ((p.X - a.X) * ey - (p.Y - a.Y) * ex) / den;
            double u = ((p.X - a.X) * dy - (p.Y - a.Y) * dx) / den;
            if (t > 0 && t < 1 && u >= 0 && u <= 1) ts.Add(t);
        }
        ts.Sort();
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            double t0 = ts[i], t1 = ts[i + 1];
            if (t1 - t0 <= 1e-12) continue;
            double m = (t0 + t1) / 2;
            if (!Contains(poly, new Point2D(a.X + dx * m, a.Y + dy * m))) continue;
            result.Add((new Point2D(a.X + dx * t0, a.Y + dy * t0), new Point2D(a.X + dx * t1, a.Y + dy * t1)));
        }
        return result;
    }

    /// <summary>Segment a–b clipped to rectangle <paramref name="r"/> (Liang–Barsky); null if outside.</summary>
    internal static (Point2D A, Point2D B)? ClipToRect(Point2D a, Point2D b, Rect2D r)
    {
        double t0 = 0, t1 = 1;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - r.MinX), (dx, r.MaxX - a.X), (-dy, a.Y - r.MinY), (dy, r.MaxY - a.Y) })
        {
            if (p == 0) { if (q < 0) return null; continue; }
            double t = q / p;
            if (p < 0) { if (t > t1) return null; t0 = Math.Max(t0, t); }
            else { if (t < t0) return null; t1 = Math.Min(t1, t); }
        }
        return (new Point2D(a.X + dx * t0, a.Y + dy * t0), new Point2D(a.X + dx * t1, a.Y + dy * t1));
    }
}

public static class CourseItemKindExtensions
{
    public static string Label(this CourseItem.Kind k) => k switch
    {
        CourseItem.Kind.Text => "Text",
        CourseItem.Kind.Rectangle => "Rectangle",
        CourseItem.Kind.Ellipse => "Ellipse",
        CourseItem.Kind.Line => "Line",
        CourseItem.Kind.Image => "Image",
        CourseItem.Kind.MapIssue => "702 Map issue point",
        CourseItem.Kind.MarkedRoute => "707 Marked route",
        CourseItem.Kind.OutOfBoundsBoundary => "708 Out-of-bounds boundary",
        CourseItem.Kind.OutOfBoundsArea => "709 Out-of-bounds area",
        CourseItem.Kind.CrossingPoint => "710 Crossing point",
        CourseItem.Kind.OutOfBoundsRoute => "711 Out-of-bounds route",
        CourseItem.Kind.FirstAid => "712 First aid post",
        CourseItem.Kind.Refreshment => "713 Refreshment point",
        _ => "715 Map exchange",
    };

    /// <summary>Segoe Fluent Icons glyph for menus (the Mac uses SF Symbols).</summary>
    public static string Glyph(this CourseItem.Kind k) => k switch
    {
        CourseItem.Kind.Text => "",
        CourseItem.Kind.Rectangle => "",
        CourseItem.Kind.Ellipse => "",
        CourseItem.Kind.Line => "",
        CourseItem.Kind.Image => "",
        CourseItem.Kind.MapIssue => "",
        CourseItem.Kind.MarkedRoute => "",
        CourseItem.Kind.OutOfBoundsBoundary => "",
        CourseItem.Kind.OutOfBoundsArea => "",
        CourseItem.Kind.CrossingPoint => "",
        CourseItem.Kind.OutOfBoundsRoute => "",
        CourseItem.Kind.FirstAid => "",
        CourseItem.Kind.Refreshment => "",
        _ => "",
    };

    /// <summary>How a new object is placed: tap (point symbols), drag a line, drag a rectangle.</summary>
    public static CourseItem.Placement Placement(this CourseItem.Kind k) => k switch
    {
        CourseItem.Kind.Text or CourseItem.Kind.Image or CourseItem.Kind.MapIssue or CourseItem.Kind.CrossingPoint or CourseItem.Kind.FirstAid
            or CourseItem.Kind.Refreshment or CourseItem.Kind.MapExchange => CourseItem.Placement.Tap,
        CourseItem.Kind.Line or CourseItem.Kind.MarkedRoute or CourseItem.Kind.OutOfBoundsBoundary or CourseItem.Kind.OutOfBoundsRoute => CourseItem.Placement.Line,
        _ => CourseItem.Placement.Area,
    };

    public static bool IsIofSymbol(this CourseItem.Kind k) => CourseItem.IofSymbolKinds.Contains(k);
}

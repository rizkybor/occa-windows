using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;

namespace Occa.Core.Courses;

/// <summary>
/// Course planner (like Purple Pen / OCAD Course Setting): controls are shared by many courses, a course = a sequence
/// of controls. Positions are in the project CRS (UTM, metres). Port of <c>Course.swift</c> (CakraCore); the JSON is
/// the <c>courses.json</c> of the .cakra package and stays byte-compatible with Mac/iPad.
/// </summary>
[JsonConverter(typeof(CamelEnumConverter<ControlKind>))]
public enum ControlKind { Start, Control, Finish }

public static class ControlKindExtensions
{
    public static string Label(this ControlKind k) => k switch { ControlKind.Start => "Start", ControlKind.Control => "Control", _ => "Finish" };
    public static string RawValue(this ControlKind k) => k switch { ControlKind.Start => "start", ControlKind.Control => "control", _ => "finish" };
}

/// <summary>One control point (start, control, or finish) on the map (<c>ControlPoint</c> on Mac).</summary>
public sealed class ControlPoint
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public ControlKind Kind { get; set; }
    /// <summary>Control code (31–255 per SPORTident/Emit punching units); null for start/finish.</summary>
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("position")] public Point2D Position { get; set; }
    [JsonPropertyName("description")] public ControlDescription Description { get; set; } = new();
    /// <summary>Size multiplier for this control's symbol (null = 100 %, IOF standard size).</summary>
    [JsonPropertyName("sizeFactor")] public double? SizeFactor { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ControlPoint() { }

    public ControlPoint(ControlKind kind, Point2D position, int? code = null, ControlDescription? description = null)
    {
        Kind = kind;
        Position = position;
        Code = code;
        Description = description ?? new ControlDescription();
    }

    [JsonIgnore] public double EffectiveSize => SizeFactor ?? 1;
    public static readonly (double Min, double Max) SizeRange = (0.5, 2);

    /// <summary>Short label: "31", "S1", "F1".</summary>
    public string Label(CourseSet set)
    {
        switch (Kind)
        {
            case ControlKind.Control: return Code?.ToString() ?? "?";
            case ControlKind.Start: return "S" + (IndexAmong(set, ControlKind.Start) + 1);
            default: return "F" + (IndexAmong(set, ControlKind.Finish) + 1);
        }
    }

    private int IndexAmong(CourseSet set, ControlKind kind)
    {
        int i = set.Controls.Where(c => c.Kind == kind).ToList().FindIndex(c => c.Id == Id);
        return Math.Max(i, 0);
    }

    /// <summary>Id for IOF XML (<c>&lt;Id&gt;</c>): the control code, or S1/F1.</summary>
    public string IofId(CourseSet set) => Label(set);

    public ControlPoint Clone() => new()
    {
        Id = Id, Kind = Kind, Code = Code, Position = Position, Description = Description with { }, SizeFactor = SizeFactor, Extra = Extra,
    };
}

[JsonConverter(typeof(CamelEnumConverter<CourseKind>))]
public enum CourseKind
{
    /// <summary>Fixed order: start → controls 1…n → finish.</summary>
    Normal,
    /// <summary>Score/rogaining: free order, controls are numbered by their code.</summary>
    Score,
}

public static class CourseKindExtensions
{
    public static string Label(this CourseKind k) => k == CourseKind.Normal ? "Normal" : "Score";
}

/// <summary>Course: control sequence + competitor data (<c>Course</c> on Mac).</summary>
public sealed class Course
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public CourseKind Kind { get; set; } = CourseKind.Normal;
    /// <summary>Ordered control ids, including start (first) and finish (last) if present.</summary>
    [JsonPropertyName("controlIDs")] public List<Guid> ControlIds { get; set; } = new();
    /// <summary>Estimated number of competitors (for computing control load, like "Competitor load" in Purple Pen).</summary>
    [JsonPropertyName("competitors")] public int Competitors { get; set; }
    /// <summary>Total climb (m), entered manually as in Purple Pen.</summary>
    [JsonPropertyName("climbM")] public int? ClimbM { get; set; }
    /// <summary>Gaps cut into the course line (Purple Pen "Add gap"), e.g. so a leg doesn't cover an important map detail.</summary>
    [JsonPropertyName("legGaps")] public List<LegGap>? LegGaps { get; set; }
    /// <summary>Classes running this course (optional heading line of the description sheet, e.g. "M45 M50 W21").</summary>
    [JsonPropertyName("classes")] public string? Classes { get; set; }
    /// <summary>IOF 14.1: distance from the timed start to the start triangle (m); null = no line.</summary>
    [JsonPropertyName("startDistanceM")] public int? StartDistanceM { get; set; }
    /// <summary>IOF 15.x special instruction rows between the controls.</summary>
    [JsonPropertyName("specialInstructions")] public List<SpecialInstruction>? SpecialInstructions { get; set; }
    /// <summary>IOF 16.x route from the last control to the finish; null = 16.3 (navigate, no tapes).</summary>
    [JsonPropertyName("finishRoute")] public FinishRoute? FinishRoute { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public Course() { }

    public Course(string name, IEnumerable<Guid>? controlIds = null, CourseKind kind = CourseKind.Normal, int competitors = 0, int? climbM = null)
    {
        Name = name;
        Kind = kind;
        ControlIds = controlIds?.ToList() ?? new List<Guid>();
        Competitors = competitors;
        ClimbM = climbM;
    }

    [JsonIgnore] public FinishRoute EffectiveFinishRoute => FinishRoute ?? global::Occa.Core.Courses.FinishRoute.Navigate;
    [JsonIgnore] public IReadOnlyList<SpecialInstruction> AllSpecialInstructions => SpecialInstructions ?? (IReadOnlyList<SpecialInstruction>)Array.Empty<SpecialInstruction>();

    public Course Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, ControlIds = new List<Guid>(ControlIds), Competitors = Competitors, ClimbM = ClimbM,
        LegGaps = LegGaps?.Select(g => g with { }).ToList(), Classes = Classes, StartDistanceM = StartDistanceM,
        SpecialInstructions = SpecialInstructions?.Select(s => s with { }).ToList(), FinishRoute = FinishRoute, Extra = Extra,
    };
}

/// <summary>IOF special instruction kinds 15.1–15.6 (raw values "15.1"… in JSON).</summary>
[JsonConverter(typeof(SpecialInstructionKindConverter))]
public enum SpecialInstructionKind { TapedAway, TapedBetween, CrossingPoint, OutOfBoundsPassage, TapedToMapExchange, MapFlip }

public static class SpecialInstructionKindExtensions
{
    public static readonly SpecialInstructionKind[] All =
    {
        SpecialInstructionKind.TapedAway, SpecialInstructionKind.TapedBetween, SpecialInstructionKind.CrossingPoint,
        SpecialInstructionKind.OutOfBoundsPassage, SpecialInstructionKind.TapedToMapExchange, SpecialInstructionKind.MapFlip,
    };

    public static string RawValue(this SpecialInstructionKind k) => k switch
    {
        SpecialInstructionKind.TapedAway => "15.1",
        SpecialInstructionKind.TapedBetween => "15.2",
        SpecialInstructionKind.CrossingPoint => "15.3",
        SpecialInstructionKind.OutOfBoundsPassage => "15.4",
        SpecialInstructionKind.TapedToMapExchange => "15.5",
        _ => "15.6",
    };

    public static SpecialInstructionKind? FromRaw(string raw) => All.Cast<SpecialInstructionKind?>().FirstOrDefault(k => k!.Value.RawValue() == raw);

    /// <summary>Short label for menus.</summary>
    public static string Label(this SpecialInstructionKind k) => k switch
    {
        SpecialInstructionKind.TapedAway => "Taped route away from control",
        SpecialInstructionKind.TapedBetween => "Taped route between controls",
        SpecialInstructionKind.CrossingPoint => "Mandatory crossing point",
        SpecialInstructionKind.OutOfBoundsPassage => "Passage through out of bounds area",
        SpecialInstructionKind.TapedToMapExchange => "Taped route to map exchange",
        _ => "Map flip",
    };

    /// <summary>Whether the symbol carries a route length.</summary>
    public static bool HasLength(this SpecialInstructionKind k) => k is SpecialInstructionKind.TapedAway or SpecialInstructionKind.TapedBetween or SpecialInstructionKind.TapedToMapExchange;

    /// <summary>English text as in the specification.</summary>
    public static string Text(this SpecialInstructionKind k, int? meters)
    {
        var m = meters is int v ? $" {v} m" : "";
        return k switch
        {
            SpecialInstructionKind.TapedAway => $"Follow taped route{m} away from control",
            SpecialInstructionKind.TapedBetween => $"Follow taped route{m} between controls",
            SpecialInstructionKind.CrossingPoint => "Mandatory crossing point",
            SpecialInstructionKind.OutOfBoundsPassage => "Mandatory passage through out of bounds area",
            SpecialInstructionKind.TapedToMapExchange => $"Follow taped route{m} to map exchange",
            _ => "Map flip (turn the map over)",
        };
    }
}

public sealed class SpecialInstructionKindConverter : JsonConverter<SpecialInstructionKind>
{
    public override SpecialInstructionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => SpecialInstructionKindExtensions.FromRaw(reader.GetString() ?? "") ?? throw new JsonException("Unknown special instruction kind.");

    public override void Write(Utf8JsonWriter writer, SpecialInstructionKind value, JsonSerializerOptions options) => writer.WriteStringValue(value.RawValue());
}

/// <summary>IOF special instruction (15.1–15.6): a full-width row between control descriptions.</summary>
public sealed record SpecialInstruction
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("kind")] public SpecialInstructionKind Kind { get; set; }
    /// <summary>Position in the course: after the n-th control (0 = right after the start).</summary>
    [JsonPropertyName("afterControl")] public int AfterControl { get; set; }
    /// <summary>Route length (m) for 15.1, 15.2, 15.5.</summary>
    [JsonPropertyName("lengthM")] public int? LengthM { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public SpecialInstruction() { }

    public SpecialInstruction(SpecialInstructionKind kind, int afterControl, int? lengthM = null)
    {
        Kind = kind;
        AfterControl = afterControl;
        LengthM = lengthM;
    }
}

/// <summary>IOF 16.x: nature of the route from the last control to the finish (raw values "16.1"–"16.3").</summary>
[JsonConverter(typeof(FinishRouteConverter))]
public enum FinishRoute { Taped, Funnel, Navigate }

public static class FinishRouteExtensions
{
    public static readonly FinishRoute[] All = { FinishRoute.Taped, FinishRoute.Funnel, FinishRoute.Navigate };

    public static string RawValue(this FinishRoute r) => r switch { FinishRoute.Taped => "16.1", FinishRoute.Funnel => "16.2", _ => "16.3" };
    public static FinishRoute? FromRaw(string raw) => All.Cast<FinishRoute?>().FirstOrDefault(r => r!.Value.RawValue() == raw);

    public static string Label(this FinishRoute r) => r switch
    {
        FinishRoute.Taped => "Taped route to finish",
        FinishRoute.Funnel => "Navigate to funnel, then tapes",
        _ => "Navigate to finish (no tapes)",
    };

    /// <summary>English text, e.g. "400 m from last control to finish, follow taped route".</summary>
    public static string Text(this FinishRoute r, int? meters) => r switch
    {
        FinishRoute.Taped => meters is int t ? $"{t} m from last control to finish, follow taped route" : "Follow taped route to finish",
        FinishRoute.Funnel => meters is int f ? $"{f} m from last control to finish, navigate to finish funnel, then follow tapes"
            : "Navigate to finish funnel, then follow tapes",
        _ => meters is int n ? $"Navigate {n} m to finish" : "Finish",
    };
}

public sealed class FinishRouteConverter : JsonConverter<FinishRoute>
{
    public override FinishRoute Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FinishRouteExtensions.FromRaw(reader.GetString() ?? "") ?? throw new JsonException("Unknown finish route.");

    public override void Write(Utf8JsonWriter writer, FinishRoute value, JsonSerializerOptions options) => writer.WriteStringValue(value.RawValue());
}

/// <summary>
/// A gap in the course line of the leg <c>From</c> → <c>To</c>: centred at fraction <c>At</c> of the control-to-control
/// distance, <c>LengthMM</c> long on paper.
/// </summary>
public sealed record LegGap
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("from")] public Guid From { get; set; }
    [JsonPropertyName("to")] public Guid To { get; set; }
    [JsonPropertyName("at")] public double At { get; set; }
    [JsonPropertyName("lengthMM")] public double LengthMM { get; set; } = DefaultLengthMM;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Default gap length on paper (mm).</summary>
    public const double DefaultLengthMM = 2.5;

    public LegGap() { }

    public LegGap(Guid from, Guid to, double at, double lengthMM = DefaultLengthMM)
    {
        From = from;
        To = to;
        At = at;
        LengthMM = lengthMM;
    }
}

/// <summary>
/// Single-page race map layout: full map + overprint, control descriptions in a map corner (<c>CoursePrintLayout</c> on Mac).
/// Unknown keys (title box/north arrow from older versions) are kept for the round trip; missing keys = defaults.
/// </summary>
public sealed class CoursePrintLayout : IJsonOnDeserialized
{
    [JsonConverter(typeof(CamelEnumConverter<Corner>))]
    public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    [JsonConverter(typeof(CamelEnumConverter<DescriptionColor>))]
    public enum DescriptionColor { Black, Purple }

    [JsonPropertyName("descriptionOnMap")] public bool DescriptionOnMap { get; set; } = true;
    [JsonPropertyName("descriptionCorner")] public Corner DescriptionCorner { get; set; } = Corner.TopRight;
    /// <summary>Description cell side (mm); IOF 2024: 5–7 mm, usually 6 mm on the map.</summary>
    [JsonPropertyName("descriptionCellMM")] public double DescriptionCellMM { get; set; } = 6;
    /// <summary>Second page with descriptions only (to hand out / attach to the arm).</summary>
    [JsonPropertyName("separateDescriptionPage")] public bool SeparateDescriptionPage { get; set; }
    /// <summary>Maximum rows (start, controls, finish) per column; the rest moves to the next column. null = one column.</summary>
    [JsonPropertyName("descriptionMaxRows")] public int? DescriptionMaxRows { get; set; }
    /// <summary>Free position: top-left corner of the description block from the top-left of the map area (mm, x right, y down). null = at the corner.</summary>
    [JsonPropertyName("descriptionOffsetMM")] public Point2D? DescriptionOffsetMM { get; set; }
    /// <summary>IOF symbol descriptions (columns A–H, pictograms); false = text descriptions (A, B, then text).</summary>
    [JsonPropertyName("descriptionSymbols")] public bool DescriptionSymbols { get; set; } = true;
    /// <summary>Description ink colour: black (standard) or course purple like the controls.</summary>
    [JsonPropertyName("descriptionColor")] public DescriptionColor DescriptionColorValue { get; set; } = DescriptionColor.Black;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static readonly (int Min, int Max) MaxRowsRange = (3, 40);
    /// <summary>IOF 2024: description boxes 5–7 mm.</summary>
    public static readonly (double Min, double Max) CellRangeMM = (5, 7);

    /// <summary>Older projects allowed up to 8 mm; IOF 2024 boxes are 5–7 mm.</summary>
    public void OnDeserialized() => DescriptionCellMM = Math.Clamp(DescriptionCellMM, CellRangeMM.Min, CellRangeMM.Max);

    public CoursePrintLayout Clone() => (CoursePrintLayout)MemberwiseClone();
}

public static class CoursePrintLayoutExtensions
{
    public static string Label(this CoursePrintLayout.Corner c) => c switch
    {
        CoursePrintLayout.Corner.TopLeft => "Top left",
        CoursePrintLayout.Corner.TopRight => "Top right",
        CoursePrintLayout.Corner.BottomLeft => "Bottom left",
        _ => "Bottom right",
    };

    public static bool IsTop(this CoursePrintLayout.Corner c) => c is CoursePrintLayout.Corner.TopLeft or CoursePrintLayout.Corner.TopRight;
    public static bool IsLeft(this CoursePrintLayout.Corner c) => c is CoursePrintLayout.Corner.TopLeft or CoursePrintLayout.Corner.BottomLeft;

    public static string Label(this CoursePrintLayout.DescriptionColor c) => c == CoursePrintLayout.DescriptionColor.Black ? "Black" : "Purple (like controls)";
    /// <summary>Hex colour; purple = the course overprint colour.</summary>
    public static string Hex(this CoursePrintLayout.DescriptionColor c) => c == CoursePrintLayout.DescriptionColor.Black ? "#000000" : CourseStyle.Isom.ColorHex;
}

/// <summary>Overprint size multipliers relative to the IOF standard (1 = 100 %).</summary>
public sealed record CourseAppearance
{
    /// <summary>Control circle, start triangle, finish circles.</summary>
    [JsonPropertyName("controlScale")] public double ControlScale { get; set; } = 1;
    /// <summary>Control number height.</summary>
    [JsonPropertyName("numberScale")] public double NumberScale { get; set; } = 1;
    /// <summary>Line width (circles, legs).</summary>
    [JsonPropertyName("lineScale")] public double LineScale { get; set; } = 1;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public CourseAppearance() { }

    public CourseAppearance(double controlScale, double numberScale, double lineScale)
    {
        ControlScale = controlScale;
        NumberScale = numberScale;
        LineScale = lineScale;
    }

    public static readonly (double Min, double Max) Range = (0.5, 2);
    [JsonIgnore] public bool IsStandard => ControlScale == 1 && NumberScale == 1 && LineScale == 1;
}

/// <summary>All controls + courses in a project (stored in courses.json). Port of <c>CourseSet</c>.</summary>
public sealed class CourseSet : IJsonOnDeserializing, IJsonOnDeserialized
{
    [JsonPropertyName("eventTitle")] public string EventTitle { get; set; } = "";
    /// <summary>Organizer name, printed below the event name on the description sheet (null in older packages).</summary>
    [JsonPropertyName("organizer")] public string? Organizer { get; set; }
    [JsonPropertyName("controls")] public List<ControlPoint> Controls { get; set; } = new();
    [JsonPropertyName("courses")] public List<Course> Courses { get; set; } = new();
    /// <summary>First code for automatic numbering.</summary>
    [JsonPropertyName("firstCode")] public int FirstCode { get; set; } = 31;
    /// <summary>Skip codes that read differently when the card/map is upside down (e.g. 66 ↔ 99, 68 ↔ 89).</summary>
    [JsonPropertyName("avoidAmbiguousCodes")] public bool AvoidAmbiguousCodes { get; set; } = true;
    /// <summary>Course symbol size for all controls (null = IOF standard), like Purple Pen's "Course appearance".</summary>
    [JsonPropertyName("appearance")] public CourseAppearance? Appearance { get; set; }
    /// <summary>Print layout of the course map (position and columns of the control descriptions).</summary>
    [JsonPropertyName("printLayout")] public CoursePrintLayout? PrintLayout { get; set; }
    /// <summary>Extra text, shapes, and images on the course map (null in older packages).</summary>
    [JsonPropertyName("items")] public List<CourseItem>? Items { get; set; }
    /// <summary>
    /// Numbering of the stored control description symbols: <see cref="DescriptionFormatCurrent"/> (IOF 2024).
    /// Missing in data saved before the 2024 update, which is migrated once when decoded.
    /// </summary>
    [JsonPropertyName("descriptionFormat")] public int? DescriptionFormat { get; set; } = DescriptionFormatCurrent;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>IOF Control Descriptions edition used by the stored symbol ids.</summary>
    public const int DescriptionFormatCurrent = 2024;
    public static readonly (int Min, int Max) CodeRange = (31, 255);

    public CourseSet() { }

    public CourseSet(string eventTitle = "", IEnumerable<ControlPoint>? controls = null, IEnumerable<Course>? courses = null, int firstCode = 31, bool avoidAmbiguousCodes = true)
    {
        EventTitle = eventTitle;
        Controls = controls?.ToList() ?? new List<ControlPoint>();
        Courses = courses?.ToList() ?? new List<Course>();
        FirstCode = firstCode;
        AvoidAmbiguousCodes = avoidAmbiguousCodes;
    }

    /// <summary>A missing <c>descriptionFormat</c> key means data saved before the 2024 update (Mac: <c>decodeIfPresent ?? 2018</c>).</summary>
    public void OnDeserializing() => DescriptionFormat = null;

    /// <summary>Descriptions saved with the 2018 numbering are converted to 2024 (once; the format is then stamped).</summary>
    public void OnDeserialized()
    {
        if ((DescriptionFormat ?? 2018) < DescriptionFormatCurrent)
            foreach (var c in Controls) c.Description = c.Description.MigratedFrom2018();
        DescriptionFormat = DescriptionFormatCurrent;
    }

    [JsonIgnore] public CourseAppearance EffectiveAppearance => Appearance ?? new CourseAppearance();
    [JsonIgnore] public CoursePrintLayout EffectivePrintLayout => PrintLayout ?? new CoursePrintLayout();
    [JsonIgnore] public IReadOnlyList<CourseItem> AllItems => Items ?? (IReadOnlyList<CourseItem>)Array.Empty<CourseItem>();
    [JsonIgnore] public bool IsEmpty => Controls.Count == 0 && Courses.Count == 0 && AllItems.Count == 0;

    /// <summary>Extra objects shown on course <paramref name="course"/> (null = the "All controls" view: all objects).</summary>
    public List<CourseItem> ItemsFor(Course? course) => AllItems.Where(i => i.Shows(course)).ToList();

    public ControlPoint? Control(Guid id) => Controls.FirstOrDefault(c => c.Id == id);
    public Course? CourseById(Guid id) => Courses.FirstOrDefault(c => c.Id == id);

    /// <summary>The course's controls in order (missing ids are skipped).</summary>
    public List<ControlPoint> ControlsOf(Course course)
    {
        var byId = new Dictionary<Guid, ControlPoint>();
        foreach (var c in Controls) byId.TryAdd(c.Id, c);
        var list = new List<ControlPoint>();
        foreach (var id in course.ControlIds) if (byId.TryGetValue(id, out var c)) list.Add(c);
        return list;
    }

    public CourseSet Clone() => new()
    {
        EventTitle = EventTitle, Organizer = Organizer, Controls = Controls.Select(c => c.Clone()).ToList(), Courses = Courses.Select(c => c.Clone()).ToList(),
        FirstCode = FirstCode, AvoidAmbiguousCodes = AvoidAmbiguousCodes, Appearance = Appearance is null ? null : Appearance with { },
        PrintLayout = PrintLayout?.Clone(), Items = Items?.Select(i => i.Clone()).ToList(), DescriptionFormat = DescriptionFormat, Extra = Extra,
    };

    // MARK: Control codes

    /// <summary>Codes that, read upside down (rotated 180°), become another valid code, e.g. 66 → 99, 68 → 89, 61 → 19.</summary>
    public static bool IsAmbiguous(int code)
    {
        var flip = new Dictionary<char, char> { ['0'] = '0', ['1'] = '1', ['6'] = '9', ['8'] = '8', ['9'] = '6' };
        var digits = code.ToString();
        if (!digits.All(flip.ContainsKey)) return false;
        var rotated = new string(digits.Reverse().Select(d => flip[d]).ToArray());
        if (rotated[0] == '0' || !int.TryParse(rotated, out var other)) return false;
        return other != code && other >= CodeRange.Min && other <= CodeRange.Max;
    }

    /// <summary>Next free code starting at <see cref="FirstCode"/>.</summary>
    public int? NextFreeCode()
    {
        var used = Controls.Where(c => c.Code is not null).Select(c => c.Code!.Value).ToHashSet();
        for (int c = Math.Max(FirstCode, CodeRange.Min); c <= CodeRange.Max; c++)
        {
            if (used.Contains(c)) continue;
            if (AvoidAmbiguousCodes && IsAmbiguous(c)) continue;
            return c;
        }
        return null;
    }

    /// <summary>
    /// Renumber all control codes starting at <see cref="FirstCode"/>: in order of appearance in courses (first course first),
    /// then unused controls. Ambiguous codes are skipped when <see cref="AvoidAmbiguousCodes"/>.
    /// </summary>
    public CourseSet Renumbered()
    {
        var order = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var id in Courses.SelectMany(c => c.ControlIds).Concat(Controls.Select(c => c.Id)))
        {
            if (!seen.Add(id)) continue;
            if (Control(id)?.Kind == ControlKind.Control) order.Add(id);
        }
        var next = Clone();
        int code = Math.Max(FirstCode, CodeRange.Min);
        foreach (var id in order)
        {
            while (AvoidAmbiguousCodes && IsAmbiguous(code)) code++;
            if (code > CodeRange.Max) break;
            var target = next.Controls.FirstOrDefault(c => c.Id == id);
            if (target is null) break;
            target.Code = code;
            code++;
        }
        return next;
    }

    /// <summary>Codes used by more than one control.</summary>
    [JsonIgnore]
    public HashSet<int> DuplicateCodes
    {
        get
        {
            var seen = new HashSet<int>();
            var dup = new HashSet<int>();
            foreach (var c in Controls) if (c.Code is int code && !seen.Add(code)) dup.Add(code);
            return dup;
        }
    }

    // MARK: Length & load

    /// <summary>Leg lengths (m, straight line) between consecutive controls.</summary>
    public List<double> LegLengths(Course course)
    {
        var pts = ControlsOf(course).Select(c => c.Position).ToList();
        var result = new List<double>();
        for (int i = 0; i + 1 < pts.Count; i++) result.Add(pts[i].DistanceTo(pts[i + 1]));
        return result;
    }

    /// <summary>Course length (m): sum of straight-line legs start → … → finish (IOF/Purple Pen rule). Score: no order, so 0.</summary>
    public double Length(Course course) => course.Kind == CourseKind.Score ? 0 : LegLengths(course).Sum();

    /// <summary>Control sequence numbers (1…n) within the course; start/finish are not numbered.</summary>
    public Dictionary<Guid, int> SequenceNumbers(Course course)
    {
        int n = 0;
        var result = new Dictionary<Guid, int>();
        foreach (var c in ControlsOf(course).Where(c => c.Kind == ControlKind.Control))
        {
            n++;
            result.TryAdd(c.Id, n);
        }
        return result;
    }

    /// <summary>Control load: number of courses + competitors visiting each control.</summary>
    public readonly record struct ControlLoad(Guid ControlId, int CourseCount, int Visits);

    public List<ControlLoad> ControlLoads() => Controls.Select(c =>
    {
        var using_ = Courses.Where(k => k.ControlIds.Contains(c.Id)).ToList();
        return new ControlLoad(c.Id, using_.Count, using_.Sum(k => k.Competitors));
    }).ToList();

    /// <summary>Load of legs used by more than one course (direction ignored), heaviest first.</summary>
    public readonly record struct LegLoad(Guid From, Guid To, int CourseCount, int Visits);

    public List<LegLoad> SharedLegLoads()
    {
        var table = new Dictionary<string, LegLoad>();
        foreach (var course in Courses.Where(c => c.Kind == CourseKind.Normal))
        {
            var ids = course.ControlIds;
            if (ids.Count < 2) continue;
            var seenInCourse = new HashSet<string>();
            for (int i = 0; i + 1 < ids.Count; i++)
            {
                var a = ids[i]; var b = ids[i + 1];
                var pair = new[] { a.ToString().ToUpperInvariant(), b.ToString().ToUpperInvariant() }.OrderBy(s => s, StringComparer.Ordinal);
                var key = string.Join("|", pair);
                if (!seenInCourse.Add(key)) continue;
                var l = table.TryGetValue(key, out var existing) ? existing : new LegLoad(a, b, 0, 0);
                table[key] = l with { CourseCount = l.CourseCount + 1, Visits = l.Visits + course.Competitors };
            }
        }
        return table.Values.Where(l => l.CourseCount > 1).OrderByDescending(l => l.Visits).ThenByDescending(l => l.CourseCount).ToList();
    }

    // MARK: Warnings (like "Report: course check" in Purple Pen)

    public List<string> Warnings(double minLegM = 20)
    {
        var result = new List<string>();
        foreach (var code in DuplicateCodes.OrderBy(c => c)) result.Add($"Code {code} is used by more than one control.");
        foreach (var c in Controls.Where(c => c.Kind == ControlKind.Control))
        {
            if (c.Code is int code && IsAmbiguous(code)) result.Add($"Code {code} reads differently upside down.");
            if (c.Code is null) result.Add("A control has no code.");
        }
        // Controls close together or on the same feature (IOF: < 30 m, or < 60 m if the feature is the same).
        var cs = Controls.Where(c => c.Kind == ControlKind.Control).ToList();
        for (int i = 0; i < cs.Count; i++)
        {
            for (int j = i + 1; j < cs.Count; j++)
            {
                double d = cs[i].Position.DistanceTo(cs[j].Position);
                bool sameFeature = cs[i].Description.Feature is not null && cs[i].Description.Feature == cs[j].Description.Feature;
                if (d < 30 || (sameFeature && d < 60))
                    result.Add($"Controls {cs[i].Code ?? 0} and {cs[j].Code ?? 0} are only {(int)Math.Round(d, MidpointRounding.AwayFromZero)} m apart{(sameFeature ? " on the same feature" : "")} (IOF: min. {(sameFeature ? 60 : 30)} m).");
            }
        }
        foreach (var course in Courses)
        {
            var list = ControlsOf(course);
            if (course.Kind == CourseKind.Normal)
            {
                if (list.Count == 0 || list[0].Kind != ControlKind.Start) result.Add($"{course.Name}: no start yet.");
                if (list.Count == 0 || list[^1].Kind != ControlKind.Finish) result.Add($"{course.Name}: no finish yet.");
                for (int i = 0; i + 1 < list.Count; i++)
                    if (list[i].Id == list[i + 1].Id) result.Add($"{course.Name}: control {list[i].Label(this)} appears twice in a row.");
                for (int i = 0; i + 1 < list.Count; i++)
                    if (list[i].Id != list[i + 1].Id && list[i].Position.DistanceTo(list[i + 1].Position) < minLegM)
                        result.Add($"{course.Name}: leg {list[i].Label(this)}–{list[i + 1].Label(this)} is very short.");
            }
            if (!list.Any(c => c.Kind == ControlKind.Control)) result.Add($"{course.Name}: no controls yet.");
        }
        return result;
    }
}

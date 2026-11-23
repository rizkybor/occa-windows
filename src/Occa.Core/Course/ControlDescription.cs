using System.Text.Json;
using System.Text.Json.Serialization;

namespace Occa.Core.Courses;

/// <summary>
/// IOF control description (International Specification for Control Descriptions 2024), columns C–H.
/// A = sequence number and B = code are filled from the course. Values are stored as IOF symbol ids (e.g. "2.4" Boulder).
/// Directional symbols carry their direction in the id: "0.1NE" (column C), "12.4NE" (column G: north east corner, inside).
/// Port of <c>ControlDescription</c> (CakraCore).
/// </summary>
public sealed record ControlDescription
{
    /// <summary>C: which of several similar features (e.g. "0.1N" = northern).</summary>
    [JsonPropertyName("which")] public string? Which { get; set; }
    /// <summary>D: control feature (required for controls).</summary>
    [JsonPropertyName("feature")] public string? Feature { get; set; }
    /// <summary>E: appearance / feature detail.</summary>
    [JsonPropertyName("appearance")] public string? Appearance { get; set; }
    /// <summary>F: dimensions ("2.5", "8x4", "0.5/3"), combination (10.1 crossing, 10.2 junction) or 11.1 bend.</summary>
    [JsonPropertyName("dimension")] public string? Dimension { get; set; }
    /// <summary>G: flag location relative to the feature.</summary>
    [JsonPropertyName("location")] public string? Location { get; set; }
    /// <summary>H: other information (first aid, refreshments, manned control).</summary>
    [JsonPropertyName("other")] public string? Other { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ControlDescription() { }

    public ControlDescription(string? which = null, string? feature = null, string? appearance = null, string? dimension = null, string? location = null, string? other = null)
    {
        Which = which;
        Feature = feature;
        Appearance = appearance;
        Dimension = dimension;
        Location = location;
        Other = other;
    }

    [JsonIgnore] public bool IsEmpty => new[] { Which, Feature, Appearance, Dimension, Location, Other }.All(string.IsNullOrEmpty);

    /// <summary>English text description, e.g. "Northern boulder, 1.5 m, north east side".</summary>
    [JsonIgnore]
    public string Text
    {
        get
        {
            // English word order: which + appearance + feature ("Northern low boulder"), first letter capitalized.
            var words = string.Join(" ", new[] { IofSymbols.TextName(Which), IofSymbols.TextName(Appearance), IofSymbols.TextName(Feature) }.Where(w => w is not null)).ToLowerInvariant();
            var head = words.Length == 0 ? "" : char.ToUpperInvariant(words[0]) + words[1..];
            var parts = new List<string>();
            if (head.Length > 0) parts.Add(head);
            var d = Dimension?.Trim();
            if (!string.IsNullOrEmpty(d))
            {
                // Numbers like "1.5" are dimensions (m), not the column D symbol id 1.5.
                var sym = IofSymbols.Symbol(d);
                parts.Add(sym is not null && sym.Column == IofColumn.F ? sym.TextName : DimensionText(d));
            }
            if (IofSymbols.TextName(Location) is { } g) parts.Add(g);
            if (IofSymbols.TextName(Other) is { } h) parts.Add(h);
            return string.Join(", ", parts);
        }
    }

    /// <summary>Column F dimensions as text: "2.5" → "2.5 m", "8x4" → "8 m x 4 m", "0.5/3" → "0.5 m / 3 m".</summary>
    internal static string DimensionText(string d)
    {
        var slash = d.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (slash.Count == 2) return $"{slash[0]} m / {slash[1]} m";
        var by = d.ToLowerInvariant().Split('x').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (by.Count == 2 && by.All(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
            return $"{by[0]} m x {by[1]} m";
        return $"{d} m";
    }

    // MARK: Migration from the 2018 symbol numbering

    /// <summary>
    /// Converts a description saved with the IOF 2018 numbering to the 2024 numbering, field by field
    /// (column G 11.x → 12.x and column H 12.x → 13.x, so the two "12.x" ranges never collide).
    /// Symbols whose meaning was dropped or reused in 2024 move to hidden legacy ids that keep their old
    /// pictogram and name. Corner/tip/end/part had no stored direction in 2018 and get <see cref="IofSymbols.DefaultDirection"/> (north).
    /// </summary>
    public ControlDescription MigratedFrom2018()
    {
        var d = this with { };
        switch (Feature)
        {
            case "5.23": d.Feature = IofSymbols.Legacy.Barrier; break;
            case "5.24": d.Feature = IofSymbols.Legacy.RubbishBin; break;
        }
        if (Location is { } g)
        {
            var dir = IofSymbols.DefaultDirection;
            switch (g)
            {
                case "11.2": d.Location = IofSymbols.Legacy.TopEdge; break;
                case "11.3": d.Location = "12.8"; break;
                case "11.4": d.Location = "12.9"; break;
                case "11.5": d.Location = "12.10"; break;
                case "11.6": d.Location = "12.13"; break;
                case "11.7": d.Location = "12.11"; break;
                case "11.8": d.Location = "12.4" + dir; break;
                case "11.9": d.Location = "12.5" + dir; break;
                case "11.10": d.Location = "12.6" + dir; break;
                case "11.12": d.Location = "12.7" + dir; break;
                case "11.13": d.Location = "12.3" + dir; break;
                case "11.14": d.Location = IofSymbols.Legacy.Beginning; break;
                case "11.15": d.Location = "12.14"; break;
                case "11.11":
                    // Bend moved to column F in 2024; keep it in G (legacy) when F already holds something.
                    if (string.IsNullOrWhiteSpace(Dimension)) { d.Dimension = IofSymbols.Bend; d.Location = null; }
                    else d.Location = IofSymbols.Legacy.Bend;
                    break;
                default:
                    if (g.StartsWith("11.1", StringComparison.Ordinal) && IofSymbols.DirectionOf(g[4..]) is { } dd) d.Location = "12.1" + dd;
                    break;
            }
        }
        switch (Other)
        {
            case "12.1": d.Other = "13.1"; break;
            case "12.2": d.Other = "13.2"; break;
            case "12.3": d.Other = IofSymbols.Legacy.Radio; break;
            case "12.4": d.Other = "13.3"; break;
        }
        return d;
    }
}

/// <summary>Description sheet column (C–H).</summary>
public enum IofColumn { C, D, E, F, G, H }

public static class IofColumnExtensions
{
    public static string Letter(this IofColumn c) => c.ToString();

    public static string Title(this IofColumn c) => c switch
    {
        IofColumn.C => "Which",
        IofColumn.D => "Feature",
        IofColumn.E => "Appearance",
        IofColumn.F => "Dimensions / combination / bend",
        IofColumn.G => "Flag location",
        _ => "Other information",
    };
}

/// <summary>One IOF description symbol (<c>IOFSymbol</c> on Mac).</summary>
public sealed record IofSymbol(string Id, IofColumn Column, string Name, string English, string? Group = null, bool IsLegacy = false, string? ShortName = null)
{
    /// <summary>Name used in English text descriptions.</summary>
    public string TextName => ShortName ?? Name;
}

/// <summary>IOF 2024 description symbol catalog (numbers as in the specification; display name + official English name).</summary>
public static class IofSymbols
{
    /// <summary>Every known id, including the 8 directions of directional symbols and the hidden legacy symbols.</summary>
    public static readonly IReadOnlyList<IofSymbol> All;

    private static readonly Dictionary<string, IofSymbol> ById;

    /// <summary>All ids of a column (with every direction and legacy symbols).</summary>
    public static List<IofSymbol> SymbolsIn(IofColumn column) => All.Where(s => s.Column == column).ToList();

    /// <summary>
    /// Symbols offered in the picker: no legacy symbols; column G lists each directional symbol once (its base id,
    /// e.g. "12.4") — the direction is chosen separately.
    /// </summary>
    public static List<IofSymbol> PickerSymbols(IofColumn column)
        => column == IofColumn.G ? LocationChoices.ToList() : All.Where(s => s.Column == column && !s.IsLegacy).ToList();

    /// <summary>As <see cref="PickerSymbols(IofColumn)"/>; on an indoor map (ISInOM) column D starts with the indoor features, otherwise they come last.</summary>
    public static List<IofSymbol> PickerSymbols(IofColumn column, bool indoor)
    {
        var list = PickerSymbols(column);
        if (column != IofColumn.D || !indoor) return list;
        return list.Where(s => s.Group == IndoorGroup).Concat(list.Where(s => s.Group != IndoorGroup)).ToList();
    }

    public static IofSymbol? Symbol(string? id) => id is not null && ById.TryGetValue(id, out var s) ? s : null;

    public static string? Name(string? id) => string.IsNullOrEmpty(id) ? null : Symbol(id)?.Name;

    /// <summary>Name for text descriptions (short form of names with alternatives).</summary>
    public static string? TextName(string? id) => string.IsNullOrEmpty(id) ? null : Symbol(id)?.TextName;

    private static IofSymbol S(string id, IofColumn c, string name, string en, string? group = null, string? shortName = null)
        => new(id, c, name, en, group, false, shortName);

    // MARK: Directions

    /// <summary>The 8 directions, clockwise from north.</summary>
    public static readonly string[] Directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    /// <summary>Direction for 2018 corner/tip/end/part symbols, which had no stored direction.</summary>
    public const string DefaultDirection = "N";

    public static string? DirectionOf(string s) => Directions.Contains(s) ? s : null;

    /// <summary>"NE" → "north east".</summary>
    public static string DirectionName(string dir) => dir switch
    {
        "N" => "north", "NE" => "north east", "E" => "east", "SE" => "south east", "S" => "south", "SW" => "south west", "W" => "west", "NW" => "north west",
        _ => dir,
    };

    /// <summary>Column G symbols that take one of the 8 directions (12.1–12.7, 12.12).</summary>
    public static readonly string[] DirectionalLocations = { "12.1", "12.2", "12.3", "12.4", "12.5", "12.6", "12.7", "12.12" };

    /// <summary>Splits a column G id into its base and direction: "12.4NE" → ("12.4", "NE"); "12.8" → ("12.8", null).</summary>
    public static (string Base, string? Direction) SplitDirection(string id)
    {
        int n = 0;
        while (n < id.Length && char.IsLetter(id[id.Length - 1 - n])) n++;
        if (n == 0 || id.StartsWith("x.", StringComparison.Ordinal)) return (id, null);
        var dir = id[^n..];
        var b = id[..^n];
        return DirectionOf(dir) is not null ? (b, dir) : (id, null);
    }

    public static bool IsDirectional(string b) => DirectionalLocations.Contains(b);

    /// <summary>Column F id of the bend symbol (in column G until 2018).</summary>
    public const string Bend = "11.1";

    /// <summary>Hidden ids for 2018 symbols that were dropped or whose number got another meaning in 2024.</summary>
    public static class Legacy
    {
        public const string Barrier = "x.barrier";
        public const string RubbishBin = "x.rubbishBin";
        public const string TopEdge = "x.topEdge";
        public const string Beginning = "x.beginning";
        public const string Bend = "x.bend";
        public const string Radio = "x.radio";
    }

    /// <summary>Group of the indoor column D features.</summary>
    public const string IndoorGroup = "Indoor";

    private static readonly IofSymbol[] Which =
    {
        S("0.1N", IofColumn.C, "northern", "Northern"), S("0.1NE", IofColumn.C, "north-eastern", "North-eastern"),
        S("0.1E", IofColumn.C, "eastern", "Eastern"), S("0.1SE", IofColumn.C, "south-eastern", "South-eastern"),
        S("0.1S", IofColumn.C, "southern", "Southern"), S("0.1SW", IofColumn.C, "south-western", "South-western"),
        S("0.1W", IofColumn.C, "western", "Western"), S("0.1NW", IofColumn.C, "north-western", "North-western"),
        S("0.2", IofColumn.C, "upper", "Upper"), S("0.3", IofColumn.C, "lower", "Lower"), S("0.4", IofColumn.C, "middle", "Middle"),
    };

    private static IofSymbol[] Features()
    {
        const string land = "Landforms", rock = "Rock and boulders", water = "Water and marsh", veg = "Vegetation", man = "Man-made features", special = "Special items";
        var d = IofColumn.D;
        return new[]
        {
            S("1.1", d, "Terrace", "Terrace", land), S("1.2", d, "Spur", "Spur", land),
            S("1.3", d, "Re-entrant", "Re-entrant", land), S("1.4", d, "Earth bank", "Earth bank", land),
            S("1.5", d, "Quarry", "Quarry", land), S("1.6", d, "Earth wall", "Earth wall", land),
            S("1.7", d, "Erosion gully", "Erosion gully", land), S("1.8", d, "Small erosion gully", "Small erosion gully", land),
            S("1.9", d, "Hill", "Hill", land), S("1.10", d, "Knoll", "Knoll", land),
            S("1.11", d, "Saddle", "Saddle", land), S("1.12", d, "Depression", "Depression", land),
            S("1.13", d, "Small depression", "Small depression", land), S("1.14", d, "Pit", "Pit", land),
            S("1.15", d, "Broken ground", "Broken ground", land),
            S("1.16", d, "Ant hill (termite mound)", "Ant hill (termite mound)", land, "Ant hill"),
            S("2.1", d, "Cliff, crag", "Cliff, crag", rock, "Cliff"), S("2.2", d, "Rock pillar", "Rock pillar", rock),
            S("2.3", d, "Cave", "Cave", rock), S("2.4", d, "Boulder", "Boulder", rock),
            S("2.5", d, "Boulder field", "Boulder field", rock), S("2.6", d, "Boulder cluster", "Boulder cluster", rock),
            S("2.7", d, "Stony ground", "Stony ground", rock), S("2.8", d, "Bare rock", "Bare rock", rock),
            S("2.9", d, "Narrow passage", "Narrow passage", rock), S("2.10", d, "Trench", "Trench", rock),
            S("3.1", d, "Lake", "Lake", water), S("3.2", d, "Pond", "Pond", water),
            S("3.3", d, "Waterhole", "Waterhole", water),
            S("3.4", d, "River, stream, watercourse", "River, stream, watercourse", water, "Stream"),
            S("3.5", d, "Minor water channel, ditch", "Minor water channel, ditch", water, "Ditch"),
            S("3.6", d, "Narrow marsh", "Narrow marsh", water),
            S("3.7", d, "Marsh", "Marsh", water), S("3.8", d, "Firm ground in marsh", "Firm ground in marsh", water),
            S("3.9", d, "Well", "Well", water), S("3.10", d, "Spring", "Spring", water),
            S("3.11", d, "Water tank, water trough", "Water tank, water trough", water, "Water tank"),
            S("4.1", d, "Open land", "Open land", veg), S("4.2", d, "Semi-open land", "Semi-open land", veg),
            S("4.3", d, "Forest corner", "Forest corner", veg), S("4.4", d, "Clearing", "Clearing", veg),
            S("4.5", d, "Thicket", "Thicket", veg), S("4.6", d, "Linear thicket", "Linear thicket", veg),
            S("4.7", d, "Vegetation boundary", "Vegetation boundary", veg), S("4.8", d, "Copse", "Copse", veg),
            S("4.9", d, "Prominent tree", "Prominent tree", veg),
            S("4.10", d, "Prominent vegetation feature (e.g. root stock, tree stump)", "Prominent vegetation feature", veg, "Prominent vegetation feature"),
            S("5.1", d, "Road", "Road", man), S("5.2", d, "Track / path", "Track / path", man),
            S("5.3", d, "Ride", "Ride", man), S("5.4", d, "Bridge", "Bridge", man),
            S("5.5", d, "Power line", "Power line", man), S("5.6", d, "Pylon", "Pylon", man),
            S("5.7", d, "Tunnel", "Tunnel", man), S("5.8", d, "Wall", "Wall", man),
            S("5.9", d, "Fence", "Fence", man), S("5.10", d, "Crossing point", "Crossing point", man),
            S("5.11", d, "Building", "Building", man), S("5.12", d, "Paved area", "Paved area", man),
            S("5.13", d, "Ruin", "Ruin", man),
            S("5.14", d, "Prominent man-made line feature (e.g. pipeline, bobsleigh track)", "Prominent man-made line feature", man, "Prominent man-made line feature"),
            S("5.15", d, "Tower / pylon", "Tower / pylon", man, "Tower"),
            S("5.16", d, "Shooting platform", "Shooting platform", man),
            S("5.17", d, "Boundary stone, cairn", "Boundary stone, cairn", man, "Boundary stone"),
            S("5.18", d, "Fodder rack", "Fodder rack", man),
            S("5.19", d, "Charcoal burning ground, platform", "Charcoal burning ground, platform", man, "Charcoal burning ground"),
            S("5.20", d, "Monument / statue", "Monument / statue", man),
            S("5.21", d, "Canopy", "Canopy", man), S("5.22", d, "Stairway", "Stairway", man),
            S("5.23", d, "Out of bounds area", "Out of bounds area", man), S("5.24", d, "Railway", "Railway", man),
            S("6.1", d, "Prominent feature / special item", "Prominent feature / special item", special, "Special item (x)"),
            S("6.2", d, "Prominent feature / special item", "Prominent feature / special item", special, "Special item (o)"),
        };
    }

    /// <summary>ISInOM 6.7.3 indoor control description features (column D; Purple Pen 6.3–6.9). Offered in every picker, first on indoor maps.</summary>
    private static readonly IofSymbol[] IndoorFeatures =
    {
        S("7.119", IofColumn.D, "Pillar", "Pillar", IndoorGroup), S("7.132", IofColumn.D, "Toilet", "Toilet", IndoorGroup),
        S("7.133", IofColumn.D, "Shower", "Shower", IndoorGroup), S("7.134", IofColumn.D, "Sink", "Sink", IndoorGroup),
        S("7.141", IofColumn.D, "Table or bench", "Table or bench", IndoorGroup, "Table"),
        S("7.142", IofColumn.D, "Cupboard / shelf", "Cupboard / shelf", IndoorGroup, "Cupboard"),
        S("7.143", IofColumn.D, "Couch / sofa", "Couch / sofa", IndoorGroup, "Couch"),
    };

    private static readonly IofSymbol[] Appearance =
    {
        S("8.1", IofColumn.E, "low", "Low"), S("8.2", IofColumn.E, "shallow", "Shallow"), S("8.3", IofColumn.E, "deep", "Deep"),
        S("8.4", IofColumn.E, "overgrown", "Overgrown"), S("8.5", IofColumn.E, "open", "Open"),
        S("8.6", IofColumn.E, "rocky / stony", "Rocky / stony"), S("8.7", IofColumn.E, "marshy", "Marshy"), S("8.8", IofColumn.E, "sandy", "Sandy"),
        S("8.9", IofColumn.E, "needle leaved", "Needle leaved"), S("8.10", IofColumn.E, "broad leaved", "Broad leaved"),
        S("8.11", IofColumn.E, "ruined", "Ruined"),
    };

    private static readonly IofSymbol[] Combinations =
    {
        S("10.1", IofColumn.F, "crossing", "Crossing"), S("10.2", IofColumn.F, "junction", "Junction"), S(Bend, IofColumn.F, "bend", "Bend"),
    };

    /// <summary>Column G base symbols (2024 numbering) in picker order.</summary>
    private static readonly (string Id, string Name)[] LocationBases =
    {
        ("12.1", "side"), ("12.2", "edge"), ("12.3", "part"), ("12.4", "corner (inside)"), ("12.5", "corner (outside)"),
        ("12.6", "tip"), ("12.7", "end"), ("12.8", "upper part"), ("12.9", "lower part"), ("12.10", "top"),
        ("12.11", "foot"), ("12.12", "foot (with direction)"), ("12.13", "beneath"), ("12.14", "between"),
    };

    private static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static IofSymbol[] Locations() => LocationBases.SelectMany(b =>
    {
        if (!IsDirectional(b.Id)) return new[] { S(b.Id, IofColumn.G, b.Name, Capitalized(b.Name)) };
        var noun = b.Id == "12.12" ? "foot" : b.Name;
        return Directions.Select(dir =>
        {
            var n = $"{DirectionName(dir)} {noun}";
            return S(b.Id + dir, IofColumn.G, n, Capitalized(n));
        }).ToArray();
    }).ToArray();

    /// <summary>Picker entries for column G: one per base symbol (directional ones without a direction).</summary>
    private static readonly IofSymbol[] LocationChoices = LocationBases.Select(b => S(b.Id, IofColumn.G, b.Name, Capitalized(b.Name))).ToArray();

    private static readonly IofSymbol[] OtherInfo =
    {
        S("13.1", IofColumn.H, "first aid post", "First aid post"), S("13.2", IofColumn.H, "refreshment point", "Refreshment point"),
        S("13.3", IofColumn.H, "manned control", "Manned control"),
    };

    /// <summary>2018 symbols kept for old projects (hidden from the picker).</summary>
    private static readonly IofSymbol[] LegacySymbols =
    {
        new(Legacy.Barrier, IofColumn.D, "Barrier", "Barrier", "Man-made features", true),
        new(Legacy.RubbishBin, IofColumn.D, "Rubbish bin", "Rubbish bin", "Man-made features", true),
        new(Legacy.TopEdge, IofColumn.G, "top edge", "Top edge", null, true),
        new(Legacy.Beginning, IofColumn.G, "beginning", "Beginning", null, true),
        new(Legacy.Bend, IofColumn.G, "bend", "Bend", null, true),
        new(Legacy.Radio, IofColumn.H, "radio / TV control", "Radio / TV control", null, true),
    };

    static IofSymbols()
    {
        All = Which.Concat(Features()).Concat(IndoorFeatures).Concat(Appearance).Concat(Combinations).Concat(Locations()).Concat(OtherInfo).Concat(LegacySymbols).ToList();
        ById = new Dictionary<string, IofSymbol>();
        foreach (var s in All) ById.TryAdd(s.Id, s);
    }
}

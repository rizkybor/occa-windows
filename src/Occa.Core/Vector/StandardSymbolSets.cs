using System.Reflection;
using Occa.Core.Sheet;

namespace Occa.Core.Vector;

/// <summary>
/// OCCA's built-in ISOM 2017-2, ISSprOM 2019-2 and ISInOM 6.7.3 sets, embedded as the <c>.occasym</c> files that
/// CakraCore's <c>OccaStandardSymbols</c> writes (regenerated with the Mac repo when the specifications change), so
/// both apps draw from identical definitions.
/// </summary>
public static class StandardSymbolSets
{
    public const string IsomFileName = "OCCA ISOM 2017-2.occasym";
    public const string IssprFileName = "OCCA ISSprOM 2019-2.occasym";
    public const string IsinomFileName = "OCCA ISInOM 6.7.3.occasym";

    private static readonly Lazy<OccaSymbolFile> IsomLazy = new(() => Load(IsomFileName));
    private static readonly Lazy<OccaSymbolFile> IssprLazy = new(() => Load(IssprFileName));
    private static readonly Lazy<OccaSymbolFile> IsinomLazy = new(() => Load(IsinomFileName));

    public static OccaSymbolFile Isom => IsomLazy.Value;
    public static OccaSymbolFile Issprom => IssprLazy.Value;
    public static OccaSymbolFile Isinom => IsinomLazy.Value;

    public static OccaSymbolFile For(MapStandard standard) => standard switch
    {
        MapStandard.Isom => Isom,
        MapStandard.Issprom => Issprom,
        _ => Isinom,
    };

    public static string FileName(MapStandard standard) => standard switch
    {
        MapStandard.Isom => IsomFileName,
        MapStandard.Issprom => IssprFileName,
        _ => IsinomFileName,
    };

    private static OccaSymbolFile Load(string name)
    {
        var asm = typeof(StandardSymbolSets).Assembly;
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal))
                       ?? throw new FileNotFoundException($"Embedded symbol set {name} not found.");
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return OccaSymbolFile.Read(ms.ToArray());
    }

    /// <summary>
    /// The symbol of an object, as the Mac's <c>SymbolLibrary.symbol(code:name:sheet:)</c>: the sheet's own standard
    /// set first, then the other OCCA sets; same code and name wins, else the first symbol with that code.
    /// </summary>
    public static MapSymbol? Lookup(string code, string? name, MapStandard standard)
    {
        var sets = new[] { For(standard) }.Concat(new[] { Isom, Issprom, Isinom }.Where(s => !ReferenceEquals(s, For(standard))));
        MapSymbol? first = null;
        foreach (var set in sets)
        {
            foreach (var s in set.Symbols)
            {
                if (s.Code != code) continue;
                if (name is not null && s.Name == name) return s;
                first ??= s;
            }
        }
        return first;
    }

    public static Func<string, string?, MapSymbol?> LookupFor(SheetSpec sheet) => (code, name) => Lookup(code, name, sheet.MapStandard);
}

using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Tests;

public class SymbolSetTests
{
    [Fact]
    public void BuiltInSetsLoad()
    {
        Assert.Equal(134, StandardSymbolSets.Isom.Symbols.Count);
        Assert.Equal(123, StandardSymbolSets.Issprom.Symbols.Count);
        Assert.Equal(91, StandardSymbolSets.Isinom.Symbols.Count);
        Assert.Equal("ISOM 2017-2", StandardSymbolSets.Isom.Standard);
        Assert.Equal(15000, StandardSymbolSets.Isom.Scale);
        Assert.Equal(4000, StandardSymbolSets.Issprom.Scale);
        Assert.Equal(700, StandardSymbolSets.Isinom.Scale);
    }

    [Fact]
    public void ColourHexFollowsTheMacFormula()
    {
        var brown = StandardSymbolSets.Isom.Colors.Single(c => c.Id == "brownLine");
        Assert.Equal("#BF4000", brown.Hex);
        Assert.Equal("#000000", StandardSymbolSets.Isom.Colors.Single(c => c.Id == "black").Hex);
        Assert.Equal("#FFFFFF", StandardSymbolSets.Isom.Colors.Single(c => c.Id == "white").Hex);
        // The colour table in the file is in IOF order; symbol priorities index the Rev 4 rows.
        Assert.Equal(ColorHierarchy.ColorRow("brownLine"), StandardSymbolSets.Isom.Symbols.Single(s => s.Code == "101.0").ColorPriority);
    }

    [Fact]
    public void LooksUpByCodeAndName()
    {
        var footpath = StandardSymbolSets.Lookup("505.0", "Footpath", MapStandard.Isom);
        Assert.NotNull(footpath);
        Assert.Equal(MapSymbol.Kind.Line, footpath!.SymbolKind);
        Assert.Equal(new List<double> { 2.0, 0.25 }, footpath.LineStyle!.DashMM);
        // ISSprOM's 201 differs from ISOM's: the sheet's standard wins when the name is unknown.
        Assert.Equal("Uncrossable cliff", StandardSymbolSets.Lookup("201.0", null, MapStandard.Issprom)!.Name);
        Assert.Equal("Impassable cliff", StandardSymbolSets.Lookup("201.0", null, MapStandard.Isom)!.Name);
        Assert.Null(StandardSymbolSets.Lookup("999.9", null, MapStandard.Isom));
    }

    [Fact]
    public void IofPriorityResolvesByKind()
    {
        Assert.Equal(ColorHierarchy.ColorRow("blue"), ColorHierarchy.IofPriority("#00FFFF", MapSymbol.Kind.Area));
        Assert.Equal(ColorHierarchy.ColorRow("bluePoint"), ColorHierarchy.IofPriority("#00FFFF", MapSymbol.Kind.Point, MapStandard.Isom));
        Assert.Equal(ColorHierarchy.ColorRow("blackRoadOutline"), ColorHierarchy.IofPriority("#000000", MapSymbol.Kind.Line, edge: true));
        Assert.Equal(ColorHierarchy.ColorRow("black"), ColorHierarchy.IofPriority("#000000", MapSymbol.Kind.Line));
    }
}

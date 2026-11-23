using Occa.Core.Geometry;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Undo history and symbol-based object creation used by the drawing tools (M2 UI).</summary>
public class EditingShellTests
{
    [Fact]
    public void HistoryUndoRedoAndLimit()
    {
        var h = new EditHistory<string>(limit: 2);
        Assert.False(h.CanUndo);
        h.Record("a", "0", "1");
        h.Record("b", "1", "2");
        h.Record("c", "2", "3");
        Assert.Equal("c", h.UndoLabel);
        Assert.Equal("2", h.Undo());
        Assert.Equal("1", h.Undo());
        Assert.Null(h.Undo());   // the oldest step fell off the limit
        Assert.Equal("2", h.Redo());
        Assert.Equal("3", h.Redo());
        Assert.False(h.CanRedo);
        h.Undo();
        h.Record("d", "2", "9");
        Assert.False(h.CanRedo);
        Assert.Equal("d", h.UndoLabel);
    }

    [Fact]
    public void LineFromSymbolCarriesStyleAndName()
    {
        var sheet = new SheetSpec { ScaleDenominator = 10000 };
        var lookup = StandardSymbolSets.LookupFor(sheet);
        var path = lookup("506.0", null)!;
        var f = SymbolFeatures.Line(path, new List<BezierNode> { new(P(0, 0)), new(P(100, 0)) });
        Assert.Equal("506.0", f.SymbolCode);
        Assert.Equal(path.ColorHex, f.ColorHex);
        Assert.NotNull(f.DashMM);
        Assert.Equal(f.WidthMM, f.StandardWidthMM);
        var bigger = SymbolFeatures.Line(path, new List<BezierNode> { new(P(0, 0)), new(P(100, 0)) }, sizeFactor: 2);
        Assert.Equal(f.WidthMM * 2, bigger.WidthMM, 6);
        Assert.Equal(f.EffectiveDashMM![0] * 2, bigger.EffectiveDashMM![0], 6);
        Assert.Equal("506.0 " + path.Name + " 1", SymbolFeatures.DefaultName(new List<Feature>(), f));
        Assert.Equal("506.0 " + path.Name + " 2", SymbolFeatures.DefaultName(new List<Feature> { f }, bigger));

        var boulder = lookup("204.0", null)!;
        var pt = SymbolFeatures.Point(boulder, P(5, 5), 1.5)!;
        Assert.Equal(FeatureKind.Point, pt.Kind);
        Assert.Equal(1.5, pt.SizeFactor);
        var forest = lookup("406.0", null)!;
        var area = SymbolFeatures.Area(forest, new List<BezierNode> { new(P(0, 0)), new(P(10, 0)), new(P(10, 10)) });
        Assert.Equal(FeatureKind.Area, area.Kind);
        Assert.Equal("Area 1", SymbolFeatures.DefaultName(new List<Feature>(), SymbolFeatures.Sketch(FeatureKind.Area, area.Nodes, "#000000", 0.2)));
    }

    [Fact]
    public void SetWidthKeepsDoubleLineRatio()
    {
        var f = new Feature { Kind = FeatureKind.Line, ColorHex = "#FFFFFF", WidthMM = 0.45, CasingColorHex = "#000000", InnerWidthMM = 0.25, StandardWidthMM = 0.45 };
        f.SetWidth(0.9);
        Assert.Equal(0.9, f.WidthMM);
        Assert.Equal(0.5, f.InnerWidthMM!.Value, 9);
        Assert.Equal(0.45, f.StandardWidthMM);
    }
}

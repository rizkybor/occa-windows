namespace Occa.Core.Vector;

/// <summary>
/// Undo / redo of whole-document states (the Mac's <c>WorkspaceModel.record(.features(before:after:))</c>): each step
/// keeps the list before and after an edit; features are value copies (<c>Clone</c>) so later edits never change history.
/// </summary>
public sealed class EditHistory<T>
{
    public sealed record Step(string Label, T Before, T After);

    private readonly List<Step> _undo = new();
    private readonly List<Step> _redo = new();
    public int Limit { get; }

    public EditHistory(int limit = 200) => Limit = limit;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Records one step and clears the redo stack.</summary>
    public void Record(string label, T before, T after)
    {
        _undo.Add(new Step(label, before, after));
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>The state to restore, or null when nothing can be undone.</summary>
    public T? Undo()
    {
        if (_undo.Count == 0) return default;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step);
        return step.Before;
    }

    public T? Redo()
    {
        if (_redo.Count == 0) return default;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step);
        return step.After;
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); }
}

/// <summary>Builds drawn objects from map symbols the way the Mac workspace does (style, size and default names).</summary>
public static class SymbolFeatures
{
    /// <summary>A line with the symbol's line style (casing, dashes, caps) and width × <paramref name="sizeFactor"/>.</summary>
    public static Feature Line(MapSymbol s, List<BezierNode> nodes, double sizeFactor = 1)
    {
        var f = new Feature { Kind = FeatureKind.Line, ColorHex = s.ColorHex, WidthMM = s.DrawingWidthMM, Nodes = nodes, SymbolCode = s.Code, SymbolName = s.Name };
        if (s.LineStyle is { } style) f.Apply(style);
        if (Math.Abs(sizeFactor - 1) > 1e-9) f.SetWidth(f.WidthMM * sizeFactor);
        return f;
    }

    /// <summary>An area in the symbol's colour (patterns and colour order come from the symbol set at drawing time).</summary>
    public static Feature Area(MapSymbol s, List<BezierNode> nodes)
        => new() { Kind = FeatureKind.Area, ColorHex = s.ColorHex, WidthMM = s.DrawingWidthMM, Nodes = nodes, SymbolCode = s.Code, SymbolName = s.Name };

    /// <summary>The symbol's point object at <paramref name="p"/>; null when the symbol has no drawable elements.</summary>
    public static Feature? Point(MapSymbol s, Geometry.Point2D p, double sizeFactor = 1)
        => s.PointElements is { Count: > 0 } elements ? Feature.PointAt(p, elements.Select(e => e.Clone()).ToList(), sizeFactor, symbolCode: s.Code, symbolName: s.Name) : null;

    /// <summary>A sketch-style (no symbol) line or area in the pen colour.</summary>
    public static Feature Sketch(FeatureKind kind, List<BezierNode> nodes, string colorHex, double widthMM)
        => new() { Kind = kind, ColorHex = colorHex, WidthMM = widthMM, Nodes = nodes };

    /// <summary>"506.0 Small footpath 3" / "Line 2": the Mac's default name when a drawn object has none.</summary>
    public static string DefaultName(IReadOnlyList<Feature> existing, Feature f)
    {
        if (f.SymbolCode is { } code)
        {
            int n = existing.Count(x => x.SymbolCode == code) + 1;
            return $"{code} {f.SymbolName ?? ""} {n}".Replace("  ", " ");
        }
        return $"{f.Kind} {existing.Count(x => x.Kind == f.Kind) + 1}";
    }
}

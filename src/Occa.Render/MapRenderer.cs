using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace Occa.Render;

/// <summary>
/// Draws a project onto a Win2D drawing session. M0 placeholder: paints the paper background only. M1 adds tiled
/// rendering with a cache per zoom level, symbols in the symbol set's colour order and the basemap (PRD §8.4).
/// </summary>
public sealed class MapRenderer
{
    /// <summary>IOF "paper white"; the map sheet is drawn on this before any colour layer.</summary>
    public static readonly Color PaperColor = Color.FromArgb(255, 255, 255, 255);

    public void Draw(CanvasDrawingSession session, float width, float height)
    {
        session.Clear(PaperColor);
    }
}

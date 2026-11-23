using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Sheet;

namespace Occa.Core.Tests;

public class GeodesyTests
{
    [Fact]
    public void UtmRoundTripsWithinAMillimetre()
    {
        var monas = new LatLon(-6.1754, 106.8272);
        var zone = UtmZone.Containing(monas);
        Assert.Equal(48, zone.Number);
        Assert.False(zone.IsNorth);
        Assert.Equal(32748, zone.Epsg);
        Assert.Equal(105, zone.CentralMeridian);
        var p = zone.Project(monas);
        Assert.InRange(p.X, 700_000, 710_000);
        Assert.InRange(p.Y, 9_316_000, 9_320_000);
        var back = zone.Unproject(p);
        Assert.Equal(monas.Lat, back.Lat, 8);
        Assert.Equal(monas.Lon, back.Lon, 8);
        Assert.True(zone.Project(back).DistanceTo(p) < 0.001);
    }

    [Fact]
    public void SheetGroundSizes()
    {
        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        Assert.Equal(840, sheet.GroundWidthM);
        Assert.Equal(1188, sheet.GroundHeightM);
        Assert.Equal(MapStandard.Issprom, sheet.MapStandard);
        Assert.Equal(1, sheet.SymbolEnlargement);
        Assert.Equal(1.5, new SheetSpec { ScaleDenominator = 10000 }.SymbolEnlargement);
        Assert.Equal((2480, 3508), sheet.PixelDimensions(300));
    }

    [Fact]
    public void NorthGridStandardAndLines()
    {
        var grid = NorthGrid.Standard(15000, MapStandard.Isom);
        Assert.Equal(300, grid.SpacingM);
        Assert.Equal(0.12, grid.LineWidthMM);
        var frame = new MapFrame(new Point2D(786_000, 9_234_000), 3150, 4455);
        var lines = grid.Lines(frame);
        Assert.Equal(11, lines.Count);
        Assert.All(lines, l => Assert.Equal(l.A.X, l.B.X, 6));
        Assert.Equal(786_000, lines[0].A.X);
    }
}

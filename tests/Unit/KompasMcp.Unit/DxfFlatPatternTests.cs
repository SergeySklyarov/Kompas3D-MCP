using System.Globalization;
using System.Text;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The contour check behind the flat-part export, on synthetic DXF files, without KOMPAS.</summary>
/// <remarks>TEST: the file is published only when its entities are one closed contour of the allowed types on
/// the CUT layer; a duplicate, a zero segment or a self-intersection is a refusal, not a warning.
/// History: docs/decisions/flat-part.md#contour-check</remarks>
public class DxfFlatPatternTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kompas-flat-tests-" + Guid.NewGuid().ToString("N"));

    public DxfFlatPatternTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private string Write(string name, IEnumerable<string> entities)
    {
        var path = Path.Combine(_directory, name);
        var lines = new List<string> { "0", "SECTION", "2", "ENTITIES" };
        lines.AddRange(entities);
        lines.AddRange(new[] { "0", "ENDSEC", "0", "EOF" });
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    private static string Number(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static IEnumerable<string> Line(double x1, double y1, double x2, double y2, string layer = "CUT") =>
        new[]
        {
            "0", "LINE", "8", layer,
            "10", Number(x1), "20", Number(y1),
            "11", Number(x2), "21", Number(y2),
        };

    private static IEnumerable<string> Circle(double x, double y, double radius, string layer = "CUT") =>
        new[]
        {
            "0", "CIRCLE", "8", layer,
            "10", Number(x), "20", Number(y), "40", Number(radius),
        };

    private static IEnumerable<string> Rectangle(double width, double height) =>
        Line(0d, 0d, width, 0d)
            .Concat(Line(width, 0d, width, height))
            .Concat(Line(width, height, 0d, height))
            .Concat(Line(0d, height, 0d, 0d));

    [Fact]
    public void Check_RectangleWithHoleIsTwoLoops()
    {
        var path = Write("plate.dxf", Rectangle(40d, 30d).Concat(Circle(20d, 15d, 5d)));

        var check = DxfFlatPattern.Check(path);

        Assert.True(check.Ok, string.Join("; ", check.Problems));
        Assert.Equal(2, check.Loops);
        Assert.Equal(1, check.OuterLoops);
        Assert.Equal(1, check.InnerLoops);
        Assert.Equal(40d, check.Box!.Width, 3);
        Assert.Equal(30d, check.Box.Height, 3);
        Assert.Equal((40d * 30d) - (Math.PI * 25d), check.AreaMm2, 1);
        Assert.Equal(1, check.EntityCounts["CIRCLE"]);
    }

    [Fact]
    public void Check_DuplicateSegmentIsRefused()
    {
        var path = Write("duplicate.dxf", Rectangle(40d, 30d).Concat(Line(0d, 0d, 40d, 0d)));

        var check = DxfFlatPattern.Check(path);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, problem => problem.Contains("встречается дважды", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ZeroSegmentIsRefused()
    {
        var path = Write("zero.dxf", Rectangle(40d, 30d).Concat(Line(0d, 0d, 0d, 0d)));

        var check = DxfFlatPattern.Check(path);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, problem => problem.Contains("нулевой сегмент", StringComparison.Ordinal));
    }

    // A bow tie: four corners, two of them crossed — the contour is closed but it crosses itself.
    [Fact]
    public void Check_SelfIntersectionIsRefused()
    {
        var path = Write(
            "crossed.dxf",
            Line(0d, 0d, 40d, 30d)
                .Concat(Line(40d, 30d, 40d, 0d))
                .Concat(Line(40d, 0d, 0d, 30d))
                .Concat(Line(0d, 30d, 0d, 0d)));

        var check = DxfFlatPattern.Check(path);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, problem => problem.Contains("самопересечение", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_OpenContourIsRefused()
    {
        var path = Write(
            "open.dxf",
            Line(0d, 0d, 40d, 0d).Concat(Line(40d, 0d, 40d, 30d)).Concat(Line(40d, 30d, 0d, 30d)));

        var check = DxfFlatPattern.Check(path);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, problem => problem.Contains("не замкнут", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_OtherLayerIsRefused()
    {
        var path = Write("layer.dxf", Rectangle(40d, 30d).Concat(Circle(20d, 15d, 5d, "0")));

        var check = DxfFlatPattern.Check(path);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, problem => problem.Contains("слое «0»", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ArcIsAnalyticAndEnclosesItsSegment()
    {
        // Half of a 10 mm circle: an ARC from 0 to 180 degrees, closed by its diameter.
        var path = Write(
            "arc.dxf",
            new[]
            {
                "0", "ARC", "8", "CUT",
                "10", "0", "20", "0", "40", "10", "50", "0", "51", "180",
            }.Concat(Line(-10d, 0d, 10d, 0d)));

        var check = DxfFlatPattern.Check(path);

        Assert.True(check.Ok, string.Join("; ", check.Problems));
        Assert.Equal(1, check.Loops);
        Assert.Equal(Math.PI * 100d / 2d, check.AreaMm2, 0);
    }

    [Fact]
    public void Rewrite_KeepsTheContourDropsTheFrameAndSetsTheLayer()
    {
        var source = Write(
            "drawing.dxf",
            Rectangle(40d, 30d)
                .Concat(Circle(20d, 15d, 5d, "0"))
                .Concat(Line(-100d, -100d, -60d, -100d, "0")));
        var target = Path.Combine(_directory, "contour.dxf");

        Assert.True(DxfFlatPattern.Rewrite(source, target, new DxfBox(0d, 0d, 40d, 30d), DxfFlatPattern.CutLayer));

        var check = DxfFlatPattern.Check(target);
        Assert.True(check.Ok, string.Join("; ", check.Problems));
        Assert.Equal(2, check.Loops);
        Assert.Equal(5, check.EntityCounts.Values.Sum());
        Assert.DoesNotContain(
            File.ReadAllLines(target),
            line => line.Contains("-100", StringComparison.Ordinal));
    }
}

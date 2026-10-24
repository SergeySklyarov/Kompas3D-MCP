using System.Globalization;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.SheetMetal;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The contour rule of the flat-pattern export and the DXF reader behind it, without KOMPAS.</summary>
/// <remarks>TEST: <c>AddStandartViews</c> ignores the projection name and builds the front view, so the size
/// of the contour is what separates a flat pattern from a substituted view; a missing independent extent is
/// never a match. History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
public class FlatDxfPolicyTests
{
    private static DxfBox Box(double width, double height) => new(0d, 0d, width, height);

    [Fact]
    public void Judge_UnfoldedContourOfTheSameSizeMatches() =>
        Assert.Equal(
            FlatDxfVerdict.Match,
            FlatDxfPolicy.Judge(Box(80d, 40d), Box(80.005d, 39.995d), null, hasBends: true));

    [Fact]
    public void Judge_UnfoldedContourOfAnotherSizeIsAMismatch() =>
        Assert.Equal(
            FlatDxfVerdict.Mismatch,
            FlatDxfPolicy.Judge(Box(40d, 40d), Box(80d, 40d), null, hasBends: true));

    // The tolerance is the one the order names: 0,01 mm.
    [Theory]
    [InlineData(80.011d, FlatDxfVerdict.Mismatch)]
    [InlineData(80.009d, FlatDxfVerdict.Match)]
    public void Judge_ToleranceIsOneHundredthOfAMillimetre(double width, FlatDxfVerdict expected) =>
        Assert.Equal(expected, FlatDxfPolicy.Judge(Box(width, 40d), Box(80d, 40d), null, hasBends: false));

    // The bent body's box on a part with bends: the view was built as «Спереди».
    [Fact]
    public void Judge_ContourEqualToTheBentBodyIsASubstitutedView() =>
        Assert.Equal(
            FlatDxfVerdict.Substituted,
            FlatDxfPolicy.Judge(Box(40d, 40d), flat: null, Box(40d, 40d), hasBends: true));

    // A part without bends: the front view IS the flat pattern, so the same size is not a substitution.
    [Fact]
    public void Judge_WithoutBendsTheSameSizeIsNotASubstitution() =>
        Assert.Equal(
            FlatDxfVerdict.NotChecked,
            FlatDxfPolicy.Judge(Box(40d, 40d), flat: null, Box(40d, 40d), hasBends: false));

    // No independent value and no substitution: nothing is claimed, and the caller lowers the level.
    [Fact]
    public void Judge_NoIndependentExtentClaimsNothing() =>
        Assert.Equal(FlatDxfVerdict.NotChecked, FlatDxfPolicy.Judge(Box(40d, 40d), null, null, hasBends: true));

    [Fact]
    public void Judge_NoContourClaimsNothing() =>
        Assert.Equal(FlatDxfVerdict.NotChecked, FlatDxfPolicy.Judge(null, Box(80d, 40d), null, hasBends: true));

    [Theory]
    [InlineData("C:\\out\\flat.dxf", true)]
    [InlineData("C:\\out\\flat.DXF", true)]
    [InlineData("C:\\out\\flat.dwg", false)]
    [InlineData("C:\\out\\flat", false)]
    public void IsDxfPath_AcceptsOnlyDxf(string path, bool expected) =>
        Assert.Equal(expected, FlatDxfPolicy.IsDxfPath(path));

    [Fact]
    public void Read_MeasuresLinesArcsAndCirclesInsideTheWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), "kompas-dxf-" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            // One 80 x 40 rectangle, one circle inside it, and one stray line outside the window.
            File.WriteAllLines(path, new[]
            {
                "0", "LINE", "8", "0", "10", "0", "20", "0", "11", "80", "21", "0",
                "0", "LINE", "8", "0", "10", "80", "20", "0", "11", "80", "21", "40",
                "0", "LINE", "8", "0", "10", "80", "20", "40", "11", "0", "21", "40",
                "0", "LINE", "8", "0", "10", "0", "20", "40", "11", "0", "21", "0",
                "0", "CIRCLE", "8", "0", "10", "40", "20", "20", "40", "3",
                "0", "LINE", "8", "0", "10", "200", "20", "200", "11", "210", "21", "210",
            });
            var window = new DxfBox(0d, 0d, 80d, 40d);
            var contour = DxfContourReader.Read(path, window, FlatDxfPolicy.WindowToleranceMm);
            Assert.NotNull(contour);
            Assert.Equal(4, contour!.LineCount);
            Assert.Equal(1, contour.CircleCount);
            Assert.Equal(0, contour.ArcCount);
            Assert.NotNull(contour.Box);
            Assert.Equal(80d, contour.Box!.Width, 3);
            Assert.Equal(40d, contour.Box.Height, 3);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Read_WithoutAWindowMeasuresEveryEntity()
    {
        var path = Path.Combine(Path.GetTempPath(), "kompas-dxf-" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            File.WriteAllLines(path, new[]
            {
                "0", "LINE", "8", "0", "10", "0", "20", "0", "11", "10", "21", "0",
                "0", "LINE", "8", "0", "10", "10", "20", "0", "11", "10", "21", "50",
            });
            var contour = DxfContourReader.Read(path);
            Assert.NotNull(contour?.Box);
            Assert.Equal(
                "10x50",
                contour!.Box!.Width.ToString("0.###", CultureInfo.InvariantCulture) + "x"
                + contour.Box.Height.ToString("0.###", CultureInfo.InvariantCulture));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Read_OfAFileThatIsNotDxfAnswersNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "kompas-dxf-" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            File.WriteAllLines(path, new[] { "PK", "not a dxf" });
            Assert.Null(DxfContourReader.Read(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

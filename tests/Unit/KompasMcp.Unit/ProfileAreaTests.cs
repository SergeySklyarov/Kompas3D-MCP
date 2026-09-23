using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// The analytic profile area is what turns "КОМПАС returned true" into a verified geometry
/// change (spec 1.11), so its own numbers must be exact and its refusals must be real.
/// </summary>
public class ProfileAreaTests
{
    private static SketchEntityDto Rect(double w, double h) => new()
    {
        Kind = SketchEntityKind.Rectangle,
        StartMm = new double[] { 0, 0 },
        WidthMm = w,
        HeightMm = h,
    };

    [Fact]
    public void Rectangle_IsWidthTimesHeight()
    {
        Assert.Equal(8000d, ProfileArea.Of(new[] { Rect(100, 80) })!.Value, 9);
    }

    [Fact]
    public void Circle_IsPiR2()
    {
        var circle = new SketchEntityDto
        {
            Kind = SketchEntityKind.Circle,
            CenterMm = new double[] { 0, 0 },
            RadiusMm = 5,
        };

        Assert.Equal(Math.PI * 25d, ProfileArea.Of(new[] { circle })!.Value, 9);
    }

    [Fact]
    public void ClosedPolyline_UsesShoelace()
    {
        var triangle = new SketchEntityDto
        {
            Kind = SketchEntityKind.Polyline,
            Closed = true,
            PointsMm = new[]
            {
                new double[] { 0, 0 },
                new double[] { 10, 0 },
                new double[] { 0, 10 },
            },
        };

        Assert.Equal(50d, ProfileArea.Of(new[] { triangle })!.Value, 9);
    }

    [Fact]
    public void OpenPolyline_IsNotAnArea()
    {
        // Reporting an area for an unclosed contour would let an extrusion compare its volume
        // against a number that has no meaning.
        var open = new SketchEntityDto
        {
            Kind = SketchEntityKind.Polyline,
            Closed = false,
            PointsMm = new[] { new double[] { 0, 0 }, new double[] { 10, 0 }, new double[] { 10, 10 } },
        };

        Assert.Null(ProfileArea.Of(new[] { open }));
    }

    [Fact]
    public void LineOrArc_Alone_IsNotAnArea()
    {
        Assert.Null(ProfileArea.Of(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Line,
                StartMm = new double[] { 0, 0 },
                EndMm = new double[] { 10, 0 },
            },
        }));

        Assert.Null(ProfileArea.Of(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Arc,
                CenterMm = new double[] { 0, 0 },
                RadiusMm = 5,
                StartDeg = 0,
                SweepDeg = 90,
            },
        }));
    }

    [Fact]
    public void MultiplePrimitives_AreSummed()
    {
        var circle = new SketchEntityDto
        {
            Kind = SketchEntityKind.Circle,
            CenterMm = new double[] { 0, 0 },
            RadiusMm = 1,
        };

        var total = ProfileArea.Of(new[] { Rect(10, 10), circle })!.Value;
        Assert.Equal(100d + Math.PI, total, 9);
    }

    [Fact]
    public void Matches_UsesRelativeTolerance()
    {
        // The measured 80000 mm³ arrives as 79999.99999999999 in КОМПАС; a purely absolute
        // tolerance would have to be either huge (for big models) or flaky (for small ones).
        Assert.True(ProfileArea.Matches(80000d, 79999.99999999999d));
        Assert.True(ProfileArea.Matches(1e9, 1e9 + 0.5));
        Assert.False(ProfileArea.Matches(80000d, 80001d));
        Assert.False(ProfileArea.Matches(null, 80000d));
        Assert.False(ProfileArea.Matches(80000d, null));
    }
}

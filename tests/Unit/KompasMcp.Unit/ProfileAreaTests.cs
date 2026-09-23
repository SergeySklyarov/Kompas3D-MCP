using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// The analytic profile area is what turns "КОМПАС returned true" into a verified geometry
/// change (spec 1.11), so its own numbers must be exact and its refusals must be real.
/// </summary>
/// <remarks>
/// The nested cases below are not arithmetic exercises: each was measured on КОМПАС-3D v24 before it
/// was written down (24.09.2026, extruded 10 mm from a sketch on XY), and the measured volumes are
/// quoted next to the expectations. The controls — one circle, disjoint contours — are here for the
/// same reason as the refusals: an instrument that cannot pass is as useless as one that cannot
/// refuse, and without them "summing is wrong" is indistinguishable from "the check is broken".
/// </remarks>
public class ProfileAreaTests
{
    private static SketchEntityDto Rect(double x, double y, double w, double h) => new()
    {
        Kind = SketchEntityKind.Rectangle,
        StartMm = new double[] { x, y },
        WidthMm = w,
        HeightMm = h,
    };

    private static SketchEntityDto Circle(double cx, double cy, double r) => new()
    {
        Kind = SketchEntityKind.Circle,
        CenterMm = new double[] { cx, cy },
        RadiusMm = r,
    };

    private static SketchEntityDto Polyline(params double[][] points) => new()
    {
        Kind = SketchEntityKind.Polyline,
        Closed = true,
        PointsMm = points,
    };

    [Fact]
    public void Rectangle_IsWidthTimesHeight()
    {
        Assert.Equal(8000d, ProfileArea.Of(new[] { Rect(0, 0, 100, 80) })!.Value, 9);
    }

    [Fact]
    public void Circle_IsPiR2()
    {
        Assert.Equal(Math.PI * 25d, ProfileArea.Of(new[] { Circle(0, 0, 5) })!.Value, 9);
    }

    [Fact]
    public void ClosedPolyline_UsesShoelace()
    {
        var triangle = Polyline(new double[] { 0, 0 }, new double[] { 10, 0 }, new double[] { 0, 10 });

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
    public void SelfIntersectingPolyline_IsNotAnArea()
    {
        // A bowtie has a shoelace figure but no single enclosed region. Measured 24.09.2026: that
        // figure used to be returned as an expectation — the class of defect this test closes.
        var bowtie = Polyline(
            new double[] { 0, 0 },
            new double[] { 10, 10 },
            new double[] { 10, 0 },
            new double[] { 0, 10 });

        Assert.Null(ProfileArea.Of(new[] { bowtie }));
    }

    [Fact]
    public void DegeneratePolyline_IsNotAnArea()
    {
        var collinear = Polyline(new double[] { 0, 0 }, new double[] { 5, 0 }, new double[] { 10, 0 });

        Assert.Null(ProfileArea.Of(new[] { collinear }));
    }

    [Fact]
    public void EmptyProfile_IsNotAnArea()
    {
        Assert.Null(ProfileArea.Of(Array.Empty<SketchEntityDto>()));
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
    public void DisjointPrimitives_AreSummed()
    {
        // The control for every nested case below: contours that do not meet are two regions, and
        // their areas do add up. Measured on v24: circles R=5@(0,0) and r=3@(100,0) extruded 10 mm
        // give 1068.1415022205315 mm³ = (π·25 + π·9)·10.
        var total = ProfileArea.Of(new[] { Rect(0, 0, 10, 10), Circle(50, 0, 1) })!.Value;

        Assert.Equal(100d + Math.PI, total, 9);
    }

    [Fact]
    public void DisjointPolygons_AreSummed()
    {
        var total = ProfileArea.Of(new[]
        {
            Polyline(new double[] { 0, 0 }, new double[] { 20, 0 }, new double[] { 20, 20 }, new double[] { 0, 20 }),
            Polyline(new double[] { 100, 0 }, new double[] { 110, 0 }, new double[] { 110, 10 }, new double[] { 100, 10 }),
        })!.Value;

        Assert.Equal(500d, total, 9);
    }

    [Fact]
    public void NestedCircles_AreAnAnnulus()
    {
        // Measured on v24: circles R=10 and r=5, extruded 10 mm, give 2356.1944901923607 mm³, which is
        // π·(100−25)·10 to 6.8e-15 relative. The sum π·125 would be 3926.9908169872415 mm³.
        var area = ProfileArea.Of(new[] { Circle(0, 0, 10), Circle(0, 0, 5) })!.Value;

        Assert.Equal(Math.PI * 75d, area, 9);
    }

    [Fact]
    public void NestedCircles_OffCentre_AreAnAnnulus()
    {
        // Containment, not concentricity, is what makes a hole: here the inner centre is 4 mm off.
        var area = ProfileArea.Of(new[] { Circle(0, 0, 10), Circle(4, 0, 3) })!.Value;

        Assert.Equal(Math.PI * 91d, area, 9);
    }

    [Fact]
    public void RectangleWithInnerCircle_IsAPlateWithAHole()
    {
        // Measured on v24: a 100×80 rectangle with an r=10 circle at (50,40), extruded 10 mm, gives
        // 76858.4073464102 mm³ against (8000 − 100π)·10 = 76858.40734641021.
        var area = ProfileArea.Of(new[] { Rect(0, 0, 100, 80), Circle(50, 40, 10) })!.Value;

        Assert.Equal(8000d - (Math.PI * 100d), area, 9);
    }

    [Fact]
    public void TwoHolesInAPlate_AreBothSubtracted()
    {
        // Measured on v24: the same plate with r=10 at (30,40) and r=6 at (70,40) gives
        // 75727.43399111787 mm³ = (8000 − 100π − 36π)·10.
        var area = ProfileArea.Of(new[] { Rect(0, 0, 100, 80), Circle(30, 40, 10), Circle(70, 40, 6) })!.Value;

        Assert.Equal(8000d - (Math.PI * 136d), area, 9);
    }

    [Fact]
    public void IslandInsideAHole_IsMaterialAgain()
    {
        // Even-odd depth: a contour inside a hole is material. Measured on v24: circles R=10, r=5 and
        // r=2 extruded 10 mm give 2481.8581963359516 mm³ = π·(100−25+4)·10.
        var area = ProfileArea.Of(new[] { Circle(0, 0, 10), Circle(0, 0, 5), Circle(0, 0, 2) })!.Value;

        Assert.Equal(Math.PI * 79d, area, 9);
    }

    [Fact]
    public void NestedSquares_AreAFrame()
    {
        var area = ProfileArea.Of(new[]
        {
            Polyline(new double[] { 0, 0 }, new double[] { 20, 0 }, new double[] { 20, 20 }, new double[] { 0, 20 }),
            Polyline(new double[] { 5, 5 }, new double[] { 15, 5 }, new double[] { 15, 15 }, new double[] { 5, 15 }),
        })!.Value;

        Assert.Equal(300d, area, 9);
    }

    [Fact]
    public void RectangleInsideACircle_IsAContainedContour()
    {
        // Here the polygon is the contained one, and the order of the checks must not turn it into
        // "the circle is inside the rectangle".
        var area = ProfileArea.Of(new[] { Circle(0, 0, 10), Rect(-1, -1, 2, 2) })!.Value;

        Assert.Equal((Math.PI * 100d) - 4d, area, 9);
    }

    [Fact]
    public void IdenticalContours_AreNotARegion()
    {
        // Two coincident circles enclose one disk, not a region of zero: the formula refuses rather
        // than cancelling itself out into a meaningless target.
        Assert.Null(ProfileArea.Of(new[] { Circle(0, 0, 10), Circle(0, 0, 10) }));
    }

    [Fact]
    public void OverlappingCircles_AreNotAnalytic()
    {
        // Measured on v24: the extrusion builds the union (5829.873553201979 mm³ for R=10 with centres
        // 15 mm apart, 10 mm deep), but one measured special case is not a general formula — an
        // overlapping pair is reported as uncomputable, never as a sum.
        Assert.Null(ProfileArea.Of(new[] { Circle(0, 0, 10), Circle(15, 0, 10) }));
    }

    [Fact]
    public void TouchingCircles_AreNotAnalytic()
    {
        // Tangency: the region depends on how the kernel resolves the shared point, which is not
        // measured, so neither sum nor difference is claimed.
        Assert.Null(ProfileArea.Of(new[] { Circle(0, 0, 5), Circle(10, 0, 5) }));
    }

    [Fact]
    public void CircleOnTheEdgeOfARectangle_IsNotAnalytic()
    {
        Assert.Null(ProfileArea.Of(new[] { Rect(0, 0, 10, 10), Circle(10, 5, 3) }));
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

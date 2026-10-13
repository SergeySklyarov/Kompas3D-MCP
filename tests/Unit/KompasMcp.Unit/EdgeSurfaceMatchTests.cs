using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Does the surface an edge operation produced really adjoin the edge it was ASKED for?</summary>
/// <remarks>WHY. A fillet addressed by an edge reference was measured on a DIFFERENT corner after the
/// control rebuild, with the volume moved by the same amount in the opposite direction — so on a body whose
/// corners are equivalent the volume cannot tell "built where asked" from "built elsewhere".
/// NUMBERS. Every coordinate below is the measured one: the Г-plate 100×80 minus 40×30 has a convex corner
/// at (50,−40) and a reflex one at (10,10); an R3 fillet puts the cylinder axis at (47,−37) and (13,13),
/// and a 2×2 chamfer cuts the adjacent faces 2 mm from the edge.
/// History: docs/decisions/adapter-features.md#fillet-on-other-edge</remarks>
public class EdgeSurfaceMatchTests
{
    private static readonly double[] EdgeStart = { 50d, -40d, 0d };
    private static readonly double[] EdgeEnd = { 50d, -40d, 10d };

    // The two planes that meet at the requested edge: the face x = 50 and the face y = −40.
    private static readonly double[][] Planes =
    {
        new[] { 50d, 0d, 0d },
        new[] { 0d, -40d, 0d },
    };

    private static readonly double[][] Normals =
    {
        new[] { 1d, 0d, 0d },
        new[] { 0d, 1d, 0d },
    };

    [Fact]
    public void FilletOnTheRequestedConvexCorner_Adjoins()
    {
        var result = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeEnd,
            axisPoint: new[] { 47d, -37d, -12d },   // measured: the placement origin sits off the body
            axisDirection: new[] { 0d, 0d, 1d },
            radiusMm: 3d,
            Planes, Normals);

        Assert.True(result.Adjoins);
        Assert.Null(result.Code);
    }

    [Fact]
    public void FilletOnTheRequestedReflexCorner_Adjoins()
    {
        // The same rule answers for a reflex corner: the axis is still one radius from both faces, only on
        // the other side of them. Nothing in the rule assumes the corner is convex.
        var result = EdgeSurfaceMatch.FilletAgainstEdge(
            new double[] { 10d, 10d, 0d }, new double[] { 10d, 10d, 10d },
            axisPoint: new[] { 13d, 13d, 0d },
            axisDirection: new[] { 0d, 0d, 1d },
            radiusMm: 3d,
            new[] { new[] { 10d, 0d, 0d }, new[] { 0d, 10d, 0d } },
            new[] { new[] { 1d, 0d, 0d }, new[] { 0d, 1d, 0d } });

        Assert.True(result.Adjoins);
    }

    [Fact]
    public void FilletMovedToAnotherCorner_IsRefused()
    {
        // The measured shape of the defect: the SAME radius, on the reflex corner instead of the convex
        // one that was asked for. The volume differs only by the sign of the removed material.
        var result = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeEnd,
            axisPoint: new[] { 13d, 13d, 0d },
            axisDirection: new[] { 0d, 0d, 1d },
            radiusMm: 3d,
            Planes, Normals);

        Assert.False(result.Adjoins);
        Assert.Equal("wrong_distance", result.Code);
        Assert.Equal(3d, result.ExpectedMm);
        Assert.NotNull(result.MeasuredMm);
        Assert.True(result.MeasuredMm > 30d);
    }

    [Fact]
    public void AxisNotParallelToTheEdge_IsRefused()
    {
        var result = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeEnd,
            axisPoint: new[] { 47d, -37d, 0d },
            axisDirection: new[] { 1d, 0d, 0d },
            radiusMm: 3d,
            Planes, Normals);

        Assert.False(result.Adjoins);
        Assert.Equal("not_parallel", result.Code);
    }

    [Fact]
    public void SignOfThePlaneNormalDoesNotMatter()
    {
        // The distance to a plane is taken in absolute value, so an inward normal answers the same as an
        // outward one: the rule must not depend on an orientation the reader may not have resolved.
        var result = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeEnd,
            axisPoint: new[] { 47d, -37d, 0d },
            axisDirection: new[] { 0d, 0d, 1d },
            radiusMm: 3d,
            Planes,
            new[] { new[] { -1d, 0d, 0d }, new[] { 0d, -1d, 0d } });

        Assert.True(result.Adjoins);
    }

    [Fact]
    public void UnreadDirectionOrCoincidentEdgeEnds_IsNotAPlacementFinding()
    {
        // "Could not read" is not "the surface is elsewhere": the two must never share a code.
        var unread = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeEnd, new[] { 47d, -37d, 0d }, new[] { 0d, 0d, 0d }, 3d, Planes, Normals);
        Assert.Equal("not_read", unread.Code);

        var degenerate = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeStart, new[] { 47d, -37d, 0d }, new[] { 0d, 0d, 1d }, 3d, Planes, Normals);
        Assert.Equal("not_read", degenerate.Code);
    }

    [Fact]
    public void FewerThanTwoAdjacentFaces_IsNamed_NotGuessed()
    {
        var result = EdgeSurfaceMatch.FilletAgainstEdge(
            EdgeStart, EdgeEnd, new[] { 47d, -37d, 0d }, new[] { 0d, 0d, 1d }, 3d,
            new[] { new[] { 50d, 0d, 0d } }, new[] { new[] { 1d, 0d, 0d } });

        Assert.False(result.Adjoins);
        Assert.Equal("no_adjacent_faces", result.Code);
    }

    [Fact]
    public void ChamferOnTheRequestedCorner_Adjoins()
    {
        // 2×2 on the corner (50,−40): the chamfer plane is x − y = 88, i.e. the normal (1,−1,0) through
        // (48,−40). Its intersections with x = 50 and y = −40 sit exactly 2 mm from the edge.
        var result = EdgeSurfaceMatch.ChamferAgainstEdge(
            EdgeStart, EdgeEnd,
            facePoint: new[] { 48d, -40d, 5d },
            faceNormal: new[] { 1d, -1d, 0d },
            distance1Mm: 2d,
            distance2Mm: 2d,
            Planes, Normals);

        Assert.True(result.Adjoins);
        Assert.Equal(2d, result.MeasuredMm!.Value, 6);
    }

    [Fact]
    public void ChamferLegsMaySwapFaces()
    {
        // A 2×4 chamfer cuts the two faces at 2 and 4 in either order; which leg lands on which face is
        // what `direction` selects, and the placement is the same either way.
        var result = EdgeSurfaceMatch.ChamferAgainstEdge(
            EdgeStart, EdgeEnd,
            facePoint: new[] { 46d, -40d, 5d },   // x − 2y = 126: through (50,−38) and (46,−40)
            faceNormal: new[] { 1d, -2d, 0d },
            distance1Mm: 4d,
            distance2Mm: 2d,
            Planes, Normals);

        Assert.True(result.Adjoins);
        Assert.Equal(2d, result.MeasuredMm!.Value, 6);   // the first adjacent face carries the 2 mm leg
    }

    [Fact]
    public void ChamferMovedToAnotherCorner_IsRefused()
    {
        // The reflex corner's chamfer (x + y = 22) measured against the requested edge: it cuts x = 50 at
        // y = −28, twelve millimetres from the edge that was asked for.
        var result = EdgeSurfaceMatch.ChamferAgainstEdge(
            EdgeStart, EdgeEnd,
            facePoint: new[] { 10d, 12d, 5d },
            faceNormal: new[] { 1d, 1d, 0d },
            distance1Mm: 2d,
            distance2Mm: 2d,
            Planes, Normals);

        Assert.False(result.Adjoins);
        Assert.Equal("wrong_distance", result.Code);
    }

    [Fact]
    public void ChamferFaceParallelToTheEdgeIsNotAChamferOnIt()
    {
        // A face whose plane is NOT parallel to the edge cannot cut the corner along it.
        var result = EdgeSurfaceMatch.ChamferAgainstEdge(
            EdgeStart, EdgeEnd,
            facePoint: new[] { 48d, -40d, 5d },
            faceNormal: new[] { 0d, 0d, 1d },
            distance1Mm: 2d,
            distance2Mm: 2d,
            Planes, Normals);

        Assert.False(result.Adjoins);
        Assert.Equal("not_parallel", result.Code);
    }

    [Fact]
    public void ChamferFaceParallelToAnAdjacentFace_IsNamed()
    {
        // No intersection line exists at all, so no leg can be measured — a named gap, not a wrong leg.
        var result = EdgeSurfaceMatch.ChamferAgainstEdge(
            EdgeStart, EdgeEnd,
            facePoint: new[] { 48d, -40d, 5d },
            faceNormal: new[] { 1d, 0d, 0d },
            distance1Mm: 2d,
            distance2Mm: 2d,
            Planes, Normals);

        Assert.False(result.Adjoins);
        Assert.Equal("not_parallel", result.Code);
    }

    /// <summary>The distance helper is the one the whole rule rests on, so it is checked against a case
    /// whose answer is known by construction.</summary>
    [Fact]
    public void DistanceToLine_IsPerpendicular_NotAlongTheLine()
    {
        var direction = new[] { 0d, 0d, 1d };
        Assert.Equal(5d, EdgeSurfaceMatch.DistanceToLine(
            new[] { 3d, 4d, 100d }, new[] { 0d, 0d, 0d }, direction), 9);
    }
}

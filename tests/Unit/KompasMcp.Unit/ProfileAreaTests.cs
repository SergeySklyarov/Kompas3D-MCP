using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The analytic profile area turns "KOMPAS returned true" into a verified geometry change
/// (spec 1.11), so its own numbers must be exact and its refusals must be real.</summary>
/// <remarks>The nested cases below are not arithmetic exercises: each was measured on KOMPAS-3D v24 before it
/// was written down (extruded 10 mm from a sketch on XY), and the measured volumes are quoted next to the
/// expectations. The controls — one circle, disjoint contours — are here for the same reason as the refusals:
/// an instrument that cannot pass is as useless as one that cannot refuse.
/// History: docs/decisions/tests.md#profile-area-2</remarks>
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
        // Reporting an area for an unclosed contour would let an extrusion compare its volume against a number
        // that has no meaning.
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
        // A bowtie has a shoelace figure but no single enclosed region — that figure used to be returned as an
        // expectation, the class of defect this test closes.
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
        // The control for every nested case: contours that do not meet are two regions and their areas add up.
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
        // Measured on v24: circles R=10 and r=5, extruded 10 mm, give π·(100−25)·10; the sum π·125 would be wrong.
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
        // Measured on v24: a 100×80 rectangle with an r=10 circle at (50,40), extruded 10 mm, gives (8000 − 100π)·10.
        var area = ProfileArea.Of(new[] { Rect(0, 0, 100, 80), Circle(50, 40, 10) })!.Value;

        Assert.Equal(8000d - (Math.PI * 100d), area, 9);
    }

    [Fact]
    public void TwoHolesInAPlate_AreBothSubtracted()
    {
        // Measured on v24: the same plate with r=10 at (30,40) and r=6 at (70,40) gives (8000 − 100π − 36π)·10.
        var area = ProfileArea.Of(new[] { Rect(0, 0, 100, 80), Circle(30, 40, 10), Circle(70, 40, 6) })!.Value;

        Assert.Equal(8000d - (Math.PI * 136d), area, 9);
    }

    [Fact]
    public void IslandInsideAHole_IsMaterialAgain()
    {
        // Even-odd depth: a contour inside a hole is material. Measured on v24: circles R=10, r=5 and r=2 give
        // π·(100−25+4)·10.
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
        // Measured on v24: the extrusion builds the union for R=10 with centres 15 mm apart, 10 mm deep, but one
        // special case is not a general formula — an overlapping pair is uncomputable, never a sum.
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
        // The measured 80000 mm³ arrives slightly under in KOMPAS; a purely absolute tolerance would be either
        // huge (for big models) or flaky (for small ones).
        Assert.True(ProfileArea.Matches(80000d, 79999.99999999999d));
        Assert.True(ProfileArea.Matches(1e9, 1e9 + 0.5));
        Assert.False(ProfileArea.Matches(80000d, 80001d));
        Assert.False(ProfileArea.Matches(null, 80000d));
        Assert.False(ProfileArea.Matches(80000d, null));
    }

    // ---------------------------------------------------------------------------------------------
    // Chains welded from lines, arcs and open polylines (card GAP-012)
    // ---------------------------------------------------------------------------------------------

    private static SketchEntityDto Line(double x0, double y0, double x1, double y1) => new()
    {
        Kind = SketchEntityKind.Line,
        StartMm = new double[] { x0, y0 },
        EndMm = new double[] { x1, y1 },
    };

    private static SketchEntityDto Arc(double cx, double cy, double r, double startDeg, double sweepDeg) => new()
    {
        Kind = SketchEntityKind.Arc,
        CenterMm = new double[] { cx, cy },
        RadiusMm = r,
        StartDeg = startDeg,
        SweepDeg = sweepDeg,
    };

    private static SketchEntityDto OpenPolyline(params double[][] points) => new()
    {
        Kind = SketchEntityKind.Polyline,
        Closed = false,
        PointsMm = points,
    };

    [Fact]
    public void Slot_TwoLinesAndTwoSemicircles_Is2rLPlusPiR2()
    {
        // A slot of half-length 10 and radius 5: two straight sides of length 20 and two semicircles.
        var area = ProfileArea.Of(new[]
        {
            Line(-10, 5, 10, 5),
            Arc(10, 0, 5, 90, -180),
            Line(10, -5, -10, -5),
            Arc(-10, 0, 5, 270, -180),
        })!.Value;

        Assert.Equal((2d * 5d * 20d) + (Math.PI * 25d), area, 9);
    }

    [Fact]
    public void Slot_BothTraversalDirections_GiveTheSameArea()
    {
        // The region does not depend on the direction the chain is walked: the same slot with every
        // primitive reversed must give the same figure.
        var forward = ProfileArea.Of(new[]
        {
            Line(-10, 5, 10, 5),
            Arc(10, 0, 5, 90, -180),
            Line(10, -5, -10, -5),
            Arc(-10, 0, 5, 270, -180),
        })!.Value;

        var reversed = ProfileArea.Of(new[]
        {
            Line(10, 5, -10, 5),
            Arc(-10, 0, 5, 90, 180),
            Line(-10, -5, 10, -5),
            Arc(10, 0, 5, 270, 180),
        })!.Value;

        Assert.Equal(forward, reversed, 9);
    }

    [Fact]
    public void Semicircle_LineAndArc_IsHalfDisk()
    {
        var area = ProfileArea.Of(new[]
        {
            Arc(0, 0, 5, 0, 180),
            Line(-5, 0, 5, 0),
        })!.Value;

        Assert.Equal(Math.PI * 25d / 2d, area, 9);
    }

    [Fact]
    public void MajorArc_IsTheMajorSegment_NotTheMinorOne()
    {
        // A sweep beyond 180° must be honoured: the chord closes the MAJOR segment, whose area is
        // r²(θ−sinθ)/2 with θ = 270°, i.e. 71.404862…, not the minor segment's 7.134962…
        var area = ProfileArea.Of(new[]
        {
            Arc(0, 0, 5, 0, 270),
            Line(0, -5, 5, 0),
        })!.Value;

        Assert.Equal(0.5d * 25d * ((3d * Math.PI / 2d) - Math.Sin(3d * Math.PI / 2d)), area, 9);
        Assert.True(area > Math.PI * 25d / 2d);
    }

    [Fact]
    public void Ring_FromTwoChains_IsAFrame()
    {
        // Two contours, each welded from four separate lines: the inner one is a hole.
        var area = ProfileArea.Of(new[]
        {
            Line(0, 0, 20, 0), Line(20, 0, 20, 20), Line(20, 20, 0, 20), Line(0, 20, 0, 0),
            Line(5, 5, 15, 5), Line(15, 5, 15, 15), Line(15, 15, 5, 15), Line(5, 15, 5, 5),
        })!.Value;

        Assert.Equal(300d, area, 9);
    }

    [Fact]
    public void ChainFromTwoOpenPolylines_IsOneContour()
    {
        // Card PERF-008: a contour larger than one polyline is drawn as several polylines sharing their
        // end vertices. The area must be that of the single closed contour, not of each piece.
        var area = ProfileArea.Of(new[]
        {
            OpenPolyline(new double[] { 0, 0 }, new double[] { 10, 0 }, new double[] { 10, 10 }),
            OpenPolyline(new double[] { 10, 10 }, new double[] { 0, 10 }, new double[] { 0, 0 }),
        })!.Value;

        Assert.Equal(100d, area, 9);
    }

    [Fact]
    public void BranchingChain_IsNotAnArea()
    {
        // Three ends meet at the origin: the region is not a simple closed contour.
        Assert.Null(ProfileArea.Of(new[]
        {
            Line(0, 0, 10, 0),
            Line(0, 0, 0, 10),
            Line(0, 0, -10, 0),
        }));
    }

    [Fact]
    public void SelfIntersectingChain_IsNotAnArea()
    {
        // A bowtie of four lines is closed and has a shoelace figure, but no single region.
        Assert.Null(ProfileArea.Of(new[]
        {
            Line(0, 0, 10, 10),
            Line(10, 10, 10, 0),
            Line(10, 0, 0, 10),
            Line(0, 10, 0, 0),
        }));
    }

    [Fact]
    public void OpenChain_OfLinesAndArcs_IsNotAnArea()
    {
        // The chain does not close: the last end (0, 5) is 5 mm from the first (-10, 5).
        Assert.Null(ProfileArea.Of(new[]
        {
            Line(-10, 5, 10, 5),
            Arc(10, 0, 5, 90, -180),
            Line(10, -5, 0, -5),
        }));
    }

    [Fact]
    public void UncomputableProfile_NamesItsReason()
    {
        var outcome = ProfileArea.Compute(new[]
        {
            Line(0, 0, 10, 0),
            Line(0, 0, 0, 10),
            Line(0, 0, -10, 0),
        });

        Assert.Null(outcome.AreaMm2);
        Assert.False(string.IsNullOrWhiteSpace(outcome.UnavailableReason));
        Assert.Contains("ветв", outcome.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ArcByEndPoints_IsTheSameArcAsByAngles()
    {
        // DOC: ksdocument2d_ksarcbypoint.html — centre + radius + start point + end point + direction.
        // The two forms must name the same arc, so the semicircle by points equals the semicircle by angles.
        var byAngles = ProfileArea.Of(new[]
        {
            Arc(0, 0, 5, 0, 180),
            Line(-5, 0, 5, 0),
        })!.Value;

        var byPoints = ProfileArea.Of(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Arc,
                CenterMm = new double[] { 0, 0 },
                RadiusMm = 5,
                StartPointMm = new double[] { 5, 0 },
                EndPointMm = new double[] { -5, 0 },
                Clockwise = false,
            },
            Line(-5, 0, 5, 0),
        })!.Value;

        Assert.Equal(byAngles, byPoints, 9);
        Assert.Equal(Math.PI * 25d / 2d, byPoints, 9);
    }

    [Fact]
    public void ArcByEndPoints_Clockwise_TakesTheComplementarySide()
    {
        // The same two points clockwise sweep the OTHER way: 180° CCW becomes 180° CW here, but with
        // distinct points the complement differs — start (5,0) to end (0,5) is 90° CCW and 270° CW.
        var ccw = ProfileArea.Of(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Arc,
                CenterMm = new double[] { 0, 0 },
                RadiusMm = 5,
                StartPointMm = new double[] { 5, 0 },
                EndPointMm = new double[] { 0, 5 },
                Clockwise = false,
            },
            Line(0, 5, 5, 0),
        })!.Value;

        var clockwise = ProfileArea.Of(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Arc,
                CenterMm = new double[] { 0, 0 },
                RadiusMm = 5,
                StartPointMm = new double[] { 5, 0 },
                EndPointMm = new double[] { 0, 5 },
                Clockwise = true,
            },
            Line(0, 5, 5, 0),
        })!.Value;

        // 90° segment: 0.5·25·(π/2 − 1); 270° segment: 0.5·25·(3π/2 + 1).
        Assert.Equal(0.5d * 25d * ((Math.PI / 2d) - 1d), ccw, 9);
        Assert.Equal(0.5d * 25d * ((3d * Math.PI / 2d) + 1d), clockwise, 9);
    }

    [Fact]
    public void RealPartContour_FromClientReference_MatchesTheMeasuredVolume()
    {
        // The client contour of card GAP-012 (CP04a): 21 arcs and 4 lines, one closed chain. The expectation
        // is analytic (Green's theorem over the primitives, including each arc's circular segment) and was
        // independently confirmed by KOMPAS: extruding it 4 mm gave the volume asserted below.
        var entities = ClientContourEntities();
        var area = ProfileArea.Of(entities)!.Value;

        Assert.Equal(25, entities.Count);
        Assert.True(Math.Abs(area - 8474.982482571266d) <= 1e-6,
            $"площадь {area:R} против аналитической 8474.982482571266");
        Assert.True(Math.Abs((area * 4d) - 33899.929930285063d) <= 4e-6,
            $"объём {area * 4d:R} против измеренного КОМПАС 33899.92993028507");
    }

    private static IReadOnlyList<SketchEntityDto> ClientContourEntities()
    {
        var assembly = typeof(ProfileAreaTests).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("plate_cp04a_mcp.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(stream);
        var entities = document.RootElement
            .GetProperty("variants").GetProperty("exact_closure").GetProperty("entities");
        var options = new JsonSerializerOptions
        {
            // The file is the client's own contract: snake_case names and snake_case enum values.
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        return entities.Deserialize<List<SketchEntityDto>>(options)!;
    }
}

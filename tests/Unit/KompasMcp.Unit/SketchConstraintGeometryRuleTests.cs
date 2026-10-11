using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>TEST: the constraint relation rules are the ONLY place "the geometry obeyed" is decided, so
/// every branch is pinned here without КОМПАС — including the two ways a rule must REFUSE to answer: a
/// relation that does not hold, and a relation that is not a number at all.</summary>
/// <remarks>History: docs/decisions/tests.md#constraint-geometry</remarks>
public sealed class SketchConstraintGeometryRuleTests
{
    private static SketchShape Segment(double x1, double y1, double x2, double y2) =>
        new(SketchShapeKind.Segment, X1: x1, Y1: y1, X2: x2, Y2: y2);

    private static SketchShape Point(double x, double y) => new(SketchShapeKind.Point, X: x, Y: y);

    private static SketchShape Circle(double cx, double cy, double r) =>
        new(SketchShapeKind.Circle, CenterX: cx, CenterY: cy, Radius: r);

    private static SketchShape Arc(double cx, double cy, double r, double mx, double my) =>
        new(SketchShapeKind.Arc, CenterX: cx, CenterY: cy, Radius: r, MidX: mx, MidY: my);

    [Fact]
    public void Horizontal_HoldsWhenTheSegmentLiesAlongX()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCHorizontal", Segment(0, 0, 10, 0), null, null, null, null);
        Assert.True(verdict.Holds);
        Assert.Equal(0d, verdict.Measured!.Value, 6);
    }

    [Fact]
    public void Horizontal_DoesNotHoldAtThirtyDegrees()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCHorizontal", Segment(0, 0, 25, 14.43), null, null, null, null);
        Assert.False(verdict.Holds);
        Assert.True(verdict.Expresses);
        Assert.True(verdict.Measured > 1d);
    }

    /// <summary>180° is horizontal too: the rule must fold the direction, not compare against 0 alone.</summary>
    [Fact]
    public void Horizontal_HoldsWhenTheSegmentPointsTheOtherWay()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCHorizontal", Segment(10, 0, 0, 0), null, null, null, null);
        Assert.True(verdict.Holds);
    }

    [Fact]
    public void Vertical_HoldsAtNinetyDegrees()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCVertical", Segment(0, 0, 0, 10), null, null, null, null);
        Assert.True(verdict.Holds);
    }

    [Fact]
    public void Vertical_DoesNotHoldOnAHorizontalSegment()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCVertical", Segment(0, 0, 10, 0), null, null, null, null);
        Assert.False(verdict.Holds);
    }

    [Fact]
    public void Parallel_HoldsForOppositeDirectionsToo()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCParallel",
            Segment(0, 0, 10, 0), null, Segment(0, 5, -10, 5), null, null);
        Assert.True(verdict.Holds);
    }

    [Fact]
    public void Perpendicular_HoldsAtNinetyDegrees()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCPerpendicular",
            Segment(0, 0, 10, 0), null, Segment(0, 0, 0, 10), null, null);
        Assert.True(verdict.Holds);
    }

    [Fact]
    public void Perpendicular_DoesNotHoldAtSeventyDegrees()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCPerpendicular",
            Segment(0, 0, 10, 0), null, Segment(0, 0, 3.42, 9.4), null, null);
        Assert.False(verdict.Holds);
        Assert.True(verdict.Measured < 90d);
    }

    [Fact]
    public void Collinear_NeedsBothParallelismAndZeroDistance()
    {
        var onLine = SketchConstraintGeometryRule.Evaluate("ksCCollinear",
            Segment(0, 0, 10, 0), null, Segment(20, 0, 30, 0), null, null);
        Assert.True(onLine.Holds);

        var parallelButOffset = SketchConstraintGeometryRule.Evaluate("ksCCollinear",
            Segment(0, 0, 10, 0), null, Segment(0, 5, 10, 5), null, null);
        Assert.False(parallelButOffset.Holds);
    }

    [Fact]
    public void EqualLength_ComparesTheTwoSegments()
    {
        var equal = SketchConstraintGeometryRule.Evaluate("ksCEqualLength",
            Segment(0, 0, 10, 0), null, Segment(0, 5, 10, 5), null, null);
        Assert.True(equal.Holds);

        var unequal = SketchConstraintGeometryRule.Evaluate("ksCEqualLength",
            Segment(0, 0, 10, 0), null, Segment(0, 5, 12, 5), null, null);
        Assert.False(unequal.Holds);
    }

    [Fact]
    public void EqualRadius_ComparesTwoCircularObjects()
    {
        var equal = SketchConstraintGeometryRule.Evaluate("ksCEqualRadius", Circle(0, 0, 10), null, Circle(30, 0, 10), null, null);
        Assert.True(equal.Holds);

        var unequal = SketchConstraintGeometryRule.Evaluate("ksCEqualRadius", Circle(0, 0, 10), null, Arc(30, 0, 12, 42, 0), null, null);
        Assert.False(unequal.Holds);
    }

    [Fact]
    public void HAlignPoints_ComparesYOfTwoAddressedPoints()
    {
        // Two segments, index 1 of each: their far ends must share Y.
        var held = SketchConstraintGeometryRule.Evaluate("ksCHAlignPoints",
            Segment(0, 0, 10, 7), 1, Segment(20, 0, 30, 7), 1, null);
        Assert.True(held.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCHAlignPoints",
            Segment(0, 0, 10, 7), 1, Segment(20, 0, 30, 9), 1, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void VAlignPoints_ComparesXOfTwoAddressedPoints()
    {
        var held = SketchConstraintGeometryRule.Evaluate("ksCVAlignPoints",
            Segment(5, 0, 5, 10), 0, Segment(5, 20, 20, 20), 0, null);
        Assert.True(held.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCVAlignPoints",
            Segment(5, 0, 5, 10), 0, Segment(6, 20, 20, 20), 0, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void MergePoints_NeedsCoincidence()
    {
        var held = SketchConstraintGeometryRule.Evaluate("ksCMergePoints",
            Segment(0, 0, 10, 0), 1, Segment(10, 0, 10, 10), 0, null);
        Assert.True(held.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCMergePoints",
            Segment(0, 0, 10, 0), 1, Segment(11, 0, 10, 10), 0, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void Concentricity_ComparesCentres()
    {
        var held = SketchConstraintGeometryRule.Evaluate("ksCConcentricity", Circle(3, 4, 10), null, Arc(3, 4, 4, 7, 4), null, null);
        Assert.True(held.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCConcentricity", Circle(3, 4, 10), null, Circle(3, 5, 4), null, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void PointOnCurve_MeasuresDistanceToTheSegment()
    {
        var on = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurve", Point(5, 0), null, Segment(0, 0, 10, 0), null, null);
        Assert.True(on.Holds);

        var off = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurve", Point(5, 2), null, Segment(0, 0, 10, 0), null, null);
        Assert.False(off.Holds);
        Assert.Equal(2d, off.Measured!.Value, 6);
    }

    [Fact]
    public void PointOnCurve_MeasuresRadialMissOnACircle()
    {
        var on = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurve", Point(10, 0), null, Circle(0, 0, 10), null, null);
        Assert.True(on.Holds);

        var off = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurve", Point(12, 0), null, Circle(0, 0, 10), null, null);
        Assert.False(off.Holds);
    }

    /// <summary>The object of these two types is a POINT, not a curve (MEASURED): a curve in that place
    /// is a named NotExpressible, never a silent pass.</summary>
    [Fact]
    public void PointOnCurve_RefusesWhenTheObjectIsNotAPoint()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurve", Segment(0, 0, 10, 0), null, Circle(0, 0, 10), null, null);
        Assert.False(verdict.Expresses);
        Assert.Contains("не точка", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void PointOnCurveMiddle_ComparesAgainstTheMidpoint()
    {
        var held = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurveMiddle", Point(5, 0), null, Segment(0, 0, 10, 0), null, null);
        Assert.True(held.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCPointOnCurveMiddle", Point(5, 1), null, Segment(0, 0, 10, 0), null, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void Tangent_LineToCircleComparesCentreDistanceToRadius()
    {
        // A horizontal line at y = 10 touches a circle of radius 10 centred on the origin.
        var held = SketchConstraintGeometryRule.Evaluate("ksCTangentTwoCurves",
            Segment(-20, 10, 20, 10), null, Circle(0, 0, 10), null, null);
        Assert.True(held.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCTangentTwoCurves",
            Segment(-20, 5, 20, 5), null, Circle(0, 0, 10), null, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void Tangent_TwoCirclesAcceptSumOrDifference()
    {
        var external = SketchConstraintGeometryRule.Evaluate("ksCTangentTwoCurves", Circle(0, 0, 10), null, Circle(25, 0, 15), null, null);
        Assert.True(external.Holds);

        var inner = SketchConstraintGeometryRule.Evaluate("ksCTangentTwoCurves", Circle(0, 0, 10), null, Circle(5, 0, 15), null, null);
        Assert.True(inner.Holds);

        var missed = SketchConstraintGeometryRule.Evaluate("ksCTangentTwoCurves", Circle(0, 0, 10), null, Circle(20, 0, 15), null, null);
        Assert.False(missed.Holds);
    }

    [Fact]
    public void Symmetry_NeedsTheMidpointOnTheAxisAndTheChordPerpendicularToIt()
    {
        // Axis is the Y axis (a vertical segment through the origin).
        var held = SketchConstraintGeometryRule.Evaluate("ksCSymmetryTwoPoints",
            Point(-4, 3), null, Point(4, 3), null, Segment(0, -10, 0, 10));
        Assert.True(held.Holds);

        var offAxis = SketchConstraintGeometryRule.Evaluate("ksCSymmetryTwoPoints",
            Point(-4, 3), null, Point(4, 5), null, Segment(0, -10, 0, 10));
        Assert.False(offAxis.Holds);
    }

    [Fact]
    public void Symmetry_RefusesWithoutAnAxis()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCSymmetryTwoPoints",
            Point(-4, 3), null, Point(4, 3), null, null);
        Assert.False(verdict.Expresses);
        Assert.Contains("ось", verdict.Reason!, StringComparison.Ordinal);
    }

    /// <summary>Types whose relation is not a number read right after the write are NAMED, each with its
    /// reason — the alternative would be a silent pass.</summary>
    [Theory]
    [InlineData("ksCFixedPoint")]
    [InlineData("ksCFixedAngle")]
    [InlineData("ksCFixedLenght")]
    [InlineData("ksCAssociation")]
    [InlineData("ksCFixedDim")]
    [InlineData("ksCDimWithVariable")]
    public void TemporalAndDimensionTypes_AreNamedNotExpressible(string type)
    {
        var verdict = SketchConstraintGeometryRule.Evaluate(type, Segment(0, 0, 10, 0), null, null, null, null);
        Assert.False(verdict.Expresses);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        Assert.Equal(type, verdict.Relation);
    }

    /// <summary>The bisector's placement is not described by the help, so the rule checks the RELATION:
    /// the object makes the same acute angle with the partner and with the axis.</summary>
    [Fact]
    public void Bisector_HoldsWhenTheObjectSplitsTheAngleInTwo()
    {
        // Partner at 0°, axis at 60°, object at 30° — the bisector.
        var held = SketchConstraintGeometryRule.Evaluate("ksCBisector",
            Segment(0, 0, 10, 5.773502691896258), null, Segment(0, 0, 10, 0), null,
            Segment(0, 0, 5, 8.660254037844387));
        Assert.True(held.Holds);

        // Object at 45° is NOT the bisector of 0°/60°.
        var missed = SketchConstraintGeometryRule.Evaluate("ksCBisector",
            Segment(0, 0, 10, 10), null, Segment(0, 0, 10, 0), null,
            Segment(0, 0, 5, 8.660254037844387));
        Assert.False(missed.Holds);
        Assert.True(missed.Expresses);
    }

    [Fact]
    public void UnknownType_IsNotExpressibleRatherThanSilentlyPassing()
    {
        var verdict = SketchConstraintGeometryRule.Evaluate("ksCInvented", Segment(0, 0, 10, 0), null, null, null, null);
        Assert.False(verdict.Expresses);
    }
}

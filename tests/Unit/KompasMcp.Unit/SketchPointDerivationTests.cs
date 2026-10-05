using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The decision layer behind the model-derived sketch coordinate: when a point read out of a dependent
/// body may point at an existing sketch primitive, and when the only honest answer is a refusal.</summary>
/// <remarks>Every case is a boundary of one measurement. MEASURED (probe G): the derivation works for a base XY
/// sketch with a circular profile consumed by a through cut, and nothing else was measured. The boundary is pinned
/// so a later change cannot widen it by accident — widening wrongly deletes the wrong sketch object on a model
/// that cannot be rolled back. The numbers are the probe's own and are asserted below.
/// History: docs/decisions/tests.md#sketch-point-2</remarks>
public class SketchPointDerivationTests
{
    private static IReadOnlyCollection<SketchEntityKind> Circle() =>
        new[] { SketchEntityKind.Circle };

    // Where the derivation is allowed.

    [Fact]
    public void BaseXyWithCircularProfileIsAllowed()
    {
        var verdict = SketchPointDerivation.Verdict(PlaneBase.Xy, Circle());

        Assert.True(verdict.IsAllowed);
        Assert.Null(verdict.ReasonCode);
    }

    [Theory]
    [InlineData(PlaneBase.Xz)]
    [InlineData(PlaneBase.Yz)]
    public void OtherBasePlanesAreRefused(PlaneBase plane)
    {
        // Probe G measured the 3D→2D transport for the base XY plane only; on XZ or YZ the face origin's (x, y)
        // are not sketch coordinates, and reusing them would point the search at an arbitrary place.
        var verdict = SketchPointDerivation.Verdict(plane, Circle());

        Assert.False(verdict.IsAllowed);
        Assert.Equal("plane_not_xy", verdict.ReasonCode);
    }

    [Fact]
    public void UnknownPlaneIsRefusedRatherThanAssumed()
    {
        // A sketch from a reopened document has no remembered plane, and a referenced-plane sketch has none
        // either; assuming XY because it is common is the silent redefinition the project forbids.
        var verdict = SketchPointDerivation.Verdict(null, Circle());

        Assert.False(verdict.IsAllowed);
        Assert.Equal("plane_not_xy", verdict.ReasonCode);
    }

    [Theory]
    [InlineData(SketchEntityKind.Line)]
    [InlineData(SketchEntityKind.Arc)]
    [InlineData(SketchEntityKind.Rectangle)]
    [InlineData(SketchEntityKind.Polyline)]
    public void NonCircularProfilesAreRefused(SketchEntityKind kind)
    {
        // The point comes from a cylindrical face and lies on a circle; for a segment, arc, rectangle or polyline
        // there is no such coordinate, and handing one over would let ksFindObj hit whatever is nearby.
        var verdict = SketchPointDerivation.Verdict(PlaneBase.Xy, new[] { kind });

        Assert.False(verdict.IsAllowed);
        Assert.Equal("profile_not_circle", verdict.ReasonCode);
    }

    [Fact]
    public void MixedProfileWithAnyNonCircleIsRefused()
    {
        // One circle in the batch is not enough: the coordinate clears the whole profile, so every primitive it
        // must reach is one the point can lie on.
        var verdict = SketchPointDerivation.Verdict(
            PlaneBase.Xy,
            new[] { SketchEntityKind.Circle, SketchEntityKind.Line });

        Assert.False(verdict.IsAllowed);
        Assert.Equal("profile_not_circle", verdict.ReasonCode);
    }

    [Fact]
    public void EmptyProfileIsRefusedWithItsOwnReason()
    {
        // delete_entities carries no primitives — legal, but not a profile to derive a point for, and saying so
        // beats a confusing "not a circle" message.
        var verdict = SketchPointDerivation.Verdict(PlaneBase.Xy, Array.Empty<SketchEntityKind>());

        Assert.False(verdict.IsAllowed);
        Assert.Equal("profile_empty", verdict.ReasonCode);
    }

    // The point itself.

    [Fact]
    public void PointLiesOnTheCircleAtBothEndsOfADiameter()
    {
        // Probe G used (cx + r; cy) and checked that (cx + 2r; cy) and (cx; cy) find nothing. Both ends of a
        // diameter are returned so a radius that coincides with other geometry still has a second chance.
        var points = SketchPointDerivation.PointsOnCircle(new[] { 10d, -4d }, 12d);

        Assert.Equal(new[] { 22d, -4d }, points[0]);
        Assert.Equal(new[] { -2d, -4d }, points[1]);
    }

    [Theory]
    [InlineData(0d, 0d, 1d, true)]
    [InlineData(0d, 0d, -1d, true)]
    public void CylinderAxisAlongZIsAcceptedInEitherDirection(double x, double y, double z, bool expected)
    {
        // An axis has no inherent sign: the same hole read from the other side reports the opposite vector, and
        // rejecting one would make the route depend on how KOMPAS oriented a face.
        Assert.Equal(expected, SketchPointDerivation.AxisIsNormalToXyPlane(new[] { x, y, z }));
    }

    [Theory]
    [InlineData(1d, 0d, 0d)]
    [InlineData(0d, 1d, 0d)]
    [InlineData(0.7071d, 0d, 0.7071d)]
    [InlineData(0d, 0d, 0.5d)]
    public void CylinderAxisNotAlongZIsRejected(double x, double y, double z) =>
        Assert.False(SketchPointDerivation.AxisIsNormalToXyPlane(new[] { x, y, z }));

    [Fact]
    public void MissingAxisIsRejected()
    {
        Assert.False(SketchPointDerivation.AxisIsNormalToXyPlane(null));
        Assert.False(SketchPointDerivation.AxisIsNormalToXyPlane(Array.Empty<double>()));
    }

    // Cross-checks carried over from the probe.

    [Fact]
    public void LateralAreaMatchesTheProbesSecondIndependentCheck()
    {
        // Probe G confirmed the face was the hole wall by area as well as radius — radius alone cannot tell one
        // cylinder from another. R12 through a 10 mm plate is 2π·12·10, the acceptance figure.
        var area = SketchPointDerivation.LateralAreaMm2(12d, 10d);

        Assert.Equal(753.98223686155d, area, 9);
    }

    [Fact]
    public void RadiusAgreementUsesTheMeasuredTolerance()
    {
        Assert.True(SketchPointDerivation.RadiusAgrees(10d, 10d));
        Assert.True(SketchPointDerivation.RadiusAgrees(10d + 1e-9, 10d));
        Assert.False(SketchPointDerivation.RadiusAgrees(10d, 12d));
    }
}

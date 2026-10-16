using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>What a removed face, a drilled axis and a tilted plane prove about WHERE the operation
/// landed. Every input is a number, so a substituted reading exercises each branch.</summary>
/// <remarks>WHY. The reference to a face may re-resolve to a different face of the same topology, and on
/// a body whose faces are equivalent the volume cannot tell "acted where asked" from "acted elsewhere".
/// History: docs/decisions/adapter-features.md#edge-placement-other-ops</remarks>
public class FacePlacementMatchTests
{
    private static FacePlacementMatch.PlanarFace Face(double[] point, double[] normal, double area) =>
        new(point, normal, area);

    // --------------------------------------------------------------------------- shell

    /// <summary>Plate 100 x 80 x 10: the top face (area 8000, plane z = 10) is asked to be removed, and
    /// after the operation the body carries no face in that plane.</summary>
    [Fact]
    public void RemovedFaceGone_WhenNoFaceStaysInTheRequestedPlane()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);
        var after = new[]
        {
            Face([0d, 0d, 0d], [0d, 0d, -1d], 8000d),      // bottom, parallel but elsewhere
            Face([0d, 0d, 8d], [0d, 0d, 1d], 7584d),      // cavity bottom, parallel but elsewhere
            Face([50d, 0d, 5d], [1d, 0d, 0d], 1000d),     // side
        };

        var result = FacePlacementMatch.RemovedFaceGone(requested, after);

        Assert.Equal(FacePlacementMatch.Verdict.Confirmed, result.Verdict);
        Assert.False(result.IsRefusal);
    }

    /// <summary>The measured defect class: the reference re-resolved, another face went away and the
    /// requested one is still there at full area.</summary>
    [Fact]
    public void RemovedFaceGone_RefusesWhenTheRequestedFaceIsStillThere()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);
        var after = new[]
        {
            Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d),
            Face([0d, 0d, 0d], [0d, 0d, -1d], 8000d),
        };

        var result = FacePlacementMatch.RemovedFaceGone(requested, after);

        Assert.True(result.IsRefusal);
        Assert.Equal("shell_face_not_removed", result.Code);
    }

    /// <summary>The normal's SIGN is not consulted: the same plane reached with the opposite normal is
    /// still the requested plane.</summary>
    [Fact]
    public void RemovedFaceGone_IgnoresTheSignOfTheNormal()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);
        var after = new[] { Face([0d, 0d, 10d], [0d, 0d, -1d], 8000d) };

        var result = FacePlacementMatch.RemovedFaceGone(requested, after);

        Assert.True(result.IsRefusal);
        Assert.Equal("shell_face_not_removed", result.Code);
    }

    /// <summary>A COPLANAR SIBLING smaller than the requested face is not "the requested face is still
    /// there": the gap is named rather than turned into a refusal.</summary>
    [Fact]
    public void RemovedFaceGone_NamesTheGapForASmallerCoplanarRemainder()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);
        var after = new[] { Face([0d, 0d, 10d], [0d, 0d, 1d], 500d) };

        var result = FacePlacementMatch.RemovedFaceGone(requested, after);

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
        Assert.False(result.IsRefusal);
    }

    [Fact]
    public void RemovedFaceGone_NamesTheGapWhenTheRequestedNormalIsNotRead()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 0d], 8000d);

        var result = FacePlacementMatch.RemovedFaceGone(requested, []);

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
    }

    [Fact]
    public void RemovedFaceGone_NamesTheGapWhenTheRequestedAreaIsNotRead()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], double.NaN);

        var result = FacePlacementMatch.RemovedFaceGone(requested, []);

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
    }

    // --------------------------------------------------------------------------- hole

    /// <summary>A hole drilled into the top face (normal +Z) of the plate: axis (0, 0, 1), cylinder from
    /// z = 0 to z = 10, so the requested plane cuts it.</summary>
    [Fact]
    public void HoleAxisOnFace_ConfirmsAHoleThroughTheRequestedPlane()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [25d, 15d, 0d], [0d, 0d, 1d], 10d);

        Assert.Equal(FacePlacementMatch.Verdict.Confirmed, result.Verdict);
    }

    /// <summary>The reference re-resolved onto a SIDE face: the axis is normal to that face and no
    /// longer perpendicular to the requested one.</summary>
    [Fact]
    public void HoleAxisOnFace_RefusesAnAxisNormalToAnotherFace()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [50d, 15d, 5d], [1d, 0d, 0d], 10d);

        Assert.True(result.IsRefusal);
        Assert.Equal("hole_on_other_face", result.Code);
    }

    /// <summary>The axis is parallel to the requested normal, but the whole cylinder sits on the inner
    /// side of the requested plane: the hole was drilled from the opposite, parallel face.</summary>
    [Fact]
    public void HoleAxisOnFace_RefusesACylinderThatDoesNotReachTheRequestedPlane()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [25d, 15d, 0d], [0d, 0d, 1d], 6d);

        Assert.True(result.IsRefusal);
        Assert.Equal("hole_on_other_face", result.Code);
    }

    /// <summary>A blind hole drilled from the requested face reaches it at one end of its cylinder.</summary>
    [Fact]
    public void HoleAxisOnFace_ConfirmsABlindHoleDrilledFromTheRequestedFace()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [25d, 15d, 4d], [0d, 0d, 1d], 6d);

        Assert.Equal(FacePlacementMatch.Verdict.Confirmed, result.Verdict);
    }

    /// <summary>The normal's sign is irrelevant here too: a base face read with an inward normal answers
    /// the same.</summary>
    [Fact]
    public void HoleAxisOnFace_IgnoresTheSignOfTheFaceNormal()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, -1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [25d, 15d, 0d], [0d, 0d, 1d], 10d);

        Assert.Equal(FacePlacementMatch.Verdict.Confirmed, result.Verdict);
    }

    /// <summary>An unread cylinder length leaves the direction proven and the entrance NAMED as a gap,
    /// not refused.</summary>
    [Fact]
    public void HoleAxisOnFace_NamesTheGapWhenTheCylinderLengthIsNotRead()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [25d, 15d, 0d], [0d, 0d, 1d], null);

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
        Assert.False(result.IsRefusal);
    }

    [Fact]
    public void HoleAxisOnFace_NamesTheGapWhenTheAxisDirectionIsNotRead()
    {
        var requested = Face([0d, 0d, 10d], [0d, 0d, 1d], 8000d);

        var result = FacePlacementMatch.HoleAxisOnFace(requested, [25d, 15d, 0d], [0d, 0d, 0d], 10d);

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
    }

    // --------------------------------------------------------------------- angled plane

    /// <summary>Angled plane built about the requested base line at the requested angle.</summary>
    [Fact]
    public void AngledPlaneOnBaseLine_ConfirmsTheRequestedLineAndAngle()
    {
        var result = FacePlacementMatch.AngledPlaneOnBaseLine(30d, 30d, "Ось 1", "Ось 1");

        Assert.Equal(FacePlacementMatch.Verdict.Confirmed, result.Verdict);
    }

    /// <summary>The measured class: the base-line reference re-resolved onto another axis.</summary>
    [Fact]
    public void AngledPlaneOnBaseLine_RefusesAnotherBaseLine()
    {
        var result = FacePlacementMatch.AngledPlaneOnBaseLine(30d, 30d, "Ось 1", "Ось 2");

        Assert.True(result.IsRefusal);
        Assert.Equal("plane_on_other_base_line", result.Code);
    }

    [Fact]
    public void AngledPlaneOnBaseLine_RefusesAnotherAngle()
    {
        var result = FacePlacementMatch.AngledPlaneOnBaseLine(30d, 45d, "Ось 1", "Ось 1");

        Assert.True(result.IsRefusal);
        Assert.Equal("plane_on_other_angle", result.Code);
    }

    /// <summary>No base line was named, so the angle is taken from the base plane: the name is not
    /// compared and the angle alone decides.</summary>
    [Fact]
    public void AngledPlaneOnBaseLine_ConfirmsWithoutABaseLine()
    {
        var result = FacePlacementMatch.AngledPlaneOnBaseLine(30d, 30d, null, null);

        Assert.Equal(FacePlacementMatch.Verdict.Confirmed, result.Verdict);
    }

    [Fact]
    public void AngledPlaneOnBaseLine_NamesTheGapWhenTheBaseLineNameIsNotRead()
    {
        var result = FacePlacementMatch.AngledPlaneOnBaseLine(30d, 30d, "Ось 1", null);

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
        Assert.False(result.IsRefusal);
    }

    [Fact]
    public void AngledPlaneOnBaseLine_NamesTheGapWhenTheAngleIsNotRead()
    {
        var result = FacePlacementMatch.AngledPlaneOnBaseLine(30d, null, "Ось 1", "Ось 1");

        Assert.Equal(FacePlacementMatch.Verdict.Unverified, result.Verdict);
    }
}

using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// The two decisions about an extrusion's target body that do not need КОМПАС: which operations may
/// name a body, and whether a drawn profile can plausibly lie over the body that was named.
/// </summary>
/// <remarks>
/// The second one exists because probe P2.6 measured a silent no-op: declaring a body the contour
/// does not sit over makes SetSketch, Create and RebuildDocument all answer true while no body
/// changes volume at all. A server that cannot see the contradiction must not be able to report the
/// result as success, and the numbers below are the two configurations that measurement produced —
/// a 100×80 plate at x∈[-50,50] and a blob at x∈[138,162] with a Ø10 contour over it.
///
/// The axis correspondences asserted here are the ones measured by probe P2.4 and re-asserted by
/// acceptance rows G07_xy/G07_xz/G07_yz, not a convention invented for the test.
/// </remarks>
public class TargetBodyGuardTests
{
    private static readonly double[] PlateMin = { -50d, -40d, 0d };
    private static readonly double[] PlateMax = { 50d, 40d, 10d };
    private static readonly double[] BlobMin = { 138d, -12d, 0d };
    private static readonly double[] BlobMax = { 162d, 12d, 10d };

    /// <summary>The Ø10 contour the probe cut into the blob: centre (150, 0), radius 5.</summary>
    private static readonly ProfileBox HoleOverBlob = new(145d, -5d, 155d, 5d);

    /// <summary>The same contour moved onto the plate: centre (-20, 0), radius 5.</summary>
    private static readonly ProfileBox HoleOverPlate = new(-25d, -5d, -15d, 5d);

    private static SketchEntityDto Circle(double u, double v, double radius) =>
        new() { Kind = SketchEntityKind.Circle, CenterMm = new[] { u, v }, RadiusMm = radius };

    private static SketchEntityDto Rectangle(double u, double v, double width, double height) =>
        new()
        {
            Kind = SketchEntityKind.Rectangle,
            StartMm = new[] { u, v },
            WidthMm = width,
            HeightMm = height,
        };

    // ---------------------------------------------------------------------------------------------
    // Which operation is allowed to name a body
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("base", true, true)]
    [InlineData("boss", true, false)]
    [InlineData("cut", true, false)]
    [InlineData("base", false, false)]
    [InlineData(null, false, false)]
    public void BaseRejectsTargetBodyRef_BossAndCutRequireIt(string? operation, bool targetProvided, bool refused)
    {
        Assert.Equal(
            refused,
            TargetBodyGuard.TargetBodyRefusedForOperation(operation, targetProvided));
    }

    [Fact]
    public void AbsentTargetBody_IsNeverARefusalOfThisKind()
    {
        // "base" without a target is the normal call; confusing the two rules would reject every
        // first extrusion in existence.
        Assert.False(TargetBodyGuard.TargetBodyRefusedForOperation("base", false));
        Assert.False(TargetBodyGuard.TargetBodyRefusedForOperation(null, false));
    }

    // ---------------------------------------------------------------------------------------------
    // The contradiction that КОМПАС swallows
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ContourOverSecondBody_DisagreesWithThePlate()
    {
        // This is row A.5a of probe P2.6: declaring the plate while the contour sits over the blob.
        // КОМПАС answered Create=true, RebuildDocument succeeded and ΔV was 0 on both bodies.
        var verdict = TargetBodyGuard.ProfileMayAffectBody(HoleOverBlob, PlaneBase.Xy, PlateMin, PlateMax);

        Assert.NotNull(verdict);
        Assert.False(verdict!.Value);
    }

    [Fact]
    public void ContourOverSecondBody_AgreesWithThatBody()
    {
        // Row A.5b: the same call shape with the body the contour actually lies over — and that one
        // really did remove 250π from the blob and nothing from the plate.
        var verdict = TargetBodyGuard.ProfileMayAffectBody(HoleOverBlob, PlaneBase.Xy, BlobMin, BlobMax);

        Assert.True(verdict);
    }

    [Fact]
    public void ContourOverPlate_IsNotRefusedByTheBlobTest()
    {
        Assert.True(TargetBodyGuard.ProfileMayAffectBody(HoleOverPlate, PlaneBase.Xy, PlateMin, PlateMax));
        Assert.False(TargetBodyGuard.ProfileMayAffectBody(HoleOverPlate, PlaneBase.Xy, BlobMin, BlobMax));
    }

    [Fact]
    public void AlongThePlaneNormal_NothingIsConstrained()
    {
        // The probe's cut plane sat 10 mm ABOVE the material and the through cut still removed the
        // full 250π. Requiring the body to straddle the sketch plane would refuse exactly the
        // operation being measured, so the normal axis is deliberately not part of the test.
        var verdict = TargetBodyGuard.ProfileMayAffectBody(
            new ProfileBox(145d, -5d, 155d, 5d),
            PlaneBase.Xy,
            new double[] { 145d, -5d, 100d },
            new double[] { 155d, 5d, 110d });

        Assert.True(verdict);
    }

    [Fact]
    public void DisjointByLessThanTheCoordinateTolerance_StillCountsAsAgreement()
    {
        // docs/03 §3.3 gives 0.001 mm for coordinates; a contour that touches the body's boundary
        // within that slack must not be accused of aiming elsewhere.
        var justInside = 50d + TargetBodyGuard.ContactToleranceMm / 2;
        var justOutside = 50d + TargetBodyGuard.ContactToleranceMm * 2;

        Assert.True(TargetBodyGuard.ProfileMayAffectBody(
            new ProfileBox(justInside, -5d, justInside + 10d, 5d), PlaneBase.Xy, PlateMin, PlateMax));
        Assert.False(TargetBodyGuard.ProfileMayAffectBody(
            new ProfileBox(justOutside, -5d, justOutside + 10d, 5d), PlaneBase.Xy, PlateMin, PlateMax));
    }

    // ---------------------------------------------------------------------------------------------
    // The mapping is the measured one, per plane — and unknown planes answer "unknown"
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Xy_MapsSketchAxesOntoModelXAndY()
    {
        // The rectangle of acceptance row G07_xy (u=10..50, v=20..40) produced model
        // min=(10,20,15) max=(50,40,21), i.e. x=u, y=v.
        var profile = ProfileBox.Of(new[] { Rectangle(10, 20, 40, 20) });

        Assert.True(TargetBodyGuard.ProfileMayAffectBody(profile, PlaneBase.Xy, new double[] { 10, 20, 15 }, new double[] { 50, 40, 21 }));
        Assert.False(TargetBodyGuard.ProfileMayAffectBody(profile, PlaneBase.Xy, new double[] { 60, 20, 15 }, new double[] { 90, 40, 21 }));
    }

    [Fact]
    public void Xz_MapsSketchVontoModelMinusZ()
    {
        // G07_xz: the same rectangle gave min=(10,15,-40) max=(50,21,-20) — v lands on −z, so a body
        // at positive z is not the one this contour lies over, whatever the plate's own y says.
        var profile = ProfileBox.Of(new[] { Rectangle(10, 20, 40, 20) });

        Assert.True(TargetBodyGuard.ProfileMayAffectBody(profile, PlaneBase.Xz, new double[] { 10, 15, -40 }, new double[] { 50, 21, -20 }));
        Assert.False(TargetBodyGuard.ProfileMayAffectBody(profile, PlaneBase.Xz, new double[] { 10, 15, 20 }, new double[] { 50, 21, 40 }));
    }

    [Fact]
    public void Yz_MapsSketchUOntoModelMinusZAndVOntoModelMinusY()
    {
        // G07_yz: min=(-21,-40,-50) max=(-15,-20,-10) — u→−z, v→−y, and x is the normal.
        var profile = ProfileBox.Of(new[] { Rectangle(10, 20, 40, 20) });

        Assert.True(TargetBodyGuard.ProfileMayAffectBody(profile, PlaneBase.Yz, new double[] { -21, -40, -50 }, new double[] { -15, -20, -10 }));
        Assert.False(TargetBodyGuard.ProfileMayAffectBody(profile, PlaneBase.Yz, new double[] { -21, 20, 10 }, new double[] { -15, 40, 50 }));
    }

    [Fact]
    public void UnknownPlaneOrUnknownProfile_IsReportedAsNotChecked()
    {
        // "cannot say" must never arrive as "refused": a sketch on a referenced plane has no
        // measured correspondence here, and inventing one would reject work that КОМПАС does fine.
        Assert.Null(TargetBodyGuard.ProfileMayAffectBody(HoleOverBlob, null, BlobMin, BlobMax));
        Assert.Null(TargetBodyGuard.ProfileMayAffectBody(null, PlaneBase.Xy, BlobMin, BlobMax));
        Assert.Null(TargetBodyGuard.ProfileMayAffectBody(HoleOverBlob, PlaneBase.Xy, null, BlobMax));
        Assert.Null(TargetBodyGuard.ProfileMayAffectBody(HoleOverBlob, PlaneBase.Xy, new[] { 1d, 2d }, BlobMax));
    }

    // ---------------------------------------------------------------------------------------------
    // The profile box itself
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RectangleAndCircleGiveTheirOwnExtent()
    {
        var rectangle = ProfileBox.Of(new[] { Rectangle(0, 0, 40, 20) });
        Assert.Equal(new ProfileBox(0, 0, 40, 20), rectangle);

        var circle = ProfileBox.Of(new[] { Circle(150, 0, 5) });
        Assert.Equal(new ProfileBox(145, -5, 155, 5), circle);
    }

    [Fact]
    public void BatchIsTheBoxAroundEverythingDrawn()
    {
        var both = ProfileBox.Of(new[] { Circle(0, 0, 10), Rectangle(100, 100, 20, 20) });

        Assert.Equal(new ProfileBox(-10, -10, 120, 120), both);
    }

    [Fact]
    public void ArcIsBoxedByItsFullCircle_OnPurpose()
    {
        // An arc's true extent is inside the box of its circle. Over-approximating is the only safe
        // direction for a test whose single job is to refuse: too small a box would accuse a
        // legitimate target of being the wrong body.
        var arc = ProfileBox.Of(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Arc,
                CenterMm = new[] { 0d, 0d },
                RadiusMm = 10d,
                StartDeg = 0d,
                SweepDeg = 30d,
            },
        });

        Assert.Equal(new ProfileBox(-10, -10, 10, 10), arc);
    }

    [Fact]
    public void AnUnmeasurablePrimitiveRemovesTheBoxRatherThanLeavingAPartialOne()
    {
        // A polyline with a missing vertex would otherwise contribute a too-small rectangle, and a
        // too-small profile box is the one thing that turns this check into a false refusal.
        var broken = new SketchEntityDto { Kind = SketchEntityKind.Polyline, PointsMm = new[] { new[] { 0d } } };

        Assert.Null(ProfileBox.Of(new[] { broken }));
        Assert.Null(ProfileBox.Of(new[] { Rectangle(0, 0, 10, 10), broken }));
    }

    [Fact]
    public void UnionKeepsWhatIsKnownAndIgnoresNothingAsIfItWereEmpty()
    {
        Assert.Equal(HoleOverPlate, ProfileBox.Union(null, HoleOverPlate));
        Assert.Equal(HoleOverPlate, ProfileBox.Union(HoleOverPlate, null));
        Assert.Equal(new ProfileBox(-25, -5, 155, 5), ProfileBox.Union(HoleOverPlate, HoleOverBlob));
        Assert.Null(ProfileBox.Union(null, null));
    }
}

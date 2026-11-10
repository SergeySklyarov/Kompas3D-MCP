using KompasMcp.Api5Adapter;
using KompasMcp.Contracts;
using KompasMcp.Domain.Mates;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Inputs of <c>symmetric</c>/<c>dependent</c> and the mirror criterion of a symmetry mate.</summary>
/// <remarks>INVARIANT: a field that cannot be applied is a refusal NAMING it, before the mate exists, so a
/// plane on another kind and a face number on `dependent` never reach the model unapplied.
/// INVARIANT: the geometry criterion is the POSITION of the mirrored plane, not `Update()=true`, and an
/// unread input is null — never a zero that would pass the tolerance.
/// TEST: both halves live here: a tolerance nobody checks and an input nobody refuses look alike.
/// History: docs/decisions/assembly-ext.md#mate-types</remarks>
public class MateExtValidationTests
{
    private static CreateMateCommand Mate(string type, string? plane = null, bool? bySample = null,
        string? sample1 = null, string? sample2 = null, int? face1 = 0, int? face2 = 0) => new()
    {
        DocumentId = "asm",
        ExpectedRevision = 1,
        ConstraintType = type,
        FirstComponentRef = "component:1",
        SecondComponentRef = "component:2",
        FirstFaceIndex = face1,
        SecondFaceIndex = face2,
        SymmetryPlaneRef = plane,
        BySample = bySample,
        SampleObject1Ref = sample1,
        SampleObject2Ref = sample2,
    };

    private static string CodeOf(Action action) => Assert.Throws<KompasContractException>(action).Code;

    /// <summary>A symmetry without its plane would be a mate the kernel cannot solve.</summary>
    [Fact]
    public void Symmetric_WithoutPlane_IsInvalidArgument() =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("symmetric"))));

    /// <summary>A dependent position without its samples is refused at the input, not at the solve.</summary>
    [Fact]
    public void Dependent_WithoutSamples_IsInvalidArgument() =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("dependent", face1: null, face2: null))));

    /// <summary>The base objects of a dependent position are the components: a face number there would be
    /// accepted and never applied.</summary>
    [Fact]
    public void Dependent_WithFaceIndex_IsInvalidArgument() =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("dependent", sample1: "component:1",
                sample2: "component:2"))));

    /// <summary>The sample mode is documented and unmeasured: it is refused CAPABILITY_UNAVAILABLE, not
    /// offered untested.</summary>
    [Fact]
    public void Dependent_BySampleFalse_IsCapabilityUnavailable() =>
        Assert.Equal(ErrorCodes.CapabilityUnavailable,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("dependent", face1: null, face2: null,
                bySample: false, sample1: "component:1", sample2: "component:2"))));

    /// <summary>A plane on a coincidence is a value that would be written and ignored.</summary>
    [Fact]
    public void Coincidence_WithPlane_IsInvalidArgument() =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("coincidence", plane: "plane:1"))));

    /// <summary>Samples on a coincidence are refused by name, one code for the whole request.</summary>
    [Fact]
    public void Coincidence_WithSample_IsInvalidArgument() =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("coincidence", sample2: "component:2"))));

    /// <summary>An ordinary face mate still needs both face numbers.</summary>
    [Fact]
    public void Coincidence_WithoutFaceIndex_IsInvalidArgument() =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateMateExt(Mate("coincidence", face2: null))));

    /// <summary>The two complete requests pass the pre-COM checks.</summary>
    [Fact]
    public void ValidRequests_DoNotRefuse()
    {
        Api5Session.ValidateMateExt(Mate("symmetric", plane: "plane:1"));
        Api5Session.ValidateMateExt(Mate("dependent", face1: null, face2: null, bySample: true,
            sample1: "component:1", sample2: "component:2"));
    }

    // ── the mirror: the criterion the symmetry is confirmed by ──

    /// <summary>The probe's fixture: faces at x = 20 (+x) and x = 0 (−x) of a part, mirror plane x = 0.
    /// MEASURED: the second component's origin landed at (−20, 0, 0), i.e. on the mirror of the first
    /// face's plane — the number the probe compared with.</summary>
    /// <remarks>History: docs/decisions/assembly-ext.md#mate-types</remarks>
    [Fact]
    public void Mirror_OfTheProbeFixture_LandsWhereTheProbeMeasured()
    {
        var plane = new PlaneFrame([0, 0, 0], [1, 0, 0]);

        var point = SymmetryMirror.ReflectPoint([20, 0, 0], plane);
        var normal = SymmetryMirror.ReflectNormal([1, 0, 0], plane);

        Assert.NotNull(point);
        Assert.NotNull(normal);
        Assert.Equal(-20, point[0], 6);
        Assert.Equal(-1, normal[0], 6);
        Assert.Equal(0, SymmetryMirror.DistanceMm([-20, 0, 0], point, normal)!.Value, 6);
    }

    /// <summary>A plane point is ANY point of the plane: the mirror does not depend on where the plane's
    /// placement origin sits, which is what makes the criterion usable on a real face.</summary>
    [Fact]
    public void Mirror_IgnoresWhereThePlanePointSitsOnThePlane()
    {
        var atOrigin = new PlaneFrame([0, 0, 0], [0, 1, 0]);
        var elsewhere = new PlaneFrame([5, 0, -3], [0, 1, 0]);

        var first = SymmetryMirror.ReflectPoint([20, 7, 0], atOrigin);
        var second = SymmetryMirror.ReflectPoint([20, 7, 0], elsewhere);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first[1], second[1], 9);
        Assert.Equal(-7, first[1], 9);
    }

    /// <summary>A NEGATIVE control that the tolerance can see: 0.02 mm off is refused by the same rule that
    /// passes 0.01 mm.</summary>
    [Fact]
    public void Mirror_SeesAMillimetreFractionOfDrift()
    {
        var plane = new PlaneFrame([0, 0, 0], [1, 0, 0]);
        var mirrored = SymmetryMirror.ReflectPoint([20, 0, 0], plane)!;
        var normal = SymmetryMirror.ReflectNormal([1, 0, 0], plane)!;

        // 0.005 mm is inside the 0.01 mm tolerance with room for double rounding; 0.02 is outside it.
        var inside = SymmetryMirror.DistanceMm([-20 + 0.005, 0, 0], mirrored, normal)!.Value;
        var outside = SymmetryMirror.DistanceMm([-20 + 0.02, 0, 0], mirrored, normal)!.Value;

        Assert.True(inside <= SymmetryMirror.ToleranceMm);
        Assert.True(outside > SymmetryMirror.ToleranceMm);
    }

    /// <summary>A turned normal is NOT parallel to the mirrored one — the same rule the check applies.</summary>
    [Fact]
    public void Mirror_SeesATurnedNormal()
    {
        var mirrored = new[] { -1d, 0d, 0d };

        Assert.True(SymmetryMirror.ParallelDeviation(mirrored, [-1, 0, 0])!.Value <= SymmetryMirror.NormalTolerance);
        Assert.True(SymmetryMirror.ParallelDeviation(mirrored, [0, 1, 0])!.Value > SymmetryMirror.NormalTolerance);

        // The SIGN is not the criterion: a mirrored face may point either way, and the sign is reported.
        Assert.True(SymmetryMirror.ParallelDeviation(mirrored, [1, 0, 0])!.Value <= SymmetryMirror.NormalTolerance);
        Assert.Equal(-1, SymmetryMirror.SignedCosine(mirrored, [1, 0, 0])!.Value, 6);
    }

    /// <summary>An unread input is NULL, never a zero distance that would pass the tolerance.</summary>
    [Fact]
    public void Mirror_UnreadInputs_AreNullNotZero()
    {
        Assert.Null(SymmetryMirror.ReflectPoint([1, 2], new PlaneFrame([0, 0, 0], [1, 0, 0])));
        Assert.Null(SymmetryMirror.ReflectPoint([1, 2, 3], new PlaneFrame([0, 0, 0], [0, 0, 0])));
        Assert.Null(SymmetryMirror.DistanceMm([1, 2, 3], null!, [1, 0, 0]));
        Assert.Null(SymmetryMirror.ParallelDeviation([1, 0, 0], [0, 0, 0]));
        Assert.Null(SymmetryMirror.SignedCosine(null!, [1, 0, 0]));
    }
}

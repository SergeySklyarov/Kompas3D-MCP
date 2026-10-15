using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>What a created hole's own state and its measured effect prove.</summary>
/// <remarks>WHY. A client's report showed `kompas_hole` answering `succeeded` for a blind hole on a side
/// face that KOMPAS never applied: the feature stayed in the tree with `object_error = 32`, the volume did
/// not move and the face count did not grow. Two refusals close it - the feature's own error, and a
/// measured no-op - and neither may be confused with a declared expectation that merely did not match.
/// History: docs/decisions/adapter-features.md#hole-false-success</remarks>
public class HoleEffectPolicyTests
{
    // The measured case: a 120 x 80 x 32 plate (307200 mm3) and a blind hole O6 h8 that removes
    // pi * r^2 * h with r = 3, h = 8.
    private const double Plate = 307200d;
    private const double Removed = 226.1946710584651d;
    private const double PlateAfter = Plate - Removed;

    /// <summary>The feature's own kernel error is the more precise finding and comes first: it refuses
    /// even when the body happens to have moved.</summary>
    [Fact]
    public void FeatureError_IsARefusalWithTheGeometryFailedCode()
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: 32, Plate, Plate, facesBefore: 6d, facesAfter: 6d, declaredMismatch: false);

        Assert.Equal(HoleEffectVerdict.FeatureError, effect.Verdict);
        Assert.True(effect.IsRefusal);
        Assert.Equal(ErrorCodes.GeometryFailed, effect.RefusalErrorCode);
        Assert.Equal(HoleEffectPolicy.FeatureErrorDetailCode, effect.RefusalDetailCode);
        Assert.Equal(32, effect.ObjectError);
    }

    [Fact]
    public void FeatureError_RefusesEvenWhenTheBodyMoved()
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: 32, Plate, PlateAfter, facesBefore: 6d, facesAfter: 7d, declaredMismatch: false);

        Assert.Equal(HoleEffectVerdict.FeatureError, effect.Verdict);
        Assert.True(effect.IsRefusal);
    }

    /// <summary>Both observables read and neither moved - the measured defect: the hole is not in the body.</summary>
    [Fact]
    public void NoEffect_IsARefusalWithTheNoGeometryChangeCode()
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, Plate, facesBefore: 6d, facesAfter: 6d, declaredMismatch: false);

        Assert.Equal(HoleEffectVerdict.NoGeometryChange, effect.Verdict);
        Assert.True(effect.IsRefusal);
        Assert.Equal(ErrorCodes.NoGeometryChange, effect.RefusalErrorCode);
        Assert.Equal(HoleEffectPolicy.NoEffectDetailCode, effect.RefusalDetailCode);
    }

    /// <summary>A real hole with a declared delta that did not match is NOT a refusal: the hole happened,
    /// and the mismatch is published as a failed check with a lowered level.</summary>
    [Fact]
    public void ChangedWithAMismatchedDeclaredDelta_IsALoweredLevelNotARefusal()
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, PlateAfter, facesBefore: 6d, facesAfter: 7d, declaredMismatch: true);

        Assert.Equal(HoleEffectVerdict.LoweredLevel, effect.Verdict);
        Assert.False(effect.IsRefusal);
        Assert.Null(effect.RefusalErrorCode);
    }

    /// <summary>A correct hole: material removed, topology grown, declared expectation held.</summary>
    [Fact]
    public void ACorrectHole_IsConfirmed()
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, PlateAfter, facesBefore: 6d, facesAfter: 7d, declaredMismatch: false);

        Assert.Equal(HoleEffectVerdict.Confirmed, effect.Verdict);
        Assert.False(effect.IsRefusal);
        Assert.True(effect.MaterialRemoved);
        Assert.True(effect.FacesGrew);
        Assert.Equal(Removed, effect.RemovedMm3!.Value, 9);
    }

    /// <summary>An unread side is never "unchanged": the zero-effect refusal needs BOTH pairs. An unread
    /// FEATURE STATE, on the other hand, does not save a measured no-op - the body's own measurement is
    /// decisive there, and a false success is worse than a refusal on a body that did not move.</summary>
    [Fact]
    public void UnreadSides_AreNotANoOp()
    {
        Assert.Equal(HoleEffectVerdict.Unverifiable, HoleEffectPolicy.Evaluate(
            objectError: 0, null, null, facesBefore: 6d, facesAfter: 6d, declaredMismatch: false).Verdict);
        Assert.Equal(HoleEffectVerdict.Unverifiable, HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, Plate, facesBefore: null, facesAfter: null, declaredMismatch: false).Verdict);

        var unreadState = HoleEffectPolicy.Evaluate(
            objectError: null, Plate, Plate, facesBefore: 6d, facesAfter: 6d, declaredMismatch: false);
        Assert.Equal(HoleEffectVerdict.NoGeometryChange, unreadState.Verdict);
        Assert.True(unreadState.IsRefusal);
    }

    /// <summary>A hole REMOVES material: growth of the volume is not a hole, and topology growth alone
    /// with the volume intact is a named gap rather than a refusal (only BOTH unchanged refuse).</summary>
    [Fact]
    public void VolumeGrowthOrTopologyAlone_IsNotARefusal()
    {
        Assert.Equal(HoleEffectVerdict.Unverifiable, HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, Plate + 10d, facesBefore: 6d, facesAfter: 7d, declaredMismatch: false).Verdict);
        Assert.Equal(HoleEffectVerdict.Unverifiable, HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, Plate, facesBefore: 6d, facesAfter: 7d, declaredMismatch: false).Verdict);
    }

    /// <summary>A volume change inside the reading noise is still "nothing moved".</summary>
    [Fact]
    public void NoiseSizedVolumeChange_IsANoOp()
    {
        var effect = HoleEffectPolicy.Evaluate(
            objectError: 0, Plate, Plate - 1e-9d, facesBefore: 6d, facesAfter: 6d, declaredMismatch: false);

        Assert.Equal(HoleEffectVerdict.NoGeometryChange, effect.Verdict);
        Assert.True(effect.IsRefusal);
    }
}

using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>What a created hole's own state and its measured effect on the body prove.</summary>
/// <remarks>INVARIANT: a refusal is decided by facts about the FEATURE and the BODY, never by the fact
/// that the write call returned. MEASURED: KOMPAS can leave a hole feature in the tree carrying a
/// non-zero <c>objectError</c> while removing no material at all, and the tool used to report
/// <c>succeeded</c> for it.
/// History: docs/decisions/adapter-features.md#hole-false-success</remarks>
public enum HoleEffectVerdict
{
    /// <summary>The created feature reports a kernel error: the call is a refusal.</summary>
    FeatureError,

    /// <summary>Both observables were read and neither moved: the call is a refusal.</summary>
    NoGeometryChange,

    /// <summary>A side was not read, or material was not removed although the topology moved: "nothing
    /// moved" cannot be asserted, so this is a named gap rather than a refusal.</summary>
    Unverifiable,

    /// <summary>The body moved but the declared expectation did not hold: no refusal, lowered level.</summary>
    LoweredLevel,

    /// <summary>The body moved as declared: the hole is confirmed.</summary>
    Confirmed,
}

/// <summary>The verdict with the numbers the refusal and the named gap must carry.</summary>
public readonly record struct HoleEffectOutcome(
    HoleEffectVerdict Verdict,
    int? ObjectError,
    double? RemovedMm3,
    bool MaterialRemoved,
    bool FacesGrew)
{
    /// <summary>Whether the call must be refused instead of answered.</summary>
    public bool IsRefusal => Verdict is HoleEffectVerdict.FeatureError or HoleEffectVerdict.NoGeometryChange;

    /// <summary>The MCP error code of the refusal, or null when the call may succeed.</summary>
    public string? RefusalErrorCode => Verdict switch
    {
        HoleEffectVerdict.FeatureError => ErrorCodes.GeometryFailed,
        HoleEffectVerdict.NoGeometryChange => ErrorCodes.NoGeometryChange,
        _ => null,
    };

    /// <summary>The precise reason inside <c>details.code</c>, so two different findings never share one
    /// code: "the feature carries a kernel error" is not "the feature changed nothing".</summary>
    public string? RefusalDetailCode => Verdict switch
    {
        HoleEffectVerdict.FeatureError => HoleEffectPolicy.FeatureErrorDetailCode,
        HoleEffectVerdict.NoGeometryChange => HoleEffectPolicy.NoEffectDetailCode,
        _ => null,
    };
}

/// <summary>The one rule that turns a hole's feature state and its measured effect into a verdict.</summary>
/// <remarks>INVARIANT (order): the feature's own error is decided FIRST — it is the more precise finding,
/// and a feature that reports an error has not necessarily changed the body either.
/// INVARIANT: an unread side is never "unchanged" — <c>null</c> means "not compared", so the zero-effect
/// refusal needs BOTH the volume pair and the face-count pair.
/// INVARIANT: a declared expectation that did not hold is NOT a refusal here — the hole happened, and the
/// mismatch is published as a failed check with a lowered level (declared-expectation rule).
/// History: docs/decisions/adapter-features.md#hole-false-success</remarks>
public static class HoleEffectPolicy
{
    /// <summary>The reason inside <c>details.code</c> for a feature that carries a kernel error.</summary>
    public const string FeatureErrorDetailCode = "hole_feature_error";

    /// <summary>The reason inside <c>details.code</c> for a hole that changed nothing.</summary>
    public const string NoEffectDetailCode = "hole_not_applied";

    /// <summary>Decide the verdict from the feature's error, the volume pair, the face-count pair and the
    /// declared-expectation comparison. <paramref name="declaredMismatch"/> is true only when the client
    /// declared a volume delta and the measurement contradicted it.</summary>
    public static HoleEffectOutcome Evaluate(
        int? objectError,
        double? volumeBefore,
        double? volumeAfter,
        double? facesBefore,
        double? facesAfter,
        bool declaredMismatch)
    {
        var removed = volumeBefore is double before && volumeAfter is double after
            ? before - after
            : (double?)null;
        // A hole REMOVES material, so the sign is part of the observable: growth is not a hole.
        var materialRemoved = removed is double delta
            && delta > 0d && BodyChangePolicy.VolumeMoved(delta, volumeBefore);
        var facesGrew = FaceCountGrowth.Grew(facesBefore, facesAfter);
        var volumeRead = volumeBefore is double && volumeAfter is double;
        var facesRead = facesBefore is double && facesAfter is double;

        if (FeatureErrorRule.IsRefusal(objectError))
        {
            return new HoleEffectOutcome(
                HoleEffectVerdict.FeatureError, objectError, removed, materialRemoved, facesGrew);
        }

        if (volumeRead && facesRead && !materialRemoved && !facesGrew)
        {
            return new HoleEffectOutcome(
                HoleEffectVerdict.NoGeometryChange, objectError, removed, materialRemoved, facesGrew);
        }

        if (!volumeRead || !materialRemoved)
        {
            return new HoleEffectOutcome(
                HoleEffectVerdict.Unverifiable, objectError, removed, materialRemoved, facesGrew);
        }

        return new HoleEffectOutcome(
            declaredMismatch ? HoleEffectVerdict.LoweredLevel : HoleEffectVerdict.Confirmed,
            objectError, removed, materialRemoved, facesGrew);
    }
}

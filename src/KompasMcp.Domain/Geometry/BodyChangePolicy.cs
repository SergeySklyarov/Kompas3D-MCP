namespace KompasMcp.Domain.Geometry;

/// <summary>When a mutation has changed a body at all — the floor below which a reading is noise.</summary>
/// <remarks>WHY a separate rule. "The body did not change" is a claim about the KERNEL, not about a coarse
/// threshold: a boss Ø0.1172 × 0.0842 adds 9.08e-4 mm³, and the old 0.01 mm³ floor reported it as "no change"
/// while the volume AND the gabarit HAD changed. The floor is therefore the measured noise of a reading,
/// relative to the body's own volume AND absolute, so drift is not mistaken for a feature and no real material
/// change is missed. A body counts as changed when ANY of volume, gabarit or topology moved: a boolean
/// `intersect` changes no volume, and a feature can alter the face count with volume and box intact.
/// History: docs/decisions/adapter-core.md#volume-change-floor</remarks>
public static class BodyChangePolicy
{
    /// <summary>Absolute part of the floor, in mm³. MEASURED: repeating a measurement of the same body
    /// returns the same volume to within ~1e-13 mm³, and an untouched body re-read across an unrelated rebuild
    /// moves by no more than that; the floor sits orders above the noise and far below any real feature.</summary>
    public const double VolumeNoiseMm3 = 1e-6d;

    /// <summary>Relative part of the floor: a large body's reading carries proportionally more noise.</summary>
    public const double VolumeNoiseRelative = 1e-9d;

    /// <summary>Whether a volume delta is larger than the measured noise. <paramref name="volume"/> is the
    /// body's own volume, so the relative part scales with the magnitude being compared.</summary>
    public static bool VolumeMoved(double? delta, double? volume) =>
        delta is double moved && Math.Abs(moved) > VolumeNoiseMm3 + Math.Abs(volume ?? 0d) * VolumeNoiseRelative;

    /// <summary>Whether the body changed in ANY of the three observables. INVARIANT: an unreadable volume is
    /// not "unchanged" — the caller decides what to do with an unread reading, and a null delta alone never
    /// proves a no-op.</summary>
    public static bool Changed(double? volumeDelta, double? volume, bool boxChanged, bool topologyChanged) =>
        VolumeMoved(volumeDelta, volume) || boxChanged || topologyChanged;
}

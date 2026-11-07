namespace KompasMcp.Domain.Geometry;

/// <summary>What a moved face does to the numbers that can be read back: volume and surface area.</summary>
/// <remarks>DOC: <c>ifacemover.html</c> — <c>Offset</c> is a distance and <c>Direction</c> is a
/// <c>VARIANT_BOOL</c> «Сменить направление», so the route moves the face ALONG ITS NORMAL and the caller
/// never passes a vector. INVARIANT: a planar face moved along its normal keeps its own area and sweeps a
/// prism of base <c>A</c> and height <c>d</c>, so the body gains <c>d·A</c> of volume and <c>d·P</c> of
/// lateral area — both are predictions the caller compares the read-back with, not a description of the
/// answer. INVARIANT: the sign of <c>d</c> is the OBSERVED displacement, and the side is MEASURED:
/// <c>Direction = false</c> goes against the outward normal (the volume falls), <c>true</c> along it.
/// History: docs/decisions/direct-faces.md#implementation</remarks>
public static class FaceMoveEffect
{
    /// <summary>Volume of the body after a face of area <paramref name="faceAreaMm2"/> moved by
    /// <paramref name="displacementMm"/> along its outward normal.</summary>
    public static double ExpectedVolume(double volumeBeforeMm3, double faceAreaMm2, double displacementMm) =>
        volumeBeforeMm3 + faceAreaMm2 * displacementMm;

    /// <summary>Surface area after the same move: the moved faces keep their area, the side faces gain
    /// one band of the moved faces' perimeter.</summary>
    public static double ExpectedArea(double areaBeforeMm2, double perimeterMm, double displacementMm) =>
        areaBeforeMm2 + perimeterMm * displacementMm;

    /// <summary>The displacement the read-back volume actually shows, mm. <c>null</c> when a number is
    /// missing or the face area is zero: "not computed" differs from "moved by zero".</summary>
    public static double? ObservedDisplacement(double? volumeBeforeMm3, double? volumeAfterMm3, double? faceAreaMm2)
    {
        if (volumeBeforeMm3 is not double before || volumeAfterMm3 is not double after
            || faceAreaMm2 is not double area || !double.IsFinite(area) || Math.Abs(area) < 1e-9d)
        {
            return null;
        }

        return (after - before) / area;
    }

    /// <summary>Whether the move must leave no solid body: the expected volume <c>V + d·A</c> is not above
    /// zero, so the operation can only degenerate the body.</summary>
    /// <remarks>INVARIANT: such a move is refused BEFORE the operation exists. MEASURED: the kernel accepts an
    /// inward move equal to the wall thickness (<c>Update() = true</c>), leaves volume 0 and keeps its feature
    /// in the tree; the arithmetic above is available without COM. An unreadable volume or area is not a
    /// prediction - the refusal is then left to the numbers read back after the move.
    /// History: docs/decisions/direct-faces.md#collapse-refusal</remarks>
    public static bool CollapsesBody(double? volumeBeforeMm3, double? faceAreaMm2, double signedDisplacementMm) =>
        volumeBeforeMm3 is double volume && faceAreaMm2 is double area
        && double.IsFinite(volume) && double.IsFinite(area) && double.IsFinite(signedDisplacementMm)
        && Math.Abs(area) > 1e-9d
        && ExpectedVolume(volume, area, signedDisplacementMm) <= 0d;

    /// <summary>Whether the body changed at all: <c>Update() = true</c> alone is «accepted», and a
    /// tangent-group move is confirmed by this sign instead of by the volume formula.</summary>
    public static bool VolumeChanged(double? volumeBeforeMm3, double? volumeAfterMm3) =>
        volumeBeforeMm3 is double before && volumeAfterMm3 is double after
        && double.IsFinite(before) && double.IsFinite(after)
        && Math.Abs(after - before) > 1e-6d;

    /// <summary>Whether the set the operation holds grew past the faces that were named, which is the
    /// only measurable answer to «did the tangent chain join». <c>null</c> (unread) is not «no».</summary>
    public static bool GroupWider(int? groupSize, int namedCount) =>
        groupSize is int size && size > namedCount;

    /// <summary>Whether the read-back shows a LONGER sweep than the requested one, beyond tolerance: the
    /// kernel moved faces the caller did not name, so the volume is no witness against the move.</summary>
    /// <remarks>MEASURED: a single-mode move on a face with tangent neighbours grows the volume by more
    /// than <c>d·A</c> of the named face. A SHORTER sweep is never excused by this: nothing implicit
    /// makes the named face travel less than asked.
    /// History: docs/decisions/direct-faces.md#implicit-tangent</remarks>
    public static bool SweptFurther(double? observedMm, double requestedMm) =>
        observedMm is double observed && double.IsFinite(observed) && double.IsFinite(requestedMm)
        && Math.Abs(observed) > Math.Abs(requestedMm)
        && !Matches(Math.Abs(observed), Math.Abs(requestedMm));

    /// <summary>Comparison within the profile tolerance: 0.01 absolute or 1e-6 relative, the same rule the
    /// other geometry checks of the product use.</summary>
    public static bool Matches(double? actual, double? expected)
    {
        if (actual is not double value || expected is not double target)
        {
            return false;
        }

        if (!double.IsFinite(value) || !double.IsFinite(target))
        {
            return false;
        }

        var delta = Math.Abs(value - target);
        return delta <= 0.01d || delta <= 1e-6 * Math.Abs(target);
    }
}

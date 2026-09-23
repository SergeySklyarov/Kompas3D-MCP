using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Axis-aligned box of the primitives the server itself drew into a sketch, in sketch-local
/// millimetres (<c>u</c> along sketch X, <c>v</c> along sketch Y).
/// </summary>
/// <remarks>
/// <para>
/// The box is deliberately an <b>over-approximation</b>, never an under-approximation, because the
/// only consumer is <see cref="TargetBodyGuard.ProfileMayAffectBody"/>: that test refuses an
/// operation, so a box that is too small would refuse legitimate work, while a box that is too big
/// only weakens the refusal. An arc is therefore boxed by its full circle, and a polyline by the
/// rectangle around its vertices.
/// </para>
/// <para>
/// Null is returned for anything whose extent cannot be stated (a missing coordinate pair, an
/// unknown kind). Absence is reported as absence — the caller then says "not checked" instead of
/// treating an empty box as a proof.
/// </para>
/// </remarks>
public readonly record struct ProfileBox(double MinU, double MinV, double MaxU, double MaxV)
{
    /// <summary>Extent of a whole batch of primitives, or null if any of them is unmeasurable.</summary>
    public static ProfileBox? Of(IReadOnlyList<SketchEntityDto> entities)
    {
        ProfileBox? acc = null;
        foreach (var entity in entities)
        {
            if (Of(entity) is not ProfileBox box)
            {
                return null;
            }

            acc = Union(acc, box);
        }

        return acc;
    }

    /// <summary>Extent of one primitive in sketch coordinates.</summary>
    public static ProfileBox? Of(SketchEntityDto entity)
    {
        switch (entity.Kind)
        {
            case SketchEntityKind.Line:
                return Pair(entity.StartMm, entity.EndMm);

            case SketchEntityKind.Circle:
                // A full circle: centre ± radius on both axes.
                return entity.CenterMm is { Count: >= 2 } center && entity.RadiusMm is double radius
                    ? new ProfileBox(center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius)
                    : null;

            case SketchEntityKind.Arc:
                // Over-approximation on purpose: the arc lies inside the box of its full circle, and
                // a box this test is unhappy about must never be a false accusation.
                return entity.CenterMm is { Count: >= 2 } arcCenter && entity.RadiusMm is double arcRadius
                    ? new ProfileBox(arcCenter[0] - arcRadius, arcCenter[1] - arcRadius, arcCenter[0] + arcRadius, arcCenter[1] + arcRadius)
                    : null;

            case SketchEntityKind.Rectangle:
                if (entity.StartMm is not { Count: >= 2 } corner
                    || entity.WidthMm is not double width
                    || entity.HeightMm is not double height)
                {
                    return null;
                }

                var farU = corner[0] + width;
                var farV = corner[1] + height;
                return new ProfileBox(
                    Math.Min(corner[0], farU),
                    Math.Min(corner[1], farV),
                    Math.Max(corner[0], farU),
                    Math.Max(corner[1], farV));

            case SketchEntityKind.Polyline:
                if (entity.PointsMm is not { Count: >= 2 } points
                    || points.Any(p => p.Count < 2))
                {
                    return null;
                }

                return new ProfileBox(
                    points.Min(p => p[0]),
                    points.Min(p => p[1]),
                    points.Max(p => p[0]),
                    points.Max(p => p[1]));

            default:
                return null;
        }
    }

    /// <summary>Smallest box containing both. Null inputs are ignored, not treated as empty.</summary>
    public static ProfileBox? Union(ProfileBox? left, ProfileBox? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return new ProfileBox(
            Math.Min(left.Value.MinU, right.Value.MinU),
            Math.Min(left.Value.MinV, right.Value.MinV),
            Math.Max(left.Value.MaxU, right.Value.MaxU),
            Math.Max(left.Value.MaxV, right.Value.MaxV));
    }

    /// <summary>Interval of the box along one sketch axis (0 = u, 1 = v).</summary>
    public double[] Interval(int axis) => axis switch
    {
        0 => new[] { MinU, MaxU },
        _ => new[] { MinV, MaxV },
    };

    private static ProfileBox? Pair(IReadOnlyList<double>? first, IReadOnlyList<double>? second)
    {
        if (first is not { Count: >= 2 } a || second is not { Count: >= 2 } b)
        {
            return null;
        }

        return new ProfileBox(
            Math.Min(a[0], b[0]),
            Math.Min(a[1], b[1]),
            Math.Max(a[0], b[0]),
            Math.Max(a[1], b[1]));
    }
}

/// <summary>
/// The two decisions about an extrusion's target body that can be made without КОМПАС: whether an
/// operation may name a body at all, and whether a drawn profile can plausibly lie over the body
/// the caller declared.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second test is necessary, not sufficient, and must never be presented as geometric
/// containment.</b> It compares two rectangles: the over-approximated extent of the profile the
/// server drew, and the <c>GetGabarit</c> box of the declared body. Agreement says only "these two
/// boxes are not disjoint" — the profile may still miss the material entirely (a hole inside a
/// pocket, a contour in the concave part of an L). Disagreement says something stronger and is the
/// reason the test exists: probe P2.6 measured that declaring a body the contour does not sit over
/// makes <c>SetSketch</c>, <c>Create</c> and <c>RebuildDocument</c> all return true while the
/// document does not change at all. КОМПАС does not report that contradiction as an error, so a
/// no-op would otherwise be delivered as a success.
/// </para>
/// <para>
/// The axis correspondence below is not assumed. It is the mapping measured by probe P2.4 and
/// asserted by acceptance rows <c>G07_xy</c>/<c>G07_xz</c>/<c>G07_yz</c> in
/// <c>scripts/mcp-smoke.py</c>, where the same rectangle (u=10..50, v=20..40) with depth 6 produced
/// exactly the boxes encoded here. A plane the server did not derive from one of the three base
/// planes has no measured correspondence and yields "unknown", which the caller reports as
/// unverified rather than guessing a sign — the mistake G07 was left open over.
/// </para>
/// <para>
/// The plane's normal axis is deliberately never constrained. A through cut travels along it in
/// both directions from the sketch plane, which sits outside the material by construction (the
/// probe's cut plane was 10 mm above the bodies), so requiring the body to straddle the plane would
/// refuse the very operation being measured.
/// </para>
/// </remarks>
public static class TargetBodyGuard
{
    /// <summary>
    /// Slack applied when deciding that two intervals are disjoint, in mm. It is the coordinate
    /// tolerance of docs/03 §3.3, and it only ever widens the boxes — a profile touching a body
    /// exactly on its boundary counts as agreement.
    /// </summary>
    public const double ContactToleranceMm = 1e-3;

    /// <summary>
    /// How sketch axes <c>u</c>/<c>v</c> land on model axes for each base plane: axis index
    /// (0=x, 1=y, 2=z) and sign, plus the model axis the plane is normal to. Values are the
    /// measured ones described in the type remarks.
    /// </summary>
    private static readonly Dictionary<PlaneBase, (int AxisU, int SignU, int AxisV, int SignV, int NormalAxis)> Frames = new()
    {
        [PlaneBase.Xy] = (0, 1, 1, 1, 2),
        [PlaneBase.Xz] = (0, 1, 2, -1, 1),
        [PlaneBase.Yz] = (2, -1, 1, -1, 0),
    };

    /// <summary>Wire names of the operations that act on a body the caller must name.</summary>
    public static bool OperationTakesTargetBody(string? operation) =>
        operation is "boss" or "cut";

    /// <summary>
    /// <c>operation=base</c> creates the first body, so there is nothing to aim it at. Accepting
    /// <c>target_body_ref</c> there and ignoring it would tell the caller a target had been honoured
    /// when no such thing happened — docs/05 §4.1 forbids normalising an unsupported combination
    /// away, so it is refused.
    /// </summary>
    public static bool TargetBodyRefusedForOperation(string? operation, bool targetBodyProvided) =>
        targetBodyProvided && !OperationTakesTargetBody(operation);

    /// <summary>
    /// True when the profile's extent cannot overlap the body's gabarit on the plane's own axes,
    /// false when the two are provably disjoint, null when the comparison cannot be made at all
    /// (unknown plane, unmeasurable profile, unreadable body box).
    /// </summary>
    public static bool? ProfileMayAffectBody(
        ProfileBox? profile,
        PlaneBase? plane,
        IReadOnlyList<double>? bodyMin,
        IReadOnlyList<double>? bodyMax)
    {
        if (profile is null || plane is null)
        {
            return null;
        }

        if (bodyMin is not { Count: 3 } min || bodyMax is not { Count: 3 } max)
        {
            return null;
        }

        if (!Frames.TryGetValue(plane.Value, out var frame))
        {
            return null;
        }

        var unconstrained = 0;
        foreach (var (axis, sign, interval) in new[]
                 {
                     (frame.AxisU, frame.SignU, profile.Value.Interval(0)),
                     (frame.AxisV, frame.SignV, profile.Value.Interval(1)),
                 })
        {
            var low = sign > 0 ? interval[0] : -interval[1];
            var high = sign > 0 ? interval[1] : -interval[0];
            var bodyLow = Math.Min(min[axis], max[axis]);
            var bodyHigh = Math.Max(min[axis], max[axis]);

            if (low > bodyHigh + ContactToleranceMm || bodyLow > high + ContactToleranceMm)
            {
                return false;
            }

            unconstrained++;
        }

        // Both in-plane axes agreed. Anything else (an unmapped axis) would have returned null
        // above, so reaching here with both axes checked is the only way to answer true.
        return unconstrained == 2;
    }
}

using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>Axis-aligned box of the primitives the server itself drew into a sketch, in sketch-local
/// millimetres (<c>u</c> along sketch X, <c>v</c> along sketch Y).</summary>
/// <remarks>INVARIANT: deliberately an <b>over-approximation</b>, never an under-approximation — the only
/// consumer refuses an operation, so too small a box would refuse legitimate work. An arc is boxed by its
/// full circle, a polyline by the rectangle around its vertices. Null is returned for anything whose
/// extent cannot be stated; absence is reported as absence, never as an empty proof.</remarks>
public readonly record struct ProfileBox(double MinU, double MinV, double MaxU, double MaxV)
{
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
                return entity.CenterMm is { Count: >= 2 } center && entity.RadiusMm is double radius
                    ? new ProfileBox(center[0] - radius, center[1] - radius, center[0] + radius, center[1] + radius)
                    : null;

            case SketchEntityKind.Arc:
                // Over-approximation on purpose: the arc lies inside its full circle's box, and this test
                // must never raise a false accusation.
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

/// <summary>The two decisions about an extrusion's target body that can be made without KOMPAS: whether an
/// operation may name a body at all, and whether a drawn profile can plausibly lie over the declared body.</summary>
/// <remarks>LIMIT: the second test is necessary, not sufficient, and is never geometric containment — it
/// compares two rectangles (the over-approximated profile extent and the <c>GetGabarit</c> box of the
/// declared body). Agreement says only "not disjoint"; disagreement is stronger and is why the test exists.
/// The axis correspondence is asserted by acceptance rows <c>G07_xy</c>/<c>G07_xz</c>/<c>G07_yz</c>.
/// History: docs/decisions/geometry.md#target-body-guard</remarks>
public static class TargetBodyGuard
{
    /// <summary>Slack applied when deciding that two intervals are disjoint, in mm (the coordinate tolerance
    /// of docs/03 §3.3). It only ever widens the boxes, so a boundary touch counts as agreement.</summary>
    public const double ContactToleranceMm = 1e-3;

    /// <summary>How sketch axes <c>u</c>/<c>v</c> land on model axes for each base plane: axis index
    /// (0=x, 1=y, 2=z) and sign, plus the normal axis. Values are the measured ones (type remarks).</summary>
    private static readonly Dictionary<PlaneBase, (int AxisU, int SignU, int AxisV, int SignV, int NormalAxis)> Frames = new()
    {
        [PlaneBase.Xy] = (0, 1, 1, 1, 2),
        [PlaneBase.Xz] = (0, 1, 2, -1, 1),
        [PlaneBase.Yz] = (2, -1, 1, -1, 0),
    };

    public static bool OperationTakesTargetBody(string? operation) =>
        operation is "boss" or "cut";

    /// <summary><c>operation=base</c> creates the first body, so there is nothing to aim it at. Accepting
    /// <c>target_body_ref</c> and ignoring it would tell the caller a target had been honoured when no
    /// such thing happened — docs/05 §4.1 forbids normalising an unsupported combination away.</summary>
    public static bool TargetBodyRefusedForOperation(string? operation, bool targetBodyProvided) =>
        targetBodyProvided && !OperationTakesTargetBody(operation);

    /// <summary>True when the profile's extent cannot overlap the body's gabarit on the plane's own axes,
    /// false when the two are provably disjoint, null when the comparison cannot be made at all
    /// (unknown plane, unmeasurable profile, unreadable body box).</summary>
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

        // Both in-plane axes agreed; an unmapped axis would have returned null above.
        return unconstrained == 2;
    }
}

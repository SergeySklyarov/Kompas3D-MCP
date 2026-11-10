namespace KompasMcp.Domain.Mates;

/// <summary>A plane by one point and one (not necessarily unit) normal, in one coordinate frame.</summary>
public sealed record PlaneFrame(double[] Point, double[] Normal);

/// <summary>Whether a face landed on the MIRROR of another face about a plane.</summary>
/// <remarks>ROUTE — arithmetic over two documented reads: the face's plane origin and normal in PART
/// coordinates (<c>ksSurface.GetSurfaceParam → ksPlaneParam.GetPlacement</c>) and the component's
/// <c>IPart7.GetSummMatrix</c>. DOC: <c>imate3dsymmetry_plane.html</c> — <c>Plane</c> is an
/// <c>IModelObject</c>.
/// INVARIANT: the criterion is the POSITION of the mirrored plane, not <c>Update()=true</c>; the point
/// is the plane's own placement origin, so any point of that plane serves.
/// History: docs/decisions/assembly-ext.md#mate-types</remarks>
public static class SymmetryMirror
{
    /// <summary>How far the mirrored plane may drift, in millimetres.</summary>
    public const double ToleranceMm = 0.01;

    /// <summary>How far two normals may be from parallel, as <c>1 − |cos|</c>.</summary>
    public const double NormalTolerance = 1e-3;

    /// <summary>The point reflected in the plane: <c>p − 2((p − p0)·n)n</c>.</summary>
    public static double[]? ReflectPoint(double[] point, PlaneFrame plane)
    {
        if (point is not { Length: 3 } || Unit(plane.Normal) is not { } normal)
        {
            return null;
        }

        var atPlane = new[] { point[0] - plane.Point[0], point[1] - plane.Point[1],
            point[2] - plane.Point[2] };
        return Reflect(atPlane, normal, plane.Point);
    }

    /// <summary>The normal reflected in the plane — a mirror TURNS a normal, it does not keep it. The
    /// plane's point does not take part: a direction has no position.</summary>
    public static double[]? ReflectNormal(double[] normal, PlaneFrame plane)
    {
        if (normal is not { Length: 3 } || Unit(plane.Normal) is not { } unit)
        {
            return null;
        }

        return Reflect(normal, unit, null);
    }

    /// <summary>Distance from <paramref name="point"/> to the plane through
    /// <paramref name="mirrorPoint"/> with normal <paramref name="mirrorNormal"/>; <c>null</c> when the
    /// mirror is not computable (an unread point or a degenerate normal) — never zero.</summary>
    public static double? DistanceMm(double[] point, double[] mirrorPoint, double[] mirrorNormal)
    {
        if (point is not { Length: 3 } || mirrorPoint is not { Length: 3 }
            || Unit(mirrorNormal) is not { } normal)
        {
            return null;
        }

        var offset = new[] { point[0] - mirrorPoint[0], point[1] - mirrorPoint[1],
            point[2] - mirrorPoint[2] };
        return Math.Abs(Dot(offset, normal));
    }

    /// <summary><c>1 − |cos|</c> of two directions — 0 means collinear, whatever the sign, because a
    /// mirrored planar face may face either way and the sign is a fact, not the criterion.</summary>
    public static double? ParallelDeviation(double[] first, double[] second)
    {
        if (first is not { Length: 3 } || second is not { Length: 3 }
            || Length(first) <= 1e-9 || Length(second) <= 1e-9)
        {
            return null;
        }

        return 1 - Math.Abs(Dot(first, second) / (Length(first) * Length(second)));
    }

    /// <summary>Signed cosine of two directions — the SIGN is reported as a fact, never judged;
    /// <c>null</c> when either direction is degenerate.</summary>
    public static double? SignedCosine(double[] first, double[] second) =>
        ParallelDeviation(first, second) is null
            ? null
            : Dot(first, second) / (Length(first) * Length(second));

    private static double[] Reflect(double[] vector, double[] unitNormal, double[]? origin)
    {
        var along = Dot(vector, unitNormal);
        var reflected = new[] { vector[0] - 2 * along * unitNormal[0],
            vector[1] - 2 * along * unitNormal[1], vector[2] - 2 * along * unitNormal[2] };
        return origin is null
            ? reflected
            : [reflected[0] + origin[0], reflected[1] + origin[1], reflected[2] + origin[2]];
    }

    private static double[]? Unit(double[]? vector)
    {
        if (vector is not { Length: 3 })
        {
            return null;
        }

        var length = Length(vector);
        return length <= 1e-9 ? null : [vector[0] / length, vector[1] / length, vector[2] / length];
    }

    private static double Length(double[] vector) => Math.Sqrt(Dot(vector, vector));

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}

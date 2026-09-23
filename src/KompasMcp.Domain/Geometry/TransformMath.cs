using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// A validated right-handed rigid frame: origin plus orthonormal X, Y and the derived Z = X × Y
/// (spec 1.10). The public contract never carries Z — it is computed here so a client cannot
/// send a left-handed triple and get mirrored geometry.
/// </summary>
public sealed class RigidFrame
{
    public required double[] OriginMm { get; init; }

    public required double[] XMm { get; init; }

    public required double[] YMm { get; init; }

    public required double[] ZMm { get; init; }

    public static RigidFrame Identity { get; } = new()
    {
        OriginMm = new double[] { 0, 0, 0 },
        XMm = new double[] { 1, 0, 0 },
        YMm = new double[] { 0, 1, 0 },
        ZMm = new double[] { 0, 0, 1 },
    };

    /// <summary>parent = origin + X·x + Y·y + Z·z (spec 1.10, the published semantics).</summary>
    public double[] ToParent(double xLocalMm, double yLocalMm, double zLocalMm) => new[]
    {
        OriginMm[0] + (XMm[0] * xLocalMm) + (YMm[0] * yLocalMm) + (ZMm[0] * zLocalMm),
        OriginMm[1] + (XMm[1] * xLocalMm) + (YMm[1] * yLocalMm) + (ZMm[1] * zLocalMm),
        OriginMm[2] + (XMm[2] * xLocalMm) + (YMm[2] * yLocalMm) + (ZMm[2] * zLocalMm),
    };

    /// <summary>Coordinates of a parent-space point in this frame (projection on the axes).</summary>
    public double[] FromParent(double[] parentPointMm)
    {
        var d = new[]
        {
            parentPointMm[0] - OriginMm[0],
            parentPointMm[1] - OriginMm[1],
            parentPointMm[2] - OriginMm[2],
        };

        return new[]
        {
            Dot(d, XMm),
            Dot(d, YMm),
            Dot(d, ZMm),
        };
    }

    public static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    public static double[] Cross(double[] a, double[] b) => new[]
    {
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0]),
    };

    public static double Norm(double[] v) => Math.Sqrt(Dot(v, v));
}

/// <summary>
/// Transform validation and composition. Composition order is fixed and documented because
/// rotation and translation do not commute (spec 2.3 "указать порядок композиции").
/// </summary>
public static class TransformMath
{
    /// <summary>Unit-length and orthogonality slack for a client-supplied frame.</summary>
    public const double OrthonormalityTolerance = 1e-9;

    /// <summary>Numeric limits chosen so a plausible CAD coordinate always fits and nonsense does not.</summary>
    public const double MaxCoordinateMagnitudeMm = 1e7;

    public static RigidFrame FromDto(TransformDto dto, string fieldPath = "transform")
    {
        var origin = ReadVector(dto.OriginMm, $"{fieldPath}.origin_mm", required: true);
        var x = ReadVector(dto.XAxis, $"{fieldPath}.x_axis", required: true);
        var y = ReadVector(dto.YAxis, $"{fieldPath}.y_axis", required: true);

        var xNorm = RigidFrame.Norm(x);
        if (Math.Abs(xNorm - 1d) > OrthonormalityTolerance)
        {
            throw Invalid($"{fieldPath}.x_axis", $"Длина оси X равна {xNorm:R}, требуется единичная (±{OrthonormalityTolerance:R}). Оси не нормализуются молча.");
        }

        var yNorm = RigidFrame.Norm(y);
        if (Math.Abs(yNorm - 1d) > OrthonormalityTolerance)
        {
            throw Invalid($"{fieldPath}.y_axis", $"Длина оси Y равна {yNorm:R}, требуется единичная.");
        }

        var dot = RigidFrame.Dot(x, y);
        if (Math.Abs(dot) > OrthonormalityTolerance)
        {
            throw Invalid(fieldPath, $"X·Y = {dot:R}: оси не ортогональны.");
        }

        var z = RigidFrame.Cross(x, y);
        var det = RigidFrame.Dot(RigidFrame.Cross(x, y), z);
        if (det <= 0)
        {
            throw Invalid(fieldPath, $"Определитель базиса {det:R} ≤ 0: правая система координат не задана.");
        }

        return new RigidFrame { OriginMm = origin, XMm = x, YMm = y, ZMm = z };
    }

    public static TransformDto ToDto(RigidFrame frame) => new()
    {
        OriginMm = (double[])frame.OriginMm.Clone(),
        XAxis = (double[])frame.XMm.Clone(),
        YAxis = (double[])frame.YMm.Clone(),
    };

    /// <summary>
    /// Frame of a child expressed in world space, given its placement inside a parent that is
    /// itself placed in world space: <c>world = parent ∘ child</c>, i.e.
    /// <c>point_world = parent.ToParent(child.ToParent(point_local))</c>.
    /// </summary>
    public static RigidFrame Compose(RigidFrame parent, RigidFrame child)
    {
        // Origin maps through the parent; the child's axes rotate with the parent's basis.
        var origin = parent.ToParent(child.OriginMm[0], child.OriginMm[1], child.OriginMm[2]);

        return new RigidFrame
        {
            OriginMm = origin,
            XMm = RotateIntoParent(parent, child.XMm),
            YMm = RotateIntoParent(parent, child.YMm),
            ZMm = RotateIntoParent(parent, child.ZMm),
        };
    }

    /// <summary>
    /// Placement to send to КОМПАС so that a child ends up at <paramref name="desiredWorld"/>
    /// while the parent sits at <paramref name="parentInWorld"/>. Exact inverse of
    /// <see cref="Compose"/>, which is what makes A03 (nested assemblies) checkable.
    /// </summary>
    /// <remarks>
    /// With R = [Xp Yp Pz] the parent's rotation (columns are its axes), the child's frame in the
    /// parent's space is <c>origin = Rᵀ·(Od − Op)</c> and <c>axes = Rᵀ·(child world axes)</c>.
    /// Rᵀ is used rather than an inverse solve because an orthonormal basis inverts by transpose.
    /// </remarks>
    public static RigidFrame InverseCompose(RigidFrame parentInWorld, RigidFrame desiredWorld)
    {
        var px = parentInWorld.XMm;
        var py = parentInWorld.YMm;
        var pz = parentInWorld.ZMm;

        var delta = new[]
        {
            desiredWorld.OriginMm[0] - parentInWorld.OriginMm[0],
            desiredWorld.OriginMm[1] - parentInWorld.OriginMm[1],
            desiredWorld.OriginMm[2] - parentInWorld.OriginMm[2],
        };

        return new RigidFrame
        {
            OriginMm = TransposeApply(px, py, pz, delta),
            XMm = TransposeApply(px, py, pz, desiredWorld.XMm),
            YMm = TransposeApply(px, py, pz, desiredWorld.YMm),
            ZMm = TransposeApply(px, py, pz, desiredWorld.ZMm),
        };
    }

    /// <summary>Multiply a vector by the transpose of the orthonormal basis given as columns.</summary>
    private static double[] TransposeApply(double[] c0, double[] c1, double[] c2, double[] v) => new[]
    {
        RigidFrame.Dot(c0, v),
        RigidFrame.Dot(c1, v),
        RigidFrame.Dot(c2, v),
    };

    private static double[] RotateIntoParent(RigidFrame parent, double[] childVector) => new[]
    {
        (parent.XMm[0] * childVector[0]) + (parent.YMm[0] * childVector[1]) + (parent.ZMm[0] * childVector[2]),
        (parent.XMm[1] * childVector[0]) + (parent.YMm[1] * childVector[1]) + (parent.ZMm[1] * childVector[2]),
        (parent.XMm[2] * childVector[0]) + (parent.YMm[2] * childVector[1]) + (parent.ZMm[2] * childVector[2]),
    };

    /// <summary>Rotation about a unit axis by degrees, right-hand rule (Rodrigues).</summary>
    public static RigidFrame RotateAbout(RigidFrame frame, double[] unitAxis, double angleDeg)
    {
        var axis = ReadVector(unitAxis, "axis", required: true);
        var norm = RigidFrame.Norm(axis);
        if (Math.Abs(norm - 1d) > OrthonormalityTolerance)
        {
            throw Invalid("axis", $"Длина оси вращения {norm:R}, требуется единичная.");
        }

        if (!double.IsFinite(angleDeg))
        {
            throw Invalid("angle_deg", "Угол должен быть конечным числом.");
        }

        var rad = angleDeg * (Math.PI / 180d);
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var t = 1d - c;
        var (x, y, z) = (axis[0], axis[1], axis[2]);

        // Column-major rotation matrix applied to the frame's own axes.
        var m = new[,]
        {
            { (c + (x * x * t)), (x * y * t) - (z * s), (x * z * t) + (y * s) },
            { (y * x * t) + (z * s), (c + (y * y * t)), (y * z * t) - (x * s) },
            { (z * x * t) - (y * s), (z * y * t) + (x * s), (c + (z * z * t)) },
        };

        return new RigidFrame
        {
            OriginMm = ApplyMatrix(m, frame.OriginMm),
            XMm = ApplyMatrix(m, frame.XMm),
            YMm = ApplyMatrix(m, frame.YMm),
            ZMm = ApplyMatrix(m, frame.ZMm),
        };
    }

    private static double[] ApplyMatrix(double[,] m, double[] v) => new[]
    {
        (m[0, 0] * v[0]) + (m[0, 1] * v[1]) + (m[0, 2] * v[2]),
        (m[1, 0] * v[0]) + (m[1, 1] * v[1]) + (m[1, 2] * v[2]),
        (m[2, 0] * v[0]) + (m[2, 1] * v[1]) + (m[2, 2] * v[2]),
    };

    private static double[] ReadVector(IReadOnlyList<double>? values, string fieldPath, bool required)
    {
        if (values is null)
        {
            if (required)
            {
                throw Invalid(fieldPath, "Поле обязательно.");
            }

            return new double[] { 0, 0, 0 };
        }

        if (values.Count != 3)
        {
            throw Invalid(fieldPath, $"Ожидается 3 компоненты, получено {values.Count}.");
        }

        for (var i = 0; i < 3; i++)
        {
            var v = values[i];
            if (!double.IsFinite(v))
            {
                throw Invalid(fieldPath, $"Компонента [{i}] = {v} не является конечным числом.");
            }

            if (Math.Abs(v) > MaxCoordinateMagnitudeMm)
            {
                throw Invalid(fieldPath, $"Компонента [{i}] = {v:R} превышает допустимый порядок {MaxCoordinateMagnitudeMm:R} мм.");
            }
        }

        return new[] { values[0], values[1], values[2] };
    }

    private static KompasContractException Invalid(string field, string reason) => new(
        ErrorCodes.InvalidArgument,
        $"Трансформация некорректна ({field}): {reason}",
        details: new Dictionary<string, object?> { ["field"] = field, ["reason"] = reason });
}

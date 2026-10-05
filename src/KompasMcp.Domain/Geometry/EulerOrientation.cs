namespace KompasMcp.Domain.Geometry;

/// <summary>Placement orientation in the documented KOMPAS-3D v24 Euler-angle mode
/// (<c>ILocalCoordinateSystem.OrientationType = ksEulerCorners</c>).</summary>
/// <remarks>MEASURED: the orientation is stored as PARAMETERS, not a matrix — the angle triple survives
/// reopening; units are DEGREES. INVARIANT: <c>M = Rz(precession)·Rx(nutation)·Rz(rotation)</c> (z-x-z),
/// MEASURED not guessed (DOC gives the order only as a picture, <c>rotation_pict.html</c>; ONE product
/// matched — PNR). LIMIT: ambiguous at nutation 0/180; validity is proved by MATRIX EQUIVALENCE, not by
/// matching numbers.
/// History: docs/decisions/geometry.md#euler</remarks>
public static class EulerOrientation
{
    /// <summary>Matrix comparison tolerance: dimensionless values of order one.</summary>
    public const double MatrixTolerance = 1e-9;

    /// <summary>Tolerance below which nutation counts as degenerate (a pole of the parametrisation).</summary>
    private const double DegenerateNutation = 1e-9;

    /// <summary>Rotation matrix from three Euler angles in the MEASURED <c>PNR</c> order.</summary>
    public static double[] RotationFromAngles(
        double precessionDeg, double nutationDeg, double rotationDeg)
    {
        var p = precessionDeg * Math.PI / 180d;
        var n = nutationDeg * Math.PI / 180d;
        var r = rotationDeg * Math.PI / 180d;
        var cp = Math.Cos(p);
        var sp = Math.Sin(p);
        var cn = Math.Cos(n);
        var sn = Math.Sin(n);
        var cr = Math.Cos(r);
        var sr = Math.Sin(r);

        // M = Rz(P)·Rx(N)·Rz(R), written row by row:
        //   [ cp·cr − sp·cn·sr   −cp·sr − sp·cn·cr    sp·sn ]
        //   [ sp·cr + cp·cn·sr   −sp·sr + cp·cn·cr   −cp·sn ]
        //   [ sn·sr               sn·cr               cn   ]
        return FromRows(
            (cp * cr) - (sp * cn * sr), (-cp * sr) - (sp * cn * cr), sp * sn,
            (sp * cr) + (cp * cn * sr), (-sp * sr) + (cp * cn * cr), -cp * sn,
            sn * sr, sn * cr, cn);
    }

    /// <summary>Three Euler angles from a rotation matrix — the inverse of <see cref="RotationFromAngles"/>.</summary>
    /// <remarks>At the pole (<c>sn = 0</c>) precession and rotation are not separately defined; precession
    /// is taken as zero and rotation carries the whole sum. A matrix rebuilt from the returned triple equals
    /// the input, and that equality is what the tests check.
    /// History: docs/decisions/geometry.md#euler-pole</remarks>
    public static (double PrecessionDeg, double NutationDeg, double RotationDeg) AnglesFromRotation(
        IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        var m00 = Cell(matrix, 0, 0);
        var m01 = Cell(matrix, 0, 1);
        var m02 = Cell(matrix, 0, 2);
        var m10 = Cell(matrix, 1, 0);
        var m12 = Cell(matrix, 1, 2);
        var m20 = Cell(matrix, 2, 0);
        var m21 = Cell(matrix, 2, 1);
        var m22 = Cell(matrix, 2, 2);

        var nutation = Math.Acos(Math.Clamp(m22, -1d, 1d));
        var sin = Math.Sin(nutation);
        double precession;
        double rotation;
        if (Math.Abs(sin) > DegenerateNutation)
        {
            precession = Math.Atan2(m02, -m12);
            rotation = Math.Atan2(m20, m21);
        }
        else
        {
            // Pole: nutation = 0 or 180. Precession is taken as zero, so rotation carries the whole
            // difference — AND THE SIGN DIFFERS BETWEEN THE TWO POLES. Derivation:
            // docs/decisions/geometry.md#euler-pole
            precession = 0d;
            rotation = m22 > 0d ? Math.Atan2(m10, m00) : Math.Atan2(-m01, m00);
        }

        return (Degrees(precession), Degrees(nutation), Degrees(rotation));
    }

    /// <summary>Axis and angle from a matrix. The axis is the eigenvector with eigenvalue 1; at 180° the
    /// skew part is zero and the axis comes from <c>R + I</c>.</summary>
    public static (double[] Axis, double AngleDeg) AxisAngleFromRotation(IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        var trace = Cell(matrix, 0, 0) + Cell(matrix, 1, 1) + Cell(matrix, 2, 2);
        var angle = Math.Acos(Math.Clamp((trace - 1d) / 2d, -1d, 1d));

        var axis = new[]
        {
            Cell(matrix, 2, 1) - Cell(matrix, 1, 2),
            Cell(matrix, 0, 2) - Cell(matrix, 2, 0),
            Cell(matrix, 1, 0) - Cell(matrix, 0, 1),
        };
        var norm = Norm(axis);
        if (norm > 1e-9)
        {
            return (Scale(axis, 1d / norm), Degrees(angle));
        }

        if (angle < 1e-9)
        {
            // No rotation: the axis is undefined, and inventing one is forbidden.
            return (Array.Empty<double>(), 0d);
        }

        // 180°: R + I is symmetric and its columns are parallel to the axis; take the largest by norm.
        var candidates = new[]
        {
            new[] { Cell(matrix, 0, 0) + 1d, Cell(matrix, 1, 0), Cell(matrix, 2, 0) },
            new[] { Cell(matrix, 0, 1), Cell(matrix, 1, 1) + 1d, Cell(matrix, 2, 1) },
            new[] { Cell(matrix, 0, 2), Cell(matrix, 1, 2), Cell(matrix, 2, 2) + 1d },
        };
        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (Norm(candidate) > Norm(best))
            {
                best = candidate;
            }
        }

        var bestNorm = Norm(best);
        return bestNorm > 1e-9
            ? (Scale(best, 1d / bestNorm), Degrees(angle))
            : (Array.Empty<double>(), Degrees(angle));
    }

    /// <summary>Point on the rotation axis DERIVED from the placement:
    /// <c>c = ((1−cos θ)·t + sin θ·(d × t)) / (2(1−cos θ))</c>.</summary>
    /// <remarks>LIMIT: an axis point is not a property of the placement — rotating about any point of ONE
    /// line gives the same placement, so what is recovered is a representative of the line, not "that"
    /// point; at θ = 0 <c>null</c> is returned, not zero.
    /// History: docs/decisions/geometry.md#axis-point</remarks>
    public static double[]? AxisPointFromPlacement(IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        var (axis, angleDeg) = AxisAngleFromRotation(matrix);
        if (axis.Length != 3 || angleDeg <= 1e-9)
        {
            return null;
        }

        var angle = angleDeg * Math.PI / 180d;
        var oneMinusCos = 1d - Math.Cos(angle);
        var sin = Math.Sin(angle);
        var denominator = 2d * oneMinusCos;
        if (Math.Abs(denominator) < 1e-12)
        {
            return null;
        }

        var t = new[] { matrix[12], matrix[13], matrix[14] };
        var cross = Cross(axis, t);
        return new[]
        {
            ((oneMinusCos * t[0]) + (sin * cross[0])) / denominator,
            ((oneMinusCos * t[1]) + (sin * cross[1])) / denominator,
            ((oneMinusCos * t[2]) + (sin * cross[2])) / denominator,
        };
    }

    /// <summary>Translation of the placement — the same 12…14 that <see cref="RepositionMatrix.Apply"/> reads.</summary>
    public static double[] TranslationOf(IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        return new[] { matrix[12], matrix[13], matrix[14] };
    }

    public static bool IsIdentity(IReadOnlyList<double> matrix, double tolerance = MatrixTolerance)
    {
        Require(matrix, nameof(matrix));
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var expected = row == column ? 1d : 0d;
                if (Math.Abs(Cell(matrix, row, column) - expected) > tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static double MaxDifference(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        Require(left, nameof(left));
        Require(right, nameof(right));
        var worst = 0d;
        for (var i = 0; i < RepositionMatrix.Size; i++)
        {
            worst = Math.Max(worst, Math.Abs(left[i] - right[i]));
        }

        return worst;
    }

    public static double[] UnitAxis(IReadOnlyList<double> direction)
    {
        if (direction is not { Count: 3 })
        {
            throw new ArgumentException(
                $"Направление оси обязано состоять из трёх чисел, а состоит из {direction?.Count ?? 0}.",
                nameof(direction));
        }

        var raw = new[] { direction[0], direction[1], direction[2] };
        var norm = Norm(raw);
        if (!double.IsFinite(norm) || norm <= 0d)
        {
            throw new ArgumentException("Нулевое направление ось не задаёт.", nameof(direction));
        }

        return Scale(raw, 1d / norm);
    }

    private static double Cell(IReadOnlyList<double> matrix, int row, int column) =>
        matrix[(column * 4) + row];

    private static double[] FromRows(
        double r00, double r01, double r02,
        double r10, double r11, double r12,
        double r20, double r21, double r22) => new[]
    {
        r00, r10, r20, 0d,
        r01, r11, r21, 0d,
        r02, r12, r22, 0d,
        0d, 0d, 0d, 1d,
    };

    private static void Require(IReadOnlyList<double>? matrix, string name)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Count != RepositionMatrix.Size)
        {
            throw new ArgumentException(
                $"Матрица обязана содержать {RepositionMatrix.Size} чисел, а содержит {matrix.Count}.",
                name);
        }
    }

    private static double Degrees(double radians) => radians * 180d / Math.PI;

    private static double Norm(double[] value) =>
        Math.Sqrt((value[0] * value[0]) + (value[1] * value[1]) + (value[2] * value[2]));

    private static double[] Scale(double[] value, double factor) =>
        new[] { value[0] * factor, value[1] * factor, value[2] * factor };

    private static double[] Cross(double[] a, double[] b) => new[]
    {
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0]),
    };
}

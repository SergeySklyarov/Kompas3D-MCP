namespace KompasMcp.Domain.Geometry;

/// <summary>Homogeneous 4x4 matrix in the layout KOMPAS-3D v24 uses to write a body's position.</summary>
/// <remarks>MEASURED 18.09.2026 (probe --reposition, step RP.2, run 929f08886f1348fe921943052a4026b0):
/// ONLY the 16-number matrix moves the body — the 12-number routes and <c>SetDisplacementByAxis</c>
/// return <c>Update() = true</c> and do not move it, so a successful <c>Update()</c> proves nothing here.
/// LAYOUT: a 3x3 whose COLUMNS are the basis-vector images, then the translation row, then 1; the array
/// stores the triples consecutively, i.e. COLUMN-major, so the index order is the reverse of <c>R[i, j]</c>.
/// INVARIANT: a layout must have a discriminating control — a ROTATION; on a translation rows and columns
/// cannot be told apart (the unit rotation is symmetric). History: docs/decisions/geometry.md#reposition-layout</remarks>
public static class RepositionMatrix
{
    /// <summary>Element count: 16, and no other moves the body.</summary>
    public const int Size = 16;

    public static double[] Identity() => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        0d, 0d, 0d, 1d,
    };

    /// <summary>Pure translation: identity rotation and the offset in the last row.</summary>
    public static double[] Translate(IReadOnlyList<double> vectorMm)
    {
        RequireTriple(vectorMm, nameof(vectorMm));
        return new[]
        {
            1d, 0d, 0d, 0d,
            0d, 1d, 0d, 0d,
            0d, 0d, 1d, 0d,
            vectorMm[0], vectorMm[1], vectorMm[2], 1d,
        };
    }

    /// <summary>Rotation by <paramref name="angleDeg"/> about an axis through <paramref name="axisPointMm"/>
    /// with direction <paramref name="axisDirectionMm"/>. The translation is <c>c − R·c</c>, so the axis
    /// point stays put.</summary>
    /// <remarks>The sign is the right-hand rule about the axis direction. MEASURED (RP.4): a bar
    /// <c>[10,30]×[0,10]×[0,5]</c> rotated +90° about Z through the origin gives <c>(−10,10,0)…(0,30,5)</c>,
    /// i.e. <c>(x,y) → (−y,x)</c>.</remarks>
    public static double[] RotateAboutAxis(
        IReadOnlyList<double> axisPointMm,
        IReadOnlyList<double> axisDirectionMm,
        double angleDeg)
    {
        RequireTriple(axisPointMm, nameof(axisPointMm));
        RequireTriple(axisDirectionMm, nameof(axisDirectionMm));

        var length = RigidFrame.Norm(new[] { axisDirectionMm[0], axisDirectionMm[1], axisDirectionMm[2] });
        if (!double.IsFinite(length) || length <= 0d)
        {
            throw new ArgumentException(
                "Направление оси поворота обязано быть ненулевым и конечным.", nameof(axisDirectionMm));
        }

        var k = new[] { axisDirectionMm[0] / length, axisDirectionMm[1] / length, axisDirectionMm[2] / length };
        var angle = angleDeg * Math.PI / 180d;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var oneMinusCos = 1d - cos;

        // Rodrigues: R = I·cosθ + sinθ·[k]× + (1−cosθ)·k⊗k.
        var r = new[,]
        {
            {
                (cos + (oneMinusCos * k[0] * k[0])),
                ((oneMinusCos * k[0] * k[1]) - (sin * k[2])),
                ((oneMinusCos * k[0] * k[2]) + (sin * k[1])),
            },
            {
                ((oneMinusCos * k[1] * k[0]) + (sin * k[2])),
                (cos + (oneMinusCos * k[1] * k[1])),
                ((oneMinusCos * k[1] * k[2]) - (sin * k[0])),
            },
            {
                ((oneMinusCos * k[2] * k[0]) - (sin * k[1])),
                ((oneMinusCos * k[2] * k[1]) + (sin * k[0])),
                (cos + (oneMinusCos * k[2] * k[2])),
            },
        };

        var c = new[] { axisPointMm[0], axisPointMm[1], axisPointMm[2] };
        var t = new[]
        {
            c[0] - ((r[0, 0] * c[0]) + (r[0, 1] * c[1]) + (r[0, 2] * c[2])),
            c[1] - ((r[1, 0] * c[0]) + (r[1, 1] * c[1]) + (r[1, 2] * c[2])),
            c[2] - ((r[2, 0] * c[0]) + (r[2, 1] * c[1]) + (r[2, 2] * c[2])),
        };

        return new[]
        {
            // COLUMN-major, not row-major: three consecutive numbers are the IMAGE OF ONE BASIS AXIS
            // (R00,R10,R20 — image of X; R01,R11,R21 — image of Y; R02,R12,R22 — image of Z). The layout
            // is MEASURED (RP.2/RP.4) and stated in the class header; the index order is reversed
            // relative to the mathematical R[i, j].
            r[0, 0], r[1, 0], r[2, 0], 0d,
            r[0, 1], r[1, 1], r[2, 1], 0d,
            r[0, 2], r[1, 2], r[2, 2], 0d,
            t[0], t[1], t[2], 1d,
        };
    }

    /// <summary>Apply the matrix to a point — a control independent of KOMPAS: it checks that the built
    /// matrix really yields the computed bounding box.</summary>
    /// <remarks>Reads the array in the SAME layout <see cref="RotateAboutAxis"/> writes: axis images in
    /// consecutive triples. A mismatch between the two places is invisible on a translation and visible
    /// only on a rotation — which is how the layout defect reached acceptance.
    /// History: docs/decisions/geometry.md#reposition-layout</remarks>
    public static double[] Apply(double[] matrix, double[] point)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(point);
        if (matrix.Length != Size)
        {
            throw new ArgumentException($"Матрица обязана содержать {Size} чисел, а содержит {matrix.Length}.", nameof(matrix));
        }

        RequireTriple(point, nameof(point));
        var x = point[0];
        var y = point[1];
        var z = point[2];
        return new[]
        {
            (matrix[0] * x) + (matrix[4] * y) + (matrix[8] * z) + matrix[12],
            (matrix[1] * x) + (matrix[5] * y) + (matrix[9] * z) + matrix[13],
            (matrix[2] * x) + (matrix[6] * y) + (matrix[10] * z) + matrix[14],
        };
    }

    private static void RequireTriple(IReadOnlyList<double>? value, string name)
    {
        if (value is not { Count: 3 })
        {
            throw new ArgumentException($"Ожидались три числа, а получено {value?.Count ?? 0}.", name);
        }

        foreach (var number in value)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException("Координаты обязаны быть конечными числами.", name);
            }
        }
    }
}

/// <summary>Orthonormal basis of a plane — how a normal becomes three model points.</summary>
/// <remarks>Needed because the MEASURED API7 plane route (step SP.1) is "by three model points":
/// <c>Planes3D.Add(o3d_plane3Points)</c> → <c>IPlane3DBy3Points</c>, where the normal is
/// <c>(P2−P1)×(P3−P1)</c>. The basis is built so that this product matches the REQUESTED normal, not a
/// random rotation of it in the plane.</remarks>
public static class PlaneBasis
{
    /// <summary>Basis <c>(e1, e2)</c> in the plane with <paramref name="normal"/>, such that
    /// <c>e1 × e2 = n̂</c>. The normal is unitised: the sign <c>s = n·(p − p₀)</c> does not depend on its
    /// length, so a unit normal is the same answer but reproducible.</summary>
    public static (double[] E1, double[] E2, double[] UnitNormal) FromNormal(IReadOnlyList<double> normal)
    {
        if (normal is not { Count: 3 })
        {
            throw new ArgumentException($"Нормаль обязана состоять из трёх чисел, а состоит из {normal?.Count ?? 0}.", nameof(normal));
        }

        foreach (var number in normal)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException("Нормаль обязана состоять из конечных чисел.", nameof(normal));
            }
        }

        var raw = new[] { normal[0], normal[1], normal[2] };
        var length = RigidFrame.Norm(raw);
        if (length <= 0d)
        {
            throw new ArgumentException("Нулевая нормаль плоскость не задаёт.", nameof(normal));
        }

        var n = new[] { raw[0] / length, raw[1] / length, raw[2] / length };

        // The seed vector is the least parallel to the normal: its largest component is perpendicular to
        // n, so the cross product cannot degenerate.
        var ax = Math.Abs(n[0]);
        var ay = Math.Abs(n[1]);
        var az = Math.Abs(n[2]);
        var seed = ax <= ay && ax <= az ? new[] { 1d, 0d, 0d }
            : ay <= az ? new[] { 0d, 1d, 0d }
            : new[] { 0d, 0d, 1d };

        var e1 = RigidFrame.Cross(n, seed);
        var e1Length = RigidFrame.Norm(e1);
        e1 = new[] { e1[0] / e1Length, e1[1] / e1Length, e1[2] / e1Length };
        var e2 = RigidFrame.Cross(n, e1);

        // Self-check inside the construction: e1 × e2 must equal n̂, otherwise the plane gets the opposite
        // normal, and the sign here is the choice of side, not a detail.
        var check = RigidFrame.Cross(e1, e2);
        if (Math.Abs(check[0] - n[0]) > 1e-12 || Math.Abs(check[1] - n[1]) > 1e-12 || Math.Abs(check[2] - n[2]) > 1e-12)
        {
            throw new InvalidOperationException("Построенный базис не воспроизводит заданную нормаль.");
        }

        return (e1, e2, n);
    }

    /// <summary>Three model points for the plane through <paramref name="pointMm"/> with the given normal.
    /// The point order is the one MEASURED to give <c>(P2−P1)×(P3−P1) = n</c>.</summary>
    public static (double[] P1, double[] P2, double[] P3, double[] UnitNormal) ThreePoints(
        IReadOnlyList<double> pointMm, IReadOnlyList<double> normal)
    {
        if (pointMm is not { Count: 3 })
        {
            throw new ArgumentException($"Точка обязана состоять из трёх чисел, а состоит из {pointMm?.Count ?? 0}.", nameof(pointMm));
        }

        foreach (var number in pointMm)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException("Точка обязана состоять из конечных чисел.", nameof(pointMm));
            }
        }

        var (e1, e2, unit) = FromNormal(normal);
        var p1 = new[] { pointMm[0], pointMm[1], pointMm[2] };
        return (
            p1,
            new[] { p1[0] + e1[0], p1[1] + e1[1], p1[2] + e1[2] },
            new[] { p1[0] + e2[0], p1[1] + e2[1], p1[2] + e2[2] },
            unit);
    }
}

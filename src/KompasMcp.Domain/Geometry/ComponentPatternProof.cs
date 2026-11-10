using System.Globalization;

namespace KompasMcp.Domain.Geometry;

/// <summary>Expected positions of a COMPONENT pattern's instances, and their comparison with the model.</summary>
/// <remarks>WHY A SEPARATE PURE CLASS. A component pattern is confirmed by COMPARING the origins read from
/// the model with the origins derived from the request, not by <c>Update()=true</c>: the kernel accepts a
/// write and can build a pattern that is not the requested one. The arithmetic is kept away from COM so a
/// test can state it without a running KOMPAS.
/// DOC: <c>ilinearpattern_props.html</c> (Step1/Count1, Step2/Count2, Angle1/Angle2),
/// <c>icircularpattern_props.html</c> (Count1/Step1 radial, Count2/Step2 angular in degrees).
/// History: docs/decisions/assembly-ext.md#component-arrays</remarks>
public static class ComponentPatternProof
{
    /// <summary>Origin tolerance, mm — the same figure the per-instance hole check uses.</summary>
    public const double OriginToleranceMm = 0.01d;

    /// <summary>Angular step tolerance, degrees.</summary>
    public const double AngleToleranceDeg = 0.01d;

    /// <summary>Origin of a polar angle: below this radius the angle of a point is not defined.</summary>
    private const double AxialRadiusMm = 1e-9d;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Expected instance origins, or the named reason they cannot be derived from the request.</summary>
    /// <param name="Expected">Instance origins including the source's own position, which is instance 0.</param>
    /// <param name="Unavailable">Why the set is not derivable; null when it is.</param>
    public sealed record Plan(IReadOnlyList<double[]> Expected, string? Unavailable);

    /// <summary>Grid: copies along two directions from the source.</summary>
    /// <remarks>INVARIANT: the caller passes UNIT vectors and only the plain <c>save_all</c> order —
    /// chess order and boundary factors change WHICH instances exist, and a tilted direction
    /// (<c>Angle1</c>) changes where they stand, so those requests derive no expectation here.
    /// LIMIT: the second direction is taken as the built axis; a non-perpendicular pair is refused by the
    /// caller because the model lays copies at <c>Angle2</c> between directions, not along the axis.</remarks>
    public static Plan Grid(double[] sourceOrigin, double[] direction1, double step1Mm, int count1,
        double[]? direction2, double step2Mm, int count2)
    {
        var expected = new List<double[]>(checked(count1 * Math.Max(count2, 1)));
        for (var i = 0; i < count1; i++)
        {
            for (var j = 0; j < count2; j++)
            {
                expected.Add(
                [
                    sourceOrigin[0] + i * step1Mm * direction1[0] + j * step2Mm * (direction2?[0] ?? 0d),
                    sourceOrigin[1] + i * step1Mm * direction1[1] + j * step2Mm * (direction2?[1] ?? 0d),
                    sourceOrigin[2] + i * step1Mm * direction1[2] + j * step2Mm * (direction2?[2] ?? 0d),
                ]);
            }
        }

        return new Plan(expected, null);
    }

    /// <summary>Circular grid: copies turned about the pattern axis by one angular step each.</summary>
    /// <remarks>LIMIT: only the SINGLE ring (<paramref name="count1"/> = 1) is derived. A radial layer
    /// needs the radial direction convention of the kernel, which no run measured.
    /// MEASURED: instance 0 keeps the source's own origin.</remarks>
    public static Plan Circular(double[] sourceOrigin, double[] axisPoint, double[] axisDirection,
        int count1, int count2, double stepDeg)
    {
        if (count1 != 1)
        {
            return new Plan(Array.Empty<double[]>(),
                "радиальных слоёв больше одного: направление радиального смещения ядром не измерено");
        }

        var expected = new List<double[]>(count2);
        for (var k = 0; k < count2; k++)
        {
            expected.Add(k == 0
                ? [sourceOrigin[0], sourceOrigin[1], sourceOrigin[2]]
                : RotateAboutAxis(sourceOrigin, axisPoint, axisDirection, k * stepDeg));
        }

        return new Plan(expected, null);
    }

    /// <summary>Compare the read origins with the expected set as MULTISETS.</summary>
    /// <remarks>INVARIANT: one measured origin closes one expectation — a set comparison would pass a
    /// pattern whose instances are shifted but whose positions coincide with another node.</remarks>
    public static (bool Matched, string Detail) Match(IReadOnlyList<double[]> measured,
        IReadOnlyList<double[]> expected)
    {
        if (expected.Count == 0)
        {
            return (false, "ожидаемый набор пуст: сверять нечего");
        }

        var used = new bool[measured.Count];
        var missing = new List<string>();
        foreach (var want in expected)
        {
            var hit = -1;
            for (var i = 0; i < measured.Count; i++)
            {
                if (used[i] || !Near(measured[i], want))
                {
                    continue;
                }

                hit = i;
                break;
            }

            if (hit >= 0)
            {
                used[hit] = true;
            }
            else
            {
                missing.Add(Point(want));
            }
        }

        var extra = new List<string>();
        for (var i = 0; i < measured.Count; i++)
        {
            if (!used[i])
            {
                extra.Add(Point(measured[i]));
            }
        }

        var matched = expected.Count - missing.Count;
        var detail = $"совпало {matched} из {expected.Count}; не найдено {missing.Count}, лишних {extra.Count}"
            + (missing.Count > 0 ? "; нет: " + string.Join(", ", missing) : string.Empty)
            + (extra.Count > 0 ? "; лишние: " + string.Join(", ", extra) : string.Empty);
        return (missing.Count == 0 && extra.Count == 0, detail);
    }

    /// <summary>Origins of the instances in the order the model gave them, each matched to a node.</summary>
    /// <remarks>Used for the answer: the client sees origins READ from the model, never the requested ones.
    /// An expectation that no measured origin satisfies is left out and named by <see cref="Match"/>.</remarks>
    public static IReadOnlyList<double[]> OrderedMatches(IReadOnlyList<double[]> measured,
        IReadOnlyList<double[]> expected)
    {
        var used = new bool[measured.Count];
        var order = new List<double[]>();
        foreach (var want in expected)
        {
            for (var i = 0; i < measured.Count; i++)
            {
                if (used[i] || !Near(measured[i], want))
                {
                    continue;
                }

                used[i] = true;
                order.Add(measured[i]);
                break;
            }
        }

        return order;
    }

    /// <summary>Angles of the instance origins about the pattern axis, degrees in [0, 360).</summary>
    public static IReadOnlyList<double> Angles(IReadOnlyList<double[]> origins, double[] axisPoint,
        double[] axisDirection)
    {
        var (u, v) = Basis(axisDirection);
        var angles = new List<double>(origins.Count);
        foreach (var origin in origins)
        {
            var r = Sub(origin, axisPoint);
            var x = Dot(r, u);
            var y = Dot(r, v);
            if (Math.Sqrt(x * x + y * y) <= AxialRadiusMm)
            {
                // ON THE AXIS THE ANGLE IS NOT DEFINED: an instance standing on the axis is not a ring
                // position, and a made-up zero would read as "the first position".
                return Array.Empty<double>();
            }

            var deg = Math.Atan2(y, x) * 180d / Math.PI;
            angles.Add(deg < 0d ? deg + 360d : deg);
        }

        return angles;
    }

    /// <summary>Successive angular steps of a sorted set of angles, compared with the requested step.</summary>
    /// <remarks>WHY CONSECUTIVE PAIRS AND NOT THE WHOLE TURN: a partial ring (<c>count2</c> = 3 at 90°) has
    /// a wrap gap of 180°, which says nothing about the step the kernel applied.</remarks>
    public static (bool Matched, string Detail) SuccessiveSteps(IReadOnlyList<double> anglesDeg,
        double stepDeg)
    {
        if (anglesDeg.Count < 2)
        {
            return (false, $"углов прочитано {anglesDeg.Count}: соседних пар нет");
        }

        var sorted = anglesDeg.OrderBy(a => a).ToArray();
        var worst = 0d;
        var steps = new List<string>(sorted.Length - 1);
        for (var i = 1; i < sorted.Length; i++)
        {
            var delta = sorted[i] - sorted[i - 1];
            steps.Add(delta.ToString("0.###", Inv));
            worst = Math.Max(worst, Math.Abs(delta - stepDeg));
        }

        var detail = $"шаги между соседними: {string.Join(", ", steps)}; запрошено "
            + stepDeg.ToString("0.###", Inv) + "; наибольшее расхождение "
            + worst.ToString("0.###", Inv) + "°";
        return (worst <= AngleToleranceDeg, detail);
    }

    /// <summary>Rotate a point about the axis line through <paramref name="point"/> (Rodrigues).</summary>
    private static double[] RotateAboutAxis(double[] source, double[] point, double[] direction, double deg)
    {
        var rad = deg * Math.PI / 180d;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var r = Sub(source, point);
        var k = direction;
        var cross = new[]
        {
            k[1] * r[2] - k[2] * r[1],
            k[2] * r[0] - k[0] * r[2],
            k[0] * r[1] - k[1] * r[0],
        };
        var kd = Dot(k, r);
        return
        [
            point[0] + r[0] * c + cross[0] * s + k[0] * kd * (1 - c),
            point[1] + r[1] * c + cross[1] * s + k[1] * kd * (1 - c),
            point[2] + r[2] * c + cross[2] * s + k[2] * kd * (1 - c),
        ];
    }

    /// <summary>Two unit vectors orthogonal to the axis and to each other.</summary>
    private static (double[] U, double[] V) Basis(double[] axis)
    {
        var helper = Math.Abs(axis[0]) <= Math.Abs(axis[1]) && Math.Abs(axis[0]) <= Math.Abs(axis[2])
            ? new[] { 1d, 0d, 0d }
            : Math.Abs(axis[1]) <= Math.Abs(axis[2]) ? [0d, 1d, 0d] : [0d, 0d, 1d];
        var u = Normalise(Cross(axis, helper));
        return (u, Normalise(Cross(axis, u)));
    }

    private static bool Near(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) <= OriginToleranceMm
        && Math.Abs(a[1] - b[1]) <= OriginToleranceMm
        && Math.Abs(a[2] - b[2]) <= OriginToleranceMm;

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Cross(double[] a, double[] b) =>
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ];

    /// <summary>Unit vector; a degenerate vector answers null, and the caller names that as "not derivable".</summary>
    public static double[]? Unit(double[] vector)
    {
        var length = Math.Sqrt(Dot(vector, vector));
        if (length <= 1e-12)
        {
            return null;
        }

        return [vector[0] / length, vector[1] / length, vector[2] / length];
    }

    private static double[] Normalise(double[] v) => Unit(v) ?? [1d, 0d, 0d];

    private static string Point(double[] p) =>
        "(" + p[0].ToString("0.###", Inv) + ", " + p[1].ToString("0.###", Inv) + ", "
        + p[2].ToString("0.###", Inv) + ")";
}

namespace KompasMcp.Domain.Geometry;

/// <summary>Which way, in PART coordinates, a material change went — derived from the sketch normal.</summary>
/// <remarks>WHY a separate rule. The side used to be read only from the MEASURED shift of the target body's
/// gabarit, so a hole or pocket cut INSIDE a body (the commonest cut) moved no bound and the side stayed
/// unnamed. The sketch normal is available for every planar support — base plane, offset plane or flat face —
/// and the v24 help fixes the sign: «Для вырезаемого элемента выдавливания направление противоположно
/// нормали», «Прямое направление совпадает с нормалью, проведенной к плоскости эскиза». So base/boss move
/// material ALONG the normal and a cut AGAINST it, and `negative` flips the sign; `symmetric` acts on both
/// sides. The rule is pure so it can be tested without COM.
/// DOC: kscutextrusiondefinition_directiontype.html, ksbossextrusiondefinition_directiontype.html.
/// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
public static class MaterialDirectionRule
{
    /// <summary>How far a component may stray from a whole axis and still name it. The normal is read as a
    /// unit vector, so a clean base plane lands on ±1 exactly; the tolerance only absorbs round-off.</summary>
    public const double AxisTolerance = 1e-6;

    /// <summary>Direction material moves, from the sketch normal, in part coordinates. Null when
    /// <paramref name="symmetric"/> — material goes to BOTH sides, which is a value, not a missing one.</summary>
    public static double[]? Toward(double[] normal, bool removing, bool negative, bool symmetric)
    {
        if (symmetric)
        {
            return null;
        }

        // Cut removes AGAINST the normal, base/boss add ALONG it; `negative` reverses the request.
        var sign = (removing ? -1d : 1d) * (negative ? -1d : 1d);
        return [sign * normal[0], sign * normal[1], sign * normal[2]];
    }

    /// <summary>Render an axis-aligned direction as <c>+x</c>/<c>−x</c>…, or the vector for a tilted plane.
    /// The sign character is the MINUS SIGN (U+2212), matching the rest of the surface.</summary>
    public static string Describe(double[] direction, bool both)
    {
        var axis = AxisLabel(direction);
        if (both)
        {
            return axis is null
                ? "±(" + Vector(direction) + ") (в обе стороны)"
                : "±" + axis[1..] + " (в обе стороны)";
        }

        return axis ?? "(" + Vector(direction) + ")";
    }

    /// <summary>Render a symmetric change: the side is the normal's axis, marked as both.</summary>
    public static string DescribeSymmetric(double[] normal) =>
        AxisLabel(normal) is string axis
            ? "±" + axis[1..] + " (в обе стороны)"
            : "±(" + Vector(normal) + ") (в обе стороны)";

    /// <summary><c>+x</c>/<c>−x</c>/<c>+y</c>/<c>−y</c>/<c>+z</c>/<c>−z</c>, or null when the direction is not
    /// axis-aligned (a tilted plane) — then the caller names the vector, not a guessed axis.</summary>
    public static string? AxisLabel(double[] direction)
    {
        var names = new[] { "x", "y", "z" };
        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(Math.Abs(direction[i]) - 1d) > AxisTolerance)
            {
                continue;
            }

            var aligned = true;
            for (var j = 0; j < 3; j++)
            {
                if (j != i && Math.Abs(direction[j]) > AxisTolerance)
                {
                    aligned = false;
                }
            }

            if (aligned)
            {
                return (direction[i] > 0d ? "+" : "−") + names[i];
            }
        }

        return null;
    }

    /// <summary>Model axis index (0=x, 1=y, 2=z) a direction is aligned with, or null when it is not
    /// axis-aligned (a tilted plane) — the box then says nothing about the side.</summary>
    public static int? AxisIndex(double[]? direction)
    {
        if (direction is null)
        {
            return null;
        }

        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(Math.Abs(direction[i]) - 1d) > AxisTolerance)
            {
                continue;
            }

            var aligned = true;
            for (var j = 0; j < 3; j++)
            {
                if (j != i && Math.Abs(direction[j]) > AxisTolerance)
                {
                    aligned = false;
                }
            }

            if (aligned)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Whether two directions name the same side of the plane (parallel and same sense).</summary>
    public static bool SameSide(double[] a, double[] b) =>
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2] > 1d - 1e-6;

    private static string Vector(double[] v) => string.Join(", ",
        v.Select(c => c.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
}

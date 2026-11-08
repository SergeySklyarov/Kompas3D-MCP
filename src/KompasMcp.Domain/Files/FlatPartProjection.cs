namespace KompasMcp.Domain.Files;

/// <summary>The standard view whose line of sight is a flat part's normal.</summary>
public sealed record FlatPartView(int Flag, string Name, string Axis);

/// <summary>Picks the standard view that looks along a flat part's normal.</summary>
/// <remarks>DOC: <c>ksapi_iviews_addstandartviews.html</c> — the projection list is a bit mask,
/// «0x1 - Спереди, 0x2 - Сзади, 0x4 - Сверху, 0x8 - Снизу, 0x10 - Слева, 0x20 - Справа»;
/// <c>projectiontype.html</c> names the planes: «Спереди - Фронтальная плоскость» (XZ),
/// «Сверху - Горизонтальная плоскость» (XY), «Слева - Профильная плоскость» (YZ).
/// INVARIANT: only the six axis directions are named here. A tilted normal goes through
/// <see cref="Matrix"/>, the route measured for an arbitrary direction.
/// MEASURED: the list takes the <c>ksViewProjectionType</c> ordinals (1..6), not the mask, and a side view
/// is built only next to the main «Спереди» one — the caller adds it. The sign of the normal picks the view
/// of the opposite side, and that side sees the face mirrored; the box is the same for both sides, so the
/// frame cannot tell them apart. Which side is taken comes from the face order, not from the geometry:
/// extruding the same plate the other way kept the normal sign.
/// History: docs/decisions/flat-part.md#view-choice</remarks>
public static class FlatPartProjection
{
    /// <summary>Largest deviation of a normal's component from 1 that still counts as «along the axis».</summary>
    /// <remarks>INVARIANT: a planar face of an axis-aligned part gives its axis to floating-point precision,
    /// so this covers only the arithmetic; a real draft of 0.5° is already refused by the plane check.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    public const double AxisTolerance = 1e-4d;

    /// <summary>The view for this normal, or <c>null</c> when the normal is not along an axis.</summary>
    public static FlatPartView? Choose(double[]? normal)
    {
        if (normal is not { Length: 3 } || normal.Any(value => !double.IsFinite(value)))
        {
            return null;
        }

        var length = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
        if (length <= 0d)
        {
            return null;
        }

        var unit = new[] { normal[0] / length, normal[1] / length, normal[2] / length };
        var axis = 0;
        for (var i = 1; i < 3; i++)
        {
            if (Math.Abs(unit[i]) > Math.Abs(unit[axis]))
            {
                axis = i;
            }
        }

        if (Math.Abs(Math.Abs(unit[axis]) - 1d) > AxisTolerance)
        {
            return null;
        }

        var positive = unit[axis] > 0d;
        return axis switch
        {
            0 => positive
                ? new FlatPartView(6, "Справа", "X")
                : new FlatPartView(5, "Слева", "X"),
            1 => positive
                ? new FlatPartView(2, "Сзади", "Y")
                : new FlatPartView(1, "Спереди", "Y"),
            _ => positive
                ? new FlatPartView(3, "Сверху", "Z")
                : new FlatPartView(4, "Снизу", "Z"),
        };
    }

    /// <summary>The 4×4 of a view whose line of sight is this normal, or <c>null</c> when it is not a direction.</summary>
    /// <remarks>DOC: <c>iviewprojection7_setmatrix3d.html</c> — «16 элементов SafeAttray типа
    /// (VT_ARRAY | VT_R8). Матрица имеет размер 4х4», and nothing about the order of the elements.
    /// MEASURED (probe <c>scripts/probes/flat-tilt</c>): rows 0..2 are the view's X, Y and Z axes in model
    /// coordinates, row 3 is the translation — index 12 moved the view by 10 mm while indices 3, 7 and 11
    /// changed nothing, and element 15 was not read. The third ROW is the line of sight: writing the same
    /// numbers by columns changed which face the view shows. Agreement with the standard views was checked
    /// on all six axis directions; the arrangement «rows» was the only one that matched.
    /// INVARIANT: the triple is right-handed, so the face is seen from outside, never mirrored.
    /// History: docs/decisions/flat-part.md#tilted-normal</remarks>
    public static double[]? Matrix(double[]? normal)
    {
        var z = Unit(normal);
        if (z is null)
        {
            return null;
        }

        // MEASURED: a reference straight along the normal has no in-plane X, so the world Z is used where
        // it is not parallel to the normal and world Y where it is.
        var reference = Math.Abs(z[2]) < 0.9d ? new[] { 0d, 0d, 1d } : new[] { 0d, 1d, 0d };
        var x = Unit(Cross(reference, z));
        if (x is null)
        {
            return null;
        }

        var y = Cross(z, x);
        return new[] { x[0], x[1], x[2], 0d, y[0], y[1], y[2], 0d, z[0], z[1], z[2], 0d, 0d, 0d, 0d, 1d };
    }

    /// <summary>The normal as a unit vector, or <c>null</c> when it is not a readable direction.</summary>
    public static double[]? Unit(double[]? vector)
    {
        if (vector is not { Length: 3 } || vector.Any(value => !double.IsFinite(value)))
        {
            return null;
        }

        var length = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
        return length <= 0d ? null : new[] { vector[0] / length, vector[1] / length, vector[2] / length };
    }

    private static double[] Cross(double[] left, double[] right) => new[]
    {
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    };
}

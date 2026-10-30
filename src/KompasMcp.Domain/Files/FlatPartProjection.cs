namespace KompasMcp.Domain.Files;

/// <summary>The standard view whose line of sight is a flat part's normal.</summary>
public sealed record FlatPartView(int Flag, string Name, string Axis);

/// <summary>Picks the standard view that looks along a flat part's normal.</summary>
/// <remarks>DOC: <c>ksapi_iviews_addstandartviews.html</c> — the projection list is a bit mask,
/// «0x1 - Спереди, 0x2 - Сзади, 0x4 - Сверху, 0x8 - Снизу, 0x10 - Слева, 0x20 - Справа»;
/// <c>projectiontype.html</c> names the planes: «Спереди - Фронтальная плоскость» (XZ),
/// «Сверху - Горизонтальная плоскость» (XY), «Слева - Профильная плоскость» (YZ).
/// INVARIANT: only the six axis directions are projected. A tilted normal is refused by name, because the
/// shipped v24 type library has no documented route to a view normal to an arbitrary direction.
/// MEASURED: the API5 bit mask (0x4, 0x20) does NOT select a view here — 0x20 fell outside the enum and
/// the kernel built «Спереди», so the numbers are the API7 ordinals. The sign of the normal only picks the
/// opposite view of the same axis and does not change the contour.
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
}

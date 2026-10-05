using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>Where a coordinate for <c>ksFindObj</c> can be derived from when the server has no memory of
/// drawing the sketch at all, and under exactly which conditions that derivation is allowed.</summary>
/// <remarks>API5 has no sketch enumeration (<c>ksSketchDefinition</c> has no walker; <c>ksDocument2D</c> has
/// <c>ksFindObj</c>/<c>ksDeleteObj</c> but no <c>ksFirstObj</c>/<c>ksNextObj</c>), so the only way to point at
/// an existing primitive is a coordinate that lies on it. MEASURED: a through hole leaves a cylindrical face
/// yielding centre, radius and axis, and <c>(cx + r; cy)</c> lies on the sketch circle. LIMIT: proven for one
/// configuration only — a sketch on the base XY plane, a circle, a through cut; anything else must refuse
/// rather than extrapolate. History: docs/decisions/geometry.md#sketch-point-derivation</remarks>
public static class SketchPointDerivation
{
    public const double AxisTolerance = 1e-6;

    /// <summary>A face is a hole wall rather than a boss when its axis runs along the sketch normal.</summary>
    public const double RadiusToleranceMm = 1e-6;

    /// <summary>Whether a model-derived point may be used at all for the sketch being edited.</summary>
    /// <param name="planeBase">The base plane the sketch sits on, when the server chose it. Null means the
    /// sketch was built on a referenced plane or one the server did not create, and the 3D→2D mapping for
    /// that case was not measured.</param>
    /// <param name="profileKinds">Kinds of the primitives the caller asks to write. The derivation yields
    /// a point on a circle; a caller replacing the profile with segments would get a point that may lie on
    /// nothing.</param>
    public static SketchDerivationVerdict Verdict(
        PlaneBase? planeBase,
        IReadOnlyCollection<SketchEntityKind> profileKinds)
    {
        if (planeBase != PlaneBase.Xy)
        {
            return SketchDerivationVerdict.Refused(
                "plane_not_xy",
                planeBase is null
                    ? "Плоскость эскиза серверу неизвестна (эскиз создан не им или на ссылочной плоскости), " +
                      "а перенос 3D-координаты в плоскость эскиза измерен только для основной XY."
                    : $"Эскиз построен на плоскости {planeBase}, а перенос 3D-координаты в плоскость эскиза " +
                      "измерен только для основной XY.");
        }

        if (profileKinds.Count == 0)
        {
            return SketchDerivationVerdict.Refused(
                "profile_empty",
                "Список примитивов пуст: выводить точку не для чего.");
        }

        if (profileKinds.Any(kind => kind != SketchEntityKind.Circle))
        {
            return SketchDerivationVerdict.Refused(
                "profile_not_circle",
                "Точка выводится из цилиндрической грани и лежит на окружности эскиза; " +
                "для отрезков, дуг, прямоугольников и полилиний такой координаты не существует, " +
                "и маршрут обязан отказать, а не угадать.");
        }

        return SketchDerivationVerdict.Allowed();
    }

    /// <summary>Point on the sketch circle for a cylindrical face that is coaxial with the sketch normal.</summary>
    /// <param name="origin">Face placement origin in model coordinates. Its X and Y are the circle centre
    /// in sketch coordinates — valid only because the caller has established that the plane is XY.</param>
    /// <param name="radiusMm">Face radius, which equals the sketch circle radius.</param>
    /// <returns>The two probe points at both ends of a diameter.</returns>
    public static double[][] PointsOnCircle(IReadOnlyList<double> origin, double radiusMm) =>
        new[]
        {
            new[] { origin[0] + radiusMm, origin[1] },
            new[] { origin[0] - radiusMm, origin[1] },
        };

    /// <summary>Axis agreement with the sketch normal, the axis taken from
    /// <c>GetPlacement().GetVector(2)</c>: for an XY sketch the cylinder axis must run along Z.</summary>
    public static bool AxisIsNormalToXyPlane(IReadOnlyList<double>? axis) =>
        axis is { Count: 3 }
        && Math.Abs(Math.Abs(axis[2]) - 1d) <= AxisTolerance
        && Math.Abs(axis[0]) <= AxisTolerance
        && Math.Abs(axis[1]) <= AxisTolerance;

    /// <summary>Radius agreement between a face and the radius already measured from the same face — used to
    /// reject a candidate face that is present but not the hole being edited.</summary>
    public static bool RadiusAgrees(double faceRadiusMm, double expectedRadiusMm) =>
        Math.Abs(faceRadiusMm - expectedRadiusMm) <= RadiusToleranceMm;

    /// <summary>Side-surface area of a through cylindrical hole: <c>2πrh</c>. A second, independent check
    /// that a face is the hole wall and not another cylinder — radius alone cannot tell them apart.</summary>
    public static double LateralAreaMm2(double radiusMm, double heightMm) =>
        2d * Math.PI * radiusMm * heightMm;
}

public sealed record SketchDerivationVerdict(bool IsAllowed, string? ReasonCode, string? Reason)
{
    public static SketchDerivationVerdict Allowed() => new(true, null, null);

    public static SketchDerivationVerdict Refused(string code, string reason) => new(false, code, reason);
}

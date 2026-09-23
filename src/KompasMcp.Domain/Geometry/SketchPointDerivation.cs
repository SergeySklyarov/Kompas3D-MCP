using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Where a coordinate for <c>ksFindObj</c> can be derived from when the server has no memory of
/// drawing the sketch at all, and under exactly which conditions that derivation is allowed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> API5 has no sketch enumeration: <c>ksSketchDefinition</c> has no counter
/// and no walker, and <c>ksDocument2D</c> exposes <c>ksFindObj</c>/<c>ksDeleteObj</c> but no
/// <c>ksFirstObj</c>/<c>ksNextObj</c>. So the only way to point at an existing sketch primitive is
/// a coordinate that lies on it. For a sketch this server drew, the coordinate is remembered. For
/// one it did not — a reopened document, or a model built by hand — there is no memory, and the
/// product used to refuse <c>replace</c>/<c>delete_entities</c> outright.
/// </para>
/// <para>
/// <b>What probe G measured.</b> The coordinate can be read back out of the model itself: a
/// through hole leaves a cylindrical face, and that face yields centre, radius and axis. The point
/// <c>(cx + r; cy)</c> then lies on the sketch circle. This is not a guess — the probe checked it
/// against two control points that find nothing, and confirmed the edit by measuring the dependent
/// body (V 76858.4073464102 → 75476.1065788307 for R10 → R12 on a 100×80×10 plate).
/// </para>
/// <para>
/// <b>Where the measurement stops.</b> The derivation is proven for one configuration only: a
/// sketch on the base XY plane, a circle in the profile, and a through cut. Everything else in the
/// mapping — an inclined plane (where 3D→2D needs a transport that was never measured), a profile
/// of segments, arcs or rectangles (where a cylinder gives no point that provably lies on the
/// primitive) — must refuse rather than extrapolate. That boundary is the whole point of this
/// type: it turns "the number happened to work" into a stated precondition.
/// </para>
/// </remarks>
public static class SketchPointDerivation
{
    /// <summary>Unit-vector agreement, generous enough for a placement read back from COM.</summary>
    public const double AxisTolerance = 1e-6;

    /// <summary>A face is a hole wall rather than a boss when its axis runs along the sketch normal.</summary>
    public const double RadiusToleranceMm = 1e-6;

    /// <summary>
    /// Whether a model-derived point may be used at all for the sketch being edited.
    /// </summary>
    /// <param name="planeBase">
    /// The base plane the sketch sits on, when the server chose it. Null means the sketch was built
    /// on a referenced plane or one the server did not create, and the 3D→2D mapping for that case
    /// was not measured.
    /// </param>
    /// <param name="profileKinds">
    /// Kinds of the primitives the caller asks to write. The derivation yields a point that lies on
    /// a circle; a caller replacing the profile with segments would be handed a point that may lie
    /// on nothing.
    /// </param>
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

    /// <summary>
    /// Point on the sketch circle for a cylindrical face that is coaxial with the sketch normal.
    /// </summary>
    /// <param name="origin">
    /// Face placement origin in model coordinates. Its X and Y are the circle centre in sketch
    /// coordinates — valid only because the caller has already established that the plane is XY.
    /// </param>
    /// <param name="radiusMm">Face radius, which equals the sketch circle radius.</param>
    /// <returns>The two probe points at both ends of a diameter.</returns>
    public static double[][] PointsOnCircle(IReadOnlyList<double> origin, double radiusMm) =>
        new[]
        {
            new[] { origin[0] + radiusMm, origin[1] },
            new[] { origin[0] - radiusMm, origin[1] },
        };

    /// <summary>
    /// Axis agreement with the sketch normal. Measured on the probe with the axis taken from
    /// <c>GetPlacement().GetVector(2)</c>: for an XY sketch the cylinder axis must run along Z.
    /// </summary>
    public static bool AxisIsNormalToXyPlane(IReadOnlyList<double>? axis) =>
        axis is { Count: 3 }
        && Math.Abs(Math.Abs(axis[2]) - 1d) <= AxisTolerance
        && Math.Abs(axis[0]) <= AxisTolerance
        && Math.Abs(axis[1]) <= AxisTolerance;

    /// <summary>
    /// Radius agreement between a face and the radius already measured from the same face — used to
    /// reject a candidate face that is present but not the hole being edited.
    /// </summary>
    public static bool RadiusAgrees(double faceRadiusMm, double expectedRadiusMm) =>
        Math.Abs(faceRadiusMm - expectedRadiusMm) <= RadiusToleranceMm;

    /// <summary>
    /// Side-surface area of a through cylindrical hole: <c>2πrh</c>. Probe G used this as the second,
    /// independent check that a face really is the hole wall and not some other cylinder in the
    /// part — radius alone cannot tell them apart.
    /// </summary>
    public static double LateralAreaMm2(double radiusMm, double heightMm) =>
        2d * Math.PI * radiusMm * heightMm;
}

/// <summary>Why a model-derived sketch point is or is not allowed, with the reason stated in words.</summary>
public sealed record SketchDerivationVerdict(bool IsAllowed, string? ReasonCode, string? Reason)
{
    public static SketchDerivationVerdict Allowed() => new(true, null, null);

    public static SketchDerivationVerdict Refused(string code, string reason) => new(false, code, reason);
}

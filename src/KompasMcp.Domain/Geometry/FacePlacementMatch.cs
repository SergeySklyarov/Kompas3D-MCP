namespace KompasMcp.Domain.Geometry;

/// <summary>Whether the face an operation was ASKED about is the face it actually acted on.</summary>
/// <remarks>WHY. `kompas_shell` removes the faces it is given by REFERENCE, `kompas_hole` drills into the
/// face it is given by reference, and an angled auxiliary plane is tilted about the base line it is
/// given by reference. On the fillet and the chamfer the same class is closed by
/// <see cref="EdgeSurfaceMatch"/>; the geometry of a removed face, of a drilled axis and of a tilted
/// plane is different, so the criterion is stated separately here. INVARIANT: only numbers go in, so a
/// SUBSTITUTED reading exercises every branch.
/// History: docs/decisions/adapter-features.md#edge-placement-other-ops</remarks>
public static class FacePlacementMatch
{
    /// <summary>How far a point may sit from a plane and still count as lying in it, in mm. The faces
    /// and the plane are read from the kernel directly, so the band is a rounding band.</summary>
    public const double PlaneToleranceMm = 1e-4d;

    /// <summary>Angular band for "parallel", as the cosine's distance from 1.</summary>
    public const double DirectionTolerance = 1e-6d;

    /// <summary>How far a summed area may sit from the requested one, in mm².</summary>
    public const double AreaToleranceMm2 = 1e-6d;

    /// <summary>Angular band for the requested tilt, in degrees.</summary>
    public const double AngleToleranceDeg = 1e-6d;

    /// <summary>What a placement comparison concluded. <c>Unverified</c> is a NAMED gap, never a
    /// refusal: an unread value is not evidence that the operation landed elsewhere.</summary>
    public enum Verdict
    {
        Confirmed,
        Refused,
        Unverified,
    }

    /// <summary>One planar face reduced to what these rules need: a point on its plane, its normal
    /// (the SIGN is irrelevant, distances are taken in absolute value) and its area.</summary>
    public sealed record PlanarFace(IReadOnlyList<double> Point, IReadOnlyList<double> Normal, double AreaMm2);

    public readonly record struct Result(Verdict Verdict, string? Code, string Detail)
    {
        public bool IsRefusal => Verdict == Verdict.Refused;

        public static Result Confirmed(string detail) => new(Verdict.Confirmed, null, detail);

        public static Result Refused(string code, string detail) => new(Verdict.Refused, code, detail);

        public static Result Unverified(string detail) => new(Verdict.Unverified, null, detail);
    }

    /// <summary>Whether the face the shell was asked to REMOVE is really gone from the body.</summary>
    /// <remarks>A removed face leaves no face in its plane. The area guard keeps a COPLANAR SIBLING
    /// (a face the body had before the operation, in the same plane) from reading as "the requested
    /// face is still there": only a remainder at least as large as the requested face is that.
    /// <paramref name="facesAfter"/> is every planar face of the body AFTER the operation.</remarks>
    public static Result RemovedFaceGone(PlanarFace requested, IReadOnlyList<PlanarFace> facesAfter)
    {
        if (!Unit(requested.Normal, out var normal))
        {
            return Result.Unverified(
                "Нормаль запрошенной грани не прочитана: в какой плоскости её искать, неизвестно.");
        }

        if (double.IsNaN(requested.AreaMm2) || requested.AreaMm2 <= 0d)
        {
            return Result.Unverified(
                "Площадь запрошенной грани не прочитана: отличить «грани нет» от «остался кусок» нечем.");
        }

        var remaining = 0d;
        var coplanar = 0;
        foreach (var face in facesAfter)
        {
            if (!Unit(face.Normal, out var other) || Math.Abs(Dot(normal, other)) < 1d - DirectionTolerance)
            {
                continue;
            }

            if (Math.Abs(Dot(normal, Subtract(face.Point, requested.Point))) > PlaneToleranceMm)
            {
                continue;
            }

            coplanar++;
            remaining += double.IsNaN(face.AreaMm2) ? requested.AreaMm2 : face.AreaMm2;
        }

        if (remaining >= requested.AreaMm2 - AreaToleranceMm2)
        {
            return Result.Refused("shell_face_not_removed",
                "В плоскости запрошенной грани тело по-прежнему несёт " + coplanar
                + " грань(ей) суммарной площадью " + Num(remaining) + " мм² из запрошенных "
                + Num(requested.AreaMm2) + " мм²: снята НЕ запрошенная грань.");
        }

        if (remaining > AreaToleranceMm2)
        {
            return Result.Unverified(
                "В плоскости запрошенной грани осталась грань площадью " + Num(remaining) + " мм² из "
                + Num(requested.AreaMm2) + " мм²: это меньше запрошенной, и «снята запрошенная» от "
                + "«снята её часть» этим чтением не отличается.");
        }

        return Result.Confirmed(
            "В плоскости запрошенной грани граней не осталось: снята именно она.");
    }

    /// <summary>Whether the drilled axis lies ON the face the hole was asked to enter.</summary>
    /// <param name="axisExtentMm">The length of the cylindrical face along its axis, or <c>null</c> when
    /// the kernel did not report it. With it, the requested plane must CUT the axis segment — a hole
    /// that opens onto the opposite, parallel face has its whole cylinder on one side of the requested
    /// plane and is caught; without it the direction alone decides and the gap is named.</param>
    public static Result HoleAxisOnFace(
        PlanarFace requested,
        IReadOnlyList<double> axisOrigin,
        IReadOnlyList<double> axisDirection,
        double? axisExtentMm)
    {
        if (!Unit(requested.Normal, out var normal))
        {
            return Result.Unverified(
                "Нормаль запрошенной грани не прочитана: куда направлена ось, сверить не с чем.");
        }

        if (!Unit(axisDirection, out var axis))
        {
            return Result.Unverified("Направление оси отверстия не прочитано.");
        }

        if (axisOrigin.Count < 3)
        {
            return Result.Unverified("Точка на оси отверстия не прочитана.");
        }

        var along = Math.Abs(Dot(axis, normal));
        if (along < 1d - DirectionTolerance)
        {
            return Result.Refused("hole_on_other_face",
                "Ось отверстия не перпендикулярна запрошенной грани (косинус " + Num(along)
                + " вместо 1): отверстие построено НЕ по запрошенной грани.");
        }

        if (axisExtentMm is not double extent || !(extent > 0d))
        {
            return Result.Unverified(
                "Длина цилиндрической грани отверстия не прочитана: ось перпендикулярна запрошенной "
                + "грани, но «вход именно на ней» этим чтением не подтверждается.");
        }

        var atOrigin = Dot(normal, Subtract(axisOrigin, requested.Point));
        var atFar = Dot(normal, Subtract(Add(axisOrigin, Scale(axis, extent)), requested.Point));
        var low = Math.Min(atOrigin, atFar);
        var high = Math.Max(atOrigin, atFar);
        if (low > PlaneToleranceMm || high < -PlaneToleranceMm)
        {
            return Result.Refused("hole_on_other_face",
                "Запрошенная грань не пересекает цилиндр отверстия: расстояния от её плоскости до "
                + "концов оси " + Num(atOrigin) + " и " + Num(atFar) + " мм одного знака. Отверстие "
                + "построено НЕ по запрошенной грани.");
        }

        return Result.Confirmed(
            "Ось отверстия перпендикулярна запрошенной грани и её цилиндр пересекает её плоскость.");
    }

    /// <summary>Whether an angled auxiliary plane was built about the REQUESTED base line, at the
    /// requested angle.</summary>
    /// <param name="requestedLineName">Name of the base line the caller named, or null/empty when the
    /// caller named none and the angle is taken from the base plane.</param>
    public static Result AngledPlaneOnBaseLine(
        double requestedAngleDeg,
        double? readAngleDeg,
        string? requestedLineName,
        string? readLineName)
    {
        if (readAngleDeg is not double readAngle)
        {
            return Result.Unverified("Угол построенной плоскости не прочитан из модели.");
        }

        if (Math.Abs(readAngle - requestedAngleDeg) > AngleToleranceDeg)
        {
            return Result.Refused("plane_on_other_angle",
                "Угол построенной плоскости " + Num(readAngle) + "° против запрошенных "
                + Num(requestedAngleDeg) + "°: плоскость наклонена не так, как просили.");
        }

        if (string.IsNullOrEmpty(requestedLineName))
        {
            return Result.Confirmed(
                "Базовая прямая не задавалась: угол отсчитывается от базовой плоскости и прочитан "
                + "как " + Num(readAngle) + "°.");
        }

        if (string.IsNullOrEmpty(readLineName))
        {
            return Result.Unverified(
                "Базовая прямая (ось наклона) построенной плоскости не прочитана: сверять имя не с чем.");
        }

        if (!string.Equals(readLineName, requestedLineName, StringComparison.Ordinal))
        {
            return Result.Refused("plane_on_other_base_line",
                "Плоскость наклонена вокруг прямой «" + readLineName + "», а запрошена «"
                + requestedLineName + "»: взята не та базовая прямая.");
        }

        return Result.Confirmed(
            "Плоскость наклонена вокруг запрошенной прямой «" + readLineName + "» на запрошенные "
            + Num(readAngle) + "°.");
    }

    private static string Num(double value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static double[] Subtract(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        new[] { left[0] - right[0], left[1] - right[1], left[2] - right[2] };

    private static double[] Add(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        new[] { left[0] + right[0], left[1] + right[1], left[2] + right[2] };

    private static double[] Scale(IReadOnlyList<double> vector, double factor) =>
        new[] { vector[0] * factor, vector[1] * factor, vector[2] * factor };

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        left[0] * right[0] + left[1] * right[1] + left[2] * right[2];

    /// <summary>Normalises; false when the vector is (near) zero — an absent direction is NOT a
    /// direction of zero.</summary>
    private static bool Unit(IReadOnlyList<double> vector, out double[] unit)
    {
        unit = new[] { 0d, 0d, 0d };
        if (vector is null || vector.Count < 3)
        {
            return false;
        }

        var length = Math.Sqrt(Dot(vector, vector));
        if (!(length > 1e-12) || double.IsNaN(length) || double.IsInfinity(length))
        {
            return false;
        }

        unit = new[] { vector[0] / length, vector[1] / length, vector[2] / length };
        return true;
    }
}

namespace KompasMcp.Domain.Geometry;

/// <summary>Whether the surface an edge operation produced really adjoins the edge it was ASKED for.</summary>
/// <remarks>WHY THE VOLUME IS NOT THE CRITERION. A fillet or a chamfer is addressed by an edge REFERENCE,
/// and the kernel may re-resolve it to another edge of the same topology; on a body whose corners are
/// equivalent the volume then moves by the same amount in the opposite direction, so it cannot tell "built
/// where asked" from "built elsewhere". The PLACEMENT of the new surface can: a constant-radius fillet on a
/// straight edge is a cylinder one radius from BOTH faces that meet at it, and a chamfer cuts those two
/// faces at the two requested legs. INVARIANT: only numbers go in, so a SUBSTITUTED reading exercises every
/// branch of this rule. History: docs/decisions/adapter-features.md#fillet-on-other-edge</remarks>
public static class EdgeSurfaceMatch
{
    /// <summary>How far a measured placement may sit from its analytic value, in mm. The placement is a
    /// number the kernel reports directly (not a difference of two measurements), so the band is a
    /// rounding band, not a noise floor.</summary>
    public const double PlacementToleranceMm = 1e-4d;

    /// <summary>Angular band for "parallel" and "perpendicular", as the cosine's distance from 0 or 1.</summary>
    public const double DirectionTolerance = 1e-6d;

    /// <summary>Where a produced surface ended up relative to the requested edge.</summary>
    /// <param name="Adjoins">True only when every check ran and passed.</param>
    /// <param name="Code">Null when <paramref name="Adjoins"/>; otherwise the reason, in the shape the
    /// refusal carries: <c>surface_missing</c>, <c>not_parallel</c>, <c>wrong_distance</c>,
    /// <c>no_adjacent_faces</c>, <c>not_read</c>.</param>
    public readonly record struct Result(
        bool Adjoins,
        string? Code,
        double? MeasuredMm,
        double? ExpectedMm,
        string Detail)
    {
        public static Result Yes(double measured, double expected, string detail) =>
            new(true, null, measured, expected, detail);

        public static Result No(string code, double? measured, double? expected, string detail) =>
            new(false, code, measured, expected, detail);
    }

    /// <summary>Whether a cylindrical surface of radius <paramref name="radiusMm"/> adjoins the straight
    /// edge <c>[edgeStart, edgeEnd]</c>: the axis is parallel to the edge and at distance <c>radiusMm</c>
    /// from BOTH planes that meet at it.</summary>
    /// <param name="adjacentPlanePoints">One point on each of the two planes that contain the edge line.</param>
    /// <param name="adjacentPlaneNormals">The matching unit normals. The SIGN is irrelevant: the distance to
    /// a plane is taken in absolute value, so an inward or outward normal answers the same.</param>
    public static Result FilletAgainstEdge(
        IReadOnlyList<double> edgeStart,
        IReadOnlyList<double> edgeEnd,
        IReadOnlyList<double> axisPoint,
        IReadOnlyList<double> axisDirection,
        double radiusMm,
        IReadOnlyList<IReadOnlyList<double>> adjacentPlanePoints,
        IReadOnlyList<IReadOnlyList<double>> adjacentPlaneNormals)
    {
        if (!Direction(edgeStart, edgeEnd, out var edgeDirection))
        {
            return Result.No("not_read", null, null, "Направление запрошенного ребра не определено: две его вершины совпали.");
        }

        if (!Unit(axisDirection, out var axisUnit))
        {
            return Result.No("not_read", null, null, "Направление оси поверхности не прочитано.");
        }

        if (Math.Abs(Dot(edgeDirection, axisUnit)) < 1d - DirectionTolerance)
        {
            return Result.No("not_parallel", null, null,
                "Ось поверхности не параллельна запрошенному ребру: скругление построено не по нему.");
        }

        if (adjacentPlanePoints.Count != 2 || adjacentPlaneNormals.Count != 2)
        {
            return Result.No("no_adjacent_faces", null, null,
                "Граней, содержащих запрошенное ребро, найдено " + adjacentPlanePoints.Count
                + " вместо двух: прилегание поверхности проверить нечем.");
        }

        for (var i = 0; i < 2; i++)
        {
            if (!Unit(adjacentPlaneNormals[i], out var planeNormal))
            {
                return Result.No("not_read", null, null, "Нормаль смежной грани " + i + " не прочитана.");
            }

            var distance = Math.Abs(Dot(planeNormal, Subtract(axisPoint, adjacentPlanePoints[i])));
            if (Math.Abs(distance - radiusMm) > PlacementToleranceMm)
            {
                return Result.No("wrong_distance", distance, radiusMm,
                    "Ось поверхности отстоит от смежной грани " + i + " на " + distance.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм вместо радиуса " + radiusMm.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                    + " мм: скругление прилегает не к запрошенному ребру.");
            }
        }

        return Result.Yes(radiusMm, radiusMm, "Ось цилиндра параллельна ребру и отстоит от обеих смежных граней на радиус.");
    }

    /// <summary>Whether a planar face adjoins the straight edge <c>[edgeStart, edgeEnd]</c> as a chamfer of
    /// the two given legs: its plane is parallel to the edge, and its intersections with the two adjacent
    /// planes are lines parallel to the edge at distances <c>distance1Mm</c> and <c>distance2Mm</c> from it,
    /// in either order.</summary>
    public static Result ChamferAgainstEdge(
        IReadOnlyList<double> edgeStart,
        IReadOnlyList<double> edgeEnd,
        IReadOnlyList<double> facePoint,
        IReadOnlyList<double> faceNormal,
        double distance1Mm,
        double distance2Mm,
        IReadOnlyList<IReadOnlyList<double>> adjacentPlanePoints,
        IReadOnlyList<IReadOnlyList<double>> adjacentPlaneNormals)
    {
        if (!Direction(edgeStart, edgeEnd, out var edgeDirection))
        {
            return Result.No("not_read", null, null, "Направление запрошенного ребра не определено: две его вершины совпали.");
        }

        if (!Unit(faceNormal, out var faceUnit))
        {
            return Result.No("not_read", null, null, "Нормаль новой грани не прочитана.");
        }

        if (Math.Abs(Dot(edgeDirection, faceUnit)) > DirectionTolerance)
        {
            return Result.No("not_parallel", null, null,
                "Плоскость новой грани не параллельна запрошенному ребру: фаска построена не по нему.");
        }

        if (adjacentPlanePoints.Count != 2 || adjacentPlaneNormals.Count != 2)
        {
            return Result.No("no_adjacent_faces", null, null,
                "Граней, содержащих запрошенное ребро, найдено " + adjacentPlanePoints.Count
                + " вместо двух: прилегание фаски проверить нечем.");
        }

        var expected = new[] { distance1Mm, distance2Mm };
        var measured = new double[2];
        for (var i = 0; i < 2; i++)
        {
            if (!Unit(adjacentPlaneNormals[i], out var planeNormal))
            {
                return Result.No("not_read", null, null, "Нормаль смежной грани " + i + " не прочитана.");
            }

            // The line where the chamfer plane meets the adjacent face, then its distance to the edge.
            var direction = Cross(faceUnit, planeNormal);
            if (!Unit(direction, out var intersectionUnit))
            {
                return Result.No("not_parallel", null, null,
                    "Плоскость новой грани параллельна смежной грани " + i + ": они не пересекаются по линии, "
                    + "и катет фаски измерить нечем.");
            }

            if (Math.Abs(Dot(edgeDirection, intersectionUnit)) < 1d - DirectionTolerance)
            {
                return Result.No("not_parallel", null, null,
                    "Линия пересечения новой грани со смежной гранью " + i + " не параллельна ребру: "
                    + "фаска задана не вдоль него.");
            }

            var onIntersection = PointOnPlaneIntersection(faceUnit, facePoint, planeNormal, adjacentPlanePoints[i], direction);
            measured[i] = DistanceToLine(onIntersection, edgeStart, edgeDirection);
        }

        var straight = Math.Abs(measured[0] - expected[0]) <= PlacementToleranceMm
                       && Math.Abs(measured[1] - expected[1]) <= PlacementToleranceMm;
        var swapped = Math.Abs(measured[0] - expected[1]) <= PlacementToleranceMm
                      && Math.Abs(measured[1] - expected[0]) <= PlacementToleranceMm;
        if (!straight && !swapped)
        {
            var format = System.Globalization.CultureInfo.InvariantCulture;
            return Result.No("wrong_distance", measured[0], expected[0],
                "Новая грань отсекает смежные грани на " + measured[0].ToString("0.######", format)
                + " и " + measured[1].ToString("0.######", format) + " мм от ребра, а катеты заданы "
                + expected[0].ToString("0.######", format) + " и " + expected[1].ToString("0.######", format)
                + " мм: фаска прилегает не к запрошенному ребру.");
        }

        return Result.Yes(measured[0], expected[0],
            "Новая грань параллельна ребру и отсекает обе смежные грани на заданные катеты.");
    }

    /// <summary>Unit direction of the segment, or false when its ends coincide.</summary>
    public static bool Direction(IReadOnlyList<double> start, IReadOnlyList<double> end, out double[] unit)
    {
        var span = Subtract(end, start);
        return Unit(span, out unit);
    }

    /// <summary>Normalises in place; false when the vector is (near) zero — an absent direction is NOT a
    /// direction of zero, and the caller must not be handed one.</summary>
    public static bool Unit(IReadOnlyList<double> vector, out double[] unit)
    {
        unit = new[] { 0d, 0d, 0d };
        if (vector.Count < 3)
        {
            return false;
        }

        var length = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
        if (!(length > 1e-12) || double.IsNaN(length) || double.IsInfinity(length))
        {
            return false;
        }

        unit = new[] { vector[0] / length, vector[1] / length, vector[2] / length };
        return true;
    }

    /// <summary>A point on the line where two planes meet. The line's direction must already be known and
    /// non-degenerate; the formula is the standard symmetric one and is exact for non-parallel planes.</summary>
    private static double[] PointOnPlaneIntersection(
        IReadOnlyList<double> firstNormal,
        IReadOnlyList<double> firstPoint,
        IReadOnlyList<double> secondNormal,
        IReadOnlyList<double> secondPoint,
        IReadOnlyList<double> lineDirection)
    {
        var firstOffset = Dot(firstNormal, firstPoint);
        var secondOffset = Dot(secondNormal, secondPoint);
        var squared = Dot(lineDirection, lineDirection);
        var firstCross = Cross(secondNormal, lineDirection);
        var secondCross = Cross(lineDirection, firstNormal);
        return new[]
        {
            (firstOffset * firstCross[0] + secondOffset * secondCross[0]) / squared,
            (firstOffset * firstCross[1] + secondOffset * secondCross[1]) / squared,
            (firstOffset * firstCross[2] + secondOffset * secondCross[2]) / squared,
        };
    }

    /// <summary>Perpendicular distance from a point to a line given by a point and a unit direction.</summary>
    public static double DistanceToLine(
        IReadOnlyList<double> point, IReadOnlyList<double> linePoint, IReadOnlyList<double> lineDirection)
    {
        var offset = Subtract(point, linePoint);
        var along = Dot(offset, lineDirection);
        var perpendicular = new[]
        {
            offset[0] - along * lineDirection[0],
            offset[1] - along * lineDirection[1],
            offset[2] - along * lineDirection[2],
        };
        return Math.Sqrt(Dot(perpendicular, perpendicular));
    }

    private static double[] Subtract(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        new[] { left[0] - right[0], left[1] - right[1], left[2] - right[2] };

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        left[0] * right[0] + left[1] * right[1] + left[2] * right[2];

    private static double[] Cross(IReadOnlyList<double> left, IReadOnlyList<double> right) => new[]
    {
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    };
}

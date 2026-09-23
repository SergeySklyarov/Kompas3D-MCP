using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Analytic area of the <b>region</b> a sketch profile encloses, used as the expected value for an
/// extrusion (spec 1.11: volume must be compared against an analytic expectation, a silent PASS is
/// forbidden).
/// </summary>
/// <remarks>
/// <para>
/// The region, not the sum of the primitives: a contour lying inside another one is a hole in it, so
/// a disk with a concentric circle is an annulus (π(R²−r²)), not π(R²+r²). That distinction is
/// measured, not assumed — on КОМПАС-3D v24 a sketch with circles R=10 and r=5 extruded 10 mm deep
/// gives <c>2356.1944901923607</c> mm³ against π·(100−25)·10 = 2356.194490192345 (6.8e-15 relative),
/// and a 100×80 rectangle with an r=10 circle inside gives <c>76858.4073464102</c> mm³ against
/// (8000−100π)·10 = 76858.40734641021. Nesting is resolved by the even-odd rule — every contour
/// strictly inside another flips the sign of its area — and the depth-2 case is measured too:
/// circles R=10, r=5, r=2 extruded 10 mm give <c>2481.8581963359516</c> mm³ = π·(100−25+4)·10.
/// </para>
/// <para>
/// Only shapes whose region is exactly computable from the primitives the caller sent are answered:
/// circles, rectangles and closed polylines. A line or an arc on its own, a self-intersecting
/// polyline, a degenerate (non-positive area) contour, a profile drawn outside this session, or a
/// pair of contours that <b>touch or partially overlap</b> return null. The extrusion then reports
/// "not computable" and an unverified aspect instead of inventing a target — which is the difference
/// between "we checked" and "the numbers happened to look fine".
/// </para>
/// <para>
/// Partial overlap is refused on purpose even though the union of two overlapping circles <i>is</i>
/// exactly computable and was measured (R=10 with centres 15 mm apart, 10 mm deep:
/// <c>5829.873553201979</c> mm³ against the lens formula's 5829.873553201976). One measured special
/// case does not make the general case analytic, and an expectation special-cased until it matches is
/// exactly how an instrument starts describing itself.
/// </para>
/// <para>
/// Touching contours are refused as well, and that is deliberate: at tangency the region depends on
/// how the kernel resolves a shared point or edge, which is not measured, so neither "sum" nor
/// "difference" is a statement this code is entitled to make.
/// </para>
/// </remarks>
public static class ProfileArea
{
    /// <summary>Length tolerance of the geometric predicates, in mm.</summary>
    private const double EpsMm = 1e-9;

    /// <summary>
    /// Area of the enclosed region in mm², or null when the region is not analytically determined by
    /// the primitives.
    /// </summary>
    public static double? Of(IReadOnlyList<SketchEntityDto> entities)
    {
        if (entities.Count == 0)
        {
            return null;
        }

        var contours = new List<Contour>(entities.Count);
        foreach (var entity in entities)
        {
            if (ContourOf(entity) is not Contour contour)
            {
                return null;
            }

            contours.Add(contour);
        }

        // Depth = how many contours strictly contain this one. Even-odd: a contour at an odd depth is
        // a hole in the one around it, at an even depth it is material again.
        var depth = new int[contours.Count];
        for (var i = 0; i < contours.Count; i++)
        {
            for (var j = i + 1; j < contours.Count; j++)
            {
                switch (RelationOf(contours[i], contours[j]))
                {
                    case Relation.FirstInsideSecond:
                        depth[i]++;
                        break;

                    case Relation.SecondInsideFirst:
                        depth[j]++;
                        break;

                    case Relation.Disjoint:
                        break;

                    default:
                        // Touching or overlapping: the region is not this formula's business.
                        return null;
                }
            }
        }

        var total = 0d;
        for (var i = 0; i < contours.Count; i++)
        {
            total += depth[i] % 2 == 0 ? contours[i].AreaMm2 : -contours[i].AreaMm2;
        }

        // A non-positive total means the contours cancelled out (coincident or nested-equal
        // figures), which is a degenerate profile rather than a measured region of zero.
        return double.IsFinite(total) && total > 0d ? total : null;
    }

    /// <summary>Area of a closed polygon, absolute value of the shoelace sum, in mm².</summary>
    public static double Shoelace(IReadOnlyList<IReadOnlyList<double>> points)
    {
        double sum = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            sum += (current[0] * next[1]) - (next[0] * current[1]);
        }

        return Math.Abs(sum) / 2d;
    }

    /// <summary>
    /// Tolerance for comparing a measured volume with the analytic expectation: a relative part
    /// plus an absolute floor, so a tiny model is not failed by floating-point noise and a huge
    /// one is not passed by an oversized absolute slack.
    /// </summary>
    public static double Tolerance(double expectedMm3) =>
        Math.Max(1e-3, 1e-9 * Math.Abs(expectedMm3));

    public static bool Matches(double? expectedMm3, double? measuredMm3)
    {
        if (expectedMm3 is not double expected || measuredMm3 is not double measured)
        {
            return false;
        }

        return Math.Abs(measured - expected) <= Tolerance(expected);
    }

    /// <summary>How two contours lie relative to each other.</summary>
    private enum Relation
    {
        /// <summary>Neither contains the other and they do not meet.</summary>
        Disjoint,

        FirstInsideSecond,
        SecondInsideFirst,

        /// <summary>Touching, overlapping, or otherwise not decidable by this formula.</summary>
        Ambiguous,
    }

    /// <summary>
    /// A closed contour with the area it encloses, plus its bounding box (a cheap and sound
    /// prefilter: disjoint boxes mean disjoint contours).
    /// </summary>
    private abstract record Contour(double AreaMm2, double MinU, double MinV, double MaxU, double MaxV)
    {
        public bool BoxesMeet(Contour other) =>
            MinU <= other.MaxU + EpsMm
            && other.MinU <= MaxU + EpsMm
            && MinV <= other.MaxV + EpsMm
            && other.MinV <= MaxV + EpsMm;
    }

    private sealed record Disk(double Cx, double Cy, double R)
        : Contour(Math.PI * R * R, Cx - R, Cy - R, Cx + R, Cy + R);

    private sealed record Ring(IReadOnlyList<double[]> Points, double Area)
        : Contour(
            Area,
            Points.Min(p => p[0]),
            Points.Min(p => p[1]),
            Points.Max(p => p[0]),
            Points.Max(p => p[1]));

    /// <summary>
    /// One primitive as a contour, or null when its region is not determined by the primitive alone.
    /// </summary>
    private static Contour? ContourOf(SketchEntityDto entity)
    {
        switch (entity.Kind)
        {
            case SketchEntityKind.Circle:
                if (entity.CenterMm is not { Count: >= 2 } center
                    || entity.RadiusMm is not double radius
                    || !(radius > 0d))
                {
                    return null;
                }

                return new Disk(center[0], center[1], radius);

            case SketchEntityKind.Rectangle:
                if (entity.StartMm is not { Count: >= 2 } corner
                    || entity.WidthMm is not double width
                    || entity.HeightMm is not double height
                    || !(width > 0d)
                    || !(height > 0d))
                {
                    return null;
                }

                return PolygonOf(new[]
                {
                    new[] { corner[0], corner[1] },
                    new[] { corner[0] + width, corner[1] },
                    new[] { corner[0] + width, corner[1] + height },
                    new[] { corner[0], corner[1] + height },
                });

            case SketchEntityKind.Polyline:
                if (entity.Closed != true || entity.PointsMm is not { Count: >= 3 } points)
                {
                    return null;
                }

                if (points.Any(p => p.Count < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
                {
                    return null;
                }

                return PolygonOf(points.Select(p => new[] { p[0], p[1] }).ToArray());

            // A line or an arc on its own does not determine an area without the solver, so no
            // analytic expectation is claimed.
            default:
                return null;
        }
    }

    /// <summary>
    /// A closed polygon as a contour, or null when it is degenerate or self-intersecting. A
    /// self-intersecting outline has a shoelace figure but no single enclosed region, so answering
    /// with that figure would be an invented expectation.
    /// </summary>
    private static Ring? PolygonOf(IReadOnlyList<double[]> points)
    {
        if (SelfIntersects(points))
        {
            return null;
        }

        var area = Shoelace(points);
        return area > 0d ? new Ring(points, area) : null;
    }

    private static Relation RelationOf(Contour first, Contour second)
    {
        if (!first.BoxesMeet(second))
        {
            return Relation.Disjoint;
        }

        return (first, second) switch
        {
            (Disk a, Disk b) => DiskToDisk(a, b),
            (Disk a, Ring b) => DiskToRing(a, b),
            (Ring a, Disk b) => Flip(DiskToRing(b, a)),
            (Ring a, Ring b) => RingToRing(a, b),
            _ => Relation.Ambiguous,
        };
    }

    private static Relation Flip(Relation relation) => relation switch
    {
        Relation.FirstInsideSecond => Relation.SecondInsideFirst,
        Relation.SecondInsideFirst => Relation.FirstInsideSecond,
        _ => relation,
    };

    /// <summary>Two circles: nested, disjoint, or neither.</summary>
    private static Relation DiskToDisk(Disk first, Disk second)
    {
        var centres = Distance(first.Cx, first.Cy, second.Cx, second.Cy);
        var smaller = Math.Min(first.R, second.R);
        var larger = Math.Max(first.R, second.R);
        var reach = centres + smaller;

        if (reach < larger - EpsMm)
        {
            return first.R < second.R ? Relation.FirstInsideSecond : Relation.SecondInsideFirst;
        }

        if (reach <= larger + EpsMm)
        {
            // Internal tangency or coincident circles: the region depends on how the kernel resolves
            // the shared boundary, which is not measured.
            return Relation.Ambiguous;
        }

        return centres > first.R + second.R + EpsMm ? Relation.Disjoint : Relation.Ambiguous;
    }

    /// <summary>Circle against polygon: which contains which, or neither.</summary>
    private static Relation DiskToRing(Disk disk, Ring ring)
    {
        var distances = ring.Points.Select(p => Distance(disk.Cx, disk.Cy, p[0], p[1])).ToArray();

        // A disk is convex, so a segment whose ends are inside it never leaves it: all vertices in
        // means the whole polygon is in.
        if (distances.All(d => d < disk.R - EpsMm))
        {
            return Relation.SecondInsideFirst;
        }

        var edgeGap = MinEdgeDistance(disk.Cx, disk.Cy, ring.Points);
        var centreInside = PointInPolygon(disk.Cx, disk.Cy, ring.Points);

        if (centreInside && edgeGap > disk.R + EpsMm)
        {
            return Relation.FirstInsideSecond;
        }

        if (!centreInside && edgeGap > disk.R + EpsMm && distances.All(d => d > disk.R + EpsMm))
        {
            return Relation.Disjoint;
        }

        return Relation.Ambiguous;
    }

    /// <summary>Two polygons: nested, disjoint, or neither.</summary>
    private static Relation RingToRing(Ring first, Ring second)
    {
        if (EdgesIntersect(first.Points, second.Points))
        {
            return Relation.Ambiguous;
        }

        if (first.Points.All(p => PointInPolygon(p[0], p[1], second.Points)))
        {
            return Relation.FirstInsideSecond;
        }

        if (second.Points.All(p => PointInPolygon(p[0], p[1], first.Points)))
        {
            return Relation.SecondInsideFirst;
        }

        var anyInside = first.Points.Any(p => PointInPolygon(p[0], p[1], second.Points))
            || second.Points.Any(p => PointInPolygon(p[0], p[1], first.Points));

        return anyInside ? Relation.Ambiguous : Relation.Disjoint;
    }

    private static double Distance(double ax, double ay, double bx, double by) =>
        Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    /// <summary>Shortest distance from a point to the polygon's boundary (0 when it lies on it).</summary>
    private static double MinEdgeDistance(double px, double py, IReadOnlyList<double[]> points)
    {
        var best = double.PositiveInfinity;
        for (var i = 0; i < points.Count; i++)
        {
            best = Math.Min(best, PointToSegment(px, py, points[i], points[(i + 1) % points.Count]));
        }

        return best;
    }

    private static double PointToSegment(double px, double py, double[] a, double[] b)
    {
        var dx = b[0] - a[0];
        var dy = b[1] - a[1];
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 0d)
        {
            return Distance(px, py, a[0], a[1]);
        }

        var t = Math.Clamp((((px - a[0]) * dx) + ((py - a[1]) * dy)) / lengthSquared, 0d, 1d);
        return Distance(px, py, a[0] + (t * dx), a[1] + (t * dy));
    }

    /// <summary>
    /// Even-odd point-in-polygon by ray casting. Points on the boundary count as inside: the callers
    /// have already refused touching contours, so a boundary point here is a contradiction to
    /// resolve conservatively rather than a case to decide.
    /// </summary>
    private static bool PointInPolygon(double px, double py, IReadOnlyList<double[]> points)
    {
        var inside = false;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (PointToSegment(px, py, a, b) <= EpsMm)
            {
                return true;
            }

            var crosses = (a[1] > py) != (b[1] > py);
            if (crosses && px < (a[0] + (((b[0] - a[0]) * (py - a[1])) / (b[1] - a[1]))))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>True when any edge of one polygon meets any edge of the other.</summary>
    private static bool EdgesIntersect(IReadOnlyList<double[]> first, IReadOnlyList<double[]> second)
    {
        for (var i = 0; i < first.Count; i++)
        {
            var a1 = first[i];
            var a2 = first[(i + 1) % first.Count];
            for (var j = 0; j < second.Count; j++)
            {
                if (SegmentsMeet(a1, a2, second[j], second[(j + 1) % second.Count]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when a closed polygon crosses or touches itself.</summary>
    private static bool SelfIntersects(IReadOnlyList<double[]> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var a1 = points[i];
            var a2 = points[(i + 1) % points.Count];
            for (var j = i + 1; j < points.Count; j++)
            {
                // Neighbouring edges share a vertex by construction, and so do the last and the first.
                var adjacent = j == i + 1 || (i == 0 && j == points.Count - 1);
                if (adjacent)
                {
                    continue;
                }

                if (SegmentsMeet(a1, a2, points[j], points[(j + 1) % points.Count]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether two segments cross or touch. Touching counts as meeting on purpose: a shared point or
    /// a shared edge makes the enclosed region depend on the kernel's resolution, which this formula
    /// is not entitled to predict.
    /// </summary>
    private static bool SegmentsMeet(double[] p1, double[] p2, double[] q1, double[] q2)
    {
        // The cross products carry mm², so the tolerance has to follow the size of the figure.
        var scale = Math.Max(
            Math.Max(Math.Abs(p1[0]), Math.Abs(p1[1])),
            Math.Max(
                Math.Max(Math.Abs(p2[0]), Math.Abs(p2[1])),
                Math.Max(
                    Math.Max(Math.Abs(q1[0]), Math.Abs(q1[1])),
                    Math.Max(Math.Abs(q2[0]), Math.Abs(q2[1])))));
        var eps = EpsMm * (1d + scale);

        var d1 = Cross(q1, q2, p1);
        var d2 = Cross(q1, q2, p2);
        var d3 = Cross(p1, p2, q1);
        var d4 = Cross(p1, p2, q2);

        var straddlesFirst = (d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps);
        var straddlesSecond = (d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps);
        if (straddlesFirst && straddlesSecond)
        {
            return true;
        }

        return (Math.Abs(d1) <= eps && OnSegment(q1, q2, p1, eps))
            || (Math.Abs(d2) <= eps && OnSegment(q1, q2, p2, eps))
            || (Math.Abs(d3) <= eps && OnSegment(p1, p2, q1, eps))
            || (Math.Abs(d4) <= eps && OnSegment(p1, p2, q2, eps));
    }

    /// <summary>Cross product of (b − a) × (p − a); its sign says which side of ab the point is on.</summary>
    private static double Cross(double[] a, double[] b, double[] p) =>
        ((b[0] - a[0]) * (p[1] - a[1])) - ((b[1] - a[1]) * (p[0] - a[0]));

    /// <summary>Whether a point known to be collinear with ab lies within the segment.</summary>
    private static bool OnSegment(double[] a, double[] b, double[] p, double eps) =>
        p[0] >= Math.Min(a[0], b[0]) - eps
        && p[0] <= Math.Max(a[0], b[0]) + eps
        && p[1] >= Math.Min(a[1], b[1]) - eps
        && p[1] <= Math.Max(a[1], b[1]) + eps;
}

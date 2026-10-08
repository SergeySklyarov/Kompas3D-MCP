using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>Analytic area of the <b>region</b> a sketch profile encloses, the expected value for an
/// extrusion (spec 1.11: a silent PASS is forbidden — volume must be compared against an expectation).</summary>
/// <remarks>INVARIANT: the region, not the sum of the primitives — a contour inside another is a hole, so a
/// disk with a concentric circle is an annulus (π(R²−r²)), not π(R²+r²). Nesting is resolved by the
/// even-odd rule: every contour strictly inside another flips the sign of its area. A contour is a closed
/// primitive or a chain welded from lines, arcs and open polylines; a chain's area comes from Green's
/// theorem, an arc adding its circular segment with the sign of traversal.
/// History: docs/decisions/geometry.md#profile-area</remarks>
public static class ProfileArea
{
    /// <summary>Length tolerance of the geometric predicates, in mm.</summary>
    private const double EpsMm = 1e-9;

    /// <summary>How close two primitive ends must be to count as one vertex, in mm. One nanometre is far
    /// below any modelling tolerance while still swallowing the floating-point residue of a chain built by
    /// converting analytic arcs: a real client contour closes to 2·10⁻¹⁴ mm, and the smallest genuine gap
    /// between two distinct vertices is orders of magnitude larger.</summary>
    private const double WeldMm = 1e-6;

    /// <summary>Area of the enclosed region in mm², or null when the region is not analytically determined
    /// by the primitives.</summary>
    public static double? Of(IReadOnlyList<SketchEntityDto> entities) => Compute(entities).AreaMm2;

    /// <summary>The area together with the reason it could not be computed, so the caller can name the gap
    /// in <c>unverified_aspects</c> instead of reporting a bare "not computable".</summary>
    public static ProfileAreaOutcome Compute(IReadOnlyList<SketchEntityDto> entities)
    {
        if (entities.Count == 0)
        {
            return new ProfileAreaOutcome(null, "профиль пуст — примитивов нет");
        }

        var contours = new List<Contour>(entities.Count);
        var edges = new List<Edge>();
        var pool = new VertexPool();
        foreach (var entity in entities)
        {
            switch (entity.Kind)
            {
                case SketchEntityKind.Circle:
                case SketchEntityKind.Rectangle:
                    if (ContourOf(entity) is not Contour closed)
                    {
                        return new ProfileAreaOutcome(null,
                            $"примитив {KindName(entity)} не разобран: не хватает полей для площади");
                    }

                    contours.Add(closed);
                    break;

                case SketchEntityKind.Polyline when entity.Closed == true:
                    if (ContourOf(entity) is not Contour polygon)
                    {
                        return new ProfileAreaOutcome(null,
                            "замкнутая полилиния не разобрана: нужно не меньше трёх вершин без самопересечения");
                    }

                    contours.Add(polygon);
                    break;

                case SketchEntityKind.Line:
                case SketchEntityKind.Arc:
                case SketchEntityKind.Polyline:
                    if (!AppendEdges(entity, edges, pool, out var edgeReason))
                    {
                        return new ProfileAreaOutcome(null, edgeReason);
                    }

                    break;

                case SketchEntityKind.Spline:
                    // INVARIANT: the server does NOT approximate a spline into segments to obtain an
                    // area — that would be the server's own geometry, not the model's, and the client
                    // would be told a figure nobody built. The extrusion is verified by MEASURING its
                    // volume instead, and the gap is named here.
                    // History: docs/decisions/geometry.md#spline-area
                    return new ProfileAreaOutcome(null,
                        "площадь профиля со сплайном на стороне сервера не считается: сплайн не "
                        + "разбирается на отрезки и дуги. Геометрию подтверждает измерение объёма "
                        + "выдавливания, а не аналитическая площадь.");

                default:
                    return new ProfileAreaOutcome(null, $"примитив {KindName(entity)} не даёт аналитики");
            }
        }

        if (edges.Count > 0 && !BuildChains(edges, contours, out var chainReason))
        {
            return new ProfileAreaOutcome(null, chainReason);
        }

        if (contours.Count == 0)
        {
            return new ProfileAreaOutcome(null, "замкнутый контур не собран из примитивов");
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
                        return new ProfileAreaOutcome(null,
                            "отношение контуров не определяется надёжно: они касаются или пересекаются");
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
        return double.IsFinite(total) && total > 0d
            ? new ProfileAreaOutcome(total, null)
            : new ProfileAreaOutcome(null, "контуры взаимно уничтожились: площадь региона не положительна");
    }

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

    /// <summary>Tolerance for comparing a measured volume with the analytic expectation: a relative part
    /// plus an absolute floor, so a tiny model is not failed by noise and a huge one is not passed by an
    /// oversized absolute slack.</summary>
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

    private enum Relation
    {
        Disjoint,

        FirstInsideSecond,
        SecondInsideFirst,

        Ambiguous,
    }

    /// <summary>A closed contour with the area it encloses, plus its bounding box (a sound prefilter).</summary>
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

    /// <summary>One primitive as a contour, or null when its region is not determined by the primitive alone.</summary>
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

            // A circle or rectangle is a contour on its own; lines, arcs and open polylines are joined
            // into a chain by Compute().
            default:
                return null;
        }
    }

    /// <summary>An edge of a chain: its two welded vertices, its contribution to the signed double area
    /// (∮ x·dy − y·dx) and the points that approximate it for the topological predicates.</summary>
    private sealed record Edge(int From, int To, double Contribution, IReadOnlyList<double[]> Samples);

    /// <summary>A pool of chain vertices, welded by proximity: two ends within <see cref="WeldMm"/> are the
    /// same point.</summary>
    private sealed class VertexPool
    {
        private readonly List<double[]> _points = new();

        public int Id(double x, double y)
        {
            for (var i = 0; i < _points.Count; i++)
            {
                if (Math.Abs(_points[i][0] - x) <= WeldMm && Math.Abs(_points[i][1] - y) <= WeldMm)
                {
                    return i;
                }
            }

            _points.Add(new[] { x, y });
            return _points.Count - 1;
        }
    }

    /// <summary>Turns one line, arc or open polyline into chain edges.</summary>
    private static bool AppendEdges(SketchEntityDto entity, List<Edge> edges, VertexPool pool, out string? reason)
    {
        reason = null;
        switch (entity.Kind)
        {
            case SketchEntityKind.Line:
                if (entity.StartMm is not { Count: >= 2 } lineStart || entity.EndMm is not { Count: >= 2 } lineEnd
                    || !Finite(lineStart) || !Finite(lineEnd))
                {
                    reason = "отрезок не разобран: нужны start_mm и end_mm";
                    return false;
                }

                edges.Add(SegmentEdge(pool, lineStart[0], lineStart[1], lineEnd[0], lineEnd[1]));
                return true;

            case SketchEntityKind.Arc:
                if (entity.CenterMm is not { Count: >= 2 } center
                    || entity.RadiusMm is not double radius || !(radius > 0d)
                    || !Finite(center))
                {
                    reason = "дуга не разобрана: нужны center_mm и radius_mm";
                    return false;
                }

                double startDeg;
                double sweepDeg;
                if (entity.StartPointMm is { Count: >= 2 } arcStart && entity.EndPointMm is { Count: >= 2 } arcEnd)
                {
                    // The end-point form names the same arc as a pair of angles: the sweep is the signed
                    // angle from the start point to the end point, taken in the declared direction.
                    if (!Finite(arcStart) || !Finite(arcEnd))
                    {
                        reason = "дуга по концам не разобрана: нужны start_point_mm и end_point_mm";
                        return false;
                    }

                    startDeg = Degrees(Math.Atan2(arcStart[1] - center[1], arcStart[0] - center[0]));
                    var endDeg = Degrees(Math.Atan2(arcEnd[1] - center[1], arcEnd[0] - center[0]));
                    var ccw = ((endDeg - startDeg) % 360d + 360d) % 360d;
                    if (ccw < 1e-9 || 360d - ccw < 1e-9)
                    {
                        reason = "дуга по концам вырождена: начало и конец дают нулевой размах";
                        return false;
                    }

                    sweepDeg = entity.Clockwise == true ? ccw - 360d : ccw;
                }
                else if (entity.StartDeg is double angle && entity.SweepDeg is double sweep
                         && double.IsFinite(angle) && double.IsFinite(sweep))
                {
                    startDeg = angle;
                    sweepDeg = sweep;
                }
                else
                {
                    reason = "дуга не разобрана: нужны ЛИБО start_deg и sweep_deg, ЛИБО start_point_mm и end_point_mm";
                    return false;
                }

                edges.Add(ArcEdge(pool, center[0], center[1], radius, startDeg, sweepDeg));
                return true;

            case SketchEntityKind.Polyline:
                if (entity.PointsMm is not { Count: >= 2 } path
                    || path.Any(p => p.Count < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
                {
                    reason = "незамкнутая полилиния не разобрана: нужно не меньше двух вершин";
                    return false;
                }

                for (var i = 0; i + 1 < path.Count; i++)
                {
                    edges.Add(SegmentEdge(pool, path[i][0], path[i][1], path[i + 1][0], path[i + 1][1]));
                }

                return true;

            default:
                reason = $"примитив {KindName(entity)} не даёт аналитики";
                return false;
        }
    }

    // The vertex pool lives for the duration of one Compute() call: the ids it hands out index into it, so
    // it must not outlive the edge list that references them.
    private static Edge SegmentEdge(VertexPool pool, double ax, double ay, double bx, double by) =>
        new(pool.Id(ax, ay), pool.Id(bx, by), (ax * by) - (bx * ay), new[] { new[] { ax, ay }, new[] { bx, by } });

    /// <summary>A circular arc as a chain edge: Green's contribution is the chord term plus r²(θ−sinθ) for
    /// the signed sweep θ, and the samples lie ON the arc so a containment test against them is exact at
    /// the vertices and conservative between them.</summary>
    private static Edge ArcEdge(VertexPool pool, double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        var a0 = startDeg * Math.PI / 180d;
        var a1 = (startDeg + sweepDeg) * Math.PI / 180d;
        var ax = cx + (r * Math.Cos(a0));
        var ay = cy + (r * Math.Sin(a0));
        var bx = cx + (r * Math.Cos(a1));
        var by = cy + (r * Math.Sin(a1));
        var delta = sweepDeg * Math.PI / 180d;
        var contribution = ((ax * by) - (bx * ay)) + (r * r * (delta - Math.Sin(delta)));
        return new Edge(pool.Id(ax, ay), pool.Id(bx, by), contribution, ArcSamples(cx, cy, r, startDeg, sweepDeg));
    }

    /// <summary>Points along an arc for the topological predicates. At most one per degree and never more
    /// than 180 per arc, which keeps the pairwise self-intersection test bounded while holding the chord
    /// sagitta below a hundredth of a millimetre even on the largest radii seen in practice.</summary>
    private static IReadOnlyList<double[]> ArcSamples(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        var step = Math.Max(1.0, Math.Abs(sweepDeg) / 180d);
        var n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepDeg) / step) + 1);
        var points = new List<double[]>(n);
        for (var i = 0; i < n; i++)
        {
            var a = (startDeg + (sweepDeg * i / (n - 1))) * Math.PI / 180d;
            points.Add(new[] { cx + (r * Math.Cos(a)), cy + (r * Math.Sin(a)) });
        }

        return points;
    }

    /// <summary>Welds the edges into closed chains. Every vertex must carry exactly two edge ends — a
    /// vertex with one is an open chain, one with three or more is a branch — and the resulting polygon
    /// must not cross itself. Each chain becomes a contour whose area is exact while its points are the
    /// approximation the nesting predicates work on.</summary>
    private static bool BuildChains(List<Edge> edges, List<Contour> contours, out string? reason)
    {
        reason = null;
        var incidence = new Dictionary<int, List<int>>();
        foreach (var (edge, index) in edges.Select((e, i) => (e, i)))
        {
            Add(incidence, edge.From, index);
            Add(incidence, edge.To, index);
        }

        var branching = incidence.Where(kv => kv.Value.Count != 2).ToList();
        if (branching.Count > 0)
        {
            var worst = branching.Max(kv => kv.Value.Count);
            reason = $"цепочка контура не замкнута или ветвится: в {branching.Count} вершинах "
                + $"сходится не два конца (больше всего — {worst})";
            return false;
        }

        var used = new bool[edges.Count];
        for (var start = 0; start < edges.Count; start++)
        {
            if (used[start])
            {
                continue;
            }

            var points = new List<double[]>();
            var contribution = 0d;
            var current = edges[start].From;
            var index = start;
            while (true)
            {
                used[index] = true;
                var edge = edges[index];
                var forward = edge.From == current;
                contribution += forward ? edge.Contribution : -edge.Contribution;
                var samples = forward ? edge.Samples : edge.Samples.Reverse().ToList();
                foreach (var point in samples)
                {
                    if (points.Count > 0 && Near(points[^1], point))
                    {
                        continue;
                    }

                    points.Add(point);
                }

                current = forward ? edge.To : edge.From;
                var next = incidence[current].FirstOrDefault(j => !used[j], -1);
                if (next < 0)
                {
                    break;
                }

                index = next;
            }

            if (points.Count > 1 && Near(points[0], points[^1]))
            {
                points.RemoveAt(points.Count - 1);
            }

            if (points.Count < 3)
            {
                reason = "контур вырожден: у собранной цепочки меньше трёх точек";
                return false;
            }

            if (SelfIntersects(points))
            {
                reason = "контур самопересекается";
                return false;
            }

            var area = Math.Abs(contribution) / 2d;
            if (!(area > 0d) || !double.IsFinite(area))
            {
                reason = "площадь собранного контура не положительна";
                return false;
            }

            contours.Add(new Ring(points, area));
        }

        return true;
    }

    private static void Add(Dictionary<int, List<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = new List<int>(2);
        }

        list.Add(value);
    }

    private static bool Near(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) <= WeldMm && Math.Abs(a[1] - b[1]) <= WeldMm;

    private static double Degrees(double radians) => radians * 180d / Math.PI;

    private static bool Finite(IReadOnlyList<double> values) =>
        values.Count >= 2 && double.IsFinite(values[0]) && double.IsFinite(values[1]);

    private static string KindName(SketchEntityDto entity) => entity.Kind.ToString().ToLowerInvariant();

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

    /// <summary>Even-odd point-in-polygon by ray casting. Points on the boundary count as inside: callers
    /// have already refused touching contours, so a boundary point is a contradiction, resolved
    /// conservatively.</summary>
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

    /// <summary>Whether two segments cross or touch. Touching counts as meeting on purpose: a shared point
    /// or edge makes the enclosed region depend on the kernel's resolution.</summary>
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

    private static double Cross(double[] a, double[] b, double[] p) =>
        ((b[0] - a[0]) * (p[1] - a[1])) - ((b[1] - a[1]) * (p[0] - a[0]));

    private static bool OnSegment(double[] a, double[] b, double[] p, double eps) =>
        p[0] >= Math.Min(a[0], b[0]) - eps
        && p[0] <= Math.Max(a[0], b[0]) + eps
        && p[1] >= Math.Min(a[1], b[1]) - eps
        && p[1] <= Math.Max(a[1], b[1]) + eps;
}

/// <summary>The enclosed area in mm² and, when it could not be computed, the reason in words — so the
/// caller names the gap instead of reporting a bare "not computable".</summary>
public sealed record ProfileAreaOutcome(double? AreaMm2, string? UnavailableReason);

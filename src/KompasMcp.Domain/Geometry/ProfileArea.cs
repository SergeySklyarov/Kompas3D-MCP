using System.Globalization;
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
    /// in <c>unverified_aspects</c> instead of reporting a bare "not computable", and the state that
    /// separates a defect of the caller's contour from a figure the server cannot analyse at all.</summary>
    public static ProfileAreaOutcome Compute(IReadOnlyList<SketchEntityDto> entities)
    {
        if (entities.Count == 0)
        {
            return new ProfileAreaOutcome(null, "профиль пуст — примитивов нет", ProfileInputState.NotAnalysable);
        }

        var contours = new List<Contour>(entities.Count);
        var edges = new List<Edge>();
        var pool = new VertexPool();
        for (var index = 0; index < entities.Count; index++)
        {
            var entity = entities[index];
            switch (entity.Kind)
            {
                case SketchEntityKind.Circle:
                case SketchEntityKind.Rectangle:
                    if (ContourOf(entity, index, out var closedReason, out var closedState) is not Contour closed)
                    {
                        return new ProfileAreaOutcome(null, closedReason, closedState);
                    }

                    contours.Add(closed);
                    break;

                case SketchEntityKind.Polyline when entity.Closed == true:
                    if (ContourOf(entity, index, out var polygonReason, out var polygonState) is not Contour polygon)
                    {
                        return new ProfileAreaOutcome(null, polygonReason, polygonState);
                    }

                    contours.Add(polygon);
                    break;

                case SketchEntityKind.Line:
                case SketchEntityKind.Arc:
                case SketchEntityKind.Polyline:
                    if (!AppendEdges(entity, index, edges, pool, out var edgeReason))
                    {
                        return new ProfileAreaOutcome(null, edgeReason, ProfileInputState.NotAnalysable);
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
                        + "выдавливания, а не аналитическая площадь.", ProfileInputState.NotAnalysable);

                default:
                    return new ProfileAreaOutcome(null,
                        $"примитив {KindName(entity)} не даёт аналитики", ProfileInputState.NotAnalysable);
            }
        }

        if (edges.Count > 0
            && !BuildChains(edges, pool, contours, out var chainReason, out var chainState))
        {
            return new ProfileAreaOutcome(null, chainReason, chainState);
        }

        if (contours.Count == 0)
        {
            return new ProfileAreaOutcome(null,
                "замкнутый контур не собран из примитивов", ProfileInputState.NotAnalysable);
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
                            "отношение контуров не определяется надёжно: они касаются или пересекаются",
                            ProfileInputState.Defect);
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
            ? new ProfileAreaOutcome(total, null, ProfileInputState.Consistent)
            : new ProfileAreaOutcome(null,
                "контуры взаимно уничтожились: площадь региона не положительна", ProfileInputState.Defect);
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

    /// <summary>One primitive as a contour, or null when its region is not determined by the primitive
    /// alone; <paramref name="reason"/> then names what was missing or where the primitive crosses itself,
    /// and <paramref name="state"/> separates the two.</summary>
    private static Contour? ContourOf(
        SketchEntityDto entity,
        int entityIndex,
        out string? reason,
        out ProfileInputState state)
    {
        reason = null;
        state = ProfileInputState.NotAnalysable;
        switch (entity.Kind)
        {
            case SketchEntityKind.Circle:
                if (entity.CenterMm is not { Count: >= 2 } center
                    || entity.RadiusMm is not double radius
                    || !(radius > 0d))
                {
                    reason = "примитив окружность не разобран: не хватает полей для площади";
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
                    reason = "примитив прямоугольник не разобран: не хватает полей для площади";
                    return null;
                }

                return PolygonOf(
                    new[]
                    {
                        new[] { corner[0], corner[1] },
                        new[] { corner[0] + width, corner[1] },
                        new[] { corner[0] + width, corner[1] + height },
                        new[] { corner[0], corner[1] + height },
                    },
                    $"примитив {entityIndex} (прямоугольник)",
                    out reason,
                    out state);

            case SketchEntityKind.Polyline:
                if (entity.Closed != true || entity.PointsMm is not { Count: >= 3 } points)
                {
                    reason = "замкнутая полилиния не разобрана: нужно не меньше трёх вершин";
                    return null;
                }

                if (points.Any(p => p.Count < 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
                {
                    reason = "замкнутая полилиния не разобрана: не все вершины конечны";
                    return null;
                }

                return PolygonOf(
                    points.Select(p => new[] { p[0], p[1] }).ToArray(),
                    $"примитив {entityIndex} (замкнутая полилиния)",
                    out reason,
                    out state);

            // A circle or rectangle is a contour on its own; lines, arcs and open polylines are joined
            // into a chain by Compute().
            default:
                return null;
        }
    }

    /// <summary>An edge of a chain: its two welded vertices, its contribution to the signed double area
    /// (∮ x·dy − y·dx), the points that approximate it for the topological predicates, and the index of the
    /// caller's primitive it came from (a polyline yields several edges carrying the same index).</summary>
    private sealed record Edge(
        int From,
        int To,
        double Contribution,
        IReadOnlyList<double[]> Samples,
        int EntityIndex);

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

        public double[] Point(int id) => _points[id];
    }

    /// <summary>Turns one line, arc or open polyline into chain edges. <paramref name="entityIndex"/> is
    /// carried onto every edge the primitive produces, so a refusal can name the PRIMITIVE the caller
    /// passed rather than an internal edge number.</summary>
    private static bool AppendEdges(
        SketchEntityDto entity,
        int entityIndex,
        List<Edge> edges,
        VertexPool pool,
        out string? reason)
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

                edges.Add(SegmentEdge(pool, lineStart[0], lineStart[1], lineEnd[0], lineEnd[1], entityIndex));
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

                edges.Add(ArcEdge(pool, center[0], center[1], radius, startDeg, sweepDeg, entityIndex));
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
                    edges.Add(SegmentEdge(pool, path[i][0], path[i][1], path[i + 1][0], path[i + 1][1], entityIndex));
                }

                return true;

            default:
                reason = $"примитив {KindName(entity)} не даёт аналитики";
                return false;
        }
    }

    // The vertex pool lives for the duration of one Compute() call: the ids it hands out index into it, so
    // it must not outlive the edge list that references them.
    private static Edge SegmentEdge(VertexPool pool, double ax, double ay, double bx, double by, int entityIndex) =>
        new(pool.Id(ax, ay), pool.Id(bx, by), (ax * by) - (bx * ay),
            new[] { new[] { ax, ay }, new[] { bx, by } }, entityIndex);

    /// <summary>A circular arc as a chain edge: Green's contribution is the chord term plus r²(θ−sinθ) for
    /// the signed sweep θ, and the samples lie ON the arc so a containment test against them is exact at
    /// the vertices and conservative between them.</summary>
    private static Edge ArcEdge(
        VertexPool pool,
        double cx,
        double cy,
        double r,
        double startDeg,
        double sweepDeg,
        int entityIndex)
    {
        var a0 = startDeg * Math.PI / 180d;
        var a1 = (startDeg + sweepDeg) * Math.PI / 180d;
        var ax = cx + (r * Math.Cos(a0));
        var ay = cy + (r * Math.Sin(a0));
        var bx = cx + (r * Math.Cos(a1));
        var by = cy + (r * Math.Sin(a1));
        var delta = sweepDeg * Math.PI / 180d;
        var contribution = ((ax * by) - (bx * ay)) + (r * r * (delta - Math.Sin(delta)));
        return new Edge(pool.Id(ax, ay), pool.Id(bx, by), contribution,
            ArcSamples(cx, cy, r, startDeg, sweepDeg), entityIndex);
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
    /// approximation the nesting predicates work on.
    /// <para>INVARIANT: a refusal names the PLACE — the caller's primitive numbers, zero-based, in the
    /// order the entities were passed, and the coordinate of the offending point. A contour defect is only
    /// actionable with them; the caller cannot search a hundred-primitive chain by eye.</para>
    /// History: docs/decisions/adapter-sketch.md#profile-input-diagnosis</remarks>
    private static bool BuildChains(
        List<Edge> edges,
        VertexPool pool,
        List<Contour> contours,
        out string? reason,
        out ProfileInputState state)
    {
        reason = null;
        state = ProfileInputState.Consistent;
        var incidence = new Dictionary<int, List<int>>();
        foreach (var (edge, index) in edges.Select((e, i) => (e, i)))
        {
            Add(incidence, edge.From, index);
            Add(incidence, edge.To, index);
        }

        // A branch is reported before an open end: a chain that branches is not a simple contour whatever
        // its closure, and naming the branch is the more specific fact.
        var branching = incidence.Where(kv => kv.Value.Count > 2).OrderBy(kv => kv.Key).ToList();
        if (branching.Count > 0)
        {
            reason = DescribeVertices("цепочка контура ветвится", branching, edges, pool, "концов");
            state = ProfileInputState.Defect;
            return false;
        }

        var loose = incidence.Where(kv => kv.Value.Count == 1).OrderBy(kv => kv.Key).ToList();
        if (loose.Count > 0)
        {
            reason = DescribeVertices("цепочка контура не замкнута", loose, edges, pool, "свободных концов");
            state = ProfileInputState.Defect;
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
            // INVARIANT: owners[i] is the caller's primitive the segment (i, i+1) belongs to, not the
            // primitive that produced the vertex. At a welded vertex the segment LEAVING it belongs to the
            // next edge, so the shared sample is re-labelled — otherwise a crossing just past a joint
            // would be blamed on the previous primitive.
            var owners = new List<int>();
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
                        owners[^1] = edge.EntityIndex;
                        continue;
                    }

                    points.Add(point);
                    owners.Add(edge.EntityIndex);
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
                owners.RemoveAt(owners.Count - 1);
            }

            if (points.Count < 3)
            {
                reason = "контур вырожден: у собранной цепочки меньше трёх точек";
                state = ProfileInputState.Defect;
                return false;
            }

            if (FindSelfCrossing(points, owners) is SelfCrossing crossing)
            {
                reason = crossing.Describe(null);
                state = ProfileInputState.Defect;
                return false;
            }

            var area = Math.Abs(contribution) / 2d;
            if (!(area > 0d) || !double.IsFinite(area))
            {
                reason = "площадь собранного контура не положительна";
                state = ProfileInputState.Defect;
                return false;
            }

            contours.Add(new Ring(points, area));
        }

        return true;
    }

    /// <summary>Names the vertices where the chain ends or branches, with the caller's primitive numbers.
    /// LIMIT: at most three vertices are listed and the rest are counted — a diagnostic line, not a
    /// report, and an unbounded list would be useless to the caller anyway.</summary>
    private static string DescribeVertices(
        string lead,
        List<KeyValuePair<int, List<int>>> vertices,
        List<Edge> edges,
        VertexPool pool,
        string noun)
    {
        const int Listed = 3;
        var shown = new List<string>();
        foreach (var vertex in vertices.Take(Listed))
        {
            var point = pool.Point(vertex.Key);
            var primitives = string.Join(", ", vertex.Value
                .Select(index => edges[index].EntityIndex)
                .Distinct()
                .OrderBy(value => value));
            shown.Add($"{Point(point)} (примитивы {primitives})");
        }

        var rest = vertices.Count > Listed ? $"; и ещё {vertices.Count - Listed}" : string.Empty;
        return $"{lead}: {vertices.Count} {noun}, больше всего концов — "
            + $"{vertices.Max(v => v.Value.Count)}; в " + string.Join("; ", shown) + rest
            + ". Нумерация примитивов с нуля, в порядке передачи в профиль.";
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

    private static Ring? PolygonOf(
        IReadOnlyList<double[]> points,
        string label,
        out string? reason,
        out ProfileInputState state)
    {
        reason = null;
        state = ProfileInputState.NotAnalysable;
        var owners = Enumerable.Repeat(-1, points.Count).ToArray();
        if (FindSelfCrossing(points, owners) is SelfCrossing crossing)
        {
            reason = crossing.Describe(label);
            state = ProfileInputState.Defect;
            return null;
        }

        var area = Shoelace(points);
        if (!(area > 0d))
        {
            reason = $"{label} не разобран: площадь не положительна";
            state = ProfileInputState.Defect;
            return null;
        }

        return new Ring(points, area);
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

    /// <summary>One place where a closed contour crosses itself: the two caller primitives whose segments
    /// meet and the point at which they meet. A negative primitive index means the crossing was found
    /// inside a figure built from one primitive, where the caller's index is not per-segment.</summary>
    private sealed record Crossing(int FirstEntity, int SecondEntity, double X, double Y);

    /// <summary>Where a closed contour crosses itself: the places in walk order, and how many there are.
    /// LIMIT: at most <see cref="Listed"/> places are named — a diagnostic line, not a report, and a
    /// contour with a hundred crossings would otherwise produce a hundred-line refusal. The count is
    /// always stated when it exceeds the list, so a truncated list never looks complete.
    /// History: docs/decisions/adapter-sketch.md#profile-input-diagnosis</summary>
    private sealed record SelfCrossing(IReadOnlyList<Crossing> Places, int Count)
    {
        private const int Listed = 3;

        /// <summary>The refusal line. <paramref name="label"/> names the single primitive a self-crossing
        /// polygon was built from; null means a chain, where the two primitives are named instead.</summary>
        public string Describe(string? label)
        {
            var first = Point(new[] { Places[0].X, Places[0].Y });
            if (label is not null)
            {
                var single = $"{label} самопересекается у точки {first}";
                return Count > 1 ? single + $" Всего мест самопересечения — {Count}." : single;
            }

            var parts = new List<string>(Places.Count);
            foreach (var place in Places)
            {
                var where = Point(new[] { place.X, place.Y });
                if (place.FirstEntity < 0 || place.SecondEntity < 0)
                {
                    parts.Add("место без восстановленного номера примитива у точки " + where);
                }
                else if (place.FirstEntity == place.SecondEntity)
                {
                    parts.Add($"примитив {place.FirstEntity} пересекает сам себя у точки {where}");
                }
                else
                {
                    parts.Add($"примитивы {place.FirstEntity} и {place.SecondEntity} пересекаются у точки {where}");
                }
            }

            var lead = "контур самопересекается: " + string.Join("; ", parts) + ".";
            return Count > Places.Count ? lead + $" Всего мест самопересечения — {Count}." : lead;
        }
    }

    /// <summary>The self-crossings of a closed contour, or null when there are none.
    /// <paramref name="owners"/> is parallel to <paramref name="points"/> and names the caller's primitive
    /// each segment belongs to.</summary>
    private static SelfCrossing? FindSelfCrossing(
        IReadOnlyList<double[]> points,
        IReadOnlyList<int> owners)
    {
        const int Listed = 3;
        var places = new List<Crossing>(Listed);
        var count = 0;
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

                var b1 = points[j];
                var b2 = points[(j + 1) % points.Count];
                if (!SegmentsMeet(a1, a2, b1, b2))
                {
                    continue;
                }

                count++;
                if (places.Count < Listed)
                {
                    var (x, y) = MeetingPoint(a1, a2, b1, b2);
                    places.Add(new Crossing(owners[i], owners[j], x, y));
                }
            }
        }

        return count == 0 ? null : new SelfCrossing(places, count);
    }

    /// <summary>Where two meeting segments meet. For a proper crossing the intersection is computed; for a
    /// collinear overlap, which has no single crossing point, the midpoint of the closest pair of ends is
    /// named — a place on the contour either way, and never an invented intersection.</summary>
    private static (double X, double Y) MeetingPoint(double[] p1, double[] p2, double[] q1, double[] q2)
    {
        var d = ((p2[0] - p1[0]) * (q2[1] - q1[1])) - ((p2[1] - p1[1]) * (q2[0] - q1[0]));
        if (Math.Abs(d) > 1e-18)
        {
            var t = (((q1[0] - p1[0]) * (q2[1] - q1[1])) - ((q1[1] - p1[1]) * (q2[0] - q1[0]))) / d;
            return (p1[0] + (t * (p2[0] - p1[0])), p1[1] + (t * (p2[1] - p1[1])));
        }

        var pairs = new[]
        {
            (A: p1, B: q1),
            (A: p1, B: q2),
            (A: p2, B: q1),
            (A: p2, B: q2),
        };
        var best = pairs.OrderBy(pair => Distance(pair.A[0], pair.A[1], pair.B[0], pair.B[1])).First();
        return ((best.A[0] + best.B[0]) / 2d, (best.A[1] + best.B[1]) / 2d);
    }

    /// <summary>A point as the client reads it. INVARIANT: invariant culture and at most six decimals — the
    /// same figure must read the same way in every locale, and six decimals is the weld scale of this
    /// analysis.</summary>
    private static string Point(double[] point) =>
        "(" + Num(point[0]) + "; " + Num(point[1]) + ") мм";

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

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

/// <summary>How the server's own analysis of the INPUT profile ended. It separates a defect of the caller's
/// contour, which the caller can repair, from a figure the server cannot analyse at all (a spline, an
/// unknown primitive, a missing field) — two different answers to "why is there no area?".</summary>
public enum ProfileInputState
{
    /// <summary>The chain is assembled, closed, unbranched and free of self-intersections, and the region
    /// was determined.</summary>
    Consistent,

    /// <summary>A structural defect of the contour: self-intersection, a branch, an open end, a degenerate
    /// chain, or contours that touch or cancel.</summary>
    Defect,

    /// <summary>The server cannot analyse this input: an empty profile, a spline, a primitive without
    /// analytics, or a missing field.</summary>
    NotAnalysable,
}

/// <summary>The enclosed area in mm², the reason it could not be computed, and the state that says whether
/// the reason is a defect of the caller's contour or a figure the server does not analyse — so the caller
/// names the gap instead of reporting a bare "not computable".</summary>
public sealed record ProfileAreaOutcome(
    double? AreaMm2,
    string? UnavailableReason,
    ProfileInputState State);

namespace KompasMcp.Domain.Geometry;

/// <summary>One segment of a sketch chain, in sketch-plane coordinates, mm.</summary>
public readonly record struct RibSegment(double X1, double Y1, double X2, double Y2);

/// <summary>Whether a sketch is the OPEN single chain a rib needs, and the area it encloses together with
/// its end chord — the analytic expectation for the volume the rib adds.</summary>
/// <remarks>DOC: <c>ksribdefinition.html</c> requires an open contour drawn as ONE chain, continued
/// to the nearest face — no separate parameter exists for that continuation. MEASURED: a closed
/// contour is refused at <c>SetSketch</c> with <c>false</c> and no cause, so the defect is named here
/// BEFORE COM. INVARIANT: the area is the shoelace over the walked chain closed by the chord between
/// its free ends — the region the kernel fills; a chain that crosses itself has no such region.
/// History: docs/decisions/ribs.md#chain-check</remarks>
public static class RibChain
{
    /// <summary>How close two segment ends must be to count as one vertex, in mm — the weld scale of the
    /// profile analysis, so a chain drawn in one session reads the same way in both places.</summary>
    private const double WeldMm = 1e-6;

    /// <summary>The chain, or the reason it is not one. <see cref="AreaMm2"/> is null whenever
    /// <see cref="IsChain"/> is false: an area of a contour that does not exist would be read as a
    /// measured quantity.</summary>
    public sealed record Outcome(bool IsChain, double? AreaMm2, int SegmentCount, int PointCount, string? Reason);

    /// <summary>Analyse the chain. <paramref name="segments"/> is the sketch's segments in the order the
    /// client drew them; the refusal names a vertex or a segment NUMBER in that order, because a contour
    /// defect cannot be found by eye.</summary>
    public static Outcome Analyze(IReadOnlyList<RibSegment> segments)
    {
        if (segments.Count == 0)
        {
            return new Outcome(false, null, 0, 0,
                "в эскизе нет ни одного отрезка: ребро строится по разомкнутой цепочке отрезков");
        }

        var points = new List<double[]>(segments.Count + 1);
        var ends = new int[segments.Count, 2];
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!double.IsFinite(segment.X1) || !double.IsFinite(segment.Y1)
                || !double.IsFinite(segment.X2) || !double.IsFinite(segment.Y2))
            {
                return Refusal(i, "координаты отрезка не конечны");
            }

            ends[i, 0] = Weld(points, segment.X1, segment.Y1);
            ends[i, 1] = Weld(points, segment.X2, segment.Y2);
            if (ends[i, 0] == ends[i, 1])
            {
                return Refusal(i, "начало и конец совпадают: отрезок вырожден");
            }
        }

        var degree = new int[points.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            degree[ends[i, 0]]++;
            degree[ends[i, 1]]++;
        }

        var free = new List<int>();
        for (var vertex = 0; vertex < points.Count; vertex++)
        {
            if (degree[vertex] > 2)
            {
                return new Outcome(false, null, segments.Count, points.Count,
                    "цепочка ветвится: в вершине " + Point(points[vertex]) + " сходится "
                    + degree[vertex] + " концов отрезков, а ребро требует одной цепочки без ветвлений");
            }

            if (degree[vertex] == 1)
            {
                free.Add(vertex);
            }
        }

        if (free.Count == 0)
        {
            return new Outcome(false, null, segments.Count, points.Count,
                "контур замкнут: все вершины имеют по два конца. Ребро строится по РАЗОМКНУТОЙ "
                + "цепочке, а замкнутый контур ядро отвергает уже на SetSketch без причины");
        }

        if (free.Count != 2)
        {
            return new Outcome(false, null, segments.Count, points.Count,
                "свободных концов " + free.Count + ", а не два: контур распадается на несколько "
                + "цепочек. Первые из них — " + string.Join("; ", free.Take(3).Select(v => Point(points[v])))
                + ". Ребро строится по ОДНОЙ цепочке");
        }

        var walked = Walk(segments, ends, free[0], free[1], points.Count);
        if (walked is null)
        {
            return new Outcome(false, null, segments.Count, points.Count,
                "цепочка не проходится от одного свободного конца до другого: часть отрезков "
                + "образует отдельный замкнутый контур");
        }

        if (walked.Count != segments.Count)
        {
            return new Outcome(false, null, segments.Count, points.Count,
                "в цепочке " + walked.Count + " отрезков из " + segments.Count + ": остальные "
                + "не связаны с ней");
        }

        var path = new List<double[]>(walked.Count + 1) { points[walked[0].From] };
        path.AddRange(walked.Select(step => points[step.To]));
        var area = ProfileArea.Shoelace(path);
        if (!(area > 0d) || !double.IsFinite(area))
        {
            return new Outcome(false, null, segments.Count, points.Count,
                "площадь, ограниченная цепочкой и её замыкающей хордой, не положительна: "
                + "цепочка самопересекается или лежит на одной прямой");
        }

        return new Outcome(true, area, segments.Count, path.Count, null);
    }

    private static Outcome Refusal(int segmentIndex, string reason) =>
        new(false, null, segmentIndex + 1, 0, "отрезок " + segmentIndex + " (с нуля): " + reason);

    /// <summary>Walk the chain from one free end to the other, each segment used at most once. Returns the
    /// segments as oriented vertex pairs, or null when the walk gets stuck — a second contour.</summary>
    private static List<(int From, int To)>? Walk(
        IReadOnlyList<RibSegment> segments,
        int[,] ends,
        int start,
        int finish,
        int vertexCount)
    {
        var incident = new List<int>[vertexCount];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            incident[vertex] = new List<int>(2);
        }

        for (var i = 0; i < segments.Count; i++)
        {
            incident[ends[i, 0]].Add(i);
            incident[ends[i, 1]].Add(i);
        }

        var used = new bool[segments.Count];
        var walked = new List<(int From, int To)>(segments.Count);
        var current = start;
        while (current != finish || walked.Count == 0)
        {
            var next = incident[current].FirstOrDefault(index => !used[index], -1);
            if (next < 0)
            {
                return null;
            }

            used[next] = true;
            var other = ends[next, 0] == current ? ends[next, 1] : ends[next, 0];
            walked.Add((current, other));
            current = other;
            if (walked.Count > segments.Count)
            {
                return null;
            }
        }

        return walked;
    }

    private static int Weld(List<double[]> points, double x, double y)
    {
        // The weld is a linear scan over the chain's vertices: a rib contour is a handful of segments,
        // and a hash grid would cost more code than the scan costs time.
        for (var i = 0; i < points.Count; i++)
        {
            if (Math.Abs(points[i][0] - x) <= WeldMm && Math.Abs(points[i][1] - y) <= WeldMm)
            {
                return i;
            }
        }

        points.Add(new[] { x, y });
        return points.Count - 1;
    }

    private static string Point(double[] point) =>
        "(" + point[0].ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "; "
        + point[1].ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ") мм";

    /// <summary>Relative slack of the volume check, one hundredth: the rib is confirmed by the area of the
    /// caller's chain times the thickness, and the kernel's own tolerance is far below this.</summary>
    public const double VolumeTolerance = 0.01d;

    /// <summary>The volume a rib without draft must add: the chain's area times the thickness.</summary>
    public static double? ExpectedVolumeMm3(double? areaMm2, double thicknessMm) =>
        areaMm2 is double area && area > 0d && thicknessMm > 0d ? area * thicknessMm : null;

    /// <summary>Whether the measured increase is the expected one within <see cref="VolumeTolerance"/>.</summary>
    public static bool VolumeMatches(double? expectedMm3, double? addedMm3)
    {
        if (expectedMm3 is not double expected || addedMm3 is not double added || !(expected > 0d))
        {
            return false;
        }

        return Math.Abs(added - expected) <= VolumeTolerance * expected;
    }
}

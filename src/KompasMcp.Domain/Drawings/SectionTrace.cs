using System.Globalization;

namespace KompasMcp.Domain.Drawings;

/// <summary>What kind of section a trace asks for: two points, or a polyline whose bends move the
/// cutting plane.</summary>
public enum SectionMode
{
    Simple,
    Stepped,
}

/// <summary>The rule a cut-line trace must satisfy before anything is written to the drawing, and the
/// reading of a built section's picture.</summary>
/// <remarks>DOC: <c>icutline_points.html</c> — «x0, y0 (начальная точка), x1, y1, ... (точки
/// перегибов), xn, yn (конечная точка)», even element count; <c>icutline_arrowpos.html</c> — TRUE is
/// «слева по направлению ломаной». INVARIANT: a trace that breaks the rule is refused BEFORE the
/// document is touched, so a wrong trace cannot leave a half-built view behind.
/// MEASURED: the kernel builds a stepped section from a six-point trace and reports it as one view
/// with its own designation; the trace lives in VIEW mm, which is what
/// <c>IAssociationView.GetProjectionPoint</c> answers.
/// History: docs/decisions/drawings.md#section-route</remarks>
public static class SectionTrace
{
    /// <summary>How far a bend may sit from a right angle and still be a bend.</summary>
    /// <remarks>A trace is typed by hand in millimetres, so a bend carries the author's rounding, not
    /// a modelling error: at 0.5 degrees a 100 mm segment is off by under 0.9 mm.</remarks>
    public const double PerpendicularToleranceDegrees = 0.5d;

    /// <summary>A segment shorter than this is not a segment; it is a repeated point.</summary>
    public const double ZeroLengthMm = 1e-6d;

    /// <summary>Everything wrong with a trace, in the client's words; empty means it may be written.</summary>
    public static IReadOnlyList<string> Problems(SectionMode mode, IReadOnlyList<double>? points)
    {
        var problems = new List<string>();
        if (points is null || points.Count == 0)
        {
            problems.Add("трасса пуста: нужны координаты точек в мм");
            return problems;
        }

        if (points.Count % 2 != 0)
        {
            problems.Add($"координат {points.Count}: число должно быть чётным (пары x, y)");
            return problems;
        }

        var count = points.Count / 2;
        var required = mode == SectionMode.Simple ? 2 : 4;
        if (mode == SectionMode.Simple && count != 2)
        {
            problems.Add($"простой разрез задаётся ровно двумя точками, получено {count}");
        }

        if (mode == SectionMode.Stepped && count < required)
        {
            problems.Add($"ступенчатый разрез задаётся не меньше чем четырьмя точками, получено {count}");
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        var segments = Segments(points);
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].Length <= ZeroLengthMm)
            {
                problems.Add($"участок {index + 1} нулевой длины: соседние точки совпадают");
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        if (mode == SectionMode.Simple)
        {
            return problems;
        }

        // Even segments are cutting, odd ones are transitions between them.
        var cutting = segments.Where((_, index) => index % 2 == 0).ToList();
        var reference = cutting[0].Direction;
        foreach (var segment in cutting.Skip(1))
        {
            var sine = Math.Abs(Cross(reference, segment.Direction));
            if (sine > Math.Sin(PerpendicularToleranceDegrees * Math.PI / 180d))
            {
                problems.Add("секущие участки не параллельны: ступенчатый разрез требует одной серии");
            }
        }

        for (var index = 1; index < segments.Count; index++)
        {
            var angle = AngleDegrees(segments[index - 1].Direction, segments[index].Direction);
            if (Math.Abs(angle - 90d) > PerpendicularToleranceDegrees)
            {
                problems.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "переход в точке {0} не перпендикулярен секущему участку: угол {1:0.###}°",
                    index + 1,
                    angle));
            }
        }

        if (CrossesItself(points))
        {
            problems.Add("трасса самопересекается: участки пересекаются вне своих концов");
        }

        return problems;
    }

    /// <summary>The documented side of the arrows: <c>ArrowPos</c> TRUE is the left of the polyline.</summary>
    public static bool ArrowPos(bool lookLeft) => lookLeft;

    /// <summary>Which side a model-space look vector points to, relative to the trace; false with a
    /// named reason when the vector is not usable for this trace.</summary>
    public static bool TryLookSide(
        IReadOnlyList<double>? viewVector,
        IReadOnlyList<double> points,
        out bool lookLeft,
        out string? problem)
    {
        lookLeft = true;
        problem = null;
        if (viewVector is not { Count: 2 })
        {
            problem = "вектор взгляда задаётся двумя числами (x, y) в координатах вида";
            return false;
        }

        var segments = Segments(points);
        if (segments.Count == 0)
        {
            problem = "трассы нет: сторону стрелок определить не по чему";
            return false;
        }

        var cutting = segments[0].Direction;
        var vector = (X: viewVector[0], Y: viewVector[1]);
        var length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        if (length <= ZeroLengthMm)
        {
            problem = "вектор взгляда нулевой длины";
            return false;
        }

        // Perpendicular means the dot product is zero; the cross product of perpendicular unit vectors
        // is what a PARALLEL pair would be measured by, so the two are never interchanged here.
        var unit = (X: vector.X / length, Y: vector.Y / length);
        var cosine = Math.Abs((cutting.X * unit.X) + (cutting.Y * unit.Y));
        if (cosine > Math.Sin(PerpendicularToleranceDegrees * Math.PI / 180d))
        {
            problem = string.Format(
                CultureInfo.InvariantCulture,
                "вектор взгляда не перпендикулярен секущему участку: косинус {0:0.####}", cosine);
            return false;
        }

        lookLeft = Cross(cutting, vector) > 0d;
        return true;
    }

    /// <summary>Whether a built view is a section at all: the kernel answers with the objects it drew,
    /// so a zero count means the trace missed the body.</summary>
    public static bool Built(int? objectCount) => objectCount is > 0;

    private static List<(double X, double Y, double Length, (double X, double Y) Direction)> Segments(
        IReadOnlyList<double> points)
    {
        var segments = new List<(double, double, double, (double, double))>();
        for (var index = 0; index + 3 < points.Count; index += 2)
        {
            var dx = points[index + 2] - points[index];
            var dy = points[index + 3] - points[index + 1];
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            segments.Add((dx, dy, length, length <= ZeroLengthMm ? (0d, 0d) : (dx / length, dy / length)));
        }

        return segments;
    }

    private static double Cross((double X, double Y) left, (double X, double Y) right) =>
        (left.X * right.Y) - (left.Y * right.X);

    private static double AngleDegrees((double X, double Y) first, (double X, double Y) second)
    {
        var dot = Math.Clamp((first.X * second.X) + (first.Y * second.Y), -1d, 1d);
        return Math.Acos(dot) * 180d / Math.PI;
    }

    /// <summary>Non-adjacent segments that touch anywhere but at a shared end. Adjacent segments share a
    /// point by construction, so only the pairs with a gap between them are compared.</summary>
    private static bool CrossesItself(IReadOnlyList<double> points)
    {
        var count = points.Count / 2;
        for (var first = 0; first + 1 < count; first++)
        {
            for (var second = first + 2; second + 1 < count; second++)
            {
                if (first == 0 && second == count - 2)
                {
                    // The first and last segments share the polyline's ends only when it closes.
                    continue;
                }

                if (Intersects(points, first, second))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Intersects(IReadOnlyList<double> points, int firstSegment, int secondSegment)
    {
        double ax = points[firstSegment * 2], ay = points[(firstSegment * 2) + 1];
        double bx = points[(firstSegment * 2) + 2], by = points[(firstSegment * 2) + 3];
        double cx = points[secondSegment * 2], cy = points[(secondSegment * 2) + 1];
        double dx = points[(secondSegment * 2) + 2], dy = points[(secondSegment * 2) + 3];

        var d1 = Cross((bx - ax, by - ay), (cx - ax, cy - ay));
        var d2 = Cross((bx - ax, by - ay), (dx - ax, dy - ay));
        var d3 = Cross((dx - cx, dy - cy), (ax - cx, ay - cy));
        var d4 = Cross((dx - cx, dy - cy), (bx - cx, by - cy));
        return ((d1 > 0d && d2 < 0d) || (d1 < 0d && d2 > 0d))
            && ((d3 > 0d && d4 < 0d) || (d3 < 0d && d4 > 0d));
    }
}

/// <summary>Which picture a <c>vt_Section</c> view holds: the cut behind the plane, or the figure in it.</summary>
/// <remarks>DOC: <c>ksassociationviewparam_section.html</c> — «TRUE - сечение, FALSE - разрез»;
/// <c>iassociationview_section.html</c> — the API7 property of the same name, read and write.
/// MEASURED: the value is honoured only when the view is first built; on a built view
/// <c>Update()</c> answers false and the picture stays (<c>docs/decisions/drawings.md#section-route</c>).
/// History: docs/decisions/drawings.md#section-kind</remarks>
public static class SectionKinds
{
    public const string CutWord = "cut";

    public const string SectionWord = "section";

    /// <summary>The client's word for a kind; null means the cut, the value the kernel builds by default.</summary>
    public static bool? IsSection(string? kind) => kind switch
    {
        null or CutWord => false,
        SectionWord => true,
        _ => null,
    };

    /// <summary>Whether a word is published at all. An unknown word is refused by name, never defaulted.</summary>
    public static bool Published(string? kind) => kind is null or CutWord or SectionWord;

    /// <summary>The client's word for a read <c>Section</c> value; null stays null, because an unread
    /// value is not a kind.</summary>
    public static string? Name(bool? section) => section switch
    {
        true => SectionWord,
        false => CutWord,
        _ => null,
    };

    /// <summary>Whether an edit may ask for this kind: only the one the view already has.</summary>
    public static bool SameKind(bool? current, string? requested) =>
        requested is null || IsSection(requested) == current;
}

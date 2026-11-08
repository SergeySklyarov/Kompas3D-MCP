using System.Globalization;

namespace FlatTiltProbe;

/// <summary>One graphic primitive of a written DXF, reduced to the points that decide a contour.</summary>
internal sealed record Shape(string Kind, List<(double X, double Y)> Points, (double X, double Y, double R)? Circle);

/// <summary>The contour of one view: the shapes inside its frame, their box and the holes' circles.</summary>
/// <remarks>INVARIANT: the numbers come from the file KOMPAS wrote, never from the converter's return code;
/// the frame is the filter, because a drawing of a group carries the main view beside the wanted one.
/// LIMIT: LINE, ARC, CIRCLE and LWPOLYLINE only — other entities are counted out, not guessed.
/// History: docs/decisions/flat-part.md#view-choice</remarks>
internal sealed class Contour
{
    private readonly List<Shape> _shapes;

    public Contour(List<Shape> shapes)
    {
        _shapes = shapes;
    }

    public double[]? Box
    {
        get
        {
            var points = _shapes.SelectMany(shape => shape.Points).ToArray();
            return points.Length == 0
                ? null
                : new[]
                {
                    points.Min(point => point.X), points.Min(point => point.Y),
                    points.Max(point => point.X), points.Max(point => point.Y),
                };
        }
    }

    /// <summary>The box sides, ascending, so a rotated view is compared by size and not by orientation.</summary>
    public double[] SortedSize()
    {
        var box = Box;
        return box is null
            ? new[] { 0d, 0d }
            : new[] { Math.Abs(box[2] - box[0]), Math.Abs(box[3] - box[1]) }.OrderBy(value => value).ToArray();
    }

    /// <summary>Hole centres relative to the box centre, keyed by radius: the orientation's signature.</summary>
    public Dictionary<double, (double X, double Y)> CircleOffsets()
    {
        var box = Box;
        var result = new Dictionary<double, (double X, double Y)>();
        foreach (var shape in _shapes.Where(shape => shape.Circle is not null))
        {
            var (x, y, radius) = shape.Circle!.Value;
            var centreX = box is null ? 0d : (box[0] + box[2]) / 2d;
            var centreY = box is null ? 0d : (box[1] + box[3]) / 2d;
            result[Math.Round(radius, 3)] = (Math.Round(x - centreX, 4), Math.Round(y - centreY, 4));
        }

        return result;
    }

    /// <summary>The shapes whose own points all lie inside the view's frame, widened by half a millimetre.</summary>
    public Contour Clip(double[] frame)
    {
        if (frame is not { Length: 4 })
        {
            return this;
        }

        var left = Math.Min(frame[0], frame[2]) - 0.5d;
        var right = Math.Max(frame[0], frame[2]) + 0.5d;
        var bottom = Math.Min(frame[1], frame[3]) - 0.5d;
        var top = Math.Max(frame[1], frame[3]) + 0.5d;
        return new Contour(_shapes.Where(shape => shape.Points.Count > 0 && shape.Points.All(point =>
            point.X >= left && point.X <= right && point.Y >= bottom && point.Y <= top)).ToList());
    }

    public string Describe()
    {
        var box = Box;
        var size = SortedSize();
        var circles = CircleOffsets();
        return "габарит " + (box is null ? "нет" : Format(box))
            + " размер " + Format(size)
            + " кривые " + (circles.Count == 0
                ? "нет"
                : string.Join("; ", circles.Select(pair => "R" + Format(pair.Key) + " от центра ("
                    + Format(pair.Value.X) + ", " + Format(pair.Value.Y) + ")")));
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Format(double[] values) => string.Join("x", values.Select(Format));

    /// <summary>Reads an ASCII DXF into the primitives this probe compares.</summary>
    public static Contour Read(string path)
    {
        var lines = File.ReadAllLines(path);
        var shapes = new List<Shape>();
        var pairs = new List<(int Code, string Value)>();
        for (var index = 0; index + 1 < lines.Length; index += 2)
        {
            if (!int.TryParse(lines[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                pairs.Clear();
                continue;
            }

            pairs.Add((code, lines[index + 1].Trim()));
        }

        string? kind = null;
        var values = new List<(int Code, string Value)>();
        void Flush()
        {
            if (kind is not null && Build(kind, values) is { } shape)
            {
                shapes.Add(shape);
            }

            values.Clear();
        }

        foreach (var (code, value) in pairs)
        {
            if (code == 0)
            {
                Flush();
                kind = value;
            }
            else if (kind is not null)
            {
                values.Add((code, value));
            }
        }

        Flush();
        return new Contour(shapes);
    }

    private static Shape? Build(string kind, List<(int Code, string Value)> values)
    {
        double Number(int code)
        {
            var found = values.FirstOrDefault(pair => pair.Code == code);
            return double.TryParse(found.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0d;
        }

        switch (kind)
        {
            case "LINE":
                return new Shape(kind, new List<(double X, double Y)>
                {
                    (Number(10), Number(20)), (Number(11), Number(21)),
                }, null);
            case "CIRCLE":
                var radius = Number(40);
                var centreX = Number(10);
                var centreY = Number(20);
                return new Shape(kind, new List<(double X, double Y)>
                {
                    (centreX - radius, centreY), (centreX + radius, centreY),
                    (centreX, centreY - radius), (centreX, centreY + radius),
                }, (centreX, centreY, radius));
            case "ARC":
                var arcRadius = Number(40);
                var arcX = Number(10);
                var arcY = Number(20);
                var start = Number(50) * Math.PI / 180d;
                var end = Number(51) * Math.PI / 180d;
                var arc = new List<(double X, double Y)>();
                for (var step = 0; step <= 8; step++)
                {
                    var angle = start + ((end - start) * step / 8d);
                    arc.Add((arcX + (arcRadius * Math.Cos(angle)), arcY + (arcRadius * Math.Sin(angle))));
                }

                return new Shape(kind, arc, null);
            case "LWPOLYLINE":
                var polyline = new List<(double X, double Y)>();
                for (var index = 0; index < values.Count - 1; index++)
                {
                    if (values[index].Code == 10 && values[index + 1].Code == 20)
                    {
                        polyline.Add((Number(10), Number(20)));
                    }
                }

                return polyline.Count == 0 ? null : new Shape(kind, polyline, null);
            default:
                return null;
        }
    }
}

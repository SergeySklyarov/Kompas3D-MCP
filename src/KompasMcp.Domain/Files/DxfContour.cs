using System.Globalization;

namespace KompasMcp.Domain.Files;

/// <summary>A rectangle in DXF units (millimetres).</summary>
public sealed record DxfBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    /// <summary>The same size as <paramref name="other"/> within <paramref name="tolerance"/>, corners in
    /// either order.</summary>
    public bool SameSize(DxfBox other, double tolerance) =>
        Math.Abs(Math.Abs(Width) - Math.Abs(other.Width)) <= tolerance
        && Math.Abs(Math.Abs(Height) - Math.Abs(other.Height)) <= tolerance;
}

/// <summary>What an ASCII DXF file holds: entity counts and the box of their points.</summary>
public sealed record DxfContour(int LineCount, int ArcCount, int CircleCount, DxfBox? Box);

/// <summary>Reads an ASCII DXF far enough to measure a flat pattern: LINE / ARC / CIRCLE.</summary>
/// <remarks>INVARIANT: the numbers come from the file, never from the converter's return code. A drawing
/// also carries its sheet frame and title block, so the caller passes the window it wants measured and only
/// entities inside it count. LIMIT: group codes 0/8/10/20/11/21/40; other entities are ignored, not guessed.
/// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
public static class DxfContourReader
{
    /// <summary>Read the file. <paramref name="window"/> limits the measured points to a view's frame; the
    /// tolerance widens it by that much on each side, because a contour point may sit on the border.</summary>
    public static DxfContour? Read(string path, DxfBox? window = null, double windowToleranceMm = 0d)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var entities = new List<(string Type, List<(double X, double Y)> Points)>();
        var entity = string.Empty;
        double ax = 0, ay = 0, bx = 0, by = 0, radius = 0;
        var second = false;

        void Flush()
        {
            if (entity is "LINE")
            {
                var points = new List<(double X, double Y)> { (ax, ay) };
                if (second)
                {
                    points.Add((bx, by));
                }

                entities.Add(("LINE", points));
            }
            else if (entity is "ARC" or "CIRCLE")
            {
                // An arc is bounded by its circle: the box of the centre plus the radius contains it.
                entities.Add((entity, new List<(double X, double Y)>
                {
                    (ax - radius, ay - radius),
                    (ax + radius, ay + radius),
                }));
            }

            entity = string.Empty;
            ax = ay = bx = by = radius = 0;
            second = false;
        }

        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                return null;
            }

            var value = lines[i + 1].Trim();
            if (code == 0)
            {
                Flush();
                entity = value;
                continue;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                // Code 8 is the layer name: a string, not a number, and not measured.
                continue;
            }

            switch (code)
            {
                case 10: ax = number; break;
                case 20: ay = number; break;
                case 11: bx = number; second = true; break;
                case 21: by = number; second = true; break;
                case 40: radius = number; break;
            }
        }

        Flush();

        // INVARIANT: an entity counts only when it lies inside the window. A drawing also carries the
        // sheet frame and the title block, and counting those would report a contour nobody drew.
        var measured = window is { } box
            ? entities.Where(item => item.Points.All(point =>
                point.X >= box.MinX - windowToleranceMm && point.X <= box.MaxX + windowToleranceMm
                && point.Y >= box.MinY - windowToleranceMm && point.Y <= box.MaxY + windowToleranceMm))
            : entities;

        var lineCount = 0;
        var arcCount = 0;
        var circleCount = 0;
        var all = new List<(double X, double Y)>();
        foreach (var (type, points) in measured)
        {
            if (type is "LINE")
            {
                lineCount++;
            }
            else if (type is "ARC")
            {
                arcCount++;
            }
            else
            {
                circleCount++;
            }

            all.AddRange(points);
        }

        return new DxfContour(lineCount, arcCount, circleCount, BoxOf(all));
    }

    /// <summary>Box of the points, or <c>null</c> when there are none — an empty file is not a zero-size
    /// contour.</summary>
    public static DxfBox? BoxOf(IReadOnlyCollection<(double X, double Y)> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return minX > maxX || minY > maxY ? null : new DxfBox(minX, minY, maxX, maxY);
    }
}

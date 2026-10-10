using System.Globalization;
using System.Text;

namespace KompasMcp.Domain.Files;

/// <summary>Structural read of an STL file: byte length, triangle count and the box of the tessellation,
/// all taken from the FILE rather than from the converter's return value.</summary>
/// <remarks>DOC: the binary STL layout is an 80-byte header, a 4-byte triangle count, then 50 bytes per
/// triangle (normal, three vertices, a 2-byte attribute); the text form is a sequence of
/// <c>facet normal</c> blocks with <c>vertex</c> lines.
/// INVARIANT: the way the count was obtained is NAMED, so the number cannot be mistaken for a value the
/// kernel returned; a truncated or unparsable record is a NAMED aspect, never silently dropped.
/// History: docs/decisions/geometry.md#stl-triangle-count</remarks>
public static class StlFileInspector
{
    private const int BinaryHeaderBytes = 80;
    private const int BinaryCountBytes = 4;
    private const int BinaryTriangleBytes = 50;

    /// <summary>The file really is a binary STL.</summary>
    public const string KindBinary = "binary";

    /// <summary>The file really is a text STL.</summary>
    public const string KindText = "text";

    /// <summary>Neither shape was recognised.</summary>
    public const string KindUnknown = "unknown";

    public sealed record StlFacts(
        long ByteLength,
        long TriangleCount,
        string CountSource,
        double[] MinMm,
        double[] MaxMm,
        bool BoxRead,
        IReadOnlyList<string> UnverifiedAspects);

    /// <summary>What the FILE is, from its CONTENT — never from the request that produced it.</summary>
    /// <remarks>INVARIANT: the request is not evidence. MEASURED on the target build: the kernel's
    /// <c>formatBinary</c> yields the OPPOSITE kind from what the help page states, so a server that
    /// trusted the flag would parse a text file as binary and publish a triangle count of 443 for a file
    /// that holds 120 facets. The binary shape is accepted only on an EXACT fit (84 bytes plus a whole
    /// number of 50-byte records whose declared count agrees); otherwise a <c>facet normal</c> line makes
    /// the file text.
    /// History: docs/decisions/geometry.md#stl-triangle-count</remarks>
    public static string DetectKind(string path)
    {
        var length = new FileInfo(path).Length;
        if (length >= BinaryHeaderBytes + BinaryCountBytes)
        {
            var fromSize = (length - BinaryHeaderBytes - BinaryCountBytes) / BinaryTriangleBytes;
            if (fromSize > 0
                && BinaryHeaderBytes + BinaryCountBytes + fromSize * BinaryTriangleBytes == length)
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Position = BinaryHeaderBytes;
                var declared = new byte[BinaryCountBytes];
                if (stream.Read(declared, 0, BinaryCountBytes) == BinaryCountBytes
                    && BitConverter.ToUInt32(declared, 0) == fromSize)
                {
                    return KindBinary;
                }
            }
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[64 * 1024];
        var read = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, read).Contains("facet normal", StringComparison.OrdinalIgnoreCase)
            ? KindText
            : KindUnknown;
    }

    public static StlFacts Inspect(string path, bool binary)
    {
        var length = new FileInfo(path).Length;
        return binary ? InspectBinary(path, length) : InspectText(path, length);
    }

    public static StlFacts Inspect(string path, string kind)
    {
        var length = new FileInfo(path).Length;
        return kind == KindBinary ? InspectBinary(path, length) : InspectText(path, length);
    }

    private static StlFacts InspectBinary(string path, long length)
    {
        var unverified = new List<string>();
        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        var boxRead = false;

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        long declared = -1;
        if (length >= BinaryHeaderBytes + BinaryCountBytes)
        {
            stream.Position = BinaryHeaderBytes;
            declared = reader.ReadUInt32();
        }
        else
        {
            unverified.Add(
                "binary_header_absent — файл короче 84 байт: счётчик треугольников в заголовке отсутствует");
        }

        var fromSize = length >= BinaryHeaderBytes + BinaryCountBytes
            ? (length - BinaryHeaderBytes - BinaryCountBytes) / BinaryTriangleBytes
            : 0;
        if (declared >= 0 && declared != fromSize)
        {
            // The declared count and the size disagree: the size is what the bytes actually hold, and the
            // disagreement is named rather than smoothed over.
            unverified.Add(
                $"binary_header_count_differs — заголовок объявляет {declared} треугольников, размер файла даёт {fromSize}");
        }

        var record = new byte[BinaryTriangleBytes];
        stream.Position = BinaryHeaderBytes + BinaryCountBytes;
        for (long index = 0; index < fromSize; index++)
        {
            if (reader.Read(record, 0, BinaryTriangleBytes) != BinaryTriangleBytes)
            {
                unverified.Add($"binary_truncated — запись треугольника {index} оборвана");
                break;
            }

            // The normal occupies the first 12 bytes; the three vertices follow at 12, 24 and 36.
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var offset = 12 + vertex * 12;
                for (var axis = 0; axis < 3; axis++)
                {
                    var value = BitConverter.ToSingle(record, offset + axis * 4);
                    if (!double.IsFinite(value))
                    {
                        continue;
                    }

                    boxRead = true;
                    min[axis] = Math.Min(min[axis], value);
                    max[axis] = Math.Max(max[axis], value);
                }
            }
        }

        return new StlFacts(length, fromSize, StlExportPlan.BinaryCountSource, min, max, boxRead, unverified);
    }

    private static StlFacts InspectText(string path, long length)
    {
        var unverified = new List<string>
        {
            // The exact byte length of a text STL depends on how the writer formats numbers, so the count
            // is compared with an analytic reference rather than with the file size.
            "text_file_size_is_formatting_dependent — размер текстового STL зависит от форматирования",
        };

        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        var boxRead = false;
        long count = 0;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }

            if (!trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                unverified.Add("text_vertex_line_unparsed — строка vertex короче трёх координат");
                continue;
            }

            for (var axis = 0; axis < 3; axis++)
            {
                if (!double.TryParse(parts[axis + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var value) || !double.IsFinite(value))
                {
                    continue;
                }

                boxRead = true;
                min[axis] = Math.Min(min[axis], value);
                max[axis] = Math.Max(max[axis], value);
            }
        }

        if (count == 0)
        {
            unverified.Add("text_no_facet_lines — в файле нет строк facet normal: счёт треугольников не подтверждён");
        }

        return new StlFacts(length, count, StlExportPlan.TextCountSource, min, max, boxRead, unverified);
    }
}

using System.Buffers.Binary;
using System.IO.Compression;

namespace KompasMcp.P0Probe;

/// <summary>A decoded 8-bit RGB raster: width, height and three bytes per pixel.</summary>
internal sealed record PngImage(int Width, int Height, byte[] Rgb);

/// <summary>Minimal PNG reader for the probe's pixel comparison.</summary>
/// <remarks>INVARIANT: only the two shapes KOMPAS actually emits are supported — 8-bit truecolour
/// (colour type 2) and 8-bit greyscale (type 0). Anything else is a NAMED refusal, never a guess:
/// a decoder that silently mis-reads a colour type would report "pixels differ" as a fact about
/// the kernel when it is a fact about this instrument.
/// LIMIT: no interlacing, no palette, no 16-bit, no alpha. The probe records the refusal reason
/// and the caller keeps the verdict unknown for that comparison.
/// History: docs/decisions/probes.md#png-pixels</remarks>
internal static class PngPixels
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Decode, or null with a reason in <paramref name="failure"/>.</summary>
    public static PngImage? TryDecode(byte[] data, out string? failure)
    {
        failure = null;
        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            failure = "это не PNG: сигнатура не совпала";
            return null;
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        var idat = new MemoryStream();
        var offset = Signature.Length;
        var sawHeader = false;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > data.Length)
            {
                failure = $"блок длиной {length} выходит за конец файла";
                return null;
            }

            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            var payload = data.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
                    bitDepth = payload[8];
                    colorType = payload[9];
                    sawHeader = true;
                    break;
                case "IDAT":
                    idat.Write(payload);
                    break;
                case "IEND":
                    offset = data.Length;
                    continue;
            }

            offset += 12 + length;
        }

        if (!sawHeader || width <= 0 || height <= 0)
        {
            failure = "IHDR не прочитан или габарит неположителен";
            return null;
        }

        if (bitDepth != 8 || (colorType != 2 && colorType != 0))
        {
            failure = $"глубина {bitDepth}, тип цвета {colorType}: прибор разбирает только 8-бит truecolour и greyscale";
            return null;
        }

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            inflate.CopyTo(plain);
            raw = plain.ToArray();
        }
        catch (InvalidDataException ex)
        {
            failure = "IDAT не распаковался: " + ex.Message;
            return null;
        }

        var channels = colorType == 2 ? 3 : 1;
        var stride = width * channels;
        if (raw.Length < (stride + 1) * height)
        {
            failure = $"распакованных байт {raw.Length}, ожидалось не меньше {(stride + 1) * height}";
            return null;
        }

        var rgb = new byte[width * height * 3];
        var previous = new byte[stride];
        var line = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            Unfilter(filter, line, previous, channels);
            for (var x = 0; x < width; x++)
            {
                var value = colorType == 2
                    ? (line[x * 3], line[x * 3 + 1], line[x * 3 + 2])
                    : (line[x], line[x], line[x]);
                var target = (y * width + x) * 3;
                rgb[target] = value.Item1;
                rgb[target + 1] = value.Item2;
                rgb[target + 2] = value.Item3;
            }

            Array.Copy(line, previous, stride);
        }

        return new PngImage(width, height, rgb);
    }

    /// <summary>Undo one PNG scanline filter in place (RFC 2083 §6). Filter 4 (Paeth) is included
    /// because the kernel's encoder chooses per line.</summary>
    private static void Unfilter(byte filter, byte[] line, byte[] previous, int channels)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var left = i >= channels ? line[i - channels] : 0;
            var up = previous[i];
            var upLeft = i >= channels ? previous[i - channels] : 0;
            line[i] = filter switch
            {
                0 => line[i],
                1 => (byte)(line[i] + left),
                2 => (byte)(line[i] + up),
                3 => (byte)(line[i] + (left + up) / 2),
                4 => (byte)(line[i] + Paeth(left, up, upLeft)),
                _ => line[i],
            };
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var dl = Math.Abs(estimate - left);
        var du = Math.Abs(estimate - up);
        var dul = Math.Abs(estimate - upLeft);
        return dl <= du && dl <= dul ? left : du <= dul ? up : upLeft;
    }

    /// <summary>Number of differing pixels and the first few coordinates, so "differ" is located,
    /// not merely asserted.</summary>
    public static (int Differing, string? FirstCoords) Compare(PngImage first, PngImage second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            return (-1, null);
        }

        var differing = 0;
        var coords = new List<string>(8);
        for (var i = 0; i < first.Rgb.Length; i += 3)
        {
            if (first.Rgb[i] == second.Rgb[i] && first.Rgb[i + 1] == second.Rgb[i + 1]
                && first.Rgb[i + 2] == second.Rgb[i + 2])
            {
                continue;
            }

            differing++;
            if (coords.Count < 8)
            {
                var pixel = i / 3;
                coords.Add($"({pixel % first.Width},{pixel / first.Width})");
            }
        }

        return (differing, coords.Count == 0 ? null : string.Join(" ", coords));
    }
}

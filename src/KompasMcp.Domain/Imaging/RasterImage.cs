using System.Globalization;

namespace KompasMcp.Domain.Imaging;

/// <summary>Raster format: the wire name, the vendor enum code and the traits by which the file is
/// IDENTIFIED rather than taken on trust.</summary>
/// <remarks>DOC: codes come from the product enum (<c>ksRasterFormatEnum</c>, page
/// <c>ksrasterformatenum.html</c>): BMP = 0, JPG = 2, PNG = 3, TIF = 4. They are part of the contract with
/// the kernel, so they live HERE, not in the adapter. LIMIT: WMF is deliberately absent — DOC
/// (<c>ksdocument3d_saveastorasterformat.html</c>) says WMF saving is unsupported and the file is written as
/// EMF; publishing a format the kernel silently swaps is a promise the server does not keep.</remarks>
public sealed record RasterFormatSpec(
    string Wire,
    short Code,
    string Extension,
    string MimeType,
    byte[] Magic,
    bool MagicIsPrefix);

/// <summary>The published raster formats and their identifying traits.</summary>
public static class RasterFormats
{
    public const short CodeBmp = 0;

    public const short CodeJpg = 2;

    public const short CodePng = 3;

    public const short CodeTif = 4;

    public static readonly RasterFormatSpec Png = new(
        "png", CodePng, ".png", "image/png",
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, MagicIsPrefix: true);

    public static readonly RasterFormatSpec Jpg = new(
        "jpg", CodeJpg, ".jpg", "image/jpeg",
        new byte[] { 0xFF, 0xD8, 0xFF }, MagicIsPrefix: true);

    public static readonly RasterFormatSpec Bmp = new(
        "bmp", CodeBmp, ".bmp", "image/bmp",
        new byte[] { 0x42, 0x4D }, MagicIsPrefix: true);

    public static readonly RasterFormatSpec Tif = new(
        "tif", CodeTif, ".tif", "image/tiff",
        new byte[] { 0x49, 0x49, 0x2A, 0x00 }, MagicIsPrefix: true);

    public static readonly IReadOnlyList<RasterFormatSpec> All = new[] { Png, Jpg, Bmp, Tif };

    /// <summary>Names published by the schema, in list order.</summary>
    public static readonly IReadOnlyList<string> WireNames =
        All.Select(spec => spec.Wire).ToArray();

    /// <summary>Resolve a format by name. An unknown name is a refusal, not "PNG by default": a silent
    /// format swap is indistinguishable to the caller from honouring the request.</summary>
    public static bool TryResolve(string? wire, out RasterFormatSpec spec)
    {
        spec = Png;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Wire, wire, StringComparison.OrdinalIgnoreCase))
            {
                spec = candidate;
                return true;
            }
        }

        return false;
    }

    public static RasterFormatSpec? ByCode(short code) =>
        All.FirstOrDefault(spec => spec.Code == code);
}

/// <summary>Response-context limits, kept in one place: the client reads the image through the model
/// context, and an oversized image is a transport refusal, not a convenience.</summary>
public static class RasterLimits
{
    /// <summary>Long side of the image in pixels.</summary>
    public const int MaxLongSidePixels = 1600;

    /// <summary>Limit of the base64 image in the response (2 MiB).</summary>
    public const int MaxBase64Characters = 2 * 1024 * 1024;

    /// <summary>Lower bound of the caller-supplied target long side. Below it the snapshot stops being
    /// an illustration of the model; the number is named so a refusal can say what it clamps to.</summary>
    public const int MinLongSidePixels = 16;

    /// <summary>Target long side used when the caller named none.</summary>
    public const int DefaultLongSidePixels = 1024;

    /// <summary>Upper bound of the scale the server computes itself. MEASURED: <c>extScale=10</c>
    /// renders (596×502 on a 100×80 plate), <c>100</c> answers RASTER_EMPTY and <c>1000</c>
    /// RASTER_REFUSED — the kernel does not accept an arbitrary multiplier.</summary>
    public const double MaxAutoScale = 10.0;

    /// <summary>Lower bound of the computed scale. MEASURED: <c>extScale=0.0001</c> renders a 1×1
    /// image; 0 and negatives are refused by the schema before COM.</summary>
    public const double MinAutoScale = 0.0001;
}

/// <summary>What could be read FROM THE FILE ITSELF. Dimensions come from the header, not from the request:
/// the parameter says what was asked, the header says what came out.</summary>
public sealed record RasterImageFacts(
    bool MagicMatches,
    string MagicHex,
    int? PixelWidth,
    int? PixelHeight,
    int? BitDepth,
    int? ColorType,
    string? DimensionNote);

/// <summary>Header parsing for BMP/PNG/JPG/TIF, without external libraries.</summary>
/// <remarks>The order requires the response to carry <c>pixel_width</c> and <c>pixel_height</c> read from
/// the IHDR by the server. A dimension from the request is a claim about the INTENT; one from the header is
/// a claim about the FILE — and the second is published.</remarks>
public static class RasterImageReader
{
    private const int PngSignatureLength = 8;

    public static RasterImageFacts Inspect(byte[] data, RasterFormatSpec spec)
    {
        var magicLength = Math.Min(spec.Magic.Length, data.Length);
        var magicMatches = magicLength == spec.Magic.Length;
        for (var i = 0; magicMatches && i < magicLength; i++)
        {
            magicMatches = data[i] == spec.Magic[i];
        }

        var magicHex = Hex(data, 16);

        if (spec.Code == RasterFormats.CodePng)
        {
            return InspectPng(data, magicMatches, magicHex);
        }

        if (spec.Code == RasterFormats.CodeBmp)
        {
            return InspectBmp(data, magicMatches, magicHex);
        }

        // JPG and TIF: the magic is checked, the dimensions are not. A silent zero would be a lie, so the
        // field stays null and the reason is stated.
        return new RasterImageFacts(
            magicMatches, magicHex, null, null, null, null,
            spec.Code == RasterFormats.CodeJpg
                ? "габарит JPEG не разбирается: в ответе он остаётся непрочитанным, а не нулевым"
                : "габарит TIFF не разбирается: в ответе он остаётся непрочитанным, а не нулевым");
    }

    private static RasterImageFacts InspectPng(byte[] data, bool magicMatches, string magicHex)
    {
        // IHDR must be the first chunk: signature (8) + length (4) + type (4) + width (4) + height (4).
        var headerReadable = data.Length >= PngSignatureLength + 12 + 8
            && data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R';

        if (!headerReadable)
        {
            return new RasterImageFacts(
                magicMatches, magicHex, null, null, null, null,
                "блок IHDR не найден: габарит остаётся непрочитанным, а не нулевым");
        }

        var width = ReadInt32BigEndian(data, 16);
        var height = ReadInt32BigEndian(data, 20);
        return new RasterImageFacts(
            magicMatches, magicHex, width, height, data[24], data[25], null);
    }

    private static RasterImageFacts InspectBmp(byte[] data, bool magicMatches, string magicHex)
    {
        // BITMAPINFOHEADER: offset 14 — header size, 18 — width, 22 — height (both int32 LE).
        if (data.Length < 26)
        {
            return new RasterImageFacts(
                magicMatches, magicHex, null, null, null, null,
                "заголовок BMP короче BITMAPINFOHEADER: габарит остаётся непрочитанным");
        }

        var width = ReadInt32LittleEndian(data, 18);
        var height = ReadInt32LittleEndian(data, 22);
        return new RasterImageFacts(
            magicMatches, magicHex,
            width, Math.Abs(height), data.Length >= 30 ? ReadUInt16LittleEndian(data, 28) : null,
            null,
            height < 0 ? "высота BMP отрицательна (строки снизу вверх); в ответе — модуль" : null);
    }

    private static int ReadInt32BigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static int ReadInt32LittleEndian(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static int ReadUInt16LittleEndian(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8);

    private static string Hex(byte[] data, int count)
    {
        var take = Math.Min(count, data.Length);
        var builder = new System.Text.StringBuilder(take * 2);
        for (var i = 0; i < take; i++)
        {
            builder.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

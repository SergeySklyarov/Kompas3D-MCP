using System.Globalization;

namespace KompasMcp.Domain.Imaging;

/// <summary>
/// Растровый формат: имя на проводе, код вендорского перечисления и признаки, по которым файл
/// ОПОЗНАЁТСЯ, а не принимается на слово.
/// </summary>
/// <remarks>
/// Коды взяты из перечисления продукта (<c>ksRasterFormatEnum</c>, страница
/// <c>ksrasterformatenum.html</c>): BMP = 0, JPG = 2, PNG = 3, TIF = 4. Значения перечисления —
/// часть контракта с ядром, поэтому они лежат ЗДЕСЬ, а не в адаптере: адаптер не должен уметь
/// «свой» набор кодов, расходящийся с тем, который проверяют тесты.
/// <para>
/// WMF в перечень НЕ входит намеренно. Справка (<c>ksdocument3d_saveastorasterformat.html</c>)
/// говорит, что сохранение в WMF не поддерживается и файл записывается в EMF; публиковать формат,
/// который ядро молча подменяет другим, — обещание, которое сервер не выполняет.
/// </para>
/// </remarks>
public sealed record RasterFormatSpec(
    string Wire,
    short Code,
    string Extension,
    string MimeType,
    byte[] Magic,
    bool MagicIsPrefix);

/// <summary>Перечень публикуемых растровых форматов и их опознавательные признаки.</summary>
public static class RasterFormats
{
    /// <summary>ksRasterFormatEnum: BMP = 0.</summary>
    public const short CodeBmp = 0;

    /// <summary>ksRasterFormatEnum: JPG = 2.</summary>
    public const short CodeJpg = 2;

    /// <summary>ksRasterFormatEnum: PNG = 3.</summary>
    public const short CodePng = 3;

    /// <summary>ksRasterFormatEnum: TIF = 4.</summary>
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

    /// <summary>Имена, публикуемые схемой. Порядок — как в перечне.</summary>
    public static readonly IReadOnlyList<string> WireNames =
        All.Select(spec => spec.Wire).ToArray();

    /// <summary>
    /// Разрешить формат по имени. Неизвестное имя — отказ, а не «возьмём PNG по умолчанию»:
    /// молчаливая подмена формата неотличима для вызывающего от исполнения просьбы.
    /// </summary>
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

    /// <summary>Разрешить формат по коду вендорского перечисления (обратное направление).</summary>
    public static RasterFormatSpec? ByCode(short code) =>
        All.FirstOrDefault(spec => spec.Code == code);
}

/// <summary>
/// Ограничения контекста ответа. Названы числами и живут в одном месте: клиент читает картинку
/// через контекст модели, и «картинка на 40 мегапикселей» — это не удобство, а отказ транспорта.
/// </summary>
public static class RasterLimits
{
    /// <summary>Большая сторона снимка в пикселях.</summary>
    public const int MaxLongSidePixels = 1600;

    /// <summary>Предел base64-представления картинки в ответе (2 МиБ).</summary>
    public const int MaxBase64Characters = 2 * 1024 * 1024;
}

/// <summary>
/// Что удалось прочитать ИЗ САМОГО ФАЙЛА. Габариты берутся разбором заголовка, а не «на глаз» и не
/// из параметров запроса: параметр говорит, что просили, а заголовок — что получилось.
/// </summary>
public sealed record RasterImageFacts(
    bool MagicMatches,
    string MagicHex,
    int? PixelWidth,
    int? PixelHeight,
    int? BitDepth,
    int? ColorType,
    string? DimensionNote);

/// <summary>
/// Разбор заголовков BMP/PNG/JPG/TIF без внешних библиотек.
/// </summary>
/// <remarks>
/// Зачем это в продукте, а не «на глаз». Наряд требует от ответа <c>pixel_width</c> и
/// <c>pixel_height</c>, прочитанные из IHDR сервером. Габарит, взятый из запроса, был бы
/// утверждением о НАМЕРЕНИИ; габарит из заголовка — утверждением о ФАЙЛЕ. Второе и публикуется.
/// </remarks>
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

        // JPG и TIF: магия проверяется, габарит — нет. Молчаливый ноль вместо непрочитанного
        // габарита был бы ложью, поэтому поле остаётся null, а причина названа словами.
        return new RasterImageFacts(
            magicMatches, magicHex, null, null, null, null,
            spec.Code == RasterFormats.CodeJpg
                ? "габарит JPEG не разбирается: в ответе он остаётся непрочитанным, а не нулевым"
                : "габарит TIFF не разбирается: в ответе он остаётся непрочитанным, а не нулевым");
    }

    private static RasterImageFacts InspectPng(byte[] data, bool magicMatches, string magicHex)
    {
        // IHDR обязан быть первым блоком: подпись (8) + длина (4) + тип (4) + ширина (4) + высота (4).
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
        // BITMAPINFOHEADER: смещение 14 — размер заголовка, 18 — ширина, 22 — высота (обе int32 LE).
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

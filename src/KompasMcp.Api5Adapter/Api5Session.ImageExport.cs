using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Imaging;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Растровый снимок модели документированным маршрутом API5 (наряд
/// <c>KOMPAS_EXPORT_IMAGE_DEVELOPER_PROMPT.md</c> §4).
/// </summary>
/// <remarks>
/// <b>Маршрут.</b> <c>ksDocument3D.RasterFormatParam()</c> (страница
/// <c>ksdocument3d_rasterformatparam.html</c>) → <c>ksRasterFormatParam</c>
/// (<c>ksrasterformatparam_props.html</c>) → <c>Init()</c> → запись <c>format</c>, <c>colorBPP</c>,
/// при необходимости <c>extResolution</c>/<c>extScale</c> и <c>returnResultAsArrayBytes</c> →
/// <c>ksDocument3D.SaveAsToRasterFormat(fileName, rasterPar)</c>
/// (<c>ksdocument3d_saveastorasterformat.html</c>).
/// <para>
/// <b>Два режима и их взаимная исключительность — ИЗМЕРЕНО, а не предположено</b> (проба P2b
/// наряда, поставка <c>publish-deproutes-r2-20260921</c>, сборка 24.0.0.2799):
/// </para>
/// <list type="bullet">
/// <item>непустое имя файла → файл записан (PNG 8639 байт, 328×448), а <c>resultArrayBytes</c>
/// остаётся <c>null</c>; шесть форм вызова (пустой предварительно подставленный массив, другой
/// метод записи растра, повторное чтение свойства, свежий объект параметров после записи) все дали
/// <c>null</c>;</item>
/// <item>ПУСТОЕ имя файла → <c>resultArrayBytes</c> = <c>System.Byte[]</c>, 8639 байт, магия PNG
/// <c>89504e47…</c>, и файла на диске НЕ появляется вовсе (снимок каталога до/после).</item>
/// </list>
/// <para>
/// Отсюда и устройство метода: «вернуть картинку» и «записать файл» — РАЗНЫЕ вызовы маршрута.
/// Когда нужны оба, рендер делается ОДИН раз байтовым режимом, а файл пишется из тех же байтов:
/// второй рендер мог бы дать другой результат, а обещание «файл и ответ — одно и то же» без этого
/// не проверяемо.
/// </para>
/// <para>
/// <b>Чего этот метод не делает.</b> Не управляет проекцией (<c>IViewProjection7</c>
/// документирован, но оставлен отдельным нарядом и назван остатком), не ужимает картинку молча и
/// не выдаёт «успех» без байтов. Снимок — текущий вид окна сервера; состояние камеры не
/// фиксируется и в ответе названо неподтверждённым.
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Глубина цвета снимка. 24 бита — то, что измерено в пробах P1–P6.</summary>
    private const short RasterColorBitsPerPixel = 24;

    /// <summary>
    /// Снять растр документа. Один рендер на вызов; носитель выбирается по запросу, а не по
    /// удобству: байтовый режим — пустое имя файла, файловый — непустое.
    /// </summary>
    public ExportImageResultDto ExportImage(ExportImageCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (command.ExpectedRevision != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Снимок запрошен для ревизии {command.ExpectedRevision}, у документа {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["requested_revision"] = command.ExpectedRevision,
                    ["current_revision"] = document.Revision,
                });
        }

        if (!RasterFormats.TryResolve(command.Format, out var format))
        {
            // Перечень проверяется ДО COM и здесь, а не только схемой: измерено (проба P5), что
            // ядро значение вне перечня НЕ отвергает — оно принимается и даёт другой формат
            // (значение 99 дало BMP 440886 байт при запросе PNG). Молчаливая подмена формата
            // неотличима для вызывающего от исполнения просьбы.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Формат '{command.Format}' не входит в опубликованный перечень: " +
                string.Join(", ", RasterFormats.WireNames) + ".",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["format"] = command.Format,
                    ["published_formats"] = RasterFormats.WireNames,
                });
        }

        if (!command.ReturnImageContent && command.SavePath is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Запрос не просит ни картинки (return_image_content=false), ни файла (save_path не задан): " +
                "снимок некуда положить, и «успех» без результата был бы ложью.",
                RetryPolicy.Never);
        }

        var bytesRoute = command.ReturnImageContent;
        var fileName = bytesRoute ? string.Empty : command.SavePath!;
        if (!bytesRoute)
        {
            // Файловый режим: каталог создаётся заранее, потому что измерено (проба P4), что ядро
            // создаёт его само и пишет файл — то есть «путь не существует» не является отказом,
            // и полагаться на это как на защиту нельзя. Проверка каталога — забота Хоста.
            var directory = Path.GetDirectoryName(command.SavePath!);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        ksRasterFormatParam? parameter = null;
        byte[]? data = null;
        try
        {
            parameter = (ksRasterFormatParam)document.Document.RasterFormatParam();
            if (parameter is null)
            {
                throw new KompasContractException(
                    ErrorCodes.RasterRefused,
                    "RasterFormatParam() вернул null: документ не отдал объект параметров растра.",
                    RetryPolicy.SameOperationId);
            }

            parameter.Init();
            parameter.format = format.Code;
            parameter.colorBPP = RasterColorBitsPerPixel;
            if (command.Resolution is int resolution)
            {
                parameter.extResolution = resolution;
            }

            if (command.Scale is double scale)
            {
                parameter.extScale = scale;
            }

            if (bytesRoute)
            {
                parameter.returnResultAsArrayBytes = true;
            }

            bool returned;
            try
            {
                returned = document.Document.SaveAsToRasterFormat(fileName, parameter);
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.RasterRefused,
                    $"SaveAsToRasterFormat бросил исключение: {ex.Message}",
                    RetryPolicy.SameOperationId,
                    partialEffects: File.Exists(fileName),
                    hresult: ex.HResult,
                    details: new Dictionary<string, object?>
                    {
                        ["format"] = format.Wire,
                        ["route"] = bytesRoute ? "memory" : "file",
                    });
            }

            if (!returned)
            {
                throw new KompasContractException(
                    ErrorCodes.RasterRefused,
                    "SaveAsToRasterFormat вернул false: ядро отказало в снимке.",
                    RetryPolicy.SameOperationId,
                    partialEffects: File.Exists(fileName),
                    details: new Dictionary<string, object?>
                    {
                        ["format"] = format.Wire,
                        ["route"] = bytesRoute ? "memory" : "file",
                    });
            }

            if (bytesRoute)
            {
                data = ReadArrayBytes(parameter);
            }
            else
            {
                data = ReadFileBytes(command.SavePath!);
            }
        }
        finally
        {
            ComApartment.Release(parameter);
        }

        if (data is null || data.Length == 0)
        {
            throw new KompasContractException(
                ErrorCodes.RasterEmpty,
                bytesRoute
                    ? "Байтовый режим заявлен успешным, но resultArrayBytes пуст: картинки нет."
                    : "Файловый режим заявлен успешным, но файла нет или он пуст: картинки нет.",
                RetryPolicy.SameOperationId,
                partialEffects: command.SavePath is not null && File.Exists(command.SavePath),
                details: new Dictionary<string, object?>
                {
                    ["format"] = format.Wire,
                    ["route"] = bytesRoute ? "memory" : "file",
                    ["file_name_passed"] = bytesRoute ? "<пустое имя: байтовый режим>" : fileName,
                });
        }

        var facts = RasterImageReader.Inspect(data, format);
        if (!facts.MagicMatches)
        {
            throw new KompasContractException(
                ErrorCodes.RasterFormatMismatch,
                $"Содержимое не соответствует формату {format.Wire}: магия {facts.MagicHex}, " +
                $"ожидалась {Convert.ToHexString(format.Magic).ToLowerInvariant()}.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["format"] = format.Wire,
                    ["observed_magic_hex"] = facts.MagicHex,
                    ["expected_magic_hex"] = Convert.ToHexString(format.Magic).ToLowerInvariant(),
                });
        }

        // Файл из ТЕХ ЖЕ байтов: один рендер, не два. Второй вызов ядра мог бы дать другой кадр,
        // и тогда «в ответе и в файле одно и то же» перестало бы быть проверяемым утверждением.
        bool? savePathFromMemory = null;
        if (bytesRoute && command.SavePath is { Length: > 0 } savePath)
        {
            var directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(savePath, data);
            savePathFromMemory = true;
        }

        string? base64 = null;
        if (command.ReturnImageContent)
        {
            EnforceLimits(facts, data, format);
            base64 = Convert.ToBase64String(data);
        }

        var unverified = new List<string>
        {
            "view_state_not_captured — снят текущий вид окна сервера; состояние камеры не фиксировалось, "
            + "и снимок не является подтверждением ориентации геометрии",
            "image_content_not_analysed — сервер не разбирает изображение и не сверяет его с моделью",
        };
        if (facts.PixelWidth is null)
        {
            unverified.Add("pixel_size_not_read — габарит из заголовка не прочитан (формат не разбирается)");
        }

        return new ExportImageResultDto
        {
            Format = format.Wire,
            MimeType = format.MimeType,
            PixelWidth = facts.PixelWidth,
            PixelHeight = facts.PixelHeight,
            BytesCount = data.LongLength,
            SavePath = command.SavePath,
            RasterRoute = bytesRoute ? "memory" : "file",
            SavePathFromMemory = savePathFromMemory,
            ViewNote = "текущий вид окна сервера; управление видом вне этого инструмента",
            ImageBase64 = base64,
            ReachedLevel = facts.PixelWidth is null ? VerificationLevel.FileCreated : VerificationLevel.SyntaxChecked,
            UnverifiedAspects = unverified,
        };
    }

    /// <summary>
    /// Ограничения контекста ответа. Превышение — ИМЕНОВАННЫЙ отказ с подсказкой, а не молчаливое
    /// ужатие: уменьшение картинки сервером было бы подменой результата, а не его доставкой.
    /// </summary>
    private static void EnforceLimits(RasterImageFacts facts, byte[] data, RasterFormatSpec format)
    {
        if (facts.PixelWidth is int width && facts.PixelHeight is int height)
        {
            var longSide = Math.Max(width, height);
            if (longSide > RasterLimits.MaxLongSidePixels)
            {
                throw new KompasContractException(
                    ErrorCodes.RasterLimitExceeded,
                    $"Снимок {width}×{height} превышает предел большей стороны " +
                    $"{RasterLimits.MaxLongSidePixels} пикселей. Уменьшите resolution.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["pixel_width"] = width,
                        ["pixel_height"] = height,
                        ["max_long_side_pixels"] = RasterLimits.MaxLongSidePixels,
                        ["suggested_max_resolution"] =
                            (int)Math.Floor(RasterLimits.MaxLongSidePixels * (double)Math.Min(width, height) / longSide),
                    });
            }
        }

        // 4/3 — отношение длины base64 к длине исходных байтов (каждые 3 байта дают 4 символа).
        var base64Length = 4 * ((data.Length + 2L) / 3);
        if (base64Length > RasterLimits.MaxBase64Characters)
        {
            throw new KompasContractException(
                ErrorCodes.RasterLimitExceeded,
                $"Картинка в base64 заняла бы {base64Length} символов и превышает предел " +
                $"{RasterLimits.MaxBase64Characters}. Уменьшите resolution или запросите save_path без картинки.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["bytes_count"] = data.Length,
                    ["base64_characters"] = base64Length,
                    ["max_base64_characters"] = RasterLimits.MaxBase64Characters,
                    ["format"] = format.Wire,
                });
        }
    }

    /// <summary>
    /// Прочитать <c>resultArrayBytes</c>. Тип члена в interop — <c>Object</c> (VARIANT), поэтому
    /// ветвей несколько: SAFEARRAY байт маршалится и как <c>byte[]</c>, и как <c>Array</c>.
    /// «Не привелось» отличается от «пусто» — первое отказ прибора, второе факт о ядре.
    /// </summary>
    private static byte[]? ReadArrayBytes(ksRasterFormatParam parameter)
    {
        object? raw;
        try
        {
            raw = parameter.resultArrayBytes;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.RasterEmpty,
                $"Чтение resultArrayBytes бросило исключение: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult);
        }

        switch (raw)
        {
            case null:
                return null;
            case byte[] typed:
                return typed;
            case Array array when array.Rank == 1:
            {
                var buffer = new byte[array.Length];
                for (var i = 0; i < array.Length; i++)
                {
                    if (array.GetValue(i) is not { } item)
                    {
                        return null;
                    }

                    buffer[i] = Convert.ToByte(item, CultureInfo.InvariantCulture);
                }

                return buffer;
            }
            default:
                throw new KompasContractException(
                    ErrorCodes.RasterEmpty,
                    $"resultArrayBytes вернул {raw.GetType().FullName}, который не приводится к байтам.",
                    RetryPolicy.SameOperationId);
        }
    }

    private static byte[] ReadFileBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new KompasContractException(
                ErrorCodes.RasterEmpty,
                $"Файл снимка не прочитан: {ex.Message}",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(path));
        }
    }
}

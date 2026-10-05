using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Imaging;

namespace KompasMcp.Api5Adapter;

/// <summary>Raster snapshot of the model via the documented API5 route (order §4).</summary>
/// <remarks>MEASURED: the two modes are mutually exclusive — a non-empty name writes the file with
/// <c>resultArrayBytes</c> null, an empty name gives <c>resultArrayBytes</c> and no file on disk, so
/// "return an image" and "write a file" are DIFFERENT route calls; with both wanted the render runs
/// ONCE in byte mode. LIMIT: no silent downscale, no "success" without bytes.
/// The projection IS controlled now, by <c>ksViewProjectionCollection</c> + <c>SetCurrent</c> +
/// <c>refresh</c>; the camera beyond the projection is unverified.
/// History: docs/decisions/adapter-core.md#image-export</remarks>
public sealed partial class Api5Session
{
    /// <summary>Snapshot colour depth. 24 bits is what probes P1–P6 measured.</summary>
    private const short RasterColorBitsPerPixel = 24;

    /// <summary>Take a raster of the document. One render per call; the carrier is chosen by the request,
    /// not by convenience: byte mode — empty file name, file mode — non-empty.</summary>
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
            // The list is checked BEFORE COM, here and not only in the schema: MEASURED (probe P5) — the
            // kernel does NOT reject a value outside the list; it accepts it and gives a different format
            // (the value 99 produced a 440886-byte BMP for a PNG request). A silent format substitution is
            // indistinguishable to the caller from honouring the request.
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

        ViewProjectionSpec? requestedView = null;
        if (command.View is { Length: > 0 } requestedViewName)
        {
            if (!ViewProjections.TryResolve(requestedViewName, out var resolved))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Проекция '{requestedViewName}' не входит в опубликованный перечень: " +
                    string.Join(", ", ViewProjections.WireNames) + ".",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["view"] = requestedViewName,
                        ["published_views"] = ViewProjections.WireNames,
                    });
            }

            requestedView = resolved;
        }

        if (command.KeepView && requestedView is null)
        {
            // keep_view without view would express "keep the view I did not ask to change" — a promise
            // about nothing. Named refusal instead of silently ignoring the field.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "keep_view задан без view: сохранять нечего, потому что вид не запрашивался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["keep_view"] = true });
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
            // File mode: the directory is created ahead of time because MEASURED (probe P4) — the kernel
            // creates it itself and writes the file, so "the path does not exist" is not a refusal, and it
            // cannot be relied on as a guard. Directory checking is the Host's concern.
            var directory = Path.GetDirectoryName(command.SavePath!);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        ksRasterFormatParam? parameter = null;
        byte[]? data = null;
        var viewState = ViewSwapState.NotRequested;
        try
        {
            // The view is applied BEFORE the render and restored AFTER it. The swap is a separate object
            // so that the restore happens in a `finally` even when the render refuses: a caller must not
            // lose the window's view because a snapshot failed.
            if (requestedView is not null)
            {
                viewState = ViewSwap.Apply(document, requestedView, command.KeepView);
            }

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
            // The restore belongs to the `finally`: a refusal inside the render must not leave the
            // document showing a view the caller asked for only to take one picture of it.
            //
            // keep_view is the caller SAYING "leave it": with a CONFIRMED switch the restore is not
            // called at all. `view_restored` stays null and no "not restored" note reaches
            // `unverified_aspects` — there is nothing to verify, and the note would be a false alarm.
            // SwitchAttempted is different: the window may have moved without the label agreeing, so
            // that path restores even under keep_view and NAMES the outcome.
            var confirmedConsent = viewState.Applied && viewState.KeepView;
            if (!confirmedConsent && (viewState.Applied || viewState.SwitchAttempted))
            {
                ViewSwap.Restore(document, viewState, out var restored, out var restoreNote);
                viewState.Restored = restored;
                viewState.RestoreNote = restoreNote;
            }

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

        // The file comes from the SAME bytes: one render, not two. A second kernel call could give a
        // different frame, and then "the response and the file are the same" would stop being a
        // verifiable claim.
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
            "image_content_not_analysed — сервер не разбирает изображение и не сверяет его с моделью",
        };
        if (viewState.Applied)
        {
            unverified.Add(
                "view_identity_not_proved — применённая проекция подтверждена ТИПОМ, прочитанным обратно "
                + "из коллекции; совпадение картинки с ожидаемой проекцией не измерялось");
        }
        else
        {
            unverified.Add(
                "view_state_not_captured — снят текущий вид окна сервера; проекция не запрашивалась, "
                + "и снимок не является подтверждением ориентации геометрии");
        }

        if (facts.PixelWidth is null)
        {
            unverified.Add("pixel_size_not_read — габарит из заголовка не прочитан (формат не разбирается)");
        }

        if (viewState.RestoreNote is { Length: > 0 } note)
        {
            unverified.Add(note);
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
            ViewNote = viewState.Note,
            RequestedView = requestedView?.Wire,
            AppliedView = viewState.AppliedView,
            PreviousView = viewState.PreviousView,
            ViewRestored = viewState.Restored,
            ViewProjectionScheme = viewState.Scheme,
            ImageBase64 = base64,
            ReachedLevel = facts.PixelWidth is null ? VerificationLevel.FileCreated : VerificationLevel.SyntaxChecked,
            UnverifiedAspects = unverified,
        };
    }

    /// <summary>Response context limits. Exceeding them is a NAMED refusal with a hint, not silent
    /// downscaling: shrinking the image server-side would substitute the result, not deliver it.</summary>
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

        // 4/3 — the ratio of base64 length to source byte length (every 3 bytes give 4 characters).
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

    /// <summary>Read <c>resultArrayBytes</c>. The member's interop type is <c>Object</c> (VARIANT), so there
    /// are several branches: a byte SAFEARRAY marshals both as <c>byte[]</c> and as <c>Array</c>. "Did not
    /// cast" differs from "empty" — the former is an instrument failure, the latter a fact about the
    /// kernel.</summary>
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

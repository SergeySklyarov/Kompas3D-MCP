using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.SheetMetal;

namespace KompasMcp.Api5Adapter;

/// <summary>Flat pattern of a sheet-metal part as a DXF file (profile <c>sheet-metal-minimal-v1</c>).</summary>
/// <remarks>DOC: <c>iviews_addstandartviews.html</c> — the view is built from a FILE;
/// <c>iassociationview_projectionname.html</c> — «имя проекции из списка проекций в документе-источнике»;
/// <c>iassociationview_unfold.html</c> — «Листовая деталь в разогнутом виде» (BOOL);
/// <c>idrawingobject_update.html</c> — «Update необходимо вызвать для объекта, свойства которого были
/// изменены»; <c>iconverter_convert.html</c> — <c>Convert</c> returns 1 on success.
/// INVARIANT: the answer is the geometry inside the written DXF, never the fact that a file appeared —
/// <c>AddStandartViews</c> ignores the projection name and builds the front view (MEASURED).
/// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
public partial class Api5Session
{
    /// <summary>The product's name for the unfold projection, as the kernel gave it to the measured route.</summary>
    /// <remarks>MEASURED: <c>AddUnfoldProjection</c> answers a projection of type <c>ksVPUnfold</c> (9) named
    /// «Развертка». A different name is not fatal: the contour comparison catches a substituted front view.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private const string UnfoldProjectionName = "Развертка";

    /// <summary><c>ksViewProjectionType.ksVPUnfold</c>: the projection type that says «unfold».</summary>
    private const int UnfoldProjectionType = 9;

    /// <summary>Export the unfolded body as DXF through a temporary drawing.</summary>
    public ExportSheetMetalFlatDxfResult ExportSheetMetalFlatDxf(ExportSheetMetalFlatDxfCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (document.Kind != DocumentKind.Part)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Выгрузка развёртки применима к детали; документ «{document.Id}» имеет тип {document.Kind}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }

        // INVARIANT: the view is built from the part's FILE, so an unsaved part would export an older
        // model than the one the client is looking at. Saving is the client's call, not this tool's.
        if (document.Path is null || IsDirty(document))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentDirty,
                "Деталь не сохранена в файл или содержит несохранённые изменения, а вид развёртки строится "
                + "по СОХРАНЁННОМУ файлу детали. Сохраните деталь (kompas_save_document) и повторите.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["path"] = document.Path,
                    ["field"] = "document_id",
                });
        }

        if (!FlatDxfPolicy.IsDxfPath(command.OutputPath))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Расширение файла «{Path.GetExtension(command.OutputPath)}» не поддерживается: маршрут "
                + "выгружает чертёж в DXF, поэтому требуется путь с расширением .dxf.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "output_path" });
        }

        if (File.Exists(command.OutputPath) && !command.Overwrite)
        {
            throw new KompasContractException(
                ErrorCodes.PathNotAllowed,
                $"Файл '{command.OutputPath}' уже существует: перезапись без флага overwrite запрещена.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "output_path" });
        }

        var body = ResolveFlatBody(document, command.BodyRef);
        var (_, sheet) = RequireSheetMetal(document, "Выгрузка не выполнялась");
        var unfold = RequireUnfoldParameters(sheet, "Выгрузка не выполнялась");

        var unfolded = SafeBool(() => unfold.Unfold);
        var bends = ReadCountOf(() => sheet.SheetMetalBends.Count);
        var hasBends = bends is > 0;

        // The model's own extent in the sheet plane: the unfolded body IS the flat pattern, and a bent
        // body's box is the front view's size — the two facts the contour is judged against.
        var modelBox = FlatBoxOf(document, body);
        // INVARIANT: a sheet-metal body WITHOUT bends is already flat, so its box is the flat pattern even
        // though the unfold parameters were never created. A bent body is flat only while Unfold is true.
        var isFlat = unfolded == true || hasBends == false;
        var flatBox = isFlat ? modelBox : null;
        var bentBox = isFlat ? null : modelBox;

        var directory = Path.GetDirectoryName(command.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var application = RequireApplication7(document);
        var bridge = BridgeFor(document);
        var drawingPath = Path.Combine(
            Path.GetTempPath(), "kompas-flat-" + Guid.NewGuid().ToString("N") + ".cdw");
        var unverified = new List<string>();
        IReadOnlyList<double>? frame = null;
        IKompasDocument2D? drawing7 = null;
        try
        {
            drawing7 = application.Documents.Add(DocumentTypeEnum.ksDocumentDrawing, false) as IKompasDocument2D
                ?? throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Documents.Add(ksDocumentDrawing) не вернул IKompasDocument2D: временный чертёж для вида "
                    + "не создан, файл не выгружался.",
                    RetryPolicy.ReacquireContext);

            var drawing5 = bridge.TransferTo5(drawing7) as ksDocument2D;
            var views = (drawing7.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews
                ?? throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Временный чертёж не отдал коллекцию видов (IViews): вид развёртки не построен, файл не "
                    + "выгружался.",
                    RetryPolicy.ReacquireContext);

            var before = ViewNumbers(views);
            bool accepted;
            try
            {
                // vp_Front = 1 is the documented single front view (projectiontype.html): the orientation
                // is what the projection name changes, not the view type.
                accepted = views.AddStandartViews(
                    document.Path, UnfoldProjectionName, new object[] { 1 }, 100d, 100d, 1d, 10d, 10d);
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "AddStandartViews бросил исключение: " + ex.Message + ". Вид не построен.",
                    RetryPolicy.SameOperationId);
            }

            if (!accepted)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "AddStandartViews вернул false: ядро отказало в построении вида развёртки.",
                    RetryPolicy.SameOperationId);
            }

            var created = ViewNumbers(views).Except(before).ToArray();
            if (created.Length == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "AddStandartViews принят, но новых видов в чертеже нет: вид развёртки не появился.",
                    RetryPolicy.SameOperationId);
            }

            var number = created[0];
            ApplyUnfoldView(views, number, unverified);
            frame = ReadGabaritOfView(drawing5, document, number);
            if (frame is null)
            {
                unverified.Add("view_frame_not_read — рамка вида не прочитана (ksGetObjGabaritRect): контур "
                    + "измерен по всему файлу, вместе с рамкой листа и основной надписью");
            }

            drawing7.SaveAs(drawingPath);
            if (!File.Exists(drawingPath))
            {
                throw new KompasContractException(
                    ErrorCodes.ExportFailed,
                    "Временный чертёж не сохранился в файл: конвертер берёт входной файл по пути, выгрузка "
                    + "невозможна.",
                    RetryPolicy.SameOperationId);
            }

            ConvertDrawingToDxf(application, drawingPath, command.OutputPath);
        }
        finally
        {
            CloseTemporaryDrawing(drawing7, unverified);
            TryDelete(drawingPath);
        }

        var bytes = File.Exists(command.OutputPath) ? new FileInfo(command.OutputPath).Length : 0;
        if (bytes == 0)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                "Конвертер отчитался об успехе, но файл DXF не создан или пуст: отсутствие байтов — отказ, "
                + "а не успех.",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(command.OutputPath),
                details: new Dictionary<string, object?> { ["output_path"] = command.OutputPath });
        }

        var contour = DxfContourReader.Read(
            command.OutputPath, FrameBox(frame), FlatDxfPolicy.WindowToleranceMm);
        var verdict = FlatDxfPolicy.Judge(contour?.Box, flatBox, bentBox, hasBends);

        if (verdict == FlatDxfVerdict.Mismatch || verdict == FlatDxfVerdict.Substituted)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                verdict == FlatDxfVerdict.Substituted
                    ? "Контур в DXF совпал с габаритом СОГНУТОГО тела, а у детали есть сгибы: вид построен "
                      + "как «Спереди», а не развёрткой (AddStandartViews игнорирует имя проекции). Файл "
                      + "оставлен для разбора, но выгрузкой развёртки не считается."
                    : "Габарит контура в DXF разошёлся с габаритом развёрнутого тела больше допуска "
                      + FlatDxfPolicy.ToleranceMm.ToString("0.####", CultureInfo.InvariantCulture) + " мм: "
                      + "выгрузка не подтверждена. Файл оставлен для разбора.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["output_path"] = command.OutputPath,
                    ["contour_mm"] = DescribeBox(contour?.Box),
                    ["flat_mm"] = DescribeBox(flatBox),
                    ["bent_mm"] = DescribeBox(bentBox),
                    ["bends"] = bends,
                });
        }

        if (verdict == FlatDxfVerdict.NotChecked)
        {
            unverified.Add("flat_extent_not_independent — независимый габарит развёртки не прочитан: "
                + (unfolded == false
                    ? "модель согнута (Unfold = false), и развёрнутый габарит из неё не читается; сверка "
                      + "контура с развёрткой не выполнена"
                    : "габарит тела не прочитан")
                + ". Контур измерен, но совпадением с развёрткой не подтверждён");
        }

        if (contour is null)
        {
            unverified.Add("dxf_not_parsed — DXF не разобран (не ASCII или нет пар «код/значение»): контур "
                + "и окружности не измерены");
        }

        var level = verdict == FlatDxfVerdict.Match
            ? VerificationLevel.GeometryChecked
            : VerificationLevel.StructureChecked;

        var checks = new List<NamedCheck>
        {
            new("file_created", bytes > 0,
                Observed: bytes.ToString(CultureInfo.InvariantCulture) + " байт", Expected: "> 0 байт"),
            new("dxf_parsed", contour is not null, Observed: contour is null ? "не разобран" : "разобран"),
            new("contour_matches_flat", verdict == FlatDxfVerdict.Match,
                Observed: DescribeBox(contour?.Box),
                Expected: flatBox is null ? "независимый габарит не прочитан" : DescribeBox(flatBox)),
            new("view_is_not_front_view", verdict != FlatDxfVerdict.Substituted,
                Observed: verdict == FlatDxfVerdict.Substituted ? "контур равен согнутому телу" : "не совпал",
                Expected: "контур развёртки"),
        };

        return new ExportSheetMetalFlatDxfResult(
            command.OutputPath,
            bytes,
            contour is null
                ? null
                : new FlatDxfContourDto(
                    contour.Box?.Width ?? 0d, contour.Box?.Height ?? 0d,
                    contour.Box?.MinX ?? 0d, contour.Box?.MinY ?? 0d,
                    contour.LineCount, contour.ArcCount, contour.CircleCount),
            flatBox?.Width,
            flatBox?.Height,
            isFlat,
            level,
            new VerificationDto(level, checks, unverified));
    }

    /// <summary>The body to export: the one named by <c>body_ref</c>, or the only sheet-metal body.</summary>
    /// <remarks>INVARIANT: a part with several sheet-metal bodies and no <c>body_ref</c> is refused by name
    /// — picking one would export a body the caller did not ask about.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private ISheetMetalBody ResolveFlatBody(DocumentEntry document, string? bodyRef)
    {
        if (bodyRef is not null)
        {
            return RequireSheetMetalBody(document, bodyRef);
        }

        var (_, sheet) = RequireSheetMetal(document, "Выгрузка не выполнялась");
        var count = ReadCountOf(() => sheet.SheetMetalBodies.Count);
        if (count == 1 && SafeObject(() => sheet.SheetMetalBodies[0]) is ISheetMetalBody single)
        {
            return single;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            count is null
                ? "body_ref не задан, а коллекция листовых тел не читается: какое тело выгружать, "
                  + "неизвестно. Передайте body_ref (kompas_get_sheet_metal отдаёт его)."
                : $"body_ref не задан, а в детали {count} листовых тел: выгружать «первое» — значит выбрать "
                  + "тело за клиента. Передайте body_ref.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = "body_ref",
                ["sheet_metal_bodies"] = count,
            });
    }

    /// <summary>The body's box in the sheet plane (X/Y of its own solid body), or <c>null</c> when the
    /// body or its box is not readable.</summary>
    /// <remarks>MEASURED: a sheet-metal body is a different COM object from the solid body the part holds,
    /// so the pointer finds nothing once a second body exists; a part with exactly ONE body falls back to the
    /// documented "только тела" mode of <c>ksPart.GetGabarit</c>, which is then that body's box.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private DxfBox? FlatBoxOf(DocumentEntry document, ISheetMetalBody body)
    {
        try
        {
            if (FindSolidBody(document, body) is { } solid)
            {
                var box = ReadBodyBox(solid);
                if (FlatBoxOf(box) is { } own)
                {
                    return own;
                }
            }

            if (CountBodies(document) == 1
                && TryReadGabaritCorners(document, full: false, customizable: false, out var min, out var max)
                && min.Length >= 2 && max.Length >= 2
                && double.IsFinite(min[0]) && double.IsFinite(max[1]))
            {
                return new DxfBox(min[0], min[1], max[0], max[1]);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        return null;
    }

    private static DxfBox? FlatBoxOf(BoundingBoxDto box) =>
        box.MinMm.Count < 2 || box.MaxMm.Count < 2
        || !double.IsFinite(box.MinMm[0]) || !double.IsFinite(box.MaxMm[1])
            ? null
            : new DxfBox(box.MinMm[0], box.MinMm[1], box.MaxMm[0], box.MaxMm[1]);

    /// <summary>Make the created view the unfold: its projection name, the unfold flag, then Update().</summary>
    /// <remarks>DOC: <c>iassociationview_projectionname.html</c>, <c>iassociationview_unfold.html</c>,
    /// <c>iassociationview_hiddenlinesvisible.html</c>, <c>idrawingobject_update.html</c>.
    /// MEASURED: the name has to be set on the VIEW, because <c>AddStandartViews</c> ignores its own
    /// argument and builds «Спереди»; the unfold flag alone already changes the geometry.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private void ApplyUnfoldView(IViews views, int number, List<string> unverified)
    {
        var association = FindViewByNumber(views, number) as IAssociationView;
        if (association is null)
        {
            unverified.Add("view_not_association — вид не отдал IAssociationView: имя проекции и признак "
                + "развёртки не заданы, вид мог остаться видом «Спереди»");
            return;
        }

        var nameSet = TrySet(() => association.ProjectionName = UnfoldProjectionName);
        var unfoldSet = TrySet(() => association.Unfold = true);
        // MEASURED: a hole on the far side of the sheet reaches the view only with hidden lines on.
        TrySet(() => association.HiddenLinesVisible = true);
        var updated = SafeBool(() => association.Update());

        if (nameSet == false)
        {
            unverified.Add("projection_name_not_written — ProjectionName вида не записался: вид мог "
                + "остаться «Спереди»");
        }

        if (unfoldSet == false)
        {
            unverified.Add("view_unfold_not_written — Unfold вида не записался: развёртку подтверждает "
                + "только геометрия контура");
        }

        if (updated != true)
        {
            unverified.Add("view_update_not_confirmed — IAssociationView.Update() не подтвердил применение "
                + "свойств: геометрию подтверждает только контур в DXF");
        }
    }

    /// <summary>The view's gabarit rectangle in sheet mm, through the documented API5 chain.</summary>
    /// <remarks>DOC: <c>ksdocument2d_ksgetviewreference.html</c>, <c>ksdocument2d_ksgetobjgabaritrect.html</c>,
    /// <c>structtype2denum.html</c> («ko_RectParam 15 ksRectParam»), <c>ksrectparam_getpbot.html</c>.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private IReadOnlyList<double>? ReadGabaritOfView(ksDocument2D? editor, DocumentEntry document, int number)
    {
        if (editor is null)
        {
            return null;
        }

        try
        {
            var pointer = editor.ksGetViewReference(number);
            if (pointer == 0
                || RequireApplication(document.ApplicationId).Application
                    .GetParamStruct(KompasStructTypes.RectParam) is not ksRectParam rect)
            {
                return null;
            }

            if (editor.ksGetObjGabaritRect(pointer, rect) == 0)
            {
                return null;
            }

            var bottom = rect.GetpBot() as ksMathPointParam;
            var top = rect.GetpTop() as ksMathPointParam;
            try
            {
                return bottom is null || top is null
                    ? null
                    : new[] { bottom.x, bottom.y, top.x, top.y };
            }
            finally
            {
                ComApartment.Release(bottom);
                ComApartment.Release(top);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The view's frame as a DXF window, or <c>null</c> when it was not read.</summary>
    private static DxfBox? FrameBox(IReadOnlyList<double>? frame) =>
        frame is { Count: 4 }
            ? new DxfBox(
                Math.Min(frame[0], frame[2]), Math.Min(frame[1], frame[3]),
                Math.Max(frame[0], frame[2]), Math.Max(frame[1], frame[3]))
            : null;

    /// <summary>Convert the saved temporary drawing into the requested DXF through the vendor library.</summary>
    /// <remarks>DOC: <c>iapplication_converter.html</c> (the library is a FULL PATH),
    /// <c>iconverter_getfilter.html</c> (FORMAT_DXF = 1), <c>iconverter_convert.html</c> (1 = success).
    /// INVARIANT: a documented success without a file is refused by the caller, never accepted.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private static void ConvertDrawingToDxf(IApplication application, string drawingPath, string outputPath)
    {
        var libraryPath = KompasInteropResolver.ExportLibraryPath("dxf");
        if (libraryPath is null)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                "Библиотека выгрузки DXF не найдена в установке КОМПАС (ожидается Libs\\ImpExp\\dwgdxfExp.rtw).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["expected_library"] = "Libs\\ImpExp\\dwgdxfExp.rtw",
                    ["install_root"] = KompasInteropResolver.InstallRoot,
                });
        }

        IConverter converter;
        int commandId;
        try
        {
            converter = application.get_Converter(libraryPath) as IConverter
                ?? throw new KompasContractException(
                    ErrorCodes.FormatUnavailable,
                    $"Конвертер по пути '{libraryPath}' не получен (Converter = null).",
                    RetryPolicy.Never);
            converter.GetFilter(1, true, out commandId);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                "Получение конвертера DXF бросило исключение: " + ex.Message,
                RetryPolicy.Never,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["library"] = libraryPath });
        }

        if (commandId <= 0)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Библиотека '{Path.GetFileName(libraryPath)}' не публикует команду DXF (GetFilter вернул "
                + $"command={commandId}). Выгрузка не начата.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["library"] = libraryPath });
        }

        int converted;
        try
        {
            converted = converter.Convert(drawingPath, outputPath, commandId, false);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                "Convert бросил исключение: " + ex.Message + ". Файл не подтверждён.",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(outputPath),
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["output_path"] = outputPath });
        }

        // MEASURED: the kernel can answer 0 and still write the file, so the return is recorded and the
        // file is what decides; a 0 without a file is refused by the caller.
        if (converted != 1 && !File.Exists(outputPath))
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                $"Convert вернул {converted}, и файла нет: справка задаёт 1 как успех, 0 как неудачу.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["convert_result"] = converted });
        }
    }

    /// <summary>Close the temporary drawing without saving; a close that failed is named, not hidden.</summary>
    /// <remarks>DOC: <c>ikompasdocument_close.html</c> — the option says what happens to unsaved changes.
    /// MEASURED: the API7 call answers false on a drawing this route created through API7
    /// <c>Documents.Add</c>, while the document does not stay in the session; the answer is named, not hidden.
    /// LIMIT: the API5 2D handle has no close of its own, so no second attempt exists.
    /// History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
    private static void CloseTemporaryDrawing(IKompasDocument2D? drawing, List<string> unverified)
    {
        if (drawing is null)
        {
            return;
        }

        try
        {
            if (!drawing.Close(DocumentCloseOptions.kdDoNotSaveChanges))
            {
                unverified.Add("temporary_drawing_close_unconfirmed — временный чертёж закрыт без "
                    + "сохранения, но Close(kdDoNotSaveChanges) вернул false: закрытие не подтверждено "
                    + "ответом метода");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            unverified.Add("temporary_drawing_not_closed — закрытие временного чертежа бросило: "
                + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file in the temp directory is not worth failing an export over.
        }
    }

    /// <summary>The numbers of the numbered views, re-read from the drawing's own collection.</summary>
    private static int[] ViewNumbers(IViews views)
    {
        var numbers = new List<int>();
        var count = SafeInt(() => views.Count) ?? 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (views.get_View(i) is IView view && SafeInt(() => view.Number) is int number && number > 0)
                {
                    numbers.Add(number);
                }
            }
            catch (COMException)
            {
                // An unreadable view cannot be the one just created; the caller names a missing view.
            }
        }

        return numbers.ToArray();
    }

    /// <summary>The view with this number, or <c>null</c>.</summary>
    private static IView? FindViewByNumber(IViews views, int number)
    {
        var count = SafeInt(() => views.Count) ?? 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (views.get_View(i) is IView view && SafeInt(() => view.Number) == number)
                {
                    return view;
                }
            }
            catch (COMException)
            {
                // Not the view asked about.
            }
        }

        return null;
    }

    /// <summary>A collection count, or <c>null</c> when it does not answer.</summary>
    private static int? ReadCountOf(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>A property write: true when it did not throw.</summary>
    private static bool TrySet(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static string DescribeBox(DxfBox? box) =>
        box is null
            ? "не прочитан"
            : box.Width.ToString("0.###", CultureInfo.InvariantCulture) + " x "
              + box.Height.ToString("0.###", CultureInfo.InvariantCulture) + " мм";
}

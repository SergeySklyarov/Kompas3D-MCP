using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.SheetMetal;

namespace KompasMcp.Api5Adapter;

/// <summary>Contour of a flat part of constant thickness as a DXF file (profile <c>flat-part-v1</c>).</summary>
/// <remarks>DOC: <c>ksapi_iviews_addstandartviews.html</c> — the view is built from a FILE;
/// <c>ksviewprojectiontype.html</c> — the type of each standard view.
/// INVARIANT: the answer is the geometry inside the written file, never the fact that a file appeared —
/// its area and size are compared with the flat face and the body's box.
/// LIMIT: a view normal to an arbitrary direction is not supported in v24: <c>OrientationNormalTo</c> is
/// described in the help but absent from the shipped type library.
/// History: docs/decisions/flat-part.md#tool</remarks>
public partial class Api5Session
{
    /// <summary>Relative difference allowed between the contour's area and the flat face's area.</summary>
    /// <remarks>INVARIANT: the contour is the projection of the body, so its area equals the area of the
    /// flat side; a chord-flattened arc is the only source of error and it is far below this share.
    /// History: docs/decisions/flat-part.md#contour-check</remarks>
    private const double AreaShareTolerance = 0.005d;

    /// <summary>Absolute difference allowed between the contour and the model, in millimetres.</summary>
    private const double SizeToleranceMm = 0.01d;

    /// <summary>Export the contour of a flat part of constant thickness through a temporary drawing.</summary>
    public ExportFlatDxfResult ExportFlatDxf(ExportFlatDxfCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (document.Kind != DocumentKind.Part)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Выгрузка контура применима к детали; документ «{document.Id}» имеет тип {document.Kind}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }

        // INVARIANT: the view is built from the part's FILE, so an unsaved part would export an older model.
        if (document.Path is null || IsDirty(document))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentDirty,
                "Деталь не сохранена в файл или содержит несохранённые изменения, а вид строится по "
                + "СОХРАНЁННОМУ файлу детали. Сохраните деталь (kompas_save_document) и повторите.",
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

        AtomicPublish.RequireWritableTarget(command.OutputPath, command.Overwrite);

        var body = ResolveFlatPartBody(document, command.BodyRef);
        var (faces, flatFace) = ReadFlatPartFaces(document, body);
        var plan = FlatPartPolicy.Analyze(faces);
        switch (plan.Verdict)
        {
            case FlatPartVerdict.Ambiguous:
                throw new KompasContractException(
                    ErrorCodes.AmbiguousFlatPattern,
                    "Однозначной пары плоских граней нет: " + plan.Reason,
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["candidates"] = plan.CandidateFaceRefs,
                        ["field"] = "body_ref",
                    });
            case FlatPartVerdict.NotConstantThickness:
                throw new KompasContractException(
                    ErrorCodes.NotConstantThickness,
                    "Тело не является плоской деталью постоянной толщины: " + plan.Reason,
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["offending_faces"] = plan.OffendingFaceRefs,
                        ["thickness_mm"] = plan.ThicknessMm,
                    });
        }

        // INVARIANT: an axis normal keeps the measured standard-view route; any other direction goes
        // through a user projection built from the measured 4×4 convention, because v24 has no
        // documented member that turns a view normal to a face (OrientationNormalTo is absent).
        var view = FlatPartProjection.Choose(plan.Normal);
        var tilted = view is null ? TiltedRoute(flatFace, plan.Normal) : null;
        var matrix = tilted?.Matrix;
        if (view is null && matrix is null)
        {
            throw new KompasContractException(
                ErrorCodes.FlatNormalNotAxis,
                "Нормаль плоской детали " + DescribeVector(plan.Normal) + " не прочитана как направление: "
                + "ни стандартный вид, ни матрица проекции не строятся.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["normal"] = plan.Normal,
                    ["thickness_mm"] = plan.ThicknessMm,
                    ["field"] = "body_ref",
                });
        }

        var gabarit = ReadBodyGabarit(body);
        var directory = Path.GetDirectoryName(command.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // LIMIT: after Unfold a bent sheet-metal part IS a plate of constant thickness, so this route would
        // reach the view build that does not complete in a visible instance. Unbent sheet metal is flat by
        // itself and passes. History: docs/decisions/sheet-metal.md#flat-dxf-visible-refusal
        RefuseBentPartViewInVisibleInstance(
            document, SheetMetalBendsOf(document), "flat_part_dxf_export_in_visible_instance");

        var application = RequireApplication7(document);
        var bridge = BridgeFor(document);
        var drawingPath = Path.Combine(Path.GetTempPath(), "kompas-flatpart-" + Guid.NewGuid().ToString("N") + ".cdw");
        var rawPath = Path.Combine(Path.GetTempPath(), "kompas-flatpart-" + Guid.NewGuid().ToString("N") + ".dxf");
        var unverified = new List<string>();
        IReadOnlyList<double>? frame = null;
        IKompasDocument2D? drawing7 = null;
        // INVARIANT: the expected contour box is the FACE's own: the body's box is not the face's once the
        // normal leaves the axes, and the axis route keeps the measured body extents.
        var expected = view is null ? tilted?.Box : ExpectedFaceSize(gabarit, view.Axis);
        var requestedName = view?.Name ?? TiltedProjectionName;
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
                    "Временный чертёж не отдал коллекцию видов (IViews): вид не построен, файл не "
                    + "выгружался.",
                    RetryPolicy.ReacquireContext);

            var before = ViewNumbers(views);
            // The route names the orientation either by a standard view or by a user projection; the
            // projection is created on the PART, because the drawing is built from the part's file.
            var projectionName = matrix is null
                ? null
                : PrepareTiltedProjection(document, matrix, unverified);
            var requested = matrix is null ? FlatPartViewList(view!.Flag) : new object[] { 1 };
            requestedName = projectionName ?? view!.Name;
            bool accepted;
            try
            {
                // DOC: the second argument is «имя проекции (из списка проекций в документе источнике)»,
                // so it names the same orientation the mask asks for; both are given, because the mask
                // alone is what the kernel honours for the views measured so far.
                // DOC: the list holds standard-view types (ksviewprojectiontype.html: 1 - Спереди,
                // 2 - Сзади, 3 - Сверху, 4 - Снизу, 5 - Слева, 6 - Справа).
                accepted = views.AddStandartViews(
                    document.Path, requestedName, requested, 100d, 100d, 1d, 10d, 10d);
            }
            catch (COMException ex)
            {
                throw TiltedRefusal(matrix, requestedName,
                    "AddStandartViews бросил исключение: " + ex.Message, RetryPolicy.SameOperationId);
            }

            if (!accepted)
            {
                throw TiltedRefusal(matrix, requestedName,
                    "AddStandartViews вернул false: ядро отказало в построении вида", RetryPolicy.SameOperationId);
            }

            var created = ViewNumbers(views).Except(before).ToArray();
            if (created.Length == 0)
            {
                throw TiltedRefusal(matrix, requestedName,
                    "AddStandartViews принят, но новых видов в чертеже нет: вид не появился",
                    RetryPolicy.SameOperationId);
            }

            // INVARIANT: the contour is the outline of the body, so hidden edges stay out — a hole's far
            // rim would otherwise arrive as a second, coincident circle.
            // INVARIANT: the exported contour is the view whose frame is the flat face, never the main
            // view that the group builds alongside it.
            var target = PickFlatView(drawing5, document, created, expected)
                ?? throw TiltedRefusal(matrix, requestedName,
                    "среди построенных видов нет вида по нормали: ни одна рамка не совпала с размером "
                    + "плоской грани, выгрузка отменена, чтобы не опубликовать контур чужой грани",
                    RetryPolicy.SameOperationId);

            ApplyFlatView(views, target, unverified);
            frame = ReadGabaritOfView(drawing5, document, target);
            if (frame is null)
            {
                unverified.Add("view_frame_not_read — рамка вида не прочитана (ksGetObjGabaritRect): "
                    + "сущности за пределами контура не отсекаются по окну вида");
            }

            // INVARIANT: the temporary drawing is a claim about the FILE: the converter reads it by path, so
            // a SaveAs that left the content elsewhere would export nothing. Same rule as a client save,
            // including the temporary file beside the drawing.
            // History: docs/decisions/files.md#save-file-write
            var drawingBefore = SavedFileWrite.Capture(drawingPath);
            var temporaryPath = drawingPath + SavedFileWrite.TemporarySuffix;
            var temporaryBefore = SavedFileWrite.Capture(temporaryPath);
            drawing7.SaveAs(drawingPath);
            var notWritten = SavedFileWrite.UnconfirmedReason(
                drawingBefore, SavedFileWrite.Capture(drawingPath),
                temporaryBefore, SavedFileWrite.Capture(temporaryPath));
            if (notWritten is not null)
            {
                throw new KompasContractException(
                    ErrorCodes.ExportFailed,
                    $"Временный чертёж не сохранён файлом ({notWritten}): конвертер берёт входной файл по пути, "
                    + "выгрузка невозможна.",
                    RetryPolicy.SameOperationId,
                    details: new Dictionary<string, object?>
                    {
                        ["drawing_path"] = drawingPath,
                        ["temporary_path"] = temporaryPath,
                        ["reason"] = notWritten,
                    });
            }

            ConvertDrawingToDxf(application, drawingPath, rawPath);
        }
        finally
        {
            CloseTemporaryDrawing(drawing7, unverified);
            TryDelete(drawingPath);
        }

        try
        {
            // INVARIANT: only the contour is published — the frame, the title block and every other entity
            // of the drawing stay out, and every kept entity goes to the CUT layer.
            if (!DxfFlatPattern.Rewrite(rawPath, command.OutputPath, FrameBox(frame), DxfFlatPattern.CutLayer))
            {
                throw new KompasContractException(
                    ErrorCodes.ExportFailed,
                    "Конвертер отдал DXF, но переписать его в контур не удалось: файл не опубликован.",
                    RetryPolicy.SameOperationId,
                    details: new Dictionary<string, object?> { ["output_path"] = command.OutputPath });
            }
        }
        finally
        {
            TryDelete(rawPath);
        }

        var bytes = File.Exists(command.OutputPath) ? new FileInfo(command.OutputPath).Length : 0;
        var check = DxfFlatPattern.Check(command.OutputPath);
        var faceArea = faces.FirstOrDefault(face => face.FaceRef == plan.CandidateFaceRefs[0])?.AreaMm2;
        // INVARIANT: a tilted view is judged by the FACE's box only; the body's axis-aligned box is not
        // the face's once the normal leaves the axes, so it is not passed as a judge there.
        var verdict = JudgeContour(check, faceArea, view is null ? null : gabarit, plan.ThicknessMm, expected);

        if (bytes == 0 || !check.Ok || verdict is not null)
        {
            TryDelete(command.OutputPath);
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                verdict ?? (bytes == 0
                    ? "Файл DXF пуст: отсутствие байтов — отказ, а не успех."
                    : "Контур в DXF не прошёл проверку: " + string.Join("; ", check.Problems.Take(5))),
                RetryPolicy.Never,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["output_path"] = command.OutputPath,
                    ["view"] = requestedName,
                    ["problems"] = check.Problems,
                    ["loops"] = check.Loops,
                    ["contour_area_mm2"] = check.AreaMm2,
                    ["face_area_mm2"] = faceArea,
                    ["contour_mm"] = DescribeBox(check.Box),
                    ["model_mm"] = DescribeExtents(gabarit),
                });
        }

        var checks = new List<NamedCheck>
        {
            new("file_created", bytes > 0,
                Observed: bytes.ToString(CultureInfo.InvariantCulture) + " байт", Expected: "> 0 байт"),
            new("entities_allowed", check.Problems.Count == 0,
                Observed: string.Join(", ", check.EntityCounts.Select(pair => pair.Key + "=" + pair.Value)),
                Expected: "LINE/ARC/CIRCLE/LWPOLYLINE/POLYLINE на слое " + DxfFlatPattern.CutLayer),
            new("loops_closed", check.Loops > 0, Observed: check.Loops + " петель", Expected: ">= 1"),

        };

        // INVARIANT: the area check exists only when the face's area was read — an unread value is named in
        // unverified_aspects and lowers the level, never turned into a passing check.
        if (faceArea is { } area)
        {
            checks.Insert(3, new NamedCheck(
                "contour_area_matches_face",
                Math.Abs(check.AreaMm2 - area) <= Math.Max(area * AreaShareTolerance, SizeToleranceMm),
                Observed: check.AreaMm2.ToString("0.###", CultureInfo.InvariantCulture) + " мм2",
                Expected: area.ToString("0.###", CultureInfo.InvariantCulture) + " мм2"));
        }

        // INVARIANT: the box check exists only when the expected box was read; an unread box is named in
        // unverified_aspects, never turned into a passing check.
        if (expected is { Length: 2 })
        {
            checks.Add(new NamedCheck(
                "contour_size_matches_model",
                verdict is null,
                Observed: DescribeBox(check.Box),
                Expected: DescribeSize(expected)));
        }
        else
        {
            unverified.Add("face_box_not_read — габарит плоской грани по её рёбрам не прочитан: размер "
                + "контура сверяется только по площади, а не по габариту");
        }

        return new ExportFlatDxfResult(
            command.OutputPath,
            bytes,
            plan.Normal!,
            plan.ThicknessMm!.Value,
            check.Box is null
                ? null
                : new FlatDxfContourDto(
                    check.Box.Width, check.Box.Height, check.Box.MinX, check.Box.MinY,
                    check.EntityCounts.GetValueOrDefault("LINE"),
                    check.EntityCounts.GetValueOrDefault("ARC"),
                    check.EntityCounts.GetValueOrDefault("CIRCLE")),
            check.Loops,
            check.OuterLoops,
            check.InnerLoops,
            check.AreaMm2,
            faceArea,
            check.EntityCounts,
            DxfFlatPattern.CutLayer,
            faceArea is null ? VerificationLevel.StructureChecked : VerificationLevel.GeometryChecked,
            new VerificationDto(
                faceArea is null ? VerificationLevel.StructureChecked : VerificationLevel.GeometryChecked,
                checks,
                unverified));
    }

    /// <summary>The reason the contour disagrees with the model, or <c>null</c> when it agrees.</summary>
    /// <remarks>INVARIANT: the model is the judge. The contour of a flat part has the two largest extents
    /// of the body's box, and its area is the area of the flat side; a view built in the wrong orientation
    /// fails both. A missing model value is «не прочитано» — the caller lowers the level and says so.
    /// History: docs/decisions/flat-part.md#contour-check</remarks>
    private static string? JudgeContour(
        DxfFlatCheckResult check, double? faceArea, IReadOnlyList<double>? gabarit, double? thickness,
        double[]? faceBox)
    {
        if (check.Box is null)
        {
            return "Контур пуст: в файле нет ни одной сущности, габарит не измерен.";
        }

        if (faceArea is { } area && Math.Abs(check.AreaMm2 - area) > Math.Max(area * AreaShareTolerance, SizeToleranceMm))
        {
            return "Площадь контура " + check.AreaMm2.ToString("0.###", CultureInfo.InvariantCulture)
                + " мм2 разошлась с площадью плоской грани " + area.ToString("0.###", CultureInfo.InvariantCulture)
                + " мм2 больше допуска " + (AreaShareTolerance * 100d).ToString("0.#", CultureInfo.InvariantCulture)
                + " %: вид построен не по нормали к грани.";
        }

        // INVARIANT: a tilted view is judged by the FACE's own box, read from its edges; the body's box
        // cannot serve there, because a plate turned away from the axes has a larger axis-aligned box.
        var actual = new[] { check.Box.Width, check.Box.Height }.OrderBy(value => value).ToArray();
        if (faceBox is { Length: 2 })
        {
            var wanted = faceBox.OrderBy(value => value).ToArray();
            return Math.Abs(actual[0] - wanted[0]) > SizeToleranceMm
                || Math.Abs(actual[1] - wanted[1]) > SizeToleranceMm
                ? "Габарит контура " + DescribeBox(check.Box) + " разошёлся с габаритом плоской грани "
                    + DescribeSize(faceBox) + " больше допуска "
                    + SizeToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + " мм."
                : null;
        }

        if (gabarit is not { Count: 3 })
        {
            return null;
        }

        var model = gabarit.OrderByDescending(value => value).ToArray();
        var expected = model.Take(2).OrderByDescending(value => value).ToArray();
        var contour = new[] { check.Box.Width, check.Box.Height }.OrderByDescending(value => value).ToArray();
        if (Math.Abs(contour[0] - expected[0]) > SizeToleranceMm
            || Math.Abs(contour[1] - expected[1]) > SizeToleranceMm)
        {
            return "Габарит контура " + DescribeBox(check.Box) + " разошёлся с двумя наибольшими размерами тела "
                + expected[0].ToString("0.###", CultureInfo.InvariantCulture) + " x "
                + expected[1].ToString("0.###", CultureInfo.InvariantCulture) + " мм больше допуска "
                + SizeToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + " мм.";
        }

        if (thickness is { } value && Math.Abs(model[2] - value) > SizeToleranceMm)
        {
            return "Толщина тела " + model[2].ToString("0.###", CultureInfo.InvariantCulture)
                + " мм разошлась с толщиной по граням " + value.ToString("0.###", CultureInfo.InvariantCulture)
                + " мм: тело не постоянной толщины.";
        }

        return null;
    }

    /// <summary>Hide the hidden edges of the created view and apply the change.</summary>
    /// <remarks>DOC: <c>iassociationview_hiddenlinesvisible.html</c> — the flag shows the invisible lines of
    /// an associative view; <c>idrawingobject_update.html</c> — «Update необходимо вызвать для объекта,
    /// свойства которого были изменены». MEASURED: without the flag the far rim of a through hole arrives
    /// as a second circle, and the contour check sees a duplicate.
    /// History: docs/decisions/flat-part.md#contour-check</remarks>
    /// <summary>Type of the group's main view: <c>ksViewProjectionType.ksVPFront</c> = 1.</summary>
    private const int MainViewFlag = 1;

    /// <summary>Difference allowed between a view's frame and the flat face's size, in millimetres.</summary>
    /// <remarks>MEASURED: the frame is the view's bounding rectangle, so line widths add a fraction of a
    /// millimetre; the faces of a flat part differ by whole millimetres, so this separates them.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private const double FrameToleranceMm = 0.5d;

    /// <summary>The view list to ask for: the wanted view plus the main one it is built against.</summary>
    /// <remarks>MEASURED: a dependent view asked without «Спереди» is silently replaced by the front view,
    /// so the main view is always in the list. History: docs/decisions/flat-part.md#view-choice</remarks>
    private static object[] FlatPartViewList(int flag) =>
        flag == MainViewFlag ? new object[] { flag } : new object[] { MainViewFlag, flag };

    /// <summary>The created view whose frame has the flat face's size, or <c>null</c> when none has.</summary>
    /// <remarks>MEASURED: neither the number nor the projection name identifies the view — the kernel numbers
    /// the main view first for one set of types and last for another, and the dependent view's name is not
    /// reliably empty. INVARIANT: the view is chosen by its measured frame, because the flat face of a
    /// constant-thickness part spans the body's two extents across the normal; no such view means a named
    /// refusal instead of the first view in the list.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private int? PickFlatView(ksDocument2D? editor, DocumentEntry document, int[] created, double[]? expected)
    {
        if (expected is null)
        {
            return created.Length == 1 ? created[0] : null;
        }

        var wanted = expected.OrderBy(value => value).ToArray();
        var matches = new List<int>();
        foreach (var number in created)
        {
            if (ReadGabaritOfView(editor, document, number) is not { Count: 4 } frame)
            {
                continue;
            }

            var size = new[] { Math.Abs(frame[2] - frame[0]), Math.Abs(frame[3] - frame[1]) };
            Array.Sort(size);
            if (Math.Abs(size[0] - wanted[0]) <= FrameToleranceMm
                && Math.Abs(size[1] - wanted[1]) <= FrameToleranceMm)
            {
                matches.Add(number);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Name of the projection a tilted view is built from, as it appears in the part's list.</summary>
    /// <remarks>INVARIANT: the name is stable per machine, because the drawing is built by that name from
    /// the part's file; the suffix only appears when the name is already taken in this document.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private const string TiltedProjectionName = "KompasMcpFlat";

    /// <summary>A user projection with the measured 4×4, added to the PART the view is built from.</summary>
    /// <remarks>DOC: <c>iviewprojectionmanager_add.html</c> («Добавить новую пользовательскую проекцию»,
    /// returns <c>IViewProjection7</c>), <c>iviewprojection7_name.html</c>, <c>iviewprojection7_update.html</c>
    /// («Создать пользовательскую проекцию»), <c>ksviewprojectioncollection_getbyname.html</c>,
    /// <c>iviews_addstandartviews.html</c> — the view is built by that name «из списка проекций в документе
    /// источнике». MEASURED: the matrix of the 4×4 is the convention of
    /// <see cref="FlatPartProjection.Matrix"/>; SetMatrix3D and Update both return true and the view then
    /// comes out in that orientation; adding a projection does NOT mark the document changed, while
    /// deleting it does — so it is left in place.
    /// History: docs/decisions/flat-part.md#tilted-normal</remarks>
    private string PrepareTiltedProjection(DocumentEntry document, double[] matrix, List<string> unverified)
    {
        var document7 = BridgeFor(document).TransferTo7(document.Document3D) as IKompasDocument3D;
        var manager = (document7 as IKompasDocument3D1)?.ViewProjectionManager as IViewProjectionManager;
        if (manager is null)
        {
            throw TiltedRefusal(matrix, TiltedProjectionName,
                "документ-деталь не отдал IViewProjectionManager: пользовательская проекция не создана",
                RetryPolicy.ReacquireContext);
        }

        var name = TiltedProjectionName;
        for (var attempt = 2; attempt < 100 && Taken(manager, name); attempt++)
        {
            name = TiltedProjectionName + attempt.ToString(CultureInfo.InvariantCulture);
        }

        IViewProjection7? projection;
        try
        {
            projection = manager.Add() as IViewProjection7;
        }
        catch (COMException ex)
        {
            throw TiltedRefusal(matrix, name, "IViewProjectionManager.Add() бросил исключение: " + ex.Message,
                RetryPolicy.SameOperationId);
        }

        if (projection is null)
        {
            throw TiltedRefusal(matrix, name, "IViewProjectionManager.Add() не вернул IViewProjection7",
                RetryPolicy.SameOperationId);
        }

        projection.Name = name;
        if (SafeBool(() => projection.SetMatrix3D(matrix)) != true)
        {
            throw TiltedRefusal(matrix, name, "IViewProjection7.SetMatrix3D вернул не true",
                RetryPolicy.SameOperationId);
        }

        if (SafeBool(() => projection.Update()) != true)
        {
            throw TiltedRefusal(matrix, name, "IViewProjection7.Update вернул не true: проекция не создана",
                RetryPolicy.SameOperationId);
        }

        return name;
    }

    /// <summary>Whether this projection name is already in the document's list.</summary>
    private static bool Taken(IViewProjectionManager manager, string name)
    {
        try
        {
            return manager.ViewProjection[name] is not null;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>The 4×4 of a tilted view and the face's own box, both built on the FACE's own edge.</summary>
    /// <remarks>INVARIANT: the in-plane X is the face's longest edge, so the view comes out square to the
    /// part and the contour's box is the face's box; with any other in-plane turn the outline is the same
    /// rectangle turned inside its frame, and a box check could not be made. The turn is FREE in the
    /// measured convention (rows = the view's axes, <see cref="FlatPartProjection.Matrix"/>), so it is
    /// taken from the geometry. DOC: <c>ksfacedefinition_edgecollection.html</c>,
    /// <c>ksedge_getvertex.html</c>, <c>ksvertexdefinition_getpoint.html</c>.
    /// History: docs/decisions/flat-part.md#tilted-normal</remarks>
    private (double[] Matrix, double[]? Box)? TiltedRoute(ksFaceDefinition? face, double[]? normal)
    {
        var unit = FlatPartProjection.Unit(normal);
        if (unit is null)
        {
            return null;
        }

        var segments = FaceSegments(face);
        double[]? xAxis = null;
        var longest = 0d;
        foreach (var (a, b) in segments ?? new List<(double[] A, double[] B)>())
        {
            var delta = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            var length = Math.Sqrt(Dot(delta, delta));
            // LIMIT: an edge of the face lies IN the face plane, so a candidate that is not perpendicular
            // to the normal is a curved edge, not a direction the view can be squared to.
            if (length <= longest || Math.Abs(Dot(delta, unit)) > 1e-6 * Math.Max(length, 1d))
            {
                continue;
            }

            longest = length;
            xAxis = new[] { delta[0] / length, delta[1] / length, delta[2] / length };
        }

        if (xAxis is null)
        {
            return FlatPartProjection.Matrix(normal) is { } fallback ? (fallback, null) : null;
        }

        var yAxis = Cross(unit, xAxis);
        var matrix = new[]
        {
            xAxis[0], xAxis[1], xAxis[2], 0d,
            yAxis[0], yAxis[1], yAxis[2], 0d,
            unit[0], unit[1], unit[2], 0d,
            0d, 0d, 0d, 1d,
        };

        var us = new List<double>();
        var vs = new List<double>();
        foreach (var (a, b) in segments!)
        {
            us.Add(Dot(xAxis, a[0], a[1], a[2]));
            vs.Add(Dot(yAxis, a[0], a[1], a[2]));
            us.Add(Dot(xAxis, b[0], b[1], b[2]));
            vs.Add(Dot(yAxis, b[0], b[1], b[2]));
        }

        return (matrix, us.Count == 0 ? null : new[] { us.Max() - us.Min(), vs.Max() - vs.Min() });
    }

    /// <summary>The flat face's edges as point pairs, or <c>null</c> when they are not read.</summary>
    private List<(double[] A, double[] B)>? FaceSegments(ksFaceDefinition? face)
    {
        var collection = face?.EdgeCollection() as ksEdgeCollection;
        if (collection is null)
        {
            return null;
        }

        var segments = new List<(double[] A, double[] B)>();
        for (var index = 0; index < collection.GetCount(); index++)
        {
            if (AsInterface<ksEdgeDefinition>(collection.GetByIndex(index)) is not { } edge)
            {
                continue;
            }

            var a = VertexPoint(edge, true);
            var b = VertexPoint(edge, false);
            if (a is not null && b is not null)
            {
                segments.Add((a, b));
            }
        }

        return segments;
    }

    private static double[]? VertexPoint(ksEdgeDefinition edge, bool start)
    {
        if (SafeObject(() => edge.GetVertex(start)) is not { } vertexObject
            || AsInterface<ksVertexDefinition>(vertexObject) is not { } vertex
            || SafeBool(() => vertex.GetPoint(out _, out _, out _)) != true)
        {
            return null;
        }

        vertex.GetPoint(out var x, out var y, out var z);
        return new[] { x, y, z };
    }

    private static double[] Cross(double[] left, double[] right) => new[]
    {
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    };

    private static double Dot(double[] axis, double x, double y, double z) =>
        axis[0] * x + axis[1] * y + axis[2] * z;

    /// <summary>The refusal of a tilted route, naming the step that failed; an axis route keeps its codes.</summary>
    /// <remarks>INVARIANT: a failed matrix route refuses with <c>FLAT_NORMAL_NOT_AXIS</c> and the measured
    /// values, so the absent route stays named instead of turning into another code.
    /// History: docs/decisions/flat-part.md#view-choice</remarks>
    private KompasContractException TiltedRefusal(double[]? matrix, string name, string why, RetryPolicy retry) =>
        new(matrix is null ? ErrorCodes.GeometryFailed : ErrorCodes.FlatNormalNotAxis,
            matrix is null
                ? "Вид «" + name + "» не построен: " + why + "."
                : "Вид по нормали через пользовательскую проекцию «" + name + "» не построен: " + why
                  + ". Матрица проекции - " + DescribeMatrix(matrix) + ".",
            retry,
            details: matrix is null
                ? new Dictionary<string, object?> { ["view"] = name, ["step"] = why }
                : new Dictionary<string, object?>
                {
                    ["normal_matrix"] = matrix,
                    ["projection"] = name,
                    ["step"] = why,
                    ["field"] = "body_ref",
                });

    private static string DescribeMatrix(double[] matrix) =>
        matrix is { Length: 16 }
            ? string.Join(" ", matrix.Select(value => value.ToString("0.####", CultureInfo.InvariantCulture)))
            : "не прочитана";

    /// <summary>The two body extents across the normal, ascending; <c>null</c> when they are unknown.</summary>
    private static double[]? ExpectedFaceSize(IReadOnlyList<double>? extents, string axis)
    {
        if (extents is not { Count: 3 })
        {
            return null;
        }

        var size = axis switch
        {
            "X" => new[] { extents[1], extents[2] },
            "Y" => new[] { extents[0], extents[2] },
            _ => new[] { extents[0], extents[1] },
        };
        Array.Sort(size);
        return size;
    }

    private static void ApplyFlatView(IViews views, int number, List<string> unverified)
    {
        if (FindViewByNumber(views, number) is not IAssociationView association)
        {
            unverified.Add("view_not_association — вид не отдал IAssociationView: скрытые линии не выключены, "
                + "контур может содержать невидимые рёбра");
            return;
        }

        if (!TrySet(() => association.HiddenLinesVisible = false))
        {
            unverified.Add("hidden_lines_not_written — признак скрытых линий не записался: контур может "
                + "содержать невидимые рёбра");
        }

        if (SafeBool(() => association.Update()) != true)
        {
            unverified.Add("view_update_not_confirmed — IAssociationView.Update() не подтвердил применение "
                + "свойств: геометрию подтверждает только контур в DXF");
        }
    }

    /// <summary>The body to export: the one named by <c>body_ref</c>, or the only body of the part.</summary>
    /// <remarks>INVARIANT: a part with several bodies and no <c>body_ref</c> is refused by name — picking one
    /// would export a body the caller did not ask about.
    /// History: docs/decisions/flat-part.md#tool</remarks>
    private ksBody ResolveFlatPartBody(DocumentEntry document, string? bodyRef)
    {
        if (bodyRef is not null)
        {
            var stored = References.Require(bodyRef, document.Id, document.Revision);
            return stored.Payload as ksBody
                ?? throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"body_ref '{bodyRef}' указывает на '{stored.Kind}', а не на тело.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var count = TryCountBodies(document);
        if (count == 1 && document.PartNow().GetMainBody() is ksBody single)
        {
            return single;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            count is null
                ? "body_ref не задан, а число тел не читается: какое тело выгружать, неизвестно. "
                  + "Передайте body_ref (kompas_get_bodies отдаёт его)."
                : $"body_ref не задан, а в детали {count} тел: выгружать «первое» — значит выбрать тело за "
                  + "клиента. Передайте body_ref.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = "body_ref",
                ["bodies"] = count,
            });
    }

    /// <summary>Every face of the body as the plane check reads it, plus the face of the largest area.</summary>
    /// <remarks>INVARIANT: an unreadable area, normal or point is passed on as <c>null</c>, never as a zero:
    /// the policy then refuses the body instead of calling it flat.
    /// History: docs/decisions/flat-part.md#plane-check</remarks>
    private (IReadOnlyList<FlatPartFace> Faces, ksFaceDefinition? FlatFace) ReadFlatPartFaces(
        DocumentEntry document, ksBody body)
    {
        var faces = new List<FlatPartFace>();
        ksFaceDefinition? largest = null;
        var largestArea = double.MinValue;
        var collection = body.FaceCollection() as ksFaceCollection;
        if (collection is null)
        {
            return (faces, null);
        }

        for (var i = 0; i < collection.GetCount(); i++)
        {
            if (AsInterface<ksFaceDefinition>(collection.GetByIndex(i)) is not ksFaceDefinition face)
            {
                continue;
            }

            var area = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm));
            var surfaceType = SurfaceTypeName(face);
            var normal = SurfaceNormalAtMiddle(face);
            var point = SurfacePointAtMiddle(face);
            var axis = CylinderGeometry(face).AxisMm;
            var reference = References.Register("face", document.Id, document.Revision, face).Id;

            faces.Add(new FlatPartFace(reference, surfaceType, area ?? double.NaN, normal, point, axis));

            if (surfaceType == "plane" && area is { } value && value > largestArea)
            {
                largestArea = value;
                largest = face;
            }
        }

        return (faces, largest);
    }

    /// <summary>A point of the face at its middle parameters, or <c>null</c> when it is not read.</summary>
    /// <remarks>DOC: <c>kssurface_getpoint.html</c> — the point is addressed by the surface's own
    /// <c>u</c>/<c>v</c>; the middle of the parameter range is the same point the normal is read at.
    /// History: docs/decisions/flat-part.md#plane-check</remarks>
    private static double[]? SurfacePointAtMiddle(ksFaceDefinition face)
    {
        try
        {
            if (face.GetSurface() is not ksSurface surface)
            {
                return null;
            }

            var u = (surface.GetParamUMin() + surface.GetParamUMax()) / 2d;
            var v = (surface.GetParamVMin() + surface.GetParamVMax()) / 2d;
            return surface.GetPoint(u, v, out var x, out var y, out var z)
                ? new[] { x, y, z }
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>The body's three extents in millimetres, or <c>null</c> when the box is not read.</summary>
    private static IReadOnlyList<double>? ReadBodyGabarit(ksBody body)
    {
        try
        {
            return body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? new[] { Math.Abs(x2 - x1), Math.Abs(y2 - y1), Math.Abs(z2 - z1) }
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string DescribeVector(double[]? vector) =>
        vector is not { Length: 3 }
            ? "не прочитана"
            : "(" + string.Join(", ", vector.Select(value =>
                value.ToString("0.####", CultureInfo.InvariantCulture))) + ")";

    private static string DescribeSize(double[]? size) =>
        size is { Length: 2 }
            ? string.Join(" x ", size.OrderBy(value => value)
                .Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))) + " мм"
            : "не прочитан";

    private static string DescribeExtents(IReadOnlyList<double>? extents) =>
        extents is null
            ? "не прочитан"
            : string.Join(" x ", extents.OrderByDescending(value => value)
                .Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))) + " мм";
}

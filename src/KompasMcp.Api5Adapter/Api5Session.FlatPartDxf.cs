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

        // INVARIANT: the view is one of the six standard ones, so the normal must lie along an axis of the
        // part. A tilted normal is refused here, before any drawing is created.
        var view = FlatPartProjection.Choose(plan.Normal)
            ?? throw new KompasContractException(
                ErrorCodes.FlatNormalNotAxis,
                "Нормаль плоской детали "
                + DescribeVector(plan.Normal)
                + " не совпадает ни с одной осью X, Y, Z: вид по произвольной нормали в v24 не "
                + "поддерживается (OrientationNormalTo описан в справке, но отсутствует в поставляемой "
                + "библиотеке типов). Поверните деталь так, чтобы её плоская грань была перпендикулярна "
                + "оси, и повторите.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["normal"] = plan.Normal,
                    ["thickness_mm"] = plan.ThicknessMm,
                    ["field"] = "body_ref",
                });

        var gabarit = ReadBodyGabarit(body);
        var directory = Path.GetDirectoryName(command.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var application = RequireApplication7(document);
        var bridge = BridgeFor(document);
        var drawingPath = Path.Combine(Path.GetTempPath(), "kompas-flatpart-" + Guid.NewGuid().ToString("N") + ".cdw");
        var rawPath = Path.Combine(Path.GetTempPath(), "kompas-flatpart-" + Guid.NewGuid().ToString("N") + ".dxf");
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
                    "Временный чертёж не отдал коллекцию видов (IViews): вид не построен, файл не "
                    + "выгружался.",
                    RetryPolicy.ReacquireContext);

            var before = ViewNumbers(views);
            bool accepted;
            try
            {
                // DOC: the second argument is the model orientation name, and an empty name is the
                // document's own; the view is chosen by the standard-view mask, not by that name.
                accepted = views.AddStandartViews(
                    document.Path, string.Empty, new object[] { view.Flag }, 100d, 100d, 1d, 10d, 10d);
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "AddStandartViews бросил исключение: " + ex.Message + ". Вид «" + view.Name
                    + "» не построен.",
                    RetryPolicy.SameOperationId);
            }

            if (!accepted)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "AddStandartViews вернул false: ядро отказало в построении вида «" + view.Name + "».",
                    RetryPolicy.SameOperationId);
            }

            var created = ViewNumbers(views).Except(before).ToArray();
            if (created.Length == 0)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "AddStandartViews принят, но новых видов в чертеже нет: вид «" + view.Name
                    + "» не появился.",
                    RetryPolicy.SameOperationId);
            }

            // INVARIANT: the contour is the outline of the body, so hidden edges stay out — a hole's far
            // rim would otherwise arrive as a second, coincident circle.
            ApplyFlatView(views, created[0], unverified);
            frame = ReadGabaritOfView(drawing5, document, created[0]);
            if (frame is null)
            {
                unverified.Add("view_frame_not_read — рамка вида не прочитана (ksGetObjGabaritRect): "
                    + "сущности за пределами контура не отсекаются по окну вида");
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
        var verdict = JudgeContour(check, faceArea, gabarit, plan.ThicknessMm);

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
                    ["view"] = view.Name,
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
            new("contour_size_matches_model", verdict is null && gabarit is not null,
                Observed: DescribeBox(check.Box), Expected: DescribeExtents(gabarit)),
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
        DxfFlatCheckResult check, double? faceArea, IReadOnlyList<double>? gabarit, double? thickness)
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

        if (gabarit is not { Count: 3 })
        {
            return null;
        }

        var contour = new[] { check.Box.Width, check.Box.Height };
        var model = gabarit.OrderByDescending(value => value).ToArray();
        var expected = model.Take(2).OrderByDescending(value => value).ToArray();
        var actual = contour.OrderByDescending(value => value).ToArray();
        if (Math.Abs(actual[0] - expected[0]) > SizeToleranceMm || Math.Abs(actual[1] - expected[1]) > SizeToleranceMm)
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

    private static string DescribeExtents(IReadOnlyList<double>? extents) =>
        extents is null
            ? "не прочитан"
            : string.Join(" x ", extents.OrderByDescending(value => value)
                .Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))) + " мм";
}

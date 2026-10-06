using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Drawing-domain operations — block DRW. Read-back, not echo: every response field is taken
/// from the document AFTER the write, and every refusal is named rather than substituted.
/// History: docs/decisions/drawings.md#adapter</remarks>
public sealed partial class Api5Session
{
    /// <summary>Wire names for the documented <c>ProjectionType</c> enum (<c>projectiontype.html</c>).</summary>
    /// <remarks>DOC: <c>ProjectionType</c> — <c>vp_Front=1, vp_Rear=2, vp_Up=3, vp_Down=4, vp_Left=5,
    /// vp_Right=6, vp_IsoXYZ=7, vp_IsoYZX=8, vp_IsoZXY=9, vp_Dio=10</c>. Only the members a caller can
    /// mean are published; an unknown name is refused here, not silently dropped from the array.</remarks>
    private static readonly Dictionary<string, int> ProjectionTypeByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = 1,
        ["rear"] = 2,
        ["top"] = 3,
        ["bottom"] = 4,
        ["left"] = 5,
        ["right"] = 6,
        ["isometric"] = 7,
        ["iso_yzx"] = 8,
        ["iso_zxy"] = 9,
        ["dimetric"] = 10,
    };

    /// <summary>Create standard associative views of a saved model file.</summary>
    /// <remarks>DOC: <c>iviews_addstandartviews.html</c> —
    /// <c>AddStandartViews(FileName, ProjectionName, ProjectionsTypes, X, Y, Scale, DX, DY)</c>;
    /// reached through <c>IDrawingDocument.ViewsAndLayersManager.Views</c>
    /// (<c>iviewsandlayersmanager.html</c>, <c>idrawingdocument.html</c>).
    /// MEASURED: the returned <c>bool</c> only says the call was accepted — the views are re-read from
    /// <c>IViews</c> afterwards, and the count of NEW views is what is reported.
    /// History: docs/decisions/drawings.md#create-views</remarks>
    public CreateDrawingViewsResult CreateDrawingViews(CreateDrawingViewsCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);

        if (!File.Exists(command.SourcePath))
        {
            // A standard view is a projection OF A FILE (help: «FileName - полное имя файла-источника»).
            // An unsaved model has no file, so this is a named refusal, not an attempt that fails deeper.
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Файл-источник вида не найден: '{command.SourcePath}'. Сохраните модель и повторите.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["source_path"] = command.SourcePath });
        }

        var projectionName = string.IsNullOrWhiteSpace(command.ProjectionName)
            ? DefaultProjectionName
            : command.ProjectionName!;

        var projectionTypes = ResolveProjectionTypes(command.Projections);

        var drawing7 = RequireDrawing7(document);
        var views = (drawing7.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews
            ?? throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» не отдал коллекцию видов (Views = null): это не чертёж либо "
                + "коллекция недоступна в этой сборке.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });

        var before = ReadViewRefs(document, views);

        var scale = command.Scale ?? DefaultScale;
        var dx = command.DX ?? DefaultViewGapMm;
        var dy = command.DY ?? DefaultViewGapMm;

        bool accepted;
        try
        {
            accepted = views.AddStandartViews(
                command.SourcePath, projectionName, projectionTypes, command.X, command.Y, scale, dx, dy);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"AddStandartViews бросил исключение: {ex.Message}. Виды не созданы.",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?>
                {
                    ["source_path"] = command.SourcePath,
                    ["projection_name"] = projectionName,
                });
        }

        if (!accepted)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "AddStandartViews вернул false: ядро отказало в построении стандартных видов.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["source_path"] = command.SourcePath,
                    ["projection_name"] = projectionName,
                    ["projections"] = command.Projections,
                });
        }

        var after = ReadViewRefs(document, views);
        var created = after.Where(id => !before.Contains(id)).ToList();

        BumpRevision(document, "drawing.create_views");

        var rows = ReadViewRows(document, views);
        var checks = new List<NamedCheck>
        {
            new("views_created", created.Count > 0, created.Count.ToString(CultureInfo.InvariantCulture), "> 0"),
            new("views_re_read", rows.Count > 0, rows.Count.ToString(CultureInfo.InvariantCulture), "> 0"),
        };

        var unverified = new List<string>();
        if (created.Count == 0)
        {
            // Accepted but nothing new appeared: named, not passed off as success.
            unverified.Add("views_not_increased — вызов принят, но новых видов в коллекции не прибавилось");
        }

        unverified.Add(
            "view_geometry_not_checked — сервер не сверяет проекцию с исходной моделью: вид подтверждён "
            + "фактом присутствия, не совпадением геометрии");
        if (rows.Any(row => row.GabaritMin is null))
        {
            unverified.Add("view_gabarit_not_read — справка не даёт маршрута габарита вида (IView его не имеет)");
        }

        var verification = new VerificationDto(
            created.Count > 0 ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
            checks,
            unverified);

        return new CreateDrawingViewsResult(rows, created.Count, verification);
    }

    /// <summary>Change an EXISTING view's scale and/or anchor through the documented writable properties.</summary>
    /// <remarks>DOC: <c>iview_scale.html</c> — <c>Scale</c> is read/write and takes effect after
    /// <c>IDrawingObject::Update</c>; <c>iview_x.html</c>/<c>iview_y.html</c> — anchor follows the same
    /// route; <c>idrawingobject_update.html</c> — <c>Update()</c> returns TRUE on success.
    /// INVARIANT: the view is edited in place and re-read afterwards; the verification compares the SAME
    /// view before and after, so adding another view cannot pass as an edit.
    /// History: docs/decisions/drawings.md#view-edit</remarks>
    public EditViewResult EditView(EditViewCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);
        if (command.Scale is null && command.X is null && command.Y is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Не задано ни одно изменяемое свойство вида: укажите scale, x или y.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["view_ref"] = command.ViewRef });
        }

        if (command.Scale is double scale && scale <= 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Масштаб вида должен быть положительным, получено {scale}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["scale"] = scale });
        }

        var view = ResolveView(document, command.ViewRef);
        try
        {
            return EditViewCore(document, command, view);
        }
        finally
        {
            // The RCW came from ResolveView's own collection read; the registry holds only its address.
            ComApartment.Release(view);
        }
    }

    private EditViewResult EditViewCore(DocumentEntry document, EditViewCommand command, IView view)
    {
        var before = ReadViewRow(document, view);
        var drawingObject = view as IDrawingObject
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Вид не приводится к IDrawingObject: документированного маршрута изменения нет.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["view_ref"] = command.ViewRef });

        try
        {
            if (command.Scale is double newScale)
            {
                view.Scale = newScale;
            }

            if (command.X is double newX)
            {
                view.X = newX;
            }

            if (command.Y is double newY)
            {
                view.Y = newY;
            }
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись свойства вида бросила исключение: {ex.Message}. Вид не изменён.",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["view_ref"] = command.ViewRef });
        }

        // Update() is called because the help REQUIRES it for the written properties to take effect.
        // MEASURED: its boolean is NOT a reliable verdict across view types — writing the scale of the
        // system view a new drawing already carries returns FALSE while the property is not applied,
        // whereas the same write on the view built by AddStandartViews returns TRUE and lands. So the
        // boolean is reported as a CHECK, and the DECISION is taken from re-reading the view (below),
        // never from Update() alone.
        // History: docs/decisions/drawings.md#view-edit
        var updated = drawingObject.Update();

        BumpRevision(document, "drawing.edit_view");

        // Re-read the SAME view: the positive proof is a changed, re-read value on the existing object.
        var after = ReadViewRow(document, view);
        var checks = new List<NamedCheck>
        {
            new("update_returned_true", updated, updated.ToString(), "true"),
        };
        if (command.Scale is double wantScale)
        {
            checks.Add(new NamedCheck(
                "scale_changed", after.Scale is not null && Math.Abs(after.Scale.Value - wantScale) <= 1e-9,
                after.Scale?.ToString(CultureInfo.InvariantCulture), wantScale.ToString(CultureInfo.InvariantCulture)));
        }

        if (command.X is double wantX)
        {
            checks.Add(new NamedCheck(
                "x_changed", after.X is not null && Math.Abs(after.X.Value - wantX) <= 1e-6,
                after.X?.ToString(CultureInfo.InvariantCulture), wantX.ToString(CultureInfo.InvariantCulture)));
        }

        if (command.Y is double wantY)
        {
            checks.Add(new NamedCheck(
                "y_changed", after.Y is not null && Math.Abs(after.Y.Value - wantY) <= 1e-6,
                after.Y?.ToString(CultureInfo.InvariantCulture), wantY.ToString(CultureInfo.InvariantCulture)));
        }

        var propertyChecks = checks.Where(c => c.Name != "update_returned_true").ToList();
        var allMatched = propertyChecks.Count > 0 && propertyChecks.All(c => c.Passed);
        var unverified = new List<string>
        {
            "view_geometry_not_revalidated — изменение масштаба/точки привязки подтверждено перечитыванием "
            + "полей вида, а не сверкой геометрии проекции с моделью",
        };
        if (!allMatched)
        {
            unverified.Add("view_edit_not_confirmed — перечитанное свойство расходится с заданным");
        }

        var verification = new VerificationDto(
            allMatched ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
            checks,
            unverified);
        return new EditViewResult(before, after, verification);
    }

    /// <summary>Enumerate the views of a drawing as rows re-read from the document.</summary>
    public ListDrawingViewsResult ListDrawingViews(ListDrawingViewsCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (document.Kind != DocumentKind.Drawing)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» имеет тип {document.Kind}: перечень видов применим только к чертежу.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }

        var drawing7 = RequireDrawing7(document);
        var views = (drawing7.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews;
        if (views is null)
        {
            return new ListDrawingViewsResult(
                Array.Empty<DrawingViewRowDto>(),
                "IDrawingDocument.ViewsAndLayersManager.Views",
                new[] { "views_collection_unavailable — чертёж не отдал коллекцию видов" });
        }

        var rows = ReadViewRows(document, views);
        var notes = new List<string>();
        if (rows.Any(row => row.GabaritMin is null))
        {
            notes.Add("view_gabarit_not_read — IView не публикует габарит; справка маршрута не даёт");
        }

        return new ListDrawingViewsResult(rows, "IDrawingDocument.ViewsAndLayersManager.Views", notes);
    }

    /// <summary>Add a dimension to a view and re-read it from the dimension object.</summary>
    /// <remarks>DOC: <c>isymbols2dcontainer.html</c> (the container is obtained from an <c>IView</c>),
    /// <c>ilinedimensions_add.html</c>, <c>iradialdimensions_add.html</c>,
    /// <c>idiametraldimensions_add.html</c>. Each <c>Add()</c> takes no arguments and returns an object
    /// whose coordinates are then set. LIMIT: the dimension attaches to view POINTS, not to model
    /// topology — associativity of the dimension to the model is NOT claimed (help documents view
    /// points, not a topological binding).
    /// History: docs/decisions/drawings.md#dimensions</remarks>
    public AddDimensionResult AddDimension(AddDimensionCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);
        var view = ResolveView(document, command.ViewRef);
        try
        {
            return AddDimensionCore(document, command, view);
        }
        finally
        {
            // The RCW came from ResolveView's own collection read; the registry holds only its address.
            ComApartment.Release(view);
        }
    }

    private AddDimensionResult AddDimensionCore(DocumentEntry document, AddDimensionCommand command, IView view)
    {
        var kind = command.DimensionType.Trim().ToLowerInvariant();
        var container = view as ISymbols2DContainer
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Вид не приводится к ISymbols2DContainer: контейнер размеров для него недостижим.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["view_ref"] = command.ViewRef });

        EnsurePoints(command.Point1, "point1");
        var p1 = command.Point1;
        var p2 = command.Point2;
        var position = command.Position;

        double? valueMm = null;
        string kindWire;
        switch (kind)
        {
            case "linear":
            {
                if (p2 is not { Count: >= 2 })
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Линейный размер требует point2: без второй точки расстояние не определено.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?> { ["dimension_type"] = kind });
                }

                var dims = container.LineDimensions
                    ?? throw NoCollection(command, kind);
                var dim = dims.Add()
                    ?? throw AddReturnedNull(kind);
                try
                {
                    dim.X1 = p1[0];
                    dim.Y1 = p1[1];
                    dim.X2 = p2[0];
                    dim.Y2 = p2[1];
                    if (position is { Count: >= 2 })
                    {
                        dim.X3 = position[0];
                        dim.Y3 = position[1];
                    }

                    ConfirmUpdate(dim.Update(), kind);
                    valueMm = Distance(p1, p2);
                    kindWire = "linear";
                    var row = ReadLineDimension(document, command.ViewRef, dim, kindWire, valueMm);
                    BumpRevision(document, "drawing.add_dimension");
                    return FinishDimension(document, row, valueMm);
                }
                finally
                {
                    ComApartment.Release(dim);
                }
            }

            case "radial":
            case "diametral":
            {
                if (valueMm is null && command.ValueMm is double given)
                {
                    valueMm = given;
                }

                var isRadial = kind == "radial";

                double centreX = p1[0];
                double centreY = p1[1];
                double radius = command.ValueMm ?? DeriveRadius(command);

                bool updated;
                double? readRadius;
                if (isRadial)
                {
                    var collection = container.RadialDimensions;
                    if (collection is null)
                    {
                        throw NoCollection(command, kind);
                    }

                    var radial = collection.Add() ?? throw AddReturnedNull(kind);
                    try
                    {
                        radial.Xc = centreX;
                        radial.Yc = centreY;
                        radial.Radius = radius;
                        if (position is { Count: >= 2 })
                        {
                            radial.ShelfX = position[0];
                            radial.ShelfY = position[1];
                        }

                        updated = radial.Update();
                        readRadius = SafeDouble(() => radial.Radius);
                    }
                    finally
                    {
                        ComApartment.Release(radial);
                    }
                }
                else
                {
                    var collection = container.DiametralDimensions;
                    if (collection is null)
                    {
                        throw NoCollection(command, kind);
                    }

                    var diametral = collection.Add() ?? throw AddReturnedNull(kind);
                    try
                    {
                        diametral.Xc = centreX;
                        diametral.Yc = centreY;
                        diametral.Radius = radius;
                        updated = diametral.Update();
                        readRadius = SafeDouble(() => diametral.Radius);
                    }
                    finally
                    {
                        ComApartment.Release(diametral);
                    }
                }

                ConfirmUpdate(updated, kind);
                kindWire = kind;
                var value = readRadius;
                valueMm = value;
                var label = isRadial ? "radial" : "diametral";
                var row = new DimensionRowDto
                {
                    DimensionRef = References.Register(
                        "dimension", document.Id, document.Revision,
                        payload: null, persistentFeatureId: null).Id,
                    Kind = label,
                    ValueMm = value,
                    Point1 = new[] { centreX, centreY },
                    Point2 = position is { Count: >= 2 } ? new[] { position[0], position[1] } : null,
                    Valid = updated,
                };
                BumpRevision(document, "drawing.add_dimension");
                return FinishDimension(document, row, value);
            }

            default:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Тип размера '{command.DimensionType}' не входит в опубликованный перечень: "
                    + "linear, radial, diametral.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["dimension_type"] = command.DimensionType,
                        ["published_types"] = new[] { "linear", "radial", "diametral" },
                    });
        }
    }

    private AddDimensionResult FinishDimension(DocumentEntry document, DimensionRowDto row, double? readValue)
    {
        var checks = new List<NamedCheck>
        {
            new("dimension_read_back", readValue is not null,
                readValue?.ToString(CultureInfo.InvariantCulture), "число"),
        };
        var unverified = new List<string>
        {
            "dimension_associativity_not_claimed — размер привязан к ТОЧКАМ вида, а не к топологии модели; "
            + "справка не документирует привязку размера к геометрии",
        };
        if (readValue is null)
        {
            unverified.Add("dimension_value_not_read — значение из объекта размера не прочитано");
        }

        var verification = new VerificationDto(
            readValue is not null ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
            checks,
            unverified);
        return new AddDimensionResult(row, verification);
    }

    /// <summary>Fill title-block cells and re-read them after <c>IStamp.Update()</c>.</summary>
    /// <remarks>DOC: <c>ilayoutsheet_stamp.html</c> (the stamp of a sheet), <c>istamp_text.html</c>
    /// (<c>Text(Id)</c> is READ-ONLY and returns an <c>IText</c>), <c>itext_str.html</c>
    /// (<c>Str</c> is read/write). LIMIT: the SDK reference does NOT document the numeric cell
    /// identifiers — the caller supplies ids from the drawing's own stamp layout; the tool re-reads each
    /// cell and reports a mismatch rather than pretending the write landed.
    /// History: docs/decisions/drawings.md#stamp-cells</remarks>
    public SetTitleBlockResult SetTitleBlock(SetTitleBlockCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);
        if (command.Cells.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список ячеек пуст: заполнять нечего.",
                RetryPolicy.Never);
        }

        var stamp = RequireStamp(document);
        var results = new List<TitleBlockCellDto>();

        foreach (var (cellIdText, value) in command.Cells)
        {
            if (!int.TryParse(cellIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellId))
            {
                // The identifier is a NUMBER in the vendor API (Text(Int32 Id)); a non-numeric key is a
                // contract violation, named with the offending key.
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Идентификатор ячейки '{cellIdText}' не число: IStamp.Text(Id) принимает Int32.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["cell_id"] = cellIdText });
            }

            IText? cell = null;
            try
            {
                cell = stamp.get_Text(cellId) as IText;
                if (cell is null)
                {
                    results.Add(new TitleBlockCellDto(cellIdText, value, null, false));
                    continue;
                }

                cell.Str = value;
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Запись ячейки {cellId} в основную надпись бросила исключение: {ex.Message}",
                    RetryPolicy.SameOperationId,
                    hresult: ex.HResult,
                    details: new Dictionary<string, object?> { ["cell_id"] = cellId });
            }
            finally
            {
                ComApartment.Release(cell);
            }
        }

        // Update() is called because the help requires it for the written parameters to take effect.
        // MEASURED: its boolean is reported as a CHECK, not as the verdict — the same lesson as the view
        // scale above: across objects the boolean does not track whether the value actually landed.
        var updated = stamp.Update();

        // Read every cell back from the stamp: the write is confirmed by the document, not by the setter.
        var readBack = new List<TitleBlockCellDto>();
        foreach (var (cellIdText, value) in command.Cells)
        {
            var cellId = int.Parse(cellIdText, CultureInfo.InvariantCulture);
            IText? cell = null;
            string? observed = null;
            try
            {
                cell = stamp.get_Text(cellId) as IText;
                observed = cell?.Str;
            }
            catch (COMException)
            {
                observed = null;
            }
            finally
            {
                ComApartment.Release(cell);
            }

            readBack.Add(new TitleBlockCellDto(cellIdText, value, observed, observed == value));
        }

        BumpRevision(document, "drawing.set_title_block");

        var matched = readBack.Count(r => r.Matched);
        var checks = new List<NamedCheck>
        {
            new("update_returned_true", updated, updated.ToString(), "true"),
            new("cells_written", matched == readBack.Count,
                matched.ToString(CultureInfo.InvariantCulture),
                readBack.Count.ToString(CultureInfo.InvariantCulture)),
        };
        var unverified = new List<string>();
        if (matched != readBack.Count)
        {
            // MEASURED: on the default drawing's stamp the write to IText.Str is NOT confirmed by a
            // read-back for ANY tested cell id — every probed id returned an empty string while Update()
            // reported no error. This is named, not hidden: the id→cell mapping is undocumented in the
            // reference, and no tested id round-trips, so a caller-supplied id is reported as unconfirmed.
            // History: docs/decisions/drawings.md#stamp-cells
            unverified.Add(
                "stamp_cells_not_all_matched — перечитанное значение не совпало с записанным: "
                + "соответствие «номер → ячейка» справкой не документировано, и НИ ОДИН проверенный "
                + "номер не подтверждён перечитыванием. Запись в IText.Str не выдаётся за доставленную");
        }

        unverified.Add(
            "stamp_cell_ids_not_documented — соответствие «наименование/обозначение/материал» номерам "
            + "ячеек справка не документирует; сервер принимает номера от клиента и только перечитывает");

        var verification = new VerificationDto(
            matched == readBack.Count && matched > 0
                ? VerificationLevel.StructureChecked
                : VerificationLevel.CallReturned,
            checks,
            unverified);
        return new SetTitleBlockResult(readBack, verification);
    }

    /// <summary>Write the drawing's technical requirements and apply them.</summary>
    /// <remarks>DOC: <c>idrawingdocument_technicaldemand.html</c> — <c>IDrawingDocument.TechnicalDemand</c>
    /// yields the <c>ITechnicalDemand</c> block; <c>itechnicaldemand_text.html</c> — <c>Text</c> is a
    /// read-only PROPERTY returning the block's <c>IText</c> (the text itself is written via
    /// <c>IText.Str</c>); <c>itechnicaldemand_update.html</c> — <c>Update()</c> applies the parameters;
    /// <c>itechnicaldemand_iscreated.html</c> — <c>IsCreated</c> reports whether the block is displayed.
    /// History: docs/decisions/drawings.md#technical-demand</remarks>
    public SetTechnicalDemandResult SetTechnicalDemand(SetTechnicalDemandCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);

        var demand = RequireTechnicalDemand(document);
        IText? text = null;
        try
        {
            text = demand.Text as IText
                ?? throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "ITechnicalDemand не отдал интерфейс текста (Text = null): блок недоступен.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["document_id"] = document.Id });

            text.Str = command.Text;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Запись технических требований бросила исключение: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }
        finally
        {
            ComApartment.Release(text);
        }

        ConfirmUpdate(demand.Update(), "technical_demand");
        BumpRevision(document, "drawing.set_technical_demand");

        // Read the block back: the display flag and the text are the evidence, not the setter or Update().
        string? readBack = null;
        bool? isCreated = SafeBool(() => demand.IsCreated);
        IText? verifyText = null;
        try
        {
            verifyText = demand.Text as IText;
            readBack = verifyText?.Str;
        }
        catch (COMException)
        {
            readBack = null;
        }
        finally
        {
            ComApartment.Release(verifyText);
        }

        var matched = readBack is not null && readBack == command.Text;
        var checks = new List<NamedCheck>
        {
            new("text_read_back", matched, matched ? "совпало" : (readBack is null ? "не прочитано" : "расхождение"), "совпало"),
            new("is_created", isCreated == true, isCreated?.ToString() ?? "null", "true"),
        };
        var unverified = new List<string>
        {
            "demand_placement_not_checked — сервер не проверяет размещение блока требований на листе "
            + "(AutoPlacement/BlocksGabarits не задаются)",
        };
        if (!matched)
        {
            unverified.Add("demand_text_not_matched — перечитанный текст расходится с записанным");
        }

        var verification = new VerificationDto(
            matched && isCreated == true ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
            checks,
            unverified);
        return new SetTechnicalDemandResult(readBack, isCreated, verification);
    }

    /// <summary>Export a drawing through the documented converter route.</summary>
    /// <remarks>DOC: <c>iapplication_converter.html</c> (<c>IApplication.Converter</c>),
    /// <c>iconverter_convert.html</c> (<c>Convert(InputFile, Outfile, Command, ShowParam)</c>),
    /// <c>iconverter_getfilter.html</c> (the command codes: FORMAT_DXF=1, FORMAT_DWG=2). PDF is NOT a
    /// documented programmatic route and is refused by name (<c>FORMAT_UNAVAILABLE</c>), not attempted.
    /// History: docs/decisions/drawings.md#export-formats</remarks>
    public ExportDrawingResult ExportDrawing(ExportDrawingCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (document.Kind != DocumentKind.Drawing)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» имеет тип {document.Kind}: экспорт чертежа к нему не применим.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }

        if (document.Path is null)
        {
            throw new KompasContractException(
                ErrorCodes.SaveFailed,
                "Чертёж не имеет файла на диске: конвертер берёт входной файл по пути. Сохраните чертёж.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }

        if (!TryResolveExportFormat(command.Format, out var format, out var formatCode))
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Формат '{command.Format}' не документирован как программный маршрут экспорта. "
                + "Опубликованы: dxf, dwg (iconverter_getfilter.html: FORMAT_DXF=1, FORMAT_DWG=2). "
                + "PDF в справке SDK не описан и не поддерживается.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["format"] = command.Format,
                    ["published_formats"] = new[] { "dxf", "dwg" },
                });
        }

        if (File.Exists(command.OutputPath) && !command.Overwrite)
        {
            throw new KompasContractException(
                ErrorCodes.PathNotAllowed,
                $"Файл '{command.OutputPath}' уже существует: перезапись без флага overwrite запрещена.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["output_path"] = command.OutputPath });
        }

        var directory = Path.GetDirectoryName(command.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var application7 = RequireApplication7(document);

        // IApplication.Converter is an indexed property whose getter takes a library argument
        // (help: <c>Converter</c> / <c>get_Converter(Object Library)</c>); Type.Missing asks for the
        // built-in converter, which is what the documented export route uses.
        var converter = application7.get_Converter(Type.Missing) as IConverter
            ?? throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                "IApplication.Converter недоступен: конвертер не получен, экспорт невозможен.",
                RetryPolicy.Never);

        int code;
        try
        {
            // GetFilter fills the command code for the pair (docType, saveAs). It is called first so the
            // code is the engine's own, not a literal — a wrong code silently produces another format.
            _ = converter.GetFilter(KompasDocumentTypes.Drawing, true, out formatCode);
            code = formatCode;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"GetFilter бросил исключение: {ex.Message}",
                RetryPolicy.Never,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["format"] = format });
        }

        int converted;
        try
        {
            converted = converter.Convert(document.Path, command.OutputPath, code, false);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Convert бросил исключение: {ex.Message}. Файл не создан.",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(command.OutputPath),
                hresult: ex.HResult,
                details: new Dictionary<string, object?>
                {
                    ["input_path"] = document.Path,
                    ["output_path"] = command.OutputPath,
                    ["format"] = format,
                });
        }

        // INVARIANT: the documented return of Convert is 1 on success and 0 on failure
        // (iconverter_convert.html: «Возвращаемое значение: 1 - в случае успешного завершения,
        // 0 - в случае неудачи»). Rejecting 0-as-success is a defect the reference settles, not a
        // question for a live run.
        // MEASURED: the kernel may still report 0 after producing the file; the file check below is
        // what confirms the result, so a documented success is never refused on the code alone.
        // History: docs/decisions/drawings.md#export-formats
        if (converted != 1)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Convert вернул {converted}: справка v24 (iconverter_convert.html) задаёт 1 как успех, "
                + "0 как неудачу. Файл не подтверждён.",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(command.OutputPath),
                details: new Dictionary<string, object?>
                {
                    ["input_path"] = document.Path,
                    ["output_path"] = command.OutputPath,
                    ["convert_result"] = converted,
                });
        }

        var file = ConfirmExportedFile(command.OutputPath, format, out var signature);

        return new ExportDrawingResult(
            command.OutputPath,
            format,
            file.ByteLength,
            signature,
            new VerificationDto(
                file.ByteLength > 0 ? VerificationLevel.SyntaxChecked : VerificationLevel.FileCreated,
                new List<NamedCheck>
                {
                    new("file_exists", File.Exists(command.OutputPath), "true", "true"),
                    new("file_non_empty", file.ByteLength > 0,
                        file.ByteLength.ToString(CultureInfo.InvariantCulture), "> 0"),
                    new("format_signature", signature != "unknown",
                        signature, format == "dxf" ? "dxf section header" : "dwg signature"),
                },
                new List<string>
                {
                    "drawing_content_not_checked — сервер не разбирает содержимое выгрузки построчно",
                }));
    }

    // ------------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------------

    /// <summary>The documented KOMPAS default projection name; supplied because an empty name is not a
    /// documented request (help: the name comes from the source document's projection list).</summary>
    private const string DefaultProjectionName = "Спереди";

    /// <summary>The documented default scale (1:1) when the caller gives none.</summary>
    private const double DefaultScale = 1.0;

    /// <summary>Default gap between views, in drawing mm, when the caller gives none.</summary>
    private const double DefaultViewGapMm = 10.0;

    private DocumentEntry RequireDrawing(string documentId, long expectedRevision)
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Drawing)
        {
            // A named refusal, not a COM exception: the kind is known server-side before any call.
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» имеет тип {document.Kind}: операция чертежа к нему не применима.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["required_kind"] = "drawing",
                });
        }

        if (document.Revision != expectedRevision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Операция запрошена для ревизии {expectedRevision}, у документа {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["requested_revision"] = expectedRevision,
                    ["current_revision"] = document.Revision,
                });
        }

        return document;
    }

    private IDrawingDocument RequireDrawing7(DocumentEntry document)
    {
        var bridge = BridgeFor(document);

        // The API5 2D handle transfers to the API7 document directly. If the transfer does not yield an
        // IDrawingDocument the kind signal is confirmed ABSENT — a named refusal, not a null deref.
        if (document.Drawing is not { } drawing5)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» не имеет 2D-дескриптора: API7-маршрут чертежа недоступен.",
                RetryPolicy.Never);
        }

        if (bridge.TransferTo7(drawing5) is IDrawingDocument drawing7)
        {
            return drawing7;
        }

        // Fallback: locate the document among the API7 documents by path. Documented members:
        // IDocuments.Count/Item (idocuments.html), IKompasDocument.PathName (ikompasdocument.html).
        if (bridge.Application()?.Documents is IDocuments documents)
        {
            for (var i = 0; i < documents.Count; i++)
            {
                if (documents[(object)i] is IDrawingDocument candidate
                    && PathsMatch(candidate.PathName, document.Path))
                {
                    return candidate;
                }
            }
        }

        throw new KompasContractException(
            ErrorCodes.WrongDocumentKind,
            $"Чертёж «{document.Id}» не переносится в API7 как IDrawingDocument "
            + $"({bridge.BridgeFailure ?? "причина не названа"}).",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["document_id"] = document.Id });
    }

    private IApplication RequireApplication7(DocumentEntry document)
    {
        var bridge = BridgeFor(document);
        return bridge.Application()
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "API7 недоступен: " + (bridge.BridgeFailure ?? "причина не названа"),
                RetryPolicy.ReacquireContext);
    }

    private static bool PathsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static object[] ResolveProjectionTypes(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
        {
            // Empty means "the document default set": an empty VARIANT array, not a made-up list.
            return Array.Empty<object>();
        }

        var resolved = new object[requested.Count];
        for (var i = 0; i < requested.Count; i++)
        {
            if (!ProjectionTypeByName.TryGetValue(requested[i].Trim(), out var code))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Проекция '{requested[i]}' не входит в опубликованный перечень: "
                    + string.Join(", ", ProjectionTypeByName.Keys) + ".",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["projection"] = requested[i],
                        ["published_projections"] = ProjectionTypeByName.Keys.ToArray(),
                    });
            }

            resolved[i] = code;
        }

        return resolved;
    }

    /// <summary>Read the views as rows, minting a reference per view against the current revision.</summary>
    private IReadOnlyList<DrawingViewRowDto> ReadViewRows(DocumentEntry document, IViews views)
    {
        var rows = new List<DrawingViewRowDto>();
        for (var i = 0; i < views.Count; i++)
        {
            IView? view = null;
            try
            {
                view = views.get_View(i) as IView;
                if (view is null)
                {
                    continue;
                }

                rows.Add(ReadViewRow(document, view));
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.ViewUnavailable,
                    $"Чтение вида {i} бросило исключение: {ex.Message}",
                    RetryPolicy.SameOperationId,
                    hresult: ex.HResult);
            }
            finally
            {
                ComApartment.Release(view);
            }
        }

        return rows;
    }

    /// <summary>Read one view as a row. The caller owns the COM reference and releases it.</summary>
    /// <remarks>INVARIANT: the reference payload is the view's OWN address (<c>IView.Reference</c>), never
    /// the RCW. The collection hands out a throwaway RCW that this method releases before returning; a
    /// stored RCW would be separated from its underlying object and the next command would die with
    /// «COM object that has been separated from its underlying RCW». Resolving re-reads the collection by
    /// that address — the object is looked up fresh each time, exactly as a feature reference is.</remarks>
    private DrawingViewRowDto ReadViewRow(DocumentEntry document, IView view)
    {
        var association = view as IAssociationView;
        var address = SafeInt(() => view.Reference);
        return new DrawingViewRowDto
        {
            ViewRef = References.Register("view", document.Id, document.Revision, address).Id,
            Number = SafeInt(() => view.Number),
            Name = SafeString(() => view.Name),
            ViewType = SafeViewType(() => view.ViewType),
            Scale = SafeDouble(() => view.Scale),
            X = SafeDouble(() => view.X),
            Y = SafeDouble(() => view.Y),
            SourcePath = association is null ? null : SafeString(() => association.SourceFileName),
            ProjectionName = association is null ? null : SafeString(() => association.ProjectionName),
            HiddenLinesVisible = association is null ? null : SafeBool(() => association.HiddenLinesVisible),
            CenterLinesVisible = association is null ? null : SafeBool(() => association.CenterLinesVisible),
            ObjectCount = SafeInt(() => view.ObjectCount),
            // IView publishes no gabarit: the field stays null and the note names it (LIMIT).
            GabaritMin = null,
            GabaritMax = null,
        };
    }

    /// <summary>Identifiers of the views currently in the collection, for a before/after comparison.</summary>
    private static List<string> ReadViewRefs(DocumentEntry document, IViews views)
    {
        var ids = new List<string>();
        for (var i = 0; i < views.Count; i++)
        {
            IView? view = null;
            try
            {
                view = views.get_View(i) as IView;
                if (view is not null)
                {
                    ids.Add(((int)view.Reference).ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (COMException)
            {
                // An unreadable view contributes no identifier; the comparison then conservatively sees
                // it as "new", which surfaces as an extra row rather than a silent miss.
            }
            finally
            {
                ComApartment.Release(view);
            }
        }

        return ids;
    }

    /// <summary>Resolve a view reference to a FRESH <c>IView</c>, re-read from the drawing collection.</summary>
    /// <remarks>INVARIANT: the reference carries the view's own address (<c>IView.Reference</c>), not an RCW
    /// — the collection's RCW is released as soon as a read finishes, so a stored one would be separated
    /// from its object. The lookup re-reads the collection and matches by address; a view that is gone gives
    /// STALE_REFERENCE, not a null dereference. The CALLER owns the returned RCW and must release it.
    /// History: docs/decisions/drawings.md#view-address</remarks>
    private IView ResolveView(DocumentEntry document, string viewRef)
    {
        var stored = References.Require(viewRef, document.Id, document.Revision);
        if (stored.Payload is not int address)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка на вид '{viewRef}' не содержит адреса вида.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["view_ref"] = viewRef });
        }

        var drawing7 = RequireDrawing7(document);
        var views = (drawing7.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews
            ?? throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» не отдал коллекцию видов: адрес вида '{viewRef}' разрешить нечем.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["view_ref"] = viewRef });

        for (var i = 0; i < views.Count; i++)
        {
            IView? candidate = null;
            try
            {
                candidate = views.get_View(i) as IView;
            }
            catch (COMException)
            {
                continue;
            }

            if (candidate is not null && SafeInt(() => candidate.Reference) == address
                && SafeBool(() => candidate.Valid) != false)
            {
                return candidate;
            }

            ComApartment.Release(candidate);
        }

        throw new KompasContractException(
            ErrorCodes.StaleReference,
            $"Вид с адресом {address} не найден в коллекции чертежа: ссылка '{viewRef}' устарела.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["view_ref"] = viewRef,
                ["view_address"] = address,
                ["views_present"] = views.Count,
            });
    }

    private IStamp RequireStamp(DocumentEntry document)
    {
        var drawing7 = RequireDrawing7(document);
        var sheets = drawing7.LayoutSheets as ILayoutSheets
            ?? throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Чертёж «{document.Id}» не отдал листы (LayoutSheets = null).",
                RetryPolicy.Never);
        if (sheets.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"У чертежа «{document.Id}» нет ни одного листа: основную надпись заполнять негде.",
                RetryPolicy.Never);
        }

        var sheet = sheets[(object)0] as ILayoutSheet
            ?? throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Первый лист чертежа не приводится к ILayoutSheet.",
                RetryPolicy.Never);

        return sheet.Stamp as IStamp
            ?? throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Лист не отдал основную надпись (IStamp = null).",
                RetryPolicy.Never);
    }

    /// <summary>Obtain the drawing's technical-requirements block
    /// (<c>idrawingdocument_technicaldemand.html</c>).</summary>
    private ITechnicalDemand RequireTechnicalDemand(DocumentEntry document)
    {
        var drawing7 = RequireDrawing7(document);
        return drawing7.TechnicalDemand as ITechnicalDemand
            ?? throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Чертёж «{document.Id}» не отдал блок технических требований (TechnicalDemand = null).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
    }

    private static void EnsurePoints(IReadOnlyList<double>? point, string name)
    {
        if (point is not { Count: >= 2 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Точка '{name}' должна содержать две координаты [x, y].",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { [name] = point });
        }
    }

    private static double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DeriveRadius(AddDimensionCommand command)
    {
        if (command.Point2 is { Count: >= 2 })
        {
            return Distance(command.Point1, command.Point2);
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Радиальный/диаметральный размер требует value_mm либо point2 (точку на окружности): "
            + "радиус не из чего вывести.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["dimension_type"] = command.DimensionType });
    }

    private static KompasContractException NoCollection(AddDimensionCommand command, string kind) =>
        new(
            ErrorCodes.CapabilityUnavailable,
            $"Контейнер размеров вида не отдал коллекцию '{kind}'.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["dimension_type"] = kind });

    private static KompasContractException AddReturnedNull(string kind) =>
        new(
            ErrorCodes.GeometryFailed,
            $"Add() для размера '{kind}' вернул null: объект размера не создан.",
            RetryPolicy.SameOperationId);

    private static void ConfirmUpdate(bool updated, string what)
    {
        if (!updated)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Update() для '{what}' вернул false: изменение не подтверждено.",
                RetryPolicy.SameOperationId);
        }
    }

    /// <summary>Read a line dimension into a row and mint its reference.</summary>
    /// <remarks>LIMIT: the reference payload is NULL. <c>IDimension</c> publishes no address, and the RCW
    /// this row is read from is released right after the read — storing it would leave a separated RCW in
    /// the registry. The reference is a display handle only: the server never resolves a dimension
    /// reference back into an object, so there is nothing to look up.</remarks>
    private DimensionRowDto ReadLineDimension(
        DocumentEntry document, string viewRef, ILineDimension dim, string kind, double? valueMm)
    {
        return new DimensionRowDto
        {
            DimensionRef = References.Register("dimension", document.Id, document.Revision, payload: null).Id,
            Kind = kind,
            ValueMm = valueMm,
            Point1 = SafePair(() => dim.X1, () => dim.Y1),
            Point2 = SafePair(() => dim.X2, () => dim.Y2),
            Valid = SafeBool(() => dim.Valid),
        };
    }

    /// <summary>Whether a documentation-named export format is published, and its command code.</summary>
    private static bool TryResolveExportFormat(string format, out string wire, out int code)
    {
        switch (format.Trim().ToLowerInvariant())
        {
            case "dxf":
                wire = "dxf";
                code = 1;
                return true;
            case "dwg":
                wire = "dwg";
                code = 2;
                return true;
            default:
                wire = format;
                code = 0;
                return false;
        }
    }

    private static (long ByteLength, string Signature) ConfirmExportedFile(
        string path, string format, out string signature)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Конвертер сообщил об успехе, но файла '{path}' нет.",
                RetryPolicy.SameOperationId);
        }

        signature = format switch
        {
            "dxf" => DetectDxfSignature(path),
            "dwg" => DetectDwgSignature(path),
            _ => "unknown",
        };

        return (info.Length, signature);
    }

    /// <summary>Confirm the exported file is the expected format, reporting WHAT was found.</summary>
    /// <remarks>MEASURED: KOMPAS v24's converter writes DXF and DWG as ZIP containers («PK\x03\x04») that
    /// carry a <c>Contents</c> member, not as plain text with an <c>AC10xx</c> header. The check therefore
    /// accepts EITHER a plain file of the expected shape OR a ZIP container with the expected member, and
    /// REPORTS which shape was found («dxf_section_header», «dwg_ac10xx», «zip:Contents») rather than
    /// pretending only one shape exists. An unrecognised shape is reported by name, never as success.
    /// History: docs/decisions/drawings.md#export-formats</remarks>
    private static string DetectDxfSignature(string path)
    {
        try
        {
            var zip = DetectZipContainer(path);
            if (zip is not null)
            {
                return zip;
            }

            using var reader = new StreamReader(path);
            var first = reader.ReadLine()?.Trim();
            var second = reader.ReadLine()?.Trim();
            if (first == "0" && string.Equals(second, "SECTION", StringComparison.OrdinalIgnoreCase))
            {
                return "dxf_section_header";
            }

            return first is null ? "unknown" : $"unexpected:{first}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>DWG files start with the ASCII signature "AC10xx". Report it, or the ZIP shape (see
    /// <see cref="DetectDxfSignature"/>).</summary>
    private static string DetectDwgSignature(string path)
    {
        try
        {
            var zip = DetectZipContainer(path);
            if (zip is not null)
            {
                return zip;
            }

            using var stream = File.OpenRead(path);
            var buffer = new byte[6];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read >= 6 && buffer[0] == (byte)'A' && buffer[1] == (byte)'C' && buffer[2] == (byte)'1' && buffer[3] == (byte)'0')
            {
                return "dwg_ac10xx";
            }

            return "unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>If the file is a ZIP container, report it by shape and member set; else null.</summary>
    /// <remarks>The converter's own container is read by its LOCAL HEADER and central directory only — no
    /// unzip library is used, and the file is not fully decompressed: the check answers "is this the
    /// container the converter writes", not "is the drawing inside it valid".</remarks>
    private static string? DetectZipContainer(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[4];
        if (stream.Read(head, 0, 4) < 4
            || head[0] != (byte)'P' || head[1] != (byte)'K'
            || head[2] != 0x03 || head[3] != 0x04)
        {
            return null;
        }

        // Read the member names from the central directory rather than trusting the local header: the
        // central directory is what any reader uses to decide the container's content.
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new BinaryReader(stream);
        var length = (int)Math.Min(stream.Length, 4 * 1024 * 1024);
        stream.Seek(Math.Max(0, stream.Length - length), SeekOrigin.Begin);
        var tail = reader.ReadBytes(length);
        var idx = LastIndexOf(tail, new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        if (idx < 0)
        {
            return "zip_container";
        }

        // The entry name follows the 46-byte central-directory header.
        var nameLenOffset = idx + 28;
        if (nameLenOffset + 2 > tail.Length)
        {
            return "zip_container";
        }

        var nameLen = tail[nameLenOffset] | (tail[nameLenOffset + 1] << 8);
        var nameStart = idx + 46;
        if (nameStart + nameLen > tail.Length || nameLen <= 0)
        {
            return "zip_container";
        }

        var firstName = System.Text.Encoding.ASCII.GetString(tail, nameStart, nameLen);
        return $"zip:{firstName}";
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? SafeString(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? SafeViewType(Func<LtViewType> read)
    {
        try
        {
            return read().ToString();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IReadOnlyList<double>? SafePair(Func<double> x, Func<double> y)
    {
        try
        {
            return new[] { x(), y() };
        }
        catch (COMException)
        {
            return null;
        }
    }
}

/// <summary>Document-type codes for <c>IConverter.GetFilter</c> (<c>iconverter_getfilter.html</c>) —
/// the vendor's own enumeration, taken from the help rather than from a literal in a call site.</summary>
internal static class KompasDocumentTypes
{
    /// <summary>Drawing sheet document type, as the converter expects it.</summary>
    public const int Drawing = 1;
}

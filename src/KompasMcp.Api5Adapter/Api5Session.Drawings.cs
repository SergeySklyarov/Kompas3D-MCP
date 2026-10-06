using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
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

    /// <summary>Rebuild the drawing after a model change so its associative view re-derives its geometry.</summary>
    /// <remarks>DOC: <c>ikompasdocument2d1_rebuilddocument.html</c> — <c>IKompasDocument2D1.RebuildDocument()</c>
    /// returns TRUE on success; reached from the API7 drawing document. MEASURED: the shipped interop
    /// declares <c>IKompasDocument2D1.RebuildDocument()</c>; <c>IDrawingDocument.RebuildViews</c> (named by
    /// the SDK reference) is declared by NO type of the assembly, so the documented-but-absent member is
    /// reported rather than guessed. INVARIANT: the returned boolean is a CHECK; the verdict is the view
    /// RE-READ after the rebuild, because the help's TRUE says "the call completed", not "the projection
    /// changed". History: docs/decisions/drawings.md#view-rebuild</remarks>
    public RebuildDrawingViewsResult RebuildDrawingViews(RebuildDrawingViewsCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);
        var drawing7 = RequireDrawing7(document);

        // The route is resolved BEFORE the call so its availability is a named fact: IView is reached
        // through the drawing, and the view is re-read from the SAME collection after the rebuild.
        var view = ResolveView(document, command.ViewRef);
        try
        {
            var before = ReadViewRow(document, view);

            if (drawing7 is not IKompasDocument2D1 document2d)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Чертёж не приводится к IKompasDocument2D1: документированного маршрута перестроения "
                    + "(RebuildDocument) нет. IDrawingDocument.RebuildViews справкой назван, но в "
                    + "поставляемом interop не объявлен НИ ОДНИМ типом (измерено отражением).",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = command.DocumentId,
                        ["stage"] = "cast_to_ikompasdocument2d1",
                    });
            }

            bool rebuilt;
            try
            {
                rebuilt = document2d.RebuildDocument();
            }
            catch (COMException ex)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"RebuildDocument бросил исключение: {ex.Message}. Перестроение не выполнено.",
                    RetryPolicy.SameOperationId,
                    hresult: ex.HResult,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = command.DocumentId,
                        ["stage"] = "rebuild_document_call",
                    });
            }

            BumpRevision(document, "drawing.rebuild_views");

            // The SAME view is re-read from a FRESH collection read: the object handle is not reused
            // across the rebuild, so the comparison is on the reopened collection, not a stale RCW.
            var (after, rereadNote) = ReadViewAgain(document, command.ViewRef, before);

            var checks = new List<NamedCheck>
            {
                new("rebuild_document_returned_true", rebuilt, rebuilt.ToString(), "true"),
                new("view_reread_after_rebuild", after is not null,
                    after is null ? "не перечитан" : "перечитан", "перечитан"),
            };
            var unverified = new List<string>
            {
                "view_geometry_not_compared — перестроение подтверждено вызовом и перечитыванием вида, "
                + "а не сверкой геометрии проекции с изменённой моделью (IView габарит не публикует)",
            };
            if (!rebuilt)
            {
                unverified.Add("rebuild_returned_false — документированный маршрут вернул FALSE");
            }

            var verification = new VerificationDto(
                rebuilt && after is not null ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks,
                unverified);

            var route = "IDrawingDocument(QI IKompasDocument2D1).RebuildDocument() → IViews re-read "
                        + "(IDrawingDocument.RebuildViews НЕ объявлен в interop — измерено)";
            return new RebuildDrawingViewsResult(
                rebuilt, before, after ?? before, route + (rereadNote is null ? "" : "; " + rereadNote),
                verification);
        }
        finally
        {
            ComApartment.Release(view);
        }
    }

    /// <summary>Re-read a view from a FRESH collection read after a rebuild, keyed by reference then by
    /// the stable (number + source) key — a rebuild may re-mint references, so the address is not reused.</summary>
    private (DrawingViewRowDto? Row, string? Note) ReadViewAgain(
        DocumentEntry document, string viewRef, DrawingViewRowDto before)
    {
        var drawing7 = RequireDrawing7(document);
        var views = (drawing7.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews;
        if (views is null)
        {
            return (null, "views_collection_unavailable_after_rebuild");
        }

        var rows = ReadViewRows(document, views);
        var byRef = rows.FirstOrDefault(r => r.ViewRef == viewRef);
        if (byRef is not null)
        {
            return (byRef, null);
        }

        var byKey = rows.FirstOrDefault(r => r.Number == before.Number
                                             && string.Equals(r.SourcePath, before.SourcePath,
                                                 StringComparison.OrdinalIgnoreCase));
        return byKey is not null
            ? (byKey, "view_ref_изменился_после_перестроения_вид_найден_по_номеру_и_источнику")
            : (null, "вид_не_перечитан_после_перестроения");
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

    /// <summary>Read the dimensions of a view WITHOUT creating any object.</summary>
    /// <remarks>INVARIANT: a pure read, and no RCW is held across a close-reopen cycle — the caller
    /// re-resolves the view on the TARGET document, so persistence is judged on the reopened file's
    /// dimension objects rather than a view-wide counter. DOC: <c>isymbols2dcontainer.html</c> —
    /// <c>IView</c> yields <c>LineDimensions</c>/<c>RadialDimensions</c>/<c>DiametralDimensions</c>;
    /// <c>ilinedimensions.html</c>; <c>idrawingobject_props.html</c>. LIMIT: the linear nominal is
    /// derived from the anchor points; radial and diametral read <c>Radius</c>.
    /// History: docs/decisions/drawings.md#dimensions-read</remarks>
    public ListDimensionsResult ListDimensions(ListDimensionsCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (document.Kind != DocumentKind.Drawing)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» имеет тип {document.Kind}: перечень размеров применим только к чертежу.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = document.Kind.ToString() });
        }

        var view = ResolveView(document, command.ViewRef);
        try
        {
            return ReadDimensions(document, view, command.Limit);
        }
        finally
        {
            ComApartment.Release(view);
        }
    }

    private ListDimensionsResult ReadDimensions(DocumentEntry document, IView view, int limit)
    {
        const string route =
            "IView(QI ISymbols2DContainer).LineDimensions/RadialDimensions/DiametralDimensions "
            + "→ IDrawingObjects.Count/Item → ILineDimension/IRadialDimension/IDiametralDimension";

        var container = view as ISymbols2DContainer;
        if (container is null)
        {
            return new ListDimensionsResult(
                Array.Empty<DimensionReadingDto>(), route,
                new[] { "dimension_container_unavailable — вид не отвечает QI(ISymbols2DContainer)" });
        }

        var rows = new List<DimensionReadingDto>();
        var notes = new List<string>();

        ReadLinearDimensions(container.LineDimensions as ILineDimensions, rows, notes, limit);
        ReadRadialDimensions(container.RadialDimensions as IRadialDimensions, rows, notes, limit);
        ReadDiametralDimensions(container.DiametralDimensions as IDiametralDimensions, rows, notes, limit);

        if (rows.Count == 0)
        {
            notes.Add("dimensions_empty — в виде нет ни одного размера (пустая коллекция)");
        }

        return new ListDimensionsResult(rows, route, notes);
    }

    private void ReadLinearDimensions(
        ILineDimensions? collection, List<DimensionReadingDto> rows, List<string> notes, int limit)
    {
        if (collection is null)
        {
            notes.Add("linear_dimensions_collection_unavailable");
            return;
        }

        var count = SafeInt(() => collection.Count) ?? 0;
        for (var i = 0; i < count && rows.Count < limit; i++)
        {
            ILineDimension? dim = null;
            try
            {
                dim = collection.get_LineDimension((object)i) as ILineDimension;
                if (dim is null)
                {
                    notes.Add($"linear[{i}]: Item вернул null");
                    continue;
                }

                var x1 = SafeDouble(() => dim.X1);
                var y1 = SafeDouble(() => dim.Y1);
                var x2 = SafeDouble(() => dim.X2);
                var y2 = SafeDouble(() => dim.Y2);
                var value = (x1 is { } a && y1 is { } b && x2 is { } c && y2 is { } d)
                    ? (double?)Math.Sqrt(((c - a) * (c - a)) + ((d - b) * (d - b)))
                    : null;
                rows.Add(new DimensionReadingDto
                {
                    Kind = "linear",
                    DrawingObjectType = SafeInt(() => (int)dim.DrawingObjectType),
                    Valid = SafeBool(() => dim.Valid),
                    ValueMm = value,
                    Point1 = (x1 is { } p && y1 is { } q) ? new[] { p, q } : null,
                    Point2 = (x2 is { } p2 && y2 is { } q2) ? new[] { p2, q2 } : null,
                });
            }
            catch (COMException ex)
            {
                notes.Add($"linear[{i}]: {ex.GetType().Name}");
            }
            finally
            {
                ComApartment.Release(dim);
            }
        }
    }

    private void ReadRadialDimensions(
        IRadialDimensions? collection, List<DimensionReadingDto> rows, List<string> notes, int limit)
    {
        if (collection is null)
        {
            notes.Add("radial_dimensions_collection_unavailable");
            return;
        }

        var count = SafeInt(() => collection.Count) ?? 0;
        for (var i = 0; i < count && rows.Count < limit; i++)
        {
            IRadialDimension? dim = null;
            try
            {
                dim = collection.get_RadialDimension((object)i) as IRadialDimension;
                if (dim is null)
                {
                    notes.Add($"radial[{i}]: Item вернул null");
                    continue;
                }

                var xc = SafeDouble(() => dim.Xc);
                var yc = SafeDouble(() => dim.Yc);
                rows.Add(new DimensionReadingDto
                {
                    Kind = "radial",
                    DrawingObjectType = SafeInt(() => (int)dim.DrawingObjectType),
                    Valid = SafeBool(() => dim.Valid),
                    ValueMm = SafeDouble(() => dim.Radius),
                    Point1 = (xc is { } p && yc is { } q) ? new[] { p, q } : null,
                    Point2 = null,
                });
            }
            catch (COMException ex)
            {
                notes.Add($"radial[{i}]: {ex.GetType().Name}");
            }
            finally
            {
                ComApartment.Release(dim);
            }
        }
    }

    private void ReadDiametralDimensions(
        IDiametralDimensions? collection, List<DimensionReadingDto> rows, List<string> notes, int limit)
    {
        if (collection is null)
        {
            notes.Add("diametral_dimensions_collection_unavailable");
            return;
        }

        var count = SafeInt(() => collection.Count) ?? 0;
        for (var i = 0; i < count && rows.Count < limit; i++)
        {
            IDiametralDimension? dim = null;
            try
            {
                dim = collection.get_DiametralDimension((object)i) as IDiametralDimension;
                if (dim is null)
                {
                    notes.Add($"diametral[{i}]: Item вернул null");
                    continue;
                }

                var xc = SafeDouble(() => dim.Xc);
                var yc = SafeDouble(() => dim.Yc);
                rows.Add(new DimensionReadingDto
                {
                    Kind = "diametral",
                    DrawingObjectType = SafeInt(() => (int)dim.DrawingObjectType),
                    Valid = SafeBool(() => dim.Valid),
                    ValueMm = SafeDouble(() => dim.Radius),
                    Point1 = (xc is { } p && yc is { } q) ? new[] { p, q } : null,
                    Point2 = null,
                });
            }
            catch (COMException ex)
            {
                notes.Add($"diametral[{i}]: {ex.GetType().Name}");
            }
            finally
            {
                ComApartment.Release(dim);
            }
        }
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
    /// (<c>Str</c> is read/write), <c>istamp_update.html</c> (<c>Update()</c> applies the parameters).
    /// LIMIT: the SDK reference does NOT document the numeric cell identifiers — the caller supplies ids
    /// from the drawing's own stamp layout; the tool re-reads each cell and reports a mismatch rather
    /// than pretending the write landed. History: docs/decisions/drawings.md#stamp-cells</remarks>
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
            // MEASURED: with cell, text, write order and re-read expression held identical, the write
            // round-trips on a drawing created through the API7 route (IDocuments.Add) and does NOT on one
            // created through API5 ksCreateDocument — there the SAME property reads back the cell's
            // default. So a mismatch is a property of the CREATION ROUTE; the internal cause is not
            // established. CreateDrawingDocument now prefers the API7 route precisely so this write can
            // round-trip; a mismatch on a drawing that still lacks that route stays a mismatch and is named,
            // never reported as delivered.
            // History: docs/decisions/drawings.md#stamp-cells
            unverified.Add(
                "stamp_write_not_confirmed — перечитанное значение не совпало с записанным: на чертеже, "
                + "созданном маршрутом API5 ksCreateDocument, присвоение IText.Str ячейке ОСНОВНОЙ НАДПИСИ "
                + "не меняет то, что возвращает ТО ЖЕ свойство (ячейка читается своим значением по "
                + "умолчанию). Измерено: на чертеже, созданном маршрутом API7 Documents.Add, та же запись "
                + "перечитывается. Внутренняя причина различия не установлена — маршрут записи штампа "
                + "перечитыванием на этом документе НЕ подтверждается и за доставленную не выдаётся");
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

    /// <summary>Read title-block cells without writing them.</summary>
    /// <remarks>INVARIANT: no write to <c>IText.Str</c> and no <c>Update()</c> — reading only. A setter
    /// cannot witness persistence: it carries the expected value, so a lost value could be re-written
    /// before the reading is taken. MEASURED: re-reading after a write does not even reflect the write
    /// (the cell returns its own default), so the setter path proves nothing about persistence either way;
    /// only a route that carries no expected value can witness it. DOC: <c>istamp_text.html</c> —
    /// <c>IStamp.Text(Id)</c> is read-only and returns the cell's <c>IText</c>; <c>itext_str.html</c> —
    /// reading <c>Str</c> changes nothing. History: docs/decisions/drawings.md#stamp-read</remarks>
    public GetTitleBlockResult GetTitleBlock(GetTitleBlockCommand command)
    {
        var document = RequireDrawingKind(command.DocumentId);
        var stamp = RequireStamp(document);
        var readings = new List<TitleBlockCellReadingDto>();

        foreach (var cellIdText in command.CellIds)
        {
            if (!int.TryParse(cellIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellId))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Идентификатор ячейки '{cellIdText}' не число: IStamp.Text(Id) принимает Int32.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["cell_id"] = cellIdText });
            }

            IText? cell = null;
            string? observed = null;
            try
            {
                cell = stamp.get_Text(cellId) as IText;
                observed = cell?.Str;
            }
            catch (COMException)
            {
                // A COM refusal on read is reported as "not read", never smoothed into an empty string:
                // the absence of a value and the absence of a readable cell are different states.
                observed = null;
            }
            finally
            {
                ComApartment.Release(cell);
            }

            // Exists is true only when the cell yielded an IText; an unknown id yields null and reads empty.
            readings.Add(new TitleBlockCellReadingDto(cellIdText, observed, cell is not null));
        }

        var checks = new List<NamedCheck>
        {
            new("cells_read", readings.Count > 0, readings.Count.ToString(CultureInfo.InvariantCulture), ">0"),
        };
        var verification = new VerificationDto(
            readings.Count > 0 ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
            checks,
            new List<string>
            {
                "stamp_cell_ids_not_documented — соответствие «наименование/обозначение/материал» номерам "
                + "ячеек справка не документирует; сервер только читает переданные номера",
            });
        return new GetTitleBlockResult(readings, verification);
    }

    /// <summary>Read the drawing's technical requirements without writing them.</summary>
    /// <remarks>INVARIANT: no assignment to <c>IText.Str</c> and no <c>Update()</c>. DOC:
    /// <c>itechnicaldemand_text.html</c> — <c>Text</c> is read-only and returns the block's <c>IText</c>;
    /// <c>itechnicaldemand_iscreated.html</c> — <c>IsCreated</c> reports the block's display state.
    /// History: docs/decisions/drawings.md#technical-demand-read</remarks>
    public GetTechnicalDemandResult GetTechnicalDemand(GetTechnicalDemandCommand command)
    {
        var document = RequireDrawingKind(command.DocumentId);
        var demand = RequireTechnicalDemand(document);

        string? text = null;
        IText? blockText = null;
        try
        {
            blockText = demand.Text as IText;
            text = blockText?.Str;
        }
        catch (COMException)
        {
            text = null;
        }
        finally
        {
            ComApartment.Release(blockText);
        }

        var isCreated = SafeBool(() => demand.IsCreated);
        var checks = new List<NamedCheck>
        {
            new("is_created", isCreated == true, isCreated?.ToString() ?? "null", "true"),
        };
        var verification = new VerificationDto(
            isCreated == true ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
            checks,
            new List<string>
            {
                "demand_placement_not_checked — сервер не проверяет размещение блока требований на листе",
            });
        return new GetTechnicalDemandResult(text, isCreated, verification);
    }

    /// <summary>Export a drawing through the documented converter route.</summary>
    /// <remarks>DOC: <c>iapplication_converter.html</c> — <c>IApplication.Converter</c> takes the library
    /// as its argument and the help says it is a FULL PATH; <c>iconverter_getfilter.html</c> —
    /// <c>GetFilter(docType, saveAs, out command)</c> returns the library's own command id for the document
    /// type produced at export; <c>iconverter_convert.html</c> — <c>Convert(InputFile, Outfile, Command,
    /// ShowParam)</c> returns 1 on success. PDF is NOT a documented programmatic route and is refused by
    /// name (<c>FORMAT_UNAVAILABLE</c>), not attempted.
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

        // INVARIANT: the converter is chosen BY FULL PATH, and the format is chosen by that library's OWN
        // command id — never by a literal that another call can overwrite.
        // DOC: iapplication_converter.html — «Library - полный путь к библиотеке» (VARIANT). Passing
        // Type.Missing asks for the BUILT-IN converter, which has no DXF/DWG commands at all.
        // MEASURED: with Type.Missing, GetFilter(docType=1..5) returns command=0 and an empty filter for
        // every document type; with the dwgdxfExp.rtw path, GetFilter(FORMAT_DXF=1) returns command=1 and
        // « AutoCAD DXF (*.dxf)», GetFilter(FORMAT_DWG=2) returns command=2 and «AutoCAD DWG (*.dwg)».
        // The library is resolved from the located installation, so a missing library is a named refusal.
        var libraryPath = KompasInteropResolver.ExportLibraryPath(format);
        if (libraryPath is null)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Библиотека выгрузки для формата '{format}' не найдена в установке КОМПАС "
                + "(ожидается Libs\\ImpExp\\dwgdxfExp.rtw). Формат не выгружается угаданной библиотекой: "
                + "выбор библиотеки — полный путь, и её отсутствие названо, а не обойдено.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["format"] = format,
                    ["expected_library"] = "Libs\\ImpExp\\dwgdxfExp.rtw",
                    ["install_root"] = KompasInteropResolver.InstallRoot,
                });
        }

        IConverter converter;
        try
        {
            converter = application7.get_Converter(libraryPath) as IConverter
                ?? throw new KompasContractException(
                    ErrorCodes.FormatUnavailable,
                    $"Конвертер по пути '{libraryPath}' не получен (Converter = null): выгрузка невозможна.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["library"] = libraryPath });
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Получение конвертера '{libraryPath}' бросило исключение: {ex.Message}",
                RetryPolicy.Never,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["library"] = libraryPath });
        }

        int commandId;
        string filter;
        try
        {
            // GetFilter is asked for THIS library's command id for the document type the export produces
            // (FORMAT_DXF=1 / FORMAT_DWG=2 per iconverter_getfilter.html). The returned id is the library's
            // own; the filter string is the library's declared filter, reported as evidence.
            filter = converter.GetFilter(formatCode, true, out commandId) ?? string.Empty;
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"GetFilter(docType={formatCode}) бросил исключение: {ex.Message}",
                RetryPolicy.Never,
                hresult: ex.HResult,
                details: new Dictionary<string, object?>
                {
                    ["format"] = format,
                    ["library"] = libraryPath,
                    ["doc_type"] = formatCode,
                });
        }

        if (commandId <= 0)
        {
            // A library that publishes no command for this document type cannot produce this format. That
            // is a property of the installation, not a transient failure: refused by name with both the
            // library and the requested type shown.
            throw new KompasContractException(
                ErrorCodes.FormatUnavailable,
                $"Библиотека '{Path.GetFileName(libraryPath)}' не публикует команду для типа документа "
                + $"{formatCode} (формат '{format}'): GetFilter вернул command={commandId}. Выгрузка не начата.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["format"] = format,
                    ["library"] = libraryPath,
                    ["doc_type"] = formatCode,
                    ["filter"] = filter,
                });
        }

        int converted;
        try
        {
            converted = converter.Convert(document.Path, command.OutputPath, commandId, false);
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
        var shapeOk = IsExpectedShape(signature, format);

        return new ExportDrawingResult(
            command.OutputPath,
            format,
            file.ByteLength,
            signature,
            converted,
            new VerificationDto(
                shapeOk && file.ByteLength > 0
                    ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                new List<NamedCheck>
                {
                    new("file_exists", File.Exists(command.OutputPath), "true", "true"),
                    new("file_non_empty", file.ByteLength > 0,
                        file.ByteLength.ToString(CultureInfo.InvariantCulture), "> 0"),
                    new("format_signature", shapeOk, signature,
                        format == "dxf" ? "dxf_section_header" : "dwg_ac10xx"),
                    new("library_command", commandId > 0, commandId.ToString(CultureInfo.InvariantCulture), "> 0"),
                    new("convert_returned", converted == 1, converted.ToString(CultureInfo.InvariantCulture), "1"),
                },
                BuildExportUnverified(shapeOk, signature, format)));
    }

    /// <summary>Whether a detected signature is the expected shape for the requested format.</summary>
    /// <remarks>INVARIANT: the extension is never the evidence; the rule itself lives in the PURE
    /// <see cref="KompasMcp.Domain.Files.ExportFormatShape"/> so it is testable without KOMPAS. MEASURED:
    /// a native KOMPAS drawing is a ZIP container, which is expected shape for NO published format.</remarks>
    private static bool IsExpectedShape(string signature, string format) =>
        ExportFormatShape.Matches(signature, format);

    /// <summary>Unverified aspects for an export — the named limits of what the file check proves.</summary>
    private static List<string> BuildExportUnverified(bool shapeOk, string signature, string format)
    {
        var unverified = new List<string>
        {
            "drawing_content_not_checked — сервер не разбирает содержимое выгрузки построчно",
        };
        if (!shapeOk)
        {
            unverified.Add(
                $"export_shape_unexpected — файл не имеет ожидаемой формы '{format}' "
                + $"(найдено: {signature}); контейнер нативной модели под чужим расширением за успех "
                + "не принимается");
        }

        return unverified;
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
        var document = RequireDrawingKind(documentId);
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

    /// <summary>Resolve a drawing WITHOUT a revision check, for READ-ONLY routes.</summary>
    /// <remarks>INVARIANT: a non-mutating read carries no expected revision, so a stale revision must not
    /// refuse it — the caller is not about to write. The kind check stays: a read of a drawing's stamp
    /// against a 3D part is still a named refusal. History: docs/decisions/drawings.md#stamp-read</remarks>
    private DocumentEntry RequireDrawingKind(string documentId)
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

        return document;
    }

    private IDrawingDocument RequireDrawing7(DocumentEntry document)
    {
        // STAGE 0 — an API7 handle already held for this document: the NATIVE one captured at creation
        // through the documented API7 IDocuments.Add route, or one stored here by the FIRST successful
        // resolution (see below). This stage is tried first because it is the strongest available and it
        // never depends on a fresh bridge transfer.
        if (document.Drawing7 is { } native7)
        {
            return native7;
        }

        var bridge = BridgeFor(document);

        // The API5 2D handle transfers to the API7 document directly. If the transfer does not yield an
        // IDrawingDocument the kind signal is confirmed ABSENT — a named refusal, not a null deref.
        if (document.Drawing is not { } drawing5)
        {
            // No native API7 handle AND no API5 2D handle: neither route can name this as a drawing.
            // The earlier failure said "нет 2D-дескриптора" while an API7-created drawing legitimately
            // has none — the missing piece was the native handle, now checked at stage 0.
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» не имеет ни 2D-дескриптора (API5), ни собственного "
                + "IDrawingDocument (API7): маршрут чертежа недоступен.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["stage"] = "no_drawing_handle" });
        }

        // STAGE 1 — direct transfer of the live API5 2D handle through the CACHED bridge. This path is a
        // FALLBACK for a document that has no native API7 handle (an API5-created drawing, or one whose
        // native handle was not captured). MEASURED: for a drawing REOPENED through the documented
        // `ksOpenDocument` route the native handle from <see cref="OpenDrawingDocument"/> is present, so
        // resolution normally stops at STAGE 0 and this transfer is not reached.
        var transferred1 = bridge.TransferTo7(drawing5);
        if (transferred1 is IDrawingDocument transferred)
        {
            // CACHE A SUCCESSFUL RESOLUTION so later calls do not depend on a repeat transfer. Only a
            // SUCCESS is cached: a refusal is never stored, so a genuine absence stays a refusal.
            document.Drawing7 = transferred;
            document.Drawing7Resolution = "stage1_cached_bridge_transfer";
            return transferred;
        }

        var stage1Outcome = transferred1 is null
            ? "transfer_returned_null"
            : "transfer_returned_" + transferred1.GetType().FullName;
        var application5 = RequireApplication(document.ApplicationId);

        // STAGE 1b — the CACHED bridge returned nothing. The resolution is retried on a FRESHLY obtained
        // application before refusing, so a stale cached IApplication does not turn a resolvable drawing
        // into a refusal. LIMIT: whether the native handle can go stale after further mutation is not
        // established; the stages below exist to NAME the real cause when none of them resolves, not to
        // promise that the cached one always does.
        var freshBridge = new Api7Bridge(application5.Application);
        var freshApplication = freshBridge.Application();
        string? freshStage = null;
        if (freshApplication is not null)
        {
            if (freshBridge.TransferTo7(drawing5) is IDrawingDocument freshTransferred)
            {
                document.Drawing7 = freshTransferred;
                document.Drawing7Resolution = "stage1b_fresh_bridge_transfer";
                return freshTransferred;
            }

            if (ResolveInCollection(document, freshApplication, out var freshResolved, out freshStage)
                && freshResolved is not null)
            {
                document.Drawing7 = freshResolved;
                document.Drawing7Resolution = "stage1b_fresh_collection_scan";
                return freshResolved;
            }
            else if (freshStage is null)
            {
                freshStage = "resolved_non_drawing";
            }
        }
        else
        {
            freshStage = "fresh_application_null";
        }

        // STAGE 2 — the same scan on the CACHED bridge, so the refusal can report BOTH views (cached and
        // fresh) rather than one number that hides which application answered.
        if (bridge.Application() is { } application)
        {
            if (ResolveInCollection(document, application, out var cachedResolved, out var cachedStage)
                && cachedResolved is not null)
            {
                document.Drawing7 = cachedResolved;
                document.Drawing7Resolution = "stage2_cached_collection_scan";
                return cachedResolved;
            }

            var (count, candidates) = SnapshotCollection(application);
            throw RefuseDrawing7(document, "document_not_found_in_api7_collection", bridge.BridgeFailure,
                                 document.Path, count, candidates,
                                 (cachedStage ?? "no_match") + "; fresh=" + (freshStage ?? "resolved"),
                                 stage1Outcome, ProbeApi5Side(document, application5));
        }

        throw RefuseDrawing7(document, "application7_unavailable", bridge.BridgeFailure, null, 0, null,
                             freshStage, stage1Outcome, ProbeApi5Side(document, application5));
    }

    /// <summary>What the API5 side still knows about a document whose API7 resolution failed: whether its
    /// 2D handle is still alive, which document KOMPAS reports as ACTIVE, and how many 3D documents the
    /// API5 enumeration sees. MEASURED: this is what tells apart "the document is gone" from "the API7
    /// collection dropped it while API5 still holds it" — the two need different fixes and a bare
    /// <c>Count = 0</c> cannot distinguish them. Each read is guarded: a dead handle answers with the
    /// exception type, not with a fabricated value.
    /// History: docs/decisions/drawings.md#reopen-resolution</summary>
    private static string ProbeApi5Side(DocumentEntry document, ApplicationEntry? application)
    {
        var parts = new List<string>();

        if (document.Drawing is { } drawing5)
        {
            // Liveness by RE-TRANSFER on the raw API5 object: if the RCW's document behind it is gone,
            // TransferInterface answers null (or throws) — so this single read both proves the handle is
            // still backed by a live document AND says whether the API5→API7 transfer still yields a
            // drawing. That is exactly the fact the resolution depends on, so no separate probe is run.
            parts.Add("api5_retransfer=" + ProbeAlive(() =>
                application is null ? "no_application"
                : application.Application.TransferInterface(
                        drawing5, (int)ksAPITypeEnum.ksAPI7Dual, 0) is IDrawingDocument
                    ? "drawing"
                    : "not_drawing"));
        }
        else
        {
            parts.Add("api5_2d_handle=absent");
        }

        if (application is not null)
        {
            parts.Add("api5_active_3d=" + ProbeAlive(() =>
                application.Application.ActiveDocument3D() is ksDocument3D ? "present" : "none"));
            parts.Add("api5_visible=" + (application.DocumentsVisible ? "true" : "false"));
            parts.Add("api5_window_titles=" + ProbeAlive(() =>
            {
                var observed = ObserveApplicationWindow(application.Application);
                return observed is null ? "none" : observed.VisibleChildTitles.Count.ToString();
            }));
            // Does a SECOND, freshly obtained 2D interface transfer? MEASURED separately from the cached
            // handle, because the two can disagree: the cached one can go stale while Document2D() hands
            // back a different object — or the reverse. Recording both is what names which handle is dead.
            parts.Add("api5_fresh_document2d=" + ProbeAlive(() =>
            {
                if (application.Application.Document2D() is not ksDocument2D fresh)
                {
                    return "no_document2d";
                }

                return application.Application.TransferInterface(
                    fresh, (int)ksAPITypeEnum.ksAPI7Dual, 0) is IDrawingDocument ? "drawing" : "not_drawing";
            }));
        }
        else
        {
            parts.Add("api5_application=absent");
        }

        return string.Join(" ", parts);
    }

    /// <summary>Run a liveness probe for the resolution diagnostic; on failure return the exception type
    /// instead of a value, so the refusal stays a diagnosis rather than becoming a crash of the diagnostic
    /// itself.</summary>
    private static string ProbeAlive(Func<string> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return "dead:" + ex.GetType().Name;
        }
    }

    /// <summary>Resolve the reopened drawing among an API7 application's documents. Identification does
    /// NOT go through a blind cast: each candidate is classified by <c>IKompasDocument.DocumentType</c>
    /// (<c>documenttypeenum.html</c>: <c>ksDocumentDrawing = 1</c>) and by <c>PathName</c>; the file NAME
    /// is a tie-breaker NOTE among typed drawings, and the winner is still re-checked by cast.</summary>
    private static bool ResolveInCollection(
        DocumentEntry document, IApplication application, out IDrawingDocument? resolved, out string? note)
    {
        resolved = null;
        note = null;
        if (application.Documents is not { } documents)
        {
            note = "documents_collection_null";
            return false;
        }

        var count = documents.Count;
        IKompasDocument? byPath = null;
        IKompasDocument? byTypeAndName = null;
        for (var i = 0; i < count; i++)
        {
            if (documents[(object)i] is not { } candidate)
            {
                continue;
            }

            var pathName = SafeString(() => candidate.PathName);
            if (PathsMatch(pathName, document.Path))
            {
                byPath = candidate;
                break;
            }

            var kind = SafeDocumentType(candidate);
            if (byTypeAndName is null && kind == DrawingDocumentType
                && FileNamesMatch(SafeString(() => candidate.Name), document.Path))
            {
                byTypeAndName = candidate;
            }
        }

        resolved = (byPath ?? byTypeAndName) as IDrawingDocument;
        note = resolved is not null ? null
            : byTypeAndName is null ? "no_type_and_name_match" : "matched_non_drawing";
        return resolved is not null;
    }

    /// <summary>The collection size and a per-candidate line, for the refusal details.</summary>
    private static (int Count, List<string> Candidates) SnapshotCollection(IApplication application)
    {
        if (application.Documents is not { } documents)
        {
            return (0, new List<string>());
        }

        var count = documents.Count;
        var candidates = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (documents[(object)i] is not { } candidate)
            {
                continue;
            }

            candidates.Add($"[{i}] type={SafeDocumentType(candidate)} "
                + $"path={SafeString(() => candidate.PathName)} name={SafeString(() => candidate.Name)}");
        }

        return (count, candidates);
    }

    /// <summary><c>DocumentTypeEnum.ksDocumentDrawing</c> — the API7 type of a drawing sheet
    /// (<c>documenttypeenum.html</c>). Used to CLASSIFY a candidate, never to mint a drawing from an
    /// untyped object.</summary>
    private const int DrawingDocumentType = 1;

    private static int? SafeDocumentType(IKompasDocument candidate)
    {
        try
        {
            return (int)candidate.DocumentType;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>A named API7-drawing resolution refusal that CARRIES the stage, the collection size and
    /// the candidates seen — the diagnosis the review demanded instead of a bare "причина не названа".</summary>
    private static KompasContractException RefuseDrawing7(
        DocumentEntry document, string stage, string? bridgeFailure, string? wantedPath, int count,
        IReadOnlyList<string>? candidates, string? extraNote = null, string? stage1Outcome = null,
        string? api5Side = null)
    {
        var details = new Dictionary<string, object?>
        {
            ["document_id"] = document.Id,
            ["stage"] = stage,
            ["bridge_failure"] = bridgeFailure ?? "none",
            ["wanted_path"] = wantedPath,
            ["api7_documents_count"] = count,
            ["stage1_transfer"] = stage1Outcome ?? "not_run_no_api5_handle",
            ["api5_side"] = api5Side ?? "not_probed",
            ["drawing7_resolution"] = document.Drawing7Resolution ?? "never_resolved",
        };
        if (candidates is not null)
        {
            details["candidates"] = candidates.ToArray();
        }

        if (extraNote is not null)
        {
            details["note"] = extraNote;
        }

        return new KompasContractException(
            ErrorCodes.WrongDocumentKind,
            $"Чертёж «{document.Id}» не разрешён в API7 как IDrawingDocument на стадии {stage} "
            + $"(документов в API7: {count}, мост: {bridgeFailure ?? "в порядке"}).",
            RetryPolicy.ReacquireContext,
            details: details);
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

    /// <summary>Whether a KOMPAS document name equals the file name of a path. Used only as a tie-break
    /// among documents already classified as drawings by <c>DocumentType</c> — the name is a note, the
    /// type is the decision. History: docs/decisions/drawings.md#reopen-resolution</summary>
    private static bool FileNamesMatch(string? documentName, string? path)
    {
        if (string.IsNullOrWhiteSpace(documentName) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return string.Equals(documentName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Whether a documentation-named export format is published, and the document type the
    /// converter produces for it.</summary>
    /// <remarks>DOC: <c>iconverter_getfilter.html</c> — at export the first argument is the document type
    /// the converter PRODUCES, and the constant block for the DXF/DWG converter publishes
    /// <c>FORMAT_DXF=1</c> and <c>FORMAT_DWG=2</c>. Those numeric values are the converter's own
    /// document-type codes; the command id used at the call site is the library's, taken from
    /// <c>GetFilter</c> and not from here.</remarks>
    private static bool TryResolveExportFormat(string format, out string wire, out int docType)
    {
        switch (format.Trim().ToLowerInvariant())
        {
            case "dxf":
                wire = "dxf";
                docType = ExportDocType.Dxf;
                return true;
            case "dwg":
                wire = "dwg";
                docType = ExportDocType.Dwg;
                return true;
            default:
                wire = format;
                docType = 0;
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
    /// <remarks>INVARIANT: the format is read from the file's own CONTENT, never from its extension. A
    /// file named <c>.dxf</c> that is really something else must be reported as that something else.
    /// MEASURED: a native KOMPAS drawing (<c>.cdw</c>) is a ZIP container («PK\x03\x04») whose
    /// <c>FileInfo</c> member declares <c>FileTypeName=Kompas.cdw</c>; the earlier revision presented such
    /// a container as a successful DXF/DWG export. A container is therefore named as a container and is
    /// NOT accepted as either published format. History: docs/decisions/drawings.md#export-formats</remarks>
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
    /// <remarks>REPORTING ONLY: the container shape is returned so the caller can NAME what was found —
    /// it is never treated as success for any published format. The container's member set is read from
    /// its central directory, not by decompressing the file.</remarks>
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

/// <summary>Document-type codes the DXF/DWG converter produces, for <c>IConverter.GetFilter</c>
/// (<c>iconverter_getfilter.html</c>) — the vendor's own constants, taken from the help rather than from
/// a literal at a call site.</summary>
internal static class ExportDocType
{
    /// <summary>Document type produced for a DXF export (<c>FORMAT_DXF</c>).</summary>
    public const int Dxf = 1;

    /// <summary>Document type produced for a DWG export (<c>FORMAT_DWG</c>).</summary>
    public const int Dwg = 2;
}

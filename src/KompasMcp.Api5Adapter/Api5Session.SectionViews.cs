using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Drawings;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Associative sections of a model on a drawing: a cut line on a base view plus a
/// <c>vt_Section</c> view built from it.</summary>
/// <remarks>MEASURED: <c>Section = false</c> is the CUT and <c>true</c> the plain section, honoured only
/// when the view is first built. INVARIANT: every call here is TYPED — through <c>IDispatch</c> the same
/// members accept the write and do not apply it. <c>IHatchParam</c> is an ADDITIONAL interface of
/// <c>IAssociationView</c>, not a property.
/// History: docs/decisions/drawings.md#section-route</remarks>
public sealed partial class Api5Session
{
    /// <summary>The documented cut: the picture behind the cutting plane, which is what a client asks for.</summary>
    private const bool CutIsSection = false;

    public CreateSectionViewResult CreateSectionView(CreateSectionViewCommand command)
    {
        var document = RequireDrawing(command.DocumentId, command.ExpectedRevision);

        if (command.Letter is not null)
        {
            // icutline_text.html publishes Text as READ-ONLY («только чтение»), so a caller's letter
            // cannot be written through the documented member. Refused by name rather than ignored.
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Задать букву разреза нельзя: справка v24 (icutline_text.html) объявляет ICutLine.Text "
                + "свойством только для чтения. Оставьте letter пустым - КОМПАС присвоит букву сам "
                + "(icutline_autosorted.html).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["letter"] = command.Letter });
        }

        var mode = ParseMode(command.Mode);
        var hasModel = command.TraceModel is { Count: > 0 };
        var hasView = command.TraceView is { Count: > 0 };
        if (hasModel == hasView)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Трасса задаётся РОВНО в одной системе координат: либо trace_model (мм модели), либо "
                + "trace_view (мм вида). Получены обе или ни одной.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["trace_model_points"] = command.TraceModel?.Count,
                    ["trace_view_points"] = command.TraceView?.Count,
                });
        }

        if (command.Look is not null && command.LookVector is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Сторона стрелок задаётся либо словом look (left/right), либо вектором look_vector: "
                + "не оба сразу.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["look"] = command.Look });
        }

        var drawing7 = RequireDrawing7(document);
        var views = Api7Views.Of(drawing7)
            ?? throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{document.Id}» не отдал коллекцию видов (Views = null).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });

        var baseView = ResolveView(document, command.BaseViewRef);
        try
        {
            return CreateSectionViewCore(document, command, mode, views, baseView);
        }
        finally
        {
            // The RCW came from ResolveView's own collection read; the registry holds only its address.
            ComApartment.Release(baseView);
        }
    }

    private CreateSectionViewResult CreateSectionViewCore(
        DocumentEntry document,
        CreateSectionViewCommand command,
        SectionMode mode,
        IViews views,
        IView baseView)
    {
        var baseType = SafeViewType(() => baseView.ViewType);
        if (baseType == "vt_Section")
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Опорным видом назван сам вид-разрез: линия разреза строится на ассоциативном виде "
                + "модели, а не на разрезе.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["base_view_ref"] = command.BaseViewRef });
        }

        if (baseView is not IAssociationView association)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Вид «{command.BaseViewRef}» имеет тип {baseType ?? "не прочитан"} и не является "
                + "ассоциативным: разрезу нужен вид, связанный с моделью-источником.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["base_view_ref"] = command.BaseViewRef,
                    ["view_type"] = baseType,
                });
        }

        if (baseView is not ISymbols2DContainer symbols)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Опорный вид не отдал ISymbols2DContainer: документированного контейнера линий "
                + "разреза у него нет.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["base_view_ref"] = command.BaseViewRef });
        }

        var points = TracePoints(association, command);

        // INVARIANT: a trace that breaks the rule is refused BEFORE the document is touched, so a wrong
        // request cannot leave a half-built view behind.
        var problems = SectionTrace.Problems(mode, points);
        if (problems.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Трасса разреза недопустима: " + string.Join("; ", problems) + ".",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["mode"] = command.Mode,
                    ["trace_points"] = points.Count / 2,
                    ["problems"] = problems,
                    ["partial_effects"] = false,
                });
        }

        var lookLeft = ResolveLook(command, points, out var lookNote);

        ICutLine line;
        try
        {
            line = symbols.CutLines.Add();
            line.Points = points.ToArray();
            line.ArrowPos = SectionTrace.ArrowPos(lookLeft);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Линия разреза не создана: {ex.Message}. В документе ничего не изменено.",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["stage"] = "cut_line" });
        }

        var lineUpdated = ((IDrawingObject)line).Update();
        if (!lineUpdated)
        {
            // The measured failure mode of a trace the kernel will not take: the write is accepted, the
            // read-back is empty and Update answers false. Nothing else has been created yet.
            TryDelete(line);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ключевое перестроение линии разреза (IDrawingObject.Update) вернуло false: линия не "
                + "построена, вид-разрез не создавался. Документ не изменён.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["stage"] = "cut_line_update",
                    ["trace_points"] = points.Count / 2,
                    ["partial_effects"] = false,
                });
        }

        IView view;
        try
        {
            view = views.Add(LtViewType.vt_Section);
            var added = (IAssociationView)view;
            added.BaseObject = line;
            added.Section = CutIsSection;
            if (command.ProjectionLink is bool linked)
            {
                added.ProjectionLink = linked;
            }

            if (command.Scale is double scale)
            {
                view.Scale = scale;
            }

            view.X = command.X;
            view.Y = command.Y;
        }
        catch (COMException ex)
        {
            TryDelete(line);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Вид-разрез не создан: {ex.Message}. Линия разреза удалена, документ не изменён.",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["stage"] = "section_view" });
        }

        var viewUpdated = ((IDrawingObject)view).Update();
        var section = ReadSection(document, view, line, mode, lookLeft);
        if (!SectionTrace.Built(section.ObjectCount))
        {
            // The kernel drew nothing: the trace missed the body. Rolled back, and the client is told
            // exactly what was removed, because a silent empty view looks like a success.
            var rolledBack = TryDelete(view) && TryDelete(line);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Разрез построен пустым (ObjectCount = 0): трасса не пересекает тело либо опорный вид "
                + "не связан с моделью. " + (rolledBack
                    ? "Созданные объекты удалены, документ не изменён."
                    : "Удалить созданные объекты не удалось - проверьте вид и линию в чертеже вручную."),
                RetryPolicy.SameOperationId,
                partialEffects: !rolledBack,
                details: new Dictionary<string, object?>
                {
                    ["stage"] = "section_view_update",
                    ["update_returned"] = viewUpdated,
                    ["object_count"] = section.ObjectCount,
                    ["rolled_back"] = rolledBack,
                    ["view_ref"] = rolledBack ? null : section.ViewRef,
                    ["cut_line_ref"] = rolledBack ? null : section.CutLineRef,
                    ["revision_after"] = document.Revision,
                });
        }

        BumpRevision(document, "drawing.create_section_view");

        var checks = new List<NamedCheck>
        {
            new("cut_line_updated", lineUpdated, lineUpdated.ToString(), "true"),
            new("view_updated", viewUpdated, viewUpdated.ToString(), "true"),
            new("objects_drawn", SectionTrace.Built(section.ObjectCount),
                section.ObjectCount?.ToString(CultureInfo.InvariantCulture), "> 0"),
            new("trace_re_read", section.TraceView is { Count: > 0 },
                (section.TraceView?.Count ?? 0).ToString(CultureInfo.InvariantCulture), "> 0"),
            new("section_value", section.Section == CutIsSection,
                section.Section?.ToString(), CutIsSection.ToString()),
        };

        var unverified = new List<string>
        {
            "section_geometry_not_checked — разрез подтверждён числом объектов, обозначением и "
            + "штриховкой; контур по выгрузке не сверялся",
        };
        if (lookNote is not null)
        {
            unverified.Add(lookNote);
        }

        if (section.HatchStep is null)
        {
            unverified.Add("hatch_not_read — параметры штриховки не прочитаны (IHatchParam)");
        }

        var verification = new VerificationDto(
            VerificationLevel.StructureChecked,
            checks,
            unverified);

        return new CreateSectionViewResult(section, verification);
    }

    private static SectionMode ParseMode(string mode) => mode switch
    {
        "simple" => SectionMode.Simple,
        "stepped" => SectionMode.Stepped,
        _ => throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Режим разреза '{mode}' не поддерживается. Опубликованы: simple, stepped. Ломаный (broken), "
            + "местный (local) и сечение (section) идут другим маршрутом и в этот инструмент не входят.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["supported_modes"] = new[] { "simple", "stepped" },
            }),
    };

    /// <summary>The trace in VIEW mm: either given directly, or projected from the model through the base
    /// view's own <c>GetProjectionPoint</c>.</summary>
    private static List<double> TracePoints(IAssociationView association, CreateSectionViewCommand command)
    {
        if (command.TraceView is { Count: > 0 } view)
        {
            return view.ToList();
        }

        var model = command.TraceModel!;
        if (model.Count % 2 != 0)
        {
            return model.ToList();
        }

        var points = new List<double>(model.Count);
        for (var index = 0; index + 2 < model.Count; index += 2)
        {
            association.GetProjectionPoint(model[index], model[index + 1], 0d, out var xv, out var yv);
            points.Add(xv);
            points.Add(yv);
        }

        return points;
    }

    private static bool ResolveLook(
        CreateSectionViewCommand command,
        IReadOnlyList<double> points,
        out string? note)
    {
        note = null;
        if (command.Look is { Length: > 0 } look)
        {
            return look.ToLowerInvariant() switch
            {
                "left" => true,
                "right" => false,
                _ => throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Сторона стрелок '{look}' не документирована: опубликованы left и right.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["look"] = look }),
            };
        }

        if (command.LookVector is { Count: > 0 } vector)
        {
            if (!SectionTrace.TryLookSide(vector, points, out var left, out var problem))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Вектор взгляда не пригоден для этой трассы: " + problem + ".",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["look_vector"] = vector });
            }

            return left;
        }

        note = "look_not_given — сторона стрелок не задана: принята левая, как у КОМПАС по умолчанию "
            + "(icutline_arrowpos.html)";
        return true;
    }

    /// <summary>The section view re-read from the document, plus the trace and the arrows of its line.</summary>
    private SectionViewDto ReadSection(
        DocumentEntry document,
        IView view,
        ICutLine line,
        SectionMode mode,
        bool lookLeft)
    {
        var association = view as IAssociationView;
        var designation = view as IViewDesignation;
        var hatch = view as IHatchParam;
        var address = SafeInt(() => view.Reference);
        var trace = SafeObject(() => line.Points) is Array array
            ? array.Cast<object?>().Select(value => Convert.ToDouble(value, CultureInfo.InvariantCulture)).ToList()
            : null;

        return new SectionViewDto
        {
            ViewRef = References.Register("view", document.Id, document.Revision, address).Id,
            CutLineRef = References.Register("cutline", document.Id, document.Revision,
                SafeInt(() => line.Reference)).Id,
            ViewType = SafeViewType(() => view.ViewType),
            Mode = mode == SectionMode.Simple ? "simple" : "stepped",
            Section = association is null ? null : SafeBool(() => association.Section),
            Scale = SafeDouble(() => view.Scale),
            X = SafeDouble(() => view.X),
            Y = SafeDouble(() => view.Y),
            TraceView = trace,
            LookLeft = SafeBool(() => line.ArrowPos),
            Letter = ReadLetter(designation),
            Designation = designation is null ? null : SafeString(() => designation.Designation),
            SourcePath = association is null ? null : SafeString(() => association.SourceFileName),
            ProjectionLink = association is null ? null : SafeBool(() => association.ProjectionLink),
            ObjectCount = SafeInt(() => view.ObjectCount),
            HatchStep = hatch is null ? null : SafeDouble(() => hatch.Step),
            HatchAngle = hatch is null ? null : SafeDouble(() => hatch.HatchAngle),
            HatchStyle = hatch is null ? null : SafeInt(() => hatch.Style),
        };
    }

    /// <summary>The letter the kernel assigned, taken from the designation's first group
    /// (<c>Б-Б(1:1)</c> gives <c>Б</c>); null when the designation is not in that form.</summary>
    private static string? ReadLetter(IViewDesignation? designation)
    {
        var text = designation is null ? null : SafeString(() => designation.Designation);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var separator = text!.IndexOf('-', StringComparison.Ordinal);
        return separator > 0 ? text[..separator] : null;
    }

    /// <summary>Deletes a drawing object and reports whether it is gone; a failure is not an exception,
    /// because the caller is already describing a refusal.</summary>
    private static bool TryDelete(object? drawingObject)
    {
        if (drawingObject is not IDrawingObject target)
        {
            return false;
        }

        try
        {
            return target.Delete();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static object? SafeObject(Func<object?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

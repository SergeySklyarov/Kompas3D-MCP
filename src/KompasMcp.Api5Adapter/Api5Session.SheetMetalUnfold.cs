using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.SheetMetal;

namespace KompasMcp.Api5Adapter;

/// <summary>Unfold state of a body (profile <c>sheet-metal-minimal-v1</c>): read it and set it.</summary>
/// <remarks>DOC: <c>isheetmetalbendunfoldparameters.html</c> — the interface comes from
/// <c>ISheetMetalContainer::SheetMetalBendUnfoldParameters</c>; properties <c>Unfold</c>, <c>IsCreated</c>,
/// <c>FixedFaces</c>, <c>UnfoldPlane</c>, <c>ExcludedBendObjects</c>; method <c>UpdateParam()</c>.
/// INVARIANT: the unfold is a STATE of the body, not an operation and not a new body — the operation
/// route answers null on a bent body (MEASURED) and stays out. INVARIANT: success is the READ state plus
/// the extent along the normal, never <c>UpdateParam()</c>: it answers FALSE in every state.
/// History: docs/decisions/sheet-metal.md#unfold-tools</remarks>
public partial class Api5Session
{
    // ─── set ────────────────────────────────────────────────────────────────

    /// <summary>Unfold the body or fold it back, through the parameters of the container.</summary>
    /// <remarks>INVARIANT: every refusal that can be decided from the request happens before the first
    /// write, because the unfold cannot be deleted by this product. <c>LIMIT:</c> the plane is taken from
    /// <c>unfold_plane_ref</c> when given and from the fixed face otherwise; the geometry check decides,
    /// it is not a claim about the kernel accepting a face as a plane.
    /// History: docs/decisions/sheet-metal.md#unfold-tools</remarks>
    public SetSheetMetalUnfoldResult SetSheetMetalUnfold(SetSheetMetalUnfoldCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var body = RequireSheetMetalBody(document, command.BodyRef);
        var bridge = BridgeFor(document);
        var (_, sheet) = RequireSheetMetal(document, "Развёртка не менялась");
        var unfold = RequireUnfoldParameters(sheet, "Развёртка не менялась");

        var before = ReadUnfold(document, unfold);
        var refusal = SheetMetalUnfoldPolicy.CheckSet(command.Unfold, before.IsCreated, command.FixedFaceRef);
        if (refusal != SheetMetalUnfoldPolicy.Refusal.None)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Параметры развёртки этого тела ещё не заданы (IsCreated = false), а развернуть без "
                + "неподвижной грани нельзя: ядро не знает, какую часть тела держать на месте. Передайте "
                + "fixed_face_ref — грань этого тела. Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "fixed_face_ref" });
        }

        IModelObject? fixedFace = null;
        if (command.FixedFaceRef is { } fixedRef)
        {
            RequireKind(document, fixedRef, "face", "fixed_face_ref", "Развёртка не менялась");
            fixedFace = RequireModelObject(document, bridge, fixedRef, "face", "fixed_face_ref");
            if (OwnerIsBody(body, fixedFace) == false)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Неподвижная грань принадлежит другому элементу модели, а не телу '" + command.BodyRef
                    + "': держать на месте часть чужого тела бессмысленно, а снять развёртку этот продукт "
                    + "не может. Обращение к КОМПАС не выполнялось.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["field"] = "fixed_face_ref" });
            }
        }

        IModelObject? plane = null;
        if (command.UnfoldPlaneRef is { } planeRef)
        {
            RequireKind(document, planeRef, "plane", "unfold_plane_ref", "Развёртка не менялась");
            plane = RequireModelObject(document, bridge, planeRef, "plane", "unfold_plane_ref");
        }

        var thickness = SafeDouble(() => body.Thickness);
        var (extentBefore, sourceBefore) = ExtentOfBody(document, body);

        try
        {
            if (fixedFace is not null)
            {
                unfold.FixedFaces = new object[] { fixedFace };
            }

            // INVARIANT: the plane is always written - the page makes it a separate property, and an
            // unfold with the parameters of a previous body is the wrong plane. A face is an IModelObject,
            // so it serves when the caller named no plane (see LIMIT above).
            if (plane is not null || fixedFace is not null)
            {
                unfold.UnfoldPlane = plane ?? fixedFace!;
            }

            unfold.Unfold = command.Unfold;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись параметров развёртки отказала: " + Describe(ex) + ". Состояние тела не подтверждено.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["body_ref"] = command.BodyRef });
        }

        // The documented write route ends here; its answer is recorded, never used as the criterion.
        var updateParam = SafeBool(() => unfold.UpdateParam());

        var after = ReadUnfold(document, unfold);
        var (extentAfter, sourceAfter) = ExtentOfBody(document, body);
        BumpRevision(document, "sheet_metal.set_unfold");

        var flagRead = after.Unfold == command.Unfold;
        var created = after.IsCreated == true;
        var extentRead = extentAfter is not null && thickness is not null;
        var extentConfirms = extentRead
            && SheetMetalUnfoldPolicy.ExtentConfirms(command.Unfold, extentAfter, thickness!.Value);

        var checks = new List<NamedCheck>
        {
            new("unfold_flag_read_back", flagRead,
                Observed: DescribeFlag(after.Unfold), Expected: command.Unfold ? "true" : "false"),
            new("is_created", created, Observed: DescribeFlag(after.IsCreated), Expected: "true"),
            new("fixed_faces_read", after.FixedFaceCount is not null,
                Observed: after.FixedFaceCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано",
                Expected: "прочитанное число граней"),
            // MEASURED: a flat body's extent along the normal IS its thickness; a folded one is larger.
            new("extent_by_normal", extentConfirms,
                Observed: extentAfter?.ToString("0.####", CultureInfo.InvariantCulture) ?? "габарит не прочитан",
                Expected: command.Unfold
                    ? "толщина " + (thickness?.ToString("0.####", CultureInfo.InvariantCulture) ?? "не прочитана")
                    : "больше толщины " + (thickness?.ToString("0.####", CultureInfo.InvariantCulture) ?? "?")),
        };

        if (!flagRead || !created || !extentConfirms)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Параметры развёртки записаны, но чтение назад не подтвердило состояние: "
                + string.Join(", ", checks.Where(check => !check.Passed).Select(check => check.Name))
                + ". Развёртка остаётся в теле — снять её этим продуктом нельзя.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["body_ref"] = command.BodyRef,
                    ["requested"] = command.Unfold ? "unfold" : "fold_back",
                    ["before"] = DescribeUnfold(before),
                    ["after"] = DescribeUnfold(after),
                    ["extent_before_mm"] = extentBefore,
                    ["extent_after_mm"] = extentAfter,
                    ["update_param"] = updateParam,
                });
        }

        var unverified = new List<string>
        {
            // MEASURED: the answer is FALSE in every state, so it is recorded and never compared.
            "update_param_not_a_criterion — UpdateParam() вернул "
            + (updateParam?.ToString() ?? "не прочитано")
            + " и критерием не служит: измерено, что метод отвечает FALSE и при созданной развёртке; "
            + "успех подтверждён чтением IsCreated/Unfold и габаритом по нормали",
        };

        if (command.UnfoldPlaneRef is null)
        {
            unverified.Add("unfold_plane_is_fixed_face — плоскость развёртки не задана ссылкой: в UnfoldPlane "
                + "подана неподвижная грань (IModelObject). Клиенту неоткуда взять ссылку на стандартную "
                + "плоскость, поэтому грань служит плоскостью по умолчанию; пригодность грани как "
                + "плоскости развёртки отдельно не измерялась — её подтверждает только геометрия");
        }

        if (!extentRead)
        {
            unverified.Add("extent_not_read — габарит тела по нормали не прочитан: сравнение с толщиной не "
                + "выполнено, состояние подтверждено только флагами IsCreated/Unfold");
        }

        if (sourceAfter != "body")
        {
            unverified.Add("extent_not_per_body — габарит по нормали прочитан не по телу (" + sourceAfter
                + "): документированный маршрут ksBody.GetGabarit тела не ответил, и число взято из "
                + "документа, где единственное тело — это тело");
        }

        if (after.ExcludedBendCount is null)
        {
            unverified.Add("excluded_bends_not_read — ExcludedBendObjects не прочитан: число исключённых "
                + "сгибов не подтверждено");
        }

        return new SetSheetMetalUnfoldResult(
            ToDto(References.Require(command.BodyRef, document.Id, document.Revision), $"sheet metal body {command.BodyRef}"),
            before,
            after,
            extentBefore,
            extentAfter,
            ReadBody(document, body, ordinal: null),
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    // ─── read ───────────────────────────────────────────────────────────────

    /// <summary>The unfold state of one body as the model holds it. Writes nothing.</summary>
    public GetSheetMetalUnfoldResult GetSheetMetalUnfold(GetSheetMetalUnfoldCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var body = RequireSheetMetalBody(document, command.BodyRef);
        var (_, sheet) = RequireSheetMetal(document, "Чтение невозможно");
        var unfold = RequireUnfoldParameters(sheet, "Чтение невозможно");

        var state = ReadUnfold(document, unfold);
        var checks = new List<NamedCheck>
        {
            new("unfold_flag_read", state.Unfold is not null, Observed: DescribeFlag(state.Unfold)),
            new("is_created_read", state.IsCreated is not null, Observed: DescribeFlag(state.IsCreated)),
        };

        var unverified = new List<string>();
        if (state.FixedFaceCount is null)
        {
            unverified.Add("fixed_faces_not_read — FixedFaces не прочитан: неподвижные грани не подтверждены");
        }

        if (state.ExcludedBendCount is null)
        {
            unverified.Add("excluded_bends_not_read — ExcludedBendObjects не прочитан");
        }

        return new GetSheetMetalUnfoldResult(
            state,
            ReadBody(document, body, ordinal: null),
            new VerificationDto(VerificationLevel.CallReturned, checks, unverified));
    }

    // ─── shared ─────────────────────────────────────────────────────────────

    /// <summary>The body a <c>body_ref</c> names, or a refusal naming the kind it carries instead.</summary>
    private ISheetMetalBody RequireSheetMetalBody(DocumentEntry document, string bodyRef)
    {
        var stored = References.Require(bodyRef, document.Id, document.Revision);
        if (stored.Payload is ISheetMetalBody body)
        {
            return body;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Ссылка '{bodyRef}' указывает не на листовое тело (kind={stored.Kind}).",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
    }

    /// <summary>The unfold parameters of the container, or a named refusal — a container that answers null
    /// here is not "unfolded".</summary>
    private static ISheetMetalBendUnfoldParameters RequireUnfoldParameters(
        ISheetMetalContainer sheet, string consequence)
    {
        if (SafeObject(() => sheet.SheetMetalBendUnfoldParameters) is ISheetMetalBendUnfoldParameters unfold)
        {
            return unfold;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            "ISheetMetalContainer.SheetMetalBendUnfoldParameters не вернул интерфейс параметров развёртки. "
            + consequence + ".",
            RetryPolicy.ReacquireContext);
    }

    /// <summary>The parameters as the model holds them. Every field is a read value; <c>null</c> — not read.
    /// </summary>
    /// <remarks>MEASURED: <c>FixedFaces</c> answers ONE object for a one-face request, not an array, so both
    /// shapes are counted. <c>ExcludedBendObjects</c> is counted only: the page names no way to put a bend
    /// back into it.</remarks>
    private SheetMetalUnfoldDto ReadUnfold(DocumentEntry document, ISheetMetalBendUnfoldParameters unfold)
    {
        var faces = ReadItems(() => unfold.FixedFaces);
        var excluded = ReadItems(() => unfold.ExcludedBendObjects);

        return new SheetMetalUnfoldDto
        {
            Unfold = SafeBool(() => unfold.Unfold),
            IsCreated = SafeBool(() => unfold.IsCreated),
            FixedFaceCount = faces?.Count,
            FixedFaces = faces is null
                ? null
                : faces.Select(face => References.Register("face", document.Id, document.Revision, face).Id)
                    .ToList(),
            ExcludedBendCount = excluded?.Count,
        };
    }

    /// <summary>The VARIANT the page types as <c>SAFEARRAY DISPATCH</c> as a list: the kernel hands one
    /// object back when one was written, so a bare object counts as a one-element list.</summary>
    private static List<object>? ReadItems(Func<object?> read)
    {
        object? value;
        try
        {
            value = read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        if (value is null)
        {
            return new List<object>();
        }

        return value is Array array ? array.Cast<object>().ToList() : new List<object> { value };
    }

    /// <summary>Extent along Z of THIS body, and where the number came from.
    /// <c>ksBody.GetGabarit</c> is the documented per-body route; a part with exactly one solid body falls
    /// back to the documented "только тела" mode of <c>ksPart.GetGabarit</c>, which is then that body's box.
    /// </summary>
    /// <remarks>INVARIANT: the extent of the DOCUMENT is never published as the extent of a body when the
    /// part holds several bodies — the answer is "not read", and the caller names the gap. MEASURED: the
    /// whole-document box answers a sentinel around 1e300 until the model has been rebuilt once more.
    /// History: docs/decisions/sheet-metal.md#body-extent</remarks>
    private (double? Extent, string Source) ExtentOfBody(DocumentEntry document, ISheetMetalBody body)
    {
        try
        {
            if (FindSolidBody(document, body) is { } solid)
            {
                var box = ReadBodyBox(solid);
                if (ExtentOfCorners(box.MinMm, box.MaxMm) is { } own)
                {
                    return (own, "body");
                }
            }

            if (CountBodies(document) == 1
                && TryReadGabaritCorners(document, full: false, customizable: false, out var min, out var max)
                && ExtentOfCorners(min, max) is { } single)
            {
                return (single, "document_single_body");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "unread");
        }

        return (null, "unread");
    }

    /// <summary>The solid body of <paramref name="body"/> in <c>ksPart.BodyCollection()</c>: by the
    /// object's own pointer, then by the tree object its faces name.</summary>
    /// <remarks>MEASURED: a sheet-metal body is a different COM object from the solid body the part holds,
    /// so the pointer finds nothing once a second body exists; a face answers <c>Owner</c> with the same
    /// object the sheet-metal body does. DOC: <c>ksbody_facecollection.html</c>,
    /// <c>imodelobject_props.html</c>. History: docs/decisions/sheet-metal.md#body-extent</remarks>
    private ksBody? FindSolidBody(DocumentEntry document, ISheetMetalBody body)
    {
        var part = document.PartNow();
        var collection = (ksBodyCollection)part.BodyCollection();
        collection.refresh();
        var ownPointer = ComIdentity.Of(body);
        var ownOwner = ComIdentity.Of(SafeObject(() => body.Owner));
        var bridge = ownOwner is null ? null : BridgeFor(document);
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            if (AsInterface<ksBody>(collection.GetByIndex(i)) is not { } solid)
            {
                continue;
            }

            if (ownPointer is not null && string.Equals(ownPointer, ComIdentity.Of(solid), StringComparison.Ordinal))
            {
                return solid;
            }

            if (bridge is not null && ownOwner is not null
                && string.Equals(ownOwner, OwnerOfFirstFace(bridge, solid), StringComparison.Ordinal))
            {
                return solid;
            }
        }

        return null;
    }

    /// <summary>Identity of the tree object that owns the first face of <paramref name="solid"/>, or
    /// <c>null</c> when the body states none.</summary>
    private static string? OwnerOfFirstFace(Api7Bridge bridge, ksBody solid)
    {
        try
        {
            if (solid.FaceCollection() is not ksFaceCollection faces || faces.GetCount() == 0
                || AsInterface<ksFaceDefinition>(faces.GetByIndex(0)) is not { } face)
            {
                return null;
            }

            return bridge.TransferTo7(face) is IModelObject model
                ? ComIdentity.Of(SafeObject(() => model.Owner))
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Z extent of a box, or <c>null</c> for a sentinel or a box that is not three numbers.</summary>
    private static double? ExtentOfCorners(IReadOnlyList<double> min, IReadOnlyList<double> max)
    {
        if (min.Count < 3 || max.Count < 3)
        {
            return null;
        }

        var extent = max[2] - min[2];
        return double.IsFinite(extent) && Math.Abs(extent) < 1e6 ? extent : null;
    }

    private static string DescribeFlag(bool? value) => value?.ToString() ?? "не прочитано";

    private static string DescribeUnfold(SheetMetalUnfoldDto state) =>
        $"unfold={DescribeFlag(state.Unfold)}, is_created={DescribeFlag(state.IsCreated)}, "
        + $"неподвижных граней={state.FixedFaceCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}";
}

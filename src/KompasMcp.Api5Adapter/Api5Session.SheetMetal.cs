using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;
using KompasMcp.Domain.SheetMetal;

namespace KompasMcp.Api5Adapter;

/// <summary>Sheet-metal body (profile <c>sheet-metal-minimal-v1</c>): create, read, edit.</summary>
/// <remarks>DOC: <c>isheetmetalcontainer.html</c> — «ISheetMetalContainer является дополнительным к
/// интерфейсу IPart7 … можно получить от IPart7 посредством вызова метода IUnknown::QueryInterface»;
/// <c>isheetmetalbodies_add.html</c> — «После получения нового интерфейса нужно задать параметры
/// листового тела и вызвать метод IModelObject::Update»; <c>isheetmetalbody_thickness.html</c>,
/// <c>..._radius.html</c>, <c>..._direction.html</c>, <c>..._unfoldtype.html</c>, <c>..._straighten.html</c>,
/// <c>..._bendcoefficient.html</c>, <c>..._bendvalue.html</c>, <c>..._bendreduction.html</c>,
/// <c>ksunfoldtypeenum.html</c>, <c>ksdirectiontypeenum.html</c>.
/// INVARIANT: the counter and the properties are read back AFTER <c>Update()</c>; a mismatch is
/// <c>VERIFICATION_FAILED</c> carrying requested and read values, never a success with a warning.
/// MEASURED: the kernel accepts thickness 0 and -1.5 and radius 0, answers <c>Update() = TRUE</c> and keeps
/// the body — so the product refuses these values BEFORE <c>Add()</c>.
/// LIMIT: a created body cannot be deleted by this product (the documented <c>DeleteObject</c> route answers
/// FALSE for sheet-metal objects), so a refusal after <c>Add()</c> says <c>partialEffects: true</c> and
/// names the body it left behind; it never claims a rollback.
/// History: docs/decisions/sheet-metal.md#body-tools</remarks>
public partial class Api5Session
{
    /// <summary>Sheet-metal body family name in server responses.</summary>
    private const string SheetMetalFamily = "sheet_metal_body";

    // ─── create ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Create a flat sheet-metal body from an existing sketch.</summary>
    /// <remarks>INVARIANT: every refusal that can be decided from the request happens before the
    /// container is touched, because a body created and then abandoned stays in the model.
    /// History: docs/decisions/sheet-metal.md#body-tools</remarks>
    public CreateSheetMetalBodyResult CreateSheetMetalBody(CreateSheetMetalBodyCommand command)
    {
        var refusal = SheetMetalBodyPolicy.CheckCreate(
            command.ThicknessMm, command.BendRadiusMm, command.Direction, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);
        if (refusal != SheetMetalBodyPolicy.Refusal.None)
        {
            throw SheetMetalRefusal(refusal, command.UnfoldType);
        }

        // MEASURED: a body from an EMPTY sketch answers with the counter +1 and readable properties but
        // never reaches the saved file, and nothing is left to clean up — so an empty profile is refused
        // here, by the same reading of the drawn profile the extrusion input check uses.
        var recordedPrimitives = RecordedPrimitives(command.SketchRef);
        if (SheetMetalBodyPolicy.SketchIsEmpty(recordedPrimitives))
        {
            throw SheetMetalRefusal(SheetMetalBodyPolicy.Refusal.EmptySketch, command.UnfoldType);
        }

        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var (container, sheet) = RequireSheetMetal(document, "Тело не создавалось");

        if (BridgeFor(document).TransferTo7(target.Sketch) is not ISketch sketch7)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Эскиз не перенесён в API7 (TransferInterface вернул не ISketch) — листовое тело не создавалось.",
                RetryPolicy.ReacquireContext);
        }

        var bodiesBefore = ReadCount(() => sheet.SheetMetalBodies.Count);
        if (bodiesBefore is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Коллекция листовых тел не читается (SheetMetalBodies.Count) — тело не создавалось.",
                RetryPolicy.ReacquireContext);
        }

        ClearKompasResult(document);
        ISheetMetalBody? created;
        try
        {
            created = sheet.SheetMetalBodies.Add() as ISheetMetalBody;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalBodies.Add() отказал: " + Describe(ex) + ". Тело не создано.",
                RetryPolicy.ReacquireContext);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalBodies.Add() не вернул ISheetMetalBody. Тело не создано.",
                RetryPolicy.ReacquireContext);
        }

        // From here on a body exists in the model: every refusal says so (partialEffects) and names it,
        // because this product has no documented way to remove it.
        var bodyRef = References.Register(SheetMetalFamily, document.Id, document.Revision, created);

        try
        {
            created.Sketch = (Sketch)sketch7;
            created.Thickness = command.ThicknessMm;
            created.Radius = command.BendRadiusMm;
            created.Direction = (ksDirectionTypeEnum)SheetMetalBodyPolicy.DirectionValue(command.Direction)!.Value;
            created.UnfoldType =
                (ksUnfoldTypeEnum)SheetMetalBodyPolicy.UnfoldTypeValue(command.UnfoldType)!.Value;
            WriteUnfoldValue(created, command.UnfoldType, command.BendCoefficient, command.BendValue,
                command.BendReduction);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись свойств листового тела отказала: " + Describe(ex) + ". Тело осталось в модели.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["body_ref"] = bodyRef.Id });
        }

        var updated = SafeBool(() => created.Update());

        // MEASURED: the counter and the extent are read right after Update() and no rebuild follows. The
        // documented route ends at IModelObject::Update. An extra RebuildDocument does not lose the body
        // (a container taken again from the document still answers 1 and the file keeps it), but the
        // container obtained BEFORE the rebuild answers 0 afterwards - so a rebuild would only add a
        // stale handle to the route.
        var bodiesAfter = ReadCount(() => sheet.SheetMetalBodies.Count);
        var read = ReadBody(document, created, ordinal: bodiesAfter is { } after ? after - 1 : null);

        BumpRevision(document, "sheet_metal.create_body");
        var mismatched = SheetMetalBodyPolicy.CreateMismatches(
            read, command.ThicknessMm, command.BendRadiusMm, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);

        var counterGrew = bodiesBefore is { } beforeCount && bodiesAfter is { } afterCount
            && afterCount == beforeCount + 1;
        var extentRead = read?.ExtentByNormalMm is not null;
        var extentMatches = read?.ExtentByNormalMm is { } extent
            && Math.Abs(extent - command.ThicknessMm) <= SheetMetalBodyPolicy.Tolerance;

        var checks = new List<NamedCheck>
        {
            new("body_counter_grew", counterGrew,
                Observed: $"{bodiesBefore} → {bodiesAfter}", Expected: "было + 1"),
            new("values_read_back", mismatched.Count == 0,
                Observed: DescribeBody(read), Expected: DescribeRequest(command)),
            new("update_returned_true", updated == true, Observed: updated?.ToString() ?? "не прочитано"),
            // MEASURED: for a flat body the extent along the sketch normal IS the thickness, so this is a
            // geometry check and not a re-read of the property just written.
            new("extent_by_normal", extentMatches,
                Observed: read?.ExtentByNormalMm?.ToString("0.####", CultureInfo.InvariantCulture)
                    ?? "габарит не прочитан (GetGabarit отдал нечисловое значение)",
                Expected: command.ThicknessMm.ToString("0.####", CultureInfo.InvariantCulture)),
        };

        // An unread extent is a NAMED gap, not a failure: the counter and the properties are still the
        // comparison the order asks for. A read extent that differs IS a failure.
        var confirmed = counterGrew && mismatched.Count == 0 && (!extentRead || extentMatches);
        if (!confirmed)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Листовое тело создано, но чтение назад не подтвердило запрошенное: "
                + string.Join(", ", mismatched.DefaultIfEmpty("габарит по нормали"))
                + ". Тело осталось в модели — удалить его этим продуктом нельзя.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["body_ref"] = bodyRef.Id,
                    ["requested"] = DescribeRequest(command),
                    ["read"] = DescribeBody(read),
                    ["bodies_before"] = bodiesBefore,
                    ["bodies_after"] = bodiesAfter,
                });
        }

        var unverified = new List<string>();
        if (recordedPrimitives is null)
        {
            unverified.Add("empty_sketch_not_checked — профиль эскиза этим сеансом не записан (эскиз нарисован "
                + "другим клиентом или документ переоткрыт): пустоту эскиза проверить нечем, тело создано "
                + "без этой проверки");
        }

        if (!extentRead)
        {
            unverified.Add("extent_not_read — габарит по нормали не прочитан: на детали, где единственное "
                + "тело листовое, GetGabarit отвечает нечисловым значением; проверка габарита не выполнена, "
                + "тело подтверждено счётчиком и перечитанными свойствами");
        }

        unverified.AddRange(new[]
        {
            "direction_measured_once — из четырёх значений ksDirectionTypeEnum на листовом теле измерено "
            + "только normal; остальные приняты по странице перечисления, а не прогоном",
            "unfold_value_bounds_not_documented — страницы коэффициента, величины и уменьшения сгиба не "
            + "называют границ значения, поэтому проверяется только конечность числа",
            "body_not_deletable — созданное тело этим продуктом не удаляется: документированный "
            + "DeleteObject листовые объекты не удаляет",
        });

        return new CreateSheetMetalBodyResult(
            ToDto(bodyRef, $"sheet metal body {command.ThicknessMm:0.###}"),
            read!,
            bodiesBefore.Value,
            bodiesAfter!.Value,
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    // ─── read ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Read the sheet-metal state: one body by reference, or every body of the document plus the
    /// documented collection counters. Writes nothing.</summary>
    public GetSheetMetalResult GetSheetMetal(GetSheetMetalCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var (_, sheet) = RequireSheetMetal(document, "Чтение невозможно");

        var counters = ReadCounters(sheet);
        var bodies = new List<SheetMetalBodyDto>();

        if (command.BodyRef is not null)
        {
            var stored = References.Require(command.BodyRef, document.Id, document.Revision);
            if (stored.Payload is not ISheetMetalBody body)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{command.BodyRef}' указывает не на листовое тело (kind={stored.Kind}).",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            bodies.Add(ReadBody(document, body, ordinal: null) with { BodyRef = command.BodyRef });
        }
        else
        {
            var count = counters.Bodies ?? 0;
            for (var index = 0; index < count; index++)
            {
                var body = SafeObject(() => sheet.SheetMetalBodies[index]) as ISheetMetalBody;
                if (body is null)
                {
                    continue;
                }

                // A body read without a reference still gets one, so the caller can edit what it saw.
                var reference = References.Register(SheetMetalFamily, document.Id, document.Revision, body,
                    identity: ComIdentity.Of(body));
                bodies.Add(ReadBody(document, body, index) with { BodyRef = reference.Id });
            }
        }

        var checks = new List<NamedCheck>
        {
            new("bodies_read", bodies.Count > 0, Observed: bodies.Count.ToString(CultureInfo.InvariantCulture)),
            new("counters_read", counters.Unread.Count == 0,
                Observed: counters.Unread.Count == 0 ? "все счётчики прочитаны"
                    : "не прочитаны: " + string.Join(", ", counters.Unread)),
        };

        var unverified = new List<string>();
        if (counters.Unread.Count > 0)
        {
            unverified.Add("counters_unread — часть счётчиков коллекций не ответила: пустое значение "
                + "означает «не прочитано», а не ноль");
        }

        return new GetSheetMetalResult(
            bodies,
            counters,
            new VerificationDto(VerificationLevel.CallReturned, checks, unverified))
        {
            Bends = ReadCollection(counters.Bends, index => SafeObject(() => sheet.SheetMetalBends[index])
                as ISheetMetalBend, (bend, index) => ReadBend(bend, requestedEdge: null, ordinal: index)),
            Holes = ReadCollection(counters.Holes, index => SafeObject(() => sheet.SheetMetalHoles[index])
                as ISheetMetalHole, (hole, index) => ReadHole(hole, index)),
            // MEASURED: the cut collection is addressed from zero - the help page names no base, so the
            // zero is a measurement. History: docs/decisions/sheet-metal.md#cut-tool
            Cuts = ReadCollection(counters.Cuts,
                index => SafeObject(() => sheet.SheetMetalCuts.get_SheetMetalCut(index)) as ISheetMetalCut,
                (cut, index) => ReadCut(cut, index)),
        };
    }

    /// <summary>Every element of a collection whose counter answered, or <c>null</c> when the counter did
    /// not — an unread counter is not an empty collection.</summary>
    private static IReadOnlyList<TDto>? ReadCollection<TElement, TDto>(
        int? count, Func<int, TElement?> at, Func<TElement, int, TDto> describe)
        where TElement : class
    {
        if (count is not { } total)
        {
            return null;
        }

        var rows = new List<TDto>(total);
        for (var index = 0; index < total; index++)
        {
            if (at(index) is { } element)
            {
                rows.Add(describe(element, index));
            }
        }

        return rows;
    }

    // ─── edit ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Change thickness, bend radius, unfold method and its field of an existing body.</summary>
    /// <remarks>INVARIANT: the method is taken from the MODEL before the field rule is applied, so a field
    /// of another method is refused by name instead of being written and ignored.
    /// History: docs/decisions/sheet-metal.md#body-tools</remarks>
    public EditSheetMetalBodyResult EditSheetMetalBody(EditSheetMetalBodyCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ISheetMetalBody body)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{command.BodyRef}' указывает не на листовое тело (kind={stored.Kind}).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var before = ReadBody(document, body, ordinal: null);
        var refusal = SheetMetalBodyPolicy.CheckEdit(
            before?.UnfoldType, command.ThicknessMm, command.BendRadiusMm, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);
        if (refusal != SheetMetalBodyPolicy.Refusal.None)
        {
            throw SheetMetalRefusal(refusal, command.UnfoldType ?? before?.UnfoldType);
        }

        var (container, sheet) = RequireSheetMetal(document, "Правка невозможна");
        var repeat = SheetMetalBodyPolicy.IsRepeat(
            before, command.ThicknessMm, command.BendRadiusMm, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);

        try
        {
            if (command.ThicknessMm is { } thickness)
            {
                body.Thickness = thickness;
            }

            if (command.BendRadiusMm is { } radius)
            {
                body.Radius = radius;
            }

            if (command.UnfoldType is { } unfoldType)
            {
                body.UnfoldType = (ksUnfoldTypeEnum)SheetMetalBodyPolicy.UnfoldTypeValue(unfoldType)!.Value;
            }

            WriteUnfoldValue(body, command.UnfoldType ?? before?.UnfoldType,
                command.BendCoefficient, command.BendValue, command.BendReduction);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись свойств листового тела отказала: " + Describe(ex) + ". Тело изменено не полностью.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["body_ref"] = command.BodyRef });
        }

        var updated = SafeBool(() => body.Update());

        // Same order as on creation: read right after Update(), no extra rebuild.
        var after = ReadBody(document, body, ordinal: null);
        var bends = ReadCount(() => sheet.SheetMetalBends.Count);

        BumpRevision(document, "sheet_metal.edit_body");
        var unchanged = SameValues(before, after);
        // A flat body's extent along the normal IS its thickness, so a thickness edit must move it. With
        // bends in the body the extent is no longer the thickness, and that is named, not assumed.
        var extentApplies = command.ThicknessMm is not null && bends == 0;
        var extentFollows = extentApplies && after?.ExtentByNormalMm is { } editedExtent
            && Math.Abs(editedExtent - command.ThicknessMm!.Value) <= SheetMetalBodyPolicy.Tolerance;
        var mismatched = SheetMetalBodyPolicy.EditMismatches(
            after, command.ThicknessMm, command.BendRadiusMm, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);

        // The extent is part of the edit's success only where it means something: a flat body, thickness
        // changed. Elsewhere it is named as not applicable instead of being quietly skipped.
        var failed = new List<string>(mismatched);
        // Only a READ extent can fail the edit; an unread one is named in unverified_aspects instead.
        if (extentApplies && !extentFollows && after?.ExtentByNormalMm is not null)
        {
            failed.Add("extent_by_normal");
        }

        var checks = new List<NamedCheck>
        {
            new("values_read_back", mismatched.Count == 0,
                Observed: DescribeBody(after),
                Expected: DescribeEdit(command, before)),
            new("update_returned_true", updated == true, Observed: updated?.ToString() ?? "не прочитано"),
            new("extent_follows_thickness", !extentApplies || extentFollows,
                Observed: after?.ExtentByNormalMm?.ToString("0.####", CultureInfo.InvariantCulture)
                    ?? "габарит не прочитан",
                Expected: extentApplies
                    ? command.ThicknessMm!.Value.ToString("0.####", CultureInfo.InvariantCulture)
                    : "проверка не применима: толщина не менялась или в теле есть сгибы"),
            // INVARIANT: the repeat is visible in the answer and is a COMPARISON: a repeat must leave the
            // body as it was, an edit must change it. Both halves can fail.
            new("idempotent_repeat", repeat == unchanged,
                Observed: $"повтор={repeat} тело_не_изменилось={unchanged}",
                Expected: "повтор ничего не меняет, правка меняет"),
        };

        if (failed.Count != 0)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Правка листового тела не подтверждена чтением назад: " + string.Join(", ", failed)
                + ". Запрошенное и прочитанное — в details.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["body_ref"] = command.BodyRef,
                    ["requested"] = DescribeEdit(command, before),
                    ["read"] = DescribeBody(after),
                });
        }

        var unverified = new List<string>
        {
            "unfold_value_bounds_not_documented — страницы коэффициента, величины и уменьшения сгиба не "
            + "называют границ значения",
            "bends_not_checked — сгибы и вырезы тела правкой не пересчитываются и здесь не проверяются",
        };
        if (extentApplies && after?.ExtentByNormalMm is null)
        {
            unverified.Insert(0, "extent_not_read — габарит по нормали не прочитан (GetGabarit отдал "
                + "нечисловое значение): проверка «габарит следует толщине» не выполнена");
        }

        return new EditSheetMetalBodyResult(
            ToDto(stored, "sheet metal body (edit)"),
            before ?? new SheetMetalBodyDto(),
            after!,
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    // ─── bend ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A bend on ONE edge of an existing body.</summary>
    /// <remarks>INVARIANT: the edge must belong to this body, checked before <c>Add()</c> — MEASURED: the
    /// kernel accepts an edge of another body, grows the bend counter and moves the extent, and a bend
    /// cannot be deleted by this product. INVARIANT: success is the counter +1, every requested field read
    /// back, <c>BendObjects</c> holding the requested edge and a changed extent — never
    /// <c>Update() = true</c>. LIMIT: an edge produced by a later feature carries that feature as its
    /// <c>Owner</c>, so such an edge is refused here; the address is a body edge.
    /// History: docs/decisions/sheet-metal.md#feature-tools</remarks>
    public CreateSheetMetalBendResult CreateSheetMetalBend(CreateSheetMetalBendCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ISheetMetalBody body)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{command.BodyRef}' указывает не на листовое тело (kind={stored.Kind}).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var refusal = SheetMetalFeaturePolicy.CheckBend(
            command.AngleDeg, command.RadiusMm, command.LengthType, command.LengthMm);
        if (refusal != SheetMetalFeaturePolicy.Refusal.None)
        {
            throw FeatureRefusal(refusal, "Сгиб не создавался");
        }

        var bridge = BridgeFor(document);
        RequireKind(document, command.EdgeRef, "edge", "edge_ref", "Сгиб не создавался");
        var edge = RequireModelObject(document, bridge, command.EdgeRef, "edge", "edge_ref");
        var ownership = OwnerIsBody(body, edge);
        if (ownership == false)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Ребро принадлежит другому элементу модели, а не телу '" + command.BodyRef + "': ядро "
                + "принимает чужое ребро, счётчик сгибов растёт и габарит меняется, а удалить такой сгиб "
                + "этот продукт не может (измерено). Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "edge_ref" });
        }

        var (container, sheet) = RequireSheetMetal(document, "Сгиб не создавался");
        var bendsBefore = ReadCount(() => sheet.SheetMetalBends.Count);
        var facesBefore = CountFaces(document);
        var extentBefore = ExtentByNormal(document);

        ClearKompasResult(document);
        ISheetMetalBend? created;
        try
        {
            created = sheet.SheetMetalBends.Add() as ISheetMetalBend;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalBends.Add() отказал: " + Describe(ex) + ". Сгиб не создан.",
                RetryPolicy.ReacquireContext);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalBends.Add() не вернул ISheetMetalBend. Сгиб не создан.",
                RetryPolicy.ReacquireContext);
        }

        // A bend exists in the collection from here on and this product cannot remove it.
        var bendRef = References.Register("sheet_metal_bend", document.Id, document.Revision, created);
        try
        {
            created.BendObject = edge;
            created.Angle = command.AngleDeg;
            if (command.Direction is { } direction)
            {
                created.Direction = direction;
            }

            if (command.InternalRadius is { } internalRadius)
            {
                created.InternalRadius = internalRadius;
            }

            if (command.RadiusMm is { } radius)
            {
                created.Radius = radius;
            }

            if (command.LengthType is { } lengthType)
            {
                created.LengthType =
                    (ksBendLengthTypeEnum)SheetMetalFeaturePolicy.LengthTypeValue(lengthType)!.Value;
            }

            if (command.LengthMm is { } length)
            {
                created.Length = length;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись свойств сгиба отказала: " + Describe(ex) + ". Сгиб остался в коллекции.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["bend_ref"] = bendRef.Id });
        }

        var updated = SafeBool(() => created.Update());
        var bendsAfter = ReadCount(() => sheet.SheetMetalBends.Count);
        var read = ReadBend(created, edge, ordinal: bendsAfter is { } after ? after - 1 : null);
        var extentAfter = ExtentByNormal(document);
        var facesAfter = CountFaces(document);

        BumpRevision(document, "sheet_metal.create_bend");
        var mismatched = SheetMetalFeaturePolicy.BendMismatches(
            read, command.AngleDeg, command.Direction, command.InternalRadius, command.RadiusMm,
            command.LengthType, command.LengthMm);

        var counterGrew = bendsBefore is { } beforeCount && bendsAfter is { } afterCount
            && afterCount == beforeCount + 1;
        var extentRead = extentBefore is not null && extentAfter is not null;
        var extentMoved = extentRead && Math.Abs(extentAfter!.Value - extentBefore!.Value) > SheetMetalFeaturePolicy.Tolerance;
        // MEASURED: a second bend on the opposite side can leave the extent along the normal unchanged,
        // so "the body changed" is proven by the extent OR by a grown face count - a bend adds faces.
        var facesGrew = facesBefore is { } facesWas && facesAfter is { } facesNow && facesNow > facesWas;
        var bodyChanged = extentMoved || facesGrew;

        var checks = new List<NamedCheck>
        {
            new("bend_counter_grew", counterGrew,
                Observed: $"{bendsBefore} → {bendsAfter}", Expected: "было + 1"),
            new("values_read_back", mismatched.Count == 0,
                Observed: DescribeBend(read), Expected: DescribeBendRequest(command)),
            new("update_returned_true", updated == true, Observed: updated?.ToString() ?? "не прочитано"),
            new("bend_objects_is_requested_edge", read?.EdgeIsRequested == true,
                Observed: read?.EdgeIsRequested?.ToString() ?? "BendObjects не прочитан",
                Expected: "запрошенное ребро прочитано"),
            new("body_changed", bodyChanged,
                Observed: $"габарит {Num(extentBefore)} → {Num(extentAfter)}, граней {facesBefore} → {facesAfter}",
                Expected: "габарит тела изменился или число граней выросло"),
        };

        // INVARIANT: an unread BendObjects is a refusal, never a pass - the check may not report as
        // matched what was not read, and the tool description promises exactly this comparison.
        var verified = counterGrew && mismatched.Count == 0 && read?.EdgeIsRequested == true
            && bodyChanged;
        if (!verified)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Сгиб создан, но чтение назад не подтвердило запрошенное: "
                + string.Join(", ", checks.Where(check => !check.Passed).Select(check => check.Name))
                + ". Сгиб остался в модели — удалить его этим продуктом нельзя.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["bend_ref"] = bendRef.Id,
                    ["requested"] = DescribeBendRequest(command),
                    ["read"] = DescribeBend(read),
                    ["bends_before"] = bendsBefore,
                    ["bends_after"] = bendsAfter,
                });
        }

        var unverified = new List<string>();
        if (ownership is null)
        {
            unverified.Add("edge_owner_not_compared — владелец ребра или тела не прочитался как объект: "
                + "принадлежность ребра телу проверена только чтением BendObjects после построения");
        }

        if (!extentRead)
        {
            unverified.Add("extent_not_read — габарит тела не прочитан (GetGabarit отдал нечисловое "
                + "значение): проверка «габарит изменился» не выполнена");
        }

        unverified.Add("bend_bounds_not_documented — страницы угла, радиуса и длины называют только тип: "
            + "проверяются положительность и конечность числа, а не границы, которых справка не называет");

        return new CreateSheetMetalBendResult(
            ToDto(bendRef, $"sheet metal bend {command.AngleDeg:0.###}"),
            read!,
            bendsBefore!.Value,
            bendsAfter!.Value,
            ReadBody(document, body, ordinal: null),
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    // ─── hole ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A round hole on a face of an existing body.</summary>
    /// <remarks>INVARIANT: the kernel builds the hole's own sketch, so the caller names a face and a point.
    /// INVARIANT: success is the counter +1, every requested field read back and a face count that GREW
    /// compared with the reading before — never <c>Update() = true</c>.
    /// History: docs/decisions/sheet-metal.md#feature-tools</remarks>
    public CreateSheetMetalHoleResult CreateSheetMetalHole(CreateSheetMetalHoleCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ISheetMetalBody body)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{command.BodyRef}' указывает не на листовое тело (kind={stored.Kind}).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var refusal = SheetMetalFeaturePolicy.CheckHole(command.DiameterMm, command.CutType, command.DepthMm);
        if (refusal != SheetMetalFeaturePolicy.Refusal.None)
        {
            throw FeatureRefusal(refusal, "Отверстие не создавалось");
        }

        var bridge = BridgeFor(document);
        RequireKind(document, command.BaseFaceRef, "face", "base_face_ref", "Отверстие не создавалось");
        var face = RequireModelObject(document, bridge, command.BaseFaceRef, "face", "base_face_ref");
        var ownership = OwnerIsBody(body, face);
        if (ownership == false)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Опорная грань принадлежит другому элементу модели, а не телу '" + command.BodyRef
                + "': ядро принимает такую грань, а удалить созданное отверстие этот продукт не может. "
                + "Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "base_face_ref" });
        }

        var (container, sheet) = RequireSheetMetal(document, "Отверстие не создавалось");
        var holesBefore = ReadCount(() => sheet.SheetMetalHoles.Count);
        var facesBefore = CountFaces(document);

        ClearKompasResult(document);
        ISheetMetalHole? created;
        try
        {
            created = sheet.SheetMetalHoles.Add() as ISheetMetalHole;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalHoles.Add() отказал: " + Describe(ex) + ". Отверстие не создано.",
                RetryPolicy.ReacquireContext);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalHoles.Add() не вернул ISheetMetalHole. Отверстие не создано.",
                RetryPolicy.ReacquireContext);
        }

        var holeRef = References.Register("sheet_metal_hole", document.Id, document.Revision, created);
        try
        {
            created.Body = body;
            created.BasePlane = face;
            created.X = command.XMm;
            created.Y = command.YMm;
            created.Diameter = command.DiameterMm;
            if (command.CutType is { } cutType)
            {
                created.CutType =
                    (ksHoleCutTypeEnum)SheetMetalFeaturePolicy.CutTypeValue(cutType)!.Value;
            }

            if (command.DepthMm is { } depth)
            {
                created.Depth = depth;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись свойств отверстия отказала: " + Describe(ex) + ". Отверстие осталось в коллекции.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["hole_ref"] = holeRef.Id });
        }

        var updated = SafeBool(() => created.Update());
        var holesAfter = ReadCount(() => sheet.SheetMetalHoles.Count);
        var read = ReadHole(created, ordinal: holesAfter is { } after ? after - 1 : null);
        var facesAfter = CountFaces(document);

        BumpRevision(document, "sheet_metal.create_hole");
        var mismatched = SheetMetalFeaturePolicy.HoleMismatches(
            read, command.XMm, command.YMm, command.DiameterMm, command.CutType, command.DepthMm);

        var counterGrew = holesBefore is { } beforeCount && holesAfter is { } afterCount
            && afterCount == beforeCount + 1;
        var facesRead = facesBefore is not null && facesAfter is not null;

        var checks = new List<NamedCheck>
        {
            new("hole_counter_grew", counterGrew,
                Observed: $"{holesBefore} → {holesAfter}", Expected: "было + 1"),
            new("values_read_back", mismatched.Count == 0,
                Observed: DescribeHole(read), Expected: DescribeHoleRequest(command)),
            new("update_returned_true", updated == true, Observed: updated?.ToString() ?? "не прочитано"),
            new("face_count_grew", !facesRead || FaceCountGrowth.Grew(facesBefore, facesAfter),
                Observed: FaceCountGrowth.Observed(facesBefore, facesAfter),
                Expected: facesRead ? FaceCountGrowth.Expected(facesBefore) : "число граней не прочитано"),
        };

        var confirmed = counterGrew && mismatched.Count == 0
            && (!facesRead || FaceCountGrowth.Grew(facesBefore, facesAfter));
        if (!confirmed)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Отверстие создано, но чтение назад не подтвердило запрошенное: "
                + string.Join(", ", mismatched.DefaultIfEmpty("счётчик или число граней"))
                + ". Отверстие осталось в модели — удалить его этим продуктом нельзя.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["hole_ref"] = holeRef.Id,
                    ["requested"] = DescribeHoleRequest(command),
                    ["read"] = DescribeHole(read),
                    ["holes_before"] = holesBefore,
                    ["holes_after"] = holesAfter,
                    ["faces_before"] = facesBefore,
                    ["faces_after"] = facesAfter,
                });
        }

        var unverified = new List<string>();
        if (ownership is null)
        {
            unverified.Add("face_owner_not_compared — владелец грани или тела не прочитался как объект: "
                + "принадлежность грани телу проверена только ответом ядра");
        }

        if (!facesRead)
        {
            unverified.Add("face_count_not_read — число граней тела не прочитано: проверка «граней стало "
                + "больше» не выполнена");
        }

        unverified.Add("hole_point_not_checked — страница называет X и Y координатами центра на опорной "
            + "плоскости, но не говорит, что точка вне грани отвергается: попадание точки в грань не "
            + "проверяется");

        return new CreateSheetMetalHoleResult(
            ToDto(holeRef, $"sheet metal hole D{command.DiameterMm:0.###}"),
            read!,
            holesBefore!.Value,
            holesAfter!.Value,
            ReadBody(document, body, ordinal: null),
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    // ─── cut ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A cut in an existing body by a sketch standing on a FLAT FACE of that body.</summary>
    /// <remarks>INVARIANT: the sketch support decides which body is cut — MEASURED: a sketch on a face of
    /// another body cuts THAT body while <c>Body</c> names the first, so the owner is compared before
    /// <c>Add()</c>. INVARIANT: success is the counter +1, the requested fields read back and a face count
    /// that GREW — the extent cannot serve, a cut inside a face leaves it unchanged. <c>LIMIT:</c> the shape
    /// and the volume of the cut are not checked, only the grown face count.
    /// History: docs/decisions/sheet-metal.md#cut-tool</remarks>
    public CreateSheetMetalCutResult CreateSheetMetalCut(CreateSheetMetalCutCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var stored = References.Require(command.BodyRef, document.Id, document.Revision);
        if (stored.Payload is not ISheetMetalBody body)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{command.BodyRef}' указывает не на листовое тело (kind={stored.Kind}).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        var refusal = SheetMetalFeaturePolicy.CheckCut(command.CutType, command.DepthMm);
        if (refusal != SheetMetalFeaturePolicy.Refusal.None)
        {
            throw FeatureRefusal(refusal, "Вырез не создавался");
        }

        var target = RequireSketch(command.SketchRef);
        if (!string.Equals(target.Document.Id, document.Id, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Эскиз '{command.SketchRef}' принадлежит другому документу: вырез строится в теле "
                + "'" + command.BodyRef + "' того же документа. Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "sketch_ref" });
        }

        // MEASURED: the kernel refuses an empty sketch (Update() = FALSE) but KEEPS the record, and this
        // product cannot delete a sheet-metal feature — so emptiness is refused before Add().
        if (SheetMetalBodyPolicy.SketchIsEmpty(RecordedPrimitives(command.SketchRef)))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Эскиз выреза пуст: ядро такой вырез отвергает (Update() = FALSE), а запись в коллекции "
                + "остаётся, и удалить её этот продукт не может. Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "sketch_ref" });
        }

        var face = RequireSketchFaceSupport(target, command.SketchRef);

        var bridge = BridgeFor(document);
        var faceModel = bridge.TransferTo7(face) as IModelObject;
        var ownership = faceModel is null ? null : OwnerIsBody(body, faceModel);
        if (ownership == false)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Грань-опора эскиза принадлежит другому телу, а не телу '" + command.BodyRef + "': ядро "
                + "режет владельца грани, а не тело из body_ref (измерено), и удалить такой вырез этот "
                + "продукт не может. Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "sketch_ref" });
        }

        if (bridge.TransferTo7(target.Sketch) is not ISketch sketch7)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Эскиз не перенесён в API7 (TransferInterface вернул не ISketch) — вырез не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var (container, sheet) = RequireSheetMetal(document, "Вырез не создавался");
        var cutsBefore = ReadCount(() => sheet.SheetMetalCuts.Count);
        var facesBefore = CountFaces(document);
        var gabaritBefore = DescribeBody(document).Gabarit;

        ClearKompasResult(document);
        ISheetMetalCut? created;
        try
        {
            created = sheet.SheetMetalCuts.Add() as ISheetMetalCut;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalCuts.Add() отказал: " + Describe(ex) + ". Вырез не создан.",
                RetryPolicy.ReacquireContext);
        }

        if (created is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ISheetMetalCuts.Add() не вернул ISheetMetalCut. Вырез не создан.",
                RetryPolicy.ReacquireContext);
        }

        var cutRef = References.Register("sheet_metal_cut", document.Id, document.Revision, created);
        try
        {
            created.Body = (IKompasAPIObject)body;
            created.Sketch = (Sketch)sketch7;
            created.CutType = (ksHoleCutTypeEnum)SheetMetalFeaturePolicy.CutTypeValue(command.CutType)!.Value;
            if (command.DepthMm is { } depth)
            {
                created.Depth = depth;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись свойств выреза отказала: " + Describe(ex) + ". Вырез остался в коллекции.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["cut_ref"] = cutRef.Id });
        }

        var updated = SafeBool(() => created.Update());
        var cutsAfter = ReadCount(() => sheet.SheetMetalCuts.Count);
        var read = ReadCut(created, ordinal: cutsAfter is { } after ? after - 1 : null);
        var facesAfter = CountFaces(document);
        var gabaritAfter = DescribeBody(document).Gabarit;

        BumpRevision(document, "sheet_metal.create_cut");
        var mismatched = SheetMetalFeaturePolicy.CutMismatches(read, command.CutType, command.DepthMm);

        var counterGrew = cutsBefore is { } beforeCount && cutsAfter is { } afterCount
            && afterCount == beforeCount + 1;
        var facesRead = facesBefore is not null && facesAfter is not null;
        var facesGrew = FaceCountGrowth.Grew(facesBefore, facesAfter);

        var checks = new List<NamedCheck>
        {
            new("cut_counter_grew", counterGrew,
                Observed: $"{cutsBefore} → {cutsAfter}", Expected: "было + 1"),
            new("values_read_back", mismatched.Count == 0,
                Observed: DescribeCut(read), Expected: DescribeCutRequest(command)),
            new("update_returned_true", updated == true, Observed: updated?.ToString() ?? "не прочитано"),
            // INVARIANT: an unread face count is not a grown one - silence is never confirmation.
            new("face_count_grew", facesGrew,
                Observed: FaceCountGrowth.Observed(facesBefore, facesAfter),
                Expected: FaceCountGrowth.Expected(facesBefore)),
        };

        var verified = counterGrew && mismatched.Count == 0 && updated == true && facesGrew;
        if (!verified)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Вырез создан, но чтение назад не подтвердило запрошенное: "
                + string.Join(", ", checks.Where(check => !check.Passed).Select(check => check.Name))
                + ". Вырез остался в модели — удалить его этим продуктом нельзя.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["cut_ref"] = cutRef.Id,
                    ["requested"] = DescribeCutRequest(command),
                    ["read"] = DescribeCut(read),
                    ["cuts_before"] = cutsBefore,
                    ["cuts_after"] = cutsAfter,
                    ["faces_before"] = facesBefore,
                    ["faces_after"] = facesAfter,
                });
        }

        var unverified = new List<string>();
        if (ownership is null)
        {
            unverified.Add("face_owner_not_compared — грань-опора не перенесена в API7 как объект модели "
                + "или владелец не прочитался: принадлежность грани телу проверена только геометрией "
                + "после построения");
        }

        if (!facesRead)
        {
            unverified.Add("face_count_not_read — число граней тела не прочитано: проверка «граней стало "
                + "больше» не выполнена");
        }

        unverified.Add("cut_shape_not_checked — форма и объём выреза не проверяются: страницы называют "
            + "только тип и глубину, а признак успеха - рост числа граней");

        return new CreateSheetMetalCutResult(
            ToDto(cutRef, $"sheet metal cut {command.CutType}"),
            read!,
            cutsBefore!.Value,
            cutsAfter!.Value,
            facesBefore,
            facesAfter,
            gabaritBefore,
            gabaritAfter,
            ReadBody(document, body, ordinal: null),
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    /// <summary>The sketch support as a <c>ksFaceDefinition</c>: a cut is measured only on a flat face, so
    /// a sketch on a plane is refused by name before anything is written.</summary>
    private ksFaceDefinition RequireSketchFaceSupport(SketchTarget target, string sketchRef)
    {
        if (target.Sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Определение эскиза '{sketchRef}' не получено: опору выреза не прочитать.",
                RetryPolicy.ReacquireContext);
        }

        var support = SafeObject(() => definition.GetPlane());
        if (AsInterface<ksFaceDefinition>(support) is not { } face)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Опора эскиза '{sketchRef}' — не грань: вырез режет только по эскизу на плоской грани "
                + "тела. Создайте эскиз с face_ref (kompas_create_sketch). Обращение к КОМПАС не "
                + "выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["field"] = "sketch_ref",
                    ["support_type"] = support is ksEntity entity ? entity.type : null,
                });
        }

        if (SafeBool(() => face.IsPlanar()) is not true)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Опора эскиза '{sketchRef}' — не плоская грань: вырез на криволинейной опоре не измерен, "
                + "поэтому такой вырез не строится. Обращение к КОМПАС не выполнялось.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "sketch_ref" });
        }

        return face;
    }

    // ─── shared ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The container and its sheet-metal view; a document that is not a sheet-metal part is a
    /// named refusal, not a null further down.</summary>
    private (IModelContainer Container, ISheetMetalContainer Sheet) RequireSheetMetal(
        DocumentEntry document, string consequence)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document3D, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") + ". "
                + consequence + ".",
                RetryPolicy.ReacquireContext);
        }

        if (container is not ISheetMetalContainer sheet)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IPart7 не приводится к ISheetMetalContainer: документ не является листовой деталью "
                + "(справка: интерфейс получается от IPart7 через QueryInterface). " + consequence + ".",
                RetryPolicy.Never);
        }

        return (container, sheet);
    }

    /// <summary>Writes the field the method selects. Only the field of the CURRENT method is written: the
    /// pages say each of the three is available under its own type.</summary>
    private static void WriteUnfoldValue(
        ISheetMetalBody body, string? unfoldType, double? coefficient, double? bendValue, double? reduction)
    {
        switch (unfoldType)
        {
            case "coefficient" when coefficient is { } value:
                body.BendCoefficient = value;
                break;
            case "bend_value" when bendValue is { } value:
                body.BendValue = value;
                break;
            case "bend_reduction" when reduction is { } value:
                body.BendReduction = value;
                break;
        }
    }

    /// <summary>One body as the model holds it. Every field is a read value; <c>null</c> — not read.</summary>
    private SheetMetalBodyDto ReadBody(DocumentEntry document, ISheetMetalBody body, int? ordinal)
    {
        var unfoldType = SafeInt(() => (int)body.UnfoldType);
        var name = unfoldType is null ? null : SheetMetalBodyPolicy.UnfoldTypeName(unfoldType);

        // The field is read by the method the MODEL reports, not by what the caller asked for.
        var value = name switch
        {
            "coefficient" => SafeDouble(() => body.BendCoefficient),
            "bend_value" => SafeDouble(() => body.BendValue),
            "bend_reduction" => SafeDouble(() => body.BendReduction),
            _ => null,
        };

        return new SheetMetalBodyDto
        {
            Ordinal = ordinal,
            ThicknessMm = SafeDouble(() => body.Thickness),
            BendRadiusMm = SafeDouble(() => body.Radius),
            UnfoldType = name,
            UnfoldValue = value,
            Straighten = SafeBool(() => body.Straighten),
            ExtentByNormalMm = ExtentByNormal(document),
        };
    }

    /// <summary>Extent along Z of the whole document — for a flat sheet body this is the thickness.
    /// <c>LIMIT:</c> the normal is taken as Z, the arrangement the probe measured (sketch on XOY); a body
    /// built on another plane reports its Z extent, which is not its thickness.</summary>
    private static double? ExtentByNormal(DocumentEntry document)
    {
        if (!TryReadGabaritCorners(document, full: true, customizable: true, out var min, out var max)
            || min.Length < 3 || max.Length < 3)
        {
            return null;
        }

        var extent = max[2] - min[2];
        // MEASURED: on a part whose only body is a sheet-metal body GetGabarit answers true and hands back
        // a sentinel around 1e300 until the model has been rebuilt once more. A sentinel is "not read",
        // never a length: turning it into a number would confirm or refuse a geometry nobody measured.
        return double.IsFinite(extent) && Math.Abs(extent) < 1e6 ? extent : null;
    }

    /// <summary>Every documented collection counter of <c>isheetmetalcontainer.html</c>. A counter the
    /// kernel does not answer is named in <see cref="SheetMetalCountersDto.Unread"/>, never turned into 0.</summary>
    private static SheetMetalCountersDto ReadCounters(ISheetMetalContainer sheet)
    {
        var unread = new List<string>();
        int? Count(Func<int> read, string name)
        {
            var value = ReadCount(read);
            if (value is null)
            {
                unread.Add(name);
            }

            return value;
        }

        return new SheetMetalCountersDto
        {
            Bodies = Count(() => sheet.SheetMetalBodies.Count, "sheet_metal_bodies"),
            Bends = Count(() => sheet.SheetMetalBends.Count, "sheet_metal_bends"),
            Cuts = Count(() => sheet.SheetMetalCuts.Count, "sheet_metal_cuts"),
            Holes = Count(() => sheet.SheetMetalHoles.Count, "sheet_metal_holes"),
            LineBends = Count(() => sheet.SheetMetalLineBends.Count, "sheet_metal_line_bends"),
            ClosedCorners = Count(() => sheet.SheetMetalClosedCorners.Count, "sheet_metal_closed_corners"),
            Flangings = Count(() => sheet.SheetMetalFlangings.Count, "sheet_metal_flangings"),
            Jalousies = Count(() => sheet.SheetMetalJalousies.Count, "sheet_metal_jalousies"),
            Plates = Count(() => sheet.SheetMetalPlates.Count, "sheet_metal_plates"),
            PressFormings = Count(() => sheet.SheetMetalPressFormings.Count, "sheet_metal_press_formings"),
            Punchs = Count(() => sheet.SheetMetalPunchs.Count, "sheet_metal_punchs"),
            Ribs = Count(() => sheet.SheetMetalRibs.Count, "sheet_metal_ribs"),
            Shoulders = Count(() => sheet.SheetMetalShoulders.Count, "sheet_metal_shoulders"),
            SketchBends = Count(() => sheet.SheetMetalSketchBends.Count, "sheet_metal_sketch_bends"),
            Undercuts = Count(() => sheet.SheetMetalUndercuts.Count, "sheet_metal_undercuts"),
            RuledShells = Count(() => sheet.SheetMetalRuledShells.Count, "sheet_metal_ruled_shells"),
            LinearRuledShells =
                Count(() => sheet.SheetMetalLinearRuledShells.Count, "sheet_metal_linear_ruled_shells"),
            ConvertsToSheetMetals =
                Count(() => sheet.ConvertsToSheetMetals.Count, "converts_to_sheet_metals"),
            BendedStraightens =
                Count(() => sheet.SheetMetalBendedStraightens.Count, "sheet_metal_bended_straightens"),
            Unread = unread,
        };
    }

    /// <summary>Primitives this server drew into the sketch, or <c>null</c> when it drew none of them —
    /// a sketch written by another client or a reopened document, where "empty" cannot be claimed.</summary>
    private int? RecordedPrimitives(string sketchRef) =>
        _sketchProfiles.TryGetValue(sketchRef, out var profile) ? profile.Entities.Count : null;

    /// <summary>The reference must carry the kind the field names: a face passed as <c>edge_ref</c> would
    /// otherwise transfer to a model object and be refused later by the kernel, not by the contract.</summary>
    private void RequireKind(DocumentEntry document, string reference, string kind, string field, string consequence)
    {
        var stored = References.Require(reference, document.Id, document.Revision);
        if (string.Equals(stored.Kind, kind, StringComparison.Ordinal))
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Ссылка '{reference}' несёт объект вида '{stored.Kind}', а поле {field} требует '{kind}'. "
            + consequence + ".",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = field,
                ["kind"] = stored.Kind,
                ["expected_kind"] = kind,
            });
    }

    /// <summary>One bend as the model holds it; every field is a read value, <c>null</c> — not read.</summary>
    private static SheetMetalBendDto ReadBend(ISheetMetalBend bend, IModelObject? requestedEdge, int? ordinal) =>
        new()
        {
            Ordinal = ordinal,
            AngleDeg = SafeDouble(() => bend.Angle),
            Direction = SafeBool(() => bend.Direction),
            InternalRadius = SafeBool(() => bend.InternalRadius),
            RadiusMm = SafeDouble(() => bend.Radius),
            LengthType = SheetMetalFeaturePolicy.LengthTypeName(SafeInt(() => (int)bend.LengthType)),
            LengthMm = SafeDouble(() => bend.Length),
            EdgeIsRequested = HoldsEdge(bend, requestedEdge),
        };

    /// <summary>Whether <c>BendObjects</c> holds the requested edge. MEASURED: the kernel hands the edge
    /// back as another wrapper, so the comparison is by COM identity; an identity that cannot be stated is
    /// "not read", never "another edge".</summary>
    /// <remarks>MEASURED: the collection was measured with one object and the kernel may hand it back
    /// either as the object itself or as a one-element array; both shapes are read, and anything else is
    /// "not read" rather than a mismatch.</remarks>
    private static bool? HoldsEdge(ISheetMetalBend bend, IModelObject? requestedEdge)
    {
        var wanted = ComIdentity.Of(requestedEdge);
        if (wanted is null)
        {
            return null;
        }

        object? held;
        try
        {
            held = bend.BendObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        if (held is null)
        {
            return null;
        }

        foreach (var item in held is Array array ? array : new[] { held })
        {
            if (string.Equals(ComIdentity.Of(item), wanted, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One cut as the model holds it.</summary>
    /// <remarks>MEASURED: <c>ISheetMetalCuts</c> is addressed from zero — every index was read and the
    /// answers ran 0..4; the help page names no base, so the zero is a measurement, not a reading.
    /// History: docs/decisions/sheet-metal.md#cut-tool</remarks>
    private static SheetMetalCutDto ReadCut(ISheetMetalCut cut, int? ordinal) => new()
    {
        Ordinal = ordinal,
        CutType = SheetMetalFeaturePolicy.CutTypeName(SafeInt(() => (int)cut.CutType)),
        DepthMm = SafeDouble(() => cut.Depth),
    };

    /// <summary>One hole as the model holds it.</summary>
    private static SheetMetalHoleDto ReadHole(ISheetMetalHole hole, int? ordinal) => new()
    {
        Ordinal = ordinal,
        XMm = SafeDouble(() => hole.X),
        YMm = SafeDouble(() => hole.Y),
        DiameterMm = SafeDouble(() => hole.Diameter),
        CutType = SheetMetalFeaturePolicy.CutTypeName(SafeInt(() => (int)hole.CutType)),
        DepthMm = SafeDouble(() => hole.Depth),
    };

    /// <summary>Whether a model object's owning feature is the body's own — the documented
    /// <c>Owner</c> («объект дерева, породивший этот объект») compared by COM identity. <c>null</c> when
    /// either identity cannot be stated: not known is never "foreign".</summary>
    private static bool? OwnerIsBody(ISheetMetalBody body, IModelObject model)
    {
        var bodyOwner = ComIdentity.Of(SafeObject(() => body.Owner));
        var objectOwner = ComIdentity.Of(SafeObject(() => model.Owner));
        return bodyOwner is null || objectOwner is null
            ? null
            : string.Equals(bodyOwner, objectOwner, StringComparison.Ordinal);
    }

    /// <summary>A refused bend or hole field as a contract error naming it — before any COM call.</summary>
    private static KompasContractException FeatureRefusal(SheetMetalFeaturePolicy.Refusal refusal, string consequence)
    {
        var reason = refusal switch
        {
            SheetMetalFeaturePolicy.Refusal.AngleNotPositive =>
                "угол сгиба обязан быть положительным: страница называет только тип double, а сгиб на "
                + "нулевой угол не строит ничего",
            SheetMetalFeaturePolicy.Refusal.RadiusNotPositive =>
                "радиус сгиба обязан быть положительным",
            SheetMetalFeaturePolicy.Refusal.UnknownLengthType =>
                "способ задания длины сгиба называется одним из значений ksBendLengthTypeEnum: "
                + string.Join(", ", SheetMetalFeaturePolicy.LengthTypes.Select(entry => entry.Name)),
            SheetMetalFeaturePolicy.Refusal.LengthNotPositive =>
                "длина прямой части сгиба обязана быть положительной",
            SheetMetalFeaturePolicy.Refusal.DiameterNotPositive =>
                "диаметр отверстия обязан быть положительным",
            SheetMetalFeaturePolicy.Refusal.UnknownCutType =>
                "тип построения отверстия называется by_width или by_depth (ksHoleCutTypeEnum); "
                + "up_to_surface не входит: глубина того типа задаётся объектом DepthObject",
            SheetMetalFeaturePolicy.Refusal.MissingDepth =>
                "тип by_depth требует depth_mm: страница делает Depth доступным только при нём",
            SheetMetalFeaturePolicy.Refusal.DepthWithoutItsCutType =>
                "depth_mm передаётся только с cut_type = by_depth: при другом типе ядро сохранит число и "
                + "не применит его",
            _ => "значение поля не является конечным числом",
        };

        return new KompasContractException(
            ErrorCodes.InvalidArgument,
            reason + ". Обращение к КОМПАС не выполнялось. " + consequence + ".",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = SheetMetalFeaturePolicy.RefusalField(refusal),
            });
    }

    private static string DescribeBend(SheetMetalBendDto? bend) => bend is null
        ? "не прочитано"
        : $"angle={Num(bend.AngleDeg)} direction={bend.Direction?.ToString() ?? "не прочитано"} "
          + $"internal_radius={bend.InternalRadius?.ToString() ?? "не прочитано"} radius={Num(bend.RadiusMm)} "
          + $"length_type={bend.LengthType ?? "не прочитано"} length={Num(bend.LengthMm)} "
          + $"edge_is_requested={bend.EdgeIsRequested?.ToString() ?? "не прочитано"}";

    private static string DescribeBendRequest(CreateSheetMetalBendCommand command) =>
        $"angle={Num(command.AngleDeg)} direction={command.Direction?.ToString() ?? "не задано"} "
        + $"internal_radius={command.InternalRadius?.ToString() ?? "не задано"} radius={Num(command.RadiusMm)} "
        + $"length_type={command.LengthType ?? "не задано"} length={Num(command.LengthMm)}";

    private static string DescribeHole(SheetMetalHoleDto? hole) => hole is null
        ? "не прочитано"
        : $"x={Num(hole.XMm)} y={Num(hole.YMm)} diameter={Num(hole.DiameterMm)} "
          + $"cut_type={hole.CutType ?? "не прочитано"} depth={Num(hole.DepthMm)}";

    private static string DescribeHoleRequest(CreateSheetMetalHoleCommand command) =>
        $"x={Num(command.XMm)} y={Num(command.YMm)} diameter={Num(command.DiameterMm)} "
        + $"cut_type={command.CutType ?? "не задано"} depth={Num(command.DepthMm)}";

    private static string DescribeCut(SheetMetalCutDto? cut) => cut is null
        ? "не прочитано"
        : $"cut_type={cut.CutType ?? "не прочитано"} depth={Num(cut.DepthMm)} ordinal={cut.Ordinal}";

    private static string DescribeCutRequest(CreateSheetMetalCutCommand command) =>
        $"cut_type={command.CutType} depth={Num(command.DepthMm)}";

    /// <summary>A refused request as a contract error naming the field — raised before any COM call.</summary>
    private static KompasContractException SheetMetalRefusal(SheetMetalBodyPolicy.Refusal refusal, string? unfoldType)
    {
        var field = SheetMetalBodyPolicy.RefusalField(refusal);
        var reason = refusal switch
        {
            SheetMetalBodyPolicy.Refusal.EmptySketch =>
                "эскиз пуст — в нём нет ни одного примитива, и листовое тело из него в файл не попадёт: "
                + "ядро на пустом эскизе отвечает счётчиком +1 и перечитанными свойствами, но после "
                + "сохранения и открытия тел в документе 0 (измерено)",
            SheetMetalBodyPolicy.Refusal.ThicknessNotPositive =>
                "толщина листового тела обязана быть положительной: ядро принимает 0 и отрицательное "
                + "значение, отвечает Update() = TRUE и оставляет тело с толщиной 0 (измерено)",
            SheetMetalBodyPolicy.Refusal.RadiusNotPositive =>
                "радиус сгиба обязан быть положительным: ядро принимает 0 и оставляет тело (измерено)",
            SheetMetalBodyPolicy.Refusal.UnknownDirection =>
                "направление называётся одним из значений ksDirectionTypeEnum, описанных для тела: "
                + string.Join(", ", SheetMetalBodyPolicy.Directions.Select(entry => entry.Name)),
            SheetMetalBodyPolicy.Refusal.UnknownUnfoldType =>
                "способ определения длины развёртки называется coefficient, bend_value или bend_reduction; "
                + "bend_table в объём не входит: ядро не принимает тип без таблицы сгибов, а формат файла "
                + "таблицы справка не описывает",
            SheetMetalBodyPolicy.Refusal.MissingUnfoldValue =>
                "способ развёртки требует своего поля: bend_coefficient для coefficient, bend_value для "
                + "bend_value, bend_reduction для bend_reduction",
            SheetMetalBodyPolicy.Refusal.TwoUnfoldValues =>
                "полей способа развёртки задано больше одного: способ выбирает ровно одно",
            SheetMetalBodyPolicy.Refusal.NoEditFields =>
                "правке нужно хотя бы одно меняемое поле: thickness_mm, bend_radius_mm, unfold_type или "
                + "поле способа развёртки",
            SheetMetalBodyPolicy.Refusal.FieldOfAnotherUnfoldType =>
                "поле принадлежит другому способу развёртки, чем " + (unfoldType ?? "текущий")
                + ": записанное число ядро сохранит и не применит",
            _ => "значение поля не является конечным числом",
        };

        return new KompasContractException(
            ErrorCodes.InvalidArgument,
            reason + ". Обращение к КОМПАС не выполнялось.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["field"] = field, ["unfold_type"] = unfoldType });
    }

    private static string DescribeBody(SheetMetalBodyDto? body) => body is null
        ? "не прочитано"
        : $"thickness={Num(body.ThicknessMm)} radius={Num(body.BendRadiusMm)} "
          + $"unfold={body.UnfoldType ?? "не прочитано"} value={Num(body.UnfoldValue)} "
          + $"extent={Num(body.ExtentByNormalMm)}";

    private static string DescribeRequest(CreateSheetMetalBodyCommand command) =>
        $"thickness={Num(command.ThicknessMm)} radius={Num(command.BendRadiusMm)} "
        + $"direction={command.Direction} unfold={command.UnfoldType} "
        + $"value={Num(SheetMetalBodyPolicy.SelectedField(command.UnfoldType, command.BendCoefficient, command.BendValue, command.BendReduction))}";

    private static string DescribeEdit(EditSheetMetalBodyCommand command, SheetMetalBodyDto? before) =>
        $"thickness={Num(command.ThicknessMm ?? before?.ThicknessMm)} "
        + $"radius={Num(command.BendRadiusMm ?? before?.BendRadiusMm)} "
        + $"unfold={command.UnfoldType ?? before?.UnfoldType ?? "не прочитано"} "
        + $"value={Num(SheetMetalBodyPolicy.SelectedField(command.UnfoldType ?? before?.UnfoldType, command.BendCoefficient, command.BendValue, command.BendReduction) ?? before?.UnfoldValue)}";

    /// <summary>Whether two readings of the same body hold the same numbers — the fact a repeat must show
    /// and an edit must contradict.</summary>
    private static bool SameValues(SheetMetalBodyDto? first, SheetMetalBodyDto? second)
    {
        if (first is null || second is null)
        {
            return false;
        }

        static bool Same(double? left, double? right) =>
            left is { } l && right is { } r && Math.Abs(l - r) <= SheetMetalBodyPolicy.Tolerance;

        return Same(first.ThicknessMm, second.ThicknessMm)
            && Same(first.BendRadiusMm, second.BendRadiusMm)
            && Same(first.UnfoldValue, second.UnfoldValue)
            && string.Equals(first.UnfoldType, second.UnfoldType, StringComparison.Ordinal);
    }

    private static int? ReadCount(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }

}

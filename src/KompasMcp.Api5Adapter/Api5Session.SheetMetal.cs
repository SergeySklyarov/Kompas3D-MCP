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
        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "sheet_metal.create_body");

        var bodiesAfter = ReadCount(() => sheet.SheetMetalBodies.Count);
        var read = ReadBody(document, created, ordinal: bodiesAfter is { } after ? after - 1 : null);
        var mismatched = SheetMetalBodyPolicy.CreateMismatches(
            read, command.ThicknessMm, command.BendRadiusMm, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);

        var counterGrew = bodiesBefore is { } beforeCount && bodiesAfter is { } afterCount
            && afterCount == beforeCount + 1;
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
                    ?? "габарит не прочитан",
                Expected: command.ThicknessMm.ToString("0.####", CultureInfo.InvariantCulture)),
        };

        var confirmed = counterGrew && mismatched.Count == 0 && extentMatches;
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

        var unverified = new List<string>
        {
            "direction_measured_once — из четырёх значений ksDirectionTypeEnum на листовом теле измерено "
            + "только normal; остальные приняты по странице перечисления, а не прогоном",
            "unfold_value_bounds_not_documented — страницы коэффициента, величины и уменьшения сгиба не "
            + "называют границ значения, поэтому проверяется только конечность числа",
            "body_not_deletable — созданное тело этим продуктом не удаляется: документированный "
            + "DeleteObject листовые объекты не удаляет",
        };

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
            new VerificationDto(VerificationLevel.CallReturned, checks, unverified));
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
        Api7Bridge.Rebuild(container, document.Document3D);
        BumpRevision(document, "sheet_metal.edit_body");

        var after = ReadBody(document, body, ordinal: null);
        var unchanged = SameValues(before, after);
        // A flat body's extent along the normal IS its thickness, so a thickness edit must move it. With
        // bends in the body the extent is no longer the thickness, and that is named, not assumed.
        var bends = ReadCount(() => sheet.SheetMetalBends.Count);
        var extentApplies = command.ThicknessMm is not null && bends == 0;
        var extentFollows = extentApplies && after?.ExtentByNormalMm is { } editedExtent
            && Math.Abs(editedExtent - command.ThicknessMm!.Value) <= SheetMetalBodyPolicy.Tolerance;
        var mismatched = SheetMetalBodyPolicy.EditMismatches(
            after, command.ThicknessMm, command.BendRadiusMm, command.UnfoldType,
            command.BendCoefficient, command.BendValue, command.BendReduction);

        // The extent is part of the edit's success only where it means something: a flat body, thickness
        // changed. Elsewhere it is named as not applicable instead of being quietly skipped.
        var failed = new List<string>(mismatched);
        if (extentApplies && !extentFollows)
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

        return new EditSheetMetalBodyResult(
            ToDto(stored, "sheet metal body (edit)"),
            before ?? new SheetMetalBodyDto(),
            after!,
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
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

    /// <summary>Extent along Z of the solid bodies only — for a flat sheet body this is the thickness.
    /// <c>LIMIT:</c> the normal is taken as Z, the arrangement the probe measured (sketch on XOY); a body
    /// built on another plane reports its Z extent, which is not its thickness.</summary>
    private static double? ExtentByNormal(DocumentEntry document) =>
        TryReadGabaritCorners(document, full: false, customizable: true, out var min, out var max)
            && min.Length >= 3 && max.Length >= 3
            ? max[2] - min[2]
            : null;

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

    /// <summary>A refused request as a contract error naming the field — raised before any COM call.</summary>
    private static KompasContractException SheetMetalRefusal(SheetMetalBodyPolicy.Refusal refusal, string? unfoldType)
    {
        var field = SheetMetalBodyPolicy.RefusalField(refusal);
        var reason = refusal switch
        {
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

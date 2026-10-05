using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>Rotation (docs/05 SM-03): feature creation via the API7 factory directly.</summary>
/// <remarks>
/// INVARIANT: the route rests on measurement, not on method names. Run
/// <c>95fa844107ce41609d6278f8f6c5759f</c> of 17.09.2026 (<c>docs/acceptance/api7/rotation.json</c>),
/// steps R.24/R.25/R.26, established the factory route and its shape.
/// MEASURED: 17.09.2026 — (R.24) all three factory operations (<c>o3d_baseRotated</c> 27,
/// <c>o3d_bossRotated</c> 28, <c>o3d_cutRotated</c> 29) build a body whose analytical volume is
/// 50265.4824574366 against π·r²·h = 50265.4824574367 at r=20, h=40; (R.25) the same result
/// reproduces from scratch on a fresh document, shape verified independently of volume (one
/// cylindrical face r=20 h=40, bounds 40×40×40), and the feature survives save → close → reopen;
/// (R.26) a cut removes 25132.7412287183 from a 120×120×40 plate, while a boss writing
/// <c>OperationResult = ksOperationCut</c> changes the volume by 0.
/// MEASURED: 18.09.2026 (FullTurnProbe F.1…F.5, independently re-read from the saved <c>.m3d</c> by
/// M3dVerificationProbe) — <c>Angle[true]</c> carries the requested angle directly (360→360°,
/// 180→180°, 90→90°); the second slot of the pair, equal to the first, doubles the sweep. A full
/// turn is built by a single call. This disproved the earlier "saturation at 180°" claim, which
/// came from a step that changed <c>CutOffByPoint</c> instead of the angle
/// (<c>docs/acceptance/api7/full-turn-findings.md</c>).
/// INVARIANT: no <c>NewEntity</c> and no <c>Create()</c> appear here. The old API5 shell around an
/// API7 factory object was a MIXED lifecycle — <c>Create()</c> returned <c>true</c>, the object
/// appeared in the tree and the volume did not change — not a property of rotation.
/// LIMIT: carried as refusals, not as caveats.
/// <list type="number">
/// <item>An angle beyond a full turn is not accepted: 360° is the sweep's own ceiling (measured
/// 18.09.2026), and a value above it is cut off BEFORE the mutation.</item>
/// <item><c>dtReverse</c> builds nothing (measured R.26.sector: <c>Update()</c> = False, 0 bodies);
/// rejected before the mutation.</item>
/// <item>The axis is mandatory; rotation without an axis is not built at all.</item>
/// <item>A thin wall is unmeasured: the route was measured on a SOLID body, so a requested thin
/// wall is refused with CAPABILITY_UNAVAILABLE rather than written as an unmeasured number.</item>
/// <item>Attachment to an existing body DOES happen (measured 18.09.2026, probe F.10: <c>Union</c>
/// fuses, <c>NewBody</c> adds a second body). The operation kind is derived from the factory kind:
/// base→<c>NewBody</c>, boss→<c>Union</c>, cut→<c>Cut</c>.</item>
/// <item>The target body is chosen BY GEOMETRY (probe F.11): only the INTERSECTED body is touched,
/// not the first in the collection (KOMPAS reorders bodies, so an index is not an address). The
/// declared <c>target_body_ref</c> is therefore verified AFTER the operation.</item>
/// </list>
/// History: docs/decisions/adapter-features.md#rotated-route
/// </remarks>
public partial class Api5Session
{
    /// <summary>Tolerance for matching a cylindrical face against the expected rotation radius, mm.</summary>
    private const double RotationRadiusToleranceMm = 0.01d;

    /// <summary>Rotation family name in server responses.</summary>
    private const string RotationFamily = "rotation";

    /// <summary>Tolerance for comparing the angle read back; it lives in the model in degrees.</summary>
    private const double RotationAngleToleranceDeg = 1e-6d;

    /// <summary>Rotation creation. A bridge refusal is returned as <c>CAPABILITY_UNAVAILABLE</c> with a
    /// reason rather than a silent null: the caller must tell "API7 unavailable" from "KOMPAS
    /// rejected the parameter".</summary>
    public RotatedResult Rotated(RotatedCommand command)
    {
        // ── before COM: validation rules whose cost of error is asymmetric ──────────────────────
        ValidateRotatedCommand(command);

        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var operationName = Api7Rotated.NameOf(command.Operation);

        var part = document.PartNow();
        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var bodiesBefore = CountBodies(document);

        // Named body snapshots — each body's volume and bounds. They let the answer to "which body
        // was touched" be a number, not a guess, and let the declared target_body_ref be verified
        // AFTER the operation. An index is not an address: KOMPAS reorders bodies (measured F.11,
        // [144000; 16000] → [16000; 181699.111843077]), so snapshots are matched by bounds centre,
        // not by position.
        var bodiesBeforeSnapshot = ReadBodySnapshots(part);
        var bodyTarget = command.TargetBodyRef is null
            ? null
            : ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBeforeSnapshot);

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Вращение создаётся фабрикой IModelContainer.Rotateds, а оболочка API5 " +
                "(NewEntity + Create) на этом объекте не работает — это измерено и было причиной " +
                "прежней блокировки. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        // ── multi-body: behaviour is MEASURED, so the former refusal is gone ─────────────────────
        //
        // MEASURED: 18.09.2026 (FullTurnProbe F.11) — in a two-body part, boss/Union (bodies 2→2,
        // volumes [144000; 16000] → [16000; 181699.111843077]) and cut/Cut (bodies 2→2, volumes
        // [144000; 16000] → [131433.629385641; 16000]) each touched EXACTLY ONE body — the
        // INTERSECTED one, not the first in the collection (KOMPAS swapped the bodies,
        // [144000; 16000] → [16000; …]).
        //
        // Hence the rule: the operation does NOT silently pick a body for the caller. Body counts
        // before/after and the named change are read and returned; a mismatch with the declared
        // target_body_ref is a refusal with partialEffects, not a silent "probably that one".
        // History: docs/decisions/adapter-features.md#rotated-multi-body

        // The profile is an API7 object. An un-transferred profile is a value API7 will not accept,
        // and it must not be substituted with "a sketch in general": for a rotation the profile is
        // the body being swept.
        var profile = bridge.TransferTo7(target.Sketch) as IModelObject;
        if (profile is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Эскиз-профиль не перенесён в API7 (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") + ") — вращение не создавалось.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        // ── axis: mandatory, and built in THIS very part ─────────────────────────────────────────
        //
        // The "points are distinct" check sits here, not only in validation: coincident points
        // would give a degenerate axis, and a rotation refusal on a degenerate axis would not be a
        // fact about rotation.
        var axisHandle = Api7Rotated.TryBuildAxisBy2Points(
            bridge, part, command.AxisPoint1Mm.ToArray(), command.AxisPoint2Mm.ToArray());
        if (axisHandle is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ось вращения не построена в собственной детали — признак не создавался. " +
                "Вращение БЕЗ оси не строится вовсе (измерено: Update()=False, тел 0), поэтому ось " +
                "здесь не улучшение, а условие постановки опыта.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        // ── creation ─────────────────────────────────────────────────────────────────────────────
        var (rotation, failure) = Api7Rotated.TryCreate(
            container,
            command.Operation,
            profile,
            axisHandle.Axis,
            command.AngleDeg,
            command.Direction,
            thin: false);

        if (rotation is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Вращение через фабрику API7 не создано: " + (failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = failure });
        }

        // Without a rebuild the API7 write stays a representation — measured by probe E on
        // IExtrusion.Sketch and repeated here: the order "write → Update() → Rebuild()" is part of
        // the contract, not a style.
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "rotated." + operationName);

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        var bodiesAfterSnapshot = ReadBodySnapshots(part);
        var bodyComparison = CompareBodySnapshots(bodiesBeforeSnapshot, bodiesAfterSnapshot);

        var count = Api7Rotated.Count(container);
        var readBack = count is int n and > 0 ? Api7Rotated.Read(container, n - 1) : null;

        // ── independent shape check, not the volume a second time ───────────────────────────────
        //
        // Volume does not tell a cylinder R20 H40 from a plate of the same volume. So the body's
        // cylindrical faces (radius and height via ksCylinderParam) and the bounding box are read
        // alongside: for a cylinder R20 H40 the box is 40×40×40, for a half-cylinder 40×40×20.
        var cylinders = SafeCylinders(part);
        var bounds = SafeBounds(part);

        var checks = new List<NamedCheck>
        {
            new("rotation_created", rotation is not null,
                Observed: operationName,
                Expected: "признак создан фабрикой Rotateds и перестроен"),
            new("parameters_read_back", RotatedParametersMatch(readBack, command),
                Observed: DescribeRotated(readBack),
                Expected: DescribeRotatedCommand(command)),
            new("body_count_change", bodiesAfter >= bodiesBefore,
                Observed: $"{bodiesBefore}→{bodiesAfter}",
                Expected: command.Operation == RotationOperation.Base
                    ? "первое тело появилось или осталось одно"
                    : "число тел не уменьшилось"),
        };

        // A cylindrical face MUST appear: sweeping a flat profile around an axis yields a surface
        // of revolution. Its absence is a direct contradiction of the geometry, even if the volume matched.
        checks.Add(new NamedCheck(
            "cylindrical_face_present",
            cylinders.Count > 0,
            Observed: cylinders.Count == 0
                ? "цилиндрических граней нет"
                : string.Join("; ", cylinders.Take(3).Select(c =>
                    $"r={Num(c.Radius)} h={Num(c.Height)}")),
            Expected: "хотя бы одна поверхность вращения"));

        // ── which body was touched: MEASURED, not chosen ────────────────────────────────────────
        //
        // For boss and cut the operation must land on an existing body, and "which one" is a question
        // answered here by measurement, not by an index. MEASURED (F.11) on a two-body part: the
        // INTERSECTED body was touched, not the first in the collection. So the check runs on snapshots.
        if (command.Operation != RotationOperation.Base)
        {
            // "Touched" means volume OR bounds changed: a body that merely moved is touched too, and
            // the "exactly one body" requirement concerns the body set, not the material. The material
            // requirement is checked by a SEPARATE line below (target_body_is_the_one_touched), so the
            // two must not be merged into one verdict — that is exactly the point of order §4.
            checks.Add(new NamedCheck(
                "body_target_measured",
                bodyComparison.Touched.Count == 1,
                Observed: bodyComparison.Touched.Count == 0
                    ? "ни одно тело не изменилось"
                    : "затронуто тел " + bodyComparison.Touched.Count + ": "
                        + string.Join(" | ", bodyComparison.Rows),
                Expected: "ровно одно тело изменилось — то, которое пересекает инструмент"));
        }

        // The declared body is verified AFTER the operation: the API has no route that assigns a
        // target body to a rotation — neither IRotated nor IRotated1 declares chooseType or
        // ChooseBodies (checked against the interop assembly; they exist only on the API5 definitions
        // ksBossRotatedDefinition and ksCutRotatedDefinition). So the target cannot be substituted;
        // what can be done is to verify the kernel touched it. A mismatch is a refusal with
        // partialEffects, not silent consent.
        if (bodyTarget is not null)
        {
            var targetDelta = bodyComparison.DeltaOf(bodyTarget.Index);
            var violations = bodyComparison.UnchangedViolations(bodyTarget.Index);
            var targetMoved = targetDelta is double moved && Math.Abs(moved) > VolumeChangeFloorMm3;
            checks.Add(new NamedCheck(
                "target_body_is_the_one_touched",
                targetMoved && violations.Count == 0,
                Observed: $"тел{bodyTarget.Index}: ΔV={Num(targetDelta)}; изменения посторонних тел: "
                    + (violations.Count == 0 ? "нет" : string.Join("; ", violations)),
                Expected: "изменилось ровно объявленное тело, посторонние не тронуты"));

            if (!targetMoved || violations.Count > 0)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Операция тронула не то тело, которое объявлено в target_body_ref: " +
                    $"тел{bodyTarget.Index} ΔV={Num(targetDelta)}, " +
                    (violations.Count == 0 ? "посторонние тела не тронуты" : string.Join("; ", violations)) +
                    ". Назначить целевое тело вращению нельзя — IRotated/IRotated1 не объявляют " +
                    "селектора тела (измерено по интероп-сборке); ядро относит операцию к " +
                    "пересекаемому телу (измерено F.11). Признак СОЗДАН и перестроен, поэтому " +
                    "эффект частичный, а не нулевой.",
                    RetryPolicy.Never,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = command.TargetBodyRef,
                        ["target_body_index"] = bodyTarget.Index,
                        ["target_body_delta_mm3"] = targetDelta,
                        ["other_body_changes"] = violations,
                        ["bodies_before"] = bodiesBefore,
                        ["bodies_after"] = bodiesAfter,
                        ["code"] = "rotation_target_body_not_the_one_touched",
                    });
            }
        }

        // The sign is what distinguishes a boss from a cut, and it is checked separately from the
        // magnitude. For boss it is COMPUTED from a pair of measurements, not postulated: a boss must
        // add material, a cut must remove it.
        var isCut = command.Operation == RotationOperation.Cut;
        var isBase = command.Operation == RotationOperation.Base;
        var change = volumeAfter is double a && volumeBefore is double b ? a - b : (double?)null;
        if (!isBase)
        {
            var signOk = change is not null && (isCut ? change.Value < 0 : change.Value > 0);
            checks.Add(new NamedCheck(
                "material_sign",
                signOk,
                Observed: change is null
                    ? "не измерено"
                    : change.Value < 0 ? $"снято {Num(-change.Value)}" : change.Value > 0
                        ? $"добавлено {Num(change.Value)}"
                        : "объём не изменился",
                Expected: isCut ? "материал снят (Cut)" : "материал добавлен (Boss)"));
        }

        bool? numericMatch = null;
        if (command.ExpectedVolumeMm3 is double expected)
        {
            numericMatch = volumeAfter is double measured
                && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
            checks.Add(new NamedCheck(
                "volume_expected",
                numericMatch.Value,
                Observed: Num(volumeAfter),
                Expected: Num(expected)));
        }

        // Geometry is confirmed when the parameters read back, the surface of revolution actually
        // appeared, the material sign is right and — if the caller supplied an expectation — the volume
        // matched. Anything less is enough for call_returned, but not for geometry_checked.
        var geometryConfirmed = readBack is not null
            && checks.Exists(c => c.Name == "parameters_read_back" && c.Passed)
            && checks.Exists(c => c.Name == "cylindrical_face_present" && c.Passed)
            && checks.TrueForAll(c => c.Name != "material_sign" || c.Passed)
            && numericMatch is not false;

        var unverified = new List<string>();
        if (!geometryConfirmed)
        {
            unverified.Add(
                "geometry_not_confirmed — КОМПАС принял запись, но измерение не подтвердило " +
                "ожидаемую геометрию");
        }

        if (command.ExpectedVolumeMm3 is null)
        {
            unverified.Add(
                "expected_volume_not_supplied — аналитическое ожидание объёма не задавал вызывающий, " +
                "численного доказательства нет");
        }

        // The former angle_saturates_at_180 entry ("the sweep is linear up to 180° and stops growing")
        // was REMOVED 18.09.2026 — see History. What follows is only what really remained unverified
        // in THIS call.
        // History: docs/decisions/adapter-features.md#angle-saturation-refuted

        if (command.Operation != RotationOperation.Base)
        {
            unverified.Add(
                "target_body_not_assignable — целевое тело вращению НАЗНАЧИТЬ нельзя: IRotated и " +
                "IRotated1 не объявляют ни chooseType, ни ChooseBodies (проверено по интероп-сборке; " +
                "они есть только у API5-определений ksBossRotatedDefinition и ksCutRotatedDefinition, " +
                "а те не собирают признак). Измерено (F.11) на детали из двух тел, что ядро относит " +
                "операцию к ПЕРЕСЕКАЕМОМУ телу, а не к первому в коллекции, и что тронуто ровно одно " +
                "тело. Ответ несёт поимённое изменение тел; объявленный target_body_ref проверяется " +
                "ПОСЛЕ операции, и несовпадение даёт отказ с partialEffects");
        }

        if (command.Direction != RotationDirection.Normal)
        {
            unverified.Add(
                "direction_side_unverified — смена направления на полуобороте объём НЕ меняет " +
                "(полуцилиндр одинаков с обеих сторон) и габарит тоже симметричен, поэтому " +
                "«сектор переехал» этим вызовом не доказывается. Измерено R.26.sector: " +
                "dtMiddlePlane — единственное направление, двигающее сектор; dtReverse не строит ничего");
        }

        // ── feature reference ───────────────────────────────────────────────────────────────────
        var reference = FindRotatedEntity(document);
        if (reference is null)
        {
            // A reference to a feature the API5 tree does not show must not be issued: an edit through
            // it would fail anyway, and the caller would learn about it later.
            //
            // The level is NOT lowered to call_returned here. The missing reference and the confirmed
            // geometry are two independent claims, and the first does not weaken the second: the volume
            // matched the analytical one, the parameters were re-read, the surface of revolution was
            // found — all of that is measured and stays measured regardless of whether the feature is
            // visible in the API5 tree. The first revision always returned CallReturned here, and that
            // was a genuine acceptance defect: line RO.4 failed with "level=call_returned" on a call
            // where ALL five checks passed, including the numeric volume match. The tree number for a
            // rotation was never measured (unlike the 52→583 pair for a hole), so non-addressability
            // here is the expected state, not a sign of failed geometry.
            // History: docs/decisions/adapter-features.md#feature-ref-withheld
            unverified.Add("feature_ref_withheld — признак не найден в дереве API5, ссылка не выдана");
            return new RotatedResult(
                null,
                operationName,
                readBack?.AngleDeg,
                readBack?.Direction,
                readBack?.AxisState,
                bodiesAfter,
                volumeAfter,
                bounds,
                new VerificationDto(
                    geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                    checks,
                    unverified),
                axisHandle.Notes);
        }

        return new RotatedResult(
            ToDto(References.Register("feature", document.Id, document.Revision, reference),
                $"rotated {operationName} {command.AngleDeg:0.###}°"),
            operationName,
            readBack?.AngleDeg,
            readBack?.Direction,
            readBack?.AxisState,
            bodiesAfter,
            volumeAfter,
            bounds,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            axisHandle.Notes);
    }

    /// <summary>Read the parameters of an EXISTING rotation feature for <c>kompas_get_feature</c>.</summary>
    /// <remarks>
    /// <b>The index is taken by matching the same feature, not by taking the first one.</b> A rotation
    /// has no API5 definition by which the feature could be recognised, so matching runs on
    /// COMPOSITION: a tree entity cast to <c>IRotated</c> is searched among the
    /// <c>IModelContainer.Rotateds</c> elements by a match of the angle. The angle as an identity token
    /// is weak (two half-turns around different axes would match), so with several candidates
    /// <c>null</c> is returned — "not read", not "here is the first": passing a foreign parameter off
    /// as the addressed feature's parameter would be lying about the model.
    /// NOTE: the code compares the angle ONLY; the direction is not part of the match (the earlier
    /// wording said "angle AND direction", which the code does not do — corrected 05.10.2026).
    /// Reading is NOT mutation: <c>BeginEdit</c>/<c>EndEdit</c>/<c>Update</c> are not called, the
    /// revision is not bumped.
    /// </remarks>
    private RotatedDto? ReadRotatedFeature(DocumentEntry document, ksEntity entity)
    {
        try
        {
            var part = document.PartNow();
            var bridge = BridgeFor(document);
            var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
            if (container is null || Api7Rotated.Count(container) is not int count || count <= 0)
            {
                return null;
            }

            // The addressed feature's own angle is read through the API5 entity: it is the very object
            // the reference was issued for. Comparing it with the API7 candidates is the matching.
            var entityAngle = entity is IRotated rotatedDirect
                ? SafeReadAngle(rotatedDirect, true)
                : null;

            // When the tree entity does not answer to IRotated (a raw __ComObject — MEASURED
            // 18.09.2026), there is nothing to match by angle. Then addressing goes BY ORDINAL: the
            // feature holds its position among the rotations in the API5 tree, and the same position in
            // the API7 Rotateds collection. The order here is a measured property, not a guess: both
            // the tree and the collection enumerate features in creation order.
            var entityOrdinal = entityAngle is null ? RotatedOrdinal(part, entity) : null;
            if (entityAngle is null && entityOrdinal is not int)
            {
                return null;
            }

            if (entityOrdinal is int ordinal)
            {
                return ordinal >= 0 && ordinal < count ? Api7Rotated.Read(container, ordinal) : null;
            }

            var matches = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var candidate = Api7Rotated.Read(container, i);
                if (candidate is null)
                {
                    continue;
                }

                if (entityAngle is double want
                    && candidate.AngleDeg is double got
                    && Math.Abs(got - want) > RotationAngleToleranceDeg)
                {
                    continue;
                }

                if (entityAngle is null)
                {
                    // The addressed feature's angle was not read — there is nothing to match, so a
                    // single candidate is accepted and several are rejected.
                    matches.Add(i);
                    continue;
                }

                matches.Add(i);
            }

            return matches.Count == 1 ? Api7Rotated.Read(container, matches[0]) : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The position of a rotation feature among ALL API5 tree rotations, in creation order. <c>null</c>
    /// if the addressed feature is not found in the tree.</summary>
    /// <remarks>Needed because an entity read from the tree does not answer to <c>QI(IRotated)</c>, while the
    /// API7 <c>Rotateds</c> collection is indexed in creation order. The position is a DETERMINISTIC
    /// address: it does not depend on whether the angle is readable and does not confuse two features
    /// with the same angle (which matching by angle cannot do by construction).</remarks>
    private static int? RotatedOrdinal(ksPart part, ksEntity target)
    {
        try
        {
            var kinds = new[]
            {
                KompasObjectTypes.Of(KompasObjectTypes.OperationElement),
                (short)-1,
            };
            foreach (var kind in kinds)
            {
                if (part.EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                var ordinal = 0;
                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is not ksEntity candidate
                        || !IsRotatedEntity(candidate))
                    {
                        continue;
                    }

                    if (ReferenceEquals(candidate, target))
                    {
                        return ordinal;
                    }

                    ordinal++;
                }

                if (ordinal > 0)
                {
                    // Rotations were found, but not among them — there must be no second pass over
                    // another type: that would mean the feature lies in a different collection.
                    return null;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Edit the angle of an EXISTING rotation feature via <c>kompas_update_feature</c>.</summary>
    /// <remarks>
    /// <b>MEASURED: 18.09.2026</b> (probe <c>FullTurnProbe</c>, step <c>F.2</c>): on a single feature
    /// changing the angle 360 → 180 → 360 gave volumes
    /// <c>50265.4824574366 → 25132.7412287183 → 50265.4824574366</c> with bounds
    /// <c>z[−20,20] → z[−0,20] → z[−20,20]</c> — a geometric change, not merely a written number. The
    /// order "write angle → <c>Update()</c> → rebuild" is part of the contract, as on creation:
    /// without <c>Update()</c> the setter returns success while the model stays as it was.
    /// ONLY the angle is written (and the direction, if given). The profile and axis of an existing
    /// rotation are not changed by this call: those routes were not measured on a rotation, and
    /// accepting <c>sketch_ref</c> would promise a re-binding that does not exist.
    /// </remarks>
    private UpdateFeatureResult UpdateRotated(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.AngleDeg is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Признак — вращение: angle_deg к нему не применяется (это поле угла ФАСКИ). Угол " +
                "развёртки задаётся полем rotation_angle_deg — одно имя для двух семейств сделало " +
                "бы ответ неоднозначным.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = RotationFamily });
        }

        if (command.DepthMm is not null || command.EndCondition is not null || command.SketchRef is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Признак — вращение: depth_mm, end_condition и sketch_ref к нему не применяются. " +
                "Правка вращения — это rotation_angle_deg и rotation_direction; перепривязка профиля " +
                "и оси существующего вращения не измерялась и не выполняется.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = RotationFamily });
        }

        if (command.Distance1Mm is not null || command.Distance2Mm is not null
            || command.Direction is not null || command.RadiusMm is not null
            || command.EdgeRefs is not null || command.BaseObjectRefs is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Признак — вращение: параметры фаски (distance1_mm, distance2_mm, angle_deg, " +
                "direction) и скругления (radius_mm, edge_refs, base_object_refs) к нему не " +
                "применяются.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = RotationFamily });
        }

        // B5 queue fields (kinematics, sections, shell) are foreign to this family too. They are
        // rejected HERE, not "left to reach their own branch": the B5 branch is chosen by the field
        // itself, and without this check a call with both rotation_angle_deg and shift_mode would go to
        // kinematics, where rotation_angle_deg is simply not read — i.e. it would be accepted and
        // ignored. `couplings` was added 20.09.2026 in the same way as in SolidOps.cs: the B5 field
        // list was one field short, so a rotation edit carrying couplings was accepted while the chains
        // were not applied.
        // History: docs/decisions/adapter-features.md#foreign-field-list
        if (command.ShiftMode is not null || command.SectionRefs is not null
            || command.Couplings is not null
            || command.ThicknessMm is not null || command.ThinInward is not null
            || command.FaceRefs is not null
            // HOLE family fields (order SM07 §3.2) are foreign to rotation too. Added in the same way
            // as couplings on 20.09.2026: the foreign-field list must receive every new contract field,
            // otherwise the field is accepted and not applied.
            || command.DiameterMm is not null || command.CounterboreDiameterMm is not null
            || command.CounterboreDepthMm is not null || command.CountersinkDiameterMm is not null
            || command.CountersinkAngleDeg is not null || command.ExpectedVolumeDeltaMm3 is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Признак — вращение: параметры кинематической операции (shift_mode), элемента по " +
                "сечениям (section_refs, couplings), оболочки (thickness_mm, thin_inward, face_refs) " +
                "и отверстия (diameter_mm и поля его режимов, expected_volume_delta_mm3) к нему не " +
                "применяются.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = RotationFamily });
        }

        var part = document.PartNow();
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // The address is known in advance when the feature answers to QI(IRotated): then matching runs
        // on composition. If it does not (a raw __ComObject from the tree — MEASURED 18.09.2026), the
        // address is taken by position among the rotations, and FindIndexFor accepts it as knownIndex.
        var ordinal = entity is IRotated ? null : RotatedOrdinal(part, entity);
        var index = Api7Rotated.FindIndexFor(container, entity, ordinal);
        if (index is not int found)
        {
            var why = ordinal is null
                ? "сопоставление идёт по составу (угол и направление), а не по имени и не по индексу: " +
                  "совпадений 0 или больше одного"
                : $"позиция признака среди вращений дерева — {ordinal}, элементов в коллекции API7 — " +
                  $"{Api7Rotated.Count(container)}: адрес за пределами коллекции";
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак вращения не сопоставлен с элементом коллекции API7 Rotateds: " + why +
                ". Правка не выполняется, потому что записать угол в чужой признак значило бы " +
                "изменить не тот объект. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["code"] = "rotation_feature_not_matched" });
        }

        var readBefore = Api7Rotated.Read(container, found);
        var write = Api7Rotated.TryWriteAngleAndDirection(
            container, found, command.RotationAngleDeg, command.RotationDirection);
        if (!write.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Угол вращения не записан: " + (write.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = write.Failure });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "rotated.update");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var readBack = Api7Rotated.Read(container, found);
        var stateAfter = ReadFeatureState(entity);

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved",
                string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal),
                Observed: stateAfter.Name, Expected: stateBefore.Name),
        };

        if (command.RotationAngleDeg is double wanted)
        {
            checks.Add(new NamedCheck(
                "angle_read_back",
                readBack?.AngleDeg is double got && Math.Abs(got - wanted) <= RotationAngleToleranceDeg,
                Observed: Num(readBack?.AngleDeg), Expected: Num(wanted)));
        }

        var matched = command.ExpectedVolumeMm3 is double expected
            && volumeAfter is double measured
            && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
        if (command.ExpectedVolumeMm3 is double exp)
        {
            checks.Add(new NamedCheck("volume_expected", matched, Observed: Num(volumeAfter), Expected: Num(exp)));
        }

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is null)
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма правка не " +
                           "может быть подтверждена геометрически");
        }
        unverified.Add("dependent_features_not_enumerated — сохранность зависимых признаков здесь не " +
                       "проверяется; для этого существует приёмочная строка G03");

        var geometryConfirmed = command.ExpectedVolumeMm3 is not null && matched
            && checks.TrueForAll(c => c.Name != "angle_read_back" || c.Passed);

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            RotationFamily,
            string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal),
            featuresAfter,
            volumeBefore,
            volumeAfter,
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>Read one slot of the <c>IRotated.Angle</c> pair without failing the read.</summary>
    private static double? SafeReadAngle(IRotated rotation, bool normal)
    {
        try
        {
            return rotation.Angle[normal];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>"Field ↔ capability" rules. All of them reject the call BEFORE any COM access, and the cost of
    /// an error here is asymmetric: a spurious refusal is seen at once, whereas an accepted-and-ignored
    /// number survives until acceptance, looking like a performed operation.</summary>
    private static void ValidateRotatedCommand(RotatedCommand command)
    {
        if (command.AngleDeg <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угол вращения обязан быть строго положительным: нулевой угол не строит ничего, а " +
                "признак при этом создаётся — то есть успех был бы выдан за пустую операцию.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["angle_deg"] = command.AngleDeg });
        }

        // The former "no more than 180" limit was REMOVED 18.09.2026 — see History. The ceiling here is
        // the sweep itself: a full turn is 360°, and a sector cannot exceed it. The error class is kept
        // as a number in details, but this is now a refusal on the merits, not on the old wrong limit.
        // History: docs/decisions/adapter-features.md#angle-saturation-refuted
        if (command.AngleDeg > 360d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Угол {command.AngleDeg}° не принимается: развёртка вращения не может превысить " +
                "полный оборот, 360°. Это верхняя граница сектора, а не прежнее (опровергнутое) " +
                "насыщение на 180°: полный оборот строится одним вызовом, угол задаётся в градусах " +
                "и равен построенному. Запросите не больше 360°.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["angle_deg"] = command.AngleDeg,
                    ["measured_limit_deg"] = 360d,
                    ["code"] = "rotation_angle_exceeds_full_turn",
                });
        }

        // MEASURED (R.26.sector): dtReverse builds nothing — Update()=False, 0 bodies. This is a fact
        // about the enum value, so reporting "built" from a return code is not allowed here.
        if (command.Direction == RotationDirection.Reverse)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Направление reverse не поддержано: измерено (R.26.sector), что при нём вращение " +
                "не строится вовсе — Update() возвращает false и тел остаётся 0. Доступны normal, " +
                "both и middle_plane; сектор двигает только middle_plane.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["direction"] = "reverse",
                    ["code"] = "rotation_direction_builds_nothing",
                });
        }

        if (command.AxisPoint1Mm.Count != 3 || command.AxisPoint2Mm.Count != 3)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Точка оси задаётся тремя координатами модели (x, y, z). Принятая пара из двух чисел " +
                "дала бы точку в начале координат, и ось встала бы не туда.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["point1_length"] = command.AxisPoint1Mm.Count,
                    ["point2_length"] = command.AxisPoint2Mm.Count,
                });
        }

        var distinct = Math.Abs(command.AxisPoint1Mm[0] - command.AxisPoint2Mm[0]) > 1e-9
            || Math.Abs(command.AxisPoint1Mm[1] - command.AxisPoint2Mm[1]) > 1e-9
            || Math.Abs(command.AxisPoint1Mm[2] - command.AxisPoint2Mm[2]) > 1e-9;
        if (!distinct)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Точки оси совпадают: вырожденная ось не задаёт развёртки. Отказ вращения на такой " +
                "оси не был бы фактом о вращении, поэтому он сделан здесь.",
                RetryPolicy.Never);
        }

        // The route was measured on a SOLID body (IThinParameters.Thin = false). Writing an unmeasured
        // number into a feature would pass off an unverified configuration as a verified one.
        if (command.ThinWallMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Тонкая стенка вращения не поддержана: маршрут измерен на СПЛОШНОМ теле " +
                "(IThinParameters.Thin = false, шаги R.24/R.25). Ни один прогон не измерял тонкую " +
                "стенку вращения, поэтому число не записывается. Признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["thin_wall_mm"] = command.ThinWallMm,
                    ["code"] = "rotation_thin_wall_unmeasured",
                });
        }

        // The target body is ACCEPTED but not assigned: a rotation has no body selector in the API
        // (see the comment in Rotated()), so the reference is verified AFTER the operation against the
        // named body snapshots. Rejecting it would deny the caller the only way to express intent,
        // while accepting and not checking it would report that the choice was honoured.
    }
    /// <summary>Whether the read-back parameters match the requested ones. Extracted so the read and
    /// the expectation are described in the report the same way.</summary>
    private static bool RotatedParametersMatch(RotatedDto? readBack, RotatedCommand command)
    {
        if (readBack is null)
        {
            return false;
        }

        // The angle is compared directly with the written one: MEASURED (F.1…F.5) that the model
        // returns exactly the angle it was built with, up to 360°. The former "saturation at 180°"
        // caveat was a READING BUG: half of the pair was read, not the angle. The tolerance stays
        // strict — a discrepancy here would mean something other than requested was built.
        var angleOk = readBack.AngleDeg is double angle
            && Math.Abs(angle - command.AngleDeg) <= RotationAngleToleranceDeg;

        var axisOk = string.Equals(readBack.AxisState, "есть", StringComparison.Ordinal);
        var directionOk = readBack.Direction is not null
            && Enum.TryParse<ksDirectionTypeEnum>(readBack.Direction, out var read)
            && read == Api7Rotated.DirectionOf(command.Direction);

        return angleOk && axisOk && directionOk;
    }

    private static string DescribeRotated(RotatedDto? readBack) => readBack is null
        ? "не прочитано"
        : $"угол={Num(readBack.AngleDeg)}°, направление={readBack.Direction ?? "не прочитано"}, " +
          $"ось={readBack.AxisState ?? "не прочитано"}, тип={readBack.OperationType ?? "не прочитано"}";

    private static string DescribeRotatedCommand(RotatedCommand command) =>
        $"угол={Num(command.AngleDeg)}°, направление={command.Direction}, ось=есть";

    /// <summary>Cylindrical faces of the main body: radius and height from <c>ksCylinderParam</c>. An empty
    /// list and "not read" are distinguishable by the caller via the check, not conflated.</summary>
    private static List<CylinderReadout> SafeCylinders(ksPart part)
    {
        var found = new List<CylinderReadout>();
        try
        {
            if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
            {
                return found;
            }

            for (var f = 0; f < faces.GetCount(); f++)
            {
                if (faces.GetByIndex(f) is not ksFaceDefinition face)
                {
                    continue;
                }

                try
                {
                    if (face.GetSurface() is not ksSurface surface || !surface.IsCylinder()
                        || surface.GetSurfaceParam() is not ksCylinderParam cylinder)
                    {
                        continue;
                    }

                    found.Add(new CylinderReadout(cylinder.radius, cylinder.height));
                }
                catch (Exception ex) when (ex is COMException)
                {
                    // A face whose parameters KOMPAS did not return is no reason to abandon the walk.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // An empty list is the honest answer "could not read"; the check below compares it with zero.
        }

        return found;
    }

    /// <summary>Bounding box of the main body: for a cylinder R20 H40 it is 40×40×40, for a half-cylinder
    /// 40×40×20. This is a volume-independent shape check, so the box is read, not substituted with
    /// zero.</summary>
    /// <remarks>
    /// <c>null</c> means "not read" and is NOT equal to a zero box. The difference matters: a zero box
    /// compared against the expected bounds would look like a geometry mismatch, i.e. a failed read
    /// would turn into a claim about the model. This is the same distinction as with
    /// <c>MeasureVolume</c> and <c>BoundingBoxDto.Empty</c>: <c>GetGabarit</c> returning <c>false</c>
    /// or throwing is a fact about the READ, not about the body.
    /// </remarks>
    private static BoundingBoxDto? SafeBounds(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return null;
            }

            // ksBody.GetGabarit, not GetBoundingBoxEx: the body has the latter not at all (CS1061),
            // and it is GetGabarit that ReadBodyBox reads in Api5Session.Geometry.cs.
            return body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? new BoundingBoxDto(new[] { x1, y1, z1 }, new[] { x2, y2, z2 })
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The last API5 tree element, which is the created rotation feature.</summary>
    /// <remarks>
    /// <b>Why by numbers first, then by enumeration.</b> For a hole, the "feature was never found"
    /// defect cost a separate investigation: the adapter searched for 52 (the factory number
    /// <c>o3d_holeOperation</c>) instead of 583 (the tree number <c>o3d_Hole3D</c>) measured by probe
    /// N.1. For rotation the measurement was done by acceptance SM-03: line RO.10t printed the tree
    /// types after a rotation cut — <c>['25', '29']</c>, i.e. the cut is visible as <b>29</b>, the same
    /// number it was created with by the factory. So all three kinds (27/28/29) are checked, not one
    /// number: the client chooses the operation kind, and searching only for <c>cut</c> would miss
    /// <c>base</c>.
    /// <b>Why enumeration cannot return a foreign feature.</b> Selection runs not by name and not by
    /// index but by the entity having a profile and an axis (<c>IRotated</c> answers the cast). A name
    /// here is not an identifier — measured on a chamfer (F.8: <c>f-ch2</c> was replaced by the
    /// localised feature name) — and the "last element" index without such a check would point at
    /// anything if the operation failed. The absence of a meaningful candidate yields <c>null</c>, and
    /// the caller gets <c>feature_ref_withheld</c> instead of a reference an edit would not have worked
    /// through anyway.
    /// </remarks>
    private static ksEntity? FindRotatedEntity(DocumentEntry document)
    {
        try
        {
            var part = document.PartNow();
            var kinds = new[]
            {
                KompasObjectTypes.Of(KompasObjectTypes.OperationElement),
                (short)-1,
            };
            var rotatedTypes = new[]
            {
                KompasObjectTypes.BaseRotated,
                KompasObjectTypes.BossRotated,
                KompasObjectTypes.CutRotated,
            };

            // 1) By the measured numbers — a narrow check, not a guess.
            ksEntity? byType = null;
            foreach (var kind in kinds)
            {
                if (part.EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity
                        && rotatedTypes.Contains(entity.type))
                    {
                        byType = entity;
                    }
                }
            }

            if (byType is not null)
            {
                return byType;
            }

            // 2) The number did not confirm — the feature is searched by its nature, not by a number.
            // This is a fallback for the case where the tree numbering turns out different on another
            // build. On a tree object it usually finds nothing (QI on a __ComObject refuses, and
            // IsRotatedEntity falls back to the number here), so whatever is found "by nature" is
            // taken as the ONLY candidate, not as "the last matching one".
            foreach (var kind in kinds)
            {
                if (part.EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                ksEntity? candidate = null;
                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity && IsRotatedEntity(entity))
                    {
                        candidate = entity;
                    }
                }

                if (candidate is not null)
                {
                    return candidate;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this is a rotation feature. The primary route is the measured tree number: 27
    /// (<c>o3d_baseRotated</c>), 28 (<c>o3d_bossRotated</c>), 29 (<c>o3d_cutRotated</c>). This is the
    /// same technique that recognises a native hole (<c>entity.type == 583</c>, MEASURED by probe
    /// N.1), and it works on a feature READ FROM THE TREE. The fallback route is the answer to
    /// <c>QI(IRotated)</c>, needed for an entity READ FROM THE REFERENCE REGISTRY.
    /// </summary>
    /// <remarks>
    /// Why QI alone is not enough — MEASURED at acceptance SM-03 on 18.09.2026. An entity taken from
    /// the tree via <c>EntityCollection</c> arrives as a raw <c>__ComObject</c>, and the check
    /// <c>entity is IRotated</c> answers <c>false</c> on it, even though <c>entity.type</c> is 29 and
    /// the feature reads from the model. The first revision relied on QI alone and thus silently lost
    /// the feature: the branch name ("by QI") was passed off as the recognition result.
    /// The type number is the route for a FEATURE FROM THE TREE, not an identifier: it changes between
    /// creation time and the tree (for an extrusion 24 → 25, MEASURED P2.3), so recognition must use
    /// the SET of numbers of the three rotation kinds, not a single number remembered at creation.
    /// <b>The second route (QI) is unreachable on a tree object, and that is not an excuse but a
    /// measured boundary.</b> The question "does a <c>ksEntity</c> from the tree answer to
    /// QI(IRotated)" was tested on 18.09.2026 in three ways: an <c>is</c> cast, a runtime-type cast to
    /// the interface, and a direct call of the <c>Angle</c> member with interception — all three
    /// REFUSED on a feature that demonstrably reads from the model (angle 360, type 29, live
    /// UpdateStamp). So the QI branch is kept here as a fallback for a payload from the reference
    /// registry, but the RELIANCE is on the tree number: otherwise recognition would fall back to a
    /// means the tree object does not answer.
    /// </remarks>
    private static bool IsRotatedEntity(ksEntity entity)
    {
        try
        {
            if (entity.type is KompasObjectTypes.BaseRotated
                or KompasObjectTypes.BossRotated
                or KompasObjectTypes.CutRotated)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The number was not read — the second route remains, and it decides.
        }

        try
        {
            return entity is IRotated;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Rotation readout: radius and height of a cylindrical face.</summary>
    private sealed record CylinderReadout(double Radius, double Height);

    private static string Num(double? value) =>
        value?.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture)
        ?? "не прочитано";
}

/// <summary>Result of creating a rotation.</summary>
/// <param name="FeatureRef">Reference to the feature; null when the API5 tree does not show it.</param>
/// <param name="Operation">Kind of the performed operation as the contract word (base/boss/cut).</param>
/// <param name="AngleReadBackDeg">Angle READ from the model, not the one written.</param>
/// <param name="DirectionReadBack">Direction read from the model.</param>
/// <param name="AxisState">Axis state as the contract word (present / absent / unread).</param>
/// <param name="AxisNotes">Axis build trace: route notes, including the Valid ≠ True case.</param>
public sealed record RotatedResult(
    ReferenceDto? FeatureRef,
    string Operation,
    double? AngleReadBackDeg,
    string? DirectionReadBack,
    string? AxisState,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    VerificationDto Verification,
    IReadOnlyList<string> AxisNotes);

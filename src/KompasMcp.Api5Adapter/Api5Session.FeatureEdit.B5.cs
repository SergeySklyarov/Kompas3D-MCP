using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>Editing the three B5 families — sweep, loft and shell — via <c>kompas_update_feature</c>.</summary>
/// <remarks>INVARIANT: the edit exists rather than "delete and re-create" (order B5 §11) — re-creation yields a
/// different tree feature, name, position and dependency set; each family has a measured "write to the same
/// feature → <c>Update()</c> → rebuild" route, and the edit is confirmed by GEOMETRY, not by <c>Update()</c>.
/// MEASURED (probe <c>--b5</c>, report <c>docs/acceptance/api7/b5-sweep-loft-shell.json</c>, steps B5.13–B5.15,
/// details in history). LIMIT: <c>closed</c> of a loft is not declared editable; sweep inputs (<c>sketch_ref</c>,
/// path) are rejected by name because the route measurably does not apply. INVARIANT: fields of other families are rejected, not ignored — an accepted-and-ignored number survives to acceptance looking like a completed edit.
/// History: docs/decisions/adapter-solid.md#b5-edit</remarks>
public partial class Api5Session
{
    /// <summary>An edit field, its owning family and how to read the value from the command.</summary>
    private sealed record EditField(string Name, string Family, Func<UpdateFeatureCommand, object?> Read);

    /// <summary>Fields belonging to the B5 families. One table for two questions — "who owns it" and
    /// "what is in it": the two answers have no way to diverge.</summary>
    private static readonly EditField[] B5EditFields =
    {
        new("shift_mode", EvolutionFamily, c => c.ShiftMode),
        new("section_refs", LoftFamily, c => c.SectionRefs),
        new("couplings", LoftFamily, c => c.Couplings),
        new("thickness_mm", ShellFamily, c => c.ThicknessMm),
        new("thin_inward", ShellFamily, c => c.ThinInward),
        new("face_refs", ShellFamily, c => c.FaceRefs),
    };

    /// <summary>Edit fields NOT belonging to B5: extrusion, chamfer, fillet, rotation, the B3 families
    /// and pattern. Listed explicitly and completely — that is how they are rejected.</summary>
    /// <remarks>INVARIANT: a list of what exists, not a "forbidden list" — it changes with the contract,
    /// and the default must be "do not reject": a field assigned to no family is rejected by its own
    /// family, not here. The difference shows in the cost of error: a field missed from the list is
    /// accepted and not applied — exactly the defect class the "declared but swallowed" rule (§9.1 P4)
    /// was written for.</remarks>
    private static readonly EditField[] NonB5EditFields =
    {
        new("depth_mm", "extrusion", c => c.DepthMm),
        new("end_condition", "extrusion", c => c.EndCondition),
        new("sketch_ref", "extrusion", c => c.SketchRef),
        new("distance1_mm", "chamfer", c => c.Distance1Mm),
        new("distance2_mm", "chamfer", c => c.Distance2Mm),
        new("angle_deg", "chamfer", c => c.AngleDeg),
        new("direction", "chamfer", c => c.Direction),
        new("radius_mm", "fillet", c => c.RadiusMm),
        new("edge_refs", "fillet", c => c.EdgeRefs),
        new("base_object_refs", "fillet", c => c.BaseObjectRefs),
        new("rotation_angle_deg", "rotation", c => c.RotationAngleDeg),
        new("rotation_direction", "rotation", c => c.RotationDirection),
        new("reposition_kind", "reposition", c => c.RepositionKind),
        new("reposition_vector_mm", "reposition", c => c.RepositionVectorMm),
        new("reposition_axis_point_mm", "reposition", c => c.RepositionAxisPointMm),
        new("reposition_axis_direction_mm", "reposition", c => c.RepositionAxisDirectionMm),
        new("reposition_axis_point2_mm", "reposition", c => c.RepositionAxisPoint2Mm),
        new("reposition_angle_deg", "reposition", c => c.RepositionAngleDeg),
        new("plane", "split/cut_by_plane", c => c.Plane),
        new("keep_side", "cut_by_plane", c => c.KeepSide),
        new("target_body_ref", "cut_by_plane", c => c.TargetBodyRef),
        new("expected_part_volumes_mm3", "split", c => c.ExpectedPartVolumesMm3),
        new("operation", "boolean", c => c.Operation),
        new("pattern", "pattern", c => c.Pattern),
        // Fields of the HOLE family (order SM07 §3.2). Added 20.09.2026 together with the hole edit
        // itself: before it these fields did not exist in the contract at all, and with their appearance
        // the foreign-field list had to receive them — otherwise a "shift_mode + diameter_mm" call on a
        // sweep would be accepted, the mode applied, and the diameter swallowed.
        new("diameter_mm", "hole", c => c.DiameterMm),
        new("counterbore_diameter_mm", "hole", c => c.CounterboreDiameterMm),
        new("counterbore_depth_mm", "hole", c => c.CounterboreDepthMm),
        new("countersink_diameter_mm", "hole", c => c.CountersinkDiameterMm),
        new("countersink_angle_deg", "hole", c => c.CountersinkAngleDeg),
        new("expected_volume_delta_mm3", "hole", c => c.ExpectedVolumeDeltaMm3),
    };

    /// <summary>Reject fields not belonging to this B5 family. Rejected BEFORE any COM call.</summary>
    private static void RejectForeignB5EditFields(UpdateFeatureCommand command, string family, string allowed)
    {
        var foreign = NonB5EditFields
            .Where(f => f.Read(command) is not null)
            .Select(f => f.Name)
            .Concat(B5EditFields
                .Where(f => !string.Equals(f.Family, family, StringComparison.Ordinal)
                            && f.Read(command) is not null)
                .Select(f => f.Name))
            .ToList();

        if (foreign.Count == 0)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Признак — {family}: к нему применяются только {allowed}. Переданные поля других "
            + "семейств не игнорируются, а отвергаются: принятое и проигнорированное число доживает "
            + "до приёмки, выглядя как выполненная правка.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["family"] = family,
                ["foreign_fields"] = foreign,
            });
    }

    // ══════════════════════════════════════════════════════════ sweep ══

    /// <summary>Edit the section-shift mode of an EXISTING sweep.</summary>
    /// <remarks>MEASURED 20.09.2026 (probe <c>--b5</c>, step B5.13): on ONE feature, changing <c>sketchShiftType</c>
    /// <c>orthogonal → parallel → orthogonal</c> gave volumes <c>24674.011002723353 → 15707.963267948984 → 24674.011002723353</c>; the setup discriminates only on an ARC (on a straight path both modes give one body, B5.2, B5.8).
    /// INVARIANT: the order "write → <c>Update()</c> → rebuild" is part of the contract — the <c>Update() = true</c>
    /// answer is not proof; the volume is read from the model and compared with the caller's analytical expectation.
    /// LIMIT: step B5.15 — <c>SetSketch</c> and retargeting <c>PathPartArray()</c> are accepted (<c>Update = true</c>) and NOT applied (the volume stays), while changing the MODE on the same feature does change it; <c>sketch_ref</c> is therefore rejected by name.
    /// History: docs/decisions/adapter-solid.md#b5-sweep-edit</remarks>
    private UpdateFeatureResult UpdateSweepFeature(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignB5EditFields(command, EvolutionFamily, "shift_mode (режим движения сечения)");

        if (command.ShiftMode is not SweepShiftMode mode)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "У кинематической операции меняется только режим движения сечения — shift_mode. "
                + "Профиль и траектория существующего признака не меняются: маршрут измеренно не "
                + "применяется (шаг B5.15), поэтому sketch_ref отвергается, а не игнорируется.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = EvolutionFamily });
        }

        if (command.SketchRef is not null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "sketch_ref к кинематической операции не применяется. Измерено 20.09.2026 (шаг "
                + "B5.15) с положительным контролем на том же признаке: SetSketch принимается "
                + "(Update = true), а объём не меняется — 24674.011002723353 вместо ожидаемых "
                + "6168.502750680849 при подстановке профиля Ø10. Смена РЕЖИМА на том же признаке "
                + "объём меняет, то есть прибор изменение видит и отсутствие изменения тоже. "
                + "Изменить профиль можно только пересозданием признака.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["measured_volume_unchanged_mm3"] = 24674.011002723353d,
                    ["measured_expected_if_applied_mm3"] = 6168.502750680849d,
                    ["measured"] = "docs/acceptance/api7/b5-sweep-loft-shell.json, шаг B5.15",
                });
        }

        var definition = DefinitionOf(entity);
        if (definition is null || !IsEvolutionDefinition(definition))
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак не отвечает ни ksBaseEvolutionDefinition, ни ksBossEvolutionDefinition: "
                + "записать режим движения сечения некуда. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "sweep_definition_not_recognized",
                    ["tree_type"] = entity.type,
                });
        }

        var before = ReadSweepFeature(definition);
        var target = ShiftValue(mode);

        if (!WriteSweepShiftMode(definition, target))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "sketchShiftType не записан: определение не отвечает ни одному из двух интерфейсов "
                + "кинематической операции. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["shift_mode"] = target });
        }

        var updated = SafeBool(entity.Update) == true;
        var part = document.PartNow();
        part.RebuildModel();
        document.Document.RebuildDocument();
        BumpRevision(document, "sweep.update");

        // Re-read FROM THE MODEL: the definition is taken from the feature afresh, not the requested
        // value retold.
        var after = ReadSweepFeature(DefinitionOf(entity) ?? definition);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                          && featuresBefore == featuresAfter;

        var checks = new List<NamedCheck>
        {
            new("update_accepted", updated, "ksEntity.Update() вернул true"),
            new("feature_identity_preserved", sameFeature,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            // Case-INSENSITIVE comparison — a corrected defect, not style: the model returns the mode
            // name in lower case ("parallel") while `mode.ToString()` gives the enum member name
            // ("Parallel"), so an ordinal comparison declared the check FAILED on a correctly read mode.
            // MEASURED 20.09.2026 at the B5 acceptance: rows B5K.01/B5K.02 passed by their rule while
            // the product's answer carried `shift_mode_read_back: false` with `observed = parallel` and
            // `expected = Parallel` — the product declared itself faulty, and only a reader of FIELDS
            // rather than the verdict saw it.
            new("shift_mode_read_back",
                string.Equals(after?.ShiftMode, mode.ToString(), StringComparison.OrdinalIgnoreCase),
                Observed: after?.ShiftMode ?? "не прочитано",
                Expected: mode.ToString()),
            new("profile_preserved", after?.SectionCount == before?.SectionCount,
                Observed: "профилей " + (after?.SectionCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"),
                Expected: "профилей " + (before?.SectionCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")),
            new("path_preserved", after?.PathPartCount == before?.PathPartCount,
                Observed: "частей траектории " + (after?.PathPartCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"),
                Expected: "частей траектории " + (before?.PathPartCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")),
        };

        var unverified = new List<string>
        {
            "profile_and_path_not_editable — профиль и траектория существующей кинематической операции "
            + "этим вызовом не меняются: измерено, что SetSketch определения API5 и перепривязка "
            + "PathPartArray() принимаются и не применяются (B5.15), и то же самое подтверждено на "
            + "ВТОРОМ хранилище — запись документированного свойства API7 IEvolution.Sketch принята, "
            + "Update() = true, тело не изменилось, запись в оба хранилища подряд тоже не применяется, "
            + "и порядок записи исхода не меняет (B5.22). Поля для них поэтому не объявлены",
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не "
            + "проверяется; для этого существует отдельная приёмочная строка",
        };

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var matches = volumeAfter is { } value
                          && Math.Abs(value - expected) <= VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(volumeAfter) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма "
                + "геометрия правки не подтверждена числом, и уровень честно остаётся call_returned");
        }

        if (mode == SweepShiftMode.Orthogonal || mode == SweepShiftMode.Parallel)
        {
            unverified.Add("shift_mode_not_distinguishable_on_straight_path — на ПРЯМОЙ траектории "
                + "параллельный и ортогональный режимы дают одно тело (измерено: на дуге R50/90° они "
                + "различаются на 8966.047734774369 мм³). Проверять этот режим следует на дуге");
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            EvolutionFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            // A sweep has no depth: the field belongs to extrusion, so null.
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>Write the section-shift mode into the definition that answered one of the two interfaces.
    /// The branch is mandatory, not convenient: the two definitions share no interface for this member,
    /// and it is MEASURED that a feature created by <c>NewEntity(45)</c> answers
    /// <c>ksBossEvolutionDefinition</c>.</summary>
    private static bool WriteSweepShiftMode(object definition, short value)
    {
        try
        {
            if (definition is ksBaseEvolutionDefinition baseEvolution)
            {
                baseEvolution.sketchShiftType = value;
                return true;
            }

            if (definition is ksBossEvolutionDefinition bossEvolution)
            {
                bossEvolution.sketchShiftType = value;
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════ loft ══

    /// <summary>Edit the section set of an EXISTING loft.</summary>
    /// <remarks>MEASURED (step B5.13): the INPUT is edited — retargeting <c>ILoft.Sketchs</c> on an already-built
    /// feature changes the geometry: <c>40×40 + 20×20</c> give <c>28000</c>, <c>40×40 + 40×40</c> give the prism
    /// <c>h/3·(A₁ + A₂ + √(A₁A₂)) = 10·(1600+1600+1600) = 48000</c>, returning to the previous set returns <c>28000</c>.
    /// LIMIT: closedness (<c>closed</c>) is not declared editable — writing <c>ILoft.Closed</c> returns <c>Update() = True</c>, reads back <c>False</c>,
    /// the volume stays <c>28000</c>; closedness is set only at creation (<c>kompas_loft.closed</c>).
    /// INVARIANT: the address is by order among same-family features — stable (both tree and API7 enumerate in creation order; a name will not do: different types share one display name), but "tree position = collection index" is proved only with an EQUAL element count, else the call is rejected by name.
    /// History: docs/decisions/adapter-solid.md#b5-loft-edit</remarks>
    private UpdateFeatureResult UpdateLoftFeature(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignB5EditFields(command, LoftFamily,
            "section_refs (набор сечений в порядке соединения), couplings (цепочки соответствия)");

        if (command.SectionRefs is not { Count: > 0 } requested)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "У элемента по сечениям меняется только набор сечений — section_refs. Замкнутость "
                + "(closed) правимым параметром не объявлена: измерено (B5.13), что её запись на "
                + "построенном признаке принимается (Update = true) и не применяется — читается "
                + "обратно false, объём не меняется. Замкнутость задаётся при создании.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = LoftFamily });
        }

        ValidateLoftSectionRefs(requested);

        var container = TryContainerFor(document);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (BridgeFor(document).BridgeFailure ?? "причина не известна")
                + ". Сечения существующего признака живут только в API7 (в API5 цепочек соответствия "
                + "нет вовсе), поэтому правка не выполняется; признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var part = document.PartNow();
        var ordinal = OrdinalAmong(part, entity, IsLoftEntity);
        var collectionCount = Api7Loft.Count(container);
        var treeCount = CountAmong(part, IsLoftEntity);

        if (ordinal is not int index || collectionCount is not int inCollection || treeCount is not int inTree)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак не сопоставлен с элементом коллекции ILofts: позиция среди односемейных "
                + (ordinal is null ? "не найдена" : ordinal.Value.ToString(CultureInfo.InvariantCulture))
                + ", элементов в дереве " + (treeCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")
                + ", в коллекции " + (collectionCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")
                + ". Правка не выполняется, потому что записать сечения в чужой признак значило бы "
                + "изменить не тот объект.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["code"] = "loft_not_matched" });
        }

        // "Tree position = collection index" is proved only with an equal element count. A divergence
        // means the address is unproven, and the edit is rejected by name.
        if (inTree != inCollection)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Соответствие дерева и коллекции ILofts не доказано: признаков по сечениям в дереве "
                + inTree + ", элементов в коллекции " + inCollection + ". Порядок среди односемейных "
                + "служит адресом только при равном числе, иначе правка попала бы в другой признак. "
                + "Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "loft_ordinal_mapping_unproven",
                    ["tree_count"] = inTree,
                    ["collection_count"] = inCollection,
                });
        }

        if (index < 0 || index >= inCollection)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Позиция признака среди односемейных — " + index + ", элементов в коллекции ILofts — "
                + inCollection + ": адрес за пределами коллекции. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["code"] = "loft_ordinal_out_of_range" });
        }

        var loft = Api7Loft.Read(container, index);
        if (loft is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Элемент коллекции ILofts по индексу " + index + " не отдал ILoft: писать сечения "
                + "некуда. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["code"] = "loft_not_read" });
        }

        var bridge = BridgeFor(document);
        var transferred = new List<object>(requested.Count);
        var editTargets = new List<SketchTarget>(requested.Count);
        var transferTrace = new List<string>(requested.Count);
        // Fresh section addresses — those PROVEN by the tree. Needed separately from `transferred`: the
        // API5 definition is written with an address (ksEntity), the ILoft with the transferred API7
        // object.
        var freshEntities = new List<ksEntity>(requested.Count);
        foreach (var sectionRef in requested)
        {
            var target = RequireSketch(sectionRef);
            if (!string.Equals(target.Document.Id, document.Id, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Сечение '{sectionRef}' принадлежит документу {target.Document.Id}, а элемент по "
                    + $"сечениям правится в {document.Id}. Сечения из чужой детали не переносятся.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["section_document_id"] = target.Document.Id,
                        ["loft_document_id"] = document.Id,
                    });
            }

            // The sketch is re-addressed FROM THE TREE at edit time: a registry pointer is not accepted
            // in ILoft.Sketchs (MEASURED, row B5S.01 — see ReAddressFromTree). A refusal here is NAMED:
            // the address is unproven, and an edit by an unproven address would change the wrong object —
            // which is indistinguishable from "the same one" by volume.
            var fresh = ReAddressFromTree(part, target.Sketch, out var readdressNote);
            if (fresh is null)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"Сечение '{sectionRef}' не переадресовано с дерева: {readdressNote}. Признак "
                    + "не изменён.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["named_code"] = "section_not_re_addressed",
                        ["readdress_note"] = readdressNote,
                    });
            }

            if (bridge.TransferTo7(fresh) is not { } transferred7)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Сечение '{sectionRef}' не перенесено в API7: непереданное сечение API7 не "
                    + "увидит, и правка по неполному набору изменила бы признак не так, как просили. "
                    + "Признак не изменён.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?> { ["code"] = "section_not_transferred" });
            }

            transferred.Add(transferred7);
            editTargets.Add(target);
            freshEntities.Add(fresh);

            // WHAT WAS TRANSFERRED is published per section. Without this "the retarget did not apply"
            // is indistinguishable from "it applied to another entity": the section count is the same in
            // both outcomes, and the name is the only thing that tells them apart.
            var freshName = ReadEntityName(fresh);
            transferTrace.Add(
                $"'{ReadEntityName(target.Sketch)}' → свежее имя '{freshName}' [{readdressNote}]"
                + (string.Equals(freshName, ReadEntityName(target.Sketch), StringComparison.Ordinal)
                    ? string.Empty
                    : " — ИМЯ РАЗОШЛОСЬ С ЗАПРОШЕННЫМ"));
        }

        // ── Section-plane parallelism: the same duty as at creation (§9.2) ──
        // An edit by a section set on non-parallel planes would describe a different body just as at
        // creation, so the refusal must stand here BEFORE the write, not after it.
        var editPlaneAxes = ReadSectionPlaneAxes(editTargets);
        if (editPlaneAxes.DistinctAxes.Count > 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскости сечений не параллельны: оси нормалей " +
                string.Join(", ", editPlaneAxes.DistinctAxes.Select(AxisName)) +
                ". Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "non_parallel_section_planes",
                    ["axes"] = editPlaneAxes.DistinctAxes.Select(AxisName).ToArray(),
                    ["sections_read"] = editPlaneAxes.Read.Count,
                    ["sections_unreadable"] = editPlaneAxes.Unreadable,
                });
        }

        // ── Coupling chains: READ and decide first, then write ──
        // MEASURED 20.09.2026 (probe --b5, step B5.19): assigning ILoft.Sketchs RESETS the chains —
        // CouplingsCount read 1, and after re-assigning the SAME section set became 0, with the volume
        // returning from 20000 to 28000 (automatic coupling). The check must therefore stand BEFORE the
        // section write: after it, "the feature carried chains" would be indistinguishable from "it did
        // not", and a silent loss of coupling would look like a normal pass.
        var couplingsBefore = Api7Loft.CouplingsCount(loft);

        // ── NON-EMPTY CHAINS ON AN EXISTING FEATURE ARE REJECTED BEFORE THE WRITE ──
        // MEASURED 20.09.2026 (B5 acceptance, rows B5S.01/B5S.02): on a BUILT feature ILoft.AddCoupling()
        // returns ICoupling, PositionOffset accepts offsets, CouplingsCount reads 1 RIGHT AFTER the write —
        // but the build does not carry the chain (after Update() the model has 0 chains, volume matches a body
        // WITHOUT coupling). Reproduced in TWO write orders, so it is not our order. At CREATION the same
        // sequence keeps the chain (B5.18: CouplingsCount = 1, volume 20000 vs 28000). The documented members
        // declare no such limit — this is a MEASUREMENT, named and not silenced. The refusal stands BEFORE
        // the write; the feature is unchanged.
        if (command.Couplings is { Count: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Цепочки соответствия задаются при СОЗДАНИИ признака. На существующем признаке "
                + "ILoft.AddCoupling() регистрирует цепочку (CouplingsCount читается 1 сразу после "
                + "записи, до построения), но построение её не переносит: в модели 0 цепочек, и тело "
                + "соответствует автоматическому соответствию. Измерено 20.09.2026 на приёмке B5 в "
                + "двух порядках записи, поэтому непустой couplings отвергнут ДО записи — иначе "
                + "«выполнено» было бы выдано за тело, которого нет. Признак не изменён. "
                + "Пустой список означает «без цепочек» и выполним: он снимает соответствие.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "couplings_not_applied_on_existing_feature",
                    ["couplings_requested"] = command.Couplings.Count,
                    ["measured_at"] = "B5S.01/B5S.02, приёмка B5, 20.09.2026",
                    ["creation_control"] = "проба B5.18: при создании цепочка сохраняется "
                        + "(CouplingsCount = 1, объём 20000 против 28000)",
                    ["documented_pages"] = new[]
                    {
                        "iloft_addcoupling.html", "iloft_clearcouplings.html", "iloft_deletecoupling.html",
                    },
                });
        }

        if (command.Couplings is null && couplingsBefore is > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Признак несёт цепочек соответствия: " + couplingsBefore +
                ", а правка меняет набор сечений и цепочки НЕ называет. Цепочка описывает "
                + "соответствие точек конкретного набора сечений (ICoupling.Count — «количество "
                + "сечений в цепочке»), а замена набора сбрасывает её молча. Передайте couplings — "
                + "но учтите, что НЕПУСТОЙ список на существующем признаке отвергается отдельно "
                + "(цепочка построением не переносится — измерено), поэтому выполним только пустой "
                + "список, означающий «без цепочек». Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "couplings_must_be_restated",
                    ["couplings_in_model"] = couplingsBefore,
                    ["sections_requested"] = requested.Count,
                });
        }

        // ── WRITING THE SECTION SET: INTO BOTH INPUT STORES — THE API5 DEFINITION AND ILoft ──
        // The "section set" input lives in TWO places: the API5 definition (ksBase/BossLoftDefinition.Sketchs()
        // → ksEntityCollection) and the API7 object (ILoft.Sketchs). MEASURED 20.09.2026 (probe --b5, step
        // B5.21, a three-section feature reduced to two): while NOBODY HAS READ the definition, writing to
        // ILoft.Sketchs applies (48000); after ONE read of ksBaseLoftDefinition.Sketchs() the definition
        // becomes OWNER of the COUNT and the same write is cancelled by the first update (16114.2858257129);
        // writing INTO BOTH STORES lifts the cancellation (48000). Dropping the definition read would drop
        // kompas_get_feature, whose section refs come exactly from there. The former revision wrote only to ILoft.
        var definitionBeforeWrite = DefinitionOf(entity);
        var definitionWrite = WriteLoftSectionsToDefinition(definitionBeforeWrite, freshEntities);
        var sectionsInDefinitionAfterWrite = definitionBeforeWrite is { } definitionWritten
            ? LoftSectionCount5(definitionWritten)
            : null;

        var sectionsBefore = Api7Loft.SectionCount(loft);
        if (!WriteLoftSections(loft, transferred.ToArray()))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись ILoft.Sketchs не состоялась: признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        // READ RIGHT AFTER THE WRITE, BEFORE Update() — different claims: "the write was accepted" and
        // "the write landed". Without this read, "the write did not land" and "the write landed but the
        // rebuild did not take it" are indistinguishable, and they are treated differently.
        var sectionsAfterWrite = Api7Loft.SectionCount(loft);

        if (command.Couplings is not null)
        {
            ValidateLoftCouplings(command.Couplings, requested.Count);
            if (!WriteLoftCouplings(loft, command.Couplings, out var couplingFailure))
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Цепочки соответствия не заменены: " + couplingFailure + ". Тело по другому "
                    + "соответствию было бы другим телом, поэтому построение не выполнялось; "
                    + "признак изменён частично — набор сечений записан, цепочки прежние.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["named_code"] = "GEOMETRY_FAILED",
                        ["couplings_requested"] = command.Couplings.Count,
                    });
            }
        }

        // APPLYING THE INPUT — ksEntity.Update(). RATIONALE CORRECTED 20.09.2026: the former revision blamed
        // the call itself ("ILoft.Update() = true, and after it ILoft holds 3 again"). That is FALSE. MEASURED
        // (probe --b5, step B5.21, three-section feature reduced to two): while NOBODY HAS READ the definition,
        // both ILoft.Update() and ksEntity.Update() apply (48000); after ONE read of
        // ksBaseLoftDefinition.Sketchs() the definition owns the COUNT and the SAME write is cancelled by the
        // first update (whatever it is) — not the update cancelled, but the two input stores disagreed; writing
        // INTO BOTH STORES (WriteLoftSectionsToDefinition) lifts the cancellation. ksEntity.Update() therefore
        // applies, but what matters is that BOTH stores are written; dropping the definition read would drop kompas_get_feature.
        var updated = SafeBool(entity.Update) == true;
        var sectionsAfterEntityUpdate = Api7Loft.SectionCount(loft);
        if (!updated)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksEntity.Update() вернул false: новый набор сечений не построен. Признак изменён "
                + "частично — вход записан, тело прежнее.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        // REBUILD: API5 FIRST, THEN API7, and both numbers are published. Probe B5.13 after the edit
        // called ksPart.RebuildModel() + ksDocument3D.RebuildDocument() — an API5 REBUILD — while this
        // route once limited itself to part7.RebuildModel(true). Which of the two rebuilds carries the
        // written input into the body is distinguishable only by reading between them, so it is read
        // after each. MEASURED 20.09.2026 (step B5.21): both rebuilds give ONE body, i.e. the
        // APPLICATION (Update()) carries the input, not either rebuild; the earlier read "28000 instead
        // of 37000" is explained by the store inconsistency, not by the rebuild order.
        part.RebuildModel();
        var sectionsAfterApi5Rebuild = Api7Loft.SectionCount(loft);
        document.Document.RebuildDocument();
        var sectionsAfterDocumentRebuild = Api7Loft.SectionCount(loft);
        var volumeAfterApi5Rebuild = ReadVolume(document);

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "loft.update");
        // Sections are read by TWO routes, and BOTH numbers are published: in ILoft (where it was
        // written) and in the feature definition (from where kompas_get_feature reads them). A divergence
        // of the two reads must be visible, not hidden by choosing the convenient one; the row judges by
        // the place written to.
        var sectionsAfterApi7 = Api7Loft.SectionCount(loft);
        var sectionsAfterDefinition = DefinitionOf(entity) is { } definitionAfter
            ? LoftSectionCount5(definitionAfter)
            : null;
        var sectionsAfter = sectionsAfterApi7;
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                          && featuresBefore == featuresAfter;

        var checks = new List<NamedCheck>
        {
            new("update_accepted", updated, "ksEntity.Update() вернул true"),
            new("feature_identity_preserved", sameFeature,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            // INVARIANT: the write must be an ACTION, not a report about it — "the assignment passed"
            // and "the section set in the operation became different" are different claims, and a READ
            // sits between them.
            new("sections_write_reaches_operation", sectionsAfterWrite == requested.Count,
                Observed: "до записи " + Num(sectionsBefore) + ", после присваивания ILoft.Sketchs "
                    + Num(sectionsAfterWrite) + ", после ksEntity.Update() " + Num(sectionsAfterEntityUpdate)
                    + ", после RebuildModel() " + Num(sectionsAfterApi5Rebuild)
                    + ", после RebuildDocument() " + Num(sectionsAfterDocumentRebuild)
                    + ", в конце " + Num(sectionsAfter),
                Expected: requested.Count.ToString(CultureInfo.InvariantCulture)),
            // WHAT WAS TRANSFERRED — per section. The section count is the same whether "the requested
            // entity was transferred" or "another entity with the same name was", so the identification
            // must stand BESIDE the number, not instead of it.
            new("sections_transferred_named", transferTrace.Count == requested.Count,
                Observed: string.Join("; ", transferTrace),
                Expected: "по каждому сечению: имя запрошенного = имя свежего, коллекция названа"),
            // INVARIANT: the input is written into BOTH STORES, and both reads are published — without
            // this "the write was cancelled by the update" is indistinguishable from "the write did not
            // reach the owner", and they are treated differently.
            new("sections_written_to_definition", definitionWrite.Ok,
                Observed: definitionWrite.Note + "; сечений в определении после записи "
                    + Num(sectionsInDefinitionAfterWrite),
                Expected: requested.Count.ToString(CultureInfo.InvariantCulture)),
            new("sections_read_back", sectionsAfter == requested.Count,
                Observed: (sectionsAfterApi7?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")
                    + " (API7 ILoft — куда записано), "
                    + (sectionsAfterDefinition?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")
                    + " (определение признака)",
                Expected: requested.Count.ToString(CultureInfo.InvariantCulture)),
            // A REBUILD NEED NOT CHANGE THE BODY. A divergence of the two numbers means one of the
            // rebuilds carries the written input into the body and the other does not, and then "the edit
            // was applied" is passing a rebuild off as the edit.
            new("body_stable_across_rebuild",
                volumeAfterApi5Rebuild is null || volumeAfter is null
                || Math.Abs(volumeAfterApi5Rebuild.Value - volumeAfter.Value)
                   <= VolumeToleranceMm3(volumeAfter.Value),
                Observed: "после пересборки API5: " + Num(volumeAfterApi5Rebuild)
                    + ", после пересборки API7: " + Num(volumeAfter),
                Expected: "обе пересборки дают одно тело"),
        };

        if (command.Couplings is { } requestedCouplings)
        {
            // The MODEL is read: how many chains and what offsets stand on each section. A chain count
            // without content would prove only that the object exists.
            var couplingsInModel = ReadCouplingContent(loft);
            var chainsInModel = couplingsInModel?.Count;
            checks.Add(new NamedCheck("coupling_chains_replaced", chainsInModel == requestedCouplings.Count,
                Observed: "цепочек в модели: "
                    + (chainsInModel?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"),
                Expected: "запрошено " + requestedCouplings.Count.ToString(CultureInfo.InvariantCulture)));

            var offsets = CouplingOffsetsMatch(couplingsInModel, requestedCouplings);
            checks.Add(new NamedCheck("coupling_points_read_back", offsets.Ok,
                Observed: offsets.Observed,
                Expected: offsets.Expected));
        }

        var unverified = new List<string>
        {
            "closed_not_editable — замкнутость существующего признака этим вызовом не меняется: "
            + "измерено (B5.13), что запись ILoft.Closed принимается (Update = true) и не применяется",
            "section_order_not_distinguishable_by_volume — «порядок соблюдён» объёмом не "
            + "подтверждается: концентрические параллельные сечения дают одно тело в любом порядке. "
            + "Требуется различающая постановка (разная форма сечений, габарит, число граней)",
            "coupling_not_settable_on_existing_feature — соответствие существующего признака этим "
            + "маршрутом не задаётся: непустой couplings отвергается по имени "
            + "(couplings_not_applied_on_existing_feature). Измерено 20.09.2026 на двухсекционном "
            + "признаке: AddCoupling() регистрирует цепочку (CouplingsCount читается 1 до построения), "
            + "а построение её не несёт — в модели 0 цепочек. Ограничение измерено на этой "
            + "конфигурации; если на другой соответствие переносится построением, оно снимается "
            + "новым измерением, а не предположением",
            "section_planes_parallelism_not_checked — параллельность плоскостей сечений не "
            + "проверяется этой редакцией; по наряду это обязанность проверяющей стороны",
            "coupling_effect_not_explained_analytically — что содержимое цепочки применяется, "
            + "измерено числом (смещение точки второго сечения на 25 % контура: 28000 → 20000), но "
            + "аналитического ожидания для тела со сдвинутым соответствием наряд не даёт",
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };

        if (command.Couplings is null)
        {
            unverified.Add("couplings_not_restated — цепочки соответствия этим вызовом не заменялись: "
                + "их отсутствие подтверждено только тем, что признак их не нёс (иначе вызов был бы "
                + "отвергнут по имени)");
        }

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var matches = volumeAfter is { } value
                          && Math.Abs(value - expected) <= VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(volumeAfter) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма "
                + "геометрия правки не подтверждена числом, и уровень честно остаётся call_returned");
        }

        if (sectionsBefore is not null && sectionsBefore != sectionsAfter)
        {
            checks.Add(new NamedCheck("section_count_changed", true,
                Observed: sectionsBefore + " → " + (sectionsAfter?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"),
                Expected: "набор сечений заменён целиком"));
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            LoftFamily,
            sameFeature,
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

    /// <summary>Write the section set into <c>ILoft.Sketchs</c> — a SAFEARRAY of IDispatch pointers.</summary>
    /// <remarks>INVARIANT: the write route is the very object the feature was created by — <c>IModelContainer.Lofts.Add(o3d_bossLoft)</c>,
    /// so <c>ILoft</c> owns the feature; probe B5.13 by this route got 48000 on an ALREADY BUILT feature (retargeting S5+S6 → S5+S7).
    /// INVARIANT: writing through the definition collection is MANDATORY — one read of the definition makes IT the owner of the
    /// section COUNT, and a write only to <c>ILoft</c> is then cancelled by the first update; both stores are written, definition
    /// first, then <c>ILoft.Sketchs</c> (see <see cref="WriteLoftSectionsToDefinition"/>).
    /// LIMIT: this write does NOT check that a section stands ABOVE the feature in the tree — MEASURED: a sketch created AFTER the feature is not taken in (write accepted, <c>Update()</c> = true, 2 sections read, body unchanged); "above" is tree order, not a request field.
    /// History: docs/decisions/adapter-solid.md#b5-loft-both-stores</remarks>
    private static bool WriteLoftSections(ILoft loft, object[] sections)
    {
        try
        {
            loft.Sketchs = sections;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Writing the section set INTO THE API5 DEFINITION — the SECOND store of the same input.</summary>
    /// <remarks>INVARIANT: why, if the write goes into <c>ILoft</c> — MEASURED 20.09.2026 (probe <c>--b5</c>, step B5.21):
    /// a three-section feature is reduced to two by a write to <c>ILoft.Sketchs</c>, repeating while nobody has read the definition
    /// ("3 before, 2 after assignment, 2 after <c>Update()</c>, volume 48000"). But ONE read of <c>ksBaseLoftDefinition.Sketchs()</c>
    /// (the member <see cref="LoftSectionRefs"/> and hence <c>kompas_get_feature</c> reads through) makes the definition OWNER of the
    /// section COUNT, and the next SAME write is cancelled ("2 after assignment, 3 after <c>Update()</c>, volume 16114.2858257129");
    /// writing INTO BOTH STORES (definition first, then <c>ILoft.Sketchs</c>, then <c>Update()</c>) applies again (48000). Dropping the
    /// read would drop <c>kompas_get_feature</c>. History: docs/decisions/adapter-solid.md#b5-loft-both-stores</remarks>
    private static (bool Ok, string Note) WriteLoftSectionsToDefinition(
        object? definition, IReadOnlyList<ksEntity> sections)
    {
        object? holder;
        try
        {
            holder = definition switch
            {
                ksBaseLoftDefinition baseDefinition => baseDefinition.Sketchs(),
                ksBossLoftDefinition bossDefinition => bossDefinition.Sketchs(),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return (false, "Sketchs() определения бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        if (holder is not ksEntityCollection collection)
        {
            return (false, "Sketchs() определения не отдал ksEntityCollection: "
                + (holder is null ? "null" : holder.GetType().Name));
        }

        var before = SafeInt(() => collection.GetCount());
        var cleared = SafeBool(() => collection.Clear()) == true;
        var added = 0;
        foreach (var section in sections)
        {
            if (SafeBool(() => collection.Add(section)) == true)
            {
                added++;
            }
        }

        var after = SafeInt(() => collection.GetCount());
        var ok = cleared && added == sections.Count && after == sections.Count;
        return (ok, "до Clear() " + (before?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано")
            + ", Clear=" + cleared + ", добавлено " + added + ", стало "
            + (after?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"));
    }

    /// <summary>Entity name for the report. <c>null</c> means "not read", not an empty name: empty is
    /// indistinguishable from "forgot to read", and the identification of a transferred section rests on
    /// exactly that difference.</summary>
    private static string? ReadEntityName(ksEntity? entity)
    {
        if (entity is null)
        {
            return null;
        }

        try
        {
            return entity.name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "не прочитано: " + ex.GetType().Name;
        }
    }

    /// <summary>Re-address a sketch-section FROM THE TREE at edit time, by name.</summary>
    /// <remarks>INVARIANT: re-addressing is needed because a registry pointer lives against the revision it was registered in, while
    /// the edit arrives in the next one; the tree gives an address AT EDIT TIME, proven by the same enumeration the probe uses
    /// (<c>ksPart.EntityCollection(0).refresh()</c>). INVARIANT: a name is accepted only if UNAMBIGUOUS — a name is not an address
    /// (different entities share one display name), so collections are tried in turn, first match taken; with more than one match
    /// the address is NOT PROVEN and the caller refuses by name. A match is described in <c>note</c> on success too — otherwise the
    /// collection difference (0 / 110 / −1) disappears from the report.
    /// History: docs/decisions/adapter-solid.md#b5-readdress</remarks>
    private static ksEntity? ReAddressFromTree(ksPart part, ksEntity section, out string note)
    {        note = string.Empty;
        string name;
        try
        {
            name = section.name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            note = "имя сечения не прочитано: " + ex.GetType().Name;
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            note = "у сечения пустое имя: переадресовать нечем";
            return null;
        }

        foreach (var kind in new[]
                 {
                     (short)0,
                     KompasObjectTypes.Of(KompasObjectTypes.OperationElement),
                     (short)-1,
                 })
        {
            try
            {
                if (part.EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                collection.refresh();
                var matches = new List<ksEntity>();
                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is ksEntity candidate
                        && string.Equals(candidate.name, name, StringComparison.Ordinal))
                    {
                        matches.Add(candidate);
                    }
                }

                if (matches.Count == 0)
                {
                    continue;
                }

                if (matches.Count > 1)
                {
                    note = "сущностей с именем '" + name + "' в коллекции " + kind + ": "
                        + matches.Count + " — адрес не доказан";
                    return null;
                }

                // Describing a SUCCESSFUL match is mandatory too: without it "re-addressed" is
                // indistinguishable from "re-addressed to another entity with the same name", and the
                // collection difference (0 / 110 / −1) disappears from the report.
                note = "коллекция " + kind + ", совпадение по имени '" + name + "', совпадений 1";
                return matches[0];
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // The collection does not read — not a verdict: the rest are still scanned.
            }
        }

        note = "эскиз '" + name + "' в дереве не найден";
        return null;
    }

    /// <summary>Section count in the feature, read THROUGH THE DEFINITION — from where
    /// <see cref="LoftSectionRefs"/> and <c>kompas_get_feature</c> read them. Published BESIDE the count
    /// from <c>ILoft</c>: a divergence of the two reads of one feature must be visible. <c>null</c> means
    /// "not read", not zero.</summary>
    private static int? LoftSectionCount5(object definition)
    {
        try
        {
            var holder = definition switch
            {
                ksBaseLoftDefinition baseLoft => baseLoft.Sketchs(),
                ksBossLoftDefinition bossLoft => bossLoft.Sketchs(),
                _ => null,
            };
            return holder is ksEntityCollection collection ? collection.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>Section-set rules on EDIT — the same as at creation, for the same reason.</summary>
    private static void ValidateLoftSectionRefs(IReadOnlyList<string> refs)
    {
        if (refs.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Сечений " + refs.Count + ", а по одному сечению тело не строится: элемент по сечениям "
                + "соединяет не менее двух.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["sections"] = refs.Count });
        }

        if (refs.Any(string.IsNullOrWhiteSpace))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Среди сечений есть пустая ссылка: порядок массива — это порядок соединения, и пустое "
                + "место в нём меняет тело.",
                RetryPolicy.Never);
        }

        if (refs.Count != refs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Одно и то же сечение указано дважды: тело по двум одинаковым сечениям вырождается, и "
                + "«сколько сечений задано» стало бы неотличимо от «сколько раз его назвали».",
                RetryPolicy.Never);
        }
    }

    // ══════════════════════════════════════════════════════════ shell ══

    /// <summary>Edit the thickness, direction and removed-face set of an EXISTING shell.</summary>
    /// <remarks>INVARIANT: thickness and direction are written TOGETHER — the shell mode is a (thickness, direction)
    /// pair, and writing only the changed half makes "exactly what was requested changed" indistinguishable from "this
    /// changed too"; the missing half is taken FROM THE MODEL, not a default (MEASURED, B5.13: <c>thinType</c> reads
    /// back correctly after each edit). MEASURED 20.09.2026 — step B5.13: <c>t = 2 inward → 4 inward → 4 outward → 2
    /// inward</c> gave <c>21632 → 40256 → 53056 → 21632</c>; step B5.14: a second removed face gives <c>7040</c> at
    /// <c>10</c> faces, returning to <c>21632</c> at <c>11</c> (re-writing the same set moves no volume). LIMIT: an empty face list is rejected — MEASURED (B5.6, B5.10): the operation is accepted (<c>Create/Update = true</c>) but the body does not change.
    /// History: docs/decisions/adapter-solid.md#b5-shell-edit</remarks>
    private UpdateFeatureResult UpdateShellFeature(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignB5EditFields(command, ShellFamily, "thickness_mm, thin_inward, face_refs");

        if (DefinitionOf(entity) is not ksShellDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак не отвечает ksShellDefinition: записать толщину, направление и набор "
                + "удаляемых граней некуда. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = "shell_definition_not_recognized",
                    ["tree_type"] = entity.type,
                });
        }

        var before = ReadShellFeature(document, entity, definition, out var readNote);
        var currentThickness = SafeDouble(() => definition.thickness);
        var currentThinType = SafeBool(() => definition.thinType);

        // The mode is assembled WHOLE: the requested part comes from the command, the missing half from
        // the model.
        var thickness = command.ThicknessMm ?? currentThickness;
        var thinInward = command.ThinInward ?? currentThinType;

        if (thickness is not double targetThickness || thinInward is not bool targetThinInward)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Режим оболочки не собран: с модели не прочиталось "
                + (thickness is null ? "thickness " : string.Empty)
                + (thinInward is null ? "thinType " : string.Empty)
                + "и в запросе эта половина не задана. Писать половину режима значило бы сделать "
                + "«изменилось ровно запрошенное» неотличимым от «изменилось ещё и это». "
                + "Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["read_note"] = readNote,
                    ["thickness_from_request"] = command.ThicknessMm is not null,
                    ["direction_from_request"] = command.ThinInward is not null,
                });
        }

        if (!double.IsFinite(targetThickness) || targetThickness <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Толщина оболочки должна быть положительным конечным числом, а получено "
                + Num(targetThickness) + " мм.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["thickness_mm"] = targetThickness });
        }

        var facesBefore = FaceArrayCount(definition);

        // The removed-face set is a FULL replacement, not an addition: what must stay removed is passed.
        // The fillet edge-set edit is arranged the same way.
        List<ksFaceDefinition>? faces = null;
        if (command.FaceRefs is { Count: > 0 } faceRefs)
        {
            if (faceRefs.Count != faceRefs.Distinct(StringComparer.Ordinal).Count())
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Список удаляемых граней содержит повторы: одна и та же грань указана дважды, и "
                    + "«сколько граней снято» стало бы неотличимо от «сколько раз её назвали».",
                    RetryPolicy.Never);
            }

            faces = new List<ksFaceDefinition>(faceRefs.Count);
            foreach (var faceRef in faceRefs)
            {
                faces.Add(RequireFace(document, faceRef));
            }
        }
        else if (command.FaceRefs is { Count: 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Список удаляемых граней пуст. Измерено 20.09.2026 на обоих API: при пустом списке "
                + "операция принимается (Create/Update = true), но тело не меняется — объём остаётся "
                + "80000, число граней 6, как у исходного тела. Замкнутая оболочка пустым списком "
                + "граней не выражается; укажите хотя бы одну снимаемую грань или не передавайте поле.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["measured_closed_volume_mm3"] = 80000d,
                    ["measured_closed_face_count"] = 6,
                    ["measured_open_volume_mm3"] = 21632d,
                    ["measured_open_face_count"] = 11,
                });
        }

        if (!WriteShellMode(definition, targetThickness, targetThinInward))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись thickness/thinType в ksShellDefinition не состоялась: признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var facesWritten = -1;
        if (faces is not null)
        {
            facesWritten = WriteShellFaces(definition, faces);
            if (facesWritten != faces.Count)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Удаляемые грани записаны не полностью: " + facesWritten + " из " + faces.Count
                    + ". FaceArray() не привёлся к ksEntityCollection, Clear() или Add() вернули отказ. "
                    + "Признак изменён частично — режим записан, набор граней прежний.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["written_faces"] = facesWritten,
                        ["requested_faces"] = faces.Count,
                    });
            }
        }

        var updated = SafeBool(entity.Update) == true;
        if (!updated)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksEntity.Update() вернул false: записанный режим не применён к модели. Признак "
                + "изменён частично.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        var part = document.PartNow();
        part.RebuildModel();
        document.Document.RebuildDocument();
        BumpRevision(document, "shell.update");

        // Re-read FROM THE MODEL: the definition is taken from the feature afresh.
        var after = entity.GetDefinition() as ksShellDefinition;
        var readThickness = after is null ? null : SafeDouble(() => after.thickness);
        var readThinType = after is null ? null : SafeBool(() => after.thinType);
        var facesAfter = after is null ? null : FaceArrayCount(after);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal)
                          && featuresBefore == featuresAfter;

        var checks = new List<NamedCheck>
        {
            new("update_accepted", updated, "ksEntity.Update() вернул true"),
            new("feature_identity_preserved", sameFeature,
                Observed: stateAfter.Name + ", признаков " + featuresAfter,
                Expected: stateBefore.Name + ", признаков " + featuresBefore),
            new("thickness_read_back",
                readThickness is double rt && Math.Abs(rt - targetThickness) <= 1e-6,
                Observed: Num(readThickness), Expected: Num(targetThickness)),
            new("thin_direction_read_back", readThinType == targetThinInward,
                Observed: ThinDirectionName(readThinType) ?? "не прочитано",
                Expected: ThinDirectionName(targetThinInward) ?? "не прочитано"),
        };

        if (faces is not null)
        {
            checks.Add(new NamedCheck("removed_faces_read_back", facesAfter == faces.Count,
                Observed: facesAfter?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано",
                Expected: faces.Count.ToString(CultureInfo.InvariantCulture)));
        }

        // A second independent signal beside the volume: a shell also changes its face count. It is
        // exactly what would catch the "accepted and not applied" outcome, which looks like success by
        // volume alone.
        if (facesAfter is int afterCount && facesBefore is int beforeCount && faces is not null)
        {
            checks.Add(new NamedCheck("face_count_changed", afterCount != beforeCount,
                Observed: beforeCount + " → " + afterCount,
                Expected: "набор удаляемых граней заменён, поэтому число граней тела может измениться"));
        }

        var unverified = new List<string>
        {
            "tangent_faces_not_supported — IShell.SetFaces(Faces, TangentFaces) в API7 принимает признак "
            + "касательных граней, но у API5 ksShellDefinition такого члена нет; режим "
            + "SM-13.shell.mode_tangent_faces здесь не выражается и не выдаётся за выполненный",
            "variable_thickness_out_of_scope — переменная толщина по граням вне обязательного объёма "
            + "этапа (OQ-A13)",
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };

        if (readNote is not null)
        {
            unverified.Add(readNote);
        }

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var matches = volumeAfter is { } value
                          && Math.Abs(value - expected) <= VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(volumeAfter) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма "
                + "геометрия правки не подтверждена числом, и уровень честно остаётся call_returned");
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            ShellFamily,
            sameFeature,
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

    /// <summary>Write BOTH shell mode parameters. Written together on purpose: the mode is a pair, and
    /// writing one half would make "exactly what was requested changed" indistinguishable from "this
    /// changed too".</summary>
    private static bool WriteShellMode(ksShellDefinition definition, double thickness, bool thinType)
    {
        try
        {
            definition.thickness = thickness;
            definition.thinType = thinType;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Write the removed-face set as a FULL replacement: <c>Clear()</c>, then <c>Add()</c> per
    /// face. Returns the number of faces written; fewer than requested is a route failure.</summary>
    /// <remarks>The <c>Clear() + Add()</c> route measurably did not work for a FILLET (row
    /// <c>FL04r</c>), so it was checked separately on the shell rather than carried over by analogy: step
    /// B5.14 gave <c>21632 → 7040 → 21632</c> at <c>11 → 10 → 11</c> faces, with a negative control on
    /// re-writing.</remarks>
    private static int WriteShellFaces(ksShellDefinition definition, IReadOnlyList<ksFaceDefinition> faces)
    {
        object? holder;
        try
        {
            holder = definition.FaceArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }

        if (holder is not ksEntityCollection collection)
        {
            return 0;
        }

        if (SafeBool(() => collection.Clear()) != true)
        {
            return 0;
        }

        var written = 0;
        foreach (var face in faces)
        {
            if (SafeBool(() => collection.Add(face)) == true)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>A face by registry reference — with document and revision checks, as at shell creation.
    /// An edit of a face set from a foreign part or with a stale reference is rejected before COM.</summary>
    private ksFaceDefinition RequireFace(DocumentEntry document, string faceRef)
    {
        if (!References.TryGet(faceRef, out var stored) || stored is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка на грань '{faceRef}' не найдена в реестре ссылок.",
                RetryPolicy.ReacquireContext);
        }

        if (!string.Equals(stored.DocumentId, document.Id, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Грань '{faceRef}' принадлежит документу {stored.DocumentId}, а оболочка правится в "
                + $"{document.Id}. Грани из чужой детали не переносятся.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["face_document_id"] = stored.DocumentId,
                    ["shell_document_id"] = document.Id,
                });
        }

        if (stored.Revision != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Грань '{faceRef}' выпущена для ревизии {stored.Revision}, у документа уже "
                + $"{document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["reference_revision"] = stored.Revision,
                    ["current_revision"] = document.Revision,
                });
        }

        if (stored.Payload is not ksFaceDefinition face)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{faceRef}' указывает не на грань (kind={stored.Kind}).",
                details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
        }

        return face;
    }

    /// <summary>Number of features of this family in the tree. <c>null</c> means "not read", which
    /// differs from zero: at zero the "tree position = collection index" matching is not proved.</summary>
    private static int? CountAmong(ksPart part, Func<ksEntity, bool> isKind)
    {
        try
        {
            if (part.EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
                is not ksEntityCollection collection)
            {
                return null;
            }

            var count = 0;
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity candidate && isKind(candidate))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }
}

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

/// <summary>
/// Правка трёх семейств очереди B5 — кинематической операции, элемента по сечениям и оболочки —
/// по <c>kompas_update_feature</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему правка вообще существует, а не «удалить и создать заново».</b> Наряд B5 §11 требует
/// менять параметры СУЩЕСТВУЮЩЕГО признака: пересоздание даёт другой признак дерева, другое имя и
/// другую позицию среди односемейных, а для признаков, у которых ниже по дереву стоят зависимые,
/// ещё и другой набор зависимостей. Поэтому у каждого семейства измерен маршрут «запись в тот же
/// признак → <c>Update()</c> → пересборка», и правка подтверждается ГЕОМЕТРИЕЙ, а не ответом
/// <c>Update()</c>.
/// </para>
/// <para>
/// <b>Что измерено и каким шагом</b> (проба <c>--b5</c>, отчёт
/// <c>docs/acceptance/api7/b5-sweep-loft-shell.json</c>):
/// </para>
/// <list type="bullet">
/// <item><b>B5.13</b> — режим кинематики <c>24674.011002723353 → 15707.963267948984 →
/// 24674.011002723353</c>; толщина и направление оболочки <c>21632 → 40256 → 53056 → 21632</c>;
/// перепривязка сечений <c>28000 → 48000 → 28000</c>;</item>
/// <item><b>B5.14</b> — набор удаляемых граней оболочки <c>21632 → 7040 → 21632</c> при
/// <c>11 → 10 → 11</c> гранях, с отрицательным контролем (повторная запись того же набора объём не
/// двигает);</item>
/// <item><b>B5.15</b> — ВХОДЫ кинематики: <c>SetSketch</c> и перепривязка траектории принимаются
/// (<c>Update = true</c>) и НЕ применяются при положительном контроле на том же признаке.</item>
/// </list>
/// <para>
/// <b>Чего здесь нет и почему.</b> <c>closed</c> у элемента по сечениям не объявлен правимым: запись
/// <c>ILoft.Closed</c> на построенном признаке возвращает <c>Update() = True</c>, читается обратно
/// <c>False</c>, объём не меняется (B5.13) — «принято» не означает «применено». Входы кинематики
/// (<c>sketch_ref</c>, траектория) отвергаются по имени: маршрут измеренно не применяется (B5.15),
/// а принять и проигнорировать эскиз значило бы отчитаться о правке, которой не было.
/// </para>
/// <para>
/// <b>Поля других семейств отвергаются, а не игнорируются.</b> Цена ошибки здесь несимметрична:
/// лишний отказ виден сразу, а принятое и проигнорированное число доживает до приёмки, выглядя как
/// выполненная операция.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Поле правки, его семейство-владелец и способ прочитать значение из команды.</summary>
    private sealed record EditField(string Name, string Family, Func<UpdateFeatureCommand, object?> Read);

    /// <summary>
    /// Поля, принадлежащие семействам B5. Одна таблица на два вопроса — «кто владеет» и «что лежит»:
    /// разойтись этим двум ответам не с чем.
    /// </summary>
    private static readonly EditField[] B5EditFields =
    {
        new("shift_mode", EvolutionFamily, c => c.ShiftMode),
        new("section_refs", LoftFamily, c => c.SectionRefs),
        new("couplings", LoftFamily, c => c.Couplings),
        new("thickness_mm", ShellFamily, c => c.ThicknessMm),
        new("thin_inward", ShellFamily, c => c.ThinInward),
        new("face_refs", ShellFamily, c => c.FaceRefs),
    };

    /// <summary>
    /// Поля правки, НЕ принадлежащие B5: выдавливание, фаска, скругление, вращение, семейства B3 и
    /// массив. Перечислены явно и полностью — этим и отвергаются.
    /// </summary>
    /// <remarks>
    /// <b>Почему перечень, а не «список запрещённого».</b> Перечень того, что бывает в команде,
    /// меняется вместе с контрактом, а умолчание обязано быть «не отвергай»: поле, не приписанное
    /// ни одному семейству, отвергается не здесь, а своим семейством. Разница видна на цене ошибки:
    /// пропущенное в перечне поле принимается и не применяется — ровно тот класс дефекта, ради
    /// которого написано правило «объявленное, но проглоченное» (§9.1 П4, с другой стороны).
    /// </remarks>
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
        // Поля семейства ОТВЕРСТИЯ (наряд SM07 §3.2). Дописаны 20.09.2026 вместе с появлением самой
        // правки отверстия: до неё этих полей в контракте не было вовсе, а с их появлением перечень
        // чужих полей обязан был их получить — иначе вызов «shift_mode + diameter_mm» на
        // кинематической операции был бы принят, режим применён, а диаметр проглочен.
        new("diameter_mm", "hole", c => c.DiameterMm),
        new("counterbore_diameter_mm", "hole", c => c.CounterboreDiameterMm),
        new("counterbore_depth_mm", "hole", c => c.CounterboreDepthMm),
        new("countersink_diameter_mm", "hole", c => c.CountersinkDiameterMm),
        new("countersink_angle_deg", "hole", c => c.CountersinkAngleDeg),
        new("expected_volume_delta_mm3", "hole", c => c.ExpectedVolumeDeltaMm3),
    };

    /// <summary>
    /// Отвергнуть поля, не принадлежащие этому семейству B5. Отвергаются ДО обращения к COM.
    /// </summary>
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

    // ══════════════════════════════════════════════════════════ кинематика ══

    /// <summary>
    /// Правка режима движения сечения СУЩЕСТВУЮЩЕЙ кинематической операции.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (проба <c>--b5</c>, шаг B5.13): на ОДНОМ признаке смена
    /// <c>sketchShiftType</c> <c>orthogonal → parallel → orthogonal</c> дала объёмы
    /// <c>24674.011002723353 → 15707.963267948984 → 24674.011002723353</c>. Постановка различающая
    /// только на ДУГЕ: на прямой траектории оба режима дают одно тело (B5.2, B5.8).
    /// </para>
    /// <para>
    /// <b>Порядок «запись → <c>Update()</c> → пересборка» — часть контракта</b>: без <c>Update()</c>
    /// сеттер возвращает успех, а модель остаётся прежней. Ответ <c>Update() = true</c> при этом
    /// доказательством не считается — объём читается с модели и сверяется с аналитическим ожиданием
    /// вызывающего.
    /// </para>
    /// <para>
    /// <b>Входы признака этим вызовом не меняются, и это измеренный отказ, а не осторожность.</b>
    /// Шаг B5.15: <c>SetSketch</c> на существующем признаке и перепривязка <c>PathPartArray()</c>
    /// принимаются (<c>Update = true</c>) и НЕ применяются — объём остаётся прежним, тогда как смена
    /// РЕЖИМА на том же признаке его меняет. Поэтому <c>sketch_ref</c> отвергается по имени.
    /// </para>
    /// </remarks>
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

        // Перечитывается С МОДЕЛИ: определение берётся у признака заново, а не пересказывается
        // запрошенное значение.
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
            // Сравнение БЕЗ учёта регистра, и это исправленный дефект, а не стилистика: модель
            // отдаёт имя режима строчными («parallel»), а `mode.ToString()` — имя члена
            // перечисления («Parallel»), поэтому порядковое сравнение объявляло ПРОВАЛЬНОЙ проверку
            // при верно прочитанном режиме. Измерено 20.09.2026 на приёмке B5: строки B5K.01/B5K.02
            // проходили по своему правилу, тогда как ответ продукта нёс `shift_mode_read_back: false`
            // при `observed = parallel` и `expected = Parallel` — то есть продукт сам себя объявлял
            // неисправным, и это видел только тот, кто читает ПОЛЯ, а не вердикт.
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
            // Глубины у кинематической операции нет: поле относится к выдавливанию, поэтому null.
            null,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>
    /// Записать режим движения сечения в определение, ответившее одним из двух интерфейсов. Ветка
    /// обязательна, а не удобна: общего интерфейса с этим членом у двух определений нет, а измерено,
    /// что признак, созданный <c>NewEntity(45)</c>, отвечает <c>ksBossEvolutionDefinition</c>.
    /// </summary>
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

    // ══════════════════════════════════════════════════════════ по сечениям ══

    /// <summary>
    /// Правка набора сечений СУЩЕСТВУЮЩЕГО элемента по сечениям.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Правится ВХОД, и это измерено.</b> Шаг B5.13: перепривязка <c>ILoft.Sketchs</c> на уже
    /// построенном признаке меняет геометрию — <c>40×40 + 20×20</c> дают <c>28000</c>,
    /// <c>40×40 + 40×40</c> дают призму <c>h/3·(A₁ + A₂ + √(A₁A₂)) = 10·(1600+1600+1600) =
    /// 48000</c>, возврат к прежнему набору возвращает <c>28000</c>.
    /// </para>
    /// <para>
    /// <b>Замкнутость (<c>closed</c>) правимым параметром не объявлена, и это измеренный факт.</b>
    /// Запись <c>ILoft.Closed</c> на построенном признаке возвращает <c>Update() = True</c>, читается
    /// обратно <c>False</c>, объём остаётся <c>28000</c>: «принято» не означает «применено».
    /// Замкнутость задаётся только при создании (<c>kompas_loft.closed</c>).
    /// </para>
    /// <para>
    /// <b>Адрес элемента берётся по порядку среди односемейных, и порядок проверяется.</b> И дерево,
    /// и коллекция API7 перечисляют признаки в порядке создания, поэтому позиция — устойчивый адрес
    /// (имя для этого не годится: разные типы носят одно отображаемое имя). Но соответствие
    /// «позиция в дереве = индекс в коллекции» доказано только при РАВНОМ числе элементов: если
    /// деревьевых признаков больше или меньше, чем элементов в <c>ILofts</c>, сопоставление не
    /// доказано, и вызов отвергается по имени, а не правит элемент «наугад».
    /// </para>
    /// </remarks>
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

        // Соответствие «позиция в дереве = индекс в коллекции» доказано только при равном числе
        // элементов. Расхождение означает, что адрес не доказан, и правка отвергается по имени.
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
        // Свежие адреса сечений — те, что ДОКАЗАНЫ деревом. Нужны отдельно от `transferred`:
        // в определение API5 записывается адрес (ksEntity), в ILoft — перенесённый объект API7.
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

            // Эскиз переадресуется С ДЕРЕВА в момент правки: указатель из реестра в ILoft.Sketchs
            // не принимается (измерено, строка B5S.01 — см. ReAddressFromTree). Отказ здесь —
            // ИМЕНОВАННЫЙ: адрес не доказан, и правка по недоказанному адресу изменила бы не тот
            // объект, а «не тот объект» здесь неотличим от «тот же самый» по объёму.
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

            // ЧТО ИМЕННО ПЕРЕДАНО — публикуется по каждому сечению. Без этого «перепривязка не
            // применилась» неотличимо от «применилась перепривязка на другую сущность»: число
            // сечений в обоих исходах одно и то же, а имя — единственное, чем они различаются.
            var freshName = ReadEntityName(fresh);
            transferTrace.Add(
                $"'{ReadEntityName(target.Sketch)}' → свежее имя '{freshName}' [{readdressNote}]"
                + (string.Equals(freshName, ReadEntityName(target.Sketch), StringComparison.Ordinal)
                    ? string.Empty
                    : " — ИМЯ РАЗОШЛОСЬ С ЗАПРОШЕННЫМ"));
        }

        // ── Параллельность плоскостей сечений: та же обязанность, что и при создании (§9.2) ──
        // Правка набором сечений на непараллельных плоскостях описала бы другое тело так же, как и
        // создание, поэтому отказ обязан стоять здесь ДО записи, а не после неё.
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

        // ── Цепочки соответствия: сначала ЧТЕНИЕ и решение, потом запись ──
        // Измерено 20.09.2026 (проба --b5, шаг B5.19): присваивание ILoft.Sketchs СБРАСЫВАЕТ цепочки —
        // CouplingsCount читался 1, а после повторного присваивания ТОГО ЖЕ набора сечений стал 0, и
        // объём вернулся с 20000 к 28000 (автоматическое соответствие). Поэтому проверка обязана
        // стоять ДО записи сечений: после неё «признак нёс цепочки» стало бы неотличимо от «не нёс»,
        // и молчаливая потеря соответствия выглядела бы как нормальный проход.
        var couplingsBefore = Api7Loft.CouplingsCount(loft);

        // ── НЕПУСТЫЕ ЦЕПОЧКИ НА СУЩЕСТВУЮЩЕМ ПРИЗНАКЕ ОТВЕРГАЮТСЯ ДО ЗАПИСИ ──
        // Измерено 20.09.2026 (приёмка B5, строки B5S.01/B5S.02): на ПОСТРОЕННОМ признаке
        // ILoft.AddCoupling() возвращает ICoupling, PositionOffset принимает смещения, и
        // CouplingsCount читается 1 СРАЗУ ПОСЛЕ записи — но построение цепочку не несёт: после
        // Update() в модели 0 цепочек, и объём соответствует телу БЕЗ соответствия. Воспроизведено
        // в ДВУХ порядках записи (одно построение; и «построение набора сечений, затем перечитывание
        // ILoft по ILofts::Loft и задание соответствия»), то есть это не порядок нашей записи.
        // При СОЗДАНИИ та же последовательность цепочку сохраняет (проба B5.18: CouplingsCount = 1,
        // объём 20000 против 28000). Документированные члены (iloft_addcoupling.html,
        // iloft_clearcouplings.html, iloft_deletecoupling.html) такого ограничения не объявляют —
        // значит это ИЗМЕРЕНИЕ поведения реализации, и оно названо здесь, а не умолчано.
        // Принять такой запрос значило бы пообещать соответствие, которого модель не получит, и
        // вернуть «выполнено» на теле без него; поэтому отказ стоит ДО записи, а признак не изменён.
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

        // ── ЗАПИСЬ НАБОРА СЕЧЕНИЙ: В ОБА ХРАНИЛИЩА ВХОДА — ОПРЕДЕЛЕНИЕ API5 И ILoft ──
        // Вход «набор сечений» живёт в ДВУХ местах: в определении API5 (ksBase/BossLoftDefinition
        // .Sketchs() → ksEntityCollection) и в объекте операции API7 (ILoft.Sketchs). Измерено
        // 20.09.2026 (проба --b5, шаг B5.21, признак, созданный на трёх сечениях, сводится к двум):
        //   • пока определения НИКТО НЕ ЧИТАЛ, запись в ILoft.Sketchs применяется — «до записи 3,
        //     после присваивания 2, после Update() 2, объём 48000» — и повторяется четыре раза подряд;
        //   • после ОДНОГО чтения ksBaseLoftDefinition.Sketchs() — тем же членом, которым читает
        //     LoftSectionRefs и, значит, kompas_get_feature, — владельцем ЧИСЛА сечений становится
        //     определение, и та же запись отменяется первым же обновлением: «после присваивания 2,
        //     после ksEntity.Update() 3, объём прежний 16114.2858257129»;
        //   • запись набора В ОБА ХРАНИЛИЩА (сначала определение: «до Clear() 3, Clear=True,
        //     добавлено 2, стало 2»; затем ILoft.Sketchs; затем Update()) отмену снимает — прочитано
        //     2, объём 48000.
        // Поэтому пишутся оба. Отказ от чтения определения был бы отказом от kompas_get_feature:
        // ссылки на сечения берутся именно оттуда, и без него вход правки не выражается вовсе.
        // Прежняя редакция писала только в ILoft и потому работала лишь до первого чтения признака.
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

        // ЧИТАЕТСЯ СРАЗУ ПОСЛЕ ЗАПИСИ, ДО Update(), и это разные утверждения: «запись принята» и
        // «запись легла». Без этого чтения исход «запись не легла» и исход «запись легла, а
        // перестроение её не взяло» неотличимы, а лечатся они по-разному.
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

        // ПРИМЕНЕНИЕ ВХОДА — ksEntity.Update(). Выбор измерен, и его ОБОСНОВАНИЕ ИСПРАВЛЕНО 20.09.2026.
        // Прежняя редакция этого места приписывала отмену записи самому вызову: «ILoft.Update() = true,
        // и после него в ILoft снова 3 — значит Update() записанный вход не применяет». Это оказалось
        // НЕВЕРНО, и вот измеренное различие (проба --b5, шаг B5.21, признак, созданный на трёх
        // сечениях, сводится к двум):
        //   • ПОКА ОПРЕДЕЛЕНИЯ НИКТО НЕ ЧИТАЛ, применяется и ILoft.Update(), и ksEntity.Update() —
        //     «после присваивания 2, после Update() 2, объём 48000», четыре раза подряд;
        //   • владельцем ЧИСЛА сечений определение становится от ОДНОГО ЧТЕНИЯ
        //     ksBaseLoftDefinition.Sketchs() — того самого члена, которым читает LoftSectionRefs
        //     (а значит, и kompas_get_feature): после такого чтения ТА ЖЕ запись отменяется первым же
        //     обновлением, каким бы оно ни было — «после присваивания 2, после ksEntity.Update() 3».
        //     То есть отменяло не обновление, а несогласованность двух хранилищ входа.
        //   • запись набора В ОБА ХРАНИЛИЩА (см. WriteLoftSectionsToDefinition выше) отмену снимает.
        // Поэтому применяет ksEntity.Update() — тот же вызов, которым применяется запись определения у
        // оболочки, — но полагаться на один вызов здесь нельзя: существенно, что записаны ОБА
        // хранилища. Отказ от чтения определения был бы отказом от kompas_get_feature.
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

        // ПЕРЕСБОРКА: СНАЧАЛА API5, ПОТОМ API7, и оба числа публикуются. Проба B5.13 после правки
        // вызывала ksPart.RebuildModel() + ksDocument3D.RebuildDocument() — то есть ПЕРЕСБОРКУ API5, —
        // а здешний маршрут когда-то ограничивался part7.RebuildModel(true). Какая из двух пересборок
        // переносит записанный вход в тело, различимо только чтением между ними, поэтому читается
        // после каждой. Измерено 20.09.2026 (шаг B5.21): обе пересборки дают ОДНО тело, то есть
        // переносит вход применение (Update()), а не какая-то из пересборок; прежнее чтение «28000
        // вместо 37000» объясняется несогласованностью хранилищ, а не порядком пересборок.
        part.RebuildModel();
        var sectionsAfterApi5Rebuild = Api7Loft.SectionCount(loft);
        document.Document.RebuildDocument();
        var sectionsAfterDocumentRebuild = Api7Loft.SectionCount(loft);
        var volumeAfterApi5Rebuild = ReadVolume(document);

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "loft.update");
        // Сечения читаются ДВУМЯ путями, и публикуются ОБА числа: в ILoft (куда записано) и в
        // определении признака (оттуда же их читает kompas_get_feature). Расхождение двух чтений
        // должно быть видно, а не скрыто выбором удобного из них; судит строка по тому месту, куда
        // записано.
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
            // ЗАПИСЬ ОБЯЗАНА БЫТЬ ДЕЙСТВИЕМ, А НЕ ОТЧЁТОМ О НЁМ: «присваивание прошло» и «набор
            // сечений в операции стал другим» — разные утверждения, и между ними стоит ЧТЕНИЕ.
            new("sections_write_reaches_operation", sectionsAfterWrite == requested.Count,
                Observed: "до записи " + Num(sectionsBefore) + ", после присваивания ILoft.Sketchs "
                    + Num(sectionsAfterWrite) + ", после ksEntity.Update() " + Num(sectionsAfterEntityUpdate)
                    + ", после RebuildModel() " + Num(sectionsAfterApi5Rebuild)
                    + ", после RebuildDocument() " + Num(sectionsAfterDocumentRebuild)
                    + ", в конце " + Num(sectionsAfter),
                Expected: requested.Count.ToString(CultureInfo.InvariantCulture)),
            // ЧТО ПЕРЕДАНО — по каждому сечению. Число сечений одинаково и при «передал то, что
            // просили», и при «передал другую сущность с тем же именем», поэтому опознание обязано
            // стоять РЯДОМ с числом, а не вместо него.
            new("sections_transferred_named", transferTrace.Count == requested.Count,
                Observed: string.Join("; ", transferTrace),
                Expected: "по каждому сечению: имя запрошенного = имя свежего, коллекция названа"),
            // ВХОД ПИШЕТСЯ В ОБА ХРАНИЛИЩА, и оба чтения публикуются: без этого «запись отменена
            // обновлением» неотличимо от «запись не дошла до владельца», а лечатся они по-разному.
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
            // ПЕРЕСБОРКА НЕ ОБЯЗАНА МЕНЯТЬ ТЕЛО. Расхождение двух чисел означает, что записанный
            // вход переносит в тело одна из пересборок, а не другая, и тогда «правка применена» —
            // выдача пересборки за правку.
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
            // Читается МОДЕЛЬ: сколько цепочек и какие смещения стоят на каждом сечении. Число
            // цепочек без содержимого доказывало бы только существование объекта.
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

    /// <summary>Записать набор сечений в <c>ILoft.Sketchs</c> — SAFEARRAY указателей IDispatch.</summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут записи — тот же объект, которым признак создан.</b> Признак создаётся
    /// <c>IModelContainer.Lofts.Add(o3d_bossLoft)</c>, то есть <c>ILoft</c> и есть та операция, что
    /// владеет признаком, поэтому и вход пишется, и <c>Update()</c> берётся у неё же. Проба B5.13
    /// этим маршрутом получила на УЖЕ ПОСТРОЕННОМ признаке 48000 (перепривязка сечений S5+S6 →
    /// S5+S7), то есть маршрут исполняющийся, а не теоретический.
    /// </para>
    /// <para>
    /// <b>Запись через коллекцию определения ОБЯЗАТЕЛЬНА, и её роль ИСПРАВЛЕНА 20.09.2026.</b>
    /// Прежняя редакция этого места объявляла запись через <c>ksBaseLoftDefinition.Sketchs()</c> /
    /// <c>ksBossLoftDefinition.Sketchs()</c> «не исполняющейся» и удалила её из кода: измерялось, что
    /// <c>Clear()</c> отчитывается успехом (коллекция читается 0), <c>Add()</c> её наполняет
    /// (читается 2), <c>ksEntity.Update()</c> = true, обе пересборки выполнены — и тело прежнее. Это
    /// наблюдение было ВЕРНЫМ, а вывод из него — неверным: там запись шла ТОЛЬКО в определение, а
    /// тело строится по <c>ILoft</c>, и «не исполняется» означало «одного определения недостаточно».
    /// Измерено 20.09.2026 пробой <c>--b5</c> (шаг B5.21): достаточно ОДНОГО ЧТЕНИЯ определения, чтобы
    /// владельцем ЧИСЛА сечений стало ОНО, и тогда запись только в <c>ILoft</c> отменяется первым же
    /// обновлением. Поэтому пишутся ОБА хранилища, и порядок — сначала определение, затем
    /// <c>ILoft.Sketchs</c>: см. <see cref="WriteLoftSectionsToDefinition"/>.
    /// </para>
    /// <para>
    /// <b>Чего эта запись НЕ делает:</b> не проверяет, что сечение стоит в дереве ВЫШЕ признака.
    /// Измерено тогда же: эскиз, созданный ПОСЛЕ признака, признак в себя не берёт — запись
    /// принимается, <c>Update()</c> = true, сечений читается 2, тело прежнее. Признак ссылается
    /// только на то, что стоит выше него; проверить это на стороне прибора, а не сервера, — потому
    /// что «выше» определяется порядком дерева, а не полем запроса.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Запись набора сечений В ОПРЕДЕЛЕНИЕ API5 — во ВТОРОЕ хранилище того же входа.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем, если запись идёт в <c>ILoft</c>.</b> Измерено 20.09.2026 (проба <c>--b5</c>, шаг
    /// B5.21): признак, созданный на ТРЁХ сечениях, сводится к ДВУМ записью в <c>ILoft.Sketchs</c> —
    /// и это повторяется, пока определения никто не читал («до записи 3, после присваивания 2,
    /// после <c>Update()</c> 2, объём 48000» — четыре раза подряд). Но стоит ОДИН раз прочитать
    /// <c>ksBaseLoftDefinition.Sketchs()</c> — тем самым членом, которым читает
    /// <see cref="LoftSectionRefs"/> и, значит, <c>kompas_get_feature</c>, — как владельцем ЧИСЛА
    /// сечений становится определение, и следующая ТА ЖЕ запись отменяется первым же обновлением:
    /// «после присваивания 2, после <c>Update()</c> 3, объём прежний 16114.2858257129».
    /// </para>
    /// <para>
    /// <b>И обратное измерено на том же объекте.</b> Та же правка, у которой набор записан В ОБА
    /// ХРАНИЛИЩА — сначала определение («до <c>Clear()</c> 3, <c>Clear</c>=True, добавлено 2, стало
    /// 2»), затем <c>ILoft.Sketchs</c>, затем <c>Update()</c>, — снова применяется: прочитано 2,
    /// объём <c>48000</c>. Поэтому вход пишется в оба места, а не в одно.
    /// </para>
    /// <para>
    /// <b>Чтение определения при этом не «портит» признак — оно делает определение владельцем
    /// входа.</b> Различие существенное: отказ от чтения был бы отказом от <c>kompas_get_feature</c>,
    /// а не исправлением, и он не выразил бы вход правки вовсе (ссылки на сечения берутся оттуда).
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Имя сущности для отчёта. <c>null</c> — «не прочитано», а не пустое имя: пустое неотличимо от
    /// «забыли прочитать», и именно на этом различии стоит опознание переданного сечения.
    /// </summary>
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

    /// <summary>
    /// Переадресовать эскиз-сечение С ДЕРЕВА в момент правки, по имени.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем, если указатель уже есть.</b> Причина ИСПРАВЛЕНА 20.09.2026 по измерению (проба
    /// <c>--b5</c>, шаг B5.21). Прежняя редакция объясняла это так: «указатель <c>ksEntity</c> из
    /// реестра в <c>ILoft.Sketchs</c> не принимается — записано 2 из 3, прочитано 3». Наблюдение
    /// было верным, а причина названа неверно: запись отменяло не происхождение указателя, а то, что
    /// определения к тому моменту УЖЕ ПРОЧИТАЛИ (сечения читаются через
    /// <c>ksBaseLoftDefinition.Sketchs()</c>), и владельцем числа сечений стало оно. Отмену снимает
    /// запись в ОБА хранилища, а не свежесть указателя.
    /// </para>
    /// <para>
    /// <b>Переадресация при этом остаётся нужной, и по другой причине:</b> указатель из реестра
    /// живёт на ревизию, в которой зарегистрирован, а правка приходит в следующей. Дерево даёт адрес
    /// В МОМЕНТ правки, и он доказан — тем же перечислением, каким его берёт проба
    /// (<c>ksPart.EntityCollection(0).refresh()</c>).
    /// </para>
    /// <para>
    /// <b>Имя принимается только ОДНОЗНАЧНОЕ.</b> Имя — не адрес: разные сущности носят одно
    /// отображаемое имя. Поэтому перебираются коллекции дерева по очереди, и берётся первая, где
    /// нашлось совпадение; если совпадений в ней больше одного, адрес НЕ ДОКАЗАН, и вызывающий
    /// обязан отказать по имени, а не править признак «наугад». Совпадение описывается в
    /// <c>note</c> и при успехе: без описания «переадресовано» неотличимо от «переадресовано на
    /// другую сущность с тем же именем», и различие коллекций (0 / 110 / −1) исчезает из отчёта.
    /// </para>
    /// </remarks>
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

                // Описание УДАЧНОГО совпадения тоже обязательно: без него «переадресовано» неотличимо
                // от «переадресовано на другую сущность с тем же именем», и различие коллекций
                // (0 / 110 / −1) исчезает из отчёта.
                note = "коллекция " + kind + ", совпадение по имени '" + name + "', совпадений 1";
                return matches[0];
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Коллекция не читается — это не приговор: просматриваются и остальные.
            }
        }

        note = "эскиз '" + name + "' в дереве не найден";
        return null;
    }

    /// <summary>
    /// Число сечений в признаке, прочитанное ЧЕРЕЗ ОПРЕДЕЛЕНИЕ — оттуда же, откуда их читает
    /// <see cref="LoftSectionRefs"/> и <c>kompas_get_feature</c>. Публикуется РЯДОМ с числом из
    /// <c>ILoft</c>: расхождение двух чтений одного признака должно быть видно. <c>null</c> —
    /// «не прочитано», а не ноль.
    /// </summary>
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

    /// <summary>Правила набора сечений на ПРАВКЕ — те же, что на создании, и по той же причине.</summary>
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

    // ══════════════════════════════════════════════════════════ оболочка ══

    /// <summary>
    /// Правка толщины, направления и набора удаляемых граней СУЩЕСТВУЮЩЕЙ оболочки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Толщина и направление пишутся ВМЕСТЕ, и это не удобство, а требование к постановке.</b>
    /// Режим оболочки — пара (толщина, направление). Если писать только изменяемую половину, «изменилось
    /// ровно запрошенное» становится неотличимо от «изменилось ещё и это». Недостающая половина берётся
    /// с МОДЕЛИ (не из умалчиваемого значения): измерено (B5.13), что <c>thinType</c> читается обратно
    /// верно после каждой правки.
    /// </para>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026.</b> Шаг B5.13: <c>t = 2 внутрь → 4 внутрь → 4 наружу → 2 внутрь</c>
    /// на одном признаке дал <c>21632 → 40256 → 53056 → 21632</c>. Шаг B5.14: набор удаляемых граней
    /// правится тем же признаком — добавление второй грани даёт <c>7040</c> при <c>10</c> гранях,
    /// возврат к прежнему набору — <c>21632</c> при <c>11</c>; повторная запись того же набора объём
    /// не двигает (отрицательный контроль).
    /// </para>
    /// <para>
    /// <b>Пустой список граней отвергается и здесь.</b> Измерено на обоих API (B5.6, B5.10): при
    /// пустом списке операция принимается (<c>Create/Update = true</c>), а тело не меняется. Принять
    /// такой вызов значило бы вернуть «оболочка построена» там, где ничего не произошло.
    /// </para>
    /// </remarks>
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

        // Режим собирается ЦЕЛИКОМ: запрошенное берётся из команды, недостающая половина — с модели.
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

        // Набор удаляемых граней — ПОЛНАЯ замена, а не добавление: передаётся то, что должно
        // остаться снятым. Так же устроена правка набора рёбер скругления.
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

        // Перечитывается С МОДЕЛИ: определение берётся у признака заново.
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

        // Второй независимый признак рядом с объёмом: у оболочки меняется и число граней. Именно он
        // поймал бы исход «принято и не применено», который по одному объёму выглядит как успех.
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

    /// <summary>
    /// Записать ОБА параметра режима оболочки. Пишутся вместе намеренно: режим — пара, и запись
    /// одной половины сделала бы «изменилось ровно запрошенное» неотличимым от «изменилось ещё и это».
    /// </summary>
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

    /// <summary>
    /// Записать набор удаляемых граней как ПОЛНУЮ замену: <c>Clear()</c>, затем <c>Add()</c> по каждой.
    /// Возвращает число записанных граней; меньше запрошенного — отказ маршрута.
    /// </summary>
    /// <remarks>
    /// Маршрут <c>Clear() + Add()</c> у СКРУГЛЕНИЯ измеренно не работал (строка <c>FL04r</c>), поэтому
    /// он проверен отдельно на оболочке, а не перенесён по аналогии: шаг B5.14 дал <c>21632 → 7040 →
    /// 21632</c> при <c>11 → 10 → 11</c> гранях и отрицательный контроль на повторную запись.
    /// </remarks>
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

    /// <summary>
    /// Грань по ссылке реестра — с проверкой документа и ревизии, как при создании оболочки. Правка
    /// набора граней из чужой детали или устаревшей ссылкой отвергается до COM.
    /// </summary>
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

    /// <summary>
    /// Число признаков этого семейства в дереве. <c>null</c> — «не прочитано», и это отличается от
    /// нуля: на нуле сопоставление «позиция в дереве = индекс в коллекции» не доказывается.
    /// </summary>
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

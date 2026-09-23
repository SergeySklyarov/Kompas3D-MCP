using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Правка параметров СУЩЕСТВУЮЩЕГО признака массива (очередь B4, действие <c>edit</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему отдельный файл, а не ветка внутри <c>UpdateFeature</c>.</b> Ветка там выбирается по
/// <c>entity.type</c> — измеренному номеру признака в дереве. Для массива этот номер в сеансе B4 не
/// измерялся, и выдумывать его нельзя: ошибка здесь означала бы, что правка «не находит» признак
/// ровно так же, как это было у отверстия (искали по 52, а признак лежит под 583). Поэтому
/// признак массива опознаётся НЕ по номеру, а тем же прибором, что и чтение
/// (<see cref="Api5Session.PatternRead"/>): сопоставлением с элементом
/// <c>IModelContainer.FeaturePatterns</c> по имени оболочки дерева и штампу обновления.
/// </para>
/// <para>
/// <b>Что считается доказательством.</b> <c>Update() = true</c> — «принято», а не «применено».
/// Поэтому после перестроения признак ЧИТАЕТСЯ ОБРАТНО (<c>Api7Pattern.ReadPattern</c>), и каждому
/// запрошенному члену соответствует отдельная проверка <c>read_back_&lt;член&gt;</c>, сверяющая
/// записанное число с тем, что модель отдаёт. Сверх этого — объём документа и число тел, если
/// вызывающий дал аналитическое ожидание: объём подтверждает применение геометрически, а не по
/// возврату сеттера.
/// </para>
/// <para>
/// <b>Смена опоры не выполняется.</b> <c>Axis1/Axis2</c>, <c>Axis</c> и <c>Plane</c> принимают
/// <c>IModelObject</c>, а не ссылку сервера; ни один прогон B4 смену опоры существующего массива не
/// измерял. Вызов, где такая смена подразумевалась бы, отвергается до мутации, а не выполняется
/// частично.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Имя семейства правки для признаков массивов.</summary>
    private const string PatternFamily = "pattern";

    /// <summary>
    /// Правка признака массива: перезапись запрошенных членов в ЖИВОЙ объект коллекции, затем
    /// <c>Update()</c>, перестроение и чтение модели обратно.
    /// </summary>
    private UpdateFeatureResult UpdatePattern(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        var edit = command.Pattern
            ?? throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Ветка правки массива выбрана без поля pattern: это внутреннее противоречие вызова.",
                RetryPolicy.Never);

        // Смешение семейств отвергается ДО мутации: «применилось одно из двух» неотличимо потом от
        // «применилось и то, и другое». Массив не имеет ни глубины, ни радиуса, ни эскиза, ни
        // плоскости, поэтому любое из этих полей в одном вызове с pattern — ошибка вызывающего.
        var foreign = ForeignFamilyFields(command);
        if (foreign.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поле pattern не сочетается с полями других семейств: в вызове заданы " +
                string.Join(", ", foreign) + ". Массив правится только через pattern, и признак не " +
                "изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = PatternFamily, ["foreign_fields"] = foreign });
        }

        ValidatePatternEdit(edit);

        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);

        var (index, before) = MatchPatternForEdit(bridge, container, entity, document, command.FeatureRef);

        // Семейство берётся с ЖИВОГО объекта (ответ на QI), а не из памяти вызывающего: «каким его
        // создавали» — не факт о том, чем объект отвечает сейчас.
        ValidatePatternEditForFamily(edit, before.Family);

        var written = Api7Pattern.TryEdit(PatternAt(container, index), edit);
        if (!written.Applied)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка массива не подтверждена: " + (written.Failure ?? "причина не сообщена") +
                ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        // Порядок «запись → Update() → Rebuild» — часть контракта маршрута: без перестроения запись
        // в API7 остаётся представлением (тот же урок, что у вращения и у скругления).
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "pattern.update");

        var after = Api7Pattern.ReadPattern(PatternAt(container, index));
        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var checks = new List<NamedCheck>(written.Writes);
        checks.AddRange(ReadBackChecks(edit, after));

        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;
        checks.Add(new NamedCheck(
            "same_feature",
            sameFeature,
            Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», " +
                      $"updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
            Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"));

        var volumeMatched = false;
        if (command.Pattern!.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            volumeMatched = Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
            checks.Add(new NamedCheck(
                "volume_after_update",
                volumeMatched,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expected.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            // Отсутствие ожидания не «проходит по умолчанию»: без аналитики правка подтверждена
            // только чтением параметров, и это сказано прямо, а не спрятано в пустое поле.
            checks.Add(new NamedCheck(
                "volume_after_update",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                          ?? "не читается",
                Expected: "не задано"));
        }

        var bodyCountMatched = true;
        if (command.Pattern.ExpectedBodyCount is int expectedBodies)
        {
            bodyCountMatched = bodiesAfter == expectedBodies;
            checks.Add(new NamedCheck(
                "body_count_after_update",
                bodyCountMatched,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Expected: expectedBodies.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var readBackOk = checks
            .Where(c => c.Name.StartsWith("read_back_", StringComparison.Ordinal))
            .All(c => c.Passed);

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
            "support_not_edited — ось массива и плоскость зеркала этим вызовом не меняются: " +
            "ILinearPattern.Axis1/Axis2, ICircularPattern.Axis и IMirrorPattern.Plane принимают " +
            "IModelObject, а смена опоры существующего массива не измерялась",
        };
        if (command.Pattern.ExpectedVolumeMm3 is null)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма применение правки " +
                "подтверждается только чтением параметров");
        }

        if (!readBackOk)
        {
            unverified.Insert(0,
                "parameter_not_read_back — запись принята сеттером, но модель отдаёт другое значение");
        }

        if (!volumeMatched)
        {
            unverified.Insert(0,
                "volume_not_as_expected — параметры перечитаны, но измерение объёма не совпало с ожиданием");
        }

        var geometryConfirmed = readBackOk && sameFeature && volumeMatched && bodyCountMatched;

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            PatternFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            // Глубины и условия конца у массива нет: эти поля относятся к выдавливанию.
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>
    /// Живой объект массива по индексу коллекции.
    /// </summary>
    /// <remarks>
    /// Объект НЕ кэшируется между вызовами: адрес COM-объекта не переживает перестроения, и
    /// сохранённый объект правил бы уже не тот признак. Индекс берётся из сопоставления, сделанного в
    /// этом же вызове, до записи, — коллекция между сопоставлением и записью не менялась.
    /// </remarks>
    private static IFeaturePattern PatternAt(IModelContainer container, int index) =>
        container.FeaturePatterns?.FeaturePattern[index] as IFeaturePattern
        ?? throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            $"Элемент FeaturePatterns[{index}] не читается как признак массива.",
            RetryPolicy.ReacquireContext);

    /// <summary>
    /// Сопоставить признак дерева с элементом коллекции массивов — тем же прибором, что и чтение.
    /// </summary>
    /// <remarks>
    /// Несопоставление — это НЕ «признак не найден», а отказ с названной причиной: ссылка может
    /// указывать на признак, который коллекция массивов не показывает (например, признак был
    /// откатан, или ссылка выдана другому документу). Различать эти исходы обязан вызывающий, а
    /// молчаливая запись «в первый попавшийся» изменила бы чужой признак.
    /// </remarks>
    private (int Index, PatternReadout Readout) MatchPatternForEdit(
        Api7Bridge bridge,
        IModelContainer container,
        ksEntity entity,
        DocumentEntry document,
        string featureRef)
    {
        var count = Api7Pattern.Count(container) ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (!MatchesEntity(bridge, container, i, entity, document))
            {
                continue;
            }

            var readout = Api7Pattern.Read(container, i);
            if (readout is null)
            {
                continue;
            }

            return (i, readout);
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Признак '{featureRef}' не сопоставлен ни с одним элементом IModelContainer.FeaturePatterns " +
            $"(элементов {count}). Правка массива адресуется признаком дерева, и записывать параметры " +
            "«в первый попавшийся» означало бы изменить чужой признак. Признак не изменён.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["pattern_count"] = count });
    }

    /// <summary>
    /// Проверки чтения обратно: каждому ЗАПРОШЕННОМУ члену — своя проверка, а не одна общая.
    /// </summary>
    /// <remarks>
    /// Общая проверка «параметры совпали» не различала бы, какой именно член не применился, и
    /// строка приёмки не могла бы назвать причину. Сверяются только запрошенные члены: незаданный
    /// член остаётся прежним, и требовать от него нового значения было бы требованием к тому, чего
    /// вызов не просил.
    /// </remarks>
    private static IEnumerable<NamedCheck> ReadBackChecks(PatternEditDto edit, PatternReadout? after)
    {
        // Локальные функции НЕ перегружаются по типу параметра (ошибка CS0128, поймана сборкой):
        // поэтому у трёх видов величины три разных имени, а не одно.
        static string SD(double? v) => v?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            ?? "не читается";

        static string SI(int? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? "не читается";

        static string SB(bool? v) => v?.ToString() ?? "не читается";

        static bool Same(double? got, double? want) =>
            got is double g && want is double w && Math.Abs(g - w) <= 1e-9;

        static bool SameInt(int? got, int? want) => got is int g && want is int w && g == w;

        static bool SameBool(bool? got, bool? want) => got is bool g && want is bool w && g == want;

        if (edit.Count1 is int c1)
        {
            yield return new NamedCheck("read_back_count1", SameInt(after?.Count1, c1),
                Observed: SI(after?.Count1), Expected: SI(c1));
        }

        if (edit.Count2 is int c2)
        {
            yield return new NamedCheck("read_back_count2", SameInt(after?.Count2, c2),
                Observed: SI(after?.Count2), Expected: SI(c2));
        }

        if (edit.Step1Mm is double s1)
        {
            yield return new NamedCheck("read_back_step1", Same(after?.Step1, s1),
                Observed: SD(after?.Step1), Expected: SD(s1));
        }

        if (edit.Step2Mm is double s2)
        {
            yield return new NamedCheck("read_back_step2", Same(after?.Step2, s2),
                Observed: SD(after?.Step2), Expected: SD(s2));
        }

        if (edit.Step2Deg is double s2d)
        {
            yield return new NamedCheck("read_back_step2", Same(after?.Step2, s2d),
                Observed: SD(after?.Step2), Expected: SD(s2d));
        }

        if (edit.Angle1Deg is double a1)
        {
            yield return new NamedCheck("read_back_angle1", Same(after?.Angle1Deg, a1),
                Observed: SD(after?.Angle1Deg), Expected: SD(a1));
        }

        if (edit.Angle2Deg is double a2)
        {
            yield return new NamedCheck("read_back_angle2", Same(after?.Angle2Deg, a2),
                Observed: SD(after?.Angle2Deg), Expected: SD(a2));
        }

        if (edit.Direction1 is bool d1)
        {
            yield return new NamedCheck("read_back_direction1", SameBool(after?.Direction1, d1),
                Observed: SB(after?.Direction1), Expected: SB(d1));
        }

        if (edit.Direction2 is bool d2)
        {
            yield return new NamedCheck("read_back_direction2", SameBool(after?.Direction2, d2),
                Observed: SB(after?.Direction2), Expected: SB(d2));
        }

        if (edit.BuildingType is string bt)
        {
            yield return new NamedCheck("read_back_building_type",
                string.Equals(after?.BuildingType, bt, StringComparison.OrdinalIgnoreCase),
                Observed: after?.BuildingType ?? "не читается", Expected: bt);
        }

        if (edit.StepByAxisMm is double sba)
        {
            yield return new NamedCheck("read_back_step_by_axis", Same(after?.StepByAxis, sba),
                Observed: SD(after?.StepByAxis), Expected: SD(sba));
        }

        if (edit.ReverseDirection is bool rd)
        {
            yield return new NamedCheck("read_back_reverse_direction",
                SameBool(after?.ReverseDirection, rd),
                Observed: SB(after?.ReverseDirection), Expected: SB(rd));
        }

        if (edit.SaveInitialOrientation is bool sOrientation)
        {
            yield return new NamedCheck("read_back_save_initial_orientation",
                SameBool(after?.SaveInitialOrientation, sOrientation),
                Observed: SB(after?.SaveInitialOrientation), Expected: SB(sOrientation));
        }

        if (edit.SaveInitialObjects is bool sObjects)
        {
            yield return new NamedCheck("read_back_save_initial_objects",
                SameBool(after?.SaveInitialObjects, sObjects),
                Observed: SB(after?.SaveInitialObjects), Expected: SB(sObjects));
        }
    }

    /// <summary>Поля других семейств, заданные в одном вызове с <c>pattern</c>.</summary>
    private static List<string> ForeignFamilyFields(UpdateFeatureCommand command)
    {
        var names = new List<string>();
        if (command.DepthMm is not null)
        {
            names.Add("depth_mm");
        }

        if (command.EndCondition is not null)
        {
            names.Add("end_condition");
        }

        if (command.SketchRef is not null)
        {
            names.Add("sketch_ref");
        }

        if (command.Distance1Mm is not null)
        {
            names.Add("distance1_mm");
        }

        if (command.Distance2Mm is not null)
        {
            names.Add("distance2_mm");
        }

        if (command.AngleDeg is not null)
        {
            names.Add("angle_deg");
        }

        if (command.Direction is not null)
        {
            names.Add("direction");
        }

        if (command.RotationAngleDeg is not null)
        {
            names.Add("rotation_angle_deg");
        }

        if (command.RotationDirection is not null)
        {
            names.Add("rotation_direction");
        }

        if (command.RepositionKind is not null)
        {
            names.Add("reposition_kind");
        }

        if (command.RepositionVectorMm is not null)
        {
            names.Add("reposition_vector_mm");
        }

        if (command.RepositionAxisPointMm is not null)
        {
            names.Add("reposition_axis_point_mm");
        }

        if (command.RepositionAxisDirectionMm is not null)
        {
            names.Add("reposition_axis_direction_mm");
        }

        if (command.RepositionAxisPoint2Mm is not null)
        {
            names.Add("reposition_axis_point2_mm");
        }

        if (command.RepositionAngleDeg is not null)
        {
            names.Add("reposition_angle_deg");
        }

        if (command.Plane is not null)
        {
            names.Add("plane");
        }

        if (command.KeepSide is not null)
        {
            names.Add("keep_side");
        }

        if (command.Operation is not null)
        {
            names.Add("operation");
        }

        if (command.RadiusMm is not null)
        {
            names.Add("radius_mm");
        }

        if (command.EdgeRefs is not null)
        {
            names.Add("edge_refs");
        }

        if (command.BaseObjectRefs is not null)
        {
            names.Add("base_object_refs");
        }

        if (command.ExpectedPartVolumesMm3 is not null)
        {
            names.Add("expected_part_volumes_mm3");
        }

        if (command.TargetBodyRef is not null)
        {
            names.Add("target_body_ref");
        }

        // Поля последней очереди B5. Без них вызов «pattern + shift_mode» был бы ПРИНЯТ, а
        // shift_mode проглочен: ветка B5 выбирается по самому полю, но ветка массива стоит раньше и
        // уводит вызов в себя. `couplings` дописан 20.09.2026: очередь B5 добавила шесть правимых
        // полей, и здесь были перечислены пять из них — шестое проходило молча.
        if (command.ShiftMode is not null)
        {
            names.Add("shift_mode");
        }

        if (command.SectionRefs is not null)
        {
            names.Add("section_refs");
        }

        if (command.Couplings is not null)
        {
            names.Add("couplings");
        }

        if (command.ThicknessMm is not null)
        {
            names.Add("thickness_mm");
        }

        if (command.ThinInward is not null)
        {
            names.Add("thin_inward");
        }

        if (command.FaceRefs is not null)
        {
            names.Add("face_refs");
        }

        // Поля семейства ОТВЕРСТИЯ (наряд SM07 §3.2). Перечислены здесь по той же причине, что и
        // поля B5: ветка массива стоит РАНЬШЕ ветки отверстия, поэтому «pattern + diameter_mm» был бы
        // уведён в массив, а диаметр проглочен — принятое и не применённое число.
        if (command.DiameterMm is not null)
        {
            names.Add("diameter_mm");
        }

        if (command.CounterboreDiameterMm is not null)
        {
            names.Add("counterbore_diameter_mm");
        }

        if (command.CounterboreDepthMm is not null)
        {
            names.Add("counterbore_depth_mm");
        }

        if (command.CountersinkDiameterMm is not null)
        {
            names.Add("countersink_diameter_mm");
        }

        if (command.CountersinkAngleDeg is not null)
        {
            names.Add("countersink_angle_deg");
        }

        if (command.ExpectedVolumeDeltaMm3 is not null)
        {
            names.Add("expected_volume_delta_mm3");
        }

        return names;
    }

    /// <summary>Значения правки, не зависящие от семейства: счётные величины и знаки шагов.</summary>
    private static void ValidatePatternEdit(PatternEditDto edit)
    {
        var any = edit.Count1 is not null || edit.Count2 is not null || edit.Step1Mm is not null
                  || edit.Step2Mm is not null || edit.Step2Deg is not null || edit.Angle1Deg is not null
                  || edit.Angle2Deg is not null || edit.Direction1 is not null || edit.Direction2 is not null
                  || edit.BuildingType is not null || edit.StepByAxisMm is not null
                  || edit.ReverseDirection is not null || edit.SaveInitialOrientation is not null
                  || edit.SaveInitialObjects is not null;
        if (!any)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "В pattern не задано ни одного параметра: правка не выполнялась, признак не изменён. " +
                "Ожидается хотя бы один из count1, count2, step1_mm, step2_mm, step2_deg, angle1_deg, " +
                "angle2_deg, direction1, direction2, building_type, step_by_axis_mm, reverse_direction, " +
                "save_initial_orientation, save_initial_objects.",
                RetryPolicy.Never);
        }

        if (edit.Count1 is int c1 && c1 < 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "count1 меньше 1: массив без экземпляров не существует, признак не изменён.",
                RetryPolicy.Never);
        }

        if (edit.Count2 is int c2 && c2 < 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "count2 меньше 1: массив без экземпляров не существует, признак не изменён.",
                RetryPolicy.Never);
        }

        if (edit.Step1Mm is double s1 && s1 < 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "step1_mm отрицателен: направление задаётся полем direction, а не знаком шага.",
                RetryPolicy.Never);
        }

        if (edit.Step2Mm is double s2 && s2 < 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "step2_mm отрицателен: направление задаётся полем direction2, а не знаком шага.",
                RetryPolicy.Never);
        }

        if (edit.Step2Deg is double s2d && s2d <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "step2_deg не положителен: нулевой угловой шаг кладёт все экземпляры кольца друг на " +
                "друга, и это не массив, а одна позиция. Признак не изменён.",
                RetryPolicy.Never);
        }

        if (edit.StepByAxisMm is double sba && sba < 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "step_by_axis_mm отрицателен: знак шага вдоль оси задаётся полем reverse_direction.",
                RetryPolicy.Never);
        }
    }

    /// <summary>
    /// Члены, которых у ЭТОГО семейства нет, отвергаются до мутации.
    /// </summary>
    /// <remarks>
    /// Разные семейства массивов — разные интерфейсы API7, и часть имён между ними совпадает только
    /// по виду. Отдать сетке <c>save_initial_orientation</c> и промолчать о том, что член не
    /// применён, — значит соврать про правку; записать <c>step2_deg</c> в <c>ILinearPattern.Step2</c>
    /// (где это МИЛЛИМЕТРЫ) — значит изменить геометрию другой величиной, чем названа в запросе.
    /// Поэтому проверка называет конкретный неподходящий член.
    /// </remarks>
    private static void ValidatePatternEditForFamily(PatternEditDto edit, string family)
    {
        var bad = new List<string>();
        switch (family)
        {
            case "linear":
                if (edit.Step2Deg is not null)
                {
                    bad.Add("step2_deg");
                }

                if (edit.StepByAxisMm is not null)
                {
                    bad.Add("step_by_axis_mm");
                }

                if (edit.ReverseDirection is not null)
                {
                    bad.Add("reverse_direction");
                }

                if (edit.SaveInitialOrientation is not null)
                {
                    bad.Add("save_initial_orientation");
                }

                if (edit.SaveInitialObjects is not null)
                {
                    bad.Add("save_initial_objects");
                }

                break;

            case "circular":
                if (edit.Step2Mm is not null)
                {
                    bad.Add("step2_mm");
                }

                if (edit.Angle1Deg is not null)
                {
                    bad.Add("angle1_deg");
                }

                if (edit.Angle2Deg is not null)
                {
                    bad.Add("angle2_deg");
                }

                if (edit.Direction1 is not null)
                {
                    bad.Add("direction1");
                }

                if (edit.Direction2 is not null)
                {
                    bad.Add("direction2");
                }

                if (edit.SaveInitialObjects is not null)
                {
                    bad.Add("save_initial_objects");
                }

                break;

            case "mirror":
                if (edit.Count1 is not null)
                {
                    bad.Add("count1");
                }

                if (edit.Count2 is not null)
                {
                    bad.Add("count2");
                }

                if (edit.Step1Mm is not null)
                {
                    bad.Add("step1_mm");
                }

                if (edit.Step2Mm is not null)
                {
                    bad.Add("step2_mm");
                }

                if (edit.Step2Deg is not null)
                {
                    bad.Add("step2_deg");
                }

                if (edit.Angle1Deg is not null)
                {
                    bad.Add("angle1_deg");
                }

                if (edit.Angle2Deg is not null)
                {
                    bad.Add("angle2_deg");
                }

                if (edit.Direction1 is not null)
                {
                    bad.Add("direction1");
                }

                if (edit.Direction2 is not null)
                {
                    bad.Add("direction2");
                }

                if (edit.BuildingType is not null)
                {
                    bad.Add("building_type");
                }

                if (edit.StepByAxisMm is not null)
                {
                    bad.Add("step_by_axis_mm");
                }

                if (edit.ReverseDirection is not null)
                {
                    bad.Add("reverse_direction");
                }

                if (edit.SaveInitialOrientation is not null)
                {
                    bad.Add("save_initial_orientation");
                }

                break;

            default:
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"Признак массива опознан как '{family}': правимых параметров у этого вида не " +
                    "объявлено, признак не изменён.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["family"] = family });
        }

        if (bad.Count > 0)
        {
            var known = family switch
            {
                "linear" => "count1, count2, step1_mm, step2_mm, angle1_deg, angle2_deg, direction1, " +
                            "direction2, building_type",
                "circular" => "count1, count2, step1_mm, step2_deg, building_type, step_by_axis_mm, " +
                              "reverse_direction, save_initial_orientation",
                _ => "save_initial_objects",
            };
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Поля " + string.Join(", ", bad) + " к массиву семейства " + family +
                " не применяются: у него нет таких членов. Меняемые параметры этого семейства — " +
                known + ". Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["family"] = family,
                    ["inapplicable_fields"] = bad,
                });
        }
    }
}

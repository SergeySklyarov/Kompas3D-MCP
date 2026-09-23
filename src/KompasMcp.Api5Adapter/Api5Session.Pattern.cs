using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Массивы по сетке и по концентрической сетке, зеркальный массив (docs/05 SM-18 / SM-19 / SM-23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут один и он опубликован.</b> <c>IModelContainer.FeaturePatterns.Add(ksObj3dTypeEnum)</c>
/// → <c>QI(ILinearPattern | ICircularPattern | IMirrorPattern)</c> → запись параметров →
/// <c>Update()</c> → <c>Rebuild</c>. Соответствие «тип → интерфейс» взято со страницы справки SDK
/// <c>copytype.html</c>, открытой по проводу в этой работе, а не выведено по аналогии с вращением.
/// </para>
/// <para>
/// <b>Ось строится в ТОЙ ЖЕ детали по двум точкам модели.</b> Массивы принимают
/// <c>IModelObject</c>, а не ссылку, и ссылка из чужого документа сюда не протаскивается. Маршрут
/// построения оси — <c>IAuxiliaryGeomContainer.Axes3D.Add(o3d_axis2Points)</c> — уже измерен
/// вращением (SM-03) и здесь переиспользуется как ИЗМЕРЕННЫЙ, а не как предположенный.
/// </para>
/// <para>
/// <b>Что здесь считается доказательством.</b> Возврат <c>Update() = true</c> — это «принято», а не
/// «применено». Поэтому после перестроения модель ЧИТАЕТСЯ обратно: параметры признака, число
/// экземпляров (<c>GetExemplarsCounts</c>), объём документа, число тел и — для массивов отверстий —
/// ПОИМЁННЫЙ набор цилиндрических граней с координатами их осей. Последнее и есть проверка «по
/// каждому экземпляру»: объём не отличает четыре отверстия от трёх плюс одно наложенное.
/// </para>
/// <para>
/// <b>Соотнесение направлений кругового массива взято со страницы справки.</b>
/// <c>icircularpattern_props.html</c> называет <c>Count1</c>/<c>Step1</c> РАДИАЛЬНЫМИ, а
/// <c>Count2</c>/<c>Step2</c> — КОЛЬЦЕВЫМИ, причём <c>Step2</c> подписан как «Угловой шаг
/// (градусы)». Это закрывает OQ-B-02 и опровергает ожидание, записанное в наряде (§6.3, §8), где
/// кольцевым считалось первое направление. Под сомнение поставлено ОЖИДАНИЕ, а не измерение.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Допуск сопоставления цилиндрической грани с ожидаемым радиусом отверстия, мм.</summary>
    private const double PatternHoleRadiusToleranceMm = 0.01d;

    /// <summary>Допуск сопоставления высоты цилиндра с толщиной пластины, мм.</summary>
    private const double PatternHoleHeightToleranceMm = 0.01d;

    /// <summary>Массив по сетке (SM-18).</summary>
    public PatternResult PatternGrid(PatternGridCommand command)
    {
        ValidatePatternGridCommand(command);

        var document = RequireDocument(command.DocumentId);
        var part = document.PartNow();
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var holesBefore = ReadHoleAxes(part, command.ExpectedHoleRadiusMm, command.ExpectedHoleHeightMm);

        var sources7 = ResolvePatternSources(bridge, document, part, command.SourceRefs, command.CopyKind);

        var axis1 = Api7Rotated.TryBuildAxisBy2Points(
            bridge, part, command.Axis1Point1Mm.ToArray(), command.Axis1Point2Mm.ToArray());
        if (axis1 is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Первая ось массива не построена в собственной детали — признак не создавался. " +
                "Массив без оси не собирается вовсе, поэтому ось здесь не улучшение, а условие постановки.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        IModelObject? axis2 = null;
        var axisNotes = new List<string>(axis1.Notes);
        if (command.Axis2Point1Mm is not null && command.Axis2Point2Mm is not null && command.Count2 is > 1)
        {
            var second = Api7Rotated.TryBuildAxisBy2Points(
                bridge, part, command.Axis2Point1Mm.ToArray(), command.Axis2Point2Mm.ToArray());
            if (second is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Вторая ось сетки объявлена, но не построена — признак не создавался: " +
                    "сетка по второму направлению без второй оси не собирается.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true);
            }

            axis2 = second.Axis;
            axisNotes.AddRange(second.Notes);
        }

        var (pattern, failure) = Api7Pattern.TryCreateLinear(
            container,
            command.CopyKind,
            sources7,
            axis1.Axis,
            command.Step1Mm,
            command.Count1,
            command.Angle1Deg,
            command.Direction1,
            command.BoundaryInstancesStepFactor1,
            axis2,
            command.Step2Mm ?? 0d,
            command.Count2 ?? 1,
            command.Angle2Deg,
            command.Direction2,
            command.BoundaryInstancesStepFactor2,
            command.BuildingType,
            command.GeometryPattern);

        return FinishPattern(
            document,
            part,
            container,
            pattern,
            failure,
            "linear",
            volumeBefore,
            bodiesBefore,
            holesBefore,
            command.ExpectedVolumeMm3,
            command.ExpectedBodyCount,
            command.ExpectedHoleCount,
            command.ExpectedHoleRadiusMm,
            command.ExpectedHoleHeightMm,
            command.ExpectedHoleCentersMm,
            axisNotes);
    }

    /// <summary>Массив по концентрической сетке (SM-19).</summary>
    public PatternResult PatternCircular(PatternCircularCommand command)
    {
        ValidatePatternCircularCommand(command);

        var document = RequireDocument(command.DocumentId);
        var part = document.PartNow();
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var holesBefore = ReadHoleAxes(part, command.ExpectedHoleRadiusMm, command.ExpectedHoleHeightMm);

        var sources7 = ResolvePatternSources(bridge, document, part, command.SourceRefs, command.CopyKind);

        var axis = Api7Rotated.TryBuildAxisBy2Points(
            bridge, part, command.AxisPoint1Mm.ToArray(), command.AxisPoint2Mm.ToArray());
        if (axis is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Ось кругового массива не построена в собственной детали — признак не создавался.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var (pattern, failure) = Api7Pattern.TryCreateCircular(
            container,
            command.CopyKind,
            sources7,
            axis.Axis,
            command.Count1,
            command.Step1Mm,
            command.Count2,
            command.Step2Deg,
            command.StepByAxisMm,
            command.BoundaryInstancesStepFactor1,
            command.BoundaryInstancesStepFactor2,
            command.ReverseDirection,
            command.SaveInitialOrientation,
            command.BuildingType,
            command.GeometryPattern);

        return FinishPattern(
            document,
            part,
            container,
            pattern,
            failure,
            "circular",
            volumeBefore,
            bodiesBefore,
            holesBefore,
            command.ExpectedVolumeMm3,
            command.ExpectedBodyCount,
            command.ExpectedHoleCount,
            command.ExpectedHoleRadiusMm,
            command.ExpectedHoleHeightMm,
            command.ExpectedHoleCentersMm,
            axis.Notes);
    }

    /// <summary>Зеркальный массив (SM-23).</summary>
    public PatternResult PatternMirror(PatternMirrorCommand command)
    {
        ValidatePatternMirrorCommand(command);

        var document = RequireDocument(command.DocumentId);
        var part = document.PartNow();
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var bodiesBeforeSnapshot = ReadBodySnapshots(part);
        var holesBefore = ReadHoleAxes(part, command.ExpectedHoleRadiusMm, command.ExpectedHoleHeightMm);

        // ЧТО ИМЕННО ИСХОДНИК — ЗАВИСИТ ОТ РЕЖИМА, и это измерено прогоном строки B4M.10:
        // selected_operations отражает ОПЕРАЦИИ, all_bodies — ТЕЛА. Пока вид был один и тот же
        // (Operations), режим «по всем телам» был неисполним ДВУМЯ разными способами: пустой список
        // создавал признак, но не отражал ни одного тела (тел остаётся 2, объём 8000), а явные
        // body:-ссылки отвергались STALE_REFERENCE с сообщением «указывает на __ComObject вместо
        // признака» — потому что ссылки на тела разбирались как ссылки на признаки. Контракт
        // инструмента при этом с самого начала обещал для all_bodies именно body:-ссылки.
        var sources7 = ResolvePatternSources(
            bridge, document, part, command.SourceRefs,
            command.Mode == PatternMirrorMode.AllBodies
                ? PatternCopyKind.Bodies
                : PatternCopyKind.Operations,
            allowEmpty: command.Mode == PatternMirrorMode.AllBodies);

        var planeEntity = ResolvePlaneEntity(document, command.Plane);
        var plane7 = bridge.TransferTo7(planeEntity) as IModelObject;
        if (plane7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Плоскость симметрии не перенесена в API7 (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                ") — зеркальный массив не создавался. Плоскость подаётся ЯВНЫМ объектом, а не текущим " +
                "выделением окна: это требование зависимости dep.refs.planes.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var (pattern, failure, notes) = Api7Pattern.TryCreateMirror(
            container,
            command.Mode,
            sources7,
            plane7,
            command.SaveInitialObjects,
            command.ChooseBodiesType);

        var result = FinishPattern(
            document,
            part,
            container,
            pattern,
            failure,
            "mirror",
            volumeBefore,
            bodiesBefore,
            holesBefore,
            command.ExpectedVolumeMm3,
            command.ExpectedBodyCount,
            command.ExpectedHoleCount,
            command.ExpectedHoleRadiusMm,
            command.ExpectedHoleHeightMm,
            command.ExpectedHoleCentersMm,
            notes);

        // Сохранность постороннего тела: тело, не выбранное ни исходником, ни зеркалом, обязано
        // остаться неизменным по объёму и габариту. «Суммарный объём вырос вдвое» этого не
        // доказывает — оно не отличает удвоение тел от удвоения одного тела (§9.3 наряда).
        var bodiesAfterSnapshot = ReadBodySnapshots(part);
        var comparison = CompareBodySnapshots(bodiesBeforeSnapshot, bodiesAfterSnapshot);
        var touched = comparison.Touched.ToHashSet();
        var untouched = bodiesBeforeSnapshot
            .Select(b => b.Index)
            .Where(i => !touched.Contains(i))
            .OrderBy(i => i)
            .ToArray();
        return result with
        {
            BodyRows = comparison.Rows,
            UntouchedBodyIndexes = untouched,
        };
    }

    /// <summary>Перечитать параметры массива либо зеркала по ссылке на признак.</summary>
    public PatternReadResult PatternRead(PatternReadCommand command)
    {
        var (document, entity) = RequireFeatureEntity(command.FeatureRef);
        var bridge = BridgeFor(document);
        var container = RequirePatternContainer(bridge, document);

        var count = Api7Pattern.Count(container);
        PatternReadout? readout = null;
        var matched = -1;
        if (count is int n)
        {
            for (var i = 0; i < n; i++)
            {
                var candidate = Api7Pattern.Read(container, i);
                if (candidate is null)
                {
                    continue;
                }

                if (MatchesEntity(bridge, container, i, entity, document))
                {
                    readout = candidate;
                    matched = i;
                    break;
                }
            }
        }

        return new PatternReadResult(
            Family: readout?.Family ?? "not_matched",
            PatternIndex: matched >= 0 ? matched : null,
            PatternCount: count,
            Readout: readout,
            DeletedInstancesApplicable: readout?.Family != "mirror",
            Notes: readout is null
                ? new[]
                {
                    "Признак не сопоставлен ни с одним элементом IModelContainer.FeaturePatterns: " +
                    "ссылка указывает на признак, который коллекция массивов не показывает.",
                }
                : Array.Empty<string>());
    }

    /// <summary>
    /// Сопоставление признака дерева с элементом коллекции массивов по <c>Owner.Name</c> и
    /// <c>UpdateStamp</c>: индекс коллекции адресом не является (КОМПАС переставляет элементы), а
    /// совпадение имени без штампа не различает два одноимённых признака.
    /// </summary>
    private bool MatchesEntity(Api7Bridge bridge, IModelContainer container, int index, ksEntity entity, DocumentEntry document)
    {
        try
        {
            if (container.FeaturePatterns?.FeaturePattern[index] is not { } pattern)
            {
                return false;
            }

            var name = pattern.Owner?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            // Имя признака читается из ОБОЛОЧКИ дерева: ksFeature.Name не существует
            // (ошибка компиляции, пойманная сборкой), а имя лежит у ksEntity.name.
            var treeName = entity.name ?? (entity.GetFeature() as ksFeature)?.name;
            return !string.IsNullOrEmpty(treeName)
                && string.Equals(name, treeName, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>
    /// Общий хвост создания массива: перестроение, перечитывание, проверки и сборка ответа.
    /// </summary>
    /// <remarks>
    /// Хвост вынесен потому, что три семейства отличаются ТОЛЬКО постановкой, а доказательство у
    /// них общее: <c>Update() = true</c> — это «принято», и без перечитывания модели ответ не
    /// отличал бы «построено» от «записано».
    /// </remarks>
    private PatternResult FinishPattern(
        DocumentEntry document,
        ksPart part,
        IModelContainer container,
        IFeaturePattern? pattern,
        string? failure,
        string family,
        double? volumeBefore,
        int bodiesBefore,
        IReadOnlyList<HoleAxis> holesBefore,
        double? expectedVolume,
        int? expectedBodies,
        int? expectedHoles,
        double? holeRadius,
        double? holeHeight,
        IReadOnlyList<IReadOnlyList<double>>? expectedCenters,
        IReadOnlyList<string> routeNotes)
    {
        if (pattern is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Массив не создан фабрикой API7 (семейство {family}): " + (failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = failure });
        }

        // Без перестроения запись в API7 остаётся представлением: порядок «запись → Update() →
        // Rebuild()» измерен на вращении и здесь тот же.
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "pattern." + family);

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var readout = Api7Pattern.ReadPattern(pattern);
        var holesAfter = ReadHoleAxes(part, holeRadius, holeHeight);
        var bounds = SafeBounds(part);

        var reference = References.Register("feature", document.Id, document.Revision, pattern);

        var checks = new List<NamedCheck>
        {
            new("pattern_created", true,
                Observed: $"семейство {readout.Family}, тип {readout.TypeName ?? "не прочитан"}",
                Expected: "признак создан фабрикой FeaturePatterns и перестроен"),
            new("initial_objects_bound", readout.InitialObjectCount is > 0,
                Observed: $"исходных объектов {Num(readout.InitialObjectCount)}",
                Expected: "исходные объекты привязаны к признаку"),
            new("exemplar_counts_read", readout.Count1 is not null && readout.Count2 is not null,
                Observed: $"экземпляров {Num(readout.Count1)}×{Num(readout.Count2)}",
                Expected: "GetExemplarsCounts отдал число экземпляров"),
        };

        // Число тел: массив ОПЕРАЦИЙ новых тел не создаёт (справка
        // 48_2_osobennoiti_postroeniy_massiviv_v_mnogotelnoy_detali), массив ТЕЛ — создаёт.
        if (expectedBodies is int wanted)
        {
            checks.Add(new NamedCheck(
                "body_count",
                bodiesAfter == wanted,
                Observed: $"{bodiesBefore}→{bodiesAfter}",
                Expected: $"после операции тел ровно {wanted} (счётная величина, допуск не применяется)"));
        }

        // Объём документа. Сравнение — с аналитическим ожиданием, а не с предыдущим состоянием:
        // «стало больше» не отличает верную сетку от неверной.
        if (expectedVolume is double target && volumeAfter is double actual)
        {
            var delta = Math.Abs(actual - target);
            checks.Add(new NamedCheck(
                "document_volume",
                delta <= VolumeToleranceMm3(target),
                Observed: $"{Num(actual)} мм³ (расхождение {Num(delta)})",
                Expected: $"{Num(target)} мм³ в допуске {Num(VolumeToleranceMm3(target))}"));
        }

        // ПОИМЁННАЯ проверка экземпляров: набор осей цилиндрических граней. Объём не отличает
        // четыре отверстия от трёх плюс одно наложенное, а этот набор отличает.
        if (expectedHoles is int holesWanted)
        {
            checks.Add(new NamedCheck(
                "hole_count_per_instance",
                holesAfter.Count == holesWanted,
                Observed: $"{holesBefore.Count}→{holesAfter.Count} цилиндрических граней Ø{Num(holeRadius is double r ? r * 2 : null)}",
                Expected: $"ровно {holesWanted} (точное совпадение, допуск к счёту не применяется)"));
        }

        if (expectedCenters is not null)
        {
            var (matched, missing, extra) = MatchCenters(holesAfter, expectedCenters);
            checks.Add(new NamedCheck(
                "hole_centers_per_instance",
                missing.Count == 0 && extra.Count == 0,
                Observed: matched == 0
                    ? "оси цилиндрических граней не прочитаны"
                    : $"совпало {matched} из {expectedCenters.Count}; не найдено {missing.Count}, лишних {extra.Count}",
                Expected: "координаты осей каждого экземпляра совпадают с аналитическим набором"));
        }

        var unverified = new List<string>();
        if (expectedVolume is null)
        {
            unverified.Add("analytical_volume_not_declared");
        }

        if (expectedCenters is null && expectedHoles is not null)
        {
            unverified.Add("per_instance_coordinates_not_declared");
        }

        if (readout.Family == "mirror")
        {
            unverified.Add("mirror_has_no_skipped_instances");
        }

        return new PatternResult(
            FeatureRef: ToDto(reference, $"{family} pattern"),
            Family: family,
            Readout: readout,
            BodyCount: bodiesAfter,
            VolumeMm3: volumeAfter,
            BoundsMm: bounds,
            HoleAxes: holesAfter.Select(h => new PatternHoleDto(h.CenterMm, h.Radius, h.Height)).ToArray(),
            Checks: checks,
            RouteNotes: routeNotes,
            UnverifiedAspects: unverified);
    }

    /// <summary>
    /// Общий допуск объёма из <c>tolerance_classes</c> профиля: 0,01 мм³ абсолютный и 1e-6
    /// относительный, берётся БОЛЬШИЙ из двух. Допуск не подбирается после неудачи.
    /// </summary>
    private static double VolumeToleranceMm3(double target) =>
        Math.Max(0.01d, Math.Abs(target) * 1e-6);

    /// <summary>
    /// Сопоставить измеренные оси отверстий с аналитическим набором. Возвращаются три числа:
    /// сколько совпало, что не найдено и что лишнее — «совпало» без «лишнего» не отличает верную
    /// сетку от сетки с добавочным экземпляром.
    /// </summary>
    private static (int Matched, List<string> Missing, List<string> Extra) MatchCenters(
        IReadOnlyList<HoleAxis> measured,
        IReadOnlyList<IReadOnlyList<double>> expected)
    {
        var used = new bool[measured.Count];
        var matched = 0;
        var missing = new List<string>();
        foreach (var want in expected)
        {
            var hit = -1;
            for (var i = 0; i < measured.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var c = measured[i].CenterMm;
                if (Math.Abs(c[0] - want[0]) <= 0.01d
                    && Math.Abs(c[1] - want[1]) <= 0.01d
                    && Math.Abs(c[2] - want[2]) <= 0.01d)
                {
                    hit = i;
                    break;
                }
            }

            if (hit >= 0)
            {
                used[hit] = true;
                matched++;
            }
            else
            {
                missing.Add($"({Num(want[0])}, {Num(want[1])}, {Num(want[2])})");
            }
        }

        var extra = new List<string>();
        for (var i = 0; i < measured.Count; i++)
        {
            if (!used[i])
            {
                var c = measured[i].CenterMm;
                extra.Add($"({Num(c[0])}, {Num(c[1])}, {Num(c[2])})");
            }
        }

        return (matched, missing, extra);
    }

    /// <summary>
    /// Разрешить исходные объекты массива: <c>feature:</c>-ссылки для операций, <c>body:</c> — для
    /// тел. Смешивать виды в одном вызове нельзя: числовой тип фабрики выбирается один, и «массив
    /// тел из операций» не собирается.
    /// </summary>
    private object[] ResolvePatternSources(
        Api7Bridge bridge,
        DocumentEntry document,
        ksPart part,
        IReadOnlyList<string> sourceRefs,
        PatternCopyKind kind,
        bool allowEmpty = false)
    {
        if (sourceRefs.Count == 0)
        {
            if (allowEmpty)
            {
                // Пустой список здесь — НЕ отсутствие входа, а сам режим: «зеркально отразить все»
                // отражает все тела детали, и выбирать их поимённо не требуется. Это записано в
                // контракте (PatternMirrorCommand.SourceRefs), а не выведено из пустоты.
                return Array.Empty<object>();
            }

            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Массив без исходных объектов не собирается: список source_refs пуст.",
                RetryPolicy.Never);
        }

        var bodiesBefore = ReadBodySnapshots(part);
        var result = new List<object>(sourceRefs.Count);
        foreach (var reference in sourceRefs)
        {
            object? transferred;
            if (kind == PatternCopyKind.Bodies)
            {
                var target = ResolveBodyTarget(document, part, reference, bodiesBefore);
                transferred = bridge.TransferTo7(target.Body);
            }
            else
            {
                var (owner, entity) = RequireFeatureEntity(reference);
                if (!string.Equals(owner.Id, document.Id, StringComparison.Ordinal))
                {
                    throw new KompasContractException(
                        ErrorCodes.StaleReference,
                        $"Ссылка '{reference}' принадлежит другому документу — исходные объекты массива " +
                        "берутся только из той детали, в которой строится признак.",
                        RetryPolicy.ReacquireContext);
                }

                transferred = bridge.TransferTo7(entity);
            }

            if (transferred is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Исходный объект '{reference}' не перенесён в API7 (" +
                    (bridge.BridgeFailure ?? "TransferInterface вернул null") + ").",
                    RetryPolicy.ReacquireContext,
                    partialEffects: false);
            }

            result.Add(transferred);
        }

        return result.ToArray();
    }

    private IModelContainer RequirePatternContainer(Api7Bridge bridge, DocumentEntry document)
    {
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Массивы создаются фабрикой IModelContainer.FeaturePatterns, и подмены ей нет: " +
                "оболочка API5 (NewEntity + Create) на объектах фабрики API7 не строит ничего — " +
                "это измерено на вращении. Признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        return container;
    }

    /// <summary>
    /// Оси цилиндрических граней главного тела с ожидаемыми радиусом и высотой.
    /// </summary>
    /// <remarks>
    /// Это и есть измерение «по каждому экземпляру»: <c>ksCylinderParam.GetPlacement()</c> отдаёт
    /// размещение поверхности, а <c>ksPlacement.GetOrigin</c> — точку оси. Без координат проверка
    /// свелась бы к объёму, а объём не отличает N отверстий от N−1 отверстий и одного наложенного.
    /// <para>
    /// Радиус и высота фильтруются, а не берутся «все цилиндры»: иначе посторонняя цилиндрическая
    /// геометрия попала бы в счёт экземпляров и проверка стала бы неразличающей.
    /// </para>
    /// </remarks>
    private static List<HoleAxis> ReadHoleAxes(ksPart part, double? radius, double? height)
    {
        var found = new List<HoleAxis>();
        if (radius is not double wantRadius || height is not double wantHeight)
        {
            return found;
        }

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

                    if (Math.Abs(cylinder.radius - wantRadius) > PatternHoleRadiusToleranceMm
                        || Math.Abs(cylinder.height - wantHeight) > PatternHoleHeightToleranceMm)
                    {
                        continue;
                    }

                    if (cylinder.GetPlacement() is not ksPlacement placement
                        || !placement.GetOrigin(out var x, out var y, out var z))
                    {
                        continue;
                    }

                    found.Add(new HoleAxis(new[] { x, y, z }, cylinder.radius, cylinder.height));
                }
                catch (Exception ex) when (ex is COMException)
                {
                    // Грань, параметры которой КОМПАС не отдал, — не повод бросить обход остальных.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Пустой список — честный ответ «прочитать не удалось»; проверка сравнивает его с нулём.
        }

        return found;
    }

    private static void ValidatePatternGridCommand(PatternGridCommand command)
    {
        if (command.Count1 < 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Число экземпляров по первой оси должно быть не меньше 1, получено {command.Count1}.",
                RetryPolicy.Never);
        }

        if (command.Count1 > 1 && !(command.Step1Mm > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Шаг по первой оси должен быть положительным, когда экземпляров больше одного.",
                RetryPolicy.Never);
        }

        if (command.Count2 is int c2 && c2 > 1 && !(command.Step2Mm is > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Число экземпляров по второй оси больше одного, а шаг не задан или не положителен.",
                RetryPolicy.Never);
        }

        if (command.Count2 is > 1 && (command.Axis2Point1Mm is null || command.Axis2Point2Mm is null))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Сетка по двум направлениям требует второй оси: задайте axis2_point1_mm и axis2_point2_mm.",
                RetryPolicy.Never);
        }

        RejectUnmeasuredGeometryPattern(command.GeometryPattern, "сетка");
        RequireDistinctPoints(command.Axis1Point1Mm, command.Axis1Point2Mm, "первой оси");
        if (command.Axis2Point1Mm is not null && command.Axis2Point2Mm is not null)
        {
            RequireDistinctPoints(command.Axis2Point1Mm, command.Axis2Point2Mm, "второй оси");
        }

        if (!PatternBuildingTypes.Linear.Contains(command.BuildingType))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Способ построения '{command.BuildingType}' не описан для массива по сетке. " +
                "Допустимо: " + string.Join(", ", PatternBuildingTypes.Linear) + ".",
                RetryPolicy.Never);
        }
    }

    private static void ValidatePatternCircularCommand(PatternCircularCommand command)
    {
        if (command.Count2 < 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Число экземпляров в кольцевом направлении должно быть не меньше 1, получено {command.Count2}.",
                RetryPolicy.Never);
        }

        if (command.Count1 < 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Число экземпляров в радиальном направлении должно быть не меньше 1, получено {command.Count1}.",
                RetryPolicy.Never);
        }

        if (command.Count1 > 1 && !(command.Step1Mm > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Шаг в радиальном направлении должен быть положительным, когда экземпляров больше одного.",
                RetryPolicy.Never);
        }

        if (!(command.Step2Deg > 0))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Угловой шаг в кольцевом направлении должен быть положительным: он задаётся в ГРАДУСАХ " +
                "(страница icircularpattern_props.html: «Step2 — Угловой шаг (градусы)»).",
                RetryPolicy.Never);
        }

        RejectUnmeasuredGeometryPattern(command.GeometryPattern, "круговой массив");
        RequireDistinctPoints(command.AxisPoint1Mm, command.AxisPoint2Mm, "оси");

        if (!PatternBuildingTypes.Circular.Contains(command.BuildingType))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Способ построения '{command.BuildingType}' не описан для кругового массива. " +
                "Допустимо: " + string.Join(", ", PatternBuildingTypes.Circular) + ".",
                RetryPolicy.Never);
        }
    }

    private static void ValidatePatternMirrorCommand(PatternMirrorCommand command)
    {
        if (command.Mode == PatternMirrorMode.SelectedOperations && command.SourceRefs.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Отражение выбранных операций требует непустого списка source_refs: «зеркальный массив» " +
                "отражает выбранное, а не всё подряд.",
                RetryPolicy.Never);
        }

        if (!PatternBuildingTypes.ChooseBodies.Contains(command.ChooseBodiesType))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Тип действия над телами '{command.ChooseBodiesType}' не описан. Допустимо: " +
                string.Join(", ", PatternBuildingTypes.ChooseBodies) + ".",
                RetryPolicy.Never);
        }
    }

    /// <summary>
    /// Геометрическое копирование отвергается до COM.
    /// </summary>
    /// <remarks>
    /// Маршрут B4 измеряется на <c>GeometryPattern = false</c>. Документировано оно страницей SDK
    /// <c>ifeaturepattern_geometrypattern.html</c> и пользовательской справкой
    /// <c>48_3_3_geometricheskiy_massiv</c>, но у него свои ограничения (замкнутость поверхности,
    /// непересечение экземпляров, одинаковый вид операций) и свой режим — <c>SM-18.grid.operations.geometry</c>,
    /// который в обязательный объём B4 НЕ входит. Поэтому <c>true</c> — отказ
    /// <c>CAPABILITY_UNAVAILABLE</c> с причиной, а не тихая запись незмеренного числа.
    /// </remarks>
    private static void RejectUnmeasuredGeometryPattern(bool geometryPattern, string what)
    {
        if (!geometryPattern)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Геометрическое копирование ({what}) документировано, но в обязательный объём B4 не входит: " +
            "режим SM-18.grid.operations.geometry лежит вне очереди этапа. Признак не создавался — " +
            "вместо записи незмеренного числа вызов отвергнут до COM.",
            RetryPolicy.Never);
    }

    private static void RequireDistinctPoints(IReadOnlyList<double> p1, IReadOnlyList<double> p2, string what)
    {
        if (p1.Count != 3 || p2.Count != 3)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Точки {what} задаются тремя координатами каждая.",
                RetryPolicy.Never);
        }

        var same = Math.Abs(p1[0] - p2[0]) < 1e-9
            && Math.Abs(p1[1] - p2[1]) < 1e-9
            && Math.Abs(p1[2] - p2[2]) < 1e-9;
        if (same)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Точки {what} совпадают — ось вырождена. Отказ на вырожденной оси не был бы фактом " +
                "о массиве, поэтому он отсекается до COM.",
                RetryPolicy.Never);
        }
    }

    /// <summary>
    /// Число словом «не прочитано» вместо нуля. <c>Num(double?)</c> уже объявлен в части вращения
    /// и переиспользуется здесь: два одноимённых члена в одной частичной части — ошибка компиляции,
    /// и это поймано сборкой, а не глазами.
    /// </summary>
    private static string Num(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано";

    /// <summary>Допустимые слова способа построения — по страницам перечислений, а не по догадке.</summary>
    private static class PatternBuildingTypes
    {
        /// <summary>ksLinearPatternBuildingTypeEnum, прочитано из интероп-сборки констант.</summary>
        public static readonly string[] Linear =
        {
            "save_all", "save_along_perimeter", "save_along_axially",
            "chess_order_by_axis1", "chess_order_by_axis2",
        };

        /// <summary>ksCircularPatternBuildingTypeEnum.</summary>
        public static readonly string[] Circular =
        {
            "save_all", "chess_order_by_axis1", "chess_order_by_axis2",
        };

        /// <summary>ksChooseBodiesType.</summary>
        public static readonly string[] ChooseBodies =
        {
            "new_body", "automatic", "manual", "all_bodies",
        };
    }
}

/// <summary>Ось цилиндрической грани: точка оси в координатах модели, радиус и высота.</summary>
internal sealed record HoleAxis(double[] CenterMm, double Radius, double Height);

/// <summary>Ось отверстия-экземпляра, как её отдаёт сервер.</summary>
public sealed record PatternHoleDto(
    IReadOnlyList<double> CenterMm,
    double RadiusMm,
    double HeightMm);

/// <summary>
/// Результат создания массива либо зеркала.
/// </summary>
/// <param name="FeatureRef">Ссылка на признак для последующей правки и чтения.</param>
/// <param name="Readout">Параметры, ПРОЧИТАННЫЕ из модели, а не записанные.</param>
/// <param name="HoleAxes">Оси цилиндрических граней — поимённое доказательство «по каждому экземпляру».</param>
/// <param name="UnverifiedAspects">Что именно этот прогон НЕ проверил, поимённо.</param>
public sealed record PatternResult(
    ReferenceDto FeatureRef,
    string Family,
    PatternReadout Readout,
    int BodyCount,
    double? VolumeMm3,
    BoundingBoxDto? BoundsMm,
    IReadOnlyList<PatternHoleDto> HoleAxes,
    IReadOnlyList<NamedCheck> Checks,
    IReadOnlyList<string> RouteNotes,
    IReadOnlyList<string> UnverifiedAspects)
{
    /// <summary>Снимки тел до/после: заполняются зеркалом, где важно «какое тело тронуто».</summary>
    public IReadOnlyList<string>? BodyRows { get; init; }

    /// <summary>Индексы тел, не тронутых ни исходником, ни отражением.</summary>
    public IReadOnlyList<int>? UntouchedBodyIndexes { get; init; }
}

/// <summary>Результат чтения параметров массива по ссылке на признак.</summary>
/// <param name="DeletedInstancesApplicable">
/// <c>false</c> для зеркального массива: пользовательская справка
/// <c>glava_48_obzhie_svedeniy</c> прямо говорит, что исключение экземпляров недоступно для
/// зеркального массива и массива по образцу. Это предметная неприменимость с источником, а не
/// незакрытое действие.
/// </param>
public sealed record PatternReadResult(
    string Family,
    int? PatternIndex,
    int? PatternCount,
    PatternReadout? Readout,
    bool DeletedInstancesApplicable,
    IReadOnlyList<string> Notes);

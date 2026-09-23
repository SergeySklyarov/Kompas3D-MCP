using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Фаска (docs/05 SM-11): создание вторым способом, чтение и правка параметров.
/// </summary>
/// <remarks>
/// <para>
/// Основание — проба F от 12.09.2026 (<c>docs/acceptance/api7/chamfer.md</c>), а не имена методов:
/// <list type="bullet">
/// <item>F.2 — маршрут API5 работает: <c>NewEntity(o3d_chamfer=33)</c> →
/// <c>ksChamferDefinition.SetChamferParam(transfer, d1, d2)</c> → <c>array()</c> как
/// <c>ksEntityCollection</c> → <c>Add(ребро)</c> → <c>Create()</c> → <c>RebuildDocument()</c>;
/// четыре вертикальных ребра пластины 100×80×10 с катетами 2×2 сняли ровно 20·d₁·d₂ = 80 мм³;</item>
/// <item>F.3/F.5 — правка катетов применяется на месте и до, и после save→close→reopen, значение
/// перечитывается (<c>ok=True transfer=False d1=3 d2=3</c>);</item>
/// <item>F.4/F.11 — <c>transfer</c> (он же <c>IChamfer.Direction</c>) меняет, какой катет ложится
/// на какую грань; объём этого не различает, различают площади боковых граней;</item>
/// <item>F.8 — фаска API5 видна из API7 как <c>IChamfer</c> и её параметры читаются типизированно,
/// но имя в API7 читается другое («f-ch2» → «Фаска:1»), поэтому признак опознаётся по объекту
/// реестра и типу, а не по имени;</item>
/// <item>F.9/F.10 — способом «расстояние и угол» фаска строится только через
/// <c>IChamfer.Angle = ksChamferSideAngle</c>, причём угол — в ГРАДУСАХ: 30 при катете 2 снял
/// 46.188021535141 мм³, что есть 20·d·(d·tg 30°), а радианная гипотеза дала бы отрицательное
/// число и была отвергнута измерением.</item>
/// </list>
/// </para>
/// <para>
/// Пра́вка параметров фаски — НЕ то же самое, что перепривязка опорного эскиза выдавливания
/// (Q-EDIT-SKETCH, <c>edit = blocked_api</c>): там отказ измерен на маршруте смены профиля, а здесь
/// измерена и работает смена числа. И наоборот: правка угла существующего признака этой пробой НЕ
/// измерена и потому отказана явно, а не «по наличию свойства».
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Имя семейства фаски в ответах сервера.</summary>
    private const string ChamferFamily = "chamfer";

    /// <summary>
    /// Способ построения фаски, при котором второй катет производен от угла. Имена — это то, что
    /// <c>IChamfer.BuildingType.ToString()</c> отдаёт из API7 (в API5 способа нет вовсе).
    /// Нужны, чтобы отличить признак, для которого маршрут записи API5 теряет способ построения.
    /// </summary>
    private const string SideAngleBuildingType = "ksChamferSideAngle";

    /// <summary>Способ «два катета» — единственный, который умеет писать <c>SetChamferParam</c>.</summary>
    private const string TwoSidesBuildingType = "ksChamferTwoSides";

    /// <summary>Мосты API7 — по одному на экземпляр: второй мост значил бы второе представление сеанса.</summary>
    private readonly Dictionary<string, Api7Bridge> _api7Bridges = new(StringComparer.Ordinal);

    private Api7Bridge BridgeFor(DocumentEntry document)
    {
        var application = RequireApplication(document.ApplicationId);
        if (!_api7Bridges.TryGetValue(application.Id, out var bridge))
        {
            bridge = new Api7Bridge(application.Application);
            _api7Bridges[application.Id] = bridge;
        }

        return bridge;
    }

    /// <summary>
    /// Создание фаски «расстояние + угол» — единственного режима SM-11, которого в API5 нет
    /// физически. Отказ моста отдётся <c>CAPABILITY_UNAVAILABLE</c> с причиной, а не молчаливым
    /// null: вызывающий обязан отличать «API7 недоступен» от «КОМПАС отверг параметр».
    /// </summary>
    private ChamferResult ChamferByAngle(
        DocumentEntry document,
        IReadOnlyList<ksEntity> entities,
        ChamferCommand command,
        double? volumeBefore,
        double? facesBefore,
        int bodiesBefore,
        HashSet<string> unwrapRoutes)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Способ «расстояние и угол» без него недоступен; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var angle = command.AngleDeg!.Value;
        var baseObjects = bridge.TransferAllTo7([.. entities.Cast<object>()]);
        if (baseObjects is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Рёбра не перенесены в API7 (" + (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                ") — фаска не создавалась.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var created = Api7Chamfer.TryCreateDistanceAngle(
            container, baseObjects, command.Distance1Mm, angle, command.Direction, name: null);
        if (!created.Created)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Фаска через IChamfer не создана: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = created.Failure });
        }

        // Без RebuildModel запись в IChamfer остаётся представлением: это измерено пробой E на
        // IExtrusion.Sketch, и для фаски тот же порядок вызовов обязателен.
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "chamfer.angle");

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);
        var api5Feature = FindChamferEntity(document);
        var readBack = Api7Chamfer.Count(container) is int count and > 0
            ? Api7Chamfer.Read(container, count - 1)
            : null;

        var parametersStored = readBack is not null
            && Math.Abs((readBack.AngleDeg ?? double.NaN) - angle) <= 1e-6
            && Math.Abs((readBack.Distance1Mm ?? double.NaN) - command.Distance1Mm) <= 1e-6;

        var checks = new List<NamedCheck>
        {
            new("base_objects_transferred", baseObjects.Length == entities.Count,
                Observed: baseObjects.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Expected: entities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("parameters_read_back", parametersStored,
                Observed: readBack is null
                    ? "IChamfer не читается"
                    : $"Angle={readBack.AngleDeg:0.####} Distance1={readBack.Distance1Mm:0.####} Direction={readBack.Direction} способ={readBack.BuildingType}",
                Expected: $"Angle={angle:0.####} Distance1={command.Distance1Mm:0.####} Direction={command.Direction}"),
            new("visible_from_api5", api5Feature is not null,
                Observed: api5Feature is null ? "элемент type=33 в дереве API5 не найден" : $"«{api5Feature.name}»"),
            new("face_count_grew", facesAfter is double after && facesBefore is double before
                && after - before == entities.Count,
                Observed: $"{facesBefore}→{facesAfter}",
                Expected: $"+{entities.Count}"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        var materialRemoved = volumeBefore is double b && volumeAfter is double a && a < b;
        bool? numericMatch = null;
        if (command.ExpectedVolumeDeltaMm3 is double expectedDelta
            && volumeBefore is double vBefore && volumeAfter is double vAfter)
        {
            var measured = vBefore - vAfter;
            numericMatch = Math.Abs(measured - expectedDelta) <= ProfileArea.Tolerance(expectedDelta);
            checks.Add(new NamedCheck(
                "volume_delta",
                numericMatch.Value,
                Observed: measured.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(new NamedCheck(
                "volume_delta",
                materialRemoved,
                Observed: volumeBefore is double cb && volumeAfter is double ca
                    ? (cb - ca).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    : "not_computable",
                Expected: "не задано — проверено только направление"));
        }

        var geometryConfirmed = created.Created
            && parametersStored
            && checks.Exists(c => c.Name == "face_count_grew" && c.Passed)
            && materialRemoved
            && numericMatch is not false;

        var unverified = new List<string>
        {
            "angle_units_measured_once — градусы подтверждены одним замером на прямых рёбрах " +
            "перпендикулярных граней (F.10); на дугах и наклонных рёбрах единица не проверялась",
        };
        if (!geometryConfirmed)
        {
            unverified.Insert(0, "geometry_not_confirmed — КОМПАС принял запись, но измерение не подтвердило ожидаемую геометрию");
        }

        if (command.ExpectedVolumeDeltaMm3 is null)
        {
            unverified.Add("expected_volume_delta_not_supplied — аналитическое ожидание дельты не задавал вызывающий, численного доказательства нет");
        }

        if (api5Feature is null)
        {
            // Ссылку на признак, который API5 не видит, выдавать нельзя: правка по ней всё равно
            // упала бы, а вызывающий узнал бы об этом позже.
            unverified.Add("feature_ref_withheld — признак не найден в дереве API5, ссылка не выдана");
            return new ChamferResult(
                null,
                entities.Count,
                readBack?.Distance1Mm,
                null,
                readBack?.Direction,
                bodiesAfter,
                volumeAfter,
                new VerificationDto(VerificationLevel.CallReturned, checks, unverified),
                string.Join(", ", unwrapRoutes));
        }

        var reference = References.Register("feature", document.Id, document.Revision, api5Feature);
        return new ChamferResult(
            ToDto(reference, $"chamfer {command.Distance1Mm:0.###} × {angle:0.###}° × {entities.Count}"),
            entities.Count,
            readBack?.Distance1Mm,
            null,
            readBack?.Direction,
            bodiesAfter,
            volumeAfter,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            string.Join(", ", unwrapRoutes));
    }

    /// <summary>
    /// Последний элемент операции с <c>type = 33</c> в дереве API5. Поиск по типу и порядку, а не
    /// по имени: F.8 измерила, что имя, данное в API5, в API7 читается иначе, — имя не является
    /// идентификатором признака.
    /// </summary>
    private static ksEntity? FindChamferEntity(DocumentEntry document)
    {
        try
        {
            ksEntity? found = null;
            if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
                is not ksEntityCollection collection)
            {
                return null;
            }

            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity && entity.type == KompasObjectTypes.Chamfer)
                {
                    found = entity;
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    // ─── read и edit ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Что сервер видит по фаске: катеты и сторона — из API5; если к тому же документу строится
    /// мост API7 и фаска в нём одна, добавляются угол и способ. Пустое поле означает «не
    /// прочитано», а не «ноль».
    /// </summary>
    private ChamferDto? ReadChamfer(DocumentEntry document, object definition)
    {
        if (definition is not ksChamferDefinition chamfer)
        {
            return null;
        }

        var api5 = ReadChamferParam(chamfer);
        if (api5 is null)
        {
            return null;
        }

        var api7 = ReadChamferAngle(document, api5);
        return new ChamferDto(
            Transfer: api5.Transfer,
            Distance1Mm: api5.Distance1Mm,
            Distance2Mm: api5.Distance2Mm,
            AngleDeg: api7?.AngleDeg,
            BuildingType: api7?.BuildingType,
            // При двух катетах «сторона» в API7 перечитывается как Direction; если моста нет,
            // остаётся значение transfer из API5 — тот же параметр того же признака (F.4/F.11
            // измерили, что оба меняют геометрию одинаково).
            Direction: api7?.Direction ?? api5.Transfer,
            BaseObjectCount: api7?.BaseObjectCount);
    }

    /// <summary>
    /// Угол и способ из API7 — только когда сопоставление однозначно. Мерить угол «первой фаски
    /// подряд» при нескольких фасках означало бы приписать признаку чужое число, поэтому при
    /// неоднозначности сервер возвращает null и объясняет причину в <c>unverified</c>.
    /// </summary>
    private ChamferReadDto? ReadChamferAngle(DocumentEntry document, ChamferParam api5)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null || Api7Chamfer.Count(container) is not int count)
        {
            return null;
        }

        var matches = new List<ChamferReadDto>();
        for (var i = 0; i < count; i++)
        {
            var read = Api7Chamfer.Read(container, i);
            // Сопоставление по первому катету: он отличает фаски друг от друга в эталонных
            // случаях приёмки и точно описывает то, что записывал API5 (F.8: D1=2, D2=2, Angle=45).
            if (read?.Distance1Mm is double d && Math.Abs(d - api5.Distance1Mm) <= 1e-6)
            {
                matches.Add(read);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Правка угловой фаски маршрутом API7: <c>IChamfer</c> на живой модели, запись → <c>Update()</c>
    /// → <c>RebuildModel()</c>. Это единственный маршрут, умеющий писать угол: в API5 его нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отличия от маршрута API5 (<see cref="UpdateChamfer"/>), которые важно не потерять:
    /// <list type="bullet">
    /// <item>признак адресуется ИНДЕКСОМ в <c>IModelContainer.Chamfers</c>, а сопоставление с
    /// признаком API5 идёт по первому катету (имя идентификатором не является — F.8);</item>
    /// <item>второй катет при этом способе ПРОИЗВОДЕН от угла, поэтому он не записывается вовсе,
    /// если клиент не задал его явно: запись «прежнего» числа закрепила бы устаревшую производную
    /// и потеряла связь с углом;</item>
    /// <item>уровень подтверждения требует пересчёта производного катета: успехом считается не
    /// «Update() вернул true», а то, что модель отдаёт новый угол и катет <c>d₁·tg α</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Неоднозначное сопоставление (несколько фасок с тем же первым катетом либо ни одной) —
    /// это отказ, а не «взяли первую»: записать угол не в тот признак означает молча испортить
    /// чужую геометрию.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateChamferByAngle(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        var currentSource = entity.GetDefinition() as ksChamferDefinition;
        var current = currentSource is null ? null : ReadChamferParam(currentSource);
        if (current is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение фаски не перечитывается перед правкой: признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Угол фаски живёт только в IChamfer; признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var matches = Api7Chamfer.FindIndexesByIdenticalFirstLeg(container, current.Distance1Mm);
        if (matches.Count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                matches.Count == 0
                    ? "Фаска с первым катетом " + current.Distance1Mm.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм не найдена в IModelContainer.Chamfers: сопоставить признак API5 с IChamfer " +
                      "нечем, а записать угол в чужой признак нельзя. Признак не изменён."
                    : "Фаске с первым катетом " + current.Distance1Mm.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм отвечает " + matches.Count.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " признаков API7 — сопоставление неоднозначно, и записывать угол «в первый " +
                      "попавшийся» означало бы изменить не тот признак. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["distance1_mm"] = current.Distance1Mm,
                    ["api7_matches"] = matches.Count,
                });
        }

        var index = matches[0];
        var before = Api7Chamfer.Read(container, index);

        // Второй катет пишется, только если клиент задал его явно: иначе он производный.
        var distance1 = command.Distance1Mm;
        var distance2 = command.Distance2Mm;
        var angle = command.AngleDeg;
        var direction = command.Direction;

        var written = Api7Chamfer.TryWrite(container, index, distance1, distance2, angle, direction);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись в IChamfer не подтверждена: " + (written.Failure ?? "причина не сообщена") +
                ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        // Без перестроения запись в IChamfer остаётся представлением (тот же порядок, что при
        // создании: F.10 + проба E на IExtrusion.Sketch).
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "chamfer.update.angle");

        var after = Api7Chamfer.Read(container, index);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var expectedDistance1 = distance1 ?? current.Distance1Mm;
        var expectedAngle = angle ?? before?.AngleDeg;
        // Производный катет — это d₁·tg α, и именно этим проверка отличается от «значение
        // перечиталось»: если ядро не пересчитало второй катет, угол не применён по-настоящему.
        var expectedDerived = expectedAngle is double a && a > 0d && a < 90d
            ? expectedDistance1 * Math.Tan(a * Math.PI / 180d)
            : (double?)null;

        var angleStored = expectedAngle is null
            || (after?.AngleDeg is double readAngle && Math.Abs(readAngle - expectedAngle.Value) <= 1e-6);
        var distance1Stored = Math.Abs((after?.Distance1Mm ?? double.NaN) - expectedDistance1) <= 1e-6;
        var derivedStored = distance2 is not null
            ? Math.Abs((after?.Distance2Mm ?? double.NaN) - distance2.Value) <= 1e-6
            : expectedDerived is null
              || (after?.Distance2Mm is double readDerived
                  && Math.Abs(readDerived - expectedDerived.Value) <= 0.01);

        var parameterSource = entity.GetDefinition() as ksChamferDefinition;
        var api5ReadBack = parameterSource is null ? null : ReadChamferParam(parameterSource);
        var api5Agrees = api5ReadBack is null
            || Math.Abs(api5ReadBack.Distance1Mm - expectedDistance1) <= 1e-6;

        var buildingTypeKept = after?.BuildingType is null
            || before?.BuildingType is null
            || string.Equals(after.BuildingType, before.BuildingType, StringComparison.Ordinal);
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IChamfer[{index}] Distance1={distance1?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "без изменений"} " +
                          $"Angle={angle?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "без изменений"} " +
                          $"Direction={direction?.ToString() ?? "без изменений"}",
                Expected: "запись принята и подтверждена перестроением"),
            new("angle_read_back", angleStored,
                Observed: $"Angle={after?.AngleDeg?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: $"Angle={expectedAngle?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не задан"}"),
            new("distance1_read_back", distance1Stored,
                Observed: $"Distance1={after?.Distance1Mm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: $"Distance1={expectedDistance1.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"),
            new("derived_leg_recomputed", derivedStored,
                Observed: $"Distance2={after?.Distance2Mm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: distance2 is not null
                    ? $"задан явно {distance2.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"
                    : expectedDerived is null
                      ? "производный от угла, угол не задан"
                      : $"{expectedDerived.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} (= d₁·tg α)"),
            new("building_type_kept", buildingTypeKept,
                Observed: $"{before?.BuildingType ?? "не читается"} → {after?.BuildingType ?? "не читается"}",
                Expected: "способ построения не подменён"),
            new("api5_sees_the_change", api5Agrees,
                Observed: api5ReadBack is null
                    ? "GetChamferParam не читается"
                    : $"d1={api5ReadBack.Distance1Mm:0.####} d2={api5ReadBack.Distance2Mm:0.####}",
                Expected: $"d1={expectedDistance1.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        var volumeMatched = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
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
            checks.Add(new NamedCheck(
                "volume_after_update",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: "не задано"));
        }

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        if (after?.BuildingType == SideAngleBuildingType)
        {
            unverified.Add(
                "angle_units_measured_once — градусы подтверждены на прямых рёбрах перпендикулярных " +
                "граней (F.10 и эта правка); на дугах и наклонных рёбрах единица не проверялась");
        }

        var geometryConfirmed = angleStored && distance1Stored && derivedStored && buildingTypeKept
            && sameFeature && volumeMatched;
        if (!geometryConfirmed)
        {
            unverified.Insert(0, geometryConfirmed is false
                ? "geometry_not_confirmed — правка применена, но измерение не подтвердило ожидаемую геометрию"
                : "geometry_not_confirmed");
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            ChamferFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            // Глубины у фаски нет: поле относится к выдавливанию, поэтому null, а не «ноль».
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            AngleReadBackDeg: after?.AngleDeg);
    }

    /// <summary>
    /// Правка катетов и стороны существующей фаски. Значение перечитывается с нового объекта
    /// определения, признак обязан остаться тем же, а геометрия подтверждается измерением объёма.
    /// </summary>
    private UpdateFeatureResult UpdateChamfer(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.Distance1Mm is <= 0d || command.Distance2Mm is <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Катеты фаски обязаны быть положительными: КОМПАС принимает ноль и создаёт признак " +
                "с нулевыми гранями при неизменном объёме (измерено пробой F.12).",
                RetryPolicy.Never);
        }

        if (command.Distance1Mm is null && command.Distance2Mm is null && command.AngleDeg is null
            && command.Direction is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для фаски нужен хотя бы один меняемый параметр: distance1_mm, distance2_mm, " +
                "angle_deg или direction.",
                RetryPolicy.Never);
        }

        // ─── маршрут API7: единственный, который умеет писать угол ──────────────────────────
        //
        // Угол в API5 не выразим физически (у ksChamferDefinition члена «угол» не объявлено),
        // поэтому при наличии angle_deg пишет IChamfer. Маршрут измерен пробой 16.09.2026:
        // запись Angle с последующим Update() применяется к модели, способ построения остаётся
        // ksChamferSideAngle, а второй катет пересчитывается ядром как d₂ = d₁·tg α.
        if (command.AngleDeg is not null)
        {
            return UpdateChamferByAngle(document, entity, command, volumeBefore, featuresBefore,
                stateBefore);
        }

        // ─── маршрут API5: два катета и сторона ─────────────────────────────────────────────
        // Сюда попадают только вызовы без angle_deg. Если способ признака — «расстояние и угол»,
        // запись API5 потеряла бы угол (измерено: 30° → 45°, V 79896.07695154587 → 79820), поэтому
        // такой вызов отклоняется ДО мутации с объяснением, что делать вместо него.

        var currentSource = entity.GetDefinition() as ksChamferDefinition;
        var current = currentSource is null ? null : ReadChamferParam(currentSource);
        if (current is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение фаски не перечитывается перед правкой: признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // ИЗМЕРЕННЫЙ ЗАПРЕТ ТИХОЙ ПОДМЕНЫ СПОСОБА (проба 16.09.2026, scratch/_probe_angle_edit2.py).
        //
        // Фаска, построенная способом «расстояние и угол», живёт как ksChamferSideAngle, и её
        // второй катет — ПРОИЗВОДНОЕ от угла (d₂ = d₁·tg α). Маршрут записи API5
        // (SetChamferParam) умеет только ksChamferTwoSides: он пишет два катета и способ построения
        // НЕ сохраняет. Измерено на пластине 100×80×10, фаска d=2, α=30° (V = 79953.81197846486):
        //
        //   правка distance1_mm = 3 без angle_deg → УСПЕХ, но способ стал ksChamferTwoSides,
        //   угол 30° превратился в 45°, второй катет 1.1547005383792515 → 3,
        //   V = 79820 вместо 79896.07695154587 (то есть при сохранённом угле), расхождение 76.08 мм³.
        //
        // Это тихий неверный результат, а не граница возможностей: клиент просил поменять катет,
        // а получил другую фаску, и вернуть угол нельзя — правка angle_deg отвергается ниже.
        // Поэтому запись отклоняется ДО мутации с указанием, чем именно мерить эту фаску: удалить
        // и пересоздать в нужном способе. Молчаливое «применили как смогли» здесь запрещено.
        //
        // Способ читается из API7 (в API5 его нет вовсе): при неоднозначном сопоставлении
        // ReadChamferAngle возвращает null, и тогда запись НЕ отклоняется — «не прочитали способ»
        // это не «способ угловой», и запрещать правку по незнанию означало бы выдумать отказ.
        var existingApi7 = ReadChamferAngle(document, current);
        if (existingApi7?.BuildingType == SideAngleBuildingType && command.AngleDeg is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Фаска построена способом «расстояние и угол» (ksChamferSideAngle), а маршрут записи " +
                "API5 умеет только «два катета» (ksChamferTwoSides): правка катета молча сменила бы " +
                "способ и потеряла угол (измерено: 30° → 45°, V 79896.07695154587 → 79820). " +
                "Чтобы получить такую фаску с другим размером, удалите признак и создайте заново " +
                "нужным способом.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["existing_building_type"] = existingApi7!.BuildingType,
                    ["write_route_supports"] = TwoSidesBuildingType,
                    ["angle_deg"] = existingApi7.AngleDeg,
                    ["distance2_mm_derived"] = existingApi7.Distance2Mm,
                    ["measured"] = "scratch/_probe_angle_edit2.py, 16.09.2026",
                });
        }

        var distance1 = command.Distance1Mm ?? current.Distance1Mm;
        // Второй катет меняется только вместе с первым либо явно: иначе правка «одного катета»
        // молча превратилась бы в правку обоих.
        var distance2 = command.Distance2Mm ?? (command.Distance1Mm is not null && command.Direction is null
            ? command.Distance1Mm.Value
            : current.Distance2Mm);
        var transfer = command.Direction ?? current.Transfer;

        var writeTarget = entity.GetDefinition() as ksChamferDefinition;
        if (writeTarget is null || !writeTarget.SetChamferParam(transfer, distance1, distance2))
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "SetChamferParam не подтверждён: признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var writes = new List<NamedCheck>
        {
            new("set_chamfer_param", true,
                Observed: $"transfer={transfer} d1={distance1:0.####} d2={distance2:0.####}",
                Expected: $"transfer={transfer} d1={distance1:0.####} d2={distance2:0.####}"),
        };

        // Порядок «запись → Update() → RebuildDocument()» — часть контракта: без Update() модель
        // остаётся прежней, хотя все сеттеры вернули true (P2.3 для выдачиваний, F.3 для фаски).
        if (!entity.Update())
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksEntity.Update() вернул false: записанное значение не применено к модели.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["applied_writes"] = writes.Count });
        }

        document.Document.RebuildDocument();
        var readBack = (entity.GetDefinition() as ksChamferDefinition) is { } afterDefinition
            ? ReadChamferParam(afterDefinition)
            : null;
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var valueStored = readBack is not null
            && Math.Abs(readBack.Distance1Mm - distance1) <= 1e-6
            && Math.Abs(readBack.Distance2Mm - distance2) <= 1e-6
            && readBack.Transfer == transfer;
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>(writes)
        {
            new("parameters_read_back", valueStored,
                Observed: readBack is null
                    ? "GetChamferParam не читается"
                    : $"transfer={readBack.Transfer} d1={readBack.Distance1Mm:0.####} d2={readBack.Distance2Mm:0.####}",
                Expected: $"transfer={transfer} d1={distance1:0.####} d2={distance2:0.####}"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}», updateStamp {stateBefore.UpdateStamp}→{stateAfter.UpdateStamp}",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
        };

        var volumeMatched = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
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
            checks.Add(new NamedCheck(
                "volume_after_update",
                false,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: "не задано"));
        }

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        var geometryConfirmed = valueStored && sameFeature && volumeMatched;
        if (!geometryConfirmed)
        {
            unverified.Insert(0, valueStored
                ? "volume_not_as_expected — значение записано и перечитано, но измерение объёма не совпало с ожиданием"
                : "value_not_read_back — параметр не перечитался с нового объекта определения");
        }

        BumpRevision(document, "chamfer.update");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            ChamferFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            readBack?.Distance1Mm,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }
}

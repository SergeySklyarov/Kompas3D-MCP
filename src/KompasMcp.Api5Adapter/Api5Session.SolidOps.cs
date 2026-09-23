using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Операции над телами B3: булевы (SM-15), разделение и отсечение (SM-16), перенос и поворот (SM-17).
/// </summary>
/// <remarks>
/// <para>
/// <b>Основание — измерение, а не имена методов.</b> Три изолированные пробы от 18.09.2026
/// (<c>--boolean</c>, <c>--split</c>, <c>--reposition</c>; журналы в <c>docs/acceptance/api7/</c>)
/// подтвердили маршруты и, что важнее, их ГРАНИЦЫ. Ниже перенесено ровно то, что измерено;
/// непроверенное названо непроверенным.
/// </para>
/// <para>
/// <b>Проверки до вызова COM.</b> Ядро не отвергает повтор ссылки и не проверяет, что цель не входит
/// в набор инструментов (измерено, шаг BO.9: повтор принят молча, тела 3→2). Поэтому обе проверки
/// стоят здесь и до COM, а не полагаются на ядро.
/// </para>
/// <para>
/// <b>Успешный Update() не является доказательством.</b> На переносе три маршрута из четырёх вернули
/// <c>true</c> и не двинули тело (шаг RP.2). Поэтому каждый обработчик перечитывает модель после
/// вызова и возвращает ФАКТИЧЕСКОЕ состояние, а не обещанное.
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Имя семейства булевых операций в ссылках и ответах.</summary>
    private const string BooleanRefKind = "solid_boolean";

    private const string SplitRefKind = "solid_split";

    private const string CutRefKind = "solid_cut";

    private const string RepositionRefKind = "solid_reposition";

    // ══════════════════════════════════════════════════════════════════════════════════ SM-15 ══

    /// <summary>
    /// Булева операция над телами: явная цель, явный набор инструментов, вид операции и политика
    /// сохранения инструментов.
    /// </summary>
    public BooleanResultDto SolidBoolean(BooleanCommand command)
    {
        if (command.ToolBodyRefs is not { Count: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "tool_body_refs пуст — булевой операции нужен хотя бы один инструмент.",
                details: new Dictionary<string, object?> { ["target_body_ref"] = command.TargetBodyRef });
        }

        var document = RequireDocument(command.DocumentId);
        GuardBooleanRefs(command.TargetBodyRef, command.ToolBodyRefs);

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.boolean");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);

        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);
        var tools = new List<BodyTarget>(command.ToolBodyRefs.Count);
        foreach (var toolRef in command.ToolBodyRefs)
        {
            var tool = ResolveBodyTarget(document, part, toolRef, bodiesBefore);
            if (tool.Index == target.Index)
            {
                // Ядро такой случай отвергает (шаг BO.9), но отвергает ПОСЛЕ попытки; проверка здесь
                // даёт вызывающему имя виновника, а не «Update() вернул false».
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Тело '{toolRef}' указано и целью, и инструментом.",
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = command.TargetBodyRef,
                        ["tool_body_ref"] = toolRef,
                    });
            }

            tools.Add(tool);
        }

        if (bridge.TransferTo7(target.RawElement) is not IKompasAPIObject target7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Тело-цель не переносится в API7 как IKompasAPIObject: " +
                (bridge.BridgeFailure ?? "TransferInterface вернул null"),
                RetryPolicy.ReacquireContext);
        }

        var tools7 = new object[tools.Count];
        for (var i = 0; i < tools.Count; i++)
        {
            if (bridge.TransferTo7(tools[i].RawElement) is not { } tool7)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    $"Инструмент {i} ('{command.ToolBodyRefs[i]}') не переносится в API7: " +
                    (bridge.BridgeFailure ?? "TransferInterface вернул null"),
                    RetryPolicy.ReacquireContext);
            }

            tools7[i] = tool7;
        }

        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidBoolean.TryCreate(
            container, target7, tools7, command.Operation, command.KeepTools, name: null);

        if (created.Feature is null)
        {
            // Отказ ядра — это ФАКТ о геометрии, а не сбой адаптера, и состояние модели после него
            // сообщается фактическое: «принято» и «отвергнуто, но модель изменилась» — разные вещи.
            var after = ReadBodySnapshots(document.PartNow());
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Булева операция не построена: " + created.Failure +
                ". Ядро отвергает несвязные тела, касание по ребру и касание в точке (измерено).",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["operation"] = command.Operation.ToString().ToLowerInvariant(),
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = after.Count,
                    ["volume_before"] = SumVolumes(bodiesBefore),
                    ["volume_after"] = SumVolumes(after),
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.boolean");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.BooleanOperation, "solid.boolean");
        var resultRef = References.Register(BooleanRefKind, document.Id, document.Revision, address.Entity).Id;
        var totalVolume = rows.Sum(r => r.VolumeMm3 ?? 0d);

        var consumed = new List<string> { command.TargetBodyRef };
        consumed.AddRange(command.ToolBodyRefs);

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is double expected && !VolumeMatches(totalVolume, expected))
        {
            // Ожидание названо, но не подтверждено. Это НЕ повод ослабить проверку и не повод
            // объявить успех: расхождение попадает в ответ как неподтверждённый аспект.
            unverified.Add("expected_volume_mismatch");
        }

        return new BooleanResultDto
        {
            FeatureRef = resultRef,
            Operation = command.Operation.ToString().ToLowerInvariant(),
            KeepTools = command.KeepTools,
            ResultBodies = rows,
            SavedTools = command.KeepTools ? MatchSavedTools(rows, tools) : Array.Empty<SolidBodyDto>(),
            ConsumedInputs = consumed,
            TotalVolumeMm3 = totalVolume,
            VolumeNote = "Сумма индивидуальных объёмов всех тел документа. Объём пространственного "
                + "объединения — другая величина, и смешивать их нельзя.",
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
        };
    }

    /// <summary>
    /// Проверки адресации, которые ядро не делает: цель не среди инструментов и нет повторов.
    /// </summary>
    /// <remarks>
    /// Повтор ссылки ядро ПРИНИМАЕТ молча (измерено 18.09.2026, шаг BO.9: тела 3→2, суммарный объём
    /// 49 000→37 000), поэтому обнаружение повтора обязано жить в контракте.
    /// </remarks>
    private static void GuardBooleanRefs(string targetBodyRef, IReadOnlyList<string> toolBodyRefs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < toolBodyRefs.Count; i++)
        {
            var reference = toolBodyRefs[i];
            if (string.IsNullOrWhiteSpace(reference))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"tool_body_refs[{i}] пуст — инструмент обязан быть адресован ссылкой.");
            }

            if (string.Equals(reference, targetBodyRef, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Тело '{reference}' указано и целью, и инструментом.",
                    details: new Dictionary<string, object?> { ["target_body_ref"] = targetBodyRef });
            }

            if (!seen.Add(reference))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' повторяется в tool_body_refs. Ядро такой повтор принимает "
                    + "молча и потребляет тело дважды, поэтому повтор отвергается здесь.",
                    details: new Dictionary<string, object?>
                    {
                        ["tool_body_ref"] = reference,
                        ["tool_body_refs"] = toolBodyRefs,
                    });
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════ SM-16 ══

    /// <summary>
    /// Разделение тела плоскостью. Возвращаются ВСЕ полученные части, каждая со своей ссылкой.
    /// </summary>
    /// <remarks>
    /// Отдельного выбора «какие части сохранить» не требуется: разделение сохраняет все части по
    /// построению (измерено, шаг SP.2: брусок 24 000 → 6 000 и 18 000, сумма 24 000). Именно это
    /// измерение сняло блокировку OQ-A18, а не найденный член.
    /// </remarks>
    public SplitResultDto SolidSplit(SplitCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.split");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);
        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);
        var targetIndex = target.Index;

        var plane = ResolveCutPlane(document, bridge, container, command.Plane, out var planeFailure);
        if (plane.Plane is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Плоскость разделения не построена: " + planeFailure,
                RetryPolicy.SameOperationId);
        }

        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidSplit.TryCreate(container, new object[] { plane.Plane }, name: null);
        if (created.Feature is null)
        {
            var after = ReadBodySnapshots(document.PartNow());
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Разделение не построено: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = after.Count,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.split");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.SplitSolid, "solid.split");
        var featureRef = References.Register(SplitRefKind, document.Id, document.Revision, address.Entity).Id;

        // Части — тела, которых до операции не было; нетронутые — те, что были и остались.
        // Опознание идёт по ГЕОМЕТРИИ (габарит И объём), а не по месту в коллекции.
        //
        // Прежняя редакция отбирала части как rows.Skip(bodiesBefore.Count) — «всё, что вышло за
        // границы прежнего списка». Это неверно: разделение ЗАМЕНЯЕТ тело на месте, поэтому одна из
        // частей занимает индекс цели и в «хвост» не попадает, а нетронутое тело, наоборот, уезжает
        // в части. Измерено 18.09.2026 строками B3.08/B3.09 приёмки: в документе лежали обе части
        // 6000 и 18000, а ответ отдавал одну часть 18000 и сумму 18000 вместо 24000. Прибор это
        // заметил сам (unverified_aspects: parts_less_than_two) — то есть честность отчёта
        // работала, а отбор тел был неверен.
        //
        // Тело-цель из сопоставления ИСКЛЮЧАЕТСЯ: операция его потребила, поэтому совпадение с ним
        // ничего не доказывает и лишь вернуло бы цель в список нетронутых.
        var parts = new List<SolidBodyDto>();
        var untouched = new List<SolidBodyDto>();
        var claimed = new HashSet<int>();
        foreach (var row in rows)
        {
            var match = bodiesBefore.FirstOrDefault(
                b => b.Index != targetIndex && !claimed.Contains(b.Index) && MatchesSnapshot(row, b));
            if (match is null)
            {
                parts.Add(row);
            }
            else
            {
                claimed.Add(match.Index);
                untouched.Add(row);
            }
        }

        var partsVolume = parts.Sum(r => r.VolumeMm3 ?? 0d);

        var unverified = new List<string>();
        if (parts.Count < 2)
        {
            unverified.Add("parts_less_than_two");
        }

        if (command.ExpectedVolumeMm3 is double expected)
        {
            var beforeVolume = bodiesBefore.FirstOrDefault(b => b.Index == targetIndex)?.Volume;
            if (beforeVolume is double before && !VolumeMatches(partsVolume, before))
            {
                unverified.Add("parts_volume_differs_from_source");
            }
        }

        return new SplitResultDto
        {
            FeatureRef = featureRef,
            Parts = parts,
            UntouchedBodies = untouched,
            PartsVolumeSumMm3 = partsVolume,
            PlaneNormalMm = plane.UnitNormal,
            PlanePointMm = plane.Point,
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
        };
    }

    /// <summary>
    /// Отсечение тела по одну сторону плоскости. Сторона названа знаком <c>s = n·(p − p₀)</c>.
    /// </summary>
    /// <remarks>
    /// Соответствие измерено (шаг SP.7): <c>ICut.Direction = true</c> оставляет сторону в направлении
    /// нормали, то есть <c>s &gt; 0</c>.
    /// </remarks>
    public CutByPlaneResultDto SolidCutByPlane(CutByPlaneCommand command)
    {
        var keepPositive = command.KeepSide switch
        {
            "positive" => true,
            "negative" => false,
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"keep_side '{command.KeepSide}' не распознан: ожидалось 'positive' (s > 0) или 'negative' (s < 0).",
                details: new Dictionary<string, object?> { ["keep_side"] = command.KeepSide }),
        };

        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.cut_by_plane");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);
        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);
        var targetIndex = target.Index;

        var plane = ResolveCutPlane(document, bridge, container, command.Plane, out var planeFailure);
        if (plane.Plane is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Плоскость отсечения не построена: " + planeFailure,
                RetryPolicy.SameOperationId);
        }

        var target7 = bridge.TransferTo7(target.RawElement);
        if (target7 is not IKompasAPIObject targetBody)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Тело-цель не перенесено в API7 как IKompasAPIObject: "
                + (bridge.BridgeFailure ?? "TransferInterface вернул null")
                + ". Отсечение БЕЗ названного тела снимает материал у ВСЕХ тел документа: справка "
                + "продукта (rezultat_oper_v_zavisimosti_ot_s_o.html) называет умолчанием «Все "
                + "объекты», и объект, ЦЕЛИКОМ лежащий со стороны отсечения, в область применения "
                + "входит. Признак с незапрошенной областью не создаётся.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["target_body_ref"] = command.TargetBodyRef,
                    ["target_body_index"] = targetIndex,
                    ["code"] = "target_body_not_transferred",
                });
        }

        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidCut.TryCreateByPlane(
            container, plane.Plane, keepPositive, name: null, targetBody);
        if (created.Feature is null)
        {
            var after = ReadBodySnapshots(document.PartNow());
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отсечение не построено: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["keep_side"] = command.KeepSide,
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = after.Count,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.cut_by_plane");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.CutByPlane, "solid.cut_by_plane");
        var featureRef = References.Register(CutRefKind, document.Id, document.Revision, address.Entity).Id;

        // Остаток опознаётся СОПОСТАВЛЕНИЕМ СОСТАВА ТЕЛ, а не «первым, чей объём не равен цели».
        // Прежняя редакция искала тело через FirstOrDefault(!IsUntouched(...)), а IsUntouched
        // сравнивал объём кандидата с объёмом ЦЕЛИ до операции: постороннее тело этим не
        // опознаётся вовсе, и все остальные тела объявлялись UntouchedBodies без доказательства.
        // Сопоставление снимков видит и ИСЧЕЗНУВШИЕ тела — а именно их прежняя проверка не видела
        // (клиентский дефект CUT-PLANE-APPLIED-TO-UNNAMED-BODIES, 19.09.2026).
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var remainingSnapshot = changes.MatchedOf(targetIndex);
        var vanished = bodiesBefore.Where(b => changes.MatchedOf(b.Index) is null).ToList();
        var otherMoved = changes.UnchangedViolations(targetIndex);
        var createdBodies = changes.NewBodies;

        var remaining = remainingSnapshot is null
            ? null
            : rows.FirstOrDefault(r => MatchesSnapshot(r, remainingSnapshot));
        if (remaining is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "После отсечения тело-цель не найдено: сопоставление состава тел не дало ему пары. "
                + "Это отказ, а не «остаток нулевого объёма» — объявить нечего.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["bodies_before"] = bodiesBefore.Count,
                    ["bodies_after"] = rows.Count,
                    ["vanished_bodies"] = vanished.Count,
                    ["body_volumes_after"] = BodyVolumesText(rows),
                });
        }

        // АДРЕСНОСТЬ — ПРЕДМЕТ ЭТОГО ВЫЗОВА, и она проверяется, а не предполагается: названо ОДНО
        // тело, значит ни одно другое не имеет права измениться, исчезнуть или появиться.
        if (vanished.Count > 0 || otherMoved.Count > 0 || createdBodies.Count > 0)
        {
            var parts = new List<string>();
            if (vanished.Count > 0)
            {
                parts.Add("исчезли тела " + string.Join(", ", vanished.Select(b => "тел" + b.Index)));
            }

            if (otherMoved.Count > 0)
            {
                parts.Add("изменились посторонние тела: " + string.Join("; ", otherMoved));
            }

            if (createdBodies.Count > 0)
            {
                parts.Add("появились новые тела " + string.Join(", ", createdBodies.Select(b => "тел" + b.Index)));
            }

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отсечение затронуло не только названное тело: " + string.Join("; ", parts)
                + ". Мутация уже выполнена — откат не обещается. Фактический состав тел: "
                + BodyVolumesText(rows) + "; ревизия после мутации " + document.Revision
                + ". Прочитайте состав тел и повторите операцию на исправленном состоянии.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["target_body_index"] = targetIndex,
                    ["vanished_bodies"] = vanished.Select(b => b.Index).ToArray(),
                    ["changed_nontarget_bodies"] = otherMoved.ToArray(),
                    ["created_bodies"] = createdBodies.Select(b => b.Index).ToArray(),
                    ["body_volumes_after"] = BodyVolumesText(rows),
                    ["code"] = "cut_not_addressed_to_target_body",
                });
        }

        var untouched = rows.Where(r => !ReferenceEquals(r, remaining)).ToList();
        var targetMoved = changes.DeltaOf(targetIndex) is double moved
                          && Math.Abs(moved) > VolumeChangeFloorMm3;

        var checks = new List<NamedCheck>
        {
            new("target_body_affected", targetMoved,
                Observed: "тел" + targetIndex + ": ΔV=" + Num(changes.DeltaOf(targetIndex)),
                Expected: "изменение объёма названного тела"),
            new("nontarget_bodies_unchanged", otherMoved.Count == 0,
                Observed: untouched.Count == 0 ? "<посторонних тел нет>" : BodyVolumesText(untouched),
                Expected: "объёмы посторонних тел не изменились"),
            new("no_bodies_vanished", vanished.Count == 0,
                Observed: Num(vanished.Count), Expected: "0"),
            new("no_bodies_created", createdBodies.Count == 0,
                Observed: Num(createdBodies.Count), Expected: "0"),
        };

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is double expected
            && remaining.VolumeMm3 is double actual)
        {
            var matched = VolumeMatches(actual, expected);
            checks.Add(new NamedCheck(
                "volume_expected",
                matched,
                Observed: Num(remaining.VolumeMm3),
                Expected: Num(expected)));
            if (!matched)
            {
                unverified.Add("expected_volume_mismatch — объявленный объём остатка "
                               + Num(expected) + " не совпал с измеренным " + Num(remaining.VolumeMm3));
            }
        }
        else
        {
            unverified.Add("no_expectation_supplied — без expected_volume_mm3 отсечение не может "
                           + "быть подтверждено геометрически: адресность проверена по составу тел, "
                           + "а объём остатка — нет");
        }

        if (!targetMoved)
        {
            unverified.Add("target_body_unchanged — объём названного тела не изменился: отличить "
                           + "«плоскость не пересекает тело» от «запись не применилась» составом тел "
                           + "нельзя");
        }

        return new CutByPlaneResultDto
        {
            FeatureRef = featureRef,
            Remaining = remaining,
            KeptSide = keepPositive ? "positive" : "negative",
            PlaneNormalMm = plane.UnitNormal ?? Array.Empty<double>(),
            PlanePointMm = plane.Point ?? Array.Empty<double>(),
            UntouchedBodies = untouched,
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
            Checks = checks,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════ SM-17 ══

    /// <summary>
    /// Перенос тела на вектор или поворот вокруг оси. Объём и число тел сохраняются, положение
    /// меняется только у выбранного тела.
    /// </summary>
    public RepositionResultDto SolidReposition(RepositionCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.reposition");
        var part = document.PartNow();
        var bodiesBefore = ReadBodySnapshots(part);
        var target = ResolveBodyTarget(document, part, command.TargetBodyRef, bodiesBefore);

        var matrix = command.Kind switch
        {
            RepositionKind.Translate => BuildTranslation(command),
            RepositionKind.Rotate => BuildRotation(command),
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"kind '{command.Kind}' не распознан.",
                details: new Dictionary<string, object?> { ["kind"] = command.Kind.ToString() }),
        };

        if (bridge.TransferTo7(target.RawElement) is not IKompasAPIObject body7)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Тело не переносится в API7 как IKompasAPIObject: " +
                (bridge.BridgeFailure ?? "TransferInterface вернул null"),
                RetryPolicy.ReacquireContext);
        }

        var before = target.Snapshot;
        var featureTreeBefore = FeatureTreeSnapshot.Capture(document);
        var created = Api7SolidReposition.TryCreate(container, body7, matrix, name: null);
        if (created.Feature is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Преобразование положения не построено: " + created.Failure,
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        RequirePlacementRoundTrip(created.Feature, matrix, command.Kind);

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.reposition");

        var rows = ReadSolidBodies(document);
        var address = RequireCreatedFeatureAddress(document, featureTreeBefore, KompasObjectTypes.BodyRepositionFeature, "solid.reposition");
        var featureRef = References.Register(RepositionRefKind, document.Id, document.Revision, address.Entity).Id;

        // Успешный Update() здесь НЕ доказательство: три маршрута из четырёх вернули true и тело не
        // двинули (шаг RP.2). Поэтому положение перечитывается, и «не сдвинулось» — это отказ, а не
        // успех с нулевым результатом. Сравнение идёт с габаритом, посчитанным по ТОЙ ЖЕ матрице:
        // объём при переносе не меняется, и по нему «сдвинулось» и «осталось» неразличимы.
        var after = rows.FirstOrDefault(r => MatchesMoved(r, matrix, before));
        if (after is null)
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "IBodyReposition.Update() вернул успех, но тело осталось на прежнем месте. "
                + "На v24 это отдельный измеренный исход: положение пишет только однородная матрица 4×4, "
                + "и молчаливый no-op возможен.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = command.Kind.ToString().ToLowerInvariant(),
                    ["bbox_before"] = before?.Min is null
                        ? null
                        : new[] { before.Min, before.Max },
                });
        }

        var untouched = rows.Where(r => !ReferenceEquals(r, after)).ToList();
        var unverified = new List<string>();
        if (rows.Count != bodiesBefore.Count)
        {
            unverified.Add("body_count_changed");
        }

        return new RepositionResultDto
        {
            FeatureRef = featureRef,
            Kind = command.Kind.ToString().ToLowerInvariant(),
            BboxBefore = Box(before),
            BboxAfter = after.Bbox,
            VolumeMm3 = after.VolumeMm3 ?? double.NaN,
            BodiesBefore = bodiesBefore.Count,
            BodiesAfter = rows.Count,
            UntouchedBodies = untouched,
            Revision = document.Revision,
            UnverifiedAspects = unverified.Count == 0 ? null : unverified,
        };
    }

    private static double[] BuildTranslation(RepositionCommand command)
    {
        if (command.VectorMm is not { Count: 3 } vector)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Перенос требует vector_mm из трёх чисел в модельных координатах, мм.");
        }

        if (command.AxisPointMm is not null || command.AxisDirectionMm is not null
            || command.AxisPoint2Mm is not null || command.AngleDeg is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Перенос не принимает параметров поворота: указаны axis_* или angle_deg.",
                details: new Dictionary<string, object?> { ["kind"] = "translate" });
        }

        try
        {
            return RepositionMatrix.Translate(vector);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, ex.Message);
        }
    }

    private static double[] BuildRotation(RepositionCommand command)
    {
        if (command.AngleDeg is not double angle)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поворот требует angle_deg в градусах.");
        }

        if (command.VectorMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поворот не принимает vector_mm: указан вектор переноса.",
                details: new Dictionary<string, object?> { ["kind"] = "rotate" });
        }

        if (command.AxisPointMm is not { Count: 3 } point)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Поворот требует axis_point_mm — точку на оси, в модельных координатах, мм.");
        }

        // Ось задаётся ЛИБО направлением, ЛИБО второй точкой. Обе сразу или ни одной — отказ:
        // «взяли то, что показалось» здесь означало бы поворот вокруг не той оси, а КОМПАС на это
        // не ошибается.
        var hasDirection = command.AxisDirectionMm is { Count: 3 };
        var hasSecondPoint = command.AxisPoint2Mm is { Count: 3 };
        if (hasDirection == hasSecondPoint)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                hasDirection
                    ? "Ось поворота задана и направлением, и второй точкой — выберите одно."
                    : "Ось поворота не задана: нужен axis_direction_mm либо axis_point2_mm.",
                details: new Dictionary<string, object?>
                {
                    ["has_axis_direction"] = hasDirection,
                    ["has_axis_point2"] = hasSecondPoint,
                });
        }

        double[] direction;
        if (hasDirection)
        {
            direction = new[] { command.AxisDirectionMm![0], command.AxisDirectionMm[1], command.AxisDirectionMm[2] };
        }
        else
        {
            var second = command.AxisPoint2Mm!;
            direction = new[] { second[0] - point[0], second[1] - point[1], second[2] - point[2] };
            if (RigidFrame.Norm(direction) <= 0d)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Две точки оси поворота совпали — направление не определено.",
                    details: new Dictionary<string, object?>
                    {
                        ["axis_point_mm"] = command.AxisPointMm,
                        ["axis_point2_mm"] = command.AxisPoint2Mm,
                    });
            }
        }

        try
        {
            return RepositionMatrix.RotateAboutAxis(
                new[] { point[0], point[1], point[2] }, direction, angle);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, ex.Message);
        }
    }

    /// <summary>
    /// Записанное размещение перечитывается обратно и сверяется с заданным ПО МАТРИЦЕ, а не по числам.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем сверка вообще.</b> Успешный <c>IBodyReposition.Update()</c> означает «принято», а не
    /// «применено»: измерено (шаг RP.2), что три маршрута из четырёх вернули <c>true</c> и тело не
    /// двинули. Поэтому создание подтверждается ещё и тем, что записанные параметры читаются назад.
    /// </para>
    /// <para>
    /// <b>Почему по матрице, а не по тройке чисел.</b> Параметризация углами Эйлера неоднозначна
    /// (при нутации 0 или 180° сумма прецессии и вращения определена с точностью до
    /// перераспределения), поэтому требование равенства чисел отвергло бы ВЕРНУЮ запись. Собирается
    /// матрица из прочитанной тройки, и она сравнивается с заданной: <see cref="EulerOrientation"/>
    /// держит порядок спряжения в одном месте, и подмена этого порядка разойдётся здесь сразу —
    /// измеренное расхождение при чужом порядке равно 1, при верном — 0 либо 2,2·10⁻¹⁶.
    /// </para>
    /// <para>
    /// <b>Допуск 10⁻⁶.</b> Он на шесть порядков ниже расхождения, которое даёт неверный порядок
    /// (1), и на десять порядков выше измеренной невязки верного разложения (2,2·10⁻¹⁶). Допуск
    /// шире машинной точности намеренно: он не должен превращать округление в отказ.
    /// </para>
    /// </remarks>
    private static void RequirePlacementRoundTrip(
        IBodyReposition feature, double[] matrix, RepositionKind kind)
    {
        var read = Api7SolidReposition.ReadPlacement(feature);
        if (read.Reading is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Размещение не перечитывается сразу после записи: " + (read.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        var reading = read.Reading;
        var difference = PlacementDifference(reading, matrix);
        if (difference is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Записанное размещение прочиталось НЕ тем маршрутом, которым записано: "
                + $"OrientationType={reading.OrientationType}, ParameterType={reading.ParameterType}. "
                + "Документ хранит параметры ориентации, поэтому признак без режима углов Эйлера "
                + "означает, что запись не применилась.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["orientation_type"] = reading.OrientationType,
                    ["parameter_type"] = reading.ParameterType,
                    ["kind"] = kind.ToString().ToLowerInvariant(),
                });
        }

        if (difference > PlacementRoundTripTolerance)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Записанное размещение не воспроизводится из прочитанных параметров: расхождение "
                + $"{difference:R} по матрице (вид — {kind.ToString().ToLowerInvariant()}). Это признак "
                + "подменённого порядка спряжения углов Эйлера либо неприменённой записи.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["matrix_difference"] = difference,
                    ["requested_matrix"] = matrix,
                    ["read_angles_deg"] = reading.AnglesDeg,
                    ["read_displacement_mm"] = reading.DisplacementMm,
                });
        }
    }

    /// <summary>
    /// Расхождение между заданной матрицей и размещением, СОБРАННЫМ из прочитанных параметров.
    /// <c>null</c> — прочитать не удалось либо прочитано не тем маршрутом (а не «ноль»).
    /// </summary>
    /// <remarks>
    /// <c>null</c> и <c>0</c> здесь РАЗНЫЕ ответы, и это не придирка: ноль означает «прочитанное
    /// воспроизводит заданное», а <c>null</c> — «параметров этого маршрута у признака нет». Слить их
    /// значило бы принять отсутствие данных за совпадение.
    /// </remarks>
    private static double? PlacementDifference(
        Api7SolidReposition.PlacementReading? reading, double[] matrix)
    {
        if (reading is null
            || !reading.IsEuler
            || reading.AnglesDeg is not { Length: 3 } angles
            || !reading.HasDisplacement
            || reading.DisplacementMm is not { Length: 3 } displacement)
        {
            return null;
        }

        var restored = EulerOrientation.RotationFromAngles(angles[0], angles[1], angles[2]);
        restored[12] = displacement[0];
        restored[13] = displacement[1];
        restored[14] = displacement[2];
        return EulerOrientation.MaxDifference(restored, matrix);
    }

    /// <summary>Допуск сверки «записано → прочитано» по матрице; обоснование — в докстроке выше.</summary>
    private const double PlacementRoundTripTolerance = 1e-6;

    // ═══════════════════════════════════════════════════════════════════════════════ helpers ══

    /// <summary>
    /// Мост API7 или явный <c>CAPABILITY_UNAVAILABLE</c> с причиной. Молчаливый null недопустим:
    /// вызывающий обязан отличать «API7 недоступен» от «КОМПАС отверг геометрию».
    /// </summary>
    private IModelContainer RequireContainer(Api7Bridge bridge, DocumentEntry document, string tool)
    {
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is not null)
        {
            return container;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"Маршрут API7 для '{tool}' не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
            ". Операция не выполнялась.",
            RetryPolicy.ReacquireContext);
    }

    /// <summary>
    /// ФОРМА постановки плоскости: взаимоисключение способов, <c>offset_mm</c> без <c>base</c> и
    /// объявленный отказ на <c>base</c>. Проверяется до всякой работы с моделью, у обоих маршрутов —
    /// и у создания, и у правки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Измерено клиентской приёмкой B3 (19.09.2026, три строки FAIL):
    /// объявленный в схеме <c>CAPABILITY_UNAVAILABLE</c> на <c>plane.base</c> был НЕДОСТИЖИМ — форма
    /// поля в DTO расходилась с опубликованной, вызов падал на разборе payload с
    /// <c>JsonException</c> и кодом <c>VERIFICATION_FAILED</c>. Форма приведена к опубликованной
    /// (<c>CutPlaneDto</c>), а эта проверка делает объявленный исход исполняемым и общим для
    /// <c>kompas_split</c>, <c>kompas_cut_by_plane</c> и применимого <c>kompas_update_feature</c>.
    /// </para>
    /// <para>
    /// <b>Приоритет проверок объявлен и не зависит от порядка полей в JSON.</b>
    /// <list type="number">
    /// <item><c>base</c> назван ВМЕСТЕ с другим способом (<c>plane_ref</c> или точка с нормалью) —
    /// <c>INVALID_ARGUMENT</c>: запрос противоречив, и ответить на него объявленным отказом
    /// возможности значило бы спрятать от клиента, что он назвал два способа сразу;</item>
    /// <item><c>base</c> назван один (со смещением или без) — <c>CAPABILITY_UNAVAILABLE</c>:
    /// ровно то, что обещает описание поля;</item>
    /// <item><c>offset_mm</c> без <c>base</c> — <c>INVALID_ARGUMENT</c>: смещение без базовой
    /// плоскости не выражает плоскость, а принять параметр и промолчать значило бы объявить его
    /// принятым;</item>
    /// <item><c>plane_ref</c> вместе с точкой или нормалью — <c>INVALID_ARGUMENT</c> (проверяется
    /// вызывающим маршрутом, потому что правка ссылку отвергает по своей причине).</item>
    /// </list>
    /// </para>
    /// </remarks>
    private static void GuardCutPlaneForm(CutPlaneDto plane)
    {
        switch (CutPlaneForm.Validate(plane))
        {
            case CutPlaneFormVerdict.Ok:
                return;

            case CutPlaneFormVerdict.ModesConflict:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Плоскость задана базовой плоскостью И другим способом. base объявлен, но не "
                    + "поддержан, поэтому «base плюс точка с нормалью» — это противоречивый запрос, а "
                    + "не объявленный отказ возможности: назовите один способ.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["base"] = plane.Base!.Value.ToString(),
                        ["plane_ref"] = plane.PlaneRef,
                        ["has_point"] = plane.PointMm is not null,
                        ["has_normal"] = plane.NormalMm is not null,
                        ["code"] = "plane_modes_conflict",
                    });

            case CutPlaneFormVerdict.BaseUnsupported:
                // Объявлено в схеме как неподдержанное — тем же кодом и отказывает. Смещение названо
                // в details: объявленный параметр либо учитывается, либо о его роли сообщается, а не
                // молча теряется.
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Способ «базовая плоскость + смещение» не измерен на API7 (вспомогательная плоскость "
                    + "со смещением), поэтому не поддержан; задайте плоскость точкой и нормалью.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["base"] = plane.Base!.Value.ToString(),
                        ["offset_mm"] = plane.OffsetMm,
                        ["offset_applied"] = false,
                        ["code"] = "plane_base_unsupported",
                    });

            case CutPlaneFormVerdict.OffsetWithoutBase:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "offset_mm задан без base. Смещение отсчитывается от базовой плоскости, поэтому само "
                    + "по себе оно плоскости не задаёт: укажите base вместе с offset_mm либо задайте "
                    + "плоскость точкой и нормалью.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["offset_mm"] = plane.OffsetMm,
                        ["has_plane_ref"] = plane.PlaneRef is not null,
                        ["has_point"] = plane.PointMm is not null,
                        ["has_normal"] = plane.NormalMm is not null,
                        ["code"] = "plane_offset_without_base",
                    });

            default:
                throw new KompasContractException(
                    ErrorCodes.VerificationFailed,
                    $"Правило формы плоскости вернуло неизвестный исход: {CutPlaneForm.Validate(plane)}.",
                    RetryPolicy.Never);
        }
    }

    /// <summary>
    /// Плоскость операции: существующая опора либо точка + нормаль.
    /// </summary>
    /// <remarks>
    /// Способ «базовая плоскость + смещение» отвергается <c>CAPABILITY_UNAVAILABLE</c> с причиной, а
    /// не подставляется догадкой: маршрут смещённой вспомогательной плоскости API7 не измерен ни
    /// одним прогоном, а знак нормали базовой плоскости — это ровно то, что определяет, какую
    /// сторону отсечёт операция.
    /// </remarks>
    /// <remarks>
    /// Здесь разделены ТРИ разных исхода, и раньше они были свалены в один <c>GEOMETRY_FAILED</c>:
    /// <list type="bullet">
    /// <item>постановка невыразима (обе формы сразу, ни одной формы, нулевая или нечисловая
    /// нормаль) — <c>INVALID_ARGUMENT</c>, чинится клиентом;</item>
    /// <item>постановка выразима, но маршрут не поддержан («базовая плоскость + смещение») —
    /// <c>CAPABILITY_UNAVAILABLE</c>, как и обещано в описании поля схемы;</item>
    /// <item>ядро не построило плоскость по корректным данным — <c>GEOMETRY_FAILED</c>, и только
    /// этот исход остаётся возвратом <c>null</c> с причиной в <paramref name="failure"/>.</item>
    /// </list>
    /// Измерено 18.09.2026 строкой B3.17 приёмки: до этого разделения нулевая нормаль и плоскость
    /// без нормали отвечали <c>GEOMETRY_FAILED</c>, то есть «ядро не смогло» вместо «запрос
    /// невыразим». Для клиента это разные приглашения: повторить операцию против исправить аргумент.
    /// </remarks>
    private (IPlane3D? Plane, double[]? UnitNormal, double[]? Point) ResolveCutPlane(
        DocumentEntry document,
        Api7Bridge bridge,
        IModelContainer container,
        CutPlaneDto plane,
        out string? failure)
    {
        failure = null;

        GuardCutPlaneForm(plane);

        if (plane.PlaneRef is { Length: > 0 } reference)
        {
            if (plane.PointMm is not null || plane.NormalMm is not null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Плоскость задана и ссылкой, и точкой с нормалью — выберите одно.",
                    details: new Dictionary<string, object?>
                    {
                        ["plane_ref"] = reference,
                        ["has_point"] = plane.PointMm is not null,
                        ["has_normal"] = plane.NormalMm is not null,
                    });
            }

            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' не несёт объекта плоскости.",
                    details: new Dictionary<string, object?> { ["plane_ref"] = reference });
            }

            var (transferred, transferFailure) = Api7SolidPlane.TryTransfer(stored.Payload, bridge);
            if (transferred is null)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Плоскость по ссылке '{reference}' не получена: {transferFailure}",
                    details: new Dictionary<string, object?> { ["plane_ref"] = reference });
            }

            // Нормаль существующей плоскости не пересчитывается: она принадлежит модели, и
            // объявлять её своей означало бы отчитываться о числе, которого не измеряли.
            return (transferred, null, null);
        }

        if (plane.PointMm is not { Count: 3 } point || plane.NormalMm is not { Count: 3 } normal)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскость не задана: нужен plane_ref либо point_mm и normal_mm (по три числа).",
                details: new Dictionary<string, object?>
                {
                    ["has_point"] = plane.PointMm is not null,
                    ["has_normal"] = plane.NormalMm is not null,
                });
        }

        foreach (var value in point.Concat(normal))
        {
            if (!double.IsFinite(value))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Точка и нормаль плоскости обязаны быть конечными числами.",
                    details: new Dictionary<string, object?> { ["point_mm"] = point, ["normal_mm"] = normal });
            }
        }

        // Нулевая нормаль отвергается ДО COM: проверка берётся у PlaneBasis, а не пишется заново —
        // иначе правило «нормаль ненулевая» жило бы в двух местах и разошлось бы при первой правке.
        try
        {
            _ = PlaneBasis.FromNormal(normal);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскость невыразима: " + ex.Message,
                details: new Dictionary<string, object?> { ["normal_mm"] = normal });
        }

        var created = Api7SolidPlane.TryCreateByPointNormal(container, point, normal, name: null);
        if (created.Plane is null)
        {
            // Данные корректны, а плоскость не построилась — вот это уже отказ ядра.
            failure = created.Failure ?? "плоскость не построена";
            return (null, created.UnitNormal, new[] { point[0], point[1], point[2] });
        }

        return (created.Plane, created.UnitNormal, new[] { point[0], point[1], point[2] });
    }

    /// <summary>
    /// Тела документа с объёмом, габаритом, числом граней и признаком многокусочности.
    /// </summary>
    /// <remarks>
    /// Читается тем же маршрутом, что и <c>ReadBodySnapshots</c> (<c>refresh()</c> перед обходом,
    /// <c>CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()</c>, <c>GetGabarit</c>), но с двумя
    /// дополнительными полями, которых приёмке B3 не хватает: <c>MultiBodyParts</c> отличает «одно
    /// тело из двух кусков» от «одно тело целое», а <c>FaceCount</c> даёт независимый признак того же.
    /// </remarks>
    private List<SolidBodyDto> ReadSolidBodies(DocumentEntry document)
    {
        var rows = new List<SolidBodyDto>();
        var bodies = (ksBodyCollection)document.PartNow().BodyCollection();
        bodies.refresh();
        var count = bodies.GetCount();
        for (var i = 0; i < count; i++)
        {
            var element = AsInterface<ksBody>(bodies.GetByIndex(i))
                ?? (i == 0 ? AsInterface<ksBody>(document.PartNow().GetMainBody()) : null);
            if (element is null)
            {
                // Тело, которое не отвечает как ksBody, не пропускается: пропуск сдвинул бы нумерацию
                // и позволил бы принять чужое тело за результат.
                rows.Add(new SolidBodyDto
                {
                    BodyRef = References.Register("body_unresolved", document.Id, document.Revision, bodies.GetByIndex(i)).Id,
                    Kind = "unresolved",
                    VolumeMm3 = null,
                    Bbox = BoundingBoxDto.Empty,
                    FaceCount = -1,
                    MultiBodyParts = false,
                });
                continue;
            }

            var edgeCount = CountUniqueEdges(element, out var faces);
            rows.Add(new SolidBodyDto
            {
                BodyRef = References.Register("body", document.Id, document.Revision, element).Id,
                Kind = SafeIsSolid(element) ? "solid" : "sheet",
                VolumeMm3 = SafeDouble(() => MassProperties(element, (uint)KompasUnits.MassMmKg)?.v
                    ?? throw new InvalidCastException("объём недоступен")),
                Bbox = ReadBodyBox(element),
                FaceCount = faces,
                MultiBodyParts = SafeFlag(() => element.MultiBodyParts),
            });

            _ = edgeCount;
        }

        return rows;
    }

    /// <summary>
    /// Совпадает ли тело с результатом преобразования: сравнивается габарит, посчитанный ДО опыта по
    /// той же матрице. Сравнение по положению, а не по объёму: объём при переносе не меняется, и по
    /// нему «сдвинулось» и «осталось» неразличимы.
    /// </summary>
    private static bool MatchesMoved(SolidBodyDto row, double[] matrix, BodySnapshot? before)
    {
        if (before?.Min is not { Length: 3 } min || before.Max is not { Length: 3 } max)
        {
            return false;
        }

        var corners = new[]
        {
            new[] { min[0], min[1], min[2] }, new[] { max[0], min[1], min[2] },
            new[] { min[0], max[1], min[2] }, new[] { max[0], max[1], min[2] },
            new[] { min[0], min[1], max[2] }, new[] { max[0], min[1], max[2] },
            new[] { min[0], max[1], max[2] }, new[] { max[0], max[1], max[2] },
        };

        var images = corners.Select(c => RepositionMatrix.Apply(matrix, c)).ToList();
        var expected = new[]
        {
            images.Min(p => p[0]), images.Min(p => p[1]), images.Min(p => p[2]),
            images.Max(p => p[0]), images.Max(p => p[1]), images.Max(p => p[2]),
        };

        var actual = new[]
        {
            row.Bbox.MinMm[0], row.Bbox.MinMm[1], row.Bbox.MinMm[2],
            row.Bbox.MaxMm[0], row.Bbox.MaxMm[1], row.Bbox.MaxMm[2],
        };

        for (var i = 0; i < 6; i++)
        {
            if (!double.IsFinite(actual[i]) || Math.Abs(expected[i] - actual[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Состав дерева признаков НА МОМЕНТ СЪЁМКИ — то, чем «новый» отличается от «уже был».
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему не по имени.</b> Прежнее правило «новый = имя, которого не было в снимке» опиралось
    /// на уникальность ОТОБРАЖАЕМОГО имени. Клиентская приёмка 19.09.2026 измерила обратное: КОМПАС
    /// присваивает двум последовательным признакам изменения положения ОДНО И ТО ЖЕ имя
    /// «Изменение положения : Тело 1», поэтому второй признак отбрасывался фильтром, и
    /// <c>solid.reposition</c> отвечал <c>GEOMETRY_FAILED</c> при ВЕРНО построенной геометрии.
    /// </para>
    /// <para>
    /// <b>Что измерено вместо догадки</b> (проба I, <c>--identity</c>, 19.09.2026, прогон
    /// <c>c90961c6a3ba478697da5bc243040719</c>, отчёт <c>docs/acceptance/api7/feature-identity.json</c>):
    /// </para>
    /// <list type="number">
    /// <item>адрес элемента устойчив: два последовательных обхода коллекции 110 отдают в одном и том
    /// же индексе один и тот же COM-объект;</item>
    /// <item><c>ksEntityCollection.FindIt(entity)</c> отдаёт индекс элемента с нуля и <c>−1</c> для
    /// объекта, которого в коллекции нет;</item>
    /// <item>коллекция, взятая ДО операции, мутацию НЕ отслеживает: после двух операций она
    /// по-прежнему сообщает исходное число элементов и <c>FindIt = −1</c> для обоих новых признаков.
    /// Это и делает её снимком «что было до», а не вторым видом текущего состояния.</item>
    /// </list>
    /// <para>
    /// Отсюда правило адресации: элемент НОВЫЙ тогда и только тогда, когда удерживаемый снимок его
    /// не знает. Ни первого, ни последнего совпадения, ни фиксированного индекса, ни переименования
    /// здесь нет; при совпадающих именах различает идентичность COM-объекта, а не строка.
    /// </para>
    /// <para>
    /// <b>Отрицательный контроль.</b> Снимок, не знающий элемента, обязан отвечать <c>−1</c>, а
    /// знающий — его индекс. Оба исхода измерены в одном прогоне: элементы, существовавшие до
    /// операции, дали <c>0</c> и <c>1</c>, оба новых признака — <c>−1</c>. Если бы <c>FindIt</c>
    /// отвечал <c>−1</c> на всё, разность множеств объявила бы новыми все элементы и адрес был бы
    /// отвергнут как неоднозначный, а не выдан наугад.
    /// </para>
    /// </remarks>
    private sealed class FeatureTreeSnapshot
    {
        private readonly ksEntityCollection? _collection;

        private FeatureTreeSnapshot(ksEntityCollection? collection, IReadOnlyList<(ksEntity Entity, string Name)> elements)
        {
            _collection = collection;
            Elements = elements;
        }

        public IReadOnlyList<(ksEntity Entity, string Name)> Elements { get; }

        public int Count => Elements.Count;

        public static FeatureTreeSnapshot Capture(DocumentEntry document)
        {
            var collection = document.PartNow()
                .EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement)) as ksEntityCollection;
            var elements = new List<(ksEntity, string)>();
            if (collection is not null)
            {
                var count = collection.GetCount();
                for (var i = 0; i < count; i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity)
                    {
                        elements.Add((entity, entity.name ?? string.Empty));
                    }
                }
            }

            return new FeatureTreeSnapshot(collection, elements);
        }

        /// <summary>Индекс элемента в снимке или <c>−1</c>, если снимок его не знает.</summary>
        public int IndexOf(ksEntity entity)
        {
            if (_collection is null)
            {
                return -1;
            }

            try
            {
                return _collection.FindIt(entity);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Отказ поиска — это «не знаю», а не «знаю»: элемент считается новым, и адрес
                // либо опознается по типу, либо вызов честно отказывает. Тихий 0 здесь выдал бы
                // существующий элемент за новый.
                return -1;
            }
        }

        public bool WasPresent(ksEntity entity) => IndexOf(entity) >= 0;

        public string[] Names => Elements.Select(e => e.Name).ToArray();
    }

    /// <summary>
    /// Адрес только что созданного признака — ЭЛЕМЕНТ ДЕРЕВА API5, а не объект API7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Проба T (шаги TL.2, TL.6, TL.7, TL.9; прогон <c>1c111eff3cd94007b436c5a3862e48bc</c>) измерила:
    /// признак, созданный фабрикой API7, в дереве API5 ЕСТЬ, и именно он отвечает на подавление
    /// (<c>ksFeature.excluded</c>, объём 37 000 → 49 000 и обратно) и удаление (<c>DeleteObject</c>,
    /// тела 2 → 3). Но сам объект API7 — <c>IBoolean</c>, <c>ISplitSolid</c>, <c>ICut</c>,
    /// <c>IBodyReposition</c> — признаком API5 не является, и <c>RequireFeatureEntity</c> его
    /// отвергает. Ссылка на объект API7 сделала бы <c>discover</c>, <c>suppress_restore</c> и
    /// <c>delete_dependencies</c> невыполнимыми для всех одиннадцати строк — то есть четыре из
    /// десяти действий по каждой строке пришлось бы закрывать как «нет API».
    /// </para>
    /// <para>
    /// <b>Опознание — разностью множеств по идентичности COM-объекта</b> относительно
    /// <see cref="FeatureTreeSnapshot"/>, взятого до операции, плюс фильтр по ТИПУ признака. Ни
    /// порядок коллекции, ни отображаемое имя адресом не являются: имена у двух последовательных
    /// признаков одного вида СОВПАДАЮТ (измерено 19.09.2026, проба I), поэтому имя не различает
    /// ничего, а порядок не обещан.
    /// </para>
    /// <para>
    /// Кандидат обязан отвечать на <c>GetFeature()</c> как <c>ksFeature</c> (это отсекает
    /// вспомогательную геометрию — плоскость и точку, которые разделение и отсечение создают вместе
    /// с признаком) и быть РОВНО ОДНИМ. Ноль или несколько — честный отказ со списком, а не ссылка
    /// «на что-нибудь похожее»: подавить чужой признак означало бы молча испортить чужую геометрию.
    /// </para>
    /// <para>
    /// ПОЧЕМУ ФИЛЬТР ПО ТИПУ, А НЕ ПО СЧЁТУ. Первая редакция правила требовала «ровно один новый
    /// элемент», и на режиме <c>save_tools</c> это дало отказ при УСПЕШНО выполненной операции:
    /// <c>keep_tools=true</c> создаёт ДВА признака — саму операцию и вспомогательную «Копию тела»
    /// (измерено: <c>type=69 «Булева операция:1»</c> и <c>type=79 «Копия тела : Тело 1»</c>). Оба
    /// новых, оба отвечают <c>ksFeature</c>, и различить их счётом невозможно — различает тип
    /// операции. Числа взяты из измерения (<c>scratch/b3-measure-feature-types.py</c>,
    /// <c>kompas_list_features</c>), а не по аналогии: 69 — булева, 633 — разделение, 50 —
    /// отсечение, 79 — изменение положения.
    /// </para>
    /// </remarks>
    private (ksEntity Entity, string Name) RequireCreatedFeatureAddress(
        DocumentEntry document, FeatureTreeSnapshot before, int expectedTreeType, string tool)
    {
        var candidates = new List<(ksEntity Entity, string Name)>();
        var newNames = new List<string>();
        var newElements = new List<string>();
        if (document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement))
            is ksEntityCollection collection)
        {
            for (var i = 0; i < collection.GetCount(); i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                // «Был до» — по идентичности COM-объекта, а не по строке имени.
                if (before.WasPresent(entity) || entity.GetFeature() is not ksFeature)
                {
                    continue;
                }

                var name = entity.name ?? string.Empty;
                newNames.Add(name);
                newElements.Add($"[{i}] «{name}» type={entity.type}");
                if (entity.type != expectedTreeType)
                {
                    continue;
                }

                candidates.Add((entity, name));
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            $"После '{tool}' новый признак в дереве API5 опознан неоднозначно: подходящих "
            + $"{candidates.Count} ({string.Join(", ", candidates.Select(c => $"«{c.Name}»"))}). "
            + "Операция выполнена, но адрес признака не получен: подавление, удаление и чтение "
            + "признака по этой ссылке недоступны.",
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["candidates"] = candidates.Select(c => c.Name).ToArray(),
                ["expected_tree_type"] = expectedTreeType,
                ["new_names"] = newNames.ToArray(),
                ["new_elements"] = newElements.ToArray(),
                ["names_before"] = before.Names.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                ["elements_before"] = before.Count,
            });
    }

    /// <summary>
    /// Совпадает ли прочитанное тело со снимком «до»: объём по допуску профиля И габарит по каждой
    /// оси. Одного объёма мало: у разных тел объём может совпасть (в эталоне B3 объём цели и объём
    /// инструмента одинаковы — 24 000), и тогда «нетронутое» и «часть» перепутались бы.
    /// </summary>
    /// <remarks>
    /// Отсутствие габарита в снимке — это «сравнить нечем», а не «совпало»: возвращается
    /// <c>false</c>, и вызывающий увидит тело как неопознанное. Тихий <c>true</c> здесь означал бы
    /// тело, причисленное к нетронутым по одному лишь объёму.
    /// </remarks>
    private static bool MatchesSnapshot(SolidBodyDto row, BodySnapshot snapshot)
    {
        if (snapshot.Volume is double volume
            && row.VolumeMm3 is double actual
            && !VolumeMatches(actual, volume))
        {
            return false;
        }

        if (snapshot.Min is not { Length: 3 } min || snapshot.Max is not { Length: 3 } max)
        {
            return false;
        }

        var box = row.Bbox;
        if (box?.MinMm is not { Count: 3 } actualMin || box.MaxMm is not { Count: 3 } actualMax)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(actualMin[i] - min[i]) > 0.01d || Math.Abs(actualMax[i] - max[i]) > 0.01d)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// УСТАРЕЛО 19.09.2026. Прежний признак «тело не тронуто» для отсечения: сравнение объёма
    /// кандидата с объёмом ЦЕЛИ до операции. Этим постороннее тело не опознаётся вовсе (у него
    /// другой объём), поэтому все остальные тела объявлялись нетронутыми БЕЗ доказательства, а
    /// исчезнувшее тело в список кандидатов не попадало. Заменено сопоставлением состава тел
    /// (<c>CompareBodySnapshots</c> + <c>UnchangedViolations</c> + <c>NewBodies</c>) в
    /// <c>SolidCutByPlane</c>. Оставлено как запись о прежнем маршруте; ни один вызов на него не
    /// ссылается — если ссылка появится, это возврат дефекта
    /// <c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>.
    /// </summary>
    private static bool IsUntouched(SolidBodyDto row, List<BodySnapshot> before, int targetIndex)
    {
        var snapshot = before.FirstOrDefault(b => b.Index == targetIndex);
        if (snapshot?.Volume is not double volume || row.VolumeMm3 is not double actual)
        {
            return false;
        }

        return VolumeMatches(actual, volume);
    }

    /// <summary>
    /// Совпадает ли тело хотя бы с одним снимком «до» — по ГАБАРИТУ И ОБЪЁМУ, а не по месту в
    /// коллекции: тело-цель операция потребляет, и часть занимает её индекс.
    /// </summary>
    private static bool MatchesAnySnapshot(SolidBodyDto row, List<BodySnapshot> before) =>
        before.Any(b => MatchesSnapshot(row, b));

    private static string BodyVolumesText(List<BodySnapshot> snapshots) =>
        NumberListText(snapshots.Select(b => b.Volume).ToList());

    private static BoundingBoxDto Box(BodySnapshot? snapshot) =>
        snapshot?.Min is { Length: 3 } min && snapshot.Max is { Length: 3 } max
            ? new BoundingBoxDto(min, max)
            : BoundingBoxDto.Empty;

    /// <summary>
    /// Совпадает ли прочитанный габарит с объявленным аналитически. Допуск — допуск профиля по
    /// длине: 0,01 мм абсолютно или 1e-6 относительно. Отсутствие любой из сторон — «сравнить
    /// нечем», а не «совпало»: тихий <c>true</c> здесь выдал бы непроверенное положение за
    /// проверенное.
    /// </summary>
    private static bool BoxMatches(BoundingBoxDto? actual, BoundingBoxDto expected)
    {
        if (actual?.MinMm is not { Count: 3 } actualMin || actual.MaxMm is not { Count: 3 } actualMax
            || expected.MinMm is not { Count: 3 } expectedMin || expected.MaxMm is not { Count: 3 } expectedMax)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!double.IsFinite(actualMin[i]) || !double.IsFinite(actualMax[i]))
            {
                return false;
            }

            var tolerance = Math.Max(0.01d, 1e-6 * Math.Abs(expectedMax[i]));
            if (Math.Abs(actualMin[i] - expectedMin[i]) > tolerance
                || Math.Abs(actualMax[i] - expectedMax[i]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Габарит текстом — для сообщений об отказе и полей проверок.</summary>
    private static string BoxText(BoundingBoxDto? box)
    {
        if (box?.MinMm is not { Count: 3 } min || box.MaxMm is not { Count: 3 } max)
        {
            return "<габарит не прочитан>";
        }

        return "(" + string.Join(",", min) + ")…(" + string.Join(",", max) + ")";
    }

    private static double SumVolumes(List<BodySnapshot> snapshots) =>
        snapshots.Sum(s => s.Volume ?? 0d);

    private static double? SumVolumesOrNull(List<BodySnapshot> snapshots) =>
        snapshots.Any(s => s.Volume is null) ? null : snapshots.Sum(s => s.Volume ?? 0d);

    /// <summary>
    /// Сравнение объёмов по допуску профиля: 0,01 мм³ абсолютно или 1e-6 относительно.
    /// </summary>
    private static bool VolumeMatches(double actual, double expected)
    {
        var delta = Math.Abs(actual - expected);
        return delta <= 0.01d || delta <= 1e-6 * Math.Abs(expected);
    }

    private static List<SolidBodyDto> MatchSavedTools(
        List<SolidBodyDto> rows, List<BodyTarget> tools)
    {
        // Сохранённые инструменты ищутся по габариту ДО операции: они остаются на прежнем месте, и
        // «тот же объём» их не отличает от результата, когда результат совпал по объёму с телом.
        var matched = new List<SolidBodyDto>();
        foreach (var tool in tools)
        {
            var snapshot = tool.Snapshot;
            if (snapshot?.Min is not { Length: 3 } min || snapshot.Max is not { Length: 3 } max)
            {
                continue;
            }

            var row = rows.FirstOrDefault(r =>
                Near(r.Bbox.MinMm, min) && Near(r.Bbox.MaxMm, max)
                && snapshot.Volume is double v && r.VolumeMm3 is double a && VolumeMatches(a, v));
            if (row is not null)
            {
                matched.Add(row);
            }
        }

        return matched;
    }

    private static bool Near(IReadOnlyList<double> actual, double[] expected)
    {
        if (actual.Count != 3)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!double.IsFinite(actual[i]) || Math.Abs(actual[i] - expected[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Логический флаг тела, где «не прочитано» честно превращается в <c>false</c> только потому, что
    /// поле обязательное. Отсутствие чтения здесь не искажает приёмку: <c>MultiBodyParts</c> —
    /// ДОПОЛНИТЕЛЬНЫЙ признак, а основной (<c>FaceCount</c>) читается отдельно и независимо.
    /// </summary>
    private static bool SafeFlag(Func<bool> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return false;
        }
    }

    // =============================================================================================
    // Правка признаков B3 (наряд §5, §7: действие edit)
    // =============================================================================================

    private const string RepositionFamily = "reposition";

    private const string SplitFamily = "split";

    private const string CutByPlaneFamily = "cut_by_plane";

    private const string BooleanFamily = "boolean";

    /// <summary>
    /// Позиция элемента дерева среди признаков ТОГО ЖЕ ТИПА и общее число таких признаков в дереве.
    /// </summary>
    /// <param name="Ordinal">Сколько признаков этого типа стоит в дереве до цели; <c>null</c> — цель
    /// среди них не найдена. Ноль и «не найдено» — разные исходы, и смешивать их нельзя.</param>
    /// <param name="Count">Сколько всего признаков этого типа в дереве; <c>-1</c> — прочитать не
    /// удалось.</param>
    private readonly record struct SameTypeScan(int? Ordinal, int Count);

    /// <summary>
    /// Позиция элемента дерева среди признаков ТОГО ЖЕ ТИПА — сколько таких признаков стоит в дереве
    /// до него. Это адрес элемента в коллекции API7 той же операции.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему позиция, а не имя.</b> Имя в API5 и имя в API7 для одного и того же объекта
    /// расходятся — это измерено на скруглении (F.8: имя, заданное в API5, в API7 читается иначе),
    /// и по той же причине <c>Api7Fillet.FindIndexesByIdenticalRadius</c> и
    /// <c>Api7Rotated.FindIndexFor</c> сопоставляют по ЗНАЧЕНИЮ, а не по имени. У признаков B3
    /// значения-идентификатора, известного до правки, нет вовсе (операнды булевой операции после
    /// объединения потреблены, плоскость разделения — вспомогательный объект), поэтому адрес берётся
    /// по позиции. Это тот же приём, которым правится вращение, когда сущность дерева не отвечает
    /// на <c>QI(IRotated)</c> (<c>RotatedOrdinal</c>), и он проверяется геометрией в приёмке:
    /// правка не того признака не даст ожидаемого объёма и габарита.
    /// </para>
    /// <para>
    /// Число признаков возвращается ВМЕСТЕ с позицией, а не отдельным обходом: два обхода одной
    /// коллекции могли бы разойтись между собой, а решение о пригодности адреса принимается по обоим
    /// числам сразу (<see cref="RequireSameTypeIndex"/>).
    /// </para>
    /// </remarks>
    private static SameTypeScan ScanSameType(ksPart part, ksEntity target, int type)
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
                var seen = 0;
                int? found = null;
                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is not ksEntity candidate || candidate.type != type)
                    {
                        continue;
                    }

                    seen++;
                    if (ReferenceEquals(candidate, target))
                    {
                        found = ordinal;
                    }
                    else if (found is null)
                    {
                        ordinal++;
                    }
                }

                if (seen > 0)
                {
                    // Признаки этого типа найдены; если цели среди них нет — второго прохода по
                    // другой коллекции быть не должно: это значило бы, что цель лежит в другом месте.
                    return new SameTypeScan(found, seen);
                }
            }

            return new SameTypeScan(null, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new SameTypeScan(null, -1);
        }
    }

    /// <summary>
    /// Индекс признака в коллекции API7 той же операции — или честный отказ, если сопоставление не
    /// доказано.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему мало «позиция меньше числа элементов».</b> Позиция в дереве и индекс в коллекции
    /// API7 — это два РАЗНЫХ списка, и совпадают они только тогда, когда признаков этого типа в
    /// дереве ровно столько же, сколько элементов в коллекции. У изменения положения это условие
    /// нарушается измеренно: номер <c>79</c> носит не только «Изменение положения», но и
    /// вспомогательная «Копия тела», которую создаёт режим сохранения инструментов булевой операции
    /// (<c>scratch/b3-measure-feature-types.py</c>, 18.09.2026). В документе с копией позиция
    /// «Изменение положения» перестаёт быть индексом в <c>BodyRepositions</c>, и запись ушла бы в
    /// ЧУЖОЙ признак. Поэтому при расхождении чисел вызов отвергается до мутации.
    /// </para>
    /// <para>
    /// Цена отказа: правка признака, рядом с которым живёт признак того же номера, но другой
    /// операции, не выполняется. Это выбранная сторона — отказ предпочтён записи не в тот объект,
    /// потому что «применилось не туда» по ответу неотличимо от «применилось».
    /// </para>
    /// </remarks>
    private static int RequireSameTypeIndex(
        DocumentEntry document,
        ksEntity entity,
        int treeType,
        int? api7Count,
        string family,
        string collectionName)
    {
        var position = ScanSameType(document.PartNow(), entity, treeType);
        var ordinal = position.Ordinal;
        var treeCount = position.Count;

        if (ordinal is not int index || api7Count is not int total || treeCount != total
            || index < 0 || index >= total)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Признак ({family}) не сопоставлен с элементом коллекции API7 {collectionName}: "
                + $"позиция среди признаков этого типа в дереве — {Describe(ordinal)}, признаков этого "
                + $"типа в дереве — {Describe(treeCount)}, элементов в коллекции — {Describe(api7Count)}. "
                + "Правка не выполняется: записать параметр в чужой признак значило бы изменить не тот "
                + "объект. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["code"] = family + "_feature_not_matched",
                    ["ordinal"] = ordinal,
                    ["tree_count"] = treeCount,
                    ["api7_count"] = api7Count,
                    ["tree_type"] = treeType,
                });
        }

        return index;
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕГО признака изменения положения по <c>kompas_update_feature</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> (проба <c>--reposition</c>, шаг RP.6): правка признака[0]
    /// повторной записью того же вектора оставляет габарит <c>(17,−11,13)…(37,−1,18)</c>, а возврат
    /// вектора в ноль возвращает тело домой — параметр применяется к ИСХОДНЫМ входам, а не к
    /// текущему положению. Это и есть требование наряда §5, и повторный <c>kompas_reposition</c> ему
    /// не удовлетворяет: он создаёт второй признак и накапливает смещение.
    /// </para>
    /// <para>
    /// <b>Успешный <c>Update()</c> доказательством не является.</b> Шаг RP.2 измерил три маршрута из
    /// четырёх, которые вернули <c>true</c> и тело не двинули. Поэтому перенос ЧИТАЕТСЯ ОБРАТНО
    /// (<c>Position.X/Y/Z</c>) и сверяется с матрицей, которую просили записать: при накоплении
    /// смещения чтение дало бы удвоенное значение. Объём при жёстком преобразовании обязан
    /// сохраниться, и это тоже проверяется — по нему «сдвинулось» и «осталось» неразличимы, но
    /// «преобразование осталось жёстким» видно.
    /// </para>
    /// <para>
    /// Проверки «поля других семейств отвергаются» здесь те же, что у остальных правимых семейств:
    /// молча применить половину запроса хуже отказа.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateSolidReposition(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            RepositionFamily,
            "reposition_kind, reposition_vector_mm и поля оси поворота (reposition_axis_point_mm, "
            + "reposition_axis_direction_mm / reposition_axis_point2_mm, reposition_angle_deg)");

        if (command.RepositionKind is not RepositionKind kind)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка изменения положения требует reposition_kind (translate или rotate): без вида "
                + "преобразования непонятно, что именно менять, а угадывать по наличию полей значило бы "
                + "принимать решение за вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = RepositionFamily });
        }

        // Проверки состава полей переиспользуются ДОСЛОВНО из маршрута создания: те же имена, те же
        // правила (вектор — три конечных числа; ось — либо направление, либо вторая точка, но не обе
        // и не ни одной; угол обязателен). Дублировать их здесь значило бы завести второе место, где
        // они могут разойтись.
        var synthetic = new RepositionCommand
        {
            DocumentId = document.Id,
            TargetBodyRef = command.FeatureRef,
            Kind = kind,
            VectorMm = command.RepositionVectorMm,
            AxisPointMm = command.RepositionAxisPointMm,
            AxisDirectionMm = command.RepositionAxisDirectionMm,
            AxisPoint2Mm = command.RepositionAxisPoint2Mm,
            AngleDeg = command.RepositionAngleDeg,
            ExpectedRevision = command.ExpectedRevision,
        };

        var matrix = kind switch
        {
            RepositionKind.Translate => BuildTranslation(synthetic),
            RepositionKind.Rotate => BuildRotation(synthetic),
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"kind '{kind}' не распознан.",
                details: new Dictionary<string, object?> { ["kind"] = kind.ToString() }),
        };

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.reposition.update");

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.BodyRepositionFeature,
            Api7SolidReposition.Count(container),
            RepositionFamily,
            "BodyRepositions");

        var before = Api7SolidReposition.ReadPlacement(container, index);
        var written = Api7SolidReposition.TryWrite(container, index, matrix);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Положение не записано: " + (written.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.reposition.update");

        var readBack = Api7SolidReposition.ReadPlacement(container, index);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        var wanted = new[] { matrix[12], matrix[13], matrix[14] };

        // ПРАВКА ЧИТАЕТСЯ ОБРАТНО И СВЕРЯЕТСЯ ПО МАТРИЦЕ — тем же основанием, что и создание
        // (см. RequirePlacementRoundTrip). Здесь это ещё и прямая проверка наряда §5: параметр обязан
        // применяться к ИСХОДНЫМ входам признака, а не накапливаться. Накопление дало бы расхождение
        // ровно на величину предыдущего преобразования, то есть обнаружимо по матрице.
        var readBackDifference = PlacementDifference(readBack.Reading, matrix);
        if (readBackDifference is double readBackGap && readBackGap > PlacementRoundTripTolerance)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Правка не подтвердилась чтением: размещение, собранное из прочитанных параметров, "
                + $"расходится с заданным на {readBackGap:R} по матрице (вид — "
                + $"{kind.ToString().ToLowerInvariant()}). Это признак того, что параметр применён не к "
                + "исходным входам признака.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["matrix_difference"] = readBackGap,
                    ["requested_matrix"] = matrix,
                    ["read_back_angles_deg"] = readBack.Reading?.AnglesDeg,
                    ["read_back_displacement_mm"] = readBack.Reading?.DisplacementMm,
                    ["read_back_before_angles_deg"] = before.Reading?.AnglesDeg,
                    ["read_back_before_displacement_mm"] = before.Reading?.DisplacementMm,
                });
        }

        // Жёсткое преобразование объём не меняет. Это не «ожидание из наряда», а инвариант: если
        // объём изменился, записанное положение не является преобразованием положения.
        var volumePreserved = volumeBefore is double v0 && volumeAfter is double v1
            && Math.Abs(v1 - v0) <= ProfileArea.Tolerance(v0);
        var featureCountPreserved = featuresAfter == featuresBefore;
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);

        var rowsAfter = ReadSolidBodies(document);
        var moved = command.ExpectedBboxMm is null
            ? null
            : rowsAfter.FirstOrDefault(r => BoxMatches(r.Bbox, command.ExpectedBboxMm));

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featureCountPreserved,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
            new("volume_preserved", volumePreserved, Observed: Num(volumeAfter), Expected: Num(volumeBefore)),
        };

        if (command.ExpectedBboxMm is not null)
        {
            checks.Add(new NamedCheck(
                "bbox_expected", moved is not null,
                Observed: rowsAfter.Count == 0 ? "<тел нет>" : BoxText(rowsAfter[0].Bbox),
                Expected: BoxText(command.ExpectedBboxMm)));
        }

        if (command.ExpectedVolumeMm3 is double expected)
        {
            checks.Add(new NamedCheck(
                "volume_expected",
                volumeAfter is double measured && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected),
                Observed: Num(volumeAfter),
                Expected: Num(expected)));
        }

        var unverified = new List<string>();
        if (command.ExpectedVolumeMm3 is null)
        {
            // Формулировка не должна противоречить уровню: при объявленном габарите правка
            // подтверждена геометрически, но подтверждено ИМЕННО ПОЛОЖЕНИЕ, а не неизменность
            // объёма. Сказать «не подтверждена геометрически» значило бы соврать в другую сторону.
            unverified.Add(command.ExpectedBboxMm is null
                ? "expected_volume_not_supplied — без аналитического ожидания объёма правка не может "
                  + "быть подтверждена геометрически"
                : "expected_volume_not_supplied — объём не объявлен, поэтому подтверждено положение "
                  + "(габарит), но НЕ неизменность объёма: у жёсткого преобразования объём обязан "
                  + "сохраняться, и это осталось непроверенным");
        }

        if (command.ExpectedBboxMm is null)
        {
            unverified.Add("expected_bbox_not_supplied — у жёсткого преобразования объём инвариантен, "
                           + "поэтому положение подтверждает только габарит (expected_bbox_mm)");
        }

        if (readBack.Reading is null)
        {
            unverified.Add("position_read_back_failed: "
                + (readBack.Failure ?? "причина не сообщена"));
        }
        else if (readBackDifference is null)
        {
            // Прочиталось, но НЕ тем маршрутом: у признака нет параметрического представления
            // (OrientationType ≠ ksEulerCorners либо перенос записан не смещением). Это не «нет
            // данных», а названная причина, и она отличается от отказа чтения.
            unverified.Add("position_read_back_not_parametric — параметры размещения прочитаны не "
                + "маршрутом углов Эйлера: OrientationType=" + readBack.Reading.OrientationType
                + ", ParameterType=" + readBack.Reading.ParameterType);
        }

        if (command.ExpectedBboxMm is not null && moved is null)
        {
            // Габарит объявлен вызывающим, и он не совпал — это отказ, а не «не проверено».
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Правка выполнена, но тело не встало в объявленный габарит. Просили перенос "
                + $"({string.Join(",", wanted)}); габарит после правки — "
                + (rowsAfter.Count == 0 ? "<тел нет>" : BoxText(rowsAfter[0].Bbox))
                + ", ожидался " + BoxText(command.ExpectedBboxMm) + ". Это признак того, что параметр "
                + "применён не к исходным входам признака (наряд §5 запрещает накопление смещения).",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_translation"] = wanted,
                    ["read_back_displacement_mm"] = readBack.Reading?.DisplacementMm,
                    ["read_back_angles_deg"] = readBack.Reading?.AnglesDeg,
                    ["read_back_before_displacement_mm"] = before.Reading?.DisplacementMm,
                    ["bbox_after"] = rowsAfter.Count == 0 ? null : BoxText(rowsAfter[0].Bbox),
                    ["expected_bbox"] = BoxText(command.ExpectedBboxMm),
                });
        }

        // Объявленное ожидание не совпало — это отказ, а не «не проверено». Вернуть «успех с
        // пониженным уровнем» значило бы выдать недостигнутое за достигнутое: у правки есть
        // вызывающий, который объявил число, и он вправе узнать, что число не получено.
        if (command.ExpectedVolumeMm3 is double declared
            && (volumeAfter is not double measuredAfter
                || Math.Abs(measuredAfter - declared) > ProfileArea.Tolerance(declared)))
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Правка выполнена, но объём не совпал с объявленным: ожидалось " + Num(declared)
                + ", измерено " + Num(volumeAfter) + ". У жёсткого преобразования объём — инвариант, "
                + "поэтому расхождение означает, что записанное преобразование не является "
                + "преобразованием положения.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["expected_volume_mm3"] = declared,
                    ["volume_after_mm3"] = volumeAfter,
                    ["volume_before_mm3"] = volumeBefore,
                    ["bbox_after"] = rowsAfter.Count == 0 ? null : BoxText(rowsAfter[0].Bbox),
                });
        }

        // Уровень «геометрия проверена» означает ровно одно: объявленное вызывающим ожидание СРАВНЕНО
        // с моделью и совпало. Требовать при этом именно объём — дефект, и он измерен: у жёсткого
        // преобразования объём ИНВАРИАНТ, и положение подтверждает ГАБАРИТ (это же сказано строкой
        // выше в unverified), а первая редакция требовала ExpectedVolumeMm3, поэтому вызов, объявивший
        // ОДИН габарит и получивший его, отвечал level=call_returned. То есть совпавшее свидетельство
        // объявлялось необъявленным, и вызывающий не мог отличить «проверено и совпало» от «не
        // проверялось». Найдено строкой B3.27 приёмки (наряд §6.6), исправлено здесь.
        var declaredExpectation = DeclaresExpectation(command);
        var geometryConfirmed = declaredExpectation && checks.TrueForAll(c => c.Passed);

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            RepositionFamily,
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

    // =============================================================================================
    // Правка семейств SM-16 (разделение и отсечение) — действие edit наряда §7
    // =============================================================================================

    /// <summary>
    /// Поле контракта, принадлежащее семействам B3: имя как в схеме, семейства-владельцы и чтение
    /// значения из команды.
    /// </summary>
    /// <param name="Name">Имя поля в схеме инструмента (snake_case), а не имя свойства C#.</param>
    /// <param name="Owners">Семейства, которым поле разрешено. Их может быть несколько: опора
    /// <c>plane</c> принадлежит и разделению, и отсечению.</param>
    /// <param name="Read">Чтение значения: <c>null</c> означает «поле не передано», и это отличается
    /// от «передано и отвергнуто».</param>
    private sealed record SolidField(string Name, string[] Owners, Func<UpdateFeatureCommand, object?> Read);

    /// <summary>
    /// Все поля <see cref="UpdateFeatureCommand"/>, принадлежащие семействам B3, — ОДНА таблица на
    /// два вопроса: «кто владеет полем» и «что в нём лежит».
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему одна таблица, а не две.</b> Первая редакция держала владельцев в словаре, а значения
    /// — в отдельном перечислителе, и эти две структуры могли разойтись. Разошлись бы они молча:
    /// поле, добавленное в перечислитель без записи в словаре, не отвергалось НИКОГДА, потому что
    /// <c>TryGetValue</c> возвращал <c>false</c> и условие «поле чужое» коротко замыкалось в
    /// <c>false</c>. Это ровно тот же класс дефекта, что П5 (объявленное, но проглоченное поле),
    /// только с другой стороны: там поле забыли внести в список запрещённых, здесь — в список
    /// известных, а исход один — поле принимается и не применяется. Теперь разойтись нечему:
    /// перечислитель ходит по этой же таблице.
    /// </para>
    /// <para>
    /// <b>Почему перечень, а не «список запрещённого».</b> Перечень того, что бывает в команде,
    /// меняется вместе с контрактом, и умолчание здесь обязано быть «не отвергай»: поле, не
    /// приписанное ни одному семейству, отвергается не здесь, а своим семейством или проверкой
    /// неприменимых параметров ниже. Поэтому полнота таблицы проверяется отдельно — тестом
    /// <c>SolidFeatureClassificationTests</c>, который сверяет её с самим контрактом: новое поле
    /// команды не пройдёт, пока не будет отнесено к семейству, к неприменимым, к адресации или к
    /// ожиданиям геометрии.
    /// </para>
    /// </remarks>
    private static readonly SolidField[] SolidFields =
    {
        new("operation", new[] { BooleanFamily }, c => c.Operation),
        new("plane", new[] { SplitFamily, CutByPlaneFamily }, c => c.Plane),
        new("keep_side", new[] { CutByPlaneFamily }, c => c.KeepSide),
        new("target_body_ref", new[] { CutByPlaneFamily }, c => c.TargetBodyRef),
        new("expected_part_volumes_mm3", new[] { SplitFamily }, c => c.ExpectedPartVolumesMm3),
        new("reposition_kind", new[] { RepositionFamily }, c => c.RepositionKind),
        new("reposition_vector_mm", new[] { RepositionFamily }, c => c.RepositionVectorMm),
        new("reposition_axis_point_mm", new[] { RepositionFamily }, c => c.RepositionAxisPointMm),
        new("reposition_axis_direction_mm", new[] { RepositionFamily }, c => c.RepositionAxisDirectionMm),
        new("reposition_axis_point2_mm", new[] { RepositionFamily }, c => c.RepositionAxisPoint2Mm),
        new("reposition_angle_deg", new[] { RepositionFamily }, c => c.RepositionAngleDeg),
        // Поле семейства МАССИВА (очередь B4) — приписано своему семейству, а не «неприменимым».
        // Это исправление НАЙДЕННОГО дефекта, а не украшение: поле pattern добавлено в контракт
        // очередью B4, но ни в одну таблицу ролей не попало, поэтому проверка полноты
        // SolidFeatureClassificationTests.EveryCommandProperty_IsClassifiedExactlyOnce падала на нём
        // — и падала, судя по дате последнего прогона, с момента появления поля. Здесь оно получает
        // роль «семейственное»: ветка массива возвращается РАНЬШЕ всех остальных, поэтому признак B3
        // до неё не доходит, а признак массива с чужим полем отвергает ForeignFamilyFields.
        new("pattern", new[] { PatternFamily }, c => c.Pattern),
        // Поля семейства ОТВЕРСТИЯ (наряд SM07 §3.2, очередь B2). Приписаны своему семейству по тому
        // же основанию, что и pattern: признак отверстия их ЧИТАЕТ (своей веткой по типу дерева 583),
        // а признаки остальных семейств обязаны их отвергнуть — и отвергают, потому что перечень
        // строится по этой же таблице. Прежде эти поля не были объявлены НИГДЕ: вызов с ними на
        // чужом признаке был бы принят и проглочен, а на отверстии — отвергнут
        // CAPABILITY_UNAVAILABLE с текстом «этот признак — null (type=583)», что и измерено строкой
        // F08.16.edit до этой правки (docs/STATUS.md).
        new("diameter_mm", new[] { HoleFamily }, c => c.DiameterMm),
        new("counterbore_diameter_mm", new[] { HoleFamily }, c => c.CounterboreDiameterMm),
        new("counterbore_depth_mm", new[] { HoleFamily }, c => c.CounterboreDepthMm),
        new("countersink_diameter_mm", new[] { HoleFamily }, c => c.CountersinkDiameterMm),
        new("countersink_angle_deg", new[] { HoleFamily }, c => c.CountersinkAngleDeg),
        // Ожидание дельты объёма — тоже поле ЭТОГО семейства, а не общее: его читает только ветка
        // отверстия. Объявить его «общим ожиданием» значило бы принять его на переносе и булевой
        // операции и молча не применить — тот самый класс дефекта, что у keep_side (П5).
        new("expected_volume_delta_mm3", new[] { HoleFamily }, c => c.ExpectedVolumeDeltaMm3),
    };

    /// <summary>
    /// Объявлено ли вызывающим аналитическое ожидание геометрии — объём и/или габарит.
    /// </summary>
    /// <remarks>
    /// Правило вынесено в одно место потому, что им определяются ДВА разных исхода: булева правка БЕЗ
    /// ожидания отвергается сразу, а правка переноса С ожиданием получает уровень «геометрия
    /// проверена». Пока это условие стояло записанным дважды, оно уже разошлось — и разошлось ровно
    /// настолько, чтобы объявленный и совпавший габарит отчитывался как необъявленный (дефект П6,
    /// найден строкой B3.27 приёмки). Разница была в одном слове: в переносе уровень требовал именно
    /// объём, хотя габарит подтверждает положение не хуже.
    /// </remarks>
    private static bool DeclaresExpectation(UpdateFeatureCommand command) =>
        command.ExpectedVolumeMm3 is not null || command.ExpectedBboxMm is not null;

    /// <summary>
    /// Отказ на поля ЧУЖИХ семейств B3: определение признака перечисляется ПОЛНОСТЬЮ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему перечень, а не «список запрещённого».</b> Первая редакция перечисляла запрещённые
    /// поля руками, и от этого уже пострадала: <c>keep_side</c> в перечень не попал, поэтому вызов
    /// <c>plane + keep_side</c> на признаке РАЗДЕЛЕНИЯ принимался, а <c>keep_side</c> молча
    /// игнорировался — ровно тот класс дефекта, против которого написано правило «параметр, не
    /// объявленный в схеме, до COM не доходит» (§9.1 П4), только с другой стороны: объявленный, но
    /// проглоченный. Здесь каждое семейственное поле обязано быть ПРИПИСАНО семейству
    /// (<see cref="SolidFields"/>), и поле, переданное не своему семейству, отвергается.
    /// </para>
    /// <para>
    /// <b>Чего эта проверка не обещает.</b> Она отвергает переданное поле, но НЕ доказывает, что
    /// перечень семейственных полей полон: полноту держит тест <c>SolidFeatureClassificationTests</c>,
    /// сверяющий таблицу с самим контрактом. Раньше здесь стояло утверждение, что таблица и
    /// перечислитель «сверяются при отказе»; это было неверно — они не сверялись нигде, и расхождение
    /// между ними было молчаливым. Строка заменена на описание того, что проверка действительно
    /// делает.
    /// </para>
    /// <para>
    /// Поля, не принадлежащие ни одному семейству B3 (выдавливание, фаска, скругление, вращение),
    /// проверяются отдельно ниже: они не «чужое семейство», а просто не применимы к признаку B3.
    /// </para>
    /// </remarks>
    /// <param name="ownFields">
    /// Имена полей, которые ЭТО семейство читает, хотя таблица ролей отдаёт их другому ведомству.
    /// Заведено 20.09.2026 нарядом SM07 §3.2 ради одного измеренного случая: <c>depth_mm</c> —
    /// поле ВЫДАВЛИВАНИЯ в таблице и одновременно СВОЁ поле глухого отверстия (<c>blind_flat</c>).
    /// Без этого списка правка глухого отверстия отвергалась INVALID_ARGUMENT ещё до COM — и это
    /// ИЗМЕРЕНО на первой поставке с веткой отверстия (строки F08.15/16/19/20.edit, прогон
    /// 20.09.2026): глубина читается как чужое поле, хотя её читает ветка отверстия. Длину списка
    /// держит не «здравый смысл», а приёмка: с ним глухое отверстие правится, а цековка и зенковка
    /// с <c>depth_mm</c> по-прежнему отвергаются — но уже ПО РЕЖИМУ (<c>ValidateHoleEdit</c>), где
    /// это и измерено (HO.13/HO.16).
    /// </param>
    private static void RejectForeignSolidFields(
        UpdateFeatureCommand command,
        string family,
        string allowed,
        IReadOnlyCollection<string>? ownFields = null)
    {
        bool Owned(string name) => ownFields is not null
                                   && ownFields.Contains(name, StringComparer.Ordinal);

        var foreign = SolidFields
            .Where(f => f.Read(command) is not null
                        && !f.Owners.Contains(family, StringComparer.Ordinal)
                        && !Owned(f.Name))
            .Select(f => f.Name)
            .ToList();

        if (foreign.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — {family}: к нему применяются только {allowed}. Переданы поля других "
                + $"семейств: {string.Join(", ", foreign)}.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["family"] = family,
                    ["foreign_fields"] = foreign,
                });
        }

        if ((command.DepthMm is not null && !Owned("depth_mm"))
            || command.EndCondition is not null || command.SketchRef is not null
            || command.Distance1Mm is not null || command.Distance2Mm is not null || command.AngleDeg is not null
            || command.Direction is not null || command.RadiusMm is not null || command.EdgeRefs is not null
            || command.BaseObjectRefs is not null || command.RotationAngleDeg is not null
            || command.RotationDirection is not null
            // Очередь B5 (кинематика, сечения, оболочка) — тоже чужие поля для признаков B3.
            // `couplings` дописан 20.09.2026: очередь B5 добавила ШЕСТЬ правимых полей, и пять из
            // них попали в перечни чужих полей, а шестое — нет. Нашёл это unit-тест
            // SolidFeatureClassificationTests (поле без роли = поле, которое адаптер примет и
            // проглотит), а зонд scratch/_couplings_scope_probe.py измерил сам проглатывание:
            // вызов «правка признака + couplings» возвращал успех, геометрия менялась, а цепочки не
            // применялись. Отвергается ДО COM, как и остальные пять.
            || command.ShiftMode is not null || command.SectionRefs is not null
            || command.Couplings is not null
            || command.ThicknessMm is not null || command.ThinInward is not null
            || command.FaceRefs is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — {family}: к нему применяются только {allowed}. Параметры выдавливания, "
                + "фаски, скругления, вращения и параметры кинематической операции, элемента по "
                + "сечениям (section_refs и couplings) и оболочки к признакам B3 не применяются.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = family });
        }
    }

    /// <summary>
    /// Постановка опоры для ПРАВКИ признака SM-16: та же валидация, что у создания, но БЕЗ создания
    /// объекта плоскости.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему без создания.</b> Измерено 18.09.2026 (проба <c>--split</c>, шаг SP.9, шаг E-B —
    /// отрицательный контроль): подстановка ДРУГОЙ, только что созданной плоскости в существующий
    /// признак результата НЕ меняет — <c>Update()</c> возвращает <c>true</c>, а части остаются
    /// прежними. Работает другой маршрут (E-A для разделения, E-C для отсечения): перенос ТРЁХ ТОЧЕК
    /// ПОСТРОЕНИЯ СОБСТВЕННОЙ опоры признака. Поэтому здесь считаются три точки, а объект плоскости не
    /// создаётся вовсе — иначе в документе оставался бы неиспользованный объект на каждую правку.
    /// </para>
    /// <para>
    /// <b><c>plane_ref</c> на правке отвергается.</b> Маршрут измерен для СОБСТВЕННОЙ опоры признака,
    /// а доказать, что предъявленная ссылка и есть эта опора, нечем: сравнение ссылок плоскостей не
    /// измерялось, а подстановка чужой плоскости результата не даёт (E-B). Сдвинуть чужую плоскость и
    /// отчитаться о правке значило бы выдать недостигнутое за достигнутое, поэтому исход честный —
    /// отказ с указанием, что опора описывается точкой и нормалью.
    /// </para>
    /// <para>
    /// Проверки точки и нормали не дублируются, а берутся у тех же правил, что и при создании:
    /// конечность здесь, ненулевая нормаль — у <c>PlaneBasis.FromNormal</c>, три точки построения — у
    /// <c>PlaneBasis.ThreePoints</c>.
    /// </para>
    /// </remarks>
    private (double[] P1, double[] P2, double[] P3, double[] UnitNormal, double[] Point) ResolveSupportPlane(
        CutPlaneDto plane,
        string family)
    {
        GuardCutPlaneForm(plane);

        if (plane.PlaneRef is { Length: > 0 } reference)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Правка ({family}) принимает опору только точкой и нормалью, а не ссылкой "
                + $"'{reference}': маршрут правки — перенос точек СОБСТВЕННОЙ опоры признака, а "
                + "доказать, что предъявленная ссылка и есть эта опора, нечем (сравнение ссылок "
                + "плоскостей не измерялось). Подстановка чужой плоскости результата не даёт — "
                + "измерено шагом SP.9, опыт E-B. Перечислите опору заново: point_mm и normal_mm.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["plane_ref"] = reference,
                    ["family"] = family,
                });
        }

        if (plane.PointMm is not { Count: 3 } point || plane.NormalMm is not { Count: 3 } normal)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Опора не задана: нужны point_mm и normal_mm (по три числа).",
                details: new Dictionary<string, object?>
                {
                    ["has_point"] = plane.PointMm is not null,
                    ["has_normal"] = plane.NormalMm is not null,
                    ["family"] = family,
                });
        }

        foreach (var value in point.Concat(normal))
        {
            if (!double.IsFinite(value))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Точка и нормаль плоскости обязаны быть конечными числами.",
                    details: new Dictionary<string, object?>
                    {
                        ["point_mm"] = point,
                        ["normal_mm"] = normal,
                        ["family"] = family,
                    });
            }
        }

        try
        {
            _ = PlaneBasis.FromNormal(normal);
        }
        catch (ArgumentException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскость невыразима: " + ex.Message,
                details: new Dictionary<string, object?> { ["normal_mm"] = normal, ["family"] = family });
        }

        var three = PlaneBasis.ThreePoints(point, normal);
        return (three.P1, three.P2, three.P3, three.UnitNormal, new[] { point[0], point[1], point[2] });
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕГО признака разделения: новая опора, записанная в тот же признак.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> (проба <c>--split</c>, шаг SP.9, прогон
    /// <c>c9cd7660468c44aa97b410e253ee2cb1</c>): перенос трёх точек построения СОБСТВЕННОЙ опоры
    /// признака переводит части <c>6 000 / 18 000</c> при <c>x = 10</c> в <c>9 000 / 15 000</c> при
    /// <c>x = 15</c>, число признаков остаётся <c>1 → 1</c>, сумма <c>24 000</c> не меняется.
    /// Отрицательный контроль того же шага (E-B) показал, что «очевидный» маршрут — подстановка
    /// другой плоскости в <c>CutObjects</c> — результата не меняет, и он в правке не используется.
    /// </para>
    /// <para>
    /// <b>Определение перечисляется целиком, а не «изменяемое поле».</b> Опоры у разделения два вида
    /// (существующая плоскость либо точка с нормалью). Прочитать опору обратно МОЖНО — это измерено
    /// 18.09.2026 шагом SP.10 (<c>CutObjects</c> отдаёт три точки построения и нормаль, и разные опоры
    /// читаются по-разному), и именно это публикует <c>kompas_get_feature</c> в блоке
    /// <c>solid.plane</c>. Требование полноты запроса сохранено не из-за невозможности чтения, а
    /// потому что ответ на частичный запрос не отличил бы «изменилось ровно то, что просили» от
    /// «изменилось заодно и то, о чём промолчали».
    /// </para>
    /// <para>
    /// <b>Подтверждение — только состав частей.</b> Сумма объёмов при правке разделения не меняется,
    /// поэтому <c>expected_volume_mm3</c> здесь не принимается вовсе (отвергается с указанием на
    /// <c>expected_part_volumes_mm3</c>): строка, сверяющая сумму, прошла бы на полном бездействии.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateSolidSplit(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            SplitFamily,
            "plane (новая опора) и expected_part_volumes_mm3 (ожидаемые объёмы частей)");

        if (command.ExpectedVolumeMm3 is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "На признаке разделения expected_volume_mm3 не принимается: сумма объёмов частей при "
                + "правке не меняется (24 000 и при x = 10, и при x = 15), поэтому сверка суммы прошла "
                + "бы и на полном бездействии. Объявите expected_part_volumes_mm3 — состав частей, "
                + "например [9000, 15000].",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = SplitFamily });
        }

        if (command.ExpectedBboxMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "На признаке разделения expected_bbox_mm не принимается: частей несколько, и один "
                + "габарит не говорит, какой части он принадлежит. Объявите expected_part_volumes_mm3.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = SplitFamily });
        }

        if (command.Plane is not CutPlaneDto planeSpec)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка разделения требует plane: без новой опоры менять нечего, а подставлять опору "
                + "из текущего состояния модели значило бы принимать решение за вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = SplitFamily });
        }

        var expectedParts = RequirePartVolumes(command.ExpectedPartVolumesMm3);
        var spec = ResolveSupportPlane(planeSpec, SplitFamily);

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.split.update");
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.SplitSolid,
            Api7SolidSplit.Count(container),
            SplitFamily,
            "SplitSolids");

        var moved = Api7SolidSplit.TryMoveSupport(container, index, (spec.P1, spec.P2, spec.P3));
        if (!moved.Moved)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Опора разделения не переписана: " + (moved.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["api7_failure"] = moved.Failure,
                    ["api7_index"] = index,
                    ["requested_plane_point_mm"] = spec.Point,
                    ["requested_plane_normal_mm"] = spec.UnitNormal,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.split.update");

        var rows = ReadSolidBodies(document);
        var stateAfter = ReadFeatureState(entity);
        var featuresAfter = CountFeatures(document);
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);

        // ЧАСТИ ОПОЗНАЮТСЯ ПО ИЗМЕНЕНИЮ, а не по совпадению с ожиданием. Прежняя редакция подавала в
        // UnmatchedVolume список ВСЕХ тел документа и публиковала в observed их же объёмы: ожидание
        // искалось ГДЕ УГОДНО в документе, поэтому постороннее тело с объёмом, случайно совпавшим с
        // объявленной частью, закрывало объявление; лишняя часть пройти не мешала; а несовпадение
        // состава нельзя было отличить от неверной арифметики (дефект
        // CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, наряд §4.2). Здесь набор частей берётся из
        // СОПОСТАВЛЕНИЯ СОСТАВА тел и от объявленного ожидания не зависит вовсе. Предикат —
        // changes.Touched (изменился объём ИЛИ габарит), а не changes.Changed: часть, уехавшая с
        // прежним объёмом, тоже часть этого разделения, и выпасть из набора она не должна.
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var vanished = bodiesBefore.Where(b => changes.MatchedOf(b.Index) is null).ToList();
        var createdBodies = changes.NewBodies;
        var partSnapshots = changes.Touched
            .Select(changes.MatchedOf)
            .Where(s => s is not null)
            .Select(s => s!)
            .Concat(createdBodies)
            .ToList();

        var claimedSnapshots = new HashSet<int>();
        var parts = new List<SolidBodyDto>();
        var foreign = new List<SolidBodyDto>();
        foreach (var row in rows)
        {
            var hit = -1;
            for (var j = 0; j < partSnapshots.Count; j++)
            {
                if (!claimedSnapshots.Contains(j) && MatchesSnapshot(row, partSnapshots[j]))
                {
                    hit = j;
                    break;
                }
            }

            if (hit >= 0)
            {
                claimedSnapshots.Add(hit);
                parts.Add(row);
            }
            else
            {
                foreign.Add(row);
            }
        }

        // Части опознаны только тогда, когда операция оставила ВИДИМЫЙ след. Если не изменилось и не
        // появилось ничего, набор частей ИМЕННО ЭТОГО разделения из изменения не выводится, и
        // подставлять вместо него все тела документа нельзя — это и был дефект.
        var partsIdentified = partSnapshots.Count > 0;
        var declaredCount = expectedParts?.Count ?? 0;

        string? volumeMiss = null;
        if (expectedParts is not null && partsIdentified)
        {
            // Сравнивается ПООБЪЁМНОЕ МУЛЬТИМНОЖЕСТВО: равная мощность плюс инъективное сопоставление
            // каждого объявления своей части — это и есть равенство мультимножеств. Кратность учтена,
            // часть не закрывает два объявления и не подменяется посторонним телом.
            volumeMiss = parts.Count != declaredCount
                ? $"частей {parts.Count}, а объявлено {declaredCount}"
                : UnmatchedVolume(parts, expectedParts);
        }

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featuresAfter == featuresBefore,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
        };

        if (expectedParts is not null)
        {
            checks.Add(new NamedCheck(
                "expected_part_volumes",
                volumeMiss is null && partsIdentified,
                Observed: partsIdentified
                    ? $"части ({parts.Count}): " + BodyVolumesText(parts)
                    : "<части не опознаны: ни одно тело не изменилось и не появилось>",
                Expected: $"части ({declaredCount}): " + NumberListText(expectedParts)));
        }

        if (partsIdentified)
        {
            checks.Add(new NamedCheck(
                "foreign_bodies_unchanged", vanished.Count == 0,
                Observed: vanished.Count > 0
                    ? "исчезли тела " + string.Join(", ", vanished.Select(b => "тел" + b.Index))
                    : foreign.Count == 0 ? "<посторонних тел нет>" : BodyVolumesText(foreign),
                Expected: "посторонние тела не изменились и ни одно не исчезло"));
        }

        var unverified = new List<string>();
        if (expectedParts is null)
        {
            unverified.Add("expected_part_volumes_not_supplied — сумма объёмов частей при правке "
                           + "разделения не меняется, поэтому подтвердить правку может только состав "
                           + "частей (expected_part_volumes_mm3)");
        }
        else if (!partsIdentified)
        {
            unverified.Add("parts_not_identified_by_change — ни одно тело не изменилось и не появилось, "
                           + "поэтому ЧАСТИ ЭТОГО разделения опознать нечем: список всех тел документа "
                           + "частями не является, а сверять объявление с ним значило бы подтверждать "
                           + "ожидание подбором. Объявленное ожидание НЕ проверено.");
        }

        if (vanished.Count > 0)
        {
            unverified.Add("foreign_bodies_vanished — при правке разделения исчезли тела "
                           + string.Join(", ", vanished.Select(b => "тел" + b.Index))
                           + ": разделение ЗАМЕНЯЕТ тело на части и ничего не удаляет");
        }

        // Ни один габарит и объём не изменился: признак мог уже иметь такую опору. Это не отказ (и
        // объявленное ожидание проверено выше — оно сверяется с САМОЙ моделью), но и не молчание:
        // отличить «опора уже была такой» от «запись не применилась» чтением опоры обратно этот
        // вызов не может — маршрут чтения опоры на живом признаке не измерялся.
        if (!partsIdentified && vanished.Count == 0)
        {
            unverified.Add("geometry_unchanged_by_edit — ни один габарит и объём тела не изменились: "
                           + "отличить «опора уже была такой» от «запись не применилась» этим вызовом "
                           + "нельзя. Объявленный состав частей сверен с моделью, а не с изменением.");
        }

        if (expectedParts is not null && volumeMiss is not null)
        {
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Опора переписана, но состав частей не совпал с объявленным: " + volumeMiss
                + ". Сверялся состав ЧАСТЕЙ ЭТОГО разделения (тела, изменившиеся или появившиеся в "
                + "этой операции), а не любой набор тел документа. Это признак того, что параметр "
                + "применён не к исходным входам признака: правка обязана резать ИСХОДНОЕ тело, а не "
                + "уже полученные части (наряд §5).",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_plane_point_mm"] = spec.Point,
                    ["requested_plane_normal_mm"] = spec.UnitNormal,
                    ["body_volumes_after"] = BodyVolumesText(rows),
                    ["part_volumes_observed"] = parts.Select(p => p.VolumeMm3).ToArray(),
                    ["foreign_body_volumes"] = foreign.Select(p => p.VolumeMm3).ToArray(),
                    ["vanished_bodies"] = vanished.Select(b => b.Index).ToArray(),
                    ["expected_part_volumes_mm3"] = expectedParts,
                    ["unmatched"] = volumeMiss,
                });
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            SplitFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            ReadVolume(document),
            null,
            null,
            new VerificationDto(
                expectedParts is not null && checks.TrueForAll(c => c.Passed)
                    ? VerificationLevel.GeometryChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕГО признака отсечения: новая опора и новая оставляемая сторона.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> (проба <c>--split</c>, шаг SP.9, прогон
    /// <c>c9cd7660468c44aa97b410e253ee2cb1</c>), двумя отдельными опытами: E-C — перенос точек опоры
    /// на +5 по X меняет остаток с 6000 на 9000; E-D — смена ТОЛЬКО <c>Direction</c> на том же
    /// признаке меняет остаток с 9000 на 15000. Прежняя редакция этого обработчика подставляла в
    /// признак ДРУГУЮ плоскость, и это измеренно не работает (отрицательный контроль E-B).
    /// </para>
    /// <para>
    /// <b>Определение перечисляется целиком.</b> Требуются И опора, И сторона: прочитать текущую
    /// опору и сторону с живого признака и дополнить недостающее не измерено, а выполнить половину
    /// запроса молча — значит отчитаться о правке, которой не было.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateSolidCutByPlane(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            CutByPlaneFamily,
            "plane (опора), keep_side (оставляемая сторона), target_body_ref (область применения), "
            + "expected_volume_mm3 и expected_bbox_mm");

        if (command.ExpectedPartVolumesMm3 is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "На признаке отсечения expected_part_volumes_mm3 не принимается: отсечение оставляет "
                + "ОДНО тело, а не набор частей. Объявите expected_volume_mm3 (объём остатка).",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = CutByPlaneFamily });
        }

        if (command.Plane is not CutPlaneDto planeSpec)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка отсечения требует plane: опора перечисляется целиком, а не додумывается из "
                + "состояния модели. Опору при этом МОЖНО прочитать — kompas_get_feature отдаёт её "
                + "в блоке solid.plane (маршрут измерен 18.09.2026 пробой --split, шаг SP.10); "
                + "требование полноты остаётся потому, что ответ на частичный запрос не отличил бы "
                + "«изменилось ровно то, что просили» от «изменилось заодно и то, о чём промолчали». "
                + "Рабочий порядок — прочитать признак, подставить прочитанное и изменить нужное поле.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = CutByPlaneFamily });
        }

        if (command.KeepSide is not string side)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка отсечения требует keep_side: сторона — такой же параметр признака, как опора, "
                + "и угадывать её из текущего состояния модели нельзя.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = CutByPlaneFamily });
        }

        var keepPositive = side switch
        {
            "positive" => true,
            "negative" => false,
            _ => throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"keep_side '{side}' не распознан: ожидалось 'positive' (s > 0) или 'negative' (s < 0).",
                details: new Dictionary<string, object?> { ["keep_side"] = side }),
        };

        var spec = ResolveSupportPlane(planeSpec, CutByPlaneFamily);

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.cut_by_plane.update");
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.CutByPlane,
            Api7SolidCut.Count(container),
            CutByPlaneFamily,
            "Cuts");

        // Область применения восстанавливается на правке так же, как назначается на создании
        // (наряд §3.2). Без target_body_ref она читается обратно до мутации и незаадресованный
        // признак отвергается — иначе перенос опоры снял бы материал у посторонних тел, потому что
        // умолчание области применения «Все объекты» (справка rezultat_oper_v_zavisimosti_ot_s_o.html).
        IKompasAPIObject? targetBody = null;
        if (command.TargetBodyRef is string targetRef)
        {
            var target = ResolveBodyTarget(document, document.PartNow(), targetRef, bodiesBefore);
            if (bridge.TransferTo7(target.RawElement) is not IKompasAPIObject transferred)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Тело для области применения не перенесено в API7 как IKompasAPIObject: "
                    + (bridge.BridgeFailure ?? "TransferInterface вернул null")
                    + ". Правка признака с незаданной областью применения сняла бы материал у "
                    + "посторонних тел, поэтому вызов отвергнут до мутации.",
                    RetryPolicy.ReacquireContext,
                    details: new Dictionary<string, object?>
                    {
                        ["target_body_ref"] = targetRef,
                        ["target_body_index"] = target.Index,
                        ["code"] = "target_body_not_transferred",
                    });
            }

            targetBody = transferred;
        }

        var written = Api7SolidCut.TryMoveSupportAndSide(
            container, index, (spec.P1, spec.P2, spec.P3), keepPositive, targetBody);
        if (!written.Updated)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отсечение не переписано: " + (written.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["api7_failure"] = written.Failure,
                    ["api7_index"] = index,
                    ["keep_side"] = side,
                    ["target_body_ref"] = command.TargetBodyRef,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.cut_by_plane.update");

        var rows = ReadSolidBodies(document);
        var stateAfter = ReadFeatureState(entity);
        var featuresAfter = CountFeatures(document);
        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);

        // Остаток опознаётся ПО ИЗМЕНЕНИЮ: это тело, не совпавшее ни с одним снимком «до». Прежняя
        // редакция искала его как «тело с объёмом, отличным от объёма цели», но цели в правке уже
        // нет — её потребило СОЗДАНИЕ признака, поэтому отождествлять остаток с ней нечем. Здесь
        // отбор структурный и от объявленного ожидания НЕ зависит: подбирать тело под ожидание
        // значило бы проверять ожидание им же самим.
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var vanished = bodiesBefore.Where(b => changes.MatchedOf(b.Index) is null).ToList();
        var createdBodies = changes.NewBodies;
        var changed = changes.Changed;

        // «Затронуто» — шире, чем «изменился объём»: переехавшее тело тоже затронуто, и проверка
        // адресности обязана его видеть. Пока здесь стоял только объём, отсечение, сдвинувшее
        // посторонний брусок, проходило как «затронуто ровно одно тело».
        var touched = changes.Touched;

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featuresAfter == featuresBefore,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
        };

        var declaredVolume = command.ExpectedVolumeMm3;
        var unverified = new List<string>();

        // АДРЕСНОСТЬ — ПРЕДМЕТ ЭТОГО ВЫЗОВА. Отсечение оставляет РОВНО одно тело-остаток, поэтому
        // «затронуто ровно одно тело, ни одно не исчезло и ни одно не появилось» не украшение
        // ответа, а то, что вызов обязан подтвердить. Раньше здесь стояло
        // changed = rows.Where(r => !MatchesAnySnapshot(r, bodiesBefore)): обход шёл ТОЛЬКО по телам
        // ПОСЛЕ операции, поэтому исчезнувшее тело в список не попадало вовсе, и правка, снёсшая
        // посторонний брусок, выглядела как «изменилось ровно одно тело» и проходила как
        // geometry_checked — это и есть ложное подтверждение из клиентской приёмки 19.09.2026
        // (дефект CUT-PLANE-APPLIED-TO-UNNAMED-BODIES, наряд §3.1).
        var addressingOk = touched.Count == 1 && vanished.Count == 0 && createdBodies.Count == 0;

        if (!addressingOk && (touched.Count > 0 || vanished.Count > 0 || createdBodies.Count > 0))
        {
            var parts = new List<string>();
            if (touched.Count > 1)
            {
                parts.Add("затронуты тела " + string.Join(", ", touched.Select(i => "тел" + i))
                    + " (" + string.Join("; ", touched.Select(i => "тел" + i + ": " + ChangeKind(changes, i))) + ")");
            }

            if (vanished.Count > 0)
            {
                parts.Add("исчезли тела " + string.Join(", ", vanished.Select(b => "тел" + b.Index)));
            }

            if (createdBodies.Count > 0)
            {
                parts.Add("появились новые тела "
                    + string.Join(", ", createdBodies.Select(b => "тел" + b.Index)));
            }

            if (touched.Count == 0)
            {
                parts.Add("ни одно тело не изменилось ни объёмом, ни положением");
            }

            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка отсечения затронула не только тело-остаток: " + string.Join("; ", parts)
                + ". Отсечение оставляет ОДНО тело, и объявленное ожидание к неизвестному телу не "
                + "применяется. Мутация уже выполнена — откат не обещается. Фактический состав тел: "
                + BodyVolumesText(rows) + "; ревизия после мутации " + document.Revision
                + ". Прочитайте состав тел и повторите операцию на исправленном состоянии.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_plane_point_mm"] = spec.Point,
                    ["requested_plane_normal_mm"] = spec.UnitNormal,
                    ["keep_side"] = side,
                    ["target_body_ref"] = command.TargetBodyRef,
                    ["changed_bodies"] = changed.ToArray(),
                    ["vanished_bodies"] = vanished.Select(b => b.Index).ToArray(),
                    ["created_bodies"] = createdBodies.Select(b => b.Index).ToArray(),
                    ["body_volumes_before"] = BodyVolumesText(bodiesBefore),
                    ["body_volumes_after"] = BodyVolumesText(rows),
                    ["code"] = "cut_not_addressed_to_target_body",
                });
        }

        if (changed.Count == 1)
        {
            var remainingSnapshot = changes.MatchedOf(changed[0]);
            var remaining = remainingSnapshot is null
                ? null
                : rows.FirstOrDefault(r => MatchesSnapshot(r, remainingSnapshot));

            if (remaining is null)
            {
                throw new KompasContractException(
                    ErrorCodes.GeometryFailed,
                    "Изменившееся тело не найдено в списке тел после правки: сопоставление состава не "
                    + "дало ему пары. Объявить ожидание нечем.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["revision_after"] = document.Revision,
                        ["changed_bodies"] = changed.ToArray(),
                        ["body_volumes_after"] = BodyVolumesText(rows),
                    });
            }

            checks.Add(new NamedCheck(
                "target_body_affected",
                true,
                Observed: "тел" + changed[0] + ": ΔV=" + Num(changes.DeltaOf(changed[0])),
                Expected: "изменение объёма тела-остатка"));
            checks.Add(new NamedCheck(
                "nontarget_bodies_unchanged", vanished.Count == 0 && createdBodies.Count == 0,
                Observed: "исчезло " + Num(vanished.Count) + ", появилось " + Num(createdBodies.Count),
                Expected: "0 и 0"));
            checks.Add(new NamedCheck("no_bodies_vanished", vanished.Count == 0,
                Observed: Num(vanished.Count), Expected: "0"));
            checks.Add(new NamedCheck("no_bodies_created", createdBodies.Count == 0,
                Observed: Num(createdBodies.Count), Expected: "0"));

            if (declaredVolume is double expected)
            {
                checks.Add(new NamedCheck(
                    "volume_expected",
                    remaining.VolumeMm3 is double measured
                        && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected),
                    Observed: Num(remaining.VolumeMm3),
                    Expected: Num(expected)));
            }

            if (command.ExpectedBboxMm is BoundingBoxDto expectedBox)
            {
                checks.Add(new NamedCheck(
                    "bbox_expected",
                    BoxMatches(remaining.Bbox, expectedBox),
                    Observed: BoxText(remaining.Bbox),
                    Expected: BoxText(expectedBox)));
            }

            if (declaredVolume is null && command.ExpectedBboxMm is null)
            {
                unverified.Add("no_expectation_supplied — без аналитического ожидания "
                               + "(expected_volume_mm3 или expected_bbox_mm) правка отсечения не может "
                               + "быть подтверждена геометрически: адресность проверена по составу тел, "
                               + "а объём остатка — нет");
            }

            if (checks.Any(c => !c.Passed))
            {
                var failed = string.Join("; ", checks.Where(c => !c.Passed).Select(c => c.Name));
                throw new KompasContractException(
                    ErrorCodes.NoGeometryChange,
                    "Отсечение переписано, но геометрия не совпала с объявленной: " + failed
                    + ". Остаток — " + BoxText(remaining.Bbox) + " при объёме "
                    + Num(remaining.VolumeMm3) + ".",
                    RetryPolicy.SameOperationId,
                    partialEffects: true,
                    details: new Dictionary<string, object?>
                    {
                        ["revision_after"] = document.Revision,
                        ["requested_plane_point_mm"] = spec.Point,
                        ["requested_plane_normal_mm"] = spec.UnitNormal,
                        ["keep_side"] = side,
                        ["remaining_bbox"] = BoxText(remaining.Bbox),
                        ["remaining_volume_mm3"] = remaining.VolumeMm3,
                        ["expected_volume_mm3"] = declaredVolume,
                        ["expected_bbox"] = command.ExpectedBboxMm is null
                            ? null
                            : BoxText(command.ExpectedBboxMm),
                    });
            }

            return new UpdateFeatureResult(
                ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
                CutByPlaneFamily,
                sameFeature,
                featuresAfter,
                volumeBefore,
                ReadVolume(document),
                null,
                null,
                new VerificationDto(
                    (declaredVolume is not null || command.ExpectedBboxMm is not null)
                        && checks.TrueForAll(c => c.Passed)
                        ? VerificationLevel.GeometryChecked
                        : VerificationLevel.CallReturned,
                    checks,
                    unverified));
        }

        // Сюда попадают только два случая: изменилось ровно одно тело (разобрано выше) либо НИ ОДНО
        // тело не изменилось, не исчезло и не появилось. Ветка «изменилось больше одного» выше стала
        // недостижимой намеренно: она была отдельным отказом ровно на тот же предмет, что и проверка
        // адресности, а две проверки одного случая разошлись бы — прежняя редакция именно поэтому и
        // пропускала исчезнувшее тело: ветка `changed.Count > 1` считала только тела ПОСЛЕ операции.
        //
        // Ни одно тело не изменилось — это НЕ отказ: признак мог уже иметь такие опору и сторону.
        // Но и не подтверждение: отличить «параметры уже были такими» от «запись не применилась»
        // этим вызовом нельзя — чтение опоры и стороны обратно на живом признаке не измерялось.
        // Поэтому исход честный: вызов прошёл, уровень — «вызов вернулся», а причина названа.
        //
        // Отдельно назван случай «тело переехало, а объём не изменился»: отсечение материал уносит,
        // поэтому остаток без ΔV — не остаток, и объявленное ожидание к нему не применяется.
        var movedOnly = touched.Count == 1 && changed.Count == 0;
        checks.Add(new NamedCheck(
            "geometry_changed",
            false,
            Observed: movedOnly
                ? "тел" + touched[0] + " переехал без изменения объёма ("
                    + ChangeKind(changes, touched[0]) + ")"
                : "ни одно тело не изменилось",
            Expected: "изменение остатка"));
        unverified.Add(movedOnly
            ? "geometry_unchanged_by_edit — отсечение оставило тело с прежним объёмом: материал не "
              + "убран, остаток не опознан. Объявленное ожидание НЕ проверено."
            : "geometry_unchanged_by_edit — ни один габарит и объём тела не изменились: "
              + "отличить «опора и сторона уже были такими» от «запись не применилась» этим "
              + "вызовом нельзя. Объявленное ожидание НЕ проверено: тело-остаток не опознано.");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            CutByPlaneFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            ReadVolume(document),
            null,
            null,
            new VerificationDto(VerificationLevel.CallReturned, checks, unverified));
    }

    /// <summary>
    /// Правка ВИДА существующей булевой операции (наряд §7, действие <c>edit</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен</b> пробой <c>--boolean</c>, шаг <c>BO.11</c>, прогон
    /// <c>a2f5cf0a2ad342c59c36807101a65d51</c>: перезапись <c>IBoolean.BooleanType</c> на
    /// СУЩЕСТВУЮЩЕМ признаке + <c>Update()</c> + пересборка меняет геометрию (E-A
    /// <c>36 000 → 12 000</c> в габарите <c>x ≤ 20</c>; E-D <c>12 000 → 36 000</c>). Опыт E-E
    /// подтвердил, что применяет именно ПАРА «запись → <c>Update()</c>»: запись без <c>Update()</c>,
    /// но с пересборкой геометрию не меняет.
    /// </para>
    /// <para>
    /// <b>Почему габарит, а не только объём.</b> Объём разности и объём пересечения на эталоне §6.1
    /// РАВНЫ (12 000), поэтому сверка одного объёма подтвердила бы и полное бездействие. Различает
    /// габарит: разность лежит в <c>x ≤ 20</c>, пересечение — в <c>x ∈ [20,40]</c>. Это тот же урок,
    /// что и в строке <c>B3L.04</c> (у переноса объём до и после равен 1 000, и строка, сверяющая одни
    /// объёмы, прошла бы на бездействии), и он повторён здесь на своём семействе, а не перенесён.
    /// </para>
    /// <para>
    /// <b>Чего правка не делает.</b> Опорные тела (<c>BaseObject</c>, <c>ModifyObjects</c>) и политика
    /// сохранения инструментов НЕ перезаписываются: правка набора инструментов не измерялась, а
    /// перезапись неиспытанного маршрута выдала бы недостигнутое за достигнутое.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateSolidBoolean(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        RejectForeignSolidFields(
            command,
            BooleanFamily,
            "operation (новый вид операции), expected_volume_mm3 и expected_bbox_mm");

        if (command.Operation is not BooleanOperation operation)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка булевой операции требует operation (union, difference или intersect): без вида "
                + "операции менять нечего, а вывести его из текущего состояния значило бы принять "
                + "решение за вызывающего.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = BooleanFamily });
        }

        if (!DeclaresExpectation(command))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Правка булевой операции требует аналитического ожидания — expected_volume_mm3 и/или "
                + "expected_bbox_mm: успешный Update() доказательством не является (измерено: у переноса "
                + "три маршрута из четырёх возвращают успех и тело не двигают), а объём разности и "
                + "пересечения на эталоне РАВНЫ, поэтому одного объёма мало — габарит различает.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["family"] = BooleanFamily });
        }

        var bridge = BridgeFor(document);
        var container = RequireContainer(bridge, document, "solid.boolean.update");

        // Состав тел ДО правки — не для адресности (у булевой операции входы потребляются
        // намеренно), а чтобы результат опознавался независимо от объявленного ожидания.
        var bodiesBefore = ReadBodySnapshots(document.PartNow());

        var index = RequireSameTypeIndex(
            document,
            entity,
            KompasObjectTypes.BooleanOperation,
            Api7SolidBoolean.Count(container),
            BooleanFamily,
            "Booleans");

        var operationBefore = Api7SolidBoolean.ReadOperation(container, index);
        var written = Api7SolidBoolean.TryWriteOperation(container, index, operation);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Вид булевой операции не переписан: " + (written.Failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_operation"] = operation.ToString(),
                    ["api7_failure"] = written.Failure,
                });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "solid.boolean.update");

        var operationAfter = Api7SolidBoolean.ReadOperation(container, index);
        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);
        var rowsAfter = ReadSolidBodies(document);

        var sameFeature = string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal);
        var featureCountPreserved = featuresAfter == featuresBefore;
        var operationPreserved = operationAfter == operation;

        // ПРЕДМЕТ ДОКАЗАТЕЛЬСТВА. Результат булевой операции — это ИЗМЕНИВШЕЕСЯ (или появившееся)
        // тело, а НЕ «любое тело документа, чей габарит совпал с объявленным». Прежняя редакция
        // искала тело по совпадению с ОЖИДАНИЕМ:
        //   rowsAfter.FirstOrDefault(r => BoxMatches(r.Bbox, command.ExpectedBboxMm))
        // и публиковала в observed объёмы ВСЕХ тел документа — то есть сверяла габарит с числами
        // другого рода и другого предмета. Отдельно: тело, найденное ТАКИМ поиском, может быть
        // посторонним — совпадение габарита с ожиданием не делает тело результатом ЭТОЙ операции,
        // поэтому «подтверждение» держалось на подборе под ожидание (дефект
        // CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, наряд §4.1).
        //
        // Второй заход в тот же дефект (измерен 19.09.2026 на поставке publish-b3-20260919-targeting,
        // строка B3.25): опознание шло по changes.Changed, а это список тел с изменившимся ОБЪЁМОМ.
        // На эталоне объём разности и объём пересечения РАВНЫ (12 000 мм³), различает их только
        // положение габарита, поэтому корректно применённая правка `intersect` попадала в
        // resultBodies.Count == 0 и отвергалась как NO_GEOMETRY_CHANGE. Предикат «результат
        // изменился» обязан покрывать и переезд тела: changes.Touched = изменился объём ИЛИ габарит.
        var afterSnapshots = ReadBodySnapshots(document.PartNow());
        var changes = CompareBodySnapshots(bodiesBefore, afterSnapshots);
        var resultBodies = changes.Touched
            .Select(changes.MatchedOf)
            .Where(s => s is not null)
            .Select(s => s!)
            .Concat(changes.NewBodies)
            .ToList();
        var resultBody = resultBodies.Count == 1
            ? rowsAfter.FirstOrDefault(r => MatchesSnapshot(r, resultBodies[0]))
            : null;
        var resultIdentity = resultBodies.Count switch
        {
            0 => "ни одно тело не изменилось и не появилось — результата операции не видно",
            1 => "тел" + resultBodies[0].Index + " (" + ChangeKind(changes, resultBodies[0].Index)
                 + ", габарит [" + Range(resultBodies[0].Min) + "|" + Range(resultBodies[0].Max) + "])",
            _ => "изменившихся и появившихся тел " + resultBodies.Count
                 + " — какое из них результат операции, по этим данным не установить",
        };

        var bboxMatched = command.ExpectedBboxMm is not null
                         && resultBody is not null
                         && BoxMatches(resultBody.Bbox, command.ExpectedBboxMm);

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved", sameFeature, Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("feature_count_unchanged", featureCountPreserved,
                Observed: Num(featuresAfter), Expected: Num(featuresBefore)),
            new("operation_read_back", operationPreserved,
                Observed: operationAfter?.ToString() ?? "<не прочитан>", Expected: operation.ToString()),
        };

        if (command.ExpectedBboxMm is not null)
        {
            // Ожидание, наблюдение и вердикт относятся к ОДНОМУ предмету — габариту ОДНОГО тела,
            // опознанного по изменению, а не по совпадению с ожиданием. Тип наблюдаемого тот же, что
            // и у ожидаемого: габарит против габарита, а не список объёмов против габарита.
            checks.Add(new NamedCheck(
                "bbox_expected", bboxMatched,
                Observed: resultBody is null ? "<результат не опознан: " + resultIdentity + ">"
                    : BoxText(resultBody.Bbox),
                Expected: BoxText(command.ExpectedBboxMm)));
            checks.Add(new NamedCheck(
                "result_body_identity", resultBody is not null,
                Observed: resultIdentity,
                Expected: "ровно одно тело, изменившееся или появившееся в этой операции"));
        }

        if (command.ExpectedVolumeMm3 is double expected)
        {
            // Объём ДОКУМЕНТА и объём РЕЗУЛЬТАТА — разные величины, и смешивать их нельзя. Здесь
            // объявлено ожидание объёма документа (так его и объявляет вызывающий у булевой
            // операции), поэтому и наблюдается объём документа; объём результата публикуется
            // отдельной проверкой, а не подставляется в ту же строку.
            checks.Add(new NamedCheck(
                "volume_expected",
                volumeAfter is double measured && Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected),
                Observed: Num(volumeAfter) + " (объём документа)",
                Expected: Num(expected)));

            if (resultBody is not null)
            {
                checks.Add(new NamedCheck(
                    "result_body_volume",
                    true,
                    Observed: Num(resultBody.VolumeMm3) + " (объём тела-результата)",
                    Expected: "наблюдение, а не ожидание: объём результата отделён от объёма документа"));
            }
        }

        var volumeMatched = command.ExpectedVolumeMm3 is null
            || (volumeAfter is double m && Math.Abs(m - command.ExpectedVolumeMm3.Value)
                <= ProfileArea.Tolerance(command.ExpectedVolumeMm3.Value));
        var bboxOk = command.ExpectedBboxMm is null || bboxMatched;

        if (!volumeMatched || !bboxOk)
        {
            // Наряд §5: при частичной мутации вернуть ошибку с фактическим состоянием. Здесь мутация
            // уже произошла (вид перезаписан и применён), поэтому отказ обязан нести и ревизию, и
            // фактическую геометрию — иначе следующий вызов клиента упадёт REVISION_CONFLICT.
            throw new KompasContractException(
                ErrorCodes.NoGeometryChange,
                "Вид операции переписан, но геометрия не совпала с объявленной: "
                + string.Join("; ", new[]
                {
                    volumeMatched ? null : "volume_expected",
                    bboxOk ? null : "bbox_expected",
                }.Where(x => x is not null))
                + $". Тела после правки: {BodyVolumesText(rowsAfter)} при суммарном объёме "
                + $"{Num(volumeAfter)}.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["revision_after"] = document.Revision,
                    ["requested_operation"] = operation.ToString(),
                    ["operation_before"] = operationBefore?.ToString(),
                    ["operation_after"] = operationAfter?.ToString(),
                    ["expected_volume_mm3"] = command.ExpectedVolumeMm3,
                    ["volume_after_mm3"] = volumeAfter,
                    ["expected_bbox_mm"] = command.ExpectedBboxMm is null ? null : BoxText(command.ExpectedBboxMm),
                    ["body_volumes_after"] = BodyVolumesText(rowsAfter),
                });
        }

        var unverified = new List<string>();
        if (command.ExpectedBboxMm is null)
        {
            unverified.Add("expected_bbox_not_supplied — объём разности и объём пересечения на эталоне "
                           + "равны (12 000), поэтому без габарита правка на этих видах неотличима от "
                           + "полного бездействия");
        }

        // ГРАНИЦА ОПОЗНАНИЯ, названная явно. Опознание результата опирается на наблюдаемые величины —
        // объём и габарит. Тело, у которого изменилась форма, но совпали и объём, и все шесть
        // координат габарита, от бездействия этими измерениями неотличимо, и объявлять его
        // опознанным нельзя. Формулировка стоит здесь потому, что молчание об этом читалось бы как
        // «любое изменение результата обнаруживается».
        unverified.Add("result_identity_by_observables — результат опознаётся по изменению объёма или "
                       + "габарита; тело, изменившее форму при совпавших объёме и габарите, этим "
                       + "сравнением не обнаруживается");

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            BooleanFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            ReadVolume(document),
            null,
            null,
            new VerificationDto(VerificationLevel.GeometryChecked, checks, unverified));
    }

    /// <summary>
    /// Почему тело попало в набор «результат операции»: изменился объём, сдвинулся только габарит,
    /// или тело появилось. Строка нужна вызывающему, чтобы отличить «правка поработала материалом»
    /// от «правка переставила тело» — эти два наблюдения требуют разных выводов, и различать их
    /// по одному лишь факту попадания в список нельзя.
    /// </summary>
    private static string ChangeKind(BodyComparison changes, int index)
    {
        if (changes.NewBodies.Any(n => n.Index == index))
        {
            return "новое тело";
        }

        if (changes.Changed.Contains(index))
        {
            return "изменился объём, ΔV="
                + (changes.DeltaOf(index) is double delta ? Range(delta) : "?")
                + " мм³";
        }

        if (changes.BoxChangedOf(index))
        {
            return "объём не изменился, сдвиг габарита "
                + Range(changes.BoxShiftOf(index))
                + " мм";
        }

        return "изменение не подтверждено";
    }

    /// <summary>
    /// Проверка объявленных объёмов частей: каждому ожиданию — своё тело, порядок не важен.
    /// </summary>
    /// <remarks>
    /// Возвращается ПРИЧИНА несовпадения, а не <c>bool</c>: в отказе нужно назвать, какого объёма не
    /// нашлось, иначе вызывающему нечего исправлять. Тело, уже закрывшее одно ожидание, второму не
    /// засчитывается — иначе список <c>[6000, 6000]</c> подтверждался бы одним телом на 6 000.
    /// </remarks>
    private static string? UnmatchedVolume(IReadOnlyList<SolidBodyDto> rows, IReadOnlyList<double> expected)
    {
        var used = new bool[rows.Count];
        foreach (var want in expected)
        {
            var hit = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (used[i] || rows[i].VolumeMm3 is not double volume)
                {
                    continue;
                }

                if (Math.Abs(volume - want) <= ProfileArea.Tolerance(want))
                {
                    hit = i;
                    break;
                }
            }

            if (hit < 0)
            {
                return "не найдено тело с объёмом " + Num(want) + " (тела: " + BodyVolumesText(rows) + ")";
            }

            used[hit] = true;
        }

        return null;
    }

    /// <summary>Объёмы объявленных частей: минимум две (разделение даёт не меньше двух частей).</summary>
    private static IReadOnlyList<double>? RequirePartVolumes(IReadOnlyList<double>? declared)
    {
        if (declared is null)
        {
            return null;
        }

        if (declared.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"expected_part_volumes_mm3 содержит {declared.Count} значений: разделение даёт не "
                + "меньше двух частей, поэтому одного ожидания мало — оно не отличило бы разделение "
                + "от бездействия.",
                details: new Dictionary<string, object?> { ["expected_part_volumes_mm3"] = declared });
        }

        foreach (var value in declared)
        {
            if (!double.IsFinite(value) || value < 0d)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "Объёмы частей обязаны быть конечными неотрицательными числами.",
                    details: new Dictionary<string, object?> { ["expected_part_volumes_mm3"] = declared });
            }
        }

        return declared;
    }

    private static string BodyVolumesText(IReadOnlyList<SolidBodyDto> rows) =>
        NumberListText(rows.Select(r => r.VolumeMm3).ToList());

    private static string NumberListText(IReadOnlyList<double?> values) =>
        "[" + string.Join(", ", values.Select(Num)) + "]";

    private static string NumberListText(IReadOnlyList<double> values) =>
        "[" + string.Join(", ", values.Select(v => Num(v))) + "]";

    /// <summary>Число или честное «не прочитано» — для <c>details</c> и проверок.</summary>
    private static string Describe(int? value) => value is int number ? number.ToString() : "<не прочитано>";
}

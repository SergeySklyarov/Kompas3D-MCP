using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Вращение (docs/05 SM-03): создание признака прямой фабрикой API7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Основание — измерение, а не имена методов.</b> Прогон
/// <c>95fa844107ce41609d6278f8f6c5759f</c> от 17.09.2026 (<c>docs/acceptance/api7/rotation.json</c>,
/// запись в <c>docs/acceptance/INDEX.md</c>), шаги <c>R.24</c>, <c>R.25</c>, <c>R.26</c>:
/// <list type="bullet">
/// <item>R.24 — перебор фабрики: все три вида операции (<c>o3d_baseRotated</c> 27,
/// <c>o3d_bossRotated</c> 28, <c>o3d_cutRotated</c> 29) дали тело с аналитическим объёмом
/// 50265.4824574366 против π·r²·h = 50265.4824574367 при r=20, h=40;</item>
/// <item>R.25 — эталон задания: тот же результат воспроизведён с нуля на новом документе, форма
/// проверена независимо от объёма (одна цилиндрическая грань r=20 h=40, габарит 40×40×40), и
/// признак пережил <c>save → close → reopen</c> с повторно полученными <c>Axis</c> и
/// <c>Profile</c>; смена угла 360°→180° на ТОМ ЖЕ признаке дала 25132.7412287183.</item>
/// <item>R.26 — что операция делает с уже существующим телом: разрез снял 25132.7412287183 с
/// плиты 120×120×40, а <c>boss</c> с записанным <c>OperationResult = ksOperationCut</c> изменил
/// объём на 0.</item>
/// </list>
/// </para>
/// <para>
/// <b>Поправка от 18.09.2026: прежнее «насыщение на 180°» опровергнуто.</b> Из шага
/// <c>R.26.angles</c> был сделан вывод «развёртка останавливается на половине оборота, запись 360
/// даёт ту же половину». Этот вывод был ошибкой ИЗМЕРЕНИЯ: шаг менял <c>CutOffByPoint</c>, а не
/// угол, и ни в одной строке не записывал <c>Angle[true] = 360</c> с полным набором параметров
/// развёртки. Проба <c>tools/KompasMcp.Api7Probe/FullTurnProbe.cs</c> (F.1…F.5) и независимое
/// чтение сохранённого <c>.m3d</c> пробой <c>M3dVerificationProbe</c> показали иное:
/// <b><c>Angle[true]</c> несёт запрошенный угол напрямую</b> (360→360°, 180→180°, 90→90°), а
/// вторая половина пары, равная первой, развёртку удваивает. Полный оборот строится одним
/// вызовом. Таблица измерений и разбор трёх дефектов прежнего эксперимента —
/// <c>docs/acceptance/api7/full-turn-findings.md</c>.
/// </para>
/// <para>
/// <b>Прежняя блокировка снята, и её причина названа.</b> До 17.09.2026 вращение считалось
/// невыразимым: API5-путь создавал оболочку <c>NewEntity(27)</c> и завершал её <c>Create()</c>,
/// которая возвращала <c>true</c>, объект появлялся в дереве, объём не менялся. Это был СМЕШАННЫЙ
/// жизненный цикл — оболочка API5 вокруг объекта фабрики API7, — а не свойство вращения. Поэтому
/// здесь нет ни одного вызова <c>NewEntity</c> и ни одного <c>Create()</c>.
/// </para>
/// <para>
/// <b>Границы, которые переносятся как отказы, а не как оговорки.</b>
/// <list type="number">
/// <item><b>Угол больше полного оборота не принимается.</b> Полный оборот (360°) строится одним
/// вызовом — это измерено 18.09.2026 и заменило прежнюю (опровергнутую) гипотезу о насыщении на
/// 180°. Граница здесь — 360°, то есть потолок самой развёртки: сектор не может занять больше
/// целого оборота. Значение больше 360 отсекается ДО мутации.</item>
/// <item><b><c>dtReverse</c> не строит ничего.</b> Измерено (R.26.sector): <c>Update()</c> = False,
/// тел 0. Отвергается до мутации.</item>
/// <item><b>Ось обязательна.</b> Вращение без оси не строится вовсе. Отсутствие оси — отказ, а не
/// попытка «построить как получится».</item>
/// <item><b>Тонкая стенка не измерена.</b> Маршрут измерен на СПЛОШНОМ теле. Заданная тонкая стенка
/// отвергается CAPABILITY_UNAVAILABLE, а не записывается незмеренным числом.</item>
/// <item><b>Приклейка к существующему телу ПРОИСХОДИТ.</b> Измерено 18.09.2026 (проба F.10 —
/// управляемый опыт на одной геометрии, менялся только <c>OperationResult</c>): <c>Union</c> даёт
/// сращивание (тел 1→1, прирост равен объёму тела минус пересечение), <c>NewBody</c> — второе тело
/// (тел 1→2, прирост равен всему цилиндру). Прежний отказ <c>boss</c> опирался на прогон, который
/// писал <c>NewBody</c>, то есть просил ровно то, что и получил. Вид операции выводится из вида
/// фабрики: base→<c>NewBody</c>, boss→<c>Union</c>, cut→<c>Cut</c>.
/// </item>
/// <item><b>Целевое тело операция выбирает ПО ГЕОМЕТРИИ, и это измерено.</b> Проба F.11: в детали
/// из двух тел, где инструмент пересекает только одно, тронуто ровно ПЕРЕСЕКАЕМОЕ тело, а не
/// первое в коллекции (КОМПАС переставляет тела, поэтому индекс — не адрес). Следствие: операция
/// не выбирает тело за клиента молча — число тел и поимённое изменение читаются и возвращаются,
/// а объявленный <c>target_body_ref</c> проверяется после операции.
/// </item>
/// </list>
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Допуск сопоставления цилиндрической грани с ожидаемым радиусом вращения, мм.</summary>
    private const double RotationRadiusToleranceMm = 0.01d;

    /// <summary>Имя семейства вращения в ответах сервера.</summary>
    private const string RotationFamily = "rotation";

    /// <summary>Точность, с которой сверяется читаемый обратно угол: он лежит в модели в градусах.</summary>
    private const double RotationAngleToleranceDeg = 1e-6d;

    /// <summary>
    /// Создание вращения. Отказ моста отдаётся <c>CAPABILITY_UNAVAILABLE</c> с причиной, а не
    /// молчаливым null: вызывающий обязан отличать «API7 недоступен» от «КОМПАС отверг параметр».
    /// </summary>
    public RotatedResult Rotated(RotatedCommand command)
    {
        // ── до COM: правила, цена ошибки в которых несимметрична ─────────────────────────────────
        ValidateRotatedCommand(command);

        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var operationName = Api7Rotated.NameOf(command.Operation);

        var part = document.PartNow();
        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var bodiesBefore = CountBodies(document);

        // Поимённые снимки тел — объём и габарит каждого. Нужны затем, чтобы ответить на вопрос
        // «какое тело тронуто» числом, а не предположением, и чтобы проверить объявленное
        // target_body_ref ПОСЛЕ операции. Индекс телом не является: измерено (F.11), что КОМПАС
        // переставляет тела в коллекции ([144000; 16000] → [16000; 181699.111843077]), поэтому
        // снимки сопоставляются по центру габарита, а не по позиции.
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

        // ── многтельность: поведение ИЗМЕРЕНО, поэтому больше не отказ ──────────────────────────
        //
        // Здесь стоял отказ для boss и cut в детали с более чем одним телом. Основание было
        // честным («какое тело резать — не измерялось»), но с 18.09.2026 вопрос измерен, и отказ
        // снят по измерению, а не по удобству.
        //
        // Проба FullTurnProbe, шаг F.11: деталь из ДВУХ тел — плита x,y∈[−60,60], z∈[0,10]
        // (V=144000) и посторонний блок x,y∈[40,80], z∈[0,10] (V=16000). Инструмент (ось Z в
        // начале координат, R20) пересекает ТОЛЬКО плиту. Измерено:
        //   * boss/Union — тела 2→2, объёмы [144000; 16000] → [16000; 181699.111843077]: плита
        //     выросла ровно на 37699.1118430774 (пересечение), постороннее тело не тронуто;
        //   * cut/Cut — тела 2→2, объёмы [144000; 16000] → [131433.629385641; 16000]: с плиты снято
        //     ровно 12566.3706143592 (пересечение), постороннее тело не тронуто.
        // В обоих случаях тронуто РОВНО ОДНО тело и именно ПЕРЕСЕКАЕМОЕ, а не первое в коллекции:
        // КОМПАС переставил тела местами ([144000; 16000] → [16000; …]), и операция ушла за
        // геометрией, а не за индексом.
        //
        // Отсюда правило: операция НЕ выбирает тело за клиента молча. Число тел до и после и
        // поимённое изменение читаются и возвращаются вызывающему, а расхождение с объявленным
        // target_body_ref — отказ с partialEffects, а не тихое «наверное, то».

        // Профиль — объект API7. Непереданный профиль это значение, которое API7 не примет, и
        // подменять его «эскизом вообще» нельзя: у вращения профиль есть тело развёртки.
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

        // ── ось: обязательна, и строится в ЭТОЙ ЖЕ детали ────────────────────────────────────────
        //
        // Проверка «точки различны» стоит здесь, а не только в валидации: совпадающие точки дали бы
        // вырожденную ось, а отказ вращения на вырожденной оси не был бы фактом о вращении.
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

        // ── создание ─────────────────────────────────────────────────────────────────────────────
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

        // Без перестроения запись в API7 остаётся представлением — измерено пробой E на
        // IExtrusion.Sketch и повторено здесь: порядок «запись → Update() → Rebuild()» есть часть
        // контракта, а не стиль.
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "rotated." + operationName);

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        var bodiesAfterSnapshot = ReadBodySnapshots(part);
        var bodyComparison = CompareBodySnapshots(bodiesBeforeSnapshot, bodiesAfterSnapshot);

        var count = Api7Rotated.Count(container);
        var readBack = count is int n and > 0 ? Api7Rotated.Read(container, n - 1) : null;

        // ── независимая проверка формы, а не второй раз объём ────────────────────────────────────
        //
        // Объём не отличает цилиндр R20 H40 от плиты того же объёма. Поэтому рядом читаются
        // цилиндрические грани тела (радиус и высота через ksCylinderParam) и габарит: у цилиндра
        // R20 H40 габарит 40×40×40, у полуцилиндра 40×40×20.
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

        // Цилиндрическая грань обязана появиться: развёртка плоского профиля вокруг оси даёт
        // поверхность вращения. Её отсутствие — прямое противоречие геометрии, даже если объём сошёлся.
        checks.Add(new NamedCheck(
            "cylindrical_face_present",
            cylinders.Count > 0,
            Observed: cylinders.Count == 0
                ? "цилиндрических граней нет"
                : string.Join("; ", cylinders.Take(3).Select(c =>
                    $"r={Num(c.Radius)} h={Num(c.Height)}")),
            Expected: "хотя бы одна поверхность вращения"));

        // ── какое тело тронуто: ИЗМЕРЕНО, а не выбрано ───────────────────────────────────────────
        //
        // Для boss и cut операция обязана лечь на существующее тело, и «на какое» — вопрос, на
        // который здесь отвечает измерение, а не индекс. Измерено (F.11) на детали из двух тел:
        // тронуто ПЕРЕСЕКАЕМОЕ тело, а не первое в коллекции. Поэтому проверка идёт по снимкам.
        if (command.Operation != RotationOperation.Base)
        {
            // «Затронуто» — изменился объём ИЛИ габарит: переехавшее тело тоже затронуто, и требование
            // «ровно одно тело» относится к составу, а не к материалу. Требование к материалу
            // проверяется ОТДЕЛЬНОЙ строкой ниже (target_body_is_the_one_touched), поэтому смешивать
            // их в одном вердикте нельзя — это и есть предмет наряда §4.
            checks.Add(new NamedCheck(
                "body_target_measured",
                bodyComparison.Touched.Count == 1,
                Observed: bodyComparison.Touched.Count == 0
                    ? "ни одно тело не изменилось"
                    : "затронуто тел " + bodyComparison.Touched.Count + ": "
                        + string.Join(" | ", bodyComparison.Rows),
                Expected: "ровно одно тело изменилось — то, которое пересекает инструмент"));
        }

        // Объявленное тело проверяется ПОСЛЕ операции: маршрута, который назначает целевое тело
        // вращению, в API нет — IRotated и IRotated1 не объявляют ни chooseType, ни ChooseBodies
        // (проверено по интероп-сборке; они есть только у API5-определений ksBossRotatedDefinition
        // и ksCutRotatedDefinition). Значит подменить цель нельзя, а можно проверить, что ядро
        // тронуло именно её. Несовпадение — отказ с partialEffects, а не молчаливое согласие.
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

        // Знак — то, что отличает приклейку от разреза, и он проверяется отдельно от величины.
        // Для boss он ВЫЧИСЛЯЕТСЯ из пары измерений, а не постулируется: приклейка обязана добавить
        // материал, разрез — снять.
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

        // Геометрия подтверждается, когда параметры читаются обратно, поверхность вращения
        // действительно появилась, знак материала верен и — если вызывающий задал ожидание —
        // объём совпал. Чего-то меньшего достаточно для call_returned, но не для geometry_checked.
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

        // Прежде здесь стояла запись angle_saturates_at_180 — «развёртка линейна до 180° и дальше не
        // растёт». Она СНЯТА 18.09.2026: измерение FullTurnProbe (F.1…F.5) и независимое чтение
        // .m3d (M3dVerificationProbe) показали, что угол равен построенному вплоть до 360°, а
        // прежний вывод происходил из шага, менявшего CutOffByPoint вместо угла. Держать здесь
        // опровергнутое утверждение значило бы сообщать клиенту неверный предел при каждом вызове.
        //
        // Вместо него — то, что действительно осталось непроверенным в ЭТОМ вызове.

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

        // ── ссылка на признак ────────────────────────────────────────────────────────────────────
        var reference = FindRotatedEntity(document);
        if (reference is null)
        {
            // Ссылку на признак, которого дерево API5 не показывает, выдавать нельзя: правка по ней
            // всё равно упала бы, а вызывающий узнал бы об этом позже.
            //
            // Уровень здесь НЕ понижается до call_returned. Отсутствие ссылки и подтверждённость
            // геометрии — два независимых утверждения, и первое не ослабляет второе: объём сошёлся
            // с аналитическим, параметры перечитаны, поверхность вращения найдена — всё это
            // измерено и остаётся измеренным независимо от того, видно ли признак в дереве API5.
            // Первая редакция возвращала здесь CallReturned всегда, и это была настоящая ошибка
            // приёмки: строка RO.4 падала «уровень=call_returned» на вызове, у которого ВСЕ пять
            // проверок прошли, включая численное совпадение объёма. Номер дерева у вращения не
            // измерялся (в отличие от пары 52→583 у отверстия), поэтому неадресуемость здесь —
            // ожидаемое состояние, а не признак неудавшейся геометрии.
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

    /// <summary>
    /// Прочитать параметры СУЩЕСТВУЮЩЕГО признака вращения для <c>kompas_get_feature</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Индекс берётся сопоставлением того же признака, а не первым попавшимся.</b> У вращения нет
    /// определения API5, по которому признак можно было бы узнать, поэтому сопоставление идёт по
    /// СОСТАВУ: сущность дерева, приведённая к <c>IRotated</c>, ищется среди элементов
    /// <c>IModelContainer.Rotateds</c> по совпадению угла И направления. Угол как признак тождества
    /// слаб (два полуоборота вокруг разных осей совпадут), поэтому при нескольких кандидатах
    /// возвращается <c>null</c> — «не прочитано», а не «вот первый»: выдать чужой параметр за
    /// параметр адресованного признака значило бы соврать про модель.
    /// </para>
    /// <para>
    /// Чтение НЕ мутация: <c>BeginEdit</c>/<c>EndEdit</c>/<c>Update</c> не вызываются, ревизия не
    /// поднимается.
    /// </para>
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

            // Собственный угол адресованного признака читается через API5-сущность: она и есть тот
            // объект, на который выдана ссылка. Его сравнение с кандидатами API7 и даёт сопоставление.
            var entityAngle = entity is IRotated rotatedDirect
                ? SafeReadAngle(rotatedDirect, true)
                : null;

            // Когда сущность из дерева не отвечает на IRotated (сырой __ComObject — измерено
            // 18.09.2026), сопоставлять по углу нечем. Тогда адресация идёт ПО ПОРЯДКУ: признак
            // занимает свою позицию среди вращений в дереве API5, и та же позиция в коллекции
            // Rotateds API7. Порядок здесь — измеренное свойство, а не догадка: и дерево, и
            // коллекция перечисляют признаки в порядке создания.
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
                    // Угол адресованного признака не прочитался — сопоставлять нечем, и тогда
                    // единственный кандидат принимается, а несколько отвергаются.
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

    /// <summary>
    /// Позиция признака вращения среди ВСЕХ вращений дерева API5, в порядке создания. <c>null</c>,
    /// если адресованный признак в дереве не найден.
    /// </summary>
    /// <remarks>
    /// Нужна потому, что сущность, прочитанная из дерева, не отвечает на <c>QI(IRotated)</c>, а
    /// коллекция API7 <c>Rotateds</c> индексируется в порядке создания. Позиция — это
    /// ДЕТЕРМИНИРОВАННЫЙ адрес: она не зависит от того, читается ли угол, и не путает два признака
    /// с одинаковым углом (чего сопоставление по углу не умеет по построению).
    /// </remarks>
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
                    // Вращения найдены, но не среди них — второго прохода по другому типу быть не
                    // должно: это значило бы, что признак лежит в другой коллекции.
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

    /// <summary>
    /// Правка угла СУЩЕСТВУЮЩЕГО признака вращения по <c>kompas_update_feature</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 18.09.2026</b> (проба <c>FullTurnProbe</c>, шаг <c>F.2</c>): на одном
    /// признаке смена угла 360 → 180 → 360 дала объёмы
    /// <c>50265.4824574366 → 25132.7412287183 → 50265.4824574366</c> при габарите
    /// <c>z[−20,20] → z[−0,20] → z[−20,20]</c> — изменение геометрическое, а не только записанное
    /// число. Порядок «запись угла → <c>Update()</c> → перестроение» — часть контракта, как и при
    /// создании: без <c>Update()</c> сеттер возвращает успех, а модель остаётся прежней.
    /// </para>
    /// <para>
    /// Пишется ТОЛЬКО угол (и, если задано, направление). Профиль и ось существующего вращения
    /// этим вызовом не меняются: эти маршруты на вращении не измерялись, и принимать
    /// <c>sketch_ref</c> значило бы обещать перепривязку, которой нет.
    /// </para>
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

        // Поля очереди B5 (кинематика, сечения, оболочка) — тоже чужие этому семейству. Отвергаются
        // ЗДЕСЬ, а не «дойдут до своей ветки»: ветка B5 выбирается по самому полю, и без этой
        // проверки вызов с rotation_angle_deg и shift_mode одновременно ушёл бы в кинематику, где
        // rotation_angle_deg просто не читается, — то есть был бы принят и проигнорирован.
        // `couplings` дописан 20.09.2026 тем же порядком, что и в SolidOps.cs: перечень полей B5 был
        // неполон на одно поле, и правка вращения с couplings принималась, а цепочки не применялись.
        if (command.ShiftMode is not null || command.SectionRefs is not null
            || command.Couplings is not null
            || command.ThicknessMm is not null || command.ThinInward is not null
            || command.FaceRefs is not null
            // Поля семейства ОТВЕРСТИЯ (наряд SM07 §3.2) — тоже чужие вращению. Дописаны тем же
            // порядком, что и couplings 20.09.2026: перечень чужих полей обязан получать каждое
            // новое поле контракта, иначе поле принимается и не применяется.
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

        // Адрес известен заранее, когда признак отвечает на QI(IRotated): тогда сопоставление идёт
        // по составу. Если не отвечает (сырой __ComObject из дерева — измерено 18.09.2026), адрес
        // берётся по позиции среди вращений, и FindIndexFor его принимает как knownIndex.
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

    /// <summary>Прочитать один слот пары <c>IRotated.Angle</c>, не роняя чтение.</summary>
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

    /// <summary>
    /// Правила «поле ↔ возможность». Все они отвергают вызов ДО обращения к COM, и цена ошибки тут
    /// несимметрична: лишний отказ виден сразу, а принятое и проигнорированное число доживает до
    /// приёмки, выглядя как выполненная операция.
    /// </summary>
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

        // ПРЕЖНЕЕ ограничение «не больше 180» снято 18.09.2026, и причина названа.
        //
        // Здесь стоял отказ на угол > 180 со ссылкой на R.26.angles: «развёртка насыщается на 180°,
        // запись 360 даёт ту же половину цилиндра». Утверждение происходило из шага, который менял
        // CutOffByPoint, а не угол, и ни в одной строке не записывал Angle[true] = 360 с полным
        // набором параметров. Проба FullTurnProbe (шаги F.1…F.5) и независимое чтение
        // сохранённого .m3d пробой M3dVerificationProbe дали иную картину: Angle[true] несёт
        // запрошенный угол напрямую (360→360°, 180→180°, 90→90°), а вторая половина пары, равная
        // первой, развёртку удваивает. Полный оборот одним вызовом ВЫРАЖАЕТСЯ — он измерен объёмом
        // 50265.4824574366 (π·r²·h при r=20, h=40), одной цилиндрической гранью r=20 h=40 и
        // габаритом 40×40×40. Верхняя граница здесь — не «свойство продукта», а 360° развёртки:
        // больше полного оборота сектор не занимает.
        //
        // Класс ошибки сохранён как число в details, но это уже отказ по существу, а не по
        // прежнему неверному пределу: 360 — это потолок развёртки, а не 180.
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

        // Измерено (R.26.sector): dtReverse не строит ничего — Update()=False, тел 0. Это факт о
        // значении перечисления, и сообщать «построено» по коду возврата здесь нельзя.
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

        // Маршрут измерен на СПЛОШНОМ теле (IThinParameters.Thin = false). Записывать незмеренное
        // число в признак — значит выдать непроверенную конфигурацию за проверенную.
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

        // Целевое тело ПРИНИМАЕТСЯ, но не назначается: селектора тела у вращения в API нет (см.
        // комментарий в Rotated()), поэтому ссылка проверяется ПОСЛЕ операции по поимённым снимкам
        // тел. Отвергать её значило бы отказать вызывающему в единственном способе выразить
        // намерение — а принять и не проверить значило бы сообщить, что выбор учтён.
    }
    /// <summary>Совпали ли прочитанные параметры с запрошенными. Вынесено, чтобы чтение и ожидание
    /// описывались в отчёте одним и тем же способом.</summary>
    private static bool RotatedParametersMatch(RotatedDto? readBack, RotatedCommand command)
    {
        if (readBack is null)
        {
            return false;
        }

        // Угол сверяется с записанным напрямую: измерено (F.1…F.5), что модель возвращает ровно
        // тот угол, каким он построен, вплоть до 360°. Прежняя оговорка про «насыщение на 180°»
        // была следствием ОШИБКИ ЧТЕНИЯ: читалась половина пары, а не угол. Допуск остаётся
        // строгим — расхождение здесь означало бы, что построено не то, что просили.
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

    /// <summary>
    /// Цилиндрические грани главного тела: радиус и высота из <c>ksCylinderParam</c>. Пустой список
    /// и «не читается» здесь различимы вызывающим по наличию проверки, а не смешиваются.
    /// </summary>
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
                    // Грань, параметры которой КОМПАС не отдал, — не повод бросить обход остальных.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Пустой список — честный ответ «прочитать не удалось»; проверка ниже сравнит его с нулём.
        }

        return found;
    }

    /// <summary>
    /// Габарит главного тела: у цилиндра R20 H40 это 40×40×40, у полуцилиндра — 40×40×20. Это
    /// независимая от объёма проверка формы, поэтому бокс читается, а не подставляется нулём.
    /// </summary>
    /// <remarks>
    /// <c>null</c> означает «не прочитано» и НЕ равен нулевому боксу. Разница существенна: нулевой бокс
    /// при сравнении с ожидаемым габаритом выглядел бы как расхождение геометрии, то есть отсутствие
    /// чтения превратилось бы в утверждение о модели. Здесь то же различие, что у <c>MeasureVolume</c>
    /// и <c>BoundingBoxDto.Empty</c>: <c>GetGabarit</c> вернул <c>false</c> или бросил — это факт о
    /// ЧТЕНИИ, а не о теле.
    /// </remarks>
    private static BoundingBoxDto? SafeBounds(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return null;
            }

            // ksBody.GetGabarit, а не GetBoundingBoxEx: последнего у тела нет вовсе (CS1061),
            // и именно GetGabarit читает ReadBodyBox в Api5Session.Geometry.cs.
            return body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? new BoundingBoxDto(new[] { x1, y1, z1 }, new[] { x2, y2, z2 })
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Последний элемент дерева API5, который и есть созданный признак вращения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему сначала по номерам, а потом перебором.</b> У отверстия дефект «признак не
    /// находился никогда» стоил отдельного разбирательства: адаптер искал 52 (номер фабрики
    /// <c>o3d_holeOperation</c>) вместо 583 (номер дерева <c>o3d_Hole3D</c>), измеренного пробой N.1.
    /// У вращения измерение сделано приёмкой SM-03: строка RO.10t напечатала типы дерева после
    /// разреза вращением — <c>['25', '29']</c>, то есть разрез виден под <b>29</b>, тем же числом,
    /// каким он создавался фабрикой. Поэтому проверяются все три вида (27/28/29), а не одно число:
    /// вид операции выбирает клиент, и искать только <c>cut</c> значило бы не найти <c>base</c>.
    /// </para>
    /// <para>
    /// <b>Почему перебор не может выдать чужое.</b> Отбор идёт не по имени и не по индексу, а по
    /// наличию у сущности профиля и оси (<c>IRotated</c> отвечает на приведение). Имя здесь
    /// идентификатором не является — измерено на фаске (F.8: «f-ch2» → «Фаска:1»), — а индекс
    /// «последний элемент» без такой проверки указал бы на что угодно, если операция упала.
    /// Отсутствие осмысленного кандидата даёт <c>null</c>, и вызывающий получает
    /// <c>feature_ref_withheld</c> вместо ссылки, по которой правка всё равно не сработала бы.
    /// </para>
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

            // 1) По измеренным номерам — узкая проверка, а не догадка.
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

            // 2) Номер не подтвердился — признак ищется по своей природе, а не по числу. Это
            // запасной путь для случая, когда нумерация дерева окажется иной на другой сборке.
            // На объекте из дерева он, как правило, ничего не найдёт (QI на __ComObject отвечает
            // отказом, и IsRotatedEntity здесь падает на номер), поэтому найденное «по природе»
            // берётся как ЕДИНСТВЕННЫЙ кандидат, а не как «последний подходящий».
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
    /// Признак ли это вращения. Основной маршрут — измеренный номер дерева: 27
    /// (<c>o3d_baseRotated</c>), 28 (<c>o3d_bossRotated</c>), 29 (<c>o3d_cutRotated</c>). Это тот
    /// же приём, которым опознаётся родное отверстие (<c>entity.type == 583</c>, измерено пробой
    /// N.1), и он работает на признаке, ПРОЧИТАННОМ ИЗ ДЕРЕВА. Запасной маршрут — ответ на
    /// <c>QI(IRotated)</c>, он нужен для сущности, ПРОЧИТАННОЙ ИЗ РЕГИСТРА ССЫЛОК.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Почему одного QI недостаточно — измерено при приёмке SM-03 18.09.2026. Сущность, взятая из
    /// дерева через <c>EntityCollection</c>, приходит сырым <c>__ComObject</c>, и проверка
    /// <c>entity is IRotated</c> на ней отвечает <c>false</c>, хотя <c>entity.type</c> равен 29, а
    /// признак читается из модели. Первая редакция опиралась только на QI и потому молча теряла
    /// признак: имя ветки («по QI») выдавалось за результат опознания.
    /// </para>
    /// <para>
    /// Номер типа — это именно маршрут ПРИЗНАКА ИЗ ДЕРЕВА, а не идентификатор: он меняется между
    /// моментом создания и деревом (у выдавливания 24 → 25, измерено P2.3), поэтому опознавать
    /// следует по НАБОРУ номеров трёх видов вращения, а не по одному числу, запомненному при создании.
    /// </para>
    /// <para>
    /// <b>Второй маршрут (QI) на объекте из дерева недостижим, и это не оправдание, а измеренная
    /// граница.</b> Вопрос «отвечает ли <c>ksEntity</c> из дерева на QI(IRotated)» проверялся
    /// 18.09.2026 тремя способами: приведением <c>is</c>, приведением runtime-типа к интерфейсу и
    /// прямым вызовом члена <c>Angle</c> с перехватом — все три ОТКАЗАЛИ на признаке, который
    /// заведомо читается из модели (угол 360, тип 29, UpdateStamp живой). Поэтому ветка QI здесь
    /// оставлена как запасной путь для payload из регистра ссылок, но ОПОРА — на номер дерева:
    /// иначе опознание возвращалось бы к средству, которому объект из дерева не отвечает.
    /// </para>
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
            // Номер не прочитался — остаётся второй маршрут, и он решает.
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

    /// <summary>Читаемые параметры вращения: радиус и высота цилиндрической грани.</summary>
    private sealed record CylinderReadout(double Radius, double Height);

    private static string Num(double? value) =>
        value?.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture)
        ?? "не прочитано";
}

/// <summary>
/// Результат создания вращения.
/// </summary>
/// <param name="FeatureRef">Ссылка на признак; null, когда дерево API5 его не показывает.</param>
/// <param name="Operation">Вид выполненной операции словом контракта (base/boss/cut).</param>
/// <param name="AngleReadBackDeg">Угол, ПРОЧИТАННЫЙ из модели, а не записанный.</param>
/// <param name="DirectionReadBack">Направление, прочитанное из модели.</param>
/// <param name="AxisState">Состояние оси: «есть»/«нет»/«не прочитано».</param>
/// <param name="AxisNotes">Ход построения оси: заметки маршрута, включая случай Valid ≠ True.</param>
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

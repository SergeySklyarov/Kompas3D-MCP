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
/// Родное отверстие (docs/05 SM-07): три измеренных режима и позиция вне начала координат.
/// </summary>
/// <remarks>
/// <para>
/// Основание — проба M от 16.09.2026 (<c>docs/acceptance/api7/hole-modes.md</c>), а не имена методов:
/// <list type="bullet">
/// <item>M.2 — цековка: пилот Ø10 насквозь, выточка Ø18 глубиной 4. Снято 703.7167544041131 мм³
/// СВЕРХ сквозного отверстия, что есть π/4·(18²−10²)·4 = 703.7167544041137 — кольцо, а не второй
/// полный цилиндр. Первый набросок формулы складывал пилот с целым цилиндром Ø18 и тем самым
/// считал пилот дважды;</item>
/// <item>M.3 — зенковка: правило <c>π·h/3·(rM² + rP·rM − 2·rP²)</c> прочитано с таблицы из 11 строк
/// (3 угла × 3 входные глубины × 6 диаметров), а приёмка отказывается проходить, пока не сойдётся
/// ВСЯ таблица. Ключевое наблюдение — <c>CountersinkDepth</c> ПРОИЗВОДНА: запись 2, 4 и 6 не
/// меняет ничего, объект возвращает <c>(rM − rP)/tan(угол/2)</c>, и судить надо по возвращённому
/// числу. Первая редакция этого текста называла закон <c>4/tan(угол/2)</c> — константа была
/// подогнана под единственную строку таблицы, где <c>rM − rP = 4</c>; проба N.2 от 17.09.2026
/// развела устье при неизменных пилоте и угле (Ø14/16/18/20/24 → h = 2/3/4/5/7) и тем самым
/// показала, что «4» — это разность радиусов той строки, а не постоянная;</item>
/// <item>M.4 — глухое с плоским дном: снято 471.238898038471 против аналитических π·5²·6 =
/// 471.238898038469. Члена <c>ksDTBlind</c> в вендорском перечислении нет вовсе — глухое
/// выражается <c>ksDTValue</c>;</item>
/// <item>M.5 — позиция вне начала координат: из пяти маршрутов сдвинуло ровно один,
/// <c>Point3DParamSurface</c> + <c>OffsetType=ksOffsetByCoords</c> + <c>Offset1</c>/<c>Offset2</c>.
/// Отверстие Ø10 встало точно в (25, 15). <c>AssociationVertex</c> и <c>DirectionObject</c> дали
/// DISP_E_TYPEMISMATCH, эскиз со смещённой окружностью до API7 не доехал, <c>DepthVertex</c> и
/// <c>DepthFace</c> читаются как null, <c>Axis</c> как False.</item>
/// </list>
/// </para>
/// <para>
/// Почему маршрут API7, а не API5. Отверстие в API5 есть (<c>NewEntity(o3d_hole=52)</c>), но
/// параметров режима в его определении нет физически: проба M трижды отвергла попытку записать
/// режимные числа в сам <c>IHole3D</c>, пока не выяснилось, что они живут на <c>HoleParameters</c>,
/// приведённом к интерфейсу СВОЕГО режима. Это структурная причина, а не удобство: «цековка»
/// и «зенковка» отличаются не значением перечисления, а интерфейсом параметров.
/// </para>
/// <para>
/// Объём читается только по ГЛАВНОМУ телу — <c>ReadVolume</c>, как у скругления и фаски. Ожидание
/// дельты задаёт вызывающий: без него подтверждается лишь чтение параметров обратно, и результат
/// честно помечается недоказанной геометрией, а не выдаётся за подтверждённый.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Имя семейства отверстия в ответах сервера.</summary>
    private const string HoleFamily = "hole";

    /// <summary>Допуск сопоставления тела с отверстием по радиусу цилиндрической грани, мм.</summary>
    private const double HoleRadiusToleranceMm = 0.01d;

    /// <summary>
    /// Допуск сверки ЗАПИСАННОГО числа с перечитанным на правке, мм.
    /// </summary>
    /// <remarks>
    /// <c>1e-6</c> — тот же допуск, которым сверяется записанное с перечитанным при СОЗДАНИИ
    /// (<c>HoleParametersMatch</c>), и он не «на глаз»: зонд <c>scratch/_hole_edit_probe.py</c>
    /// прочитал записанные 10, 12, 20, 24, 5, 4 и 90 БЕЗ расхождения вовсе, а производная глубина
    /// зенковки вернулась как <c>7.000000000000001</c> — то есть собственный шум ядра лежит далеко
    /// за пределами этого допуска, и он не маскирует «не применилось».
    /// </remarks>
    private const double HoleEditToleranceMm = 1e-6d;

    /// <summary>
    /// Создание родного отверстия измеренного режима. Отказ моста отдаётся
    /// <c>CAPABILITY_UNAVAILABLE</c> с причиной, а не молчаливым null: вызывающий обязан отличать
    /// «API7 недоступен» от «КОМПАС отверг параметр».
    /// </summary>
    /// <remarks>
    /// Опорная грань берётся по явной ссылке <c>face:</c>, а не «верхней гранью тела». Проба M
    /// выбирала самую большую грань по площади, но это был приём пробы: <c>BaseSurface</c> — это
    /// решение клиента о том, где сверлить, и подменять его догадкой сервер не вправе.
    /// </remarks>
    public HoleResult Hole(HoleCommand command)
    {
        if (!References.TryGet(command.FaceRef, out var anchor) || anchor is null)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{command.FaceRef}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);
        }

        var document = RequireDocument(anchor.DocumentId);
        if (anchor.Payload is not ksFaceDefinition face)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Ссылка '{command.FaceRef}' указывает не на грань (kind={anchor.Kind}).",
                details: new Dictionary<string, object?> { ["kind"] = anchor.Kind });
        }

        ValidateHoleMode(command);

        var volumeBefore = ReadVolume(document);
        var facesBefore = CountFaces(document);
        var bodiesBefore = CountBodies(document);

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Параметры режима родного отверстия живут только в HoleParameters API7; " +
                "признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        // IChamfer.BaseObjects и IHoleDisposal.BaseSurface принимают объект API7, поэтому грань
        // обязана пересечь мост: непереданная грань — это значение, которое API7 не примет.
        var baseSurface = bridge.TransferTo7(face) as IModelObject;
        if (baseSurface is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Опорная грань не перенесена в API7 (" +
                (bridge.BridgeFailure ?? "TransferInterface вернул null") + ") — отверстие не создавалось.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var placementNotes = new List<string>();
        var (created, failure, reportedCountersinkDepth) =
            CreateHoleMode(bridge, container, command, baseSurface, placementNotes);

        if (!created)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Отверстие через IHole3D не создано: " + (failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = failure });
        }

        // Без RebuildModel запись в API7 остаётся представлением: это измерено пробой E на
        // IExtrusion.Sketch и повторено на фаске F.10 и скруглении. Порядок вызовов — часть
        // контракта, а не стиль.
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "hole." + command.Mode.ToString().ToLowerInvariant());

        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var bodiesAfter = CountBodies(document);

        var count = Api7Hole.Count(container);
        var readBack = count is int n and > 0 ? Api7Hole.Read(container, n - 1) : null;
        // Читаются ВСЕ оси подходящего радиуса, а не первая: при нескольких отверстиях одного
        // диаметра «первое совпадение» — это ось соседа, и координата созданного признака была бы
        // приписана ему. Ниже запрошенная позиция сверяется со списком отдельно.
        var axisOrigins = Api7Hole.FindCylinderOrigins(
            document.PartNow(), command.DiameterMm / 2d, HoleRadiusToleranceMm);
        var center = axisOrigins.Count > 0 ? axisOrigins[0] : null;

        var checks = new List<NamedCheck>
        {
            new("hole_created", true,
                Observed: command.Mode.ToString(),
                Expected: "режим создан и перестроен"),
            new("parameters_read_back", HoleParametersMatch(readBack, command),
                Observed: DescribeHole(readBack),
                Expected: DescribeHoleCommand(command)),
            new("face_count_grew", facesAfter is double after && facesBefore is double before && after > before,
                Observed: $"{facesBefore}→{facesAfter}",
                Expected: "цилиндрическая грань отверстия добавилась"),
            new("body_count_unchanged", bodiesAfter == bodiesBefore,
                Observed: bodiesAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        if (command.OffsetXMm is not null || command.OffsetYMm is not null)
        {
            var wantedX = command.OffsetXMm ?? 0d;
            var wantedY = command.OffsetYMm ?? 0d;
            // Ищется СРЕДИ ВСЕХ осей, а не у первой: запрос «сдвинь в (25, 15)» удовлетворён, если
            // ось с такими координатами есть на теле, и не удовлетворён, если её нет, даже когда
            // рядом стоит чужая ось подходящего радиуса.
            var matching = axisOrigins.FirstOrDefault(o =>
                Math.Abs(o[0] - wantedX) <= 0.5d && Math.Abs(o[1] - wantedY) <= 0.5d);
            checks.Add(new NamedCheck(
                "position_applied",
                matching is not null,
                Observed: axisOrigins.Count == 0
                    ? "осей Ø" + command.DiameterMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " на теле нет"
                    : string.Join("; ", axisOrigins.Select(o => $"({o[0]:0.###}, {o[1]:0.###})")),
                Expected: $"({wantedX:0.###}, {wantedY:0.###})"));
        }

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

        var geometryConfirmed = readBack is not null
            && checks.Exists(c => c.Name == "parameters_read_back" && c.Passed)
            && materialRemoved
            && numericMatch is not false
            && checks.TrueForAll(c => c.Name != "position_applied" || c.Passed);

        var unverified = new List<string>();
        if (!geometryConfirmed)
        {
            unverified.Add(
                "geometry_not_confirmed — КОМПАС принял запись, но измерение не подтвердило ожидаемую геометрию");
        }

        if (command.ExpectedVolumeDeltaMm3 is null)
        {
            unverified.Add(
                "expected_volume_delta_not_supplied — аналитическое ожидание дельты не задавал " +
                "вызывающий, численного доказательства нет");
        }

        if (command.Mode == HoleMode.ThroughCountersink)
        {
            // Производная глубина — измеренная особенность, а не оговорка: при способе
            // «диаметр + угол» запись в CountersinkDepth не действует, и объект возвращает
            // своё число. Поэтому в ответе публикуется возвращённое, и здесь сказано, почему
            // оно может отличаться от запрошенного.
            unverified.Add(
                "countersink_depth_derived — при способе «диаметр + угол» глубина зенковки " +
                "производна от угла (измерено M.3: запись 2/4/6 не меняет ничего, объект " +
                "возвращает (rM − rP)/tan(угол/2), где rM — радиус устья, rP — радиус пилота). " +
                "Сверяйте countersink_depth_mm, а не запрошенное число");
        }

        unverified.Add(
            "single_face_only — измерялся только один базовый случай на режим (M.2/M.3/M.4); " +
            "на наклонных гранях и в многотельных деталях режимы не проверялись");

        var reference = FindHoleEntity(document);
        if (reference is null)
        {
            // Ссылку на признак, которого дерево API5 не показывает, выдавать нельзя: правка по
            // ней всё равно упала бы, а вызывающий узнал бы об этом позже.
            unverified.Add("feature_ref_withheld — признак не найден в дереве API5, ссылка не выдана");
            return new HoleResult(
                null,
                command.Mode.ToString().ToLowerInvariant(),
                readBack?.DiameterMm,
                readBack?.DepthMm,
                reportedCountersinkDepth,
                center,
                bodiesAfter,
                volumeAfter,
                new VerificationDto(VerificationLevel.CallReturned, checks, unverified),
                string.Join(", ", placementNotes));
        }

        return new HoleResult(
            ToDto(References.Register("feature", document.Id, document.Revision, reference),
                $"hole Ø{command.DiameterMm:0.###} {command.Mode}"),
            command.Mode.ToString().ToLowerInvariant(),
            readBack?.DiameterMm,
            readBack?.DepthMm,
            reportedCountersinkDepth,
            center,
            bodiesAfter,
            volumeAfter,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            string.Join(", ", placementNotes));
    }

    /// <summary>
    /// Один режим — одна ветка. Возвращается (создано, причина отказа, возвращённая глубина
    /// зенковки): тройка, а не пара, потому что у зенковки есть поле, которое объект считает сам,
    /// и потерять его значило бы выдать записанное число за действующее.
    /// </summary>
    private static (bool Created, string? Failure, double? ReportedDepth) CreateHoleMode(
        Api7Bridge bridge,
        IModelContainer container,
        HoleCommand command,
        IModelObject baseSurface,
        List<string> placementNotes)
    {
        // Позиция — ОБЩИЙ шаг для всех трёх режимов, а не особенность глухого. Прежняя редакция
        // поддерживала смещение только в ветке blind_flat: приёмка HO.6 показала, что сквозная
        // цековка с offset_x_mm=-20 при этом МОЛЧА оставалась в начале координат и возвращала
        // err=None, то есть неверная позиция выдавалась за выполненную. Поэтому смещение
        // оформлено вставным шагом, который каждый режим выполняет между Add() и Update() на СВОЁМ
        // же объекте: отдельный Add() завёл бы второй признак, а первый остался бы недостроенным.
        var wantsOffset = command.OffsetXMm is not null || command.OffsetYMm is not null;
        Func<IHoleDisposal, PlacementOutcome>? place = wantsOffset
            ? disposal =>
            {
                var outcome = Api7Hole.TryPlaceByCoordinates(
                    disposal, command.OffsetXMm ?? 0d, command.OffsetYMm ?? 0d);
                placementNotes.AddRange(outcome.Notes);
                if (!outcome.Applied)
                {
                    placementNotes.Add(
                        "позиционирование не применено: " + (outcome.Failure ?? "причина не сообщена"));
                }

                return outcome;
            }
            : null;

        switch (command.Mode)
        {
            case HoleMode.BlindFlat:
                var blind = Api7Hole.TryCreateBlindFlat(
                    container, baseSurface, command.DiameterMm, command.DepthMm!.Value, place);
                return (blind.Created, blind.Failure, null);

            case HoleMode.ThroughCounterbore:
                var counterbore = Api7Hole.TryCreateCounterbore(
                    container,
                    baseSurface,
                    command.DiameterMm,
                    command.CounterboreDiameterMm!.Value,
                    command.CounterboreDepthMm!.Value,
                    place);
                return (counterbore.Created, counterbore.Failure, null);

            case HoleMode.ThroughCountersink:
                var countersink = Api7Hole.TryCreateCountersink(
                    container,
                    baseSurface,
                    command.DiameterMm,
                    command.CountersinkDiameterMm!.Value,
                    command.CountersinkAngleDeg!.Value,
                    // Глубина записывается нулём и не притворяется значимой: при способе
                    // «диаметр + угол» она производна, и запись в неё не действует (M.3).
                    depthMm: 0d,
                    place);
                return (countersink.Created, countersink.Failure, countersink.ReportedDepthMm);

            default:
                return (false, $"режим '{command.Mode}' не реализован", null);
        }
    }

    /// <summary>
    /// Правила «поля ↔ режим». Отклоняются до COM: режимные числа разных режимов живут в разных
    /// интерфейсах, и «применили, что смогли, остальное проигнорировали» здесь было бы молча неверной
    /// геометрией. Наличие проверки оплачено измерением — цена ошибки в этом месте не симметрична:
    /// лишний отказ виден сразу, а принятое и проигнорированное число доживает до приёмки.
    /// </summary>
    private static void ValidateHoleMode(HoleCommand command)
    {
        if (command.DiameterMm <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Диаметр отверстия обязан быть положительным: КОМПАС принимает ноль и строит " +
                "признак без материала при неизменном объёме (тот же дефект, что у нулевого катета " +
                "фаски, F.12). Это не отверстие.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["diameter_mm"] = command.DiameterMm });
        }

        switch (command.Mode)
        {
            case HoleMode.BlindFlat:
                if (command.DepthMm is not double depth || depth <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=blind_flat нужна положительная depth_mm: глухое отверстие без " +
                        "глубины выразить нечем.",
                        RetryPolicy.Never);
                }

                RejectPresent(command, "counterbore_diameter_mm", command.CounterboreDiameterMm, "blind_flat");
                RejectPresent(command, "counterbore_depth_mm", command.CounterboreDepthMm, "blind_flat");
                RejectPresent(command, "countersink_diameter_mm", command.CountersinkDiameterMm, "blind_flat");
                RejectPresent(command, "countersink_angle_deg", command.CountersinkAngleDeg, "blind_flat");
                break;

            case HoleMode.ThroughCounterbore:
                RejectPresent(command, "depth_mm", command.DepthMm, "through_counterbore");
                if (command.CounterboreDiameterMm is not double bore || bore <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_counterbore нужен положительный counterbore_diameter_mm: " +
                        "без диаметра выточки режим отличается от сквозного отверстия только именем.",
                        RetryPolicy.Never);
                }

                if (command.CounterboreDepthMm is not double boreDepth || boreDepth <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_counterbore нужна положительная counterbore_depth_mm.",
                        RetryPolicy.Never);
                }

                if (bore <= command.DiameterMm)
                {
                    // Выточка уже пилота не снимает ничего: измеренная формула M.2 даёт ноль или
                    // отрицательное кольцо, то есть признак построится «успешно» без геометрии.
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Диаметр выточки обязан быть больше диаметра пилота: выточка уже пилота не " +
                        "снимает материал (измеренная формула M.2 — кольцо π/4·(D²−d²)·h).",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?>
                        {
                            ["counterbore_diameter_mm"] = bore,
                            ["diameter_mm"] = command.DiameterMm,
                        });
                }

                RejectPresent(command, "countersink_diameter_mm", command.CountersinkDiameterMm, "through_counterbore");
                RejectPresent(command, "countersink_angle_deg", command.CountersinkAngleDeg, "through_counterbore");
                break;

            case HoleMode.ThroughCountersink:
                RejectPresent(command, "depth_mm", command.DepthMm, "through_countersink");
                if (command.CountersinkDiameterMm is not double mouth || mouth <= 0d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_countersink нужен положительный countersink_diameter_mm — " +
                        "диаметр устья, а не пилота.",
                        RetryPolicy.Never);
                }

                if (command.CountersinkAngleDeg is not double angle || angle <= 0d || angle >= 180d)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "При mode=through_countersink нужен countersink_angle_deg в интервале (0; 180): " +
                        "вне его конус зенковки не строится. Измерялись 60, 90 и 120 (M.3).",
                        RetryPolicy.Never);
                }

                if (mouth <= command.DiameterMm)
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Диаметр устья зенковки обязан быть больше диаметра пилота: устье уже пилота " +
                        "не снимает материал.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?>
                        {
                            ["countersink_diameter_mm"] = mouth,
                            ["diameter_mm"] = command.DiameterMm,
                        });
                }

                RejectPresent(command, "counterbore_diameter_mm", command.CounterboreDiameterMm, "through_countersink");
                RejectPresent(command, "counterbore_depth_mm", command.CounterboreDepthMm, "through_countersink");
                break;

            default:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Режим '{command.Mode}' не реализован: измерены blind_flat, through_counterbore " +
                    "и through_countersink.",
                    RetryPolicy.Never);
        }

        // Смещение задаётся парой или не задаётся вовсе: одно число из двух оставило бы вторую
        // координату на усмотрение сервера, то есть в ответе оказалась бы не та позиция, что просили.
        if ((command.OffsetXMm is null) != (command.OffsetYMm is null))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Смещение задаётся парой offset_x_mm и offset_y_mm: одна координата без второй " +
                "оставляет другую на догадку сервера.",
                RetryPolicy.Never);
        }
    }

    private static void RejectPresent(HoleCommand command, string field, double? value, string mode)
    {
        if (value is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"При mode={mode} поле {field} запрещено: оно принадлежит другому режиму, и принять " +
                "его значило бы выдать за применённый параметр, которого этот режим не читает.",
                RetryPolicy.Never);
        }
    }

    /// <summary>
    /// Последний элемент операции с <c>type = 52</c> (<c>o3d_hole</c>) в дереве API5. Поиск по типу
    /// и порядку, а не по имени: имя, данное в API5, читается из API7 иначе — измерено на фаске
    /// (F.8: «f-ch2» → «Фаска:1») — имя идентификатором признака не является.
    /// </summary>
    /// <summary>
    /// Последний элемент операции, который и есть признак отверстия. Поиск идёт по
    /// <see cref="KompasObjectTypes.Hole3D"/> (583, <c>o3d_Hole3D</c>), а НЕ по
    /// <see cref="KompasObjectTypes.HoleOperation"/> (52).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Это исправление измеренного дефекта, а не переименование. Первая версия искала 52 —
    /// <c>o3d_holeOperation</c>, то есть номер, под которым признак СОЗДАЁТСЯ через
    /// <c>NewEntity(52)</c>. Проба N.1 от 17.09.2026 напечатала обе коллекции дерева до и после
    /// создания и обе стороны вопроса: <c>NewEntity(52).type = 52 (o3d_holeOperation)</c>, а живой
    /// <c>IHoles3D[0].ModelObjectType = 583 (o3d_Hole3D)</c>; в дереве при этом появилась ровно одна
    /// запись — <c>OperationElement(110)[1] type=583 («Отверстие:1»)</c>. Номера 52 в дереве не
    /// появилось ни разу. Поэтому поиск по 52 не находил созданное API7 отверстие никогда,
    /// <c>feature_ref</c> не выдавался, и причина списывалась на «признак не виден в дереве».
    /// </para>
    /// <para>
    /// Обе коллекции просматриваются намеренно: «OperationElement = 110» — лишь одна из них, и
    /// отсутствие признака в одной не означало бы отсутствия в дереве вообще.
    /// </para>
    /// </remarks>
    private static ksEntity? FindHoleEntity(DocumentEntry document)
    {
        try
        {
            ksEntity? found = null;
            foreach (var kind in new[]
                     {
                         KompasObjectTypes.Of(KompasObjectTypes.OperationElement),
                         (short)-1,
                     })
            {
                if (document.PartNow().EntityCollection(kind) is not ksEntityCollection collection)
                {
                    continue;
                }

                for (var i = 0; i < collection.GetCount(); i++)
                {
                    if (collection.GetByIndex(i) is ksEntity entity
                        && entity.type == KompasObjectTypes.Hole3D)
                    {
                        found = entity;
                    }
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Сверка записанного с перечитанным. Сравниваются только те поля, которые режим действительно
    /// читает: у зенковки глубина НЕ сравнивается с запрошенной, потому что она производна, и
    /// требовать совпадения значило бы приёмке заведомо ложное утверждение.
    /// </summary>
    private static bool HoleParametersMatch(HoleReadDto? read, HoleCommand command)
    {
        if (read is null)
        {
            return false;
        }

        var diameterOk = read.DiameterMm is double d && Math.Abs(d - command.DiameterMm) <= 1e-6;
        return command.Mode switch
        {
            HoleMode.BlindFlat => diameterOk
                && read.DepthMm is double depth && Math.Abs(depth - command.DepthMm!.Value) <= 1e-6,
            HoleMode.ThroughCounterbore => diameterOk
                && read.SpotfacingDiameterMm is double bore
                && Math.Abs(bore - command.CounterboreDiameterMm!.Value) <= 1e-6
                && read.SpotfacingDepthMm is double boreDepth
                && Math.Abs(boreDepth - command.CounterboreDepthMm!.Value) <= 1e-6,
            HoleMode.ThroughCountersink => diameterOk
                && read.CountersinkDiameterMm is double mouth
                && Math.Abs(mouth - command.CountersinkDiameterMm!.Value) <= 1e-6
                && read.CountersinkAngleDeg is double angle
                && Math.Abs(angle - command.CountersinkAngleDeg!.Value) <= 1e-6,
            _ => false,
        };
    }

    private static string DescribeHole(HoleReadDto? read) =>
        read is null
            ? "IHole3D не читается"
            : $"тип={read.HoleType} D={Fmt(read.DiameterMm)} глубина={Fmt(read.DepthMm)} " +
              $"({read.DepthType}) дно={read.EndFaceType} выточка={Fmt(read.SpotfacingDiameterMm)}×" +
              $"{Fmt(read.SpotfacingDepthMm)} зенковка={Fmt(read.CountersinkDiameterMm)}@ " +
              $"{Fmt(read.CountersinkAngleDeg)}° h={Fmt(read.CountersinkDepthMm)}";

    private static string DescribeHoleCommand(HoleCommand command) => command.Mode switch
    {
        HoleMode.BlindFlat => $"тип=ksHTBase D={Fmt(command.DiameterMm)} глубина={Fmt(command.DepthMm)}",
        HoleMode.ThroughCounterbore =>
            $"тип=ksHTCounterbore D={Fmt(command.DiameterMm)} выточка={Fmt(command.CounterboreDiameterMm)}×" +
            $"{Fmt(command.CounterboreDepthMm)}",
        HoleMode.ThroughCountersink =>
            $"тип=ksHTCountersinking D={Fmt(command.DiameterMm)} устье=" +
            $"{Fmt(command.CountersinkDiameterMm)}@ {Fmt(command.CountersinkAngleDeg)}°",
        _ => command.Mode.ToString(),
    };

    private static string Fmt(double? value) =>
        value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается";

    /// <summary>
    /// Что сервер видит по отверстию: тип, диаметр, глубина и параметры режима.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Читается ТОЛЬКО из API7, и это измеренный факт, а не выбор: в вендорской обёртке
    /// <c>Interop.Kompas6API5</c> типа <c>ksHoleDefinition</c> НЕ СУЩЕСТВУЕТ вовсе — среди 67
    /// объявленных определений (<c>ksChamferDefinition</c> и <c>ksFilletDefinition</c> есть,
    /// отверстия нет). Отверстие описывается признаком с <c>type = 52</c> (<c>o3d_hole</c>), но
    /// определения у него в API5 нет, а значит и читать его параметры оттуда нечем. Ровно поэтому
    /// SM-07 и ушёл на маршрут API7 (ADR-004 §3): не «удобнее», а «в API5 не выражено».
    /// </para>
    /// <para>
    /// Признак адресуется ИНДЕКСОМ в <c>IModelContainer.Holes3D</c>. Если отверстий несколько, а
    /// вызывающий назвал не то, — числа были бы чужими, поэтому сопоставление однозначное:
    /// при неоднозначности возвращается null, и уровень подтверждения падает честно.
    /// </para>
    /// <para>
    /// Пустое поле означает «не прочитано», а не «ноль».
    /// </para>
    /// </remarks>
    private HoleDto? ReadHole(DocumentEntry document, int index)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null || Api7Hole.Count(container) is not int count || index < 0 || index >= count)
        {
            return null;
        }

        var read = Api7Hole.Read(container, index);
        if (read is null)
        {
            return null;
        }

        return new HoleDto(
            HoleType: read.HoleType,
            DiameterMm: read.DiameterMm,
            DepthType: read.DepthType,
            DepthMm: read.DepthMm,
            EndFaceType: read.EndFaceType,
            CounterboreDiameterMm: read.SpotfacingDiameterMm,
            CounterboreDepthMm: read.SpotfacingDepthMm,
            CountersinkDiameterMm: read.CountersinkDiameterMm,
            CountersinkAngleDeg: read.CountersinkAngleDeg,
            CountersinkDepthMm: read.CountersinkDepthMm,
            CenterMm: read.DiameterMm is double d
                ? Api7Hole.FindCylinderOrigin(document.PartNow(), d / 2d, HoleRadiusToleranceMm)
                : null);
    }

    /// <summary>
    /// Параметры отверстия для СУЩЕСТВУЮЩЕГО признака дерева — тот же <see cref="ReadHole"/>, но
    /// без индекса от вызывающего: вызывающий назвал признак, и сопоставление обязано быть
    /// однозначным, иначе числа были бы чужими.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сопоставление идёт по ЧИСЛУ отверстий в контейнере API7: одно отверстие — одна связь. Если
    /// их несколько, соответствие «признак дерева ↔ запись Holes3D» ничем не доказано (имя признака
    /// не переживает переход API5↔API7 — измерено пробой F), и возвращается null: пустое поле
    /// честнее чужого числа. Тогда <c>family</c> остаётся распознанным, но уровень подтверждения
    /// честно падает до <c>call_returned</c>.
    /// </para>
    /// <para>
    /// Индексом 0 ограничиваться нельзя: <c>Holes3D[0]</c> — это «первое отверстие документа», а не
    /// «отверстие того признака, который спросили». На документе с одним отверстием это одно и то
    /// же, и потому строка приёмки HD.25 сначала создаёт РОВНО одно отверстие.
    /// </para>
    /// </remarks>
    private HoleDto? ReadHoleFeature(DocumentEntry document)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null || Api7Hole.Count(container) != 1)
        {
            return null;
        }

        return ReadHole(document, 0);
    }

    /// <summary>
    /// Правка параметров СУЩЕСТВУЮЩЕГО родного отверстия по <c>kompas_update_feature</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут измерен 20.09.2026</b> (зонд <c>scratch/_hole_edit_probe.py</c>, нога 2 — сырой
    /// помощник <c>scratch/hole-edit-raw</c>; отчёт <c>docs/acceptance/api7/hole-modes.md</c>, раздел
    /// M.6). Признак берётся ДОКУМЕНТИРОВАННЫМ членом <c>IHoles3D.Hole3D[index]</c> — тем же
    /// маршрутом, которым читает <c>kompas_get_feature</c>, — в него пишутся члены СВОЕГО режима,
    /// применяется <c>IModelObject.Update()</c>, затем перестроение. Объём меняется ровно на
    /// аналитику во всех трёх режимах, а контроли (б) и (в) показывают, что изменение даёт именно
    /// <c>Update()</c>: без него объём не двигается, а интерфейс параметров чужого режима на объекте
    /// НЕДОСТИЖИМ.
    /// </para>
    /// <para>
    /// <b>Адрес признака — тот же, что у чтения, и НЕ угадывается.</b> Соответствие «признак дерева ↔
    /// запись <c>Holes3D</c>» доказывается единственностью отверстия в документе: при <c>count != 1</c>
    /// соответствие ничем не подтверждено, и вызов отвергается <c>CAPABILITY_UNAVAILABLE</c> до COM.
    /// Подбирать адрес по списку тел или по порядку в дереве запрещено уроком F-11: адрес
    /// обеспечивается постановкой, а не догадкой.
    /// </para>
    /// <para>
    /// <b>Режим правкой НЕ меняется.</b> Режим существующего признака читается из модели
    /// (<c>IHole3D.HoleType</c>) и служит рамкой: поля чужого режима отвергаются по имени до COM, а
    /// записываются члены СВОЕГО. Смена режима (<c>blind_flat</c> → <c>through_counterbore</c> и
    /// обратно) не измерялась и здесь не выполняется — «принято и построено иначе» неотличимо потом
    /// от «применено».
    /// </para>
    /// <para>
    /// <b>Глубина зенковки не утверждается.</b> При <c>ksCTDiameterAngle</c> она производна
    /// (M.3/N.2), поэтому проверка <c>countersink_depth_derived</c> публикует ПРОЧИТАННОЕ число и
    /// говорит прямо, что записанное не проверяется.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateHole(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        // Поля ЧУЖИХ семейств — до COM, и это не осторожность, а измеренный класс дефекта: принятое
        // и не применённое число доживает до приёмки, выглядя как выполненная правка. Перечень
        // строится по общей таблице семейственных полей плюс явный список неприменимых к признакам
        // (см. RejectForeignSolidFields), поэтому новое поле контракта обязано получить роль — иначе
        // его уронит SolidFeatureClassificationTests, а не приёмка.
        RejectForeignSolidFields(
            command,
            HoleFamily,
            "diameter_mm, depth_mm (только режим blind_flat), counterbore_diameter_mm и " +
            "counterbore_depth_mm (только through_counterbore), countersink_diameter_mm и " +
            "countersink_angle_deg (только through_countersink), expected_volume_delta_mm3",
            // depth_mm — СВОЁ поле этого семейства, хотя таблица ролей отдаёт его выдавливанию:
            // у глухого отверстия глубина задаётся именно им. Чужой ли он ДЛЯ РЕЖИМА (у цековки и
            // зенковки глубина производна или задаётся выточкой) решает ValidateHoleEdit по
            // прочитанному режиму — там это и измерено. Без этой строки правка глухого отверстия
            // отвергалась до COM: измерено строками F08.15/16/19/20.edit 20.09.2026.
            ownFields: new[] { "depth_mm" });

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Параметры режима родного отверстия живут только в HoleParameters API7; признак " +
                "не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // Адрес: ИНДЕКС в IHoles3D — тот же маршрут, что у чтения (ReadHoleFeature). Единственность
        // отверстия и есть доказательство соответствия; при нескольких отверстиях «нулевой» элемент
        // коллекции — это чужое отверстие, и запись в него изменила бы не тот признак.
        var count = Api7Hole.Count(container);
        if (count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Признак отверстия не сопоставлен с записью коллекции API7: отверстий в документе " +
                $"{count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано"}, " +
                "а адрес существующего признака — индекс в IHoles3D, и при нескольких отверстиях " +
                "соответствие «признак дерева ↔ запись Holes3D» ничем не доказано. Правка не " +
                "выполняется: запись в чужое отверстие изменила бы не тот объект.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["holes_count"] = count });
        }

        var readBefore = Api7Hole.Read(container, 0);
        var mode = HoleModeOfRead(readBefore);
        if (mode is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Режим существующего отверстия не опознан: IHole3D.HoleType прочитан как " +
                $"'{readBefore?.HoleType ?? "не читается"}', а правка выполняется только в СВОЁМ " +
                "режиме. Измерены три: ksHTBase, ksHTCounterbore, ksHTCountersinking.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["hole_type"] = readBefore?.HoleType });
        }

        ValidateHoleEdit(command, mode.Value);

        bool written;
        string? failure;
        double? reportedCountersinkDepth = null;
        switch (mode.Value)
        {
            case HoleMode.BlindFlat:
            {
                var outcome = Api7Hole.TryWriteBlindFlat(container, 0, command.DiameterMm, command.DepthMm);
                written = outcome.Written;
                failure = outcome.Failure;
                break;
            }

            case HoleMode.ThroughCounterbore:
            {
                var outcome = Api7Hole.TryWriteCounterbore(
                    container, 0, command.DiameterMm,
                    command.CounterboreDiameterMm, command.CounterboreDepthMm);
                written = outcome.Written;
                failure = outcome.Failure;
                break;
            }

            default:
            {
                var outcome = Api7Hole.TryWriteCountersink(
                    container, 0, command.DiameterMm,
                    command.CountersinkDiameterMm, command.CountersinkAngleDeg);
                written = outcome.Written;
                failure = outcome.Failure;
                reportedCountersinkDepth = outcome.ReportedDepthMm;
                break;
            }
        }

        if (!written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Параметры отверстия не записаны: " + (failure ?? "причина не сообщена"),
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["api7_failure"] = failure,
                    ["hole_mode"] = mode.Value.ToString(),
                });
        }

        // Порядок «запись → Update() → перестроение» — часть контракта, а не стиль: без RebuildModel
        // запись в API7 остаётся представлением (измерено пробой E и повторено на фаске и скруглении).
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "hole.update");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var readBack = Api7Hole.Read(container, 0);
        var stateAfter = ReadFeatureState(entity);

        var checks = new List<NamedCheck>
        {
            new("feature_identity_preserved",
                string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal),
                Observed: stateAfter.Name, Expected: stateBefore.Name),
            new("hole_mode_preserved",
                string.Equals(readBefore?.HoleType, readBack?.HoleType, StringComparison.Ordinal),
                Observed: readBack?.HoleType ?? "не читается",
                Expected: readBefore?.HoleType ?? "не читается"),
        };

        // Каждому ЗАПРОШЕННОМУ числу отвечает своя проверка чтения обратно: «Update() вернул true»
        // применением не является, а сводная проверка «параметры совпали» не отличила бы применённое
        // поле от неприменённого.
        if (command.DiameterMm is double wantedDiameter)
        {
            checks.Add(new NamedCheck(
                "diameter_read_back",
                readBack?.DiameterMm is double got && Math.Abs(got - wantedDiameter) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.DiameterMm), Expected: Fmt(wantedDiameter)));
        }

        if (command.DepthMm is double wantedDepth)
        {
            checks.Add(new NamedCheck(
                "depth_read_back",
                readBack?.DepthMm is double got && Math.Abs(got - wantedDepth) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.DepthMm), Expected: Fmt(wantedDepth)));
        }

        if (command.CounterboreDiameterMm is double wantedBore)
        {
            checks.Add(new NamedCheck(
                "counterbore_diameter_read_back",
                readBack?.SpotfacingDiameterMm is double got && Math.Abs(got - wantedBore) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.SpotfacingDiameterMm), Expected: Fmt(wantedBore)));
        }

        if (command.CounterboreDepthMm is double wantedBoreDepth)
        {
            checks.Add(new NamedCheck(
                "counterbore_depth_read_back",
                readBack?.SpotfacingDepthMm is double got && Math.Abs(got - wantedBoreDepth) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.SpotfacingDepthMm), Expected: Fmt(wantedBoreDepth)));
        }

        if (command.CountersinkDiameterMm is double wantedMouth)
        {
            checks.Add(new NamedCheck(
                "countersink_diameter_read_back",
                readBack?.CountersinkDiameterMm is double got && Math.Abs(got - wantedMouth) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.CountersinkDiameterMm), Expected: Fmt(wantedMouth)));
        }

        if (command.CountersinkAngleDeg is double wantedAngle)
        {
            checks.Add(new NamedCheck(
                "countersink_angle_read_back",
                readBack?.CountersinkAngleDeg is double got && Math.Abs(got - wantedAngle) <= HoleEditToleranceMm,
                Observed: Fmt(readBack?.CountersinkAngleDeg), Expected: Fmt(wantedAngle)));
        }

        if (mode.Value == HoleMode.ThroughCountersink)
        {
            // Производное число ПУБЛИКУЕТСЯ, но не утверждается записанным: при способе «диаметр +
            // угол» запись в CountersinkDepth не действует (M.3), и сверять её с запрошенным числом
            // значило бы требовать от приёмки заведомо ложного совпадения.
            checks.Add(new NamedCheck(
                "countersink_depth_derived",
                reportedCountersinkDepth is not null,
                Observed: Fmt(reportedCountersinkDepth),
                Expected: "производна от угла и устья; записанное число не утверждается"));
        }

        // Знак дельты — часть определения величины: объём уменьшается, когда правка снимает материал,
        // и РАСТЁТ, когда глубина уменьшается. Поэтому сверяется «снято» = до − после, а не модуль:
        // первая редакция зонда сравнивала разноимённые числа и давала ложное «не совпало» на верной
        // геометрии (дефект прибора, а не факт о продукте).
        var removedDelta = volumeBefore is double beforeVolume && volumeAfter is double afterVolume
            ? beforeVolume - afterVolume
            : (double?)null;

        bool? deltaMatched = null;
        if (command.ExpectedVolumeDeltaMm3 is double expectedDelta)
        {
            deltaMatched = removedDelta is double measuredDelta
                           && Math.Abs(measuredDelta - expectedDelta) <= ProfileArea.Tolerance(expectedDelta);
            checks.Add(new NamedCheck(
                "volume_delta",
                deltaMatched.Value,
                Observed: removedDelta is double observedDelta
                    ? observedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    : "not_computable",
                Expected: expectedDelta.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }

        bool? volumeMatched = null;
        if (command.ExpectedVolumeMm3 is double expectedVolume)
        {
            volumeMatched = volumeAfter is double measuredVolume
                            && Math.Abs(measuredVolume - expectedVolume) <= ProfileArea.Tolerance(expectedVolume);
            checks.Add(new NamedCheck(
                "volume_expected", volumeMatched.Value,
                Observed: Fmt(volumeAfter),
                Expected: expectedVolume.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var declared = command.ExpectedVolumeDeltaMm3 is not null || command.ExpectedVolumeMm3 is not null;
        var unverified = new List<string>();
        if (!declared)
        {
            unverified.Add(
                "expected_volume_delta_not_supplied — аналитическое ожидание дельты объёма не задавал " +
                "вызывающий, численного доказательства правки нет: подтверждено только чтение " +
                "параметров обратно");
        }

        if (mode.Value == HoleMode.ThroughCountersink)
        {
            unverified.Add(
                "countersink_depth_derived — при способе «диаметр + угол» глубина зенковки производна " +
                "от угла (измерено M.3: запись 2/4/6 не меняет ничего, объект возвращает " +
                "(rM − rP)/tan(угол/2)). Записанное число не проверяется");
        }

        unverified.Add(
            "hole_identity_by_single_hole — соответствие признака дерева и записи IHoles3D доказано " +
            "единственностью отверстия в документе; при нескольких отверстиях вызов отвергается, а " +
            "сопоставление по имени или по порядку в дереве не измерялось");

        unverified.Add(
            "dependent_features_not_enumerated — сохранность признаков, построенных ПОСЛЕ отверстия, " +
            "здесь не проверяется");

        var readBacksOk = checks.TrueForAll(c =>
            (!c.Name.EndsWith("_read_back", StringComparison.Ordinal) || c.Passed)
            && (c.Name != "hole_mode_preserved" || c.Passed)
            && (c.Name != "feature_identity_preserved" || c.Passed));
        var expectationsOk = (command.ExpectedVolumeDeltaMm3 is null || deltaMatched is true)
                             && (command.ExpectedVolumeMm3 is null || volumeMatched is true);
        var geometryConfirmed = declared && expectationsOk && readBacksOk && readBack is not null;

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            HoleFamily,
            string.Equals(stateBefore.Name, stateAfter.Name, StringComparison.Ordinal),
            featuresAfter,
            volumeBefore,
            volumeAfter,
            readBack?.DepthMm,
            null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified));
    }

    /// <summary>
    /// Режим существующего отверстия по прочитанному <c>IHole3D.HoleType</c>.
    /// </summary>
    /// <remarks>
    /// Неизвестное значение возвращает <c>null</c>, а не «похожий» режим: правка в чужом режиме
    /// записала бы числа в интерфейс, которого объект не читает, и объём не изменился бы — то есть
    /// отказ выглядел бы как выполненная правка.
    /// </remarks>
    private static HoleMode? HoleModeOfRead(HoleReadDto? read) => read?.HoleType switch
    {
        "ksHTBase" => HoleMode.BlindFlat,
        "ksHTCounterbore" => HoleMode.ThroughCounterbore,
        "ksHTCountersinking" => HoleMode.ThroughCountersink,
        _ => null,
    };

    /// <summary>
    /// «Поля ↔ режим» на ПРАВКЕ: у режима свой набор, и чужое поле отвергается по имени до COM.
    /// </summary>
    /// <remarks>
    /// То же правило, что при создании (<see cref="ValidateHoleMode"/>), и по той же причине:
    /// режимные числа разных режимов живут в РАЗНЫХ интерфейсах параметров, поэтому «приняли, что
    /// смогли, остальное проигнорировали» — это молча неверная геометрия. Положительность
    /// проверяется по измеренному основанию: нулевой диаметр КОМПАС принимает и строит признак без
    /// материала при неизменном объёме (тот же дефект, что у нулевого катета фаски, F.12).
    /// </remarks>
    private static void ValidateHoleEdit(UpdateFeatureCommand command, HoleMode mode)
    {
        switch (mode)
        {
            case HoleMode.BlindFlat:
                RejectForeignHoleEditField(command, "blind_flat", "counterbore_diameter_mm",
                    command.CounterboreDiameterMm);
                RejectForeignHoleEditField(command, "blind_flat", "counterbore_depth_mm",
                    command.CounterboreDepthMm);
                RejectForeignHoleEditField(command, "blind_flat", "countersink_diameter_mm",
                    command.CountersinkDiameterMm);
                RejectForeignHoleEditField(command, "blind_flat", "countersink_angle_deg",
                    command.CountersinkAngleDeg);
                PositiveHoleEditValue("diameter_mm", command.DiameterMm, "blind_flat");
                PositiveHoleEditValue("depth_mm", command.DepthMm, "blind_flat");
                break;

            case HoleMode.ThroughCounterbore:
                RejectForeignHoleEditField(command, "through_counterbore", "depth_mm", command.DepthMm);
                RejectForeignHoleEditField(command, "through_counterbore", "countersink_diameter_mm",
                    command.CountersinkDiameterMm);
                RejectForeignHoleEditField(command, "through_counterbore", "countersink_angle_deg",
                    command.CountersinkAngleDeg);
                PositiveHoleEditValue("diameter_mm", command.DiameterMm, "through_counterbore");
                PositiveHoleEditValue("counterbore_diameter_mm", command.CounterboreDiameterMm,
                    "through_counterbore");
                PositiveHoleEditValue("counterbore_depth_mm", command.CounterboreDepthMm,
                    "through_counterbore");
                break;

            case HoleMode.ThroughCountersink:
                RejectForeignHoleEditField(command, "through_countersink", "depth_mm", command.DepthMm);
                RejectForeignHoleEditField(command, "through_countersink", "counterbore_diameter_mm",
                    command.CounterboreDiameterMm);
                RejectForeignHoleEditField(command, "through_countersink", "counterbore_depth_mm",
                    command.CounterboreDepthMm);
                PositiveHoleEditValue("diameter_mm", command.DiameterMm, "through_countersink");
                PositiveHoleEditValue("countersink_diameter_mm", command.CountersinkDiameterMm,
                    "through_countersink");
                PositiveHoleEditValue("countersink_angle_deg", command.CountersinkAngleDeg,
                    "through_countersink");
                break;

            default:
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Режим '{mode}' не реализован: измерены blind_flat, through_counterbore и " +
                    "through_countersink.",
                    RetryPolicy.Never);
        }
    }

    private static void RejectForeignHoleEditField(
        UpdateFeatureCommand command,
        string mode,
        string field,
        double? value)
    {
        if (value is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — отверстие режима {mode}: поле {field} принадлежит ДРУГОМУ режиму, и " +
                "принять его значило бы выдать за применённый параметр, которого этот режим не " +
                "читает. Свои поля: " + HoleOwnFields(mode) + ". Смена режима существующего " +
                "отверстия правкой не выполняется: этот маршрут не измерялся.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["family"] = HoleFamily,
                    ["hole_mode"] = mode,
                    ["foreign_fields"] = new[] { field },
                });
        }
    }

    private static string HoleOwnFields(string mode) => mode switch
    {
        "blind_flat" => "diameter_mm, depth_mm",
        "through_counterbore" => "diameter_mm, counterbore_diameter_mm, counterbore_depth_mm",
        "through_countersink" => "diameter_mm, countersink_diameter_mm, countersink_angle_deg",
        _ => "нет",
    };

    private static void PositiveHoleEditValue(string field, double? value, string mode)
    {
        if (value is double number && (number <= 0d || double.IsNaN(number) || double.IsInfinity(number)))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Признак — отверстие режима {mode}: поле {field} обязано быть положительным конечным " +
                "числом. Нулевой диаметр КОМПАС принимает и строит признак без материала при " +
                "неизменном объёме (измерено на создании, тот же класс, что нулевой катет фаски F.12), " +
                "то есть «принято» здесь не означает «применено».",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = field, ["value"] = value });
        }
    }
}

/// <summary>
/// Результат родного отверстия. Диаметр и глубина возвращаются перечитанными из модели, а не
/// переданными: «мы вызвали Update()» геометрическим фактом не является. <c>feature_ref</c> пуст,
/// когда признак создан, но не виден в дереве API5: ссылка, по которой правка всё равно упала бы,
/// честнее предупреждения. <c>countersink_depth_mm</c> — то, что вернул объект, а не то, что
/// записали: при способе «диаметр + угол» это свойство производное (M.3).
/// </summary>
public sealed record HoleResult(
    ReferenceDto? FeatureRef,
    string Mode,
    double? DiameterReadBackMm,
    double? DepthReadBackMm,
    double? CountersinkDepthReadBackMm,
    double[]? CenterMm,
    int BodyCount,
    double? VolumeMm3,
    VerificationDto Verification,
    string PlacementRoute);

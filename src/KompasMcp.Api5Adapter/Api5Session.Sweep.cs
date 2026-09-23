using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Кинематическая операция — «Элемент по траектории» (docs/05 SM-04, очередь B5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — документированный API5, и это проверено по справке, а не по аналогии с вращением.</b>
/// <c>ksbaseevolutiondefinition.html</c> («Основание — кинематический элемент (Интерфейсы
/// ksBaseEvolutionDefinition, IBaseEvolutionDefinition)») описывает интерфейс, который «можно
/// получить, используя метод интерфейса элемента модели <c>ksEntity::GetDefinition</c>», и
/// перечисляет ровно те члены, что здесь используются: <c>sketchShiftType</c>, <c>SetSketch</c>,
/// <c>PathPartArray</c>, <c>GetPathLength(bitVector)</c>. Тип объекта — <c>o3d_baseEvolution = 45</c>
/// (<c>obj3dtype.html</c>). При этом <c>ievolutions_add.html</c> перечисляет допустимыми значениями
/// <c>IEvolutions::Add</c> только <c>o3d_bossEvolution</c> (46) и <c>o3d_cutEvolution</c> (47) —
/// <b>базового типа 45 в списке нет</b>. Измерено 20.09.2026 (шаг B5.7): <c>IEvolutions.Add(45)</c>
/// возвращает <c>KompasAPI7.EvolutionClass</c>, то есть объект ВЫДАЁТСЯ, — но валидность тела по
/// этому пути не измерялась, и «выдан объект» не то же самое, что «документированный маршрут».
/// Поэтому создание идёт <c>ksPart.NewEntity(45)</c> + <c>ksBaseEvolutionDefinition</c>, а не через
/// фабрику API7.
/// </para>
/// <para>
/// <b>Справка объявляет этот интерфейс устаревшим — и это записано, а не спрятано.</b>
/// «Данный интерфейс устарел. Рекомендуется использовать вместо него интерфейс
/// ksBossLoftDefinition» (в тексте страницы именно так, хотя для кинематического элемента
/// естественен <c>ksBossEvolutionDefinition</c> — расхождение внутри самой справки). Приклеенный
/// маршрут <c>NewEntity(46)</c> измерен отдельно (шаг B5.8) и строит ТО ЖЕ тело:
/// <c>31415.92653589775</c> против <c>31415.926535897932</c> на эталоне «окружность Ø20 по отрезку
/// 100». Обязательные строки этапа описаны как <b>базовые</b>, поэтому здесь используется тип 45.
/// </para>
/// <para>
/// <b>Режимы движения сечения документированы и совпали с измерением.</b>
/// <c>ksbaseevolutiondefinition_sketchshifttype.html</c>: 0 — «образующая переносится параллельно
/// самой себе», 1 — «сохраняет исходный угол с направляющей», 2 — «плоскость образующей
/// выставляется и сохраняется ортогональной направляющей». Измерено (шаг B5.2): на дуге R50/90°
/// ортогональный режим дал <c>S × L = 24674.011002723397</c>, параллельный отличается на
/// <c>8966.04773477437</c> мм³ — то есть режим различает.
/// </para>
/// <para>
/// <b>Траектория присоединяется к <c>ksEntityCollection</c>, и это измерено.</b> Шаг B5.1:
/// <c>PathPartArray()</c> возвращает <c>System.__ComObject</c>, который успешно приводится к
/// <c>ksEntityCollection</c>, и <c>Add(эскиз)</c> возвращает <c>True</c>. Рефлексия по
/// <c>__ComObject</c> членов не даёт и как способ разведки непригодна — приведение типа работает,
/// отражение нет.
/// </para>
/// <para>
/// <b>Что здесь считается доказательством.</b> <c>Create() = true</c> и <c>Update() = true</c> — это
/// «принято», а не «применено»: объём читается обратно с модели и сравнивается с аналитическим
/// ожиданием вызывающего. Без ожидания уровень честно остаётся <c>call_returned</c>.
/// </para>
/// <para>
/// <b>Чего здесь нет и почему.</b> Тонкая стенка (<c>SetThinParam</c>) не задаётся: маршрут измерен
/// на сплошном теле, и ставить неизмеренное число значило бы выдать желаемое за проверенное.
/// Вырезание телом (<c>SM-04.cut</c>, OQ-A2) вне обязательного объёма и не реализуется.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Базовое тело кинематической операции — <c>o3d_baseEvolution</c>.</summary>
    private const short BaseEvolution = 45;

    /// <summary>Единица длины для <c>GetPathLength</c>: <c>ST_MIX_LENGTH_MM</c>.</summary>
    private const uint PathLengthMillimetres = 1u;

    /// <summary>Кинематическая операция: профиль по траектории (SM-04).</summary>
    public SweepResult Sweep(SweepCommand command)
    {
        ValidateSweepCommand(command);

        var target = RequireSketch(command.SketchRef);
        var document = target.Document;
        var part = document.PartNow();

        // Траектория обязана лежать в ТОЙ ЖЕ детали. Разные документы дали бы либо отказ ядра, либо
        // — хуже — молчаливо подставленную чужую геометрию, а этого здесь не проверял никто.
        var path = RequireSketch(command.PathRef);
        if (!string.Equals(path.Document.Id, document.Id, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Профиль и траектория принадлежат разным документам: кинематическая операция строится " +
                "в одной детали, и переносить траекторию из чужой нельзя.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["sketch_document_id"] = document.Id,
                    ["path_document_id"] = path.Document.Id,
                });
        }

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);

        if (part.NewEntity(BaseEvolution) is not ksEntity entity
            || entity.GetDefinition() is not ksBaseEvolutionDefinition definition)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение кинематической операции не получено: NewEntity(45) вернул объект, у " +
                "которого нет ksBaseEvolutionDefinition. Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        // Ответы входных вызовов СОБИРАЮТСЯ, а не отбрасываются. Измерено 20.09.2026: на геометрии,
        // которую проба строит тем же маршрутом и получает тело (окружность R10 по отрезку 100 —
        // 31415.92653589775), через MCP Create() отвечал false на ШЕСТИ разных постановках подряд.
        // Отказ без этих чисел неразличим с «ядру не понравилась геометрия», и разбирать его
        // пришлось бы заново. Поэтому SetSketch и содержимое держателя траектории читаются обратно.
        var sketchAccepted = SafeBool(() => definition.SetSketch(target.Sketch));
        definition.sketchShiftType = ShiftValue(command.ShiftMode);
        var sketchReadBack = SafeBool(() => definition.GetSketch() is not null);

        var pathParts = AttachPath(definition, path.Sketch);
        if (pathParts == 0)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Траектория не присоединена: PathPartArray() не привёлcя к ksEntityCollection. " +
                "Операция не создавалась.",
                RetryPolicy.ReacquireContext);
        }

        var pathPartsReadBack = PathPartCount(() => definition.PathPartArray());

        var created = SafeBool(entity.Create);

        if (created != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Create() кинематической операции вернул " + (created is null ? "ошибку вызова" : "false") +
                ": тело не построено. Траектория с разрывом или профиль, не пересекающий её, дают " +
                "именно этот исход, и он отказ, а не частичный результат.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["set_sketch_accepted"] = sketchAccepted,
                    ["sketch_read_back_present"] = sketchReadBack,
                    ["path_parts_attached"] = pathParts,
                    ["path_parts_read_back"] = pathPartsReadBack,
                    ["profile_sketch_name"] = target.Sketch.name,
                    ["path_sketch_name"] = path.Sketch.name,
                    ["entity_type_after_new"] = entity.type,
                    ["definition_runtime"] = definition.GetType().Name,
                    ["definition_answers_boss"] = definition is ksBossEvolutionDefinition,
                });
        }

        SafeBool(entity.Update);
        part.RebuildModel();
        document.Document.RebuildDocument();

        // Длина траектории читается ПОСЛЕ построения: первый прогон пробы читал её до Create() и
        // получал 0 — это был дефект прибора, а не факт о продукте (шаг B5.3).
        var pathLength = SafeDouble(() => definition.GetPathLength(PathLengthMillimetres));

        var reference = References.Register("feature", document.Id, document.Revision, entity);
        BumpRevision(document, "sweep.create");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);

        var checks = new List<NamedCheck>
        {
            new("operation_created", created == true, "Create() вернул true"),
            new("path_attached", pathParts > 0, "элементов траектории: " + pathParts),
            new("body_count_grew", bodiesAfter > bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
            new("path_length_read", pathLength is not null, "длина траектории: " + Num(pathLength) + " мм"),
        };

        var unverified = new List<string>();

        // Признак «материал добавлен» отделён от численного совпадения: кинематическая операция
        // базового типа обязана УВЕЛИЧИТЬ объём, и это проверяется без всякого аналитического
        // ожидания. Иначе «операция не применилась» выглядело бы как «применилась, но ожидание
        // не задано».
        //
        // Измерено 20.09.2026: на ПЕРВОМ теле объём до операции не читается вовсе — ReadVolume()
        // отдаёт null, потому что главного тела ещё нет. Это не ноль и не «не выросло»: величины до
        // операции не существует. Прежняя редакция (volumeBefore is double before) объявляла на этом
        // ложный отказ «не прочитано → 31415.9265358978 мм³» на КАЖДОЙ операции, создающей первое
        // тело. Состояния различаются тем, ЧТО ИЗМЕРЕНО: тел 0 — материала до операции не было по
        // определению модели, и «вырос» означает «стало больше нуля»; тел больше нуля, а объём не
        // прочитан — величина НЕ ПРОЧИТАНА, и проверка обязана называться непрочитанной, а не ложной.
        if (volumeAfter is not double volumeAfterValue)
        {
            unverified.Add("material_added_not_measured — объём после операции не прочитан, поэтому " +
                           "«объём вырос» не проверено, а не опровергнуто");
        }
        else if (volumeBefore is double volumeBeforeValue)
        {
            checks.Add(new NamedCheck("material_added", volumeAfterValue > volumeBeforeValue,
                Observed: Num(volumeBefore) + " → " + Num(volumeAfter) + " мм³",
                Expected: "объём вырос"));
        }
        else if (bodiesBefore == 0)
        {
            checks.Add(new NamedCheck("material_added", volumeAfterValue > 0,
                Observed: "тел до операции не было (объёма до не существует) → " +
                          Num(volumeAfter) + " мм³",
                Expected: "объём вырос (материала не было — стало больше нуля)"));
        }
        else
        {
            unverified.Add("material_added_not_measured — объём до операции не прочитан при непустой " +
                           "модели (тел " + bodiesBefore + "), поэтому «объём вырос» не проверено, " +
                           "а не опровергнуто");
        }
        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var observed = volumeAfter;
            var matches = observed is { } value
                          && Math.Abs(value - expected) < VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(observed) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "геометрия кинематической операции не подтверждена числом");
        }

        unverified.Add("dependent_features_not_enumerated — сохранность зависимых признаков здесь не " +
                       "проверяется; для этого существует отдельная приёмочная строка");
        if (command.ShiftMode == SweepShiftMode.Orthogonal)
        {
            unverified.Add("orthogonal_mode_not_distinguishable_on_straight_path — на ПРЯМОЙ " +
                           "траектории параллельный и ортогональный режимы дают одно тело (измерено " +
                           "20.09.2026: на дуге R50/90° они различаются на 8966.047734774369 мм³). " +
                           "Проверять этот режим следует на дуге.");
        }

        return new SweepResult(
            ToDto(reference, entity.name),
            command.ShiftMode.ToString(),
            1,
            pathParts,
            pathLength,
            bodiesAfter,
            volumeAfter,
            part.GetMainBody() is ksBody mainBody ? ReadBodyBox(mainBody) : null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>());
    }

    /// <summary>
    /// Вызвать булев COM-член, не роняя чтение. <c>null</c> означает «вызов не состоялся» и
    /// отличается от <c>false</c> («вызов состоялся и вернул false»): смешивать их — значит выдавать
    /// отказ вызова за отказ продукта.
    /// </summary>
    private static bool? SafeBool(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Присоединить эскиз-траекторию к определению. Возвращает число присоединённых элементов:
    /// ноль означает, что маршрут не построился, и это отказ, а не «ноль траектории».
    /// </summary>
    private static int AttachPath(ksBaseEvolutionDefinition definition, ksEntity pathSketch)
    {
        object? holder;
        try
        {
            holder = definition.PathPartArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }

        if (holder is not ksEntityCollection collection)
        {
            return 0;
        }

        return SafeBool(() => collection.Add(pathSketch)) == true ? 1 : 0;
    }

    /// <summary>
    /// Числовое значение типа движения сечения — <c>ksEvolutionShiftSketchTypeEnum</c>, прочитанное
    /// со страницы официальной справки v24 <c>ksevolutionshiftsketchtypeenum.html</c>.
    /// </summary>
    private static short ShiftValue(SweepShiftMode mode) => mode switch
    {
        SweepShiftMode.Parallel => 0,   // ksEvShiftParallel
        SweepShiftMode.KeepAngle => 1,  // ksEvShiftKeepAngle
        SweepShiftMode.Orthogonal => 2, // ksEvShiftOrtogonal
        _ => throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            "Неизвестный тип движения сечения: " + mode,
            RetryPolicy.Never),
    };

    /// <summary>
    /// Правила «поле ↔ возможность», отвергающие вызов ДО обращения к COM. Цена ошибки здесь
    /// несимметрична: лишний отказ виден сразу, а принятое и проигнорированное число доживает до
    /// приёмки, выглядя как выполненная операция.
    /// </summary>
    private static void ValidateSweepCommand(SweepCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SketchRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Профиль не задан: кинематическая операция строится по замкнутому профилю.",
                RetryPolicy.Never);
        }

        if (string.IsNullOrWhiteSpace(command.PathRef))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Траектория не задана: без неё кинематическая операция не строится.",
                RetryPolicy.Never);
        }

        if (string.Equals(command.SketchRef, command.PathRef, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Профиль и траектория — один и тот же объект: тело развёртки не может быть своим же " +
                "направляющим, и такой вызов ядро отвергнет, но лучше отвергнуть его до COM.",
                RetryPolicy.Never);
        }
    }
}

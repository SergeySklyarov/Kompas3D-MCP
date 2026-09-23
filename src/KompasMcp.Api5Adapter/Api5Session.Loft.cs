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
/// Элемент по сечениям (docs/05 SM-05, очередь B5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут — API7, и это следует из состава обязательных строк, а не из удобства.</b>
/// Обязательная строка <c>SM-05.base.mode_couplings</c> требует <b>цепочек соответствия сечений</b>,
/// а в API5 их нет вовсе: ни <c>ksBaseLoftDefinition</c>, ни <c>ksBossLoftDefinition</c> не
/// объявляют ни <c>AddCoupling</c>, ни <c>Coupling</c>. В API7 они документированы —
/// <c>iloft_propers.html</c> перечисляет <c>Coupling</c> и <c>CouplingsCount</c>,
/// <c>iloft_addcoupling.html</c> описывает <c>AddCoupling()</c> → <c>ICoupling</c>. Измерено
/// (шаг B5.9): <c>AddCoupling()</c> вернул <c>KompasAPI7.CouplingClass</c>, <c>CouplingsCount = 1</c>.
/// Поэтому семейство ведётся одним маршрутом, на котором выразимы ВСЕ его обязательные строки.
/// </para>
/// <para>
/// <b>Фабрика документирована для приклеенного типа.</b> <c>ilofts_add.html</c>: «Допустимыми
/// значениями <c>LoftType</c> являются <c>o3d_bossLoft</c>, <c>o3d_cutLoft</c> для коллекции
/// операций <c>IModelContainer::Lofts</c>»; «после получения нового интерфейса нужно задать
/// параметры операции и вызвать метод <c>IModelObject::Update</c>». Сечения задаются свойством
/// <c>ILoft.Sketchs</c> типа <c>VARIANT</c> — «массив <c>SAFEARRAY</c> объектов <c>LPDISPATCH</c>»
/// (<c>iloft_sketchs.html</c>). Измерено: присваивание массива дало чтение <c>System.Object[]</c>
/// из 2 элементов, <c>Update() = True</c>, объём <c>28000</c> — тот же эталон, что у API5-маршрута
/// <c>NewEntity(30)</c> (шаг B5.4).
/// </para>
/// <para>
/// <b>Что здесь НЕ утверждается.</b> Порядок сечений объёмом не доказывается: концентрические
/// параллельные сечения дают <c>28000</c> в любом порядке, поэтому «порядок соблюдён» требует
/// различающей постановки и здесь не выдаётся за проверенное. Параллельность плоскостей сечений —
/// обязанность проверяющей стороны (§6.3 п. 7 наряда); в этой редакции она НЕ проверяется и названа
/// открытым аспектом, а не замолчана. Содержимое цепочек соответствия (какие точки сечений
/// сопоставлены) не задаётся: измерено существование цепочки, а не её настройка.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Приклеенный элемент по сечениям — <c>o3d_bossLoft</c>.</summary>
    private const int BossLoft = 31;

    /// <summary>Наибольшее число цепочек соответствия в одном вызове.</summary>
    private const int MaxLoftCouplings = 64;

    /// <summary>
    /// Допуск сверки смещения точки вдоль контура, мм. Взят из класса допусков профиля для ДЛИН
    /// (0,01 мм), а не подобран после неудачи: измеренная сходимость round-trip'а на шаге B5.17 —
    /// записано 5 мм, прочитано 5 мм, то есть совпадение точное.
    /// </summary>
    private const double CouplingOffsetToleranceMm = 0.01d;

    /// <summary>Элемент по сечениям: тело по упорядоченному набору сечений (SM-05).</summary>
    public LoftResult Loft(LoftCommand command)
    {
        ValidateLoftCommand(command);

        var document = RequireDocument(command.DocumentId);

        // Сечения обязаны принадлежать ТОЙ ЖЕ детали и одной ревизии: ссылка из чужого документа
        // дала бы либо отказ ядра, либо молчаливо чужую геометрию.
        var sections = new List<ksEntity>(command.SectionRefs.Count);
        var targets = new List<SketchTarget>(command.SectionRefs.Count);
        foreach (var sectionRef in command.SectionRefs)
        {
            var target = RequireSketch(sectionRef);
            if (!string.Equals(target.Document.Id, document.Id, StringComparison.Ordinal))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Сечение '{sectionRef}' принадлежит документу {target.Document.Id}, а элемент " +
                    $"по сечениям строится в {document.Id}. Сечения из чужой детали не переносятся.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["section_document_id"] = target.Document.Id,
                        ["loft_document_id"] = document.Id,
                    });
            }

            targets.Add(target);
            sections.Add(target.Sketch);
        }

        // ── Параллельность плоскостей сечений — ОБЯЗАННОСТЬ ВЫЗЫВАЮЩЕЙ СТОРОНЫ, и наряд B5 §9.2
        // требует на непараллельные плоскости ИМЕНОВАННЫЙ отказ. Сам ILoft её не требует и не
        // запрещает: измерено (шаг B5.11), что на непараллельных плоскостях он либо отказывает
        // безлико, либо строит тело, описывающее не то, что просили. Отказ выносится ТОЛЬКО на
        // измеренном расхождении осей нормалей: нечитаемая плоскость отказа не даёт, а называется
        // непрочитанной — молчаливое «наверное, параллельны» было бы утверждением без измерения.
        var planeAxes = ReadSectionPlaneAxes(targets);
        if (planeAxes.DistinctAxes.Count > 1)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Плоскости сечений не параллельны: оси нормалей " +
                string.Join(", ", planeAxes.DistinctAxes.Select(AxisName)) +
                ". Элемент по сечениям соединяет сечения, лежащие в параллельных плоскостях; " +
                "сечения на пересекающихся плоскостях описывали бы другое тело. Признак не создавался.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "non_parallel_section_planes",
                    ["axes"] = planeAxes.DistinctAxes.Select(AxisName).ToArray(),
                    ["sections_read"] = planeAxes.Read.Count,
                    ["sections_unreadable"] = planeAxes.Unreadable,
                });
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Цепочки соответствия сечений живут только в API7 (в API5 их нет вовсе), поэтому " +
                "семейство ведётся этим маршрутом; признак не создавался.",
                RetryPolicy.ReacquireContext);
        }

        var volumeBefore = ReadVolume(document);
        var bodiesBefore = CountBodies(document);
        var facesBefore = CountFaces(document);

        ILoft? loft = null;
        string? creationFailure = null;
        try
        {
            if (container.Lofts is not ILofts collection)
            {
                creationFailure = "IModelContainer.Lofts не привёлся к ILofts";
            }
            else
            {
                loft = collection.Add((ksObj3dTypeEnum)BossLoft);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            creationFailure = ex.GetType().Name + ": " + ex.Message;
        }

        if (loft is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ILofts.Add(o3d_bossLoft) не дал ILoft: " + (creationFailure ?? "причина не сообщена") +
                ". Признак не создавался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["api7_failure"] = creationFailure });
        }

        // Сечения переносятся в API7: Sketchs принимает SAFEARRAY указателей IDispatch, и
        // непереданный объект — это значение, которого API7 не увидит. Тот же приём, что измерен на
        // IChamfer.BaseObjects (transfer + присваивание object[]).
        var transferred = new List<object>(sections.Count);
        foreach (var section in sections)
        {
            if (bridge.TransferTo7(section) is IModelObject modelObject)
            {
                transferred.Add(modelObject);
            }
        }

        if (transferred.Count != sections.Count)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "В API7 перенесено " + transferred.Count + " сечений из " + sections.Count +
                ": непереданное сечение API7 не увидит, и строить тело по неполному набору нельзя. " +
                "Признак не создавался.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["transferred"] = transferred.Count,
                    ["requested"] = sections.Count,
                });
        }

        try
        {
            loft.Sketchs = transferred.ToArray();
            loft.Closed = command.Closed;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись сечений в ILoft.Sketchs не состоялась: " + ex.GetType().Name + ": " +
                ex.Message + ". Признак не построен.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        // ── Цепочки соответствия сечений: задаются ДО первого Update() ──
        // Порядок документирован фабрикой («задать параметры операции и вызвать IModelObject::Update»,
        // ilofts_add.html) и измерен шагом B5.18: цепочка, заданная до первого Update(), применяется
        // в одном построении (CouplingsCount = 1, объём 20000 при смещении 20 мм из 80 — то же
        // значение, что и у цепочки, добавленной после построения).
        var chainFailures = new List<string>();
        var chainsWritten = AttachCouplings(loft, command.Couplings, chainFailures);
        if (chainFailures.Count > 0)
        {
            // Запрошенное соответствие — часть запроса, а не украшение: построенное тело с другим
            // соответствием было бы другим телом. Поэтому отказ приходит ДО построения, а не после.
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Цепочки соответствия сечений не заданы: " + string.Join("; ", chainFailures) +
                ". Тело по неполному соответствию было бы ДРУГИМ телом, поэтому построение не " +
                "выполнялось; запрошено цепочек " + command.Couplings.Count + ", задано " +
                chainsWritten + ".",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["named_code"] = "GEOMETRY_FAILED",
                    ["chains_requested"] = command.Couplings.Count,
                    ["chains_written"] = chainsWritten,
                    ["chain_failures"] = chainFailures,
                });
        }

        var updated = SafeBool(loft.Update);
        if (updated != true)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ILoft.Update() вернул " + (updated is null ? "ошибку вызова" : "false") +
                ": тело по сечениям не построено. Одно сечение, разомкнутое сечение там, где " +
                "требуется замкнутое, или несовместимые контуры дают именно этот исход.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["named_code"] = "GEOMETRY_FAILED" });
        }

        Api7Bridge.Rebuild(container, document.Document);

        var reference = References.Register("feature", document.Id, document.Revision, loft);
        BumpRevision(document, "loft.create");

        var volumeAfter = ReadVolume(document);
        var bodiesAfter = CountBodies(document);
        var facesAfter = CountFaces(document);

        // Цепочки читаются ИЗ МОДЕЛИ и целиком: сколько их, сколько сечений в каждой и какие смещения
        // стоят на каждом сечении. Число цепочек без содержимого доказывало бы только существование
        // объекта, тогда как обязательная строка требует ОПРЕДЕЛЁННОГО соответствия сечений.
        var couplingsInModel = ReadCouplingContent(loft);

        var checks = new List<NamedCheck>
        {
            new("operation_created", updated == true, "ILoft.Update() вернул true"),
            new("sections_transferred", transferred.Count == sections.Count,
                "перенесено сечений: " + transferred.Count + " из " + sections.Count),
            new("body_count_grew", bodiesAfter > bodiesBefore,
                "тел " + bodiesBefore + " → " + bodiesAfter),
        };

        if (command.Couplings.Count > 0)
        {
            var chainsInModel = couplingsInModel?.Count;
            checks.Add(new NamedCheck("coupling_chains_created", chainsInModel == command.Couplings.Count,
                Observed: "цепочек в модели: " + (chainsInModel?.ToString(CultureInfo.InvariantCulture)
                                                  ?? "не прочитано"),
                Expected: "запрошено " + command.Couplings.Count.ToString(CultureInfo.InvariantCulture)));

            var offsets = CouplingOffsetsMatch(couplingsInModel, command.Couplings);
            checks.Add(new NamedCheck("coupling_points_read_back", offsets.Ok,
                Observed: offsets.Observed,
                Expected: offsets.Expected));
        }

        // Сечения читаются ОБРАТНО из модели: сколько их принял признак. Это проверка того, что
        // набор дошёл целиком, а не того, что порядок соблюдён.
        var sectionsInModel = ReadSectionCount(loft);
        checks.Add(new NamedCheck("sections_read_back", sectionsInModel == sections.Count,
            Observed: sectionsInModel?.ToString() ?? "не прочитано",
            Expected: sections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var unverified = new List<string>
        {
            "section_order_not_distinguishable_by_volume — концентрические параллельные сечения дают " +
            "28000 в любом порядке (измерено), поэтому «порядок соблюдён» требует различающей " +
            "постановки (разная форма или поворот сечений, габарит, число граней) и здесь не " +
            "подтверждён объёмом",
            "section_planes_parallelism_proved_only_for_readable_planes — параллельность плоскостей " +
            "сечений проверяется по оси нормали, прочитанной с определения эскиза, и расхождение " +
            "даёт именованный отказ non_parallel_section_planes ДО создания признака. Число " +
            "нечитаемых плоскостей этого вызова: " + planeAxes.Unreadable +
            "; для них параллельность НЕ доказана и отказа не даёт — нечитаемая плоскость " +
            "называется непрочитанной, а не «наверное, параллельной»",
            "coupling_point_frame_not_published — наружу выходит смещение вдоль контура " +
            "(ICoupling.PositionOffset), а не координаты точки: измерено (шаг B5.17), что " +
            "ICoupling.SetPoint проецирует поданную точку на контур и читается обратно в ЛОКАЛЬНЫХ " +
            "координатах эскиза сечения — подано (10; 10; 30) (центр квадрата 20×20), прочитано " +
            "(20; 10; 30). Публиковать параметр с несовпадающими прямой и обратной половинами " +
            "значило бы обещать round-trip, которого нет",
            "coupling_effect_not_explained_analytically — то, что содержимое цепочки применяется, " +
            "измерено числом (смещение точки второго сечения на 25 % контура: 28000 → 20000, " +
            "разность 8000 мм³), но аналитического ожидания для тела со сдвинутым соответствием " +
            "наряд не даёт, поэтому эталоном служит совпадение с измеренным 20000, а не формула",
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };

        if (command.Couplings.Count == 0)
        {
            // Молчание — тоже утверждение: вызов без цепочек не подтверждает «соответствие
            // определено», и это названо, а не оставлено пустым местом.
            unverified.Add("couplings_not_requested — цепочки соответствия в этом вызове не задавались, " +
                "поэтому этим вызовом подтверждается существование признака и его геометрия, но НЕ " +
                "определённое соответствие сечений");
        }

        var geometryConfirmed = false;
        if (command.ExpectedVolumeMm3 is { } expected)
        {
            var observed = volumeAfter;
            var matches = observed is { } value
                          && Math.Abs(value - expected) <= VolumeToleranceMm3(expected);
            checks.Add(new NamedCheck("expected_volume", matches,
                "объём " + Num(observed) + " мм³", "ожидание " + Num(expected) + " мм³"));
            geometryConfirmed = matches;
        }
        else
        {
            unverified.Add("expected_volume_not_supplied — без аналитического ожидания объёма " +
                           "геометрия элемента по сечениям не подтверждена числом");
        }

        return new LoftResult(
            ToDto(reference, loft.Name),
            command.Building.ToString(),
            command.Closed,
            sections.Count,
            couplingsInModel?.Count,
            couplingsInModel,
            bodiesAfter,
            volumeAfter,
            document.PartNow().GetMainBody() is ksBody mainBody ? ReadBodyBox(mainBody) : null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            new List<string>
            {
                "маршрут API7: ILofts.Add(o3d_bossLoft) → ILoft.Sketchs (SAFEARRAY) → цепочки " +
                "соответствия → Update(); лишних граней: " + facesAfter,
                "цепочки соответствия: запрошено " + command.Couplings.Count + ", задано " +
                chainsWritten + ", прочитано из модели " +
                (couplingsInModel?.Count.ToString(CultureInfo.InvariantCulture) ?? "не прочитано") +
                " (смещения в мм вдоль контуров сечений)",
            });
    }

    /// <summary>Результат чтения осей нормалей плоскостей сечений.</summary>
    private sealed record SectionPlaneAxes(
        IReadOnlyList<int> Read, IReadOnlyList<int> DistinctAxes, int Unreadable);

    /// <summary>
    /// Оси нормалей плоскостей сечений, прочитанные с определений ЭСКИЗОВ. Ось — не знак: она
    /// отвечает на вопрос «параллельна ли плоскость XOY / XOZ / YOZ», а не «куда смотрит нормаль».
    /// Для параллельности этого достаточно и не требуется.
    /// </summary>
    private static SectionPlaneAxes ReadSectionPlaneAxes(IReadOnlyList<SketchTarget> targets)
    {
        var read = new List<int>(targets.Count);
        var unreadable = 0;
        foreach (var target in targets)
        {
            int? axis;
            try
            {
                axis = target.Definition.GetPlane() is ksEntity plane ? PlaneNormalAxis(plane) : null;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                axis = null;
            }

            if (axis is int value)
            {
                read.Add(value);
            }
            else
            {
                unreadable++;
            }
        }

        return new SectionPlaneAxes(read, read.Distinct().ToList(), unreadable);
    }

    /// <summary>Имя плоскости по оси её нормали — для сообщения отказа, а не для вывода.</summary>
    private static string AxisName(int axis) => axis switch
    {
        0 => "YOZ (нормаль X)",
        1 => "XOZ (нормаль Y)",
        2 => "XOY (нормаль Z)",
        _ => "ось " + axis.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Правила «поле ↔ возможность» для цепочек соответствия, общие для создания и правки: число
    /// точек в цепочке равно числу сечений, значения конечны, цепочек не больше
    /// <see cref="MaxLoftCouplings"/>. Отвергается ДО обращения к COM.
    /// </summary>
    private static void ValidateLoftCouplings(IReadOnlyList<LoftCoupling> chains, int sectionCount)
    {
        if (chains.Count > MaxLoftCouplings)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Цепочек соответствия " + chains.Count + ", а принимается не более " +
                MaxLoftCouplings + ".",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["couplings"] = chains.Count });
        }

        for (var i = 0; i < chains.Count; i++)
        {
            var chain = chains[i];
            if (chain.OffsetsMm.Count != sectionCount)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    "В цепочке соответствия " + i + " смещений " + chain.OffsetsMm.Count + ", а сечений " +
                    sectionCount + ": цепочка задаёт по точке на КАЖДОЕ сечение, и неполная цепочка "
                    + "описывала бы другое соответствие.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["chain"] = i,
                        ["offsets"] = chain.OffsetsMm.Count,
                        ["sections"] = sectionCount,
                    });
            }

            for (var j = 0; j < chain.OffsetsMm.Count; j++)
            {
                var offset = chain.OffsetsMm[j];
                if (double.IsNaN(offset) || double.IsInfinity(offset))
                {
                    throw new KompasContractException(
                        ErrorCodes.InvalidArgument,
                        "Смещение " + j + " в цепочке " + i + " равно '" + offset +
                        "': нечисловое или бесконечное значение не описывает положение на контуре.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?> { ["chain"] = i, ["point"] = j });
                }
            }
        }
    }

    /// <summary>
    /// Полная замена цепочек на существующем признаке: <c>ClearCouplings()</c>, затем по цепочке на
    /// каждую запрошенную. Возвращает <c>false</c> и причину, если замена не состоялась целиком —
    /// «часть цепочек» это другое соответствие, а не половина успеха.
    /// </summary>
    private static bool WriteLoftCouplings(
        ILoft loft, IReadOnlyList<LoftCoupling> chains, out string failure)
    {
        failure = string.Empty;

        var existing = SafeInt(() => loft.CouplingsCount);
        if (existing is > 0 && SafeBool(loft.ClearCouplings) != true)
        {
            failure = "ClearCouplings() не убрал прежние цепочки (" + existing + ")";
            return false;
        }

        var failures = new List<string>();
        var written = AttachCouplings(loft, chains, failures);
        if (failures.Count > 0)
        {
            failure = string.Join("; ", failures);
            return false;
        }

        if (written != chains.Count)
        {
            failure = "задано цепочек " + written + " из " + chains.Count;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Задать цепочки соответствия сечений: <c>ILoft.AddCoupling()</c> → <c>ICoupling</c>, затем
    /// <c>ICoupling.PositionOffset(Index)</c> на каждое сечение в порядке сечений.
    /// </summary>
    /// <remarks>
    /// Возвращается число ПОЛНОСТЬЮ заданных цепочек, а причины отказов складываются в
    /// <paramref name="failures"/>: частично заданная цепочка — это другое соответствие, и молча
    /// считать её успехом значило бы выдать чужое тело за запрошенное.
    /// </remarks>
    private static int AttachCouplings(
        ILoft loft, IReadOnlyList<LoftCoupling> chains, List<string> failures)
    {
        var written = 0;
        for (var i = 0; i < chains.Count; i++)
        {
            var chain = chains[i];
            ICoupling? coupling;
            try
            {
                coupling = loft.AddCoupling();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                failures.Add("цепочка " + i + ": AddCoupling отказал — " + ex.GetType().Name + ": " +
                             ex.Message);
                continue;
            }

            if (coupling is null)
            {
                failures.Add("цепочка " + i + ": AddCoupling не вернул ICoupling");
                continue;
            }

            var placed = 0;
            for (var section = 0; section < chain.OffsetsMm.Count; section++)
            {
                var index = section;
                var offset = chain.OffsetsMm[section];
                if (SafeBool(() =>
                    {
                        coupling.PositionOffset[index] = offset;
                        return true;
                    }) != true)
                {
                    failures.Add("цепочка " + i + ", сечение " + index + ": PositionOffset = " +
                                 Num(offset) + " мм не принят");
                    continue;
                }

                placed++;
            }

            if (placed == chain.OffsetsMm.Count)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Цепочки соответствия, прочитанные <b>ИЗ МОДЕЛИ</b>: <c>CouplingsCount</c>, затем по каждой
    /// <c>Coupling(Index)</c> → <c>ICoupling</c> → <c>Count</c> и <c>PositionOffset(Index)</c>.
    /// <c>null</c> — «не прочитано» и отличается от пустого списка.
    /// </summary>
    private static IReadOnlyList<LoftCouplingDto>? ReadCouplingContent(ILoft loft)
    {
        int? total;
        try
        {
            total = loft.CouplingsCount;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }

        if (total is not { } chains || chains < 0)
        {
            return null;
        }

        var result = new List<LoftCouplingDto>(chains);
        for (var i = 0; i < chains; i++)
        {
            var chainIndex = i;
            ICoupling? coupling;
            try
            {
                coupling = loft.Coupling[chainIndex];
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                result.Add(new LoftCouplingDto(null, Array.Empty<double?>()));
                continue;
            }

            int? inChain;
            try
            {
                inChain = coupling.Count;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                inChain = null;
            }

            var offsets = new List<double?>();
            for (var j = 0; j < (inChain ?? 0); j++)
            {
                var section = j;
                offsets.Add(SafeDouble(() => coupling.PositionOffset[section]));
            }

            result.Add(new LoftCouplingDto(inChain, offsets));
        }

        return result;
    }

    /// <summary>
    /// Сверка «запрошено ↔ прочитано ИЗ МОДЕЛИ» по всем цепочкам и всем их точкам. Допуск —
    /// длинный (0,01 мм, как для длин в профиле выпуска); числа цепочек и сечений сверяются точно.
    /// </summary>
    private static (bool Ok, string Observed, string Expected) CouplingOffsetsMatch(
        IReadOnlyList<LoftCouplingDto>? model, IReadOnlyList<LoftCoupling> requested)
    {
        var expected = DescribeRequestedCouplings(requested);
        if (model is null)
        {
            return (false, "не прочитано", expected);
        }

        var ok = model.Count == requested.Count;
        var observed = new List<string>();
        for (var i = 0; i < model.Count; i++)
        {
            var chain = model[i];
            if (i >= requested.Count)
            {
                observed.Add("цепочка " + i + ": лишняя");
                continue;
            }

            var want = requested[i].OffsetsMm;
            if (chain.SectionCount != want.Count)
            {
                ok = false;
            }

            var parts = new List<string>();
            for (var j = 0; j < want.Count; j++)
            {
                var got = j < chain.OffsetsMm.Count ? chain.OffsetsMm[j] : null;
                parts.Add(Num(got));
                if (got is not { } value || Math.Abs(value - want[j]) > CouplingOffsetToleranceMm)
                {
                    ok = false;
                }
            }

            observed.Add("цепочка " + i + " (сечений в модели " +
                         (chain.SectionCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано") +
                         "): " + string.Join(" / ", parts) + " мм");
        }

        return (ok, string.Join("; ", observed), expected);
    }

    /// <summary>Запрошенные цепочки одной строкой — вторая половина сверки.</summary>
    private static string DescribeRequestedCouplings(IReadOnlyList<LoftCoupling> requested) =>
        string.Join("; ", requested.Select((chain, index) =>
            "цепочка " + index + " (сечений " + chain.OffsetsMm.Count.ToString(CultureInfo.InvariantCulture) +
            "): " + string.Join(" / ", chain.OffsetsMm.Select(offset => Num(offset))) + " мм"));

    /// <summary>
    /// Число сечений, принятых признаком, — чтение <b>ИЗ МОДЕЛИ</b>, а не пересказ запроса. Массив
    /// приходит как <c>SAFEARRAY</c> объектов; <c>null</c> означает «не прочитано» и отличается от
    /// нуля.
    /// </summary>
    private static int? ReadSectionCount(ILoft loft)
    {
        try
        {
            return loft.Sketchs is Array array ? array.Length : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Чтение целого без падения: <c>null</c> — «не прочитано», а не ноль.</summary>
    private static int? SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Правила «поле ↔ возможность», отвергающие вызов ДО обращения к COM.
    /// </summary>
    private static void ValidateLoftCommand(LoftCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DocumentId))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Документ не задан: у набора сечений нет одного «опорного» объекта, как у профиля.",
                RetryPolicy.Never);
        }

        if (command.SectionRefs.Count < 2)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Сечений " + command.SectionRefs.Count + ", а по одному сечению тело не строится: " +
                "элемент по сечениям соединяет не менее двух.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["sections"] = command.SectionRefs.Count });
        }

        if (command.SectionRefs.Any(string.IsNullOrWhiteSpace))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Среди сечений есть пустая ссылка: порядок массива — это порядок соединения, и " +
                "пустое место в нём меняет тело.",
                RetryPolicy.Never);
        }

        if (command.SectionRefs.Count != command.SectionRefs.Distinct(StringComparer.Ordinal).Count())
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Одно и то же сечение указано дважды: тело по двум одинаковым сечениям вырождается, " +
                "и «сколько сечений задано» стало бы неотличимо от «сколько раз его назвали».",
                RetryPolicy.Never);
        }

        // Цепочки соответствия: число точек в цепочке обязано совпасть с числом сечений. Это не
        // придирка: PositionOffset(Index) адресуется «индексом сечения в цепочке»
        // (icoupling_positionoffset.html), и цепочка короче набора сечений задаёт соответствие не для
        // всех сечений — то есть другое тело, чем запрошено.
        ValidateLoftCouplings(command.Couplings, command.SectionRefs.Count);

        if (command.Building != LoftBuilding.Auto)
        {
            // Способ построения у крайних сечений выражается членом ILoft.BuildingType(BeginSection).
            // Измерено значение ТОЛЬКО авто-режима: у только что созданного признака и для начала, и
            // для конца прочитан 0 (ksLoftAuto). Значения 1/2/3 (по нормали, по объекту, купол) на
            // этом маршруте НЕ измерялись, поэтому они не принимаются молча — иначе «принято и
            // проигнорировано» дожило бы до приёмки, выглядя как выполненный режим.
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Способ построения у крайних сечений '" + command.Building + "' на этом маршруте не " +
                "измерялся: измерен только Auto (ILoft.BuildingType(BeginSection) = 0 = ksLoftAuto, " +
                "шаг B5.9). Режимы «по нормали», «по объекту» и «купол» требуют своего измерения " +
                "прежде, чем приниматься.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["building"] = command.Building.ToString() });
        }
    }
}

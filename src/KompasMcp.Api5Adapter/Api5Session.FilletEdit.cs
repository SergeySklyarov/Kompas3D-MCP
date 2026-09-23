using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.References;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Правка радиуса существующего скругления (docs/05 SM-09, режим <c>edit</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему маршрут API7, а не API5.</b> У <c>ksFilletDefinition</c> радиус объявлен и читается
/// (<c>get_radius</c>/<c>set_radius</c>, проверено по метаданным P0.2), поэтому первая версия этого
/// маршрута писала именно в него — и не применилась. Измерено 16.09.2026 (строка FL04r, документ
/// пластины 100×80×10 со скруглением R3 по четырём вертикальным рёбрам):
/// <c>definition.radius = 5</c> вернуло управление без ошибки, <c>entity.Update()</c> вернул true,
/// <c>RebuildDocument()</c> прошёл, определение перечитало радиус 5 — а объём остался прежним
/// 79922.74333882307, то есть геометрия не изменилась. Сеттер принял значение, признак его читает,
/// и модель его игнорирует: это ровно тот случай, который v1 обязан называть отказом, а не
/// успехом. Поэтому радиус пишется в <c>IFillet.Radius1</c> на живой модели — как угол фаски
/// (см. <see cref="Api5Session.UpdateChamferByAngle"/>).
/// </para>
/// <para>
/// <b>Сопоставление признака.</b> Имя в API5 и API7 не совпадает (F.8: «f-ch2» → «Фаска:1»),
/// поэтому признак API5 отыскивается в <c>IModelContainer.Fillets</c> по СОВПАДЕНИЮ РАДИУСА —
/// единственного скаляра, который обе стороны описывают одинаково. Неоднозначное совпадение
/// (ни одного либо несколько) — отказ <c>CAPABILITY_UNAVAILABLE</c> до мутации: записать радиус в
/// чужое скругление означает молча испортить чужую геометрию.
/// </para>
/// </remarks>
public partial class Api5Session
{
    /// <summary>Имя семейства скругления в ответах сервера.</summary>
    private const string FilletFamily = "fillet";

    /// <summary>
    /// Вид ссылки на СОБСТВЕННЫЙ вход признака. Держится здесь, а не берётся из реестра напрямую,
    /// чтобы строковая форма ссылки была объявлена рядом с тем, кто её разбирает: расхождение
    /// «минтим одно, читаем другое» иначе не поймать ни компилятором, ни приёмкой.
    /// </summary>
    private const string InputReferenceKind = ReferenceRegistry.InputKind;

    /// <summary>
    /// Что сервер видит по скруглению: радиус и способ читаются из API7, если к тому же документу
    /// строится мост и сопоставление однозначно. Пустое поле означает «не прочитано», а не «ноль».
    /// </summary>
    private FilletDto? ReadFillet(DocumentEntry document, object definition)
    {
        if (definition is not ksFilletDefinition fillet)
        {
            return null;
        }

        // Радиус определения API5 читается всегда — он и есть запасной ключ сопоставления.
        var api5Radius = fillet.radius;
        var api7 = ReadFilletRadius(document, api5Radius, fillet);
        return new FilletDto(
            RadiusMm: api7?.RadiusMm ?? api5Radius,
            Radius2Mm: api7?.Radius2Mm,
            Tangent: api7?.Tangent ?? fillet.tangent,
            BuildingType: api7?.BuildingType,
            BaseObjectCount: api7?.BaseObjectCount,
            BaseObjectReferences: api7?.BaseObjectReferences,
            BaseObjectInputRefs: MintInputReferences(document, api7));
    }

    /// <summary>
    /// Издать реестровые ссылки <c>input:&lt;hex&gt;</c> на собственные входы признака — по одной на
    /// элемент <c>BaseObjects</c>, в том же порядке, что <c>BaseObjectReferences</c>.
    /// </summary>
    /// <remarks>
    /// Зачем это здесь, а не в правке набора. Собственный вход признака — не ребро тела: строки
    /// реестра <c>edge:</c> у него нет и быть не может, потому что реестр выдаёт их рёбрам ТЕЛА.
    /// Числа <c>IModelObject.Reference</c> в контракт правки не подставляются по типу. Значит
    /// строку для входа должен издать СЕРВЕР, и делает он это в том единственном месте, где числа
    /// входа вообще становятся известны наружу, — при чтении признака. Тот же принцип, что и у
    /// <c>edge:</c>-ссылок: клиент не сочиняет идентификаторы, а подставляет выданные.
    /// <para>
    /// Привязка к РЕВИЗИИ документа (а не к состоянию признака) — намеренная и та же, что у прочих
    /// ссылок: после мутации ссылки прошлой ревизии отсекаются <c>Require</c>, и клиент обязан
    /// перечитать контекст. Иначе правку можно было бы предъявить по устаревшему составу.
    /// </para>
    /// <para>
    /// Когда ссылки не выдаются: мост API7 не построен, скруглений с тем же радиусом несколько
    /// (входы не прочитаны — <c>null</c>) или входов нет вовсе (пустой список). Порядок ссылок
    /// совпадает с порядком <c>BaseObjectReferences</c>, потому что оба строятся из одного обхода
    /// <c>BaseObjects</c>.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string>? MintInputReferences(DocumentEntry document, FilletReadDto? api7)
    {
        if (api7?.BaseObjectReferences is not { } numbers)
        {
            return null;
        }

        var minted = new List<string>(numbers.Count);
        foreach (var number in numbers)
        {
            // Идентификатор — САМ адрес входа, а не свежий uuid: смысл ссылки здесь в том, чтобы
            // донести число IModelObject.Reference до правки набора, и uuid этого не делал бы.
            var id = $"{InputReferenceKind}:{number:x}";
            References.RegisterDeterministic(
                id, InputReferenceKind, document.Id, document.Revision, payload: number);
            minted.Add(id);
        }

        return minted;
    }

    /// <summary>
    /// Радиус и способ из API7 — по СВОИМ ВХОДАМ признака, а радиус остаётся запасным путём.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему опознание по входам, а не по радиусу.</b> Радиус — не ключ: на модели законно
    /// живут два скругления одного радиуса, и опыт адресации приёмки (два R3, «изменение
    /// выбранного, сохранение второго») ставится именно на такую модель. Опознание по радиусу
    /// возвращало при этом <c>null</c> — и признак оставался без параметров API7, а вместе с ним
    /// без <c>base_object_input_refs</c>: клиент не получал ссылок ни на один из двух признаков и
    /// не мог адресовать ни один. Это не «нет данных», а отказ в обслуживании поддерживаемого
    /// сценария, поэтому источником ключа сделаны СВОИ ВХОДЫ признака — та же мера состава, по
    /// которой работает и маршрут записи (<c>FindIndexByInputReferences</c>).
    /// </para>
    /// <para>
    /// <b>Радиус остаётся запасным путём</b> и употребляется ровно тогда, когда состав прочитать
    /// не удалось (измеренное свойство API5 на существующем скруглении — определение входов не
    /// отдаёт). Тогда и только тогда вступает прежнее правило: однозначное совпадение по радиусу
    /// или <c>null</c>.
    /// </para>
    /// </remarks>
    private FilletReadDto? ReadFilletRadius(DocumentEntry document, double api5Radius, object? definition = null)
    {
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            return null;
        }

        // Основной путь: признак опознаётся по составу СВОИХ входов. Он различает два скругления
        // одного радиуса, чего радиус не может по построению.
        if (definition is ksFilletDefinition source
            && source.array() is ksEntityCollection array)
        {
            var ownRefs = new List<int>();
            for (var i = 0; i < array.GetCount(); i++)
            {
                if (array.GetByIndex(i) is ksEntity edge
                    && ReferenceOfApi7(bridge, edge) is int reference)
                {
                    ownRefs.Add(reference);
                }
            }

            if (ownRefs.Count > 0 && ownRefs.Count == array.GetCount())
            {
                var byInputs = Api7Fillet.FindIndexByInputReferences(container, ownRefs);
                if (byInputs is int index)
                {
                    return Api7Fillet.Read(container, index);
                }
            }
        }

        // Запасной путь — только когда состав прочитать не удалось. Неоднозначность по радиусу
        // означает «опознать нечем», и возвращается null, а не «первое попавшееся».
        var matches = Api7Fillet.FindIndexesByIdenticalRadius(container, api5Radius);
        return matches.Count == 1 ? Api7Fillet.Read(container, matches[0]) : null;
    }

    /// <summary>
    /// Правка радиуса скругления. Геометрия подтверждается измерением объёма: клиент обязан задать
    /// <c>expected_volume_mm3</c> (для четырёх угловых рёбер пластины 100×80×10 это
    /// 80000 − 4·(1−π/4)·r²·10), иначе уровень остаётся <c>call_returned</c>.
    /// </summary>
    private UpdateFeatureResult UpdateFilletRadius(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        var requested = command.RadiusMm;
        if (requested is null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Для скругления нужен radius_mm: других меняемых числовых параметров у него в v1 нет.",
                RetryPolicy.Never);
        }

        if (requested is <= 0d)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Радиус скругления обязан быть больше 0: ноль КОМПАС принимает и создаёт признак с " +
                "нулевыми гранями при неизменном объёме (измерено на фаске пробой F.12).",
                RetryPolicy.Never);
        }

        // Текущий радиус — из определения API5. Он нужен как КЛЮЧ СОПОСТАВЛЕНИЯ, а не как источник
        // истины: писать мы в него не будем (см. remark к классу).
        var currentSource = entity.GetDefinition() as ksFilletDefinition;
        var currentRadius = currentSource?.radius;
        if (currentRadius is not double radiusNow)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Определение скругления не перечитывает радиус: сопоставить признак API5 с IFillet " +
                "нечем, признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Радиус скругления правится только через IFillet, потому что запись в " +
                "ksFilletDefinition.radius на существующем признаке не применяется " +
                "(измерено строкой FL04r). Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var matches = Api7Fillet.FindIndexesByIdenticalRadius(container, radiusNow);
        if (matches.Count != 1)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                matches.Count == 0
                    ? "Скругление с радиусом " + radiusNow.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм не найдено в IModelContainer.Fillets: сопоставить признак API5 с IFillet " +
                      "нечем, а записать радиус в чужой признак нельзя. Признак не изменён."
                    : "Скруглению с радиусом " + radiusNow.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " мм отвечает " + matches.Count.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                      " признаков API7 — сопоставление неоднозначно, и записывать радиус «в первый " +
                      "попавшийся» означало бы изменить не тот признак. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["radius_mm"] = radiusNow,
                    ["api7_matches"] = matches.Count,
                });
        }

        var index = matches[0];
        var before = Api7Fillet.Read(container, index);

        var written = Api7Fillet.TryWriteRadius(container, index, requested, null, null);
        if (!written.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись в IFillet не подтверждена: " + (written.Failure ?? "причина не сообщена") +
                ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = written.Failure });
        }

        // Без перестроения запись в IFillet остаётся представлением: тот же порядок, что при
        // создании скругления и при правке угла фаски (F.10 + проба E).
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "fillet.update.radius");

        var after = Api7Fillet.Read(container, index);
        var volumeAfter = ReadVolume(document);
        var facesAfter = CountFaces(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        // Радиус перечитывается с определения API5 тоже: если API7 принял запись, а определение
        // отдаёт прежнее число, то признак API5 и живая модель разошлись, и это надо назвать.
        var definitionAfter = entity.GetDefinition() as ksFilletDefinition;
        var api5Radius = definitionAfter?.radius;

        var api7RadiusStored = after?.RadiusMm is double readRadius
            && Math.Abs(readRadius - requested.Value) <= 1e-6;
        var api5SeesChange = api5Radius is double r5 && Math.Abs(r5 - requested.Value) <= 1e-6;
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IFillet[{index}] Radius1={requested.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} (было {radiusNow.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)})",
                Expected: "запись принята и подтверждена перестроением"),
            new("radius_read_back", api7RadiusStored,
                Observed: $"IFillet.Radius1={after?.RadiusMm?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается"}",
                Expected: requested.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
            new("api5_sees_the_change", api5SeesChange,
                Observed: api5Radius?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не читается",
                Expected: requested.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
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
        if (!api5SeesChange)
        {
            unverified.Add(
                "api5_definition_stale — IFillet радиус принял, а определение API5 отдаёт прежнее " +
                "число: чтение через kompas_get_feature на этом признаке покажет устаревший радиус");
        }

        var geometryConfirmed = api7RadiusStored && sameFeature && volumeMatched;
        if (!geometryConfirmed)
        {
            unverified.Insert(0, api7RadiusStored
                ? "volume_not_as_expected — радиус записан и перечитан, но измерение объёма не совпало с ожиданием"
                : "radius_not_read_back — радиус не перечитался из IFillet");
        }

        if (command.ExpectedVolumeMm3 is null)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма правка не может быть " +
                "подтверждена геометрически");
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            FilletFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            // Глубины и условия конца у скругления нет: эти поля относятся к выдавливанию.
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            // Радиус, перечитанный из модели. Отдельным хвостовым параметром — как угол фаски.
            RadiusReadBackMm: after?.RadiusMm);
    }

    /// <summary>
    /// Правка НАБОРА рёбер скругления: какие рёбра остаются скруглёнными. Набор заменяется целиком
    /// одним присваиванием <c>IFillet.BaseObjects</c> — это протокол маршрута, а не удобство.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Маршрут — API7, и он измерен.</b> Проба H-2 (<c>docs/acceptance/api7/fillet-base-objects.md</c>,
    /// 14 PASS / 0 FAIL / 0 UNKNOWN, четыре прогона подряд, собственный <c>run_id</c>):
    /// </para>
    /// <code>
    /// IModelContainer.Fillets[i]        → IFillet
    /// IFillet.BaseObjects               → System.Object[] из IModelObject (чтение)
    /// IFillet.BaseObjects = IModelObject[] (запись, полная замена)
    /// IFillet.Update()                  (обязателен)
    /// </code>
    /// <para>
    /// Все объекты берутся с ЖИВОЙ модели — после <c>save → close → reopen</c>, — и ни один объект,
    /// захваченный при создании скругления, не используется. В этом и была ошибка пробы H.
    /// </para>
    /// <para>
    /// <b>ОТРИЦАТЕЛЬНЫЙ РЕЗУЛЬТАТ ПО МАРШРУТУ API5 ОСТАЁТСЯ ВЕРНЫМ.</b> Он измерен 16.09.2026 восемью
    /// пробами, и с появлением рабочего маршрута не отменяется: <c>ksFilletDefinition.array()</c>
    /// (<c>Clear()</c>, затем <c>Add()</c>) правку набора на существующем признаке НЕ даёт. После
    /// скругления углы заняты цилиндрическими гранями, и «угловых вертикальных» рёбер в топологии
    /// 0 из 4: (а) исходные угловые рёбра отзывает само создание скругления — предъявление даёт
    /// <c>STALE_REFERENCE</c> до всякой правки; (б) существующие вертикальные рёбра скруглённых углов
    /// признак НЕ УДЕРЖИВАЕТ — любой набор из них схлопывает определение, <c>edges_read_back = 0</c>,
    /// объём возвращается к пластине (проверено на 1, 2, 4 и 8 рёбрах). Ненулевой
    /// <c>edges_read_back</c> даёт только ВТОРОЙ вызов подряд, и это destroy-and-rebuild, а не правка
    /// набора. Поэтому прежний маршрут из этого метода удалён, а не оставлен веткой.
    /// </para>
    /// <para>
    /// <b>Сопоставление признака API5 с IFillet — НЕ по имени, НЕ по индексу и НЕ по радиусу.</b>
    /// Имя между API5 и API7 не совпадает (F.8: «f-ch2» → «Фаска:1»); индекс в коллекции произволен;
    /// радиус не различает два скругления одного радиуса, а опыт адресации (H2.7) как раз на такой
    /// модели и ставился. Признак опознаётся по СВОИМ ТЕКУЩИМ входам: <c>ksFilletDefinition.array()</c>
    /// даёт рёбра API5, они переносятся в API7 и сравниваются по устойчивому
    /// <c>IModelObject.Reference</c> с входами каждого <c>IFillet</c>. Совпадение множеств — признак
    /// найден; при нуле или нескольких совпадениях вызов отвергается ДО мутации, потому что записать
    /// набор в чужое скругление означает молча испортить чужую геометрию.
    /// </para>
    /// <para>
    /// <b>Опознание и ПРЕДЪЯВЛЕНИЕ — разные вопросы, и ключи у них разные.</b> Опознание признака
    /// делается по его собственным входам (выше). Предъявление нового набора делается объектами,
    /// добытыми переносом <c>ksAPI7Dual</c>, — так измерен решающий контроль H2.4: признаку было
    /// предъявлено ребро тела, которого среди его собственных входов ЗАВЕДОМО не было, и КОМПАС
    /// состав принял.
    /// </para>
    /// <para>
    /// <b>Ранее здесь стоял запрет, опиравшийся на догадку, — и он был снят измерением (17.09.2026).</b>
    /// Набор разрешалось только ОТБИРАТЬ из собственных входов признака по совпадению <c>Reference</c>,
    /// а несовпадающие рёбра отвергались. Обоснованием служило наблюдение H2.7, прочитанное как
    /// «ссылки входов признака и рёбра тела лежат в РАЗНЫХ контекстах и несопоставимы». Замер на
    /// <c>FL10x</c> (1→1) это опроверг: полосы СОСЕДНИЕ — перенесённое ребро
    /// <c>1073742309</c> против входа признака <c>1073742308</c>, тип у обоих <c>ksObjectEdge</c>,
    /// обе ссылки устойчивы при повторном переносе и повторном чтении, а адресные привязки разные.
    /// Двойственность API5/API7 (два COM-объекта про одно ребро) была принята за непроходимую границу.
    /// Проба H-2 такой сверки не делала никогда, поэтому запрет не был измерением. Запрет снят;
    /// подмножество собственных входов осталось ОТДЕЛЬНОЙ веткой — для него это строго измеренный
    /// путь H2.3/H2.5.
    /// </para>
    /// <para>
    /// <b>Границы, не переносимые на общий вывод:</b> расширение набора на эталоне 100×80×10 не
    /// измерено (у пластины ровно четыре вертикальных угла); измерены сокращение (4→3, 4→2) и замена
    /// при неизменном размере (1→1).
    /// </para>
    /// <para>
    /// <b>СОСТОЯНИЕ: маршрут РЕАЛИЗОВАН; подтверждение приёмкой продукта — по <c>FL10…FL10x</c>.</b>
    /// Правка 17.09.2026 сняла запрет на предъявление перенесённых объектов (см. выше). Прежнее
    /// состояние было PASS 34 · FAIL 4 (<c>FL10</c>, <c>FL10s</c>, <c>FL10b</c>, <c>FL10x</c> красные),
    /// и это записано здесь как история, а не как текущее утверждение: актуальный приговор —
    /// в <c>docs/acceptance/INDEX.md</c> и <c>docs/STATUS.md</c>. Не считать метод подтверждённым,
    /// пока <c>FL10</c> красная.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Правка набора: выбирает ВАЛЮТУ предъявления по тому, что прислал клиент.
    /// </summary>
    /// <remarks>
    /// Различие валют измерено, а не выбрано для удобства: сокращение набора выражается только
    /// собственными входами признака (H2.3 4→3, H2.5 4→2 — проба брала объекты ИЗ BaseObjects и не
    /// искала рёбер тела), а замена состава — рёбрами тела (FL10x 1→1, level=geometry_checked).
    /// Поэтому ветка не «предпочтительная», а обязательная: у каждой операции своя валюта, и
    /// смешение их в одном вызове неотличимо потом от «применилось одно из двух».
    /// </remarks>
    private UpdateFeatureResult UpdateFilletEdgeSet(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.BaseObjectRefs is not null && command.EdgeRefs is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs и base_object_refs вместе запрещены: это два разных состава одного набора. " +
                "В ответе «применилось одно из двух» было бы неотличимо от «применилось и то, и другое».",
                RetryPolicy.Never);
        }

        // Пустой набор ОТЛИЧАЕТСЯ от «не задан» и обязан отказывать своим ответом: иначе
        // base_object_refs=[] провалился бы в ветку edge_refs и клиент прочитал бы «ни edge_refs,
        // ни base_object_refs не задан», хотя он их задал. Отказ до мутации, как и требует контракт.
        if (command.BaseObjectRefs is { Count: 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "base_object_refs пуст: новый набор рёбер скругления пустым быть не может — это был " +
                "бы не набор, а удаление признака, и оно делается другим инструментом.",
                RetryPolicy.Never);
        }

        if (command.EdgeRefs is { Count: 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs пуст: новый набор рёбер скругления пустым быть не может — это был бы не " +
                "набор, а удаление признака, и оно делается другим инструментом.",
                RetryPolicy.Never);
        }

        if (command.BaseObjectRefs is { Count: > 0 } requestedInputs)
        {
            return UpdateFilletEdgeSetByOwnInputs(
                document, entity, command, requestedInputs, volumeBefore, featuresBefore, stateBefore);
        }

        if (command.EdgeRefs is not { Count: > 0 } edgeRefs)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Ни edge_refs, ни base_object_refs не задан: новый набор рёбер скругления пустым " +
                "быть не может — это был бы не набор, а удаление признака, и оно делается другим " +
                "инструментом.",
                RetryPolicy.Never);
        }

        return UpdateFilletEdgeSetByBodyEdges(
            document, entity, command, edgeRefs, volumeBefore, featuresBefore, stateBefore);
    }

    /// <summary>
    /// Правка набора по СОБСТВЕННЫМ входам признака: запрашиваются адреса входов, которые признак
    /// сам отдал, и пишется подмножество ЕГО ЖЕ объектов.
    /// </summary>
    /// <remarks>
    /// Это измеренно строгий путь сокращения (проба H-2, опыты H2.3 4→3 и H2.5 4→2): проба писала
    /// в <c>IFillet.BaseObjects</c> массив, собранный из прочитанных ею же элементов
    /// (<c>ElementAt(raw, i)</c>), <c>Clear()</c> НЕ вызывала и рёбер конечного тела НЕ искала.
    /// Её собственные слова: «Пишем подмножество из N объектов, прочитанных из BaseObjects».
    /// <para>
    /// Отличие от пути через <c>edge_refs</c> не в удобстве, а в предмете: там предъявляются рёбра
    /// ТЕЛА (годно для замены состава, негодно для сокращения — FL10 схлопывает признак), здесь —
    /// собственные входы. Это и есть та валюта, которой не хватало.
    /// </para>
    /// </remarks>
    private UpdateFeatureResult UpdateFilletEdgeSetByOwnInputs(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        IReadOnlyList<string> requestedInputRefs,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.RadiusMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "radius_mm и base_object_refs вместе запрещены: правка радиуса и правка набора — " +
                "разные предметы и разные подтверждения.",
                RetryPolicy.Never);
        }

        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Собственные входы признака правятся только через IFillet.BaseObjects. " +
                "Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var wanted = new List<int>(requestedInputRefs.Count);
        var unknown = new List<string>();
        foreach (var reference in requestedInputRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Kind != InputReferenceKind)
            {
                unknown.Add($"{reference} (kind={stored.Kind}, ожидался {InputReferenceKind})");
                continue;
            }

            if (stored.Payload is int number)
            {
                wanted.Add(number);
            }
            else
            {
                unknown.Add($"{reference} (payload не число)");
            }
        }

        if (unknown.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Не разобрано ссылок на собственные входы: " + string.Join(", ", unknown) +
                ". Возьмите base_object_input_refs из kompas_get_feature. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["unrecognised"] = unknown });
        }

        if (wanted.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "base_object_refs пуст: новый набор рёбер скругления пустым быть не может — это был " +
                "бы не набор, а удаление признака, и оно делается другим инструментом.",
                RetryPolicy.Never);
        }

        // Признак ищется по совпадению ЕГО входов с запрошенными, а не по радиусу: два скругления
        // одного радиуса на модели — обычное дело (измерено опытом H2.7). Совпадение должно быть
        // ровно у одного, иначе подмножество уйдёт в чужое скругление.
        var liveInputs = Api7Fillet.ReadBaseObjectsAll(container);
        var owner = -1;
        for (var i = 0; i < liveInputs.Count; i++)
        {
            // null означает «набор этого скругления не прочитался» — это не «входов нет» и не
            // кандидат на запись: пропустить его обязательно, иначе «не прочитано» станет
            // совпадением, и набор уйдёт в признак, которого мы не читали.
            if (liveInputs[i] is not { } candidateInputs)
            {
                continue;
            }

            var refs = Api7Fillet.ReferencesOf(candidateInputs);
            if (wanted.All(refs.Contains))
            {
                if (owner >= 0)
                {
                    owner = -2;
                    break;
                }

                owner = i;
            }
        }

        if (owner < 0)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                owner == -2
                    ? "Ссылки на собственные входы совпали более чем с одним скруглением: записать " +
                      "набор в чужой признак означало бы молча исправить чужую геометрию. " +
                      "Признак не изменён."
                    : "Ни одно скругление не удерживает указанные входы: состав изменился после " +
                      "чтения либо ссылки выпущены для другой ревизии. Перечитайте " +
                      "kompas_get_feature. Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        // Пишутся объекты, ПРОЧИТАННЫЕ У САМОГО ПРИЗНАКА, а не собранные заново: в этом весь
        // измеренный смысл пути. Объект, добытый иначе, признак не удерживает (измерено FL10).
        // Владелец уже проверен на «прочитан» при опознании, поэтому здесь разыменование безопасно,
        // но берётся оно один раз — и в сверке, и в записи участвует ОДИН И ТОТ ЖЕ список.
        var own = liveInputs[owner]!;
        var ownRefs = Api7Fillet.ReferencesOf(own);

        // ОБЯЗАТЕЛЬНАЯ СВЕРКА ВЛАДЕЛЬЦА С ЗАПРОШЕННЫМ ПРИЗНАКОМ. Опознание по составу входов
        // отвечает на вопрос «кто удерживает эти входы», а не «тот ли это признак, который просили».
        // Передача входов скругления B вместе с feature_ref скругления A прошла бы эту проверку
        // молча и исправила бы B, отчитавшись про A, — то есть выдала бы правку одного признака за
        // правку другого. Поэтому владелец обязан совпасть с запрошенным признаком, и отказ идёт
        // ДО мутации.
        //
        // Сверяются собственные входы ЗАПРОШЕННОГО признака (те, что он отдаёт в API5 через
        // определение) с входами найденного владельца: у скругления A и скругления B входы разные
        // по определению — иначе их не различить и на чтении. Перенос идёт тем же ksAPI7Dual, что и
        // при опознании.
        var requestedOwnInputs = new List<int>();
        if (entity.GetDefinition() is ksFilletDefinition requestedSource
            && requestedSource.array() is ksEntityCollection requestedArray)
        {
            for (var i = 0; i < requestedArray.GetCount(); i++)
            {
                if (requestedArray.GetByIndex(i) is ksEntity requestedEdge
                    && ReferenceOfApi7(bridge, requestedEdge) is int requestedRef)
                {
                    requestedOwnInputs.Add(requestedRef);
                }
            }
        }

        // Пустой список означает «определение входов не отдало» — это измеренное свойство API5 на
        // СУЩЕСТВУЮЩЕМ скруглении, а не доказательство чужого признака. Поэтому сверка делается
        // только когда входы прочитались: непрочитанное не должно превращаться в ложный отказ.
        if (requestedOwnInputs.Count > 0)
        {
            var requestedSet = requestedOwnInputs.OrderBy(x => x).ToArray();
            var ownerSet = ownRefs.OrderBy(x => x).ToArray();
            if (!requestedSet.SequenceEqual(ownerSet))
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Собственные входы найденного скругления не совпадают с входами признака, " +
                    "указанного в feature_ref: запись в найденный признак исправила бы ЧУЖУЮ " +
                    "геометрию, отчитавшись про запрошенную. Признак не изменён.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["feature_ref_inputs"] = requestedSet,
                        ["matched_owner_inputs"] = ownerSet,
                        ["matched_owner_index"] = owner,
                    });
            }
        }

        var kept = new List<IModelObject>(wanted.Count);
        for (var i = 0; i < own.Count; i++)
        {
            if (i < ownRefs.Count && wanted.Contains(ownRefs[i]))
            {
                kept.Add(own[i]);
            }
        }

        if (kept.Count != wanted.Count)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Собственных входов нашлось {kept.Count} из запрошенных {wanted.Count}: часть ссылок " +
                "не отвечает элементу BaseObjects. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["found"] = kept.Count,
                    ["requested"] = wanted.Count,
                    ["own_refs"] = ownRefs,
                });
        }

        var targetRefs = Api7Fillet.ReferencesOf(kept);
        var write = Api7Fillet.TryWriteBaseObjects(container, owner, kept);
        if (!write.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись набора в IFillet.BaseObjects не подтверждена: " +
                (write.Failure ?? "причина не сообщена") + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = write.Failure });
        }

        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "fillet.update.inputs");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        // Набор перечитывается С ЖИВОЙ МОДЕЛИ: запись без эффекта не должна выглядеть применённой.
        var after = Api7Fillet.ReadBaseObjects(container, owner);
        var afterRefs = after is null ? null : Api7Fillet.ReferencesOf(after);
        var inputsSet = afterRefs is not null && SameSet(afterRefs, targetRefs);
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var volumeMatched = false;
        if (command.ExpectedVolumeMm3 is double expected && volumeAfter is double measured)
        {
            volumeMatched = Math.Abs(measured - expected) <= ProfileArea.Tolerance(expected);
        }

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IFillet[{owner}].BaseObjects ← [{string.Join(", ", targetRefs)}] " +
                          $"(было [{string.Join(", ", ownRefs)}]); предъявлены СОБСТВЕННЫЕ входы " +
                          $"признака: {kept.Count} из {own.Count}; адреса записанных объектов: " +
                          string.Join(", ", kept.Select(AddressOf)),
                Expected: "запись принята и подтверждена перестроением"),
            new("edges_read_back", inputsSet,
                Observed: afterRefs is null ? "не читается" : $"[{string.Join(", ", afterRefs)}]",
                Expected: $"[{string.Join(", ", targetRefs)}]"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}»",
                Expected: $"признаков {featuresBefore}, имя «{stateBefore.Name}»"),
            new("volume_after_update", volumeMatched,
                Observed: volumeAfter?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                          ?? "не читается",
                Expected: command.ExpectedVolumeMm3?.ToString(
                              "0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не задано"),
        };

        var unverified = new List<string>
        {
            "dependent_features_not_enumerated — сохранность зависимых признаков здесь не проверяется",
        };
        if (!inputsSet)
        {
            unverified.Add("edges_not_read_back — набор не перечитался в ожидаемом составе");
        }
        else if (!volumeMatched)
        {
            unverified.Add(
                "volume_not_as_expected — набор записан и перечитан, но объём не совпал с ожиданием");
        }

        if (command.ExpectedVolumeMm3 is null)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма правка набора не " +
                "может быть подтверждена геометрически");
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            FilletFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                inputsSet && sameFeature && volumeMatched
                    ? VerificationLevel.GeometryChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified),
            RadiusReadBackMm: null,
            EdgesReadBack: afterRefs?.Count);
    }

    /// <summary>
    /// Правка набора по рёбрам ТЕЛА (<c>edge_refs</c>) — измеренно годная валюта ЗАМЕНЫ состава и
    /// негодная для СОКРАЩЕНИЯ.
    /// </summary>
    /// <remarks>
    /// Сохраняется отдельным путём, а не веткой общего: у него своя валюта, свой измеренный опыт
    /// (H2.4, 1→1) и своя граница (сокращение схлопывает признак — FL10). Слить оба пути значило бы
    /// снова потерять различие, из-за которого строки стоят раздельно.
    /// </remarks>
    private UpdateFeatureResult UpdateFilletEdgeSetByBodyEdges(
        DocumentEntry document,
        ksEntity entity,
        UpdateFeatureCommand command,
        IReadOnlyList<string> edgeRefs,
        double? volumeBefore,
        int featuresBefore,
        FeatureState stateBefore)
    {
        if (command.RadiusMm is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "radius_mm и edge_refs вместе запрещены: правка радиуса и правка набора — разные " +
                "предметы и разные подтверждения. Смешав их, ответ не отличит «применилось и то, и " +
                "другое» от «применилось одно из двух».",
                RetryPolicy.Never);
        }

        // Текущий набор — ДЛЯ СОПОСТАВЛЕНИЯ признака API5 с IFillet, а не для решения о мутации.
        // Разворачивается тем же путём, что и при создании: реестр ссылок + три маршрута
        // разворачивания ребра в entity (измерено P2.2). Второй копии этого разбора здесь не надо.
        //
        // ВАЖНО ПО ИСТОЧНИКУ. Определение API5 (ksFilletDefinition.array()) на СУЩЕСТВУЮЩЕМ
        // скруглении рёбер не отдаёт вовсе — измерено (0 из 4 после скругления, строка FL10,
        // отрицательный результат, сохранённый в gap), и это ровно то же свойство, из-за которого
        // маршрут правки лежит в API7, а не в API5. Опираться на него при ОПОЗНАНИИ значило бы
        // зависеть от того самого механизма, который признан неработающим. Поэтому основной
        // источник — собственные входы живого признака в API7 (IFillet.BaseObjects), а определение
        // API5 остаётся запасным путём.
        var bridge = BridgeFor(document);
        var container = bridge.ContainerFor(document.Document, document.Id, document.Revision);
        if (container is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Набор рёбер скругления правится только через IFillet.BaseObjects: маршрут через " +
                "определение API5 (Clear/Add) правку набора не даёт — измерено восемью пробами " +
                "16.09.2026 и подтверждено пробой H-2. Признак не изменён.",
                RetryPolicy.ReacquireContext);
        }

        var beforeSource = entity.GetDefinition() as ksFilletDefinition;
        var beforeArray = beforeSource?.array() as ksEntityCollection;
        var currentEdges = new List<ksEntity>();
        if (beforeArray is not null)
        {
            for (var i = 0; i < beforeArray.GetCount(); i++)
            {
                if (beforeArray.GetByIndex(i) is ksEntity current)
                {
                    currentEdges.Add(current);
                }
            }
        }

        // Ссылки ТЕКУЩИХ входов признака — ключ опознания. Ребро API5 переносится в API7 и
        // сравнивается по Reference: индекс произволен, имя между API5 и API7 не совпадает,
        // радиус не различает два скругления одного радиуса.
        var currentRefs = new List<int>(currentEdges.Count);
        foreach (var edge in currentEdges)
        {
            if (ReferenceOfApi7(bridge, edge) is int reference)
            {
                currentRefs.Add(reference);
            }
        }

        int? index = null;
        string identifiedBy;
        if (currentRefs.Count > 0 && currentRefs.Count == currentEdges.Count)
        {
            // Основной путь: признак опознаётся по СОСТАВУ собственных входов.
            index = Api7Fillet.FindIndexByInputReferences(container, currentRefs);
            identifiedBy = "состав входов";
        }
        else
        {
            // Запасной путь: определение API5 входов не отдало (измерено), либо перенеслись не все.
            // Радиус определения — единственный скаляр, который обе стороны описывают одинаково.
            // Число входов берётся у IFillet (он его отдаёт всегда), а не у определения API5.
            identifiedBy = "радиус и число входов (определение API5 входов не отдало)";
            if (beforeSource?.radius is double radiusKey)
            {
                var byRadius = Api7Fillet.FindIndexesByRadius(container, radiusKey);
                if (byRadius.Count == 1)
                {
                    index = byRadius[0];
                }
            }
        }

        if (index is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Скругление не опознано однозначно в IModelContainer.Fillets ("
                + identifiedBy + "): ни одного либо несколько совпадений. Записать набор в чужой " +
                "признак означало бы молча испортить чужую геометрию. Признак не изменён.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["identified_by"] = identifiedBy,
                    ["current_input_refs"] = currentRefs,
                    ["api7_fillets"] = Api7Fillet.Count(container),
                    ["api7_by_input_refs"] =
                        Api7Fillet.FindIndexesByInputReferences(container, currentRefs),
                });
        }

        // Новый набор строится как ПОДМНОЖЕСТВО СОБСТВЕННЫХ ВХОДОВ признака, а не как набор
        // заново добытых из тела объектов. Это не приём реализации, а измеренное требование:
        // проба H-2 (H2.3/H2.5) принимает признак назад ТОЛЬКО те объекты, которые сама у него
        // прочитала, — её собственные слова: «Пишем подмножество из N объектов, прочитанных из
        // BaseObjects… Clear() НЕ вызывается, рёбра конечного тела НЕ ищутся».
        //
        // Здесь это первое время было сделано иначе: ссылки клиента разрешались в рёбра тела и
        // переносились в API7 заново. Такой набор признак не принимает — измерено приёмкой
        // (FL10: level=call_returned, V=80000 при err=None, то есть вызов прошёл, а набор не
        // применился). Отсюда правило: требуемый набор ОТБИРАЕТСЯ из входов признака, а ссылки
        // клиента лишь ГОВОРЯТ, какие из них оставить.
        var requested = new List<ksEntity>(edgeRefs.Count);
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in edgeRefs)
        {
            var stored = References.Require(reference, document.Id, document.Revision);
            if (stored.Payload is not ksEdgeDefinition edge)
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Ссылка '{reference}' указывает не на ребро (kind={stored.Kind}).",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["kind"] = stored.Kind });
            }

            var unwrapped = UnwrapEdgeToEntity(edge, reference);
            routes.Add(unwrapped.Route);
            requested.Add(unwrapped.Entity);
        }

        // Опознание запрошенных рёбер в терминах API7. Ребро тела приходит из API5 и требует
        // переноса <c>ksAPI7Dual</c>; вход признака приходит из BaseObjects уже объектом API7.
        // Обе стороны приводятся к <c>int</c>, но разными путями — это и есть та асимметрия,
        // из-за которой совпадение по Reference НЕ является универсальным ключом.
        var liveInputs = Api7Fillet.ReadBaseObjects(container, index.Value);
        IReadOnlyList<IModelObject> targets;
        string selectionRoute;
        if (liveInputs is null)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "IFillet.BaseObjects не перечитался: набор признака прочитать нечем, а опознать " +
                "признак без него нельзя. Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true);
        }

        var inputRefs = Api7Fillet.ReferencesOf(liveInputs);

        var wantedRefs = new List<int>(requested.Count);
        var unresolved = new List<string>();
        for (var i = 0; i < requested.Count; i++)
        {
            if (ReferenceOfApi7(bridge, requested[i]) is int wantedRef)
            {
                wantedRefs.Add(wantedRef);
            }
            else
            {
                unresolved.Add(edgeRefs[i]);
            }
        }

        if (unresolved.Count > 0)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Из запрошенных рёбер в API7 не перенеслось " + unresolved.Count + " (" +
                string.Join(", ", unresolved) + "): предъявить их признаку нечем. " +
                "Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["unresolved"] = unresolved });
        }

        // Новый набор ПРЕДЪЯВЛЯЕТСЯ объектами, добытыми переносом, — ровно так, как это измерено
        // в решающем контроле пробы H-2 (H2.4). Проба писала в IFillet.BaseObjects массив из ОДНОГО
        // ребра тела, перенесённого ksAPI7Dual, которого среди собственных входов признака ЗАВЕДОМО
        // не было (свободный угол), и КОМПАС это принял: состав переехал на другой угол при
        // неизменном размере набора. Значит продукт принимает перенесённые объекты и НЕ требует,
        // чтобы они совпадали с тем, что признак уже держит.
        //
        // Здесь стояло СТРОЖЕ: набор отбирался только из собственных входов признака по совпадению
        // Reference, и всё остальное отвергалось. Это правило было введено по недоразумению —
        // оно опиралось на наблюдение H2.7 («входы 1073742065–67 против ребра тела 1073742080»),
        // прочитанное как «контексты несопоставимы». Измерено 17.09.2026 на FL10x: полосы СОСЕДНИЕ
        // (перенесённое ребро 1073742309 против входа признака 1073742308), тип у обоих
        // ksObjectEdge, обе ссылки устойчивы при повторном чтении, а адресные привязки разные.
        // То есть это не «чужое пространство нумерации», а обычная двойственность API5/API7:
        // ksEntity тела и IModelObject признака — два разных COM-объекта про одно ребро. Проба
        // такую сверку НЕ делала никогда, поэтому запрет был не измерением, а догадкой.
        //
        // Что осталось от прежнего правила и почему: признак по-прежнему надо ОПОЗНАТЬ (см. выше,
        // FindIndexByInputReferences), и для этого нужны его собственные входы. Совпадающие ссылки
        // означают, что клиент просит оставить часть того, что уже есть, — тогда берутся ТЕ ЖЕ
        // объекты признака (не пересозданные), потому что для подмножества это строго измеренный
        // путь H2.3/H2.5. НЕсовпадающие — предъявляются перенесёнными, как в H2.4.
        var overlapping = wantedRefs.Count(w => inputRefs.Contains(w));
        var targetsByOwn = overlapping > 0 && overlapping == wantedRefs.Count;

        if (targetsByOwn)
        {
            // Все запрошенные рёбра — среди собственных входов признака: строго измеренный путь
            // сокращения (H2.3 4→3, H2.5 4→2). Пишутся объекты ИЗ BaseObjects, Clear() не зовётся.
            var ownKept = new List<IModelObject>(wantedRefs.Count);
            for (var i = 0; i < liveInputs.Count; i++)
            {
                if (i < inputRefs.Count && wantedRefs.Contains(inputRefs[i]))
                {
                    ownKept.Add(liveInputs[i]);
                }
            }

            targets = ownKept;
            selectionRoute = $"отбор из BaseObjects признака: {ownKept.Count} из {liveInputs.Count}";
        }
        else
        {
            // Хотя бы одно запрошенное ребро признак не держит: предъявляются ПЕРЕНЕСЁННЫЕ объекты
            // (H2.4). Перенос обязателен именно здесь: `requested` — это `ksEntity` API5, а
            // `BaseObjects` принимает `IModelObject`. Тот же режим ksAPI7Dual, что и в опознании.
            //
            // Разворачивание берётся из `requested` в ТОМ ЖЕ порядке, что и `wantedRefs`: ссылка
            // каждого объекта уже добыта выше и стоит в `wantedRefs` под тем же индексом, поэтому
            // повторный перенос не нужен — и не делается, чтобы объект, попавший в набор, был
            // буквально тем же, чья ссылка измерена.
            var transferredTargets = new List<IModelObject>(requested.Count);
            var transferFailed = new List<string>();
            for (var i = 0; i < requested.Count; i++)
            {
                if (bridge.TransferTo7(requested[i]) is IModelObject modelObject)
                {
                    transferredTargets.Add(modelObject);
                }
                else
                {
                    transferFailed.Add(edgeRefs[i]);
                }
            }

            if (transferFailed.Count > 0)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Ребро перенеслось в API7 для опознания, но не перенеслось для предъявления (" +
                    string.Join(", ", transferFailed) + "). Признак не изменён.",
                    RetryPolicy.ReacquireContext,
                    partialEffects: true,
                    details: new Dictionary<string, object?> { ["unresolved_on_transfer"] = transferFailed });
            }

            // Отдельно оговорено и НЕ проверяется здесь: расширяет ли это набор или заменяет его.
            // Проба измерила замену (1→1) и сокращение (4→3, 4→2); чистого расширения на эталоне
            // из четырёх углов поставить нельзя — у пластины ровно четыре вертикальных угла.
            // Поэтому ответ не утверждает «добавилось N», он сообщает измеренный состав после
            // Update, а предикат подтверждения (объём + перечитанный набор) решает сам.
            targets = transferredTargets;
            selectionRoute =
                $"предъявлены перенесённые объекты (H2.4): {transferredTargets.Count} из запрошенных, " +
                $"совпало с входами признака {overlapping} из {wantedRefs.Count}";
        }

        var targetRefs = Api7Fillet.ReferencesOf(targets);

        var write = Api7Fillet.TryWriteBaseObjects(container, index.Value, targets);
        if (!write.Written)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Запись набора в IFillet.BaseObjects не подтверждена: " +
                (write.Failure ?? "причина не сообщена") + ". Признак не изменён.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["api7_failure"] = write.Failure });
        }

        // Без перестроения запись в IFillet остаётся представлением: тот же порядок, что при создании
        // скругления и при правке радиуса (F.10 + проба E).
        Api7Bridge.Rebuild(container, document.Document);
        BumpRevision(document, "fillet.update.edges");

        var volumeAfter = ReadVolume(document);
        var featuresAfter = CountFeatures(document);
        var stateAfter = ReadFeatureState(entity);

        // Набор перечитывается С ЖИВОЙ МОДЕЛИ, а не с того объекта, в который писали: запись без
        // эффекта (как ksFilletDefinition.radius на FL04r) не должна выглядеть применённой.
        var afterInputs = Api7Fillet.ReadBaseObjects(container, index.Value);
        var afterRefs = afterInputs is null ? null : Api7Fillet.ReferencesOf(afterInputs);

        var edgesSet = afterRefs is not null && SameSet(afterRefs, targetRefs);
        var sameFeature = featuresBefore == featuresAfter && stateBefore.Name == stateAfter.Name;

        var checks = new List<NamedCheck>
        {
            new("api7_write_applied", true,
                Observed: $"IFillet[{index.Value}].BaseObjects ← [{string.Join(", ", targetRefs)}] " +
                          $"(было [{string.Join(", ", currentRefs)}]); {selectionRoute}; " +
                          "адреса записанных объектов: " +
                          string.Join(", ", targets.Select(AddressOf)) + "; " +
                          "направления разворачивания запрошенных рёбер: " + string.Join(" / ", routes),
                Expected: "запись принята и подтверждена перестроением"),
            new("edges_read_back", edgesSet,
                Observed: afterRefs is null
                    ? "не читается"
                    : $"[{string.Join(", ", afterRefs)}]",
                Expected: $"[{string.Join(", ", targetRefs)}]"),
            new("same_feature", sameFeature,
                Observed: $"признаков {featuresBefore}→{featuresAfter}, имя «{stateAfter.Name}»",
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
        if (!volumeMatched)
        {
            unverified.Add(edgesSet
                ? "volume_not_as_expected — набор записан и перечитан, но измерение объёма не совпало с ожиданием"
                : "edges_not_read_back — набор не перечитался в ожидаемом составе");
        }

        if (command.ExpectedVolumeMm3 is null)
        {
            unverified.Add(
                "expected_volume_not_supplied — без аналитического ожидания объёма правка набора не " +
                "может быть подтверждена геометрически");
        }

        var geometryConfirmed = edgesSet && sameFeature && volumeMatched;

        // ИСЧЕЗНОВЕНИЕ ПРИЗНАКА — ЭТО ОТКАЗ, А НЕ «УСПЕХ С ПОНИЖЕННЫМ УРОВНЕМ».
        //
        // Измерено 17.09.2026 (FL25, Г-образная пластина 100×80 с вырезом 40×30, шесть вертикальных
        // углов). Признаку был предъявлен набор [дуга скруглённого угла + вертикальное ребро
        // свободного угла] — обе части рёбра ТЕЛА. Запись прошла, перестроение прошло, ответ вернул
        // err=None и level=call_returned, а объём оказался равен 68000, то есть Г-ПЛАСТИНЕ БЕЗ
        // СКРУГЛЕНИЙ: набор не «не применился», он СХЛОПНУЛ признак, стерев уже сделанное
        // скругление. Клиент, читающий только err и level, принял бы это за успешную правку.
        //
        // Причина не в валюте как таковой, а в её границе: рёбра ТЕЛА описывают углы, которых
        // признак СЕЙЧАС не держит, и предъявление такого набора означает «построй скругление
        // заново по этим рёбрам», а не «оставь прежнее и добавь». Для сокращения и замены это
        // безразлично (там все предъявленные рёбра признаку уже принадлежат — FL10/FL10s/FL10b/
        // FL10x), а при РАСШИРЕНИИ предъявляется угол, которым признак не владеет, и прежний
        // состав теряется целиком.
        //
        // Поэтому случай «признак перестал читаться как скругление ИЛИ признак не выжил» объявляется
        // отказом ДО возврата успеха. Это не ослабление ожидания (объём по-прежнему сверяется с
        // аналитикой) и не подмена исхода: мутация уже произошла, поэтому отказ несёт
        // partial_effects=true — клиент обязан узнать, что модель изменена.
        //
        // Объём родителя берётся как объём ДО правки плюс снятое правкой: если признак стёрт,
        // геометрия возвращается ровно к «до», а не к «до минус скругления». Поэтому критерий —
        // «после правки признак не читается как скругление», а не арифметика по объёму: она
        // зависела бы от числа углов и повторила бы ошибку подгонки.
        if (afterRefs is null || !sameFeature)
        {
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Правка набора СТЁРЛА признак скругления: после записи и перестроения признак " +
                "больше не читается как скругление" +
                (volumeAfter is double lost
                    ? $" (объём тела {lost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}, " +
                      $"до правки {volumeBefore?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитан"})"
                    : string.Empty) +
                ". Предъявленный набор описан рёбрами ТЕЛА, а не собственными входами признака: " +
                "такой набор задаёт скругление ЗАНОВО по указанным рёбрам и не сохраняет прежний " +
                "состав. Для сокращения и замены это безразлично, для расширения — невыразимо. " +
                "Модель ИЗМЕНЕНА этой правкой — перечитайте контекст и документ.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["presented_inputs"] = targetRefs,
                    ["inputs_before"] = currentRefs,
                    ["selection_route"] = selectionRoute,
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["feature_read_back"] = afterRefs is not null,
                    ["feature_state_survived"] = sameFeature,
                });
        }

        // ЗАПИСЬ БЕЗ ЭФФЕКТА — ТОЖЕ ОТКАЗ, А НЕ «УСПЕХ С ПОНИЖЕННЫМ УРОВНЕМ».
        //
        // Измерено 18.09.2026 (FL25, Г-образная пластина 100×80 с вырезом 40×30). Признаку был
        // предъявлен набор [дуга скруглённого угла + вертикальное ребро СВОБОДНОГО угла] — обе части
        // рёбра ТЕЛА. Запись прошла, перестроение прошло, ответ вернул err=None и
        // level=call_returned, edges_read_back=1 при предъявленных 2, а объём остался на ОДНОМ угле
        // (67980.68583470576). Клиент, читающий status и err, принял бы это за выполненное
        // расширение — ровно тот порок, ради которого строка FL25 и заведена.
        //
        // Прежняя редакция ниже ловила только ИСЧЕЗНОВЕНИЕ признака (схлопывание). Между
        // «признак стёрт» и «набор записан» есть третий исход — «не произошло ничего», и он обязан
        // быть отказом по той же причине: правку просили именно потому, что она что-то меняет.
        // Ответ, в котором сработала проверка edges_read_back=false, не вправе называться успехом.
        //
        // Критерий — ПЕРЕЧИТАННЫЙ СОСТАВ, а не объём: объём зависит от числа и вида углов
        // (скругление входящего угла ДОБАВЛЯЕТ материал), и арифметика по нему повторила бы ошибку
        // подгонки. partial_effects отличает «модель изменена не тем, чем просили» от «модель не
        // тронута»: он сравнивает объём с объёмом ДО правки, а не с ожиданием клиента.
        if (!edgesSet)
        {
            var modelChanged = volumeAfter is double volumeNow && volumeBefore is double volumeWas
                               && Math.Abs(volumeNow - volumeWas) > ProfileArea.Tolerance(volumeWas);
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Набор не перечитался в запрошенном составе: записано " +
                $"[{string.Join(", ", targetRefs)}], на модели " +
                (afterRefs is null ? "набор не читается" : $"[{string.Join(", ", afterRefs)}]") +
                ". Набор рёбер ТЕЛА задаёт скругление ЗАНОВО по указанным рёбрам, а не «оставь " +
                "прежнее и добавь»: расширение набора им не выражается (сокращение и замена — " +
                "выражаются, они идут через base_object_refs). " +
                (modelChanged
                    ? "Модель ИЗМЕНЕНА этой правкой — перечитайте контекст и документ."
                    : "Геометрия не изменилась, но ревизия поднята: перечитайте контекст."),
                modelChanged ? RetryPolicy.AfterReconciliation : RetryPolicy.Never,
                partialEffects: modelChanged,
                details: new Dictionary<string, object?>
                {
                    ["presented_inputs"] = targetRefs,
                    ["inputs_read_back"] = afterRefs,
                    ["inputs_before"] = currentRefs,
                    ["selection_route"] = selectionRoute,
                    ["volume_before_mm3"] = volumeBefore,
                    ["volume_after_mm3"] = volumeAfter,
                    ["expected_volume_mm3"] = command.ExpectedVolumeMm3,
                    ["feature_read_back"] = afterRefs is not null,
                    ["feature_state_survived"] = sameFeature,
                });
        }

        return new UpdateFeatureResult(
            ToDto(References.Require(command.FeatureRef, document.Id, document.Revision), stateAfter.Name),
            FilletFamily,
            sameFeature,
            featuresAfter,
            volumeBefore,
            volumeAfter,
            DepthReadBackMm: null,
            EndConditionReadBack: null,
            new VerificationDto(
                geometryConfirmed ? VerificationLevel.GeometryChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            // Радиус не трогали: набор рёбер и радиус — разные предметы правки, и ответ не должен
            // выглядеть так, будто изменилось и то, и другое.
            RadiusReadBackMm: null,
            EdgesReadBack: afterRefs?.Count);
    }

    /// <summary>Сравнение составов как множеств: порядок выдачи коллекции недетерминирован.</summary>
    private static bool SameSet(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var a = left.OrderBy(x => x).ToArray();
        var b = right.OrderBy(x => x).ToArray();
        return a.SequenceEqual(b);
    }

    /// <summary>
    /// Устойчивая ссылка ребра API5 (<c>ksEntity</c>), перенесённого в API7.
    /// </summary>
    /// <remarks>
    /// Переносится тем же режимом <c>ksAPI7Dual</c>, что и всё остальное: проба E измерила, что
    /// неверный режим переноса даёт не ошибку, а объект, который «читается», но представляет другую
    /// сущность. null означает «не перенеслось» — это ответ, а не ноль.
    /// </remarks>
    private static int? ReferenceOfApi7(Api7Bridge bridge, ksEntity edge)
    {
        try
        {
            return bridge.TransferTo7(edge) is IModelObject modelObject ? modelObject.Reference : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Тождество COM-объекта, не зависящее от его собственных свойств: две ссылки на один объект
    /// дают одно число, даже если <c>Reference</c> у них разный или не читается.
    /// </summary>
    /// <remarks>
    /// Нужно ровно для одного различия, которое иначе нечем разрешить: «ссылка входов признака и
    /// ссылка ребра тела не совпали» может означать либо ДВА РАЗНЫХ объекта с разными полосами
    /// нумерации, либо ОДИН объект, чей <c>Reference</c> вычисляется в контексте чтения. Свойство
    /// <c>Reference</c> эти два случая не различает, адресная привязка RCW — различает.
    /// </remarks>
    private static string AddressOf(object instance)
    {
        try
        {
            return Marshal.GetIUnknownForObject(instance).ToString("X");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return "недоступен";
        }
    }
}

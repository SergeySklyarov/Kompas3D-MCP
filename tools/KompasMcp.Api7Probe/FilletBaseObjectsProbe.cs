using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба H-2 — входы САМОГО признака скругления через <c>IFillet.BaseObjects</c> на живом признаке.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему отдельная проба, а не шаг H.5.</b> Задание SM-09 (§137) требует, чтобы новый результат
/// имел собственный идентификатор прогона и собственные артефакты, а к старому отчёту было добавлено
/// точное пояснение, а не переписанный задним числом результат. Отдельный флаг даёт отдельный
/// <c>run_id</c>, отдельную рабочую папку и отдельный файл отчёта — H остаётся логом 16.09.
/// </para>
/// <para>
/// <b>Что здесь решается.</b> Проба H мерила пересчёт признака по ПОДСТАВЛЕННОМУ входу: в
/// <c>BaseObjects</c> уходили запомненные угловые рёбра пластины (<c>cornerEdges.Take(2)</c>).
/// Отсюда её геометрия верна, а подпись «ПРИМЕНЯЕТСЯ» — нет. Решающий вопрос другой: отдаёт ли
/// <c>BaseObjects</c> живого скругления те объекты, которые признак принял бы ОБРАТНО. Если да —
/// маршрут адресации существует, и <c>edit</c> можно вести через него. Если нет — доказано, что
/// адресовать вход признака нечем и остаётся <c>blocked_api</c>, но уже с точной формулировкой.
/// </para>
/// <para>
/// <b>Порядок опытов и почему он такой.</b>
/// </para>
/// <list type="number">
/// <item>Пластина 100×80×10, четыре вертикальных угла R3 (эталон 79922.74333882307).</item>
/// <item>Сохранение, закрытие, повторное открытие. Это требование задания §2: «Не используй
/// сохранённые до скругления COM-объекты или публичные ссылки». После reopen у нас заведомо нет
/// ни одного объекта, захваченного при создании, — значит всё, что мы прочитаем, получено от
/// ЖИВОГО признака.</item>
/// <item>Чтение <c>BaseObjects</c> без мутации: тип VARIANT/SAFEARRAY, длина, доступные интерфейсы,
/// пригодность каждого элемента. Три случая различаются явно — пустое значение, другой тип
/// массива, ошибка маршалинга.</item>
/// <item>Контроль: проверить, что элементы <c>BaseObjects</c> вообще имеют геометрический смысл —
/// сверить их число и, где возможно, тип с ожиданием.</item>
/// <item>Сокращение 4→2 и 4→3 ИМЕННО объектами из <c>BaseObjects</c>, без Clear и без поиска
/// рёбер конечного тела. Проверка СОСТАВА, а не количества: набор из трёх отличим от «сломали и
/// собрали четыре».</item>
/// <item>Если сокращение подтверждено — save → close → reopen → перечитать.</item>
/// </list>
/// <para>
/// <b>Почему 4→3, а не только 4→2.</b> Эталон 79961.37166941153 (два угла) и 79942.05750411731
/// (три) уже измерены на этой установке. Три — это «тот же размер минус один», то есть набор,
/// который нельзя получить простым совпадением числа: если после записи объём окажется на четырёх
/// углах, значит запись проигнорирована; если на двух — сработало не то, что мы просили; только
/// эталон трёх углов доказывает, что принят НАШ набор. Это и есть различение состава от количества.
/// </para>
/// <para>
/// <b>Чего проба не делает.</b> Не вызывает <c>Clear()</c> перед записью из <c>BaseObjects</c>
/// (это подмена предмета: задание прямо запрещает предварительное опустошение), не ищет рёбра
/// конечного тела и не трогает публичный реестр ссылок MCP — прямой зонд обязан работать без него.
/// </para>
/// </remarks>
internal sealed class FilletBaseObjectsProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;
    private const double Radius3 = 3d;

    private const short Fillet = 34;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private IApplication? _app7;

    public FilletBaseObjectsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    /// <summary>Writes the report from inside a run, for the <c>--keep</c> diagnostic path.</summary>
    public static void Flush(ProbeReport report, Options options)
    {
        var stem = "fillet-base-objects";
        report.Flush(
            Path.Combine(options.ReportDir, stem + ".json"),
            Path.Combine(options.ReportDir, stem + ".md"));
    }

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            // Решающая линия идёт первой и на своём документе: всё дальнейшее — уточнения к ней.
            var live = BuildLiveFillet();
            if (live is not null)
            {
                ReadBaseObjects(live);
                ReduceUsingBaseObjects(live);
            }

            // ── различающие контроли. Без них «объём перешёл на эталон трёх» доказывает не больше,
            //    чем доказала проба H: пересчёт признака по подставленному входу даёт ТУ ЖЕ картину.
            //    Каждый идёт на своём документе (своя пластина), чтобы не наследовать чужое состояние.
            SubstituteOneEdge();
            EnlargeUsingBaseObjects();

            // ── Адресация на модели с ДВУМЯ скруглениями одного радиуса: обязательный критерий
            //    приёмки (§«Проверка адресации на модели с двумя скруглениями одинакового радиуса:
            //    меняется только выбранный признак»). Один признак без соседа не различает
            //    «адресация» и «единственный кандидат».
            AddressAmongTwoFillets();

            // Согласование с пробой H — не пересказ, а сверка на числах ЭТОГО прогона: она
            // пересчитывает, следует ли из полученного то, что приписывалось пробе H.
            ReconcileWithProbeH();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("H2.X", "Необработанное исключение пробы H-2");
            crash.Errors.Add(ex.ToString());
            crash.Fail(ex.Message);
        }
        finally
        {
            Finish();
        }
    }

    // ════════════════════════════════════════════════════════ подготовка живого признака ══

    /// <summary>
    /// Пластина со скруглением R3 по четырём вертикальным углам, СОХРАНЁННАЯ, ЗАКРЫТАЯ и
    /// ОТКРЫТАЯ ЗАНОВО. Возвращается признак, полученный уже после reopen.
    /// </summary>
    /// <param name="label">
    /// Метка сценария: у каждого контроля свой документ и свой файл, потому что сокращение набора
    /// необратимо и на общем документе опыты мерили бы друг друга.
    /// </param>
    /// <param name="filledCorners">
    /// Сколько углов из четырёх скруглить при ПЕРВИЧНОМ создании признака. По умолчанию все четыре
    /// (эталон 79922.74333882307). Контроль замены требует трёх: тогда четвёртый угол остаётся
    /// свободным, и его ребро можно предъявить как добавляемое. Создать набор из четырёх и потом
    /// искать «свободный угол» невозможно — при r=3 по всем углам свободных углов не остаётся
    /// вообще, и первая версия контроля именно на этом и остановилась.
    /// </param>
    private LiveFillet? BuildLiveFillet(string label = "H2.1", int filledCorners = 4)
    {
        var step = _report.Begin(
            label + ":live",
            "Живое скругление после save→close→reopen",
            "Можно ли получить IFillet, не держа ни одного объекта, захваченного при создании?");

        ksDocument3D? doc = null;
        var path = Path.Combine(_options.WorkDir, $"h2-{label.Replace(':', '-')}.m3d");
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return null;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, label) is null)
            {
                step.Fail("Пластина не построена.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // Угловые рёбра ДО скругления — только для создания признака. Дальше они не нужны и
            // НЕ ПОЙДУТ ни в одно измерение: после reopen у нас их всё равно нет.
            var corners = VerticalCornerEdges(part, step);
            step.Data["corner_edges_before_create"] = corners.Count;
            if (corners.Count != 4)
            {
                step.Fail($"Вертикальных угловых рёбер {corners.Count}, нужно ровно 4 — эталон не годится.");
                Close(doc);
                return null;
            }

            // Скругляется ПЕРВЫЕ `filledCorners` углов из четырёх. Порядок выдачи рёбер
            // недетерминирован, но для эталона это неважно: важно лишь КОЛИЧЕСТВО углов, а объём
            // от выбора угла не зависит (формула симметрична). Для контроля замены берётся 3 —
            // тогда четвёртый угол остаётся свободным и его ребро можно предъявить как добавляемое.
            var toFillet = corners.Take(filledCorners).ToList();
            step.Data["corners_to_fillet"] = toFillet.Count;

            var created = CreateFillet(step, part, toFillet, Radius3);
            if (created is null)
            {
                Close(doc);
                return null;
            }

            // Перестроение — как в пробе H: у части документа, а не у признака (у ksPart члена
            // Document нет; компилятор это подтвердил).
            doc.RebuildDocument();
            var expectedCreate = PlateVolume - filledCorners * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var volumeAfterCreate = Api5.Volume(part);
            step.Data["volume_after_create"] = Api5.Num(volumeAfterCreate);
            step.Data["expected_at_create"] = Api5.Num(expectedCreate);
            if (volumeAfterCreate is not double vc || Math.Abs(vc - expectedCreate) > Tolerance(expectedCreate))
            {
                step.Fail($"Скругление {filledCorners} углов не дало эталон: {Api5.Num(volumeAfterCreate)} против {Api5.Num(expectedCreate)}.");
                Close(doc);
                return null;
            }

            // ── вот здесь разрывается связь с созданием ──
            doc.SaveAs(path);
            Close(doc);
            doc = null;
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Open(path) != true)
            {
                step.Fail("Повторно открыть сохранённый документ не удалось.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();
            var reopenedPart = (ksPart)doc.GetPart(-1);
            var volumeAfterReopen = Api5.Volume(reopenedPart);
            step.Data["volume_after_reopen"] = Api5.Num(volumeAfterReopen);

            var container = Container(step, doc);
            if (container is null)
            {
                Close(doc);
                return null;
            }

            var filletCount = Api5.SafeInt(() => container.Fillets.Count);
            step.Data["fillets_count"] = filletCount;
            if (filletCount != 1)
            {
                step.Fail($"Скруглений в контейнере {filletCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, ожидалось 1 — сопоставление неоднозначно.");
                Close(doc);
                return null;
            }

            if (container.Fillets[0] is not IFillet live)
            {
                step.Fail("Элемент Fillets[0] не отдаёт IFillet.");
                Close(doc);
                return null;
            }

            if (volumeAfterReopen is not double vr || Math.Abs(vr - expectedCreate) > Tolerance(expectedCreate))
            {
                step.Fail($"После reopen объём {Api5.Num(volumeAfterReopen)} не на эталоне {filledCorners} углов {Api5.Num(expectedCreate)} — признак не выжил, мерить нечего.");
                Close(doc);
                return null;
            }

            step.Data["saved_path"] = path;
            step.Observe($"Признак выжил reopen: V={Api5.Num(volumeAfterReopen)} при эталоне {Api5.Num(expectedCreate)}; " +
                         "ни один объект, захваченный при создании, в дальнейших шагах не используется.");

            // Число входов читается ЗДЕСЬ, а не только в шаге H2.2: контроли H2.4/H2.5 — это другие
            // документы со своими признаками, и `BaseObjectCount` от первого признака к ним не
            // относится. Первая версия оставляла поле пустым, и оба контроля отказывались работать
            // с формулировкой «входов не прочитано» — отказ харнесса, который легко принять за
            // отказ продукта.
            var inputs = Api5.SafeObject(() => live.BaseObjects);
            var inputLength = inputs is null ? null : LengthOf(inputs);
            step.Data["base_objects_at_prep"] = inputLength;
            step.Data["base_objects_type_at_prep"] = inputs is null ? "null" : Api5.RuntimeName(inputs);

            step.Pass("Живое скругление получено после reopen; связь с созданием разорвана.");
            return new LiveFillet
            {
                Doc = doc,
                Part = reopenedPart,
                Container = container,
                Fillet = live,
                BaseObjectCount = inputLength,
            };
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Подготовка живого признака не удалась: " + ex.Message);
            if (doc is not null)
            {
                Close(doc);
            }

            return null;
        }
    }

    // ════════════════════════════════════════ живой признак на модели с ДВУМЯ скруглениями ══

    /// <summary>
    /// Пластина 100×80×10 с <b>ДВУМЯ независимыми скруглениями R3 на разных углах</b>, сохранённая,
    /// закрытая и открытая заново. Возвращается контейнер, в котором ровно два признака.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем второй признак.</b> Критерий приёмки требует проверить адресацию на модели с двумя
    /// скруглениями ОДИНАКОВОГО радиуса: меняется только выбранное. На модели с одним признаком
    /// «адресация» и «единственный кандидат» неразличимы, и маршрут выглядит работающим независимо
    /// от того, умеет ли он выбирать.
    /// </para>
    /// <para>
    /// <b>Почему два ОТДЕЛЬНЫХ признака, а не один с двумя рёбрами.</b> Один признак с набором из
    /// двух рёбер — это один объект, и запись в него ничего не говорит о выборе МЕЖДУ объектами.
    /// Нужны два объекта в <c>container.Fillets</c>.
    /// </para>
    /// <para>
    /// <b>Радиус одинаков намеренно.</b> Если бы радиусы различались, сопоставление можно было бы
    /// вести по радиусу; задание прямо запрещает переносить такое упрощение в продукт, и опыт с
    /// разными радиусами не проверял бы то, что требуется.
    /// </para>
    /// </remarks>
    private LiveFillet? BuildLiveTwoFillets(string label)
    {
        var step = _report.Begin(
            label + ":live",
            "Живые ДВА скругления R3 после save→close→reopen",
            "Даёт ли документ два независимых признака одного радиуса после reopen?");

        ksDocument3D? doc = null;
        var path = Path.Combine(_options.WorkDir, $"h2-{label.Replace(':', '-')}-two.m3d");
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return null;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, label) is null)
            {
                step.Fail("Пластина не построена.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // Все четыре угловых ребра — из них берутся ДВА разных угла, каждый в свой признак.
            var corners = VerticalCornerEdges(part, step);
            step.Data["corner_edges_before_create"] = corners.Count;
            if (corners.Count != 4)
            {
                step.Fail($"Вертикальных угловых рёбер {corners.Count}, нужно ровно 4 — эталон не годится.");
                Close(doc);
                return null;
            }

            // Углы выбираются ПО КООРДИНАТАМ, а не по позициям в коллекции: порядок выдачи
            // недетерминирован, и два «первых» элемента легко оказались бы одним и тем же углом.
            var allCorners = new[] { "(-50,-40)", "(-50,40)", "(50,-40)", "(50,40)" };
            var byCorner = new Dictionary<string, ksEntity>(StringComparer.Ordinal);
            foreach (var edge in corners)
            {
                if (CornerOf(edge) is string corner && !byCorner.ContainsKey(corner))
                {
                    byCorner[corner] = edge;
                }
            }

            if (byCorner.Count != 4)
            {
                step.Fail($"Различных углов среди рёбер {byCorner.Count}, нужно 4 — два признака не построить.");
                Close(doc);
                return null;
            }

            var firstCorner = allCorners[0];
            var secondCornerA = allCorners[1];
            var secondCornerB = allCorners[3];
            step.Data["first_fillet_corners"] = new[] { firstCorner };
            step.Data["second_fillet_corners"] = new[] { secondCornerA, secondCornerB };
            step.Data["first_fillet_radius"] = Radius3;
            step.Data["second_fillet_radius"] = Radius3;

            var firstFeature = CreateFillet(step, part, new[] { byCorner[firstCorner] }, Radius3);
            if (firstFeature is null)
            {
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // Второй признак — на ДВА угла. Разные размеры наборов нужны, чтобы мутация была
            // ВИДНА и ОТНОСИМА: у признака с одним входом снятие входа означало бы опустошение
            // набора, и это другой опыт. Первая версия делала оба признака одноугольными и
            // остановилась именно на этом.
            var secondFeature = CreateFillet(step, part, new[] { byCorner[secondCornerA], byCorner[secondCornerB] }, Radius3);
            if (secondFeature is null)
            {
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // Эталон: три угла из четырёх (1 + 2, без пересечения).
            var expectedThree = PlateVolume - 3 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var volumeAfterCreate = Api5.Volume(part);
            step.Data["volume_after_create"] = Api5.Num(volumeAfterCreate);
            step.Data["expected_three_corners"] = Api5.Num(expectedThree);
            if (volumeAfterCreate is not double vc || Math.Abs(vc - expectedThree) > Tolerance(expectedThree))
            {
                step.Fail($"Скругления 1+2 угла не дали эталон: {Api5.Num(volumeAfterCreate)} против {Api5.Num(expectedThree)}.");
                Close(doc);
                return null;
            }

            // ── разрывается связь с созданием ──
            doc.SaveAs(path);
            Close(doc);
            doc = null;
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Open(path) != true)
            {
                step.Fail("Повторно открыть сохранённый документ не удалось.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();
            var reopenedPart = (ksPart)doc.GetPart(-1);
            var volumeAfterReopen = Api5.Volume(reopenedPart);
            step.Data["volume_after_reopen"] = Api5.Num(volumeAfterReopen);

            var container = Container(step, doc);
            if (container is null)
            {
                Close(doc);
                return null;
            }

            var filletCount = Api5.SafeInt(() => container.Fillets.Count);
            step.Data["fillets_count"] = filletCount;
            if (filletCount != 2)
            {
                step.Fail($"Скруглений в контейнере {filletCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, ожидалось 2 — адресация неоднозначна.");
                Close(doc);
                return null;
            }

            if (container.Fillets[0] is not IFillet live)
            {
                step.Fail("Элемент Fillets[0] не отдаёт IFillet.");
                Close(doc);
                return null;
            }

            step.Data["saved_path"] = path;
            step.Observe($"Два признака выжили reopen: V={Api5.Num(volumeAfterReopen)} при эталоне " +
                         $"{Api5.Num(expectedThree)}; первый на угол {firstCorner}, второй на углы " +
                         $"{secondCornerA} и {secondCornerB}, оба R{Radius3.ToString(CultureInfo.InvariantCulture)}.");

            var inputs = Api5.SafeObject(() => live.BaseObjects);
            var inputLength = inputs is null ? null : LengthOf(inputs);
            step.Data["first_base_objects_at_prep"] = inputLength;
            step.Data["first_base_objects_type_at_prep"] = inputs is null ? "null" : Api5.RuntimeName(inputs);

            step.Pass("Две живые скругления получены после reopen; связь с созданием разорвана.");
            return new LiveFillet
            {
                Doc = doc,
                Part = reopenedPart,
                Container = container,
                Fillet = live,
                BaseObjectCount = inputLength,
            };
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Подготовка двух скруглений не удалась: " + ex.Message);
            if (doc is not null)
            {
                Close(doc);
            }

            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════ чтение BaseObjects ══

    /// <summary>
    /// Что именно возвращает <c>BaseObjects</c> живого признака: тип, длина, интерфейсы, пригодность.
    /// Различает пустое значение, другой тип массива и ошибку маршалинга — задание §2 запрещает
    /// читать несовпадение с <c>object[]</c> как пустой набор.
    /// </summary>
    private void ReadBaseObjects(LiveFillet live)
    {
        var step = _report.Begin(
            "H2.2",
            "Чтение BaseObjects живого скругления: тип, длина, интерфейсы",
            "Отдаёт ли живой признак свои входы — и в каком именно виде?");

        object? raw;
        try
        {
            raw = live.Fillet.BaseObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data["read_failure"] = HResult.Describe(ex);
            step.Unknown("Чтение BaseObjects отвергнуто: " + ex.GetType().Name + ": " + ex.Message +
                         " — это ОТКАЗ ЧТЕНИЯ, а не пустой набор.");
            return;
        }

        if (raw is null)
        {
            step.Data["raw_type"] = "null";
            step.Fail("BaseObjects вернул null: живой признак не отдаёт свои входы этим членом. " +
                      "Пустое значение, а не ошибка — это самостоятельный результат.");
            return;
        }

        step.Data["raw_type"] = Api5.RuntimeName(raw);

        // Тип массива различается явно: object[] — ожидаемый, но не единственно возможный.
        var length = LengthOf(raw);
        step.Data["length"] = length;
        step.Observe($"BaseObjects вернул {Api5.RuntimeName(raw)} длиной " +
                     (length?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано") + ".");

        if (length is not int count || count == 0)
        {
            step.Fail("BaseObjects вернул пустой набор: признак не удерживает ни одного входа этим " +
                      "членом. (Значение прочитано и не является ошибкой — это ответ «входов нет».)");
            return;
        }

        live.BaseObjectCount = count;

        // Пригодность каждого элемента: что это за объект и отвечает ли он на интерфейсы, которыми
        // признак принимает входы. Прямое приведение к object[] уже есть в проекте; проверяем
        // СЕМАНТИКУ объектов, а не способ их достать (задание §5).
        var details = new List<string>();
        var usable = 0;
        for (var i = 0; i < count; i++)
        {
            var element = ElementAt(raw, i);
            if (element is null)
            {
                details.Add($"[{i}] null");
                continue;
            }

            var name = Api5.RuntimeName(element);
            var asModelObject = TryQi<IModelObject>(element);
            var asEntity5 = element as ksEntity;
            var kind = DescribeEdge(element);

            if (asModelObject is not null)
            {
                usable++;
            }

            details.Add($"[{i}] {name} → IModelObject={(asModelObject is not null)}, " +
                        $"ksEntity={(asEntity5 is not null)}, {kind}");
        }

        step.Data["elements"] = details;
        step.Data["usable_as_input"] = usable;
        step.Observe($"Входов {count}, из них приводятся к IModelObject: {usable}.");

        if (usable == 0)
        {
            step.Fail($"Ни один из {count} входов не приводится к IModelObject: предъявить их признаку " +
                      "обратно этим маршрутом нельзя. Признак удерживает входы, но не отдаёт их в " +
                      "пригодном для записи виде.");
            return;
        }

        step.Pass($"BaseObjects живого признака читается: {count} входов, {usable} пригодны к предъявлению.");
    }

    // ══════════════════════════════════════════════════════ сокращение входами признака ══

    /// <summary>
    /// Само сокращение: из объектов, ПРОЧИТАННЫХ ИЗ <c>BaseObjects</c>, собрать подмножество и
    /// записать обратно. Ни <c>Clear()</c>, ни поиска рёбер конечного тела — обе операции подменили
    /// бы предмет (задание §2).
    /// </summary>
    private void ReduceUsingBaseObjects(LiveFillet live)
    {
        var step = _report.Begin(
            "H2.3",
            "Сокращение 4→3 входами, прочитанными ИЗ BaseObjects",
            "Принимает ли признак назад свои же входы, если их стало меньше?");

        if (live.BaseObjectCount is not int count || count == 0)
        {
            step.Unknown("Входов для сокращения нет: шаг чтения не дал пригодных объектов. " +
                         "Сокращать нечего — это следствие отказа чтения, а не самостоятельный отказ.");
            return;
        }

        object? raw;
        try
        {
            raw = live.Fillet.BaseObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data["read_failure"] = HResult.Describe(ex);
            step.Unknown("BaseObjects не перечитался перед сокращением: " + ex.Message);
            return;
        }

        var volumeBefore = Api5.Volume(live.Part);
        step.Data["volume_before"] = Api5.Num(volumeBefore);

        // Три из ЧЕТЫРЁХ (а не два): набор из трёх отличим от «сломали и собрали четыре» и от
        // постороннего набора из двух. Эталон трёх углов измерен ранее: 79942.05750411731.
        var keep = Math.Min(3, count);
        var subset = new List<object>(keep);
        for (var i = 0; i < keep; i++)
        {
            var element = ElementAt(raw, i);
            if (element is not null)
            {
                subset.Add(element);
            }
        }

        step.Data["requested_subset"] = subset.Count;
        step.Data["source_count"] = count;
        step.Observe($"Пишем подмножество из {subset.Count} объектов, прочитанных из BaseObjects " +
                     $"(всего их {count}). Clear() НЕ вызывается, рёбра конечного тела НЕ ищутся.");

        try
        {
            live.Fillet.BaseObjects = subset.ToArray();
            step.Data["write"] = "ok";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            step.Data["write"] = HResult.Describe(ex);
            step.Unknown("Запись подмножества из BaseObjects отвергнута: " + ex.Message +
                         " — отказ записи, а не «не применилось».");
            return;
        }

        var updated = Applied(() => live.Fillet.Update());
        step.Data["update"] = updated;
        Rebuild(live);

        var volumeAfter = Api5.Volume(live.Part);
        step.Data["volume_after"] = Api5.Num(volumeAfter);

        var expected1 = PlateVolume - 1 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected2 = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected3 = PlateVolume - 3 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        step.Data["expected_1_corner"] = Api5.Num(expected1);
        step.Data["expected_2_corners"] = Api5.Num(expected2);
        step.Data["expected_3_corners"] = Api5.Num(expected3);
        step.Data["expected_4_corners"] = Api5.Num(expected4);

        step.Observe($"Объём: было {Api5.Num(volumeBefore)} → стало {Api5.Num(volumeAfter)}; " +
                     $"набор из {subset.Count} дал бы {Api5.Num(expected3)}, сохранение прежнего — {Api5.Num(expected4)}, " +
                     $"схлопывание до пластины — {Api5.Num(PlateVolume)}.");

        if (Matches(volumeAfter, expected3))
        {
            step.Pass($"Сокращение входами ИЗ BaseObjects ПРИМЕНЯЕТСЯ: объём перешёл на эталон {subset.Count} рёбер. " +
                      "Маршрут адресации входов существующего признака найден.");
        }
        else if (Matches(volumeAfter, expected4))
        {
            step.Fail("Запись подмножества из BaseObjects НЕ применяется: объём остался на четырёх рёбрах. " +
                      "Признак принимает запись без эффекта — как ksFilletDefinition.radius на FL04r.");
        }
        else if (Matches(volumeAfter, PlateVolume))
        {
            step.Fail("Набор схлопнулся до пластины: прочитанные из BaseObjects объекты признак как входы " +
                      "НЕ удерживает. Это тот же тупик, что и на живых рёбрах конечного тела.");
        }
        else if (Matches(volumeAfter, expected2) || Matches(volumeAfter, expected1))
        {
            step.Fail($"Объём {Api5.Num(volumeAfter)} соответствует НЕ нашему набору из {subset.Count} " +
                      "рёбер: принят не тот состав, который мы предъявили.");
        }
        else
        {
            step.Fail($"Объём {Api5.Num(volumeAfter)} не совпал ни с набором из {subset.Count} " +
                      $"({Api5.Num(expected3)}), ни с прежним ({Api5.Num(expected4)}), ни с пластиной " +
                      $"({Api5.Num(PlateVolume)}).");
        }
    }

    // ═════════════════════════════════════════════════════════ различающие контроли ══
    /// <summary>
    /// <b>Замена одного ребра другим при НЕИЗМЕННОМ размере набора (1→1).</b> Это единственный опыт,
    /// который отличает «признак принял наш состав» от «признак пересчитал то, чем был обязан быть».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Почему без него нельзя. В опыте 4→3 объём перешёл на эталон трёх углов — но признак, у
    /// которого сняли одно ребро, ОБЯЗАН пересчитаться именно так, независимо от того, понял ли он
    /// наши входы. Ровно этой подменой и была ложная подпись пробы H. Замена держит количество
    /// прежним, поэтому объём здесь не различает НИЧЕГО: набор из одного угла даёт
    /// 79980.68583470577 при любом выборе угла (формула симметрична). Различает только СОСТАВ.
    /// </para>
    /// <para>
    /// <b>Почему один угол, а не три или четыре (обе прежние версии опыта оказались
    /// несостоятельны, и по разным причинам).</b>
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Четыре угла.</b> Первая версия строила признак на всех четырёх углах и падала с
    /// «свободных углов нет»: при r=3 по всем углам свободного угла не остаётся вообще, и
    /// предъявлять как добавляемое нечего. Это отказ замысла опыта, а не свойство КОМПАС.
    /// </item>
    /// <item>
    /// <b>Три угла.</b> Вторая версия строила признак на трёх углах и падала с «угловых рёбер
    /// пластины 1, нужно 4». Причина установлена, и она поучительна: <see cref="VerticalCornerEdges"/>
    /// ищет рёбра, которые вертикальны, стоят в углу и идут на всю толщину. После скругления трёх
    /// углов рёбра ЭТИХ углов на конечном теле исчезают — на их месте цилиндры. Поэтому «свободное
    /// угловое ребро» искалось среди того, чего больше нет. Ошибка была в предпосылке опыта, а не в
    /// отказе признака; сообщение об отказе это и показало (4 ожидалось, 1 найдено).
    /// </item>
    /// </list>
    /// <para>
    /// <b>Один угол даёт чистый опыт.</b> Скругляется ровно один угол (эталон 79980.68583470577),
    /// три остаются свободными. Заменяем предъявленное ребро на ребро ДРУГОГО свободного угла:
    /// размер набора не меняется (1→1), объём не меняется (та же формула), а скругление обязано
    /// переехать с угла A на угол B. Ничего «обязанного пересчитаться» здесь нет: признак с одним
    /// входом, у которого подменили этот вход, либо принял подмену, либо нет.
    /// </para>
    /// <para>
    /// <b>Чем именно измеряется состав.</b> Углы пластины имеют координаты (±50, ±40). Признак
    /// состава — координаты осевых линий цилиндрических граней (<c>CylinderAxisCorners</c>): если
    /// скругление переехало, прежний угол обязан исчезнуть из этого набора, а новый — появиться.
    /// Никакая другая величина здесь этого не покажет.
    /// </para>
    /// <para>
    /// <b>Откуда берётся «свободное» ребро.</b> Обходом конечного тела ПОСЛЕ reopen, тем же отбором,
    /// что и при создании. У скруглённого угла ребра на теле нет — там цилиндр, и отбор по
    /// «прямое + вертикальное + в углу + на всю толщину» его не пропускает. Поэтому у эталона с
    /// одним скруглением обход обязан вернуть РОВНО ТРИ ребра — по числу свободных углов, и это
    /// само по себе проверяется. Запоминать указатели рёбер до создания нельзя: после
    /// save→close→open они мертвы, и предъявление мёртвого указателя было бы опытом о маршалинге,
    /// а не об адресации. Свободное ребро опознаётся по координате угла, а не по позиции в
    /// коллекции: порядок выдачи недетерминирован (измерено, 6 прогонов — 6 порядков).
    /// </para>
    /// </remarks>
    private void SubstituteOneEdge()
    {
        var step = _report.Begin(
            "H2.4",
            "КОНТРОЛЬ: замена одного ребра при неизменном размере набора (1→1)",
            "Признак принимает НАШ состав или пересчитывает то, чем был обязан быть?");

        // Один угол: три остаются свободными, и подмена угла не задевает уже скруглённые рёбра.
        var live = BuildLiveFillet("H2.4", filledCorners: 1);
        if (live is null)
        {
            step.Unknown("Живой признак для контроля не подготовлен — опыт не состоялся.");
            return;
        }

        try
        {
            var count = live.BaseObjectCount;
            if (count != 1)
            {
                step.Unknown($"Входов {count?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, нужен 1 — заменять нечем.");
                return;
            }

            // Углы, которые СКРУГЛЕНЫ сейчас (один), и углы пластины, которых в наборе нет (три).
            var filletedCorners = CylinderAxisCorners(live.Part, step);
            step.Data["corners_rounded_before"] = filletedCorners;
            if (filletedCorners.Count != 1)
            {
                step.Unknown($"Скруглённых углов прочитано {filletedCorners.Count}, ожидался 1 — контроль несостоятелен.");
                return;
            }

            var allCorners = new[] { "(-50,-40)", "(-50,40)", "(50,-40)", "(50,40)" };
            var free = allCorners.Where(c => !filletedCorners.Contains(c)).ToArray();
            step.Data["corners_free_before"] = free;
            if (free.Length != 3)
            {
                step.Unknown($"Свободных углов {free.Length}, ожидалось 3 — заменять нечем.");
                return;
            }

            // Свободное ребро берётся С ТЕЛА ПОСЛЕ REOPEN, обходом того же отбора, что и при
            // создании. Почему не «запомнить ребро до создания»: указатели на объекты прежнего
            // документа после save→close→open мертвы, и предъявлять признаку мёртвый указатель —
            // это опыт о маршалинге, а не об адресации. Почему не «обход всех четырёх»: у
            // скруглённого угла ребра на теле уже нет (на его месте цилиндр), поэтому у эталона с
            // ОДНИМ скруглением обход обязан вернуть ТРИ ребра — по числу свободных углов.
            var plateCorners = VerticalCornerEdges(live.Part, step);
            step.Data["free_corner_edges_on_reopened_body"] = plateCorners.Count;
            if (plateCorners.Count != 3)
            {
                step.Unknown($"Свободных угловых рёбер на теле после reopen {plateCorners.Count}, ожидалось 3 " +
                             "(четыре угла минус один скруглённый) — заменять нечем.");
                return;
            }

            // Какое из рёбер свободно — определяем по координатам, а не по позиции в коллекции:
            // порядок выдачи недетерминирован (измерено, 6 прогонов — 6 порядков). Берётся ПЕРВОЕ
            // свободное; какой именно угол попал в замену, записывается.
            var foreign = plateCorners.FirstOrDefault(e => CornerOf(e) is string corner && free.Contains(corner));
            var foreignCorner = foreign is null ? null : CornerOf(foreign);
            if (foreign is null || foreignCorner is null)
            {
                step.Unknown("Свободное угловое ребро не опознано по координатам — замена не состоялась.");
                return;
            }

            step.Data["substituting_corner"] = foreignCorner;

            // ── Заменяем ОДИН вход: предъявляем ребро свободного угла вместо прежнего. Размер
            //    набора НЕ меняется (1→1), объём НЕ меняется — меняется только состав. Именно это
            //    и отличает адресацию от пересчёта по подставленному входу.
            object? raw;
            try
            {
                raw = live.Fillet.BaseObjects;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                step.Data["read_failure"] = HResult.Describe(ex);
                step.Unknown("BaseObjects не перечитался перед заменой: " + ex.Message);
                return;
            }

            // Какой именно угол СНИМАЕТСЯ: тот, что скруглён сейчас (он же — единственный вход).
            var droppedCorner = filletedCorners.Count == 1 ? filletedCorners[0] : null;
            step.Data["dropped_corner"] = droppedCorner ?? "не опознан";

            var transferred = ToApi7(step, foreign);
            if (transferred is null)
            {
                step.Unknown("Свободное ребро не перенеслось в API7 — заменять нечем.");
                return;
            }

            var replacement = new[] { transferred };
            step.Data["requested_size"] = replacement.Length;
            step.Data["source_count"] = count;

            var volumeBefore = Api5.Volume(live.Part);
            step.Data["volume_before"] = Api5.Num(volumeBefore);

            try
            {
                live.Fillet.BaseObjects = replacement;
                step.Data["write"] = "ok";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["write"] = HResult.Describe(ex);
                step.Unknown("Запись замены отвергнута: " + ex.Message);
                return;
            }

            var updated = Applied(() => live.Fillet.Update());
            step.Data["update"] = updated;
            Rebuild(live);

            var volumeAfter = Api5.Volume(live.Part);
            var cornersAfter = CylinderAxisCorners(live.Part, step);
            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["corners_rounded_after"] = cornersAfter;

            // Эталон при одном углу. И до, и после замены он ОДИН И ТОТ ЖЕ: объём при 1→1
            // не различает ничего — в этом и смысл опыта.
            var expected1 = PlateVolume - 1 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            step.Data["expected_one_corner"] = Api5.Num(expected1);

            // Исчез ли из состава угол, который мы НЕ предъявляли?
            var removed = filletedCorners.Where(c => !cornersAfter.Contains(c)).ToArray();
            var added = cornersAfter.Where(c => !filletedCorners.Contains(c)).ToArray();
            step.Data["corners_left"] = removed;
            step.Data["corners_joined"] = added;

            var volumeHeld = Matches(volumeAfter, expected1);
            var sizeHeld = cornersAfter.Count == filletedCorners.Count;
            var foreignArrived = cornersAfter.Contains(foreignCorner!);
            var droppedLeft = droppedCorner is null || !cornersAfter.Contains(droppedCorner);
            var compositionChanged = removed.Length > 0 || added.Length > 0;

            step.Observe($"Состав: было {string.Join(", ", filletedCorners)}; стало {string.Join(", ", cornersAfter)}. " +
                         $"Предъявлено вместо {droppedCorner ?? "?"}: {foreignCorner}. " +
                         $"Ушли: {string.Join(", ", removed)}; пришли: {string.Join(", ", added)}. " +
                         $"Объём: {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfter)} " +
                         $"(набор из одного угла даёт {Api5.Num(expected1)} при ЛЮБОМ углу — объём здесь не различает).");

            if (volumeHeld && sizeHeld && foreignArrived && droppedLeft && compositionChanged)
            {
                step.Pass($"Замена ПРИМЕНЯЕТСЯ: состав изменился при неизменном размере набора — скругление " +
                          $"переехало на угол {foreignCorner}, а угол {droppedCorner}, который мы сняли, ушёл. " +
                          "Это не пересчёт по подставленному входу: признак принял именно НАШ состав.");
            }
            else if (!compositionChanged && volumeHeld)
            {
                step.Fail("Состав НЕ изменился при неизменном размере набора: признак удержал прежний угол и " +
                          "проигнорировал предъявленное ребро. Значит в опыте 4→3 он пересчитался по СВОЕМУ " +
                          "составу, а не по нашему — маршрут адресации этим не доказан.");
            }
            else if (volumeHeld && sizeHeld && !foreignArrived)
            {
                step.Fail($"Объём удержан, размер набора прежний, но предъявленный угол {foreignCorner} НЕ " +
                          $"скруглён: состав изменился не туда — пришли {string.Join(", ", added)}, " +
                          $"ушли {string.Join(", ", removed)}.");
            }
            else if (!volumeHeld)
            {
                step.Fail($"Набор не удержал один угол: объём {Api5.Num(volumeAfter)} против эталона " +
                          $"{Api5.Num(expected1)}.");
            }
            else
            {
                step.Fail($"Замена не состоялась по составу: скруглённых углов после записи {cornersAfter.Count} " +
                          $"при прежнем размере {filletedCorners.Count}; ушли {string.Join(", ", removed)}, " +
                          $"пришли {string.Join(", ", added)}, ожидалось прибытие {foreignCorner}.");
            }
        }
        finally
        {
            Close(live.Doc);
        }
    }

    /// <summary>
    /// <b>Повторяемость маршрута на свежем документе.</b> Задание §2 требует опыта с расширением, но
    /// чистого расширения НАБОРА на этом эталоне не построить: у пластины ровно четыре вертикальных
    /// угла, и все четыре уже скруглены при r=3. Поэтому здесь проверяется то, что осмысленно, —
    /// <b>второй независимый прогон сокращения на своём документе</b>: маршрут обязан
    /// воспроизводиться, а не сработать один раз.
    /// </summary>
    /// <remarks>
    /// Подмена не выдаётся за выполнение требования: опыт с расширением НАБОРА остаётся невыполненным
    /// на этом эталоне, и это записывается явно. Объявлять расширение измеренным по этому опыту
    /// нельзя.
    /// </remarks>
    private void EnlargeUsingBaseObjects()
    {
        var step = _report.Begin(
            "H2.5",
            "Повторяемость маршрута на свежем документе (расширение набора невыполнимо)",
            "Воспроизводится ли маршрут, или он сработал один раз?");

        step.Observe("Расширение набора НА ЭТОМ ЭТАЛОНЕ невыполнимо: у пластины 100×80×10 ровно четыре " +
                     "вертикальных угловых ребра, и все четыре уже в наборе при r=3 по всем углам. " +
                     "Опыт «4→5» потребовал бы другого эталона и был бы несравним с числами этой серии. " +
                     "Здесь измеряется то, что доступно, — повторяемость уже найденного маршрута.");

        // Исходный набор — четыре угла: повтор проверяет ДРУГОЕ сокращение, чем H2.3 (тот сокращал
        // с четырёх до трёх). Два одинаковых опыта не различили бы «маршрут работает» от
        // «сработал один раз».
        var live = BuildLiveFillet("H2.5");
        if (live is null)
        {
            step.Unknown("Живой признак для повтора не подготовлен — опыт не состоялся.");
            return;
        }

        try
        {
            var count = live.BaseObjectCount;
            if (count != 4)
            {
                step.Unknown($"Входов {count?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, нужно 4.");
                return;
            }

            object? raw;
            try
            {
                raw = live.Fillet.BaseObjects;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                step.Data["read_failure"] = HResult.Describe(ex);
                step.Unknown("BaseObjects не перечитался: " + ex.Message);
                return;
            }

            // Сокращение 4→2: иная величина, чем в H2.3, поэтому и эталон иной.
            const int keep = 2;
            var subset = new List<object>();
            for (var i = 0; i < keep; i++)
            {
                if (ElementAt(raw, i) is object element)
                {
                    subset.Add(element);
                }
            }

            step.Data["requested_subset"] = subset.Count;
            step.Data["source_count"] = count;

            try
            {
                live.Fillet.BaseObjects = subset.ToArray();
                step.Data["write"] = "ok";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["write"] = HResult.Describe(ex);
                step.Unknown("Запись отвергнута на повторе: " + ex.Message);
                return;
            }

            step.Data["update"] = Applied(() => live.Fillet.Update());
            Rebuild(live);

            var volumeAfter = Api5.Volume(live.Part);
            var expected2 = PlateVolume - keep * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["expected_2_corners"] = Api5.Num(expected2);
            step.Data["expected_4_corners"] = Api5.Num(expected4);

            step.Observe($"Объём {Api5.Num(volumeAfter)}: набор из {keep} дал бы {Api5.Num(expected2)}, " +
                         $"прежний из четырёх — {Api5.Num(expected4)}.");

            if (Matches(volumeAfter, expected2))
            {
                step.Pass($"Маршрут воспроизвёлся на свежем документе при ДРУГОМ сокращении: объём {Api5.Num(volumeAfter)} = эталон двух рёбер.");
            }
            else if (Matches(volumeAfter, expected4))
            {
                step.Fail("На повторе объём остался на четырёх рёбрах: маршрут сработал один раз и не воспроизводится.");
            }
            else
            {
                step.Fail($"На повторе объём {Api5.Num(volumeAfter)} не совпал ни с эталоном {keep} рёбер " +
                          $"({Api5.Num(expected2)}), ни с прежним ({Api5.Num(expected4)}): маршрут невоспроизводим.");
            }
        }
        finally
        {
            Close(live.Doc);
        }
    }

    /// <summary>
    /// <b>Адресация на модели с ДВУМЯ скруглениями одного радиуса.</b> Обязательный критерий приёмки:
    /// меняется только выбранный признак. Один признак без соседа не различает «адресация» и
    /// «единственный кандидат» — на модели с одним скруглением любой маршрут выглядит работающим.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему эталон иной, а не четыре угла.</b> Нужны ДВА независимых признака одного радиуса на
    /// одной пластине. Из четырёх вертикальных углов делаются два скругления: первое по ОДНОМУ углу,
    /// второе — по ДВУМ. Радиус у обоих R3, документ один. Разные размеры наборов нужны для
    /// различимости: у признака с одним входом снятие входа означало бы опустошение набора, и это
    /// другой опыт.
    /// </para>
    /// <para>
    /// <b>Что здесь измеряется.</b> Не объём (он симметричен), а РАСПРЕДЕЛЕНИЕ скруглений по углам:
    /// какие углы заняты до записи и какие после. Мутируется признак с ДВУМЯ входами, признак с
    /// одним входом служит СВИДЕТЕЛЕМ: если он изменится — адресация не найдена. Если изменился
    /// только мутируемый — адресация по признаку подтверждена.
    /// </para>
    /// <para>
    /// <b>Почему это не тот же опыт, что H2.4.</b> H2.4 держит ОДИН признак и меняет в нём угол:
    /// он различает «принял наш состав» от «пересчитал по своему». Здесь проверяется, что маршрут
    /// не путает ДВА признака. Два разных вопроса.
    /// </para>
    /// <para>
    /// <b>Почему соответствие признаков не берётся по индексу.</b> Задание прямо запрещает
    /// сопоставлять произвольные скругления по позиции в коллекции. Каждый признак опознаётся по
    /// СВОИМ входным рёбрам: угол входного ребра читается через перенос в API7, и по нему признак
    /// получает имя. Тот же приём применяется к свидетелю при перечитывании.
    /// </para>
    /// </remarks>
    private void AddressAmongTwoFillets()
    {
        var step = _report.Begin(
            "H2.7",
            "АДРЕСАЦИЯ: два скругления R3 на одной пластине — меняется только выбранное",
            "Не задевает ли запись во входы одного признака соседний признак того же радиуса?");

        var live = BuildLiveTwoFillets("H2.7");
        if (live is null)
        {
            step.Unknown("Модель с двумя скруглениями не подготовлена — опыт не состоялся.");
            return;
        }

        try
        {
            var filletCount = Api5.SafeInt(() => live.Container.Fillets.Count);
            step.Data["fillets_count"] = filletCount;
            if (filletCount != 2)
            {
                step.Unknown($"Признаков в контейнере {filletCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, ожидалось 2.");
                return;
            }

            if (live.Container.Fillets[0] is not IFillet a || live.Container.Fillets[1] is not IFillet b)
            {
                step.Unknown("Не оба элемента Fillets отдают IFillet — адресовать нечем.");
                return;
            }

            // ── Какая запись какому признаку соответствует — НЕ предполагается по индексу.
            //    Задание прямо запрещает сопоставлять произвольные скругления по позиции в
            //    коллекции. Поэтому каждый признак опознаётся по СВОИМ входным рёбрам.
            //
            //    Первая версия опознания читала угол входного элемента через CornerOfApi7, который
            //    переносил элемент в API7 и приводил к ksEntity. Он молча возвращал null на КАЖДОМ
            //    элементе (fillets_0_corners=[], fillets_1_corners=[]), и вердикт падал в последнюю
            //    ветку — то есть отказ идентификации выглядел как «адресация не подтверждена».
            //    Поэтому здесь сначала ИЗМЕРЯЕТСЯ, какой маршрут чтения элемента вообще работает,
            //    и результат записывается, а не подразумевается.
            var aInputs = InputsOf(a, step, "a");
            var bInputs = InputsOf(b, step, "b");
            if (aInputs.Count == 0 || bInputs.Count == 0)
            {
                step.Unknown("Один из признаков не отдал входы через BaseObjects — адресовать нечем.");
                return;
            }

            // Опознание по устойчивому идентификатору IModelObject.Reference — тому самому, что уже
            // измерен в H2.2 (Reference=1073741872…75, Type=ksObjectEdge). Он не зависит от порядка
            // выдачи коллекции, в отличие от индекса.
            var aRefs = RefsOfInputs(aInputs);
            var bRefs = RefsOfInputs(bInputs);
            step.Data["fillets_0_input_refs"] = aRefs;
            step.Data["fillets_1_input_refs"] = bRefs;
            step.Data["fillets_0_input_count"] = aInputs.Count;
            step.Data["fillets_1_input_count"] = bInputs.Count;

            // ── ВАЖНОЕ ИЗМЕРЕНИЕ, закрывающее один путь опознания. Ссылки входов признака НЕ равны
            //    ссылкам рёбер конечного тела: у входов 1073742065–2067, у единственного уцелевшего
            //    углового ребра тела — 1073742080. Это разные объекты в разных контекстах, и
            //    связать вход с углом по Reference НЕЛЬЗЯ. Измерено, а не предположено: первая
            //    версия опознания на этом и остановилась, вернув 0 и 0.
            //
            //    Поэтому состав признака измеряется НЕ через углы тела, а через его СОБСТВЕННЫЕ
            //    входы: их ссылки устойчивы и перечитываются. С углами тела сверяется лишь
            //    количество и распределение скруглений — через CylinderAxisCorners, который
            //    читается с граней, а не с входов.
            var bodyCornerEdges = VerticalCornerEdges(live.Part, step);
            step.Data["body_free_corner_edges"] = bodyCornerEdges.Count;
            step.Data["body_corner_refs"] = bodyCornerEdges
                .Select(e => ReferenceOf(e) is int r && CornerOf(e) is string c ? $"{r}={c}" : null)
                .Where(s => s is not null)
                .ToList();

            if (aRefs.Count == 0 || bRefs.Count == 0)
            {
                step.Unknown("Входы признаков не отдали устойчивых ссылок — состав перечитывать нечем.");
                return;
            }

            // Радиусы обязаны совпадать: опыт про адресацию ПРИ ОДИНАКОВОМ радиусе — иначе
            // сопоставление шло бы по радиусу, а это как раз то упрощение, которое задание
            // запрещает переносить в продукт.
            var aRadius = ReadRadius(a, step, "a");
            var bRadius = ReadRadius(b, step, "b");
            step.Data["fillets_0_radius"] = aRadius;
            step.Data["fillets_1_radius"] = bRadius;
            if (aRadius is null || bRadius is null || Math.Abs(aRadius.Value - bRadius.Value) > 1e-9)
            {
                step.Unknown("Радиусы признаков не совпали или не прочитаны — опыт не различает адресацию.");
                return;
            }

            // Мутируется признак с ДВУМЯ входами. Одноугольный не трогается и служит свидетелем:
            // если он всё-таки изменится, значит запись задела не тот объект.
            var (victim, victimInputs, victimTag, witness, witnessInputs, witnessTag) =
                aInputs.Count >= 2
                    ? (a, aInputs, "Fillets[0]", b, bInputs, "Fillets[1]")
                    : bInputs.Count >= 2
                        ? (b, bInputs, "Fillets[1]", a, aInputs, "Fillets[0]")
                        : (null, null, null, null, null, null);

            if (victim is null || victimInputs is null || witness is null || witnessInputs is null)
            {
                step.Unknown($"Ни у одного признака нет двух входов ({aInputs.Count} и {bInputs.Count}) — " +
                             "снять один вход, не опустошая набор, нечем.");
                return;
            }

            step.Data["mutated_fillet"] = victimTag;
            step.Data["witness_fillet"] = witnessTag;
            step.Data["mutated_inputs_before"] = victimInputs.Count;
            step.Data["witness_inputs_before"] = witnessInputs.Count;

            var victimRefsBefore = RefsOfInputs(victimInputs);
            var witnessRefsBefore = RefsOfInputs(witnessInputs);
            step.Data["mutated_refs_before"] = victimRefsBefore;
            step.Data["witness_refs_before"] = witnessRefsBefore;

            // Состав каждого признака ДО записи — по его собственным входам.
            var cornersBefore = CylinderAxisCorners(live.Part, step);
            step.Data["corners_rounded_before_total"] = cornersBefore;

            var volumeBefore = Api5.Volume(live.Part);
            step.Data["volume_before"] = Api5.Num(volumeBefore);

            var reduced = victimInputs.Take(victimInputs.Count - 1).ToList();
            step.Data["written_to_mutated"] = reduced.Count;

            try
            {
                victim.BaseObjects = reduced.ToArray();
                step.Data["write"] = "ok";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["write"] = HResult.Describe(ex);
                step.Unknown("Запись во входы выбранного признака отвергнута: " + ex.Message +
                             " — отказ записи, а не «задело соседа».");
                return;
            }

            step.Data["update"] = Applied(() => victim.Update());
            Rebuild(live);

            var volumeAfter = Api5.Volume(live.Part);
            var cornersAfter = CylinderAxisCorners(live.Part, step);
            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["corners_rounded_after_total"] = cornersAfter;

            var lost = cornersBefore.Where(c => !cornersAfter.Contains(c)).ToArray();
            var gained = cornersAfter.Where(c => !cornersBefore.Contains(c)).ToArray();
            step.Data["corners_lost"] = lost;
            step.Data["corners_gained"] = gained;

            // Свидетель перечитывается: его входы обязаны остаться ровно теми же — по ссылкам.
            var witnessAfter = InputsOf(witness, step, "witness_after");
            var witnessRefsAfter = RefsOfInputs(witnessAfter);
            step.Data["witness_inputs_after"] = witnessAfter.Count;
            step.Data["witness_refs_after"] = witnessRefsAfter;

            var witnessUntouched =
                witnessAfter.Count == witnessInputs.Count &&
                witnessRefsBefore.OrderBy(r => r).SequenceEqual(witnessRefsAfter.OrderBy(r => r));
            step.Data["witness_untouched"] = witnessUntouched;

            // Мутируемый признак перечитывается: из его набора обязан уйти ровно один вход.
            var victimAfter = InputsOf(victim, step, "mutated_after");
            var victimRefsAfter = RefsOfInputs(victimAfter);
            step.Data["mutated_inputs_after"] = victimAfter.Count;
            step.Data["mutated_refs_after"] = victimRefsAfter;

            // Ожидание: с тела ушёл ровно один угол, объём — эталон двух углов.
            var expectedAfter = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var volumeMatchesTwoCorners = Matches(volumeAfter, expectedAfter);
            var lostOneCorner = lost.Length == 1 && gained.Length == 0;
            var victimLostExactlyOne = victimRefsAfter.Count == victimRefsBefore.Count - 1;

            step.Observe($"Мутируемый: {victimTag}, входов {victimInputs.Count}→{victimAfter.Count} " +
                         $"(ссылки {string.Join(",", victimRefsBefore)} → {string.Join(",", victimRefsAfter)}). " +
                         $"Свидетель: {witnessTag}, входов {witnessInputs.Count}→{witnessAfter.Count} " +
                         $"(ссылки {string.Join(",", witnessRefsBefore)} → {string.Join(",", witnessRefsAfter)}). " +
                         $"С тела ушли: {string.Join(", ", lost)}; пришли: {string.Join(", ", gained)}. " +
                         $"Объём: {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfter)}.");

            if (witnessUntouched && victimLostExactlyOne && lostOneCorner && volumeMatchesTwoCorners)
            {
                step.Pass($"Адресация подтверждена: запись во входы {victimTag} сняла ровно один ЕГО вход " +
                          $"({victimRefsBefore.Count}→{victimRefsAfter.Count}) и НЕ задела {witnessTag} того же " +
                          "радиуса — ссылки свидетеля совпали до и после. С тела ушёл ровно один угол.");
            }
            else if (!witnessUntouched)
            {
                step.Fail($"Запись задела НЕ только выбранный признак: свидетель {witnessTag} изменился " +
                          $"({witnessInputs.Count}→{witnessAfter.Count} входов; ссылки " +
                          $"{string.Join(",", witnessRefsBefore)} → {string.Join(",", witnessRefsAfter)}). " +
                          "На модели с двумя скруглениями одного радиуса это неоднозначная адресация — " +
                          "переносить в продукт без защиты нельзя.");
            }
            else if (!victimLostExactlyOne)
            {
                step.Fail($"Сам {victimTag} изменился не так, как предъявлено: входов " +
                          $"{victimRefsBefore.Count}→{victimRefsAfter.Count}, ожидалось −1.");
            }
            else if (lost.Length > 1 || gained.Length > 0)
            {
                step.Fail($"С тела ушло не то, что предъявлено: ушли {string.Join(", ", lost)}, " +
                          $"пришли {string.Join(", ", gained)}; ожидалось снятие одного угла.");
            }
            else if (!volumeMatchesTwoCorners)
            {
                step.Fail($"Геометрия не соответствует снятию одного угла: объём {Api5.Num(volumeAfter)}, " +
                          $"ожидался {Api5.Num(expectedAfter)}.");
            }
            else
            {
                step.Fail($"Адресация не подтверждена: ушли {string.Join(", ", lost)}, пришли " +
                          $"{string.Join(", ", gained)}.");
            }
        }
        finally
        {
            Close(live.Doc);
        }
    }

    /// <summary>
    /// Устойчивые идентификаторы <c>IModelObject.Reference</c> входных элементов признака. Не зависят
    /// от порядка выдачи коллекции, в отличие от индекса, и перечитываются после мутации — поэтому
    /// именно они служат мерой СОСТАВА признака в опыте адресации.
    /// </summary>
    /// <remarks>
    /// <b>Чего эти ссылки НЕ делают.</b> Они не связывают вход признака с углом конечного тела:
    /// измерено, что у входов ссылки 1073742065–2067, а у уцелевшего углового ребра тела —
    /// 1073742080. Это разные объекты в разных контекстах; попытка связать их по Reference дала
    /// 0 и 0 и остановила первую версию опыта. Границу полезно помнить: одна и та же ссылка
    /// осмысленна внутри своего контекста и не переносится в чужой.
    /// </remarks>
    private static List<int> RefsOfInputs(IReadOnlyList<object> inputs)
    {
        var refs = new List<int>();
        foreach (var input in inputs)
        {
            if (TryQi<IModelObject>(input) is IModelObject modelObject)
            {
                try
                {
                    refs.Add(modelObject.Reference);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // Элемент без читаемой ссылки пропускается: это ответ, а не падение.
                }
            }
        }

        return refs;
    }

    /// <summary>Устойчивая ссылка ребра тела, полученного как <c>ksEntity</c> (API5-объект).</summary>
    private int? ReferenceOf(ksEntity edge)
    {
        try
        {
            var transferred = _app.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is IModelObject modelObject)
            {
                return modelObject.Reference;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Входы признака через <c>BaseObjects</c>: объекты, пригодные к предъявлению обратно.</summary>
    private static List<object> InputsOf(IFillet fillet, ProbeStep step, string tag)
    {
        var inputs = new List<object>();
        object? raw;
        try
        {
            raw = fillet.BaseObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data[$"{tag}_read_failure"] = HResult.Describe(ex);
            return inputs;
        }

        var length = raw is null ? null : LengthOf(raw);
        if (length is not int count)
        {
            step.Data[$"{tag}_type"] = raw is null ? "null" : Api5.RuntimeName(raw);
            return inputs;
        }

        step.Data[$"{tag}_type"] = Api5.RuntimeName(raw!);
        for (var i = 0; i < count; i++)
        {
            if (ElementAt(raw!, i) is object element)
            {
                inputs.Add(element);
            }
        }

        return inputs;
    }

    /// <summary>
    /// Радиус признака в API7: <c>IFillet.Radius1</c> — измеренный член (dispid 3, см.
    /// <c>Api7Bridge.Api7Fillet.Read</c>). Нужен, чтобы убедиться, что два признака действительно
    /// одного радиуса, — иначе опыт про адресацию выродился бы в сопоставление по радиусу, которое
    /// задание прямо запрещает переносить в продукт. Первая версия обращалась к <c>IFillet.Radius</c>:
    /// такого члена нет, компилятор это подтвердил.
    /// </summary>
    private static double? ReadRadius(IFillet fillet, ProbeStep step, string tag)
    {
        try
        {
            return fillet.Radius1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data[$"{tag}_radius_failure"] = HResult.Describe(ex);
            return null;
        }
    }

    /// <summary>
    /// <b>Сверка с пробой H на числах ЭТОГО прогона.</b> Не пересказ и не ссылка на документ:
    /// метод пересчитывает, следует ли из ТОЛЬКО ЧТО полученного утверждение, которое приписывалось
    /// пробе H, и в каком месте это утверждение перестаёт быть следствием.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задание §137 требует не переписывать историю задним числом: результат этой пробы имеет
    /// собственный <c>run_id</c>, а к старому добавляется точное пояснение. Поэтому сверка здесь
    /// именно СЧИТАЕТСЯ: она берёт числа опытов 4→3 и 1→1 из этого же отчёта и проверяет
    /// различающую способность каждого по отдельности.
    /// </para>
    /// <para>
    /// Проверяемое утверждение: «запись <c>BaseObjects</c> применяется к существующему признаку».
    /// Опыт 4→3 его НЕ подтверждает, даже когда объём точно попал в эталон трёх углов: признак,
    /// у которого сняли одно ребро, обязан пересчитаться так же и в том случае, если он наши
    /// входы не разобрал вовсе. Опыт 1→1 подтверждает, но не объёмом, а составом: при неизменном
    /// размере набора объём до и после ОДИН И ТОТ ЖЕ, и единственное, что различается, — какой
    /// угол скруглён.
    /// </para>
    /// <para>
    /// Отсюда и граница: если опыт 1→1 не показал смены состава, то опыт 4→3 следует читать как
    /// «пересчёт по своему составу», а маршрут адресации — как НЕ найденный. Именно эта связка
    /// делает сверку содержательной, а не декоративной.
    /// </para>
    /// </remarks>
    private void ReconcileWithProbeH()
    {
        var step = _report.Begin(
            "H2.6",
            "Сверка с пробой H: что именно из доказанного следует",
            "Подтверждает ли опыт 4→3 запись входов, или для этого нужен опыт 1→1?");

        var reduce = FindStep("H2.3");
        var substitute = FindStep("H2.4");
        if (reduce is null || substitute is null)
        {
            step.Unknown("Опытов H2.3/H2.4 в отчёте нет — сверять не с чем.");
            return;
        }

        var reduceApplied = reduce.Verdict == Verdict.Pass;
        var substituteChangedComposition = substitute.Verdict == Verdict.Pass;

        step.Data["reduce_4_to_3_alone_sufficient"] = false;
        step.Data["reduce_4_to_3_verdict"] = reduce.Verdict.ToString();
        step.Data["substitute_1_to_1_verdict"] = substitute.Verdict.ToString();

        // Числа обеих сторон — из ЭТОГО прогона, а не из документа пробы H.
        step.Data["this_run_expected_3_corners"] = Api5.Num(ExpectedCorners(3));
        step.Data["this_run_expected_1_corner"] = Api5.Num(ExpectedCorners(1));
        step.Data["this_run_reduce_volume_after"] = reduce.Data.TryGetValue("volume_after", out var v3) ? v3 : null;
        step.Data["this_run_substitute_volume_before"] = substitute.Data.TryGetValue("volume_before", out var vb) ? vb : null;
        step.Data["this_run_substitute_volume_after"] = substitute.Data.TryGetValue("volume_after", out var va) ? va : null;
        step.Data["this_run_substitute_corners_before"] = substitute.Data.TryGetValue("corners_rounded_before", out var cb) ? cb : null;
        step.Data["this_run_substitute_corners_after"] = substitute.Data.TryGetValue("corners_rounded_after", out var ca) ? ca : null;

        var volumeUnchangedInSubstitute =
            substitute.Data.TryGetValue("volume_before", out var before) &&
            substitute.Data.TryGetValue("volume_after", out var after) &&
            before is not null && after is not null &&
            string.Equals(before.ToString(), after.ToString(), StringComparison.Ordinal);
        step.Data["substitute_volume_literally_unchanged"] = volumeUnchangedInSubstitute;

        if (substituteChangedComposition && reduceApplied)
        {
            step.Pass(
                "Опыт 4→3 объёмом не различает: признак, у которого сняли ребро, ОБЯЗАН дать эталон трёх. " +
                "Различает опыт 1→1 — объём там НЕ меняется (" +
                $"{(volumeUnchangedInSubstitute ? "буквально один и тот же" : "сравнить не удалось")}), " +
                "а состав меняется. Маршрут записи входов подтверждён именно составом, а не объёмом. " +
                "Проба H мерила другое: она подставляла запомненные рёбра до создания признака, и её " +
                "числа — пересчёт по подставленному входу.");
        }
        else if (reduceApplied && !substituteChangedComposition)
        {
            step.Fail(
                "Опыт 4→3 дал эталон трёх, но опыт 1→1 НЕ показал смены состава. Следовательно 4→3 читается " +
                "как пересчёт по СВОЕМУ составу, а запись входов не доказана: маршрут адресации не найден.");
        }
        else
        {
            step.Unknown(
                "Ни сокращение, ни замена не прошли: об адресации входов этим прогоном сказать нечего.");
        }
    }

    /// <summary>Эталон объёма пластины с <paramref name="corners"/> скруглёнными углами при R3.</summary>
    private static double ExpectedCorners(int corners) =>
        PlateVolume - corners * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;

    /// <summary>Шаг уже собранного отчёта по идентификатору — для сверки между опытами.</summary>
    private ProbeStep? FindStep(string id) =>
        _report.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    /// <summary>
    /// Координаты углов, В КОТОРЫХ СТОИТ ЦИЛИНДРИЧЕСКАЯ ГРАНЬ скругления — «какие углы скруглены»,
    /// прочитанное с конечного тела, а не с признака. Это и есть мера СОСТАВА: объём одинаков у
    /// любого набора из четырёх углов, а углы — разные.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сигнатура цилиндрической грани скругления: ось на расстоянии r от угла пластины. Углы
    /// (±50, ±40) при R3 дают оси в (±47, ±37). Источник осевой точки — не выдумка, а измеренный
    /// путь <see cref="Api5.ReadFaces"/>: <c>GetSurface() → ksSurface → GetSurfaceParam() →
    /// ksCylinderParam → GetPlacement() → GetOrigin()</c>. Первая версия этого метода вызывала
    /// несуществующие <c>face.Surface()</c>/<c>ksCylinderSurface</c> — компилятор поймал, и это
    /// ровно тот случай, когда догадка о члене API не должна попадать в замер.
    /// </para>
    /// </remarks>
    private static List<string> CylinderAxisCorners(ksPart part, ProbeStep step)
    {
        var corners = new List<string>();
        foreach (var reading in Api5.CylinderFaces(part))
        {
            var point = reading.CylinderOrigin;
            if (point is null || point.Length < 3)
            {
                step.Observe($"Цилиндрическая грань [{reading.Index}] без читаемого размещения — угол не опознан.");
                continue;
            }

            var x = point[0];
            var y = point[1];

            // Ось скругления отстоит от угла на r по обеим осям. Принимаются только оси решётки
            // (±47, ±37): иначе в «скруглённые углы» попали бы посторонние цилиндры.
            if (Math.Abs(Math.Abs(x) - (PlateWidth / 2d - Radius3)) > 1e-3 ||
                Math.Abs(Math.Abs(y) - (PlateHeight / 2d - Radius3)) > 1e-3)
            {
                continue;
            }

            var sx = Math.Sign(x) * (PlateWidth / 2d);
            var sy = Math.Sign(y) * (PlateHeight / 2d);
            var label = $"({sx.ToString("0.####", CultureInfo.InvariantCulture)},{sy.ToString("0.####", CultureInfo.InvariantCulture)})";
            if (!corners.Contains(label))
            {
                corners.Add(label);
            }
        }

        return corners;
    }

    /// <summary>Координатный ярлык углового ребра: «(50,-40)» и т. п.</summary>
    private static string? CornerOf(ksEntity edge)
    {
        try
        {
            if (edge.GetDefinition() is not ksEdgeDefinition definition)
            {
                return null;
            }

            return CornerOfDefinition(definition);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Координатный ярлык угла по определению ребра — общий для ребра тела и для ребра, добытого
    /// иным путём. Вызывается только там, где определение уже получено проверенным маршрутом.
    /// </summary>
    private static string? CornerOfDefinition(ksEdgeDefinition definition)
    {
        if (definition.GetVertex(true) is not ksVertexDefinition v0 || definition.GetVertex(false) is not ksVertexDefinition v1
            || !v0.GetPoint(out var ax, out var ay, out _) || !v1.GetPoint(out var bx, out var by, out _))
        {
            return null;
        }

        var x = (ax + bx) / 2d;
        var y = (ay + by) / 2d;
        return $"({x.ToString("0.####", CultureInfo.InvariantCulture)},{y.ToString("0.####", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Перенос ребра в API7 тем же способом, каким это делает проба H.</summary>
    private object? ToApi7(ProbeStep step, ksEntity edge)
    {
        try
        {
            var transferred = _app.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is null)
            {
                step.Data["transfer_edge"] = "null";
            }

            return transferred;
        }
        catch (Exception ex)
        {
            step.Data["transfer_edge"] = HResult.Describe(ex);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════ механика ══

    /// <summary>
    /// Пластина 100×80×10, четыре вертикальных угла R3. Эталон: 79922.74333882307.
    /// Возвращается созданный признак — он нужен ТОЛЬКО для дальнейшего сохранения документа.
    /// Перестроение делает вызывающий: у <c>ksPart</c> нет члена <c>Document</c>, а документ у него
    /// есть (это выяснилось компилятором, а не догадкой).
    /// </summary>
    private static ksEntity? CreateFillet(ProbeStep step, ksPart part, IReadOnlyList<ksEntity> edges, double radius)
    {
        var feature = (ksEntity)part.NewEntity(Fillet);
        if (feature.GetDefinition() is not ksFilletDefinition definition)
        {
            step.Fail("Определение скругления не отдало ksFilletDefinition.");
            return null;
        }

        definition.radius = radius;
        definition.tangent = false;
        if (definition.array() is not ksEntityCollection array)
        {
            step.Fail("array() не отдал ksEntityCollection.");
            return null;
        }

        var added = 0;
        foreach (var edge in edges)
        {
            if (array.Add(edge))
            {
                added++;
            }
        }

        if (added != edges.Count || !feature.Create())
        {
            step.Fail($"Скругление не создано: рёбер принято {added} из {edges.Count}.");
            return null;
        }

        return feature;
    }

    /// <summary>
    /// Четыре вертикальных угловых ребра конечного тела — критерий отбора ВЗЯТ ИЗ ПРОБЫ H БЕЗ
    /// ИЗМЕНЕНИЙ (<c>Api5.SafeBool(edge.IsStraight)</c> + вершины + проверка «вертикально, в углу,
    /// на всю толщину»). Он уже измерен рабочим там, и второй, «свой» отбор дал бы второй эталон,
    /// несравнимый с прежними числами.
    /// </summary>
    private static List<ksEntity> VerticalCornerEdges(ksPart part, ProbeStep step)
    {
        var chosen = new List<ksEntity>();
        var seen = new HashSet<IntPtr>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("Тело или коллекция граней недоступны — рёбер не видно.");
            return chosen;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (faces.GetByIndex(f) is not object faceObject)
            {
                continue;
            }

            var face = faceObject as ksFaceDefinition
                       ?? ((faceObject as ksEntity)?.GetDefinition() as ksFaceDefinition);
            if (face?.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var edgeObject = edges.GetByIndex(e);
                var edge = edgeObject as ksEdgeDefinition
                           ?? ((edgeObject as ksEntity)?.GetDefinition() as ksEdgeDefinition);
                if (edge is null || Api5.SafeBool(edge.IsStraight) != true)
                {
                    continue;
                }

                if (edge.GetVertex(true) is not ksVertexDefinition v0 || edge.GetVertex(false) is not ksVertexDefinition v1
                    || !v0.GetPoint(out var ax, out var ay, out var az) || !v1.GetPoint(out var bx, out var by, out var bz))
                {
                    continue;
                }

                var vertical = Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
                var corner = Math.Abs(Math.Abs(ax) - PlateWidth / 2d) < 1e-3 && Math.Abs(Math.Abs(ay) - PlateHeight / 2d) < 1e-3;
                var spans = Math.Abs(Math.Max(az, bz) - PlateThickness) < 1e-3 && Math.Abs(Math.Min(az, bz)) < 1e-3;
                if (!(vertical && corner && spans))
                {
                    continue;
                }

                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity ?? edge.GetOwnerEntity() as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                var pointer = Marshal.GetIUnknownForObject(entity);
                try
                {
                    if (seen.Add(pointer))
                    {
                        chosen.Add(entity);
                    }
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
        }

        return chosen;
    }

    /// <summary>
    /// Длина набора, каким бы типом он ни вернулся. Различение обязательно: <c>object[]</c> — лишь
    /// один из вариантов, и несовпадение с ним НЕ означает пустоту (задание §2).
    /// </summary>
    private static int? LengthOf(object raw) => raw switch
    {
        object[] array => array.Length,
        Array array => array.Length,
        _ => null,
    };

    /// <summary>Элемент набора по индексу — с учётом того, что это может быть любой <see cref="Array"/>.</summary>
    private static object? ElementAt(object raw, int index) => raw switch
    {
        object[] array when index < array.Length => array[index],
        Array array when index < array.Length => array.GetValue(index),
        _ => null,
    };

    /// <summary>Смысл элемента как ребра: отвечает ли он на интерфейсы, которыми признак принимает входы.</summary>
    private static string DescribeEdge(object element)
    {
        var asModelObject = TryQi<IModelObject>(element);
        if (asModelObject is null)
        {
            return "не IModelObject";
        }

        try
        {
            var index = asModelObject.Reference;
            var type = asModelObject.Type;
            return $"Reference={index}, Type={type}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "IModelObject без читаемых Reference/Type";
        }
    }

    /// <summary>
    /// QI на объект: то, что элемент вернулся из набора, не означает, что он отвечает интерфейсу
    /// (измерено на вращениях: обычное приведение не работает, работает QI — см. R.22).
    /// </summary>
    private static T? TryQi<T>(object element) where T : class
    {
        try
        {
            return element as T;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return null;
        }
    }

    private IModelContainer? Container(ProbeStep step, ksDocument3D doc)
    {
        try
        {
            var document7 = _app.TransferInterface(doc, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            var container = document7?.TopPart as IModelContainer;
            step.Data["qi_imodelcontainer"] = container is not null;
            if (container is null)
            {
                step.Unknown("Документ/TopPart не дали IModelContainer.");
            }

            return container;
        }
        catch (Exception ex)
        {
            step.Data["qi_imodelcontainer"] = "исключение: " + ex.Message;
            return null;
        }
    }

    private static void Rebuild(LiveFillet live)
    {
        if (live.Container is IPart7 part7)
        {
            part7.RebuildModel(true);
        }

        live.Doc.RebuildDocument();
    }

    private double Tolerance(double expected) =>
        Math.Max(Math.Abs(expected), 1d) * 8 * 2.2204460492503131e-16;

    private bool Matches(double? measured, double expected) =>
        measured is double m && Math.Abs(m - expected) <= Tolerance(expected);

    /// <summary>
    /// Приводит тройственное состояние к «применилось / не применилось»: <c>null</c> от «не спросили»
    /// здесь означает НЕ «записали», поэтому по умолчанию false.
    /// </summary>
    private static bool Applied(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static void Close(ksDocument3D doc)
    {
        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // Закрытие после падения шага — уборка, а не измерение: осиротевший документ заметен
            // на шаге H2.Z.
        }
    }

    private ProbeStep? Bridge()
    {
        var step = _report.Begin("H2.0", "Мост API5→API7", "Тот же сеанс отдаёт IApplication?");
        try
        {
            _app7 = _app.GetType().InvokeMember(
                "ksGetApplication7", System.Reflection.BindingFlags.InvokeMethod, null, _app, null) as IApplication;
            step.Data["app7"] = _app7 is not null;
            if (_app7 is null)
            {
                step.Fail("ksGetApplication7 не отдал IApplication.");
                return null;
            }

            step.Pass("Мост построен.");
            return step;
        }
        catch (Exception ex)
        {
            step.Data["app7_error"] = ex.Message;
            step.Fail("ksGetApplication7 бросил: " + ex.Message);
            return null;
        }
    }

    private void Launch()
    {
        var step = _report.Begin("H2.1:app", "Свой невидимый экземпляр", null);
        try
        {
            dynamic? raw = Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!);
            _app = (KompasObject)raw!;
            _app.Visible = false;
            step.Data["pid"] = Environment.ProcessId;
            step.Pass("Экземпляр поднят невидимо.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Экземпляр не поднят: " + ex.Message);
        }

        Bridge();
    }

    private void Finish()
    {
        var step = _report.Begin("H2.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app?.Quit();
            step.Data["quit"] = true;
            step.Pass("Сеанс завершён.");
        }
        catch (Exception ex)
        {
            step.Data["quit"] = "исключение: " + ex.Message;
            step.Fail("Сеанс не завершён: " + ex.Message);
        }
    }

    private sealed class LiveFillet
    {
        public required ksDocument3D Doc { get; init; }

        public required ksPart Part { get; init; }

        public required IModelContainer Container { get; init; }

        public required IFillet Fillet { get; init; }

        public int? BaseObjectCount { get; set; }
    }
}

using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба RO — почему признак, стоящий на 180°, читается как ПЕРЕНОС, и что читается вместо него.
/// </summary>
/// <remarks>
/// <para>
/// <b>Симптом, который надо объяснить.</b> Клиентская приёмка 19.09.2026 (поставка
/// <c>publish-b3-20260919-identity-fixed</c>, строка <c>B3C2.3.3.step5b.kind_at_180</c>) получила на
/// признаке, повёрнутом на 180° вокруг Z, ответ <c>solid.reposition_kind = translate</c> с пустыми
/// углом и осью — при ПРАВИЛЬНОЙ геометрии (<c>kompas_list_bodies</c> отдал
/// <c>(−30,−10,0)…(−10,0,5)</c>, то есть зеркало относительно начала). Контроль того же признака на
/// 90° в том же переоткрытом документе читался правильно (<c>rotate</c>, 90, <c>(0,0,1)</c>).
/// </para>
/// <para>
/// <b>Что здесь проверяется и почему именно это.</b> Отчёт приёмки объяснял расхождение тем, что
/// «столбцы матрицы единичны, поэтому это перенос». Это объяснение НЕ принимается: единичная длина
/// столбцов имеет место и при 90°, и она не различает случаи. Разбор вывода
/// (<c>Api5Session.SolidRead.cs</c>, <c>DeriveReposition</c>) показывает, что ветка 180° в нём ЕСТЬ и
/// математически верна: для <c>diag(−1,−1,1)</c> след равен −1, угол 180°, кососимметричная часть
/// нулевая, и ось берётся из <c>R + I</c> → <c>rotate/180/(0,0,1)</c>. Значит <c>translate</c> мог
/// получиться только из ОСЕЙ, близких к единичной матрице, — то есть из ДРУГОГО объекта.
/// </para>
/// <para>
/// <b>Гипотеза, которая здесь измеряется.</b> Сопоставление признака дерева с элементом коллекции
/// API7 идёт ПО ПОРЯДКОВОМУ НОМЕРУ (<c>RequireSameTypeIndex</c>: порядок среди признаков того же
/// типа в дереве → индекс в <c>IModelContainer.BodyRepositions</c>), а проверяется только СОВПАДЕНИЕ
/// ЧИСЛА элементов. Если подавление и восстановление признака меняет порядок коллекции (а оно его
/// меняет: снятие подавления убирает элементы из коллекции, возврат добавляет их заново), то
/// порядковый номер перестаёт указывать на тот же признак — и чтение описывает СОСЕДА. Тогда
/// «180° читается как перенос» означает «прочитан признак переноса, стоящий рядом», а не дефект
/// вывода угла. Гипотеза подтверждается, только если порядок коллекции после подавления и
/// восстановления РАСХОДИТСЯ с порядком дерева, а чтение по порядковому номеру даёт вид соседа.
/// </para>
/// <para>
/// <b>Отрицательный контроль встроен.</b> Первое чтение идёт ДО подавления: там порядок заведомо
/// согласован (это уже измерено клиентской приёмкой, шаг <c>step3.read_both</c>). Если бы и оно
/// давало неверный вид, дело было бы не в подавлении, и проба обязана это показать, а не списать
/// расхождение на гипотезу.
/// </para>
/// <para>
/// <b>Что НЕ является предметом пробы.</b> Чтение вектора переноса и точки оси
/// (<c>reposition_vector_mm</c>, <c>reposition_axis_point_mm</c>) — это отдельная граница, измеренная
/// пробой <c>--reposition-read</c>. Здесь измеряется только вид и параметры поворота.
/// </para>
/// </remarks>
internal sealed class RepositionOrderProbe
{
    /// <summary>Тело A: 20×10×5 = 1000 мм³, габарит (0,0,0)…(20,10,5).</summary>
    private const double Ax0 = 0d, Ax1 = 20d, Ay0 = 0d, Ay1 = 10d, Az = 5d;

    /// <summary>Постороннее тело S: 10×10×10 = 1000 мм³, далеко от A.</summary>
    private const double Sx0 = 100d, Sx1 = 110d, Sy0 = 0d, Sy1 = 10d, Sz = 10d;

    /// <summary>Тип признака изменения положения в дереве (измерен, <c>Api5Session.cs</c>).</summary>
    private const int RepositionTreeType = 79;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    /// <summary>
    /// Второй признак (поворот), удержанный МЕЖДУ подавлением и восстановлением. Подавленный признак
    /// исчезает из <c>EntityCollection(110)</c> (измерено: 4→3 элемента), поэтому найти его повторным
    /// обходом дерева нельзя — держать объект обязательно, а не удобно.
    /// </summary>
    private ksEntity? _second;
    private ksDocument3D? _reopened;
    private ksPart? _reopenedPart;
    private IModelContainer? _reopenedContainer;

    public RepositionOrderProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "reposition-order.json"),
            Path.Combine(options.ReportDir, "reposition-order.md"));

    public void Run()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        Launch();
        try
        {
            var part = Fixture();
            if (part is null)
            {
                return;
            }

            var chain = Chain(part);
            if (chain is null)
            {
                return;
            }

            var (doc, a, s, translate, rotate) = chain.Value;

            // Отрицательный контроль к гипотезе: ДО подавления порядок заведомо согласован.
            var beforeSuppress = OrdinalReading(doc, part, "RO.3",
                "чтение по порядковому номеру ДО подавления — контроль к гипотезе");

            SuppressRestore(doc, part, rotate, true);
            var afterSuppress = OrdinalReading(doc, part, "RO.5",
                "чтение по порядковому номеру ПОСЛЕ подавления и восстановления второго признака");

            var afterRestore = SuppressRestore(doc, part, rotate, false);
            _ = afterRestore;

            EditTo180(doc, part, rotate);
            var at180 = OrdinalReading(doc, part, "RO.7",
                "чтение по порядковому номеру ПОСЛЕ правки второго признака до 180°");

            // Развёртка углов — на ЖИВОМ документе: после переоткрытия объект, взятый до закрытия,
            // уже не годится (шаг RO.8 первого прогона показал это прямо: Update()=False и GetVector
            // отказал). Переоткрытие поэтому идёт последним, и оно измеряет ПОРЯДОК, а не вывод.
            AngleSweep(part, doc, a, translate, rotate);

            ReopenAndRead(doc, part, "RO.8");
            ReopenedReadback("RO.10");

            Verdict(beforeSuppress, afterSuppress, at180);
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ RO.0 ══

    private void Launch()
    {
        var step = _report.Begin("RO.0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Сеанс поднимается сам и не трогает чужой?");
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        _ownPid = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid));
        step.Observe("чужой процесс до запуска: " + _pidsBefore.Count + ", свой процесс: " + _ownPid);
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("RO.9", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit: " + HResult.Describe(ex));
        }

        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 15000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var alive = NewProcesses();
        step.Data["orphans"] = alive;
        step.Observe("своих процессов после Quit: " + alive.Count + ", ожидание " + waited + " мс");
        if (alive.Count == 0)
        {
            step.Pass("сеанс освобождён");
        }
        else
        {
            step.Unknown("процесс остался");
        }
    }

    private List<int> NewProcesses()
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            var pid = process.Id;
            process.Dispose();
            if (!_pidsBefore.Contains(pid))
            {
                found.Add(pid);
            }
        }

        return found;
    }

    // ══════════════════════════════════════════════════════════════ RO.1 ══

    private ksPart? Fixture()
    {
        var step = _report.Begin("RO.1", "Заготовка: тело A 20×10×5 (1000 мм³) и постороннее S 10×10×10",
            "Строится ли заготовка, на которой воспроизводится цепочка приёмки?");
        var doc = NewPart(out var part);
        _doc = doc;

        var a = ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "ro-a");
        var s = ExtrudeRect(doc, part, Sx0, Sx1, Sy0, Sy1, Sz, step, "ro-s");
        var rows = BodyRows(part);
        step.Observe("тела: " + Describe(rows));
        step.Data["bodies"] = rows.Count;

        if (a is null || s is null || rows.Count != 2)
        {
            step.Fail("заготовка не построена: тел " + rows.Count + ", A=" + (a is null) + ", S=" + (s is null));
            return null;
        }

        step.Pass("заготовка построена: A и S, оба по 1000 мм³");
        return part;
    }

    private ksDocument3D _doc = null!;

    // ══════════════════════════════════════════════════════════════ RO.2 ══

    /// <summary>
    /// Цепочка приёмки: признак[0] — перенос A на (10,0,0), признак[1] — поворот A на +90° вокруг Z.
    /// Два признака получают ОДНО отображаемое имя — это условие уже исправленного дефекта
    /// адресации по имени, и оно здесь сохраняется намеренно.
    /// </summary>
    private (ksDocument3D Doc, BodyRow A, BodyRow S, IBodyReposition Translate, IBodyReposition Rotate)? Chain(
        ksPart part)
    {
        var step = _report.Begin("RO.2",
            "Цепочка: перенос A на (10,0,0), затем поворот той же A на +90° вокруг Z",
            "Создаются ли два признака изменения положения подряд, как в клиентской цепочке?");

        var rows = BodyRows(part);
        var a = rows.FirstOrDefault(r => Near(r, Ax0, Ay0, 0d, Ax1, Ay1, Az));
        if (a is null)
        {
            step.Fail("тело A не найдено");
            return null;
        }

        var translate = CreateReposition(part, a, Translation(10d, 0d, 0d), step, "перенос");
        part.RebuildModel();
        _doc.RebuildDocument();
        if (translate is null)
        {
            step.Fail("признак переноса не создан");
            return null;
        }

        var movedA = BodyRows(part).FirstOrDefault(r => Near(r, 10d, 0d, 0d, 30d, 10d, Az));
        if (movedA is null)
        {
            step.Fail("тело A не встало на (10,0,0)…(30,10,5) — перенос не применился");
            return null;
        }

        var rotate = CreateReposition(part, movedA, RotationZ(90d), step, "поворот 90");
        part.RebuildModel();
        _doc.RebuildDocument();
        if (rotate is null)
        {
            step.Fail("признак поворота не создан");
            return null;
        }

        var rotatedA = BodyRows(part).FirstOrDefault(r => Near(r, -10d, 10d, 0d, 0d, 30d, Az));
        var s = BodyRows(part).FirstOrDefault(r => Near(r, Sx0, Sy0, 0d, Sx1, Sy1, Sz));
        step.Observe("тело A после поворота: " + (rotatedA?.Describe() ?? "<не найдено>"));
        step.Observe("постороннее S: " + (s?.Describe() ?? "<не найдено>"));

        if (rotatedA is null || s is null)
        {
            step.Fail("поворот не дал габарита (−10,10,0)…(0,30,5) либо S исчезло");
            return null;
        }

        var sameType = TreeElements(part, step).Where(e => e.Type == RepositionTreeType).ToList();
        step.Observe("признаков типа " + RepositionTreeType + " в дереве: " + sameType.Count);
        foreach (var feature in sameType)
        {
            step.Observe("  дерево: " + feature.Describe());
        }

        step.Data["tree_count"] = sameType.Count;
        step.Data["api7_count"] = Count(part);
        step.Pass("цепочка построена: перенос затем поворот, постороннее S на месте");
        return (_doc, rotatedA, s, translate, rotate);
    }

    // ══════════════════════════════════════════════════════════════ RO.3/RO.5/RO.7 ══

    /// <summary>
    /// Чтение ТЕМ ЖЕ ПРАВИЛОМ, что и продукт: порядковый номер признака среди однотипных в дереве →
    /// индекс в <c>IModelContainer.BodyRepositions</c>, с проверкой только СОВПАДЕНИЯ ЧИСЛА
    /// элементов (это и есть <c>RequireSameTypeIndex</c>).
    /// </summary>
    private OrdinalResult OrdinalReading(ksDocument3D doc, ksPart part, string id, string title)
    {
        var step = _report.Begin(id, title,
            "Указывает ли порядковый номер дерева на тот же признак, что и до подавления?");

        var result = new OrdinalResult();
        var container = Container(doc);
        if (container is null)
        {
            step.Fail("IModelContainer недоступен");
            return result;
        }

        var sameType = TreeElements(part, step).Where(e => e.Type == RepositionTreeType).ToList();
        var api7Count = Count(part);

        step.Observe("дерево типа " + RepositionTreeType + ": " + sameType.Count
            + ", элементов BodyRepositions: " + api7Count);
        step.Data["tree_count"] = sameType.Count;
        step.Data["api7_count"] = api7Count;

        if (api7Count is not int total || total != sameType.Count)
        {
            step.Unknown("число элементов коллекции (" + api7Count + ") не совпало с числом признаков в "
                + "дереве (" + sameType.Count + ") — продукт в этом случае ОТКАЗЫВАЕТ, а не читает наугад");
            return result;
        }

        for (var ordinal = 0; ordinal < sameType.Count; ordinal++)
        {
            var treeName = sameType[ordinal].Name;
            var reading = ReadElement(container, ordinal, step, "порядковый " + ordinal);
            step.Observe("  порядковый " + ordinal + " (дерево «" + treeName + "») → " + reading.Text);

            result.Rows.Add(new OrdinalRow(ordinal, treeName, reading));
            if (reading.Kind == "rotate")
            {
                result.RotateOrdinals.Add(ordinal);
            }
            else if (reading.Kind == "translate")
            {
                result.TranslateOrdinals.Add(ordinal);
            }
        }

        step.Data["rows"] = result.Rows.Select(r => r.ToString()).ToList();
        step.Pass("прочитано по порядковому номеру: " + result.Rows.Count + " записей");
        return result;
    }

    // ══════════════════════════════════════════════════════════════ подавление ══

    private bool SuppressRestore(ksDocument3D doc, ksPart part, IBodyReposition rotate, bool suppress)
    {
        var id = suppress ? "RO.4" : "RO.4b";
        var step = _report.Begin(id,
            suppress
                ? "Подавить ВТОРОЙ признак (поворот) — маршрутом продукта"
                : "Восстановить ВТОРОЙ признак тем же объектом",
            "Меняет ли подавление и восстановление ПОРЯДОК коллекции API7?");

        try
        {
            var container = Container(doc);
            if (container is null)
            {
                step.Fail("IModelContainer недоступен");
                return false;
            }

            if (suppress)
            {
                var entities = TreeElements(part, step).Where(e => e.Type == RepositionTreeType).ToList();
                if (entities.Count < 2)
                {
                    step.Fail("признаков типа " + RepositionTreeType + " меньше двух: " + entities.Count);
                    return false;
                }

                _second = entities[1].Entity;
            }

            if (_second is null)
            {
                step.Fail("второй признак не удержан с шага подавления — восстановить нечем");
                return false;
            }

            if (_second.GetFeature() is not ksFeature feature)
            {
                step.Fail("второй признак не отвечает GetFeature() как ksFeature");
                return false;
            }

            feature.excluded = suppress;
            part.RebuildModel();
            doc.RebuildDocument();

            var readBack = Api5.SafeBool(() => (_second.GetFeature() as ksFeature)?.excluded ?? false);
            var tree = TreeElements(part, step).Where(e => e.Type == RepositionTreeType).ToList();
            var order = OrderOf(container, step);
            step.Observe("excluded прочитан обратно: " + Api5.Raw(readBack));
            step.Observe("дерево типа " + RepositionTreeType + " после операции: " + tree.Count
                + " (" + string.Join("; ", tree.Select(e => e.Describe())) + ")");
            step.Observe("порядок BodyRepositions после операции: " + order);
            step.Data["excluded_read_back"] = readBack;
            step.Data["tree_count"] = tree.Count;
            step.Data["api7_count"] = Count(container);
            step.Data["order"] = order;
            _ = rotate;
            step.Pass("операция выполнена, дерево и порядок записаны");
            return true;
        }
        catch (Exception ex)
        {
            step.Fail("операция не выполнена: " + HResult.Describe(ex));
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════ правка до 180° ══

    private void EditTo180(ksDocument3D doc, ksPart part, IBodyReposition rotate)
    {
        var step = _report.Begin("RO.6",
            "Правка ВТОРОГО признака до 180° — тем же маршрутом, что и продукт",
            "Воспроизводится ли симптом: геометрия 180°, а чтение даёт перенос?");
        try
        {
            // Правка адресуется НЕ порядковым номером, а самим объектом, измеренным в RO.2:
            // иначе проба повторила бы дефект вместо того, чтобы его измерить.
            rotate.Position.InitByMatrix3D(RotationZ(180d));
            var updated = rotate.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("InitByMatrix3D(180° вокруг Z) → Update()=" + updated);

            var axes = AxesOf(rotate, step);
            step.Observe("оси САМОГО объекта после правки: " + Describe(axes));
            step.Observe("вывод по ним: " + Derive(axes));

            var body = BodyRows(part).FirstOrDefault(r => Near(r, -30d, -10d, 0d, -10d, 0d, Az));
            step.Observe("геометрия тела: " + (body?.Describe() ?? "<габарита (−30,−10,0)…(−10,0,5) нет>"));
            step.Data["geometry_at_180"] = body?.Describe();
            step.Data["axes_own"] = Describe(axes);
            step.Data["derive_own"] = Derive(axes).ToString();

            step.Pass(body is null ? "правка прошла, но геометрии 180° нет" : "правка прошла, геометрия 180°");
        }
        catch (Exception ex)
        {
            step.Fail("правка не выполнена: " + HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════════════════════════ переоткрытие ══

    private void ReopenAndRead(ksDocument3D doc, ksPart part, string id)
    {
        var step = _report.Begin(id, "save → close → open: совпадает ли чтение по порядковому номеру с живым",
            "Устраняет ли переоткрытие расхождение, и если да — то порядком или данными?");
        var path = Path.Combine(_options.WorkDir, "ro-order-" + DateTime.Now.ToString("HHmmss") + ".m3d");
        try
        {
            var saved = doc.SaveAs(path);
            step.Observe("SaveAs: " + saved + " → " + path);
            doc.close();

            var reopened = (ksDocument3D)_app.Document3D();
            var opened = reopened.Open(path, false);
            step.Observe("Open: " + opened);
            if (!opened)
            {
                step.Fail("переоткрытие не удалось");
                return;
            }

            var newPart = (ksPart)reopened.GetPart(-1);
            var container = Container(reopened);
            if (container is null)
            {
                step.Fail("IModelContainer переоткрытого документа недоступен");
                return;
            }

            var sameType = TreeElements(newPart, step).Where(e => e.Type == RepositionTreeType).ToList();
            step.Observe("после переоткрытия: признаков типа " + RepositionTreeType + " — " + sameType.Count
                + ", элементов BodyRepositions — " + Count(container));
            step.Observe("порядок после переоткрытия: " + OrderOf(container, step));

            for (var ordinal = 0; ordinal < sameType.Count; ordinal++)
            {
                var reading = ReadElement(container, ordinal, step, "порядковый " + ordinal + " (переоткрытый)");
                step.Observe("  порядковый " + ordinal + " (дерево «" + sameType[ordinal].Name + "») → " + reading.Text);
            }

            IdentifyElements(reopened, newPart, container, step);
            _reopened = reopened;
            _reopenedPart = newPart;
            _reopenedContainer = container;
            step.Pass("переоткрытие измерено");
        }
        catch (Exception ex)
        {
            step.Fail("переоткрытие прервано: " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Разделить две причины, которые дают ОДИН И ТОТ ЖЕ ответ «translate»: прочитан ЧУЖОЙ элемент
    /// коллекции либо преобразование не читается с переоткрытого признака. Разделяет запись:
    /// если в элемент 1 можно ЗАПИСАТЬ известную матрицу и от неё двинется тело A, то элемент 1 —
    /// это признак изменения положения, действующий на A, и нулевое чтение с него есть дефект
    /// ЧТЕНИЯ, а не подмена объекта.
    /// </summary>
    private void IdentifyElements(ksDocument3D doc, ksPart part, IModelContainer container, ProbeStep step)
    {
        for (var i = 0; i < (Count(container) ?? 0); i++)
        {
            try
            {
                if (container.BodyRepositions[i] is not IBodyReposition element)
                {
                    step.Observe("элемент " + i + ": не отдаёт IBodyReposition");
                    continue;
                }

                var axes = AxesOf(element, step);
                var (kind, axis, angle, branch) = Derive(axes);
                step.Observe("элемент " + i + ": вид=" + kind + ", угол=" + Api5.Num(angle)
                    + ", ось=" + Describe(axis) + ", ветка=" + branch + ", оси=" + Describe(axes));
                step.Observe("элемент " + i + ": RepositionBody=" + DescribeBody(element));
            }
            catch (Exception ex)
            {
                step.Observe("элемент " + i + ": " + HResult.Describe(ex));
            }
        }

        // Запись здесь НЕ выполняется: разделение причин («чужой элемент» против «нечитаемое
        // преобразование») требует, чтобы записанное преобразование было ТЕМ ЖЕ, что и до записи, —
        // иначе повторная запись 180° стала бы записью 90°, и неизменность геометрии ничего не
        // доказывала бы. Поэтому запись вынесена в шаг RO.10.
    }

    /// <summary>
    /// Переоткрытый признак: читается ли преобразование ДО записи и меняется ли геометрия от
    /// повторной записи ТОГО ЖЕ преобразования.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Разделение, которое наряд §5 требует явно: «неверное сопоставление объекта» против
    /// «особенностей чтения Position». Оба дают ОДИН И ТОТ ЖЕ ответ «translate», и различить их
    /// чтением невозможно — различает ПОВТОРНАЯ ЗАПИСЬ ТОГО ЖЕ преобразования:
    /// </para>
    /// <list type="bullet">
    /// <item>если элемент 1 — ЧУЖОЙ элемент, то запись в него повернёт тело на 180° вокруг Z от
    /// исходного положения, и геометрия СДВИНЕТСЯ;</item>
    /// <item>если элемент 1 — тот самый признак, а нечитаемым было только чтение, то запись того же
    /// преобразования не изменит геометрию НИ НА ЧТО, а чтение после записи станет верным.</item>
    /// </list>
    /// <para>
    /// Третье переоткрытие с ДРУГИМ записанным углом (90°) отделяет «дефект при 180°» от «дефекта
    /// чтения любого переоткрытого преобразования»: угол меняется, а условие чтения — нет.
    /// </para>
    /// </remarks>
    private void ReopenedReadback(string id)
    {
        var step = _report.Begin(id,
            "Переоткрытый признак: чтение ДО записи и повторная запись ТОГО ЖЕ преобразования",
            "Прочитан чужой элемент коллекции или преобразование не читается с переоткрытого признака?");
        var doc = _reopened;
        var part = _reopenedPart;
        var container = _reopenedContainer;
        if (doc is null || part is null || container is null)
        {
            step.Unknown("переоткрытый документ недоступен — разделение причин не выполнено");
            return;
        }

        try
        {
            // (а) Чтение ДО любой записи: что отдаёт переоткрытый признак.
            var before = ReadElement(container, 1, step, "элемент 1 до записи");
            var bodies0 = Describe(BodyRows(part));
            step.Observe("до записи: " + before.Text);
            step.Observe("геометрия, восстановленная ИЗ ФАЙЛА: " + bodies0);

            // (б) Пересборка без записи: делает ли перестроение преобразование читаемым?
            doc.RebuildDocument();
            var afterRebuild = ReadElement(container, 1, step, "элемент 1 после пересборки");
            step.Observe("после пересборки БЕЗ записи: " + afterRebuild.Text);

            if (container.BodyRepositions[1] is not IBodyReposition second)
            {
                step.Fail("элемент 1 не отдаёт IBodyReposition");
                return;
            }

            // (в) Запись ЗАВЕДОМО известного преобразования: она обязана и прочитаться, и примениться.
            // Тело при этом обязано встать в габарит ПОВОРОТА ОТ ПЕРЕНОСА, а не от исходного тела:
            // перенос (10,0,0) даёт [10,30]×[0,10]×[0,5], поворот на 180° вокруг Z — [−30,−10]…[−10,0].
            // Это и отличает «элемент 1 — второй признак» от «элемент 1 — первый».
            second.Position.InitByMatrix3D(RotationZ(180d));
            var updated = second.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var afterWrite = ReadElement(container, 1, step, "элемент 1 после записи 180°");
            var bodies1 = Describe(BodyRows(part));
            var at180 = BodyRows(part).FirstOrDefault(r => Near(r, -30d, -10d, 0d, -10d, 0d, Az));
            step.Observe("запись 180° вокруг Z: Update()=" + updated);
            step.Observe("чтение после записи: " + afterWrite.Text);
            step.Observe("геометрия после записи: " + bodies1);

            // (г) ПОВТОРНАЯ запись ТОГО ЖЕ преобразования. Теперь оно заведомо записано — и если
            // геометрия от повтора не двигается, то записанное значение и было этим преобразованием:
            // значит нулевое чтение до записи было дефектом ЧТЕНИЯ, а не подменой объекта.
            second.Position.InitByMatrix3D(RotationZ(180d));
            var repeated = second.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var afterRepeat = ReadElement(container, 1, step, "элемент 1 после повторной записи 180°");
            var bodies2 = Describe(BodyRows(part));
            step.Observe("повторная запись ТОГО ЖЕ 180°: Update()=" + repeated
                + ", чтение: " + afterRepeat.Text);
            step.Observe("геометрия после повторной записи: " + bodies2);

            // (д) Положительный контроль записи: ДРУГОЕ преобразование обязано сдвинуть геометрию.
            second.Position.InitByMatrix3D(RotationZ(90d));
            var updated90 = second.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var after90 = ReadElement(container, 1, step, "элемент 1 после записи 90°");
            var at90 = BodyRows(part).FirstOrDefault(r => Near(r, -10d, 10d, 0d, 0d, 30d, Az));
            step.Observe("запись 90° вокруг Z: Update()=" + updated90 + ", чтение: " + after90.Text);
            step.Observe("геометрия после 90°: " + Describe(BodyRows(part)));

            // (е) Третье переоткрытие: в модели 90°, условие чтения то же — БЕЗ записи.
            var path = Path.Combine(_options.WorkDir, "ro-readback-" + DateTime.Now.ToString("HHmmss") + ".m3d");
            var saved = doc.SaveAs(path);
            doc.close();
            var third = (ksDocument3D)_app.Document3D();
            var opened = third.Open(path, false);
            step.Observe("третье переоткрытие: SaveAs=" + saved + ", Open=" + opened);
            ElementReading? thirdReading = null;
            var thirdBodies = string.Empty;
            if (opened)
            {
                var thirdPart = (ksPart)third.GetPart(-1);
                var thirdContainer = Container(third);
                if (thirdContainer is not null)
                {
                    thirdReading = ReadElement(thirdContainer, 1, step, "элемент 1 третьего переоткрытия");
                    thirdBodies = Describe(BodyRows(thirdPart));
                }
            }

            step.Observe("чтение после третьего переоткрытия (в модели 90°): "
                + (thirdReading?.Text ?? "<нет>"));
            step.Observe("геометрия после третьего переоткрытия: " + thirdBodies);

            var readIsIdentityBeforeWrite = before.Kind == "translate";
            var rebuildDoesNotHelp = afterRebuild.Kind == "translate";
            var writeApplies = afterWrite.Kind == "rotate" && at180 is not null;
            var repeatIsIdempotent = SameGeometry(bodies1, bodies2) && afterRepeat.Kind == "rotate";
            var writeWorks = after90.Kind == "rotate" && at90 is not null;
            var identityAlsoAt90 = thirdReading?.Kind == "translate";

            step.Data["read_before_write"] = before.Text;
            step.Data["bodies_restored_from_file"] = bodies0;
            step.Data["read_after_rebuild"] = afterRebuild.Text;
            step.Data["read_after_write"] = afterWrite.Text;
            step.Data["bodies_after_write"] = bodies1;
            step.Data["read_after_repeat"] = afterRepeat.Text;
            step.Data["bodies_after_repeat"] = bodies2;
            step.Data["repeat_idempotent"] = repeatIsIdempotent;
            step.Data["read_after_90_write"] = after90.Text;
            step.Data["read_after_third_reopen"] = thirdReading?.Text;

            if (readIsIdentityBeforeWrite && rebuildDoesNotHelp && writeApplies
                && repeatIsIdempotent && writeWorks && identityAlsoAt90)
            {
                step.Pass("причина — ЧТЕНИЕ Position переоткрытого признака: то же преобразование "
                    + "записано, повторная запись геометрию не сдвинула, чтение до записи давало "
                    + "единичные оси, а при ДРУГОМ угле после переоткрытия — то же самое");
            }
            else if (readIsIdentityBeforeWrite && !writeApplies)
            {
                step.Fail("запись в элемент 1 не применилась к телу A: элемент 1 — не тот признак, "
                    + "и нулевое чтение было ВЕРНЫМ (неверное сопоставление объекта)");
            }
            else if (writeApplies && !repeatIsIdempotent)
            {
                step.Fail("повторная запись ТОГО ЖЕ преобразования сдвинула геометрию: записанное "
                    + "значение не было этим преобразованием, и вывод «дефект чтения» не подтверждён");
            }
            else
            {
                step.Unknown("условия разделения выполнены не полностью — причина не установлена");
            }
        }
        catch (Exception ex)
        {
            step.Fail("разделение причин прервано: " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Сравнение геометрии после записи — по СТРОКЕ описания тел: она несёт и объём, и оба габарита,
    /// то есть ровно то, что обязано совпасть у идемпотентной записи.
    /// </summary>
    private static bool SameGeometry(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>Тело-вход элемента — по <c>IBody7.BodyId</c>; коллекционный номер тела идентичностью не является.</summary>
    private static string DescribeBody(IBodyReposition element)
    {
        try
        {
            var body = element.RepositionBody;
            if (body is null)
            {
                return "<нет>";
            }

            var id = Late.Get(body, "BodyId");
            return id is null
                ? "перенесено, BodyId не прочитан (" + body.GetType().Name + ")"
                : "BodyId=" + Api5.Raw(id);
        }
        catch (Exception ex)
        {
            return "<" + HResult.Describe(ex) + ">";
        }
    }

    // ══════════════════════════════════════════════════════════════ развёртка углов ══

    /// <summary>
    /// Регрессия по углам на ОДНОМ признаке: 0°, ±90°, 180°, около 180°, 270°, 360°, произвольная
    /// ось, ось через точку. Читается и вывод, и сами оси — чтобы «неверный вид» можно было отнести
    /// либо к выводу, либо к прочитанным числам.
    /// </summary>
    private void AngleSweep(ksPart part, ksDocument3D doc, BodyRow a, IBodyReposition translate, IBodyReposition rotate)
    {
        var step = _report.Begin("RO.8b", "Развёртка углов на одном признаке: вывод вида и оси по каждому",
            "Какие углы выводятся неверно, и лежит ли причина в выводе или в прочитанных числах?");
        _ = part;
        _ = a;
        _ = translate;

        var cases = new (string Label, double[] Matrix)[]
        {
            ("0°", RotationZ(0d)),
            ("+90° вокруг Z", RotationZ(90d)),
            ("−90° вокруг Z", RotationZ(-90d)),
            ("180° вокруг Z", RotationZ(180d)),
            ("179° вокруг Z", RotationZ(179d)),
            ("181° вокруг Z", RotationZ(181d)),
            ("270° вокруг Z", RotationZ(270d)),
            ("360° вокруг Z", RotationZ(360d)),
            ("180° вокруг X", RotationAbout(new[] { 1d, 0d, 0d }, 180d)),
            ("120° вокруг (1,1,1)/√3", RotationAbout(new[] { 1d, 1d, 1d }, 120d)),
        };

        foreach (var (label, matrix) in cases)
        {
            try
            {
                rotate.Position.InitByMatrix3D(matrix);
                var updated = rotate.Update();
                doc.RebuildDocument();

                var axes = AxesOf(rotate, step);
                var (kind, axis, angle, trace, branch) = DeriveDetailed(axes);
                step.Observe($"{label}: Update()={updated}, след={trace:0.############}, "
                    + $"ветка={branch}, вид={kind}, угол={Api5.Num(angle)}, ось={Describe(axis)}");
            }
            catch (Exception ex)
            {
                step.Observe(label + ": " + HResult.Describe(ex));
            }
        }

        step.Pass("развёртка углов измерена");
    }

    // ══════════════════════════════════════════════════════════════ свод ══

    private void Verdict(OrdinalResult before, OrdinalResult after, OrdinalResult at180)
    {
        var step = _report.Begin("RO.V", "Свод: подтверждена ли гипотеза о порядке коллекции",
            "Совпадает ли расхождение чтения с расхождением порядка?");

        step.Observe("до подавления: rotate на порядковых " + string.Join(",", before.RotateOrdinals)
            + ", translate на " + string.Join(",", before.TranslateOrdinals));
        step.Observe("после подавления/восстановления: rotate на " + string.Join(",", after.RotateOrdinals)
            + ", translate на " + string.Join(",", after.TranslateOrdinals));
        step.Observe("после правки до 180°: rotate на " + string.Join(",", at180.RotateOrdinals)
            + ", translate на " + string.Join(",", at180.TranslateOrdinals));

        step.Data["rotate_ordinals_before_suppress"] = before.RotateOrdinals;
        step.Data["rotate_ordinals_after_suppress"] = after.RotateOrdinals;
        step.Data["rotate_ordinals_at_180"] = at180.RotateOrdinals;

        var beforeRead = before.Rows.Count > 0;
        var afterRead = after.Rows.Count > 0;
        if (!beforeRead || !afterRead)
        {
            step.Unknown("одно из чтений не состоялось вовсе (дерево и коллекция разошлись по числу "
                + "элементов, и продукт ОТКАЗАЛ, а не прочитал наугад): до подавления записей "
                + before.Rows.Count + ", после — " + after.Rows.Count + ". Отказ — не измерение, и "
                + "делать из него вывод о порядке коллекции нельзя.");
            return;
        }

        if (before.RotateOrdinals.Count > 0 && after.RotateOrdinals.Count == 0)
        {
            step.Fail("ПОДТВЕРЖДЕНО: до подавления поворот читается на порядковом "
                + string.Join(",", before.RotateOrdinals) + ", а после подавления и восстановления — "
                + "ни на одном. Порядок коллекции разошёлся с порядком дерева, и чтение по "
                + "порядковому номеру описывает СОСЕДА. Это и есть механизм симптома: «180° читается "
                + "как перенос» означает «прочитан признак переноса».");
            return;
        }

        if (before.RotateOrdinals.Count > 0 && after.RotateOrdinals.Count > 0)
        {
            step.Unknown("порядок коллекции после подавления и восстановления НЕ разошёлся: поворот "
                + "читается и там, и там. Гипотеза о порядке этим не подтверждена — причина симптома "
                + "остаётся неустановленной, и записывать её такой нельзя.");
            return;
        }

        step.Unknown("первое чтение само не дало поворота на порядковом номере — отрицательный "
            + "контроль не прошёл, и отнести расхождение к подавлению нельзя");
    }

    // ══════════════════════════════════════════════════════════════ приборы ══

    /// <summary>
    /// Вывод вида и параметров из осей локальной системы. ДОСЛОВНО повторяет алгоритм продукта
    /// (<c>Api5Session.SolidRead.DeriveReposition</c>), потому что проба обязана измерять ТУ ЖЕ
    /// ветку, а не похожую: разойдись они — измерялся бы прибор, а не продукт.
    /// </summary>
    private static (string Kind, double[]? Axis, double? Angle, string Branch) Derive(double[][]? axes)
    {
        var (kind, axis, angle, _, branch) = DeriveDetailed(axes);
        return (kind, axis, angle, branch);
    }

    private static (string Kind, double[]? Axis, double? Angle, double Trace, string Branch) DeriveDetailed(
        double[][]? axes)
    {
        if (axes is not [var ox, var oy, var oz])
        {
            return ("<оси не прочитаны>", null, null, double.NaN, "<нет осей>");
        }

        var trace = ox[0] + oy[1] + oz[2];
        var angle = Math.Acos(Math.Clamp((trace - 1d) / 2d, -1d, 1d)) * 180d / Math.PI;

        var axis = new[] { oy[2] - oz[1], oz[0] - ox[2], ox[1] - oy[0] };
        var norm = Norm(axis);
        if (norm > 1e-9)
        {
            return ("rotate", Unit(axis, norm), angle, trace, "кососимметричная часть не нулевая");
        }

        if (angle < 1e-6)
        {
            return ("translate", null, null, trace, "угол ≈ 0");
        }

        var candidates = new[]
        {
            new[] { ox[0] + 1d, ox[1], ox[2] },
            new[] { oy[0], oy[1] + 1d, oy[2] },
            new[] { oz[0], oz[1], oz[2] + 1d },
        };
        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (Norm(candidate) > Norm(best))
            {
                best = candidate;
            }
        }

        var bestNorm = Norm(best);
        return bestNorm > 1e-9
            ? ("rotate", Unit(best, bestNorm), 180d, trace, "R + I")
            : ("rotate", null, angle, trace, "R + I пуст");
    }

    private ElementReading ReadElement(IModelContainer container, int index, ProbeStep step, string label)
    {
        try
        {
            if (container.BodyRepositions[index] is not IBodyReposition reposition)
            {
                return new ElementReading(index, null, "<элемент не отдаёт IBodyReposition>");
            }

            var axes = AxesOf(reposition, step);
            var (kind, axis, angle, branch) = Derive(axes);
            _ = label;
            return new ElementReading(index, kind,
                "вид=" + kind + ", угол=" + Api5.Num(angle) + ", ось=" + Describe(axis)
                + ", ветка=" + branch + ", оси=" + Describe(axes));
        }
        catch (Exception ex)
        {
            return new ElementReading(index, null, "<" + HResult.Describe(ex) + ">");
        }
    }

    /// <summary>Порядок коллекции — по осям: у переноса они единичны, у поворота нет.</summary>
    private string OrderOf(IModelContainer container, ProbeStep step)
    {
        var parts = new List<string>();
        for (var i = 0; i < (Count(container) ?? 0); i++)
        {
            var reading = ReadElement(container, i, step, "порядок " + i);
            parts.Add(i + ":" + (reading.Kind switch
            {
                "translate" => "перенос",
                "rotate" => "поворот",
                _ => "?",
            }));
        }

        return "[" + string.Join(", ", parts) + "]";
    }

    private static double[][]? AxesOf(IBodyReposition reposition, ProbeStep step)
    {
        try
        {
            if (reposition.Position is not ILocalCoordinateSystem local)
            {
                step.Observe("Position не отвечает ILocalCoordinateSystem");
                return null;
            }

            var wanted = new[]
            {
                ksObj3dTypeEnum.o3d_axisOX,
                ksObj3dTypeEnum.o3d_axisOY,
                ksObj3dTypeEnum.o3d_axisOZ,
            };
            var axes = new double[3][];
            for (var i = 0; i < wanted.Length; i++)
            {
                if (!local.GetVector(wanted[i], out var x, out var y, out var z))
                {
                    step.Observe("GetVector(" + wanted[i] + ") отказал");
                    return null;
                }

                axes[i] = new[] { x, y, z };
            }

            return axes;
        }
        catch (Exception ex)
        {
            step.Observe("оси не прочитаны: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Признаки дерева — тем же маршрутом, что и продукт: <c>EntityCollection(110)</c>
    /// (<c>o3d_operationElement</c>), а не <c>GetFeature().SubFeatureCollection(...)</c>. Маршрут
    /// выбран по измерению, а не по удобству: первый шаг этой пробы показал, что
    /// <c>SubFeatureCollection(true, false)</c> отдаёт ПУСТУЮ коллекцию на живой модели, тогда как
    /// <c>EntityCollection(110)</c> отдаёт признаки, — и прибор, читающий пустоту, объявил бы
    /// «признаков нет» там, где их два.
    /// </summary>
    private static List<TreeElement> TreeElements(ksPart part, ProbeStep step)
    {
        var list = new List<TreeElement>();
        try
        {
            if (part.EntityCollection(110) is not ksEntityCollection collection)
            {
                step.Observe("EntityCollection(110) недоступна");
                return list;
            }

            var count = collection.GetCount();
            step.Observe("элементов в EntityCollection(110): " + count);
            for (var i = 0; i < count; i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                list.Add(new TreeElement(i, entity, entity.name ?? string.Empty, entity.type));
            }
        }
        catch (Exception ex)
        {
            step.Observe("дерево не прочитано: " + HResult.Describe(ex));
        }

        return list;
    }

    private static string Describe(double[][]? axes) =>
        axes is null
            ? "<не прочитаны>"
            : string.Join(" | ", axes.Select(a => "(" + string.Join(", ", a.Select(v => Api5.Num(v))) + ")"));

    private static string Describe(double[]? axis) =>
        axis is null ? "<нет>" : "(" + string.Join(", ", axis.Select(v => Api5.Num(v))) + ")";

    private static double Norm(double[] value) =>
        Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]);

    private static double[] Unit(double[] value, double norm) =>
        new[] { value[0] / norm, value[1] / norm, value[2] / norm };

    // ══════════════════════════════════════════════════════════════ матрицы ══

    /// <summary>Матрица 4×4 с единичной ориентацией и заданным переносом.</summary>
    private static double[] Translation(double x, double y, double z) => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        x, y, z, 1d,
    };

    /// <summary>Поворот вокруг Z на угол в градусах, матрица 4×4 по столбцам.</summary>
    private static double[] RotationZ(double angleDeg)
    {
        var r = angleDeg * Math.PI / 180d;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        return new[]
        {
            c, s, 0d, 0d,
            -s, c, 0d, 0d,
            0d, 0d, 1d, 0d,
            0d, 0d, 0d, 1d,
        };
    }

    /// <summary>Поворот на угол вокруг произвольного направления (нормализуется) — формула Родрига.</summary>
    private static double[] RotationAbout(double[] direction, double angleDeg)
    {
        var norm = Norm(direction);
        var (x, y, z) = (direction[0] / norm, direction[1] / norm, direction[2] / norm);
        var r = angleDeg * Math.PI / 180d;
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        var t = 1d - c;

        // Строки — формула Родрига R = t·nnᵀ + c·I + s·[n]×, где
        // [n]× = [[0,−nz,ny],[nz,0,−nx],[−ny,nx,0]]. Знаки s-членов проверены на матрице
        // 120° вокруг (1,1,1)/√3: она обязана быть циклической подстановкой [[0,0,1],[1,0,0],[0,1,0]],
        // и обратный знак дал бы ровно ОБРАТНЫЙ поворот — то есть повернул бы тело не туда, а проба
        // отчиталась бы о дефекте продукта там, где ошибочен её собственный эталон (эталон: случай
        // 4/tan — прибор обязан быть чистым до вывода о продукте).
        var m = new[]
        {
            t * x * x + c, t * x * y - s * z, t * x * z + s * y,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c,
        };

        return new[]
        {
            m[0], m[3], m[6], 0d,
            m[1], m[4], m[7], 0d,
            m[2], m[5], m[8], 0d,
            0d, 0d, 0d, 1d,
        };
    }

    // ══════════════════════════════════════════════════════════════ COM ══

    private IBodyReposition? CreateReposition(ksPart part, BodyRow target, double[] matrix, ProbeStep step, string label)
    {
        try
        {
            var container = Container(_doc);
            if (container?.BodyRepositions?.Add() is not IBodyReposition reposition)
            {
                step.Observe(label + ": BodyRepositions.Add() недоступен");
                return null;
            }

            if (_app.TransferInterface(target.Element!, 2, 0) is not IKompasAPIObject transferred)
            {
                step.Observe(label + ": тело не переносится в API7");
                return null;
            }

            reposition.RepositionBody = transferred;
            reposition.Position.InitByMatrix3D(matrix);
            var updated = reposition.Update();
            step.Observe(label + ": Update()=" + updated);
            return updated ? reposition : null;
        }
        catch (Exception ex)
        {
            step.Observe(label + ": " + HResult.Describe(ex));
            return null;
        }
    }

    private IModelContainer? Container(ksDocument3D doc)
    {
        try
        {
            return _app.TransferInterface(doc, 2, 0) is IKompasDocument3D document7
                && document7.TopPart is IModelContainer container
                ? container
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? Count(IModelContainer? container)
    {
        try
        {
            return container?.BodyRepositions.Count;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private int? Count(ksPart part)
    {
        _ = part;
        return Count(Container(_doc));
    }

    private ksDocument3D NewPart(out ksPart part)
    {
        var doc = (ksDocument3D)_app.Document3D();
        doc.Create(true, true);
        if (doc.GetPart(-1) is not ksPart created)
        {
            throw new InvalidOperationException("деталь не получена");
        }

        part = created;
        return doc;
    }

    private static BodyRow? ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        if (!created)
        {
            step.Observe(prefix + ": Create() = false");
            return null;
        }

        return BodyRows(part).FirstOrDefault(r => Near(r, u0, v0, 0d, u1, v1, thickness));
    }

    private static ksEntity ProfileSketchOn(
        ksDocument3D doc, string name, short planeType, double u0, double u1, double v0, double v1)
    {
        var part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(planeType) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости " + planeType + " нет");
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
        }

        definition.EndEdit();
        return sketch;
    }

    private static List<BodyRow> BodyRows(ksPart part)
    {
        var rows = new List<BodyRow>();
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return rows;
            }

            bodies.refresh();
            var count = bodies.GetCount();
            for (var i = 0; i < count; i++)
            {
                if (bodies.GetByIndex(i) is not { } element)
                {
                    continue;
                }

                var volume = Api5.BodyVolume(element);
                double[]? min = null;
                double[]? max = null;
                if (element is ksBody body && body.GetGabarit(out var gx0, out var gy0, out var gz0,
                        out var gx1, out var gy1, out var gz1))
                {
                    min = new[] { gx0, gy0, gz0 };
                    max = new[] { gx1, gy1, gz1 };
                }

                rows.Add(new BodyRow(i, element, volume, min, max));
            }
        }
        catch (Exception)
        {
            // Не предмет шага.
        }

        return rows;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is { Length: 3 } min && row.Max is { Length: 3 } max
        && Math.Abs(min[0] - x0) < 1e-6 && Math.Abs(min[1] - y0) < 1e-6 && Math.Abs(min[2] - z0) < 1e-6
        && Math.Abs(max[0] - x1) < 1e-6 && Math.Abs(max[1] - y1) < 1e-6 && Math.Abs(max[2] - z1) < 1e-6;

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join("; ", rows.Select(r => r.Describe()));

    private sealed record BodyRow(int Index, object? Element, double? Volume, double[]? Min, double[]? Max)
    {
        public string Describe() =>
            "тел" + Index + ": V=" + Api5.Num(Volume)
            + ", габарит [" + string.Join(", ", (Min ?? Array.Empty<double>()).Select(v => Api5.Num(v)))
            + "]…[" + string.Join(", ", (Max ?? Array.Empty<double>()).Select(v => Api5.Num(v))) + "]";
    }

    private sealed record TreeElement(int Index, ksEntity Entity, string Name, int Type)
    {
        public string Describe() => "[" + Index + "] «" + Name + "» type=" + Type;
    }

    private sealed record ElementReading(int Index, string? Kind, string Text);

    private sealed record OrdinalRow(int Ordinal, string? TreeName, ElementReading Reading)
    {
        public string? Kind => Reading.Kind;

        public override string ToString() =>
            Ordinal + " «" + (TreeName ?? "<нет>") + "» → " + Reading.Text;
    }

    private sealed class OrdinalResult
    {
        public List<OrdinalRow> Rows { get; } = new();

        public List<int> RotateOrdinals { get; } = new();

        public List<int> TranslateOrdinals { get; } = new();
    }
}

using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe RO — why a feature standing at 180° reads as a TRANSLATION, and what is read instead.</summary>
/// <remarks>MEASURED (client acceptance 19.09.2026, delivery <c>publish-b3-20260919-identity-fixed</c>,
/// row <c>B3C2.3.3.step5b.kind_at_180</c>): a feature rotated 180° about Z returned
/// <c>solid.reposition_kind = translate</c> with empty angle and axis, while the geometry was CORRECT
/// (<c>(−30,−10,0)…(−10,0,5)</c>, a mirror about the origin); the same feature at 90° in the same
/// reopened document read correctly (<c>rotate</c>, 90, <c>(0,0,1)</c>). The acceptance report blamed
/// "the matrix columns are unit, so it is a translation" — REJECTED: unit columns hold at 90° too and do
/// not separate the cases. INVARIANT: the 180° branch in <c>Api5Session.SolidRead.DeriveReposition</c>
/// exists and is correct (<c>diag(−1,−1,1)</c>: trace −1, angle 180, zero skew, axis from <c>R + I</c>),
/// so <c>translate</c> can only come from axes near the identity — i.e. from ANOTHER object.
/// HYPOTHESIS: the tree feature is matched to the API7 collection element BY ORDINAL
/// (<c>RequireSameTypeIndex</c>), checking only that the element COUNTS match; suppress/restore changes
/// the collection order, so the ordinal then describes a NEIGHBOUR. TEST: the hypothesis holds only if
/// the collection order diverges from the tree order and the ordinal reading yields the neighbour's
/// kind. The first reading runs BEFORE suppression as a built-in negative control (already MEASURED by
/// the client acceptance, step <c>step3.read_both</c>). LIMIT: the translation vector and axis point
/// (<c>reposition_vector_mm</c>, <c>reposition_axis_point_mm</c>) are a separate boundary MEASURED by
/// probe <c>--reposition-read</c>; here only the kind and rotation parameters are measured.
/// History: docs/decisions/probes.md#ro-order</remarks>
internal sealed class RepositionOrderProbe
{
    /// <summary>Body A: 20×10×5 = 1000 mm³, extent (0,0,0)…(20,10,5).</summary>
    private const double Ax0 = 0d, Ax1 = 20d, Ay0 = 0d, Ay1 = 10d, Az = 5d;

    /// <summary>Foreign body S: 10×10×10 = 1000 mm³, far from A.</summary>
    private const double Sx0 = 100d, Sx1 = 110d, Sy0 = 0d, Sy1 = 10d, Sz = 10d;

    /// <summary>Reposition-feature type in the tree (MEASURED, <c>Api5Session.cs</c>).</summary>
    private const int RepositionTreeType = 79;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    /// <summary>The second feature (rotation), held BETWEEN suppression and restore. A suppressed
    /// feature disappears from <c>EntityCollection(110)</c> (MEASURED: 4→3 elements), so it cannot be
    /// found by re-walking the tree — holding the object is mandatory, not convenient.</summary>
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

            // Negative control for the hypothesis: BEFORE suppression the order is certainly consistent.
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

            // The angle sweep runs on the LIVE document: after reopening, an object taken before the
            // close is no longer usable (RO.8 of the first run showed it plainly: Update()=False and
            // GetVector refused). Reopening therefore comes last, and it measures ORDER, not derivation.
            // History: docs/decisions/probes.md#ro-reopen
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

    /// <summary>Acceptance chain: feature[0] — translation of A by (10,0,0), feature[1] — rotation of A
    /// by +90° about Z. Both features get the SAME displayed name — the condition of the already-fixed
    /// name-address defect, kept here on purpose.</summary>
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

    /// <summary>Read by the SAME RULE as the product: the feature's ordinal among same-type tree features
    /// → index in <c>IModelContainer.BodyRepositions</c>, checking only that the element COUNTS match
    /// (this is <c>RequireSameTypeIndex</c>).</summary>
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

    // ══════════════════════════════════════════════════════════════ suppression ══

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

    // ══════════════════════════════════════════════════════════════ edit to 180° ══

    private void EditTo180(ksDocument3D doc, ksPart part, IBodyReposition rotate)
    {
        var step = _report.Begin("RO.6",
            "Правка ВТОРОГО признака до 180° — тем же маршрутом, что и продукт",
            "Воспроизводится ли симптом: геометрия 180°, а чтение даёт перенос?");
        try
        {
            // The edit is addressed by the OBJECT MEASURED in RO.2, not by ordinal — otherwise the probe
            // would repeat the defect instead of measuring it.
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

    // ══════════════════════════════════════════════════════════════ reopen ══

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

    /// <summary>Separates two causes that give the SAME answer "translate": a FOREIGN collection element
    /// was read, or the transform does not read from the reopened feature. The write separates them: if a
    /// known matrix can be WRITTEN into element 1 and body A moves, then element 1 is a reposition feature
    /// acting on A, and the zero reading is a READ defect, not an object substitution.</summary>
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

        // No write happens here: separating the causes ("foreign element" vs "unreadable transform")
        // requires the written transform to be the SAME as before the write — otherwise a repeated 180°
        // write would become a 90° write and the unchanged geometry would prove nothing. The write is
        // therefore deferred to step RO.10.
    }

    /// <summary>Reopened feature: does the transform read BEFORE a write, and does the geometry change
    /// from a repeated write of the SAME transform?</summary>
    /// <remarks>The separation order §5 demands explicitly: "wrong object match" vs "Position read
    /// peculiarities". Both give the SAME answer "translate" and cannot be told apart by reading — only a
    /// REPEATED WRITE of the SAME transform separates them: if element 1 is a FOREIGN element, the write
    /// rotates the body 180° about Z from its initial position and the geometry MOVES; if element 1 is
    /// that very feature and only the read was unreadable, writing the same transform changes the geometry
    /// NOTHING and the read after the write becomes correct. A third reopen with a DIFFERENT written angle
    /// (90°) separates "defect at 180°" from "defect reading any reopened transform": the angle changes,
    /// the read condition does not. History: docs/decisions/probes.md#ro-readback</remarks>
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
            // (a) Read BEFORE any write: what the reopened feature returns.
            var before = ReadElement(container, 1, step, "элемент 1 до записи");
            var bodies0 = Describe(BodyRows(part));
            step.Observe("до записи: " + before.Text);
            step.Observe("геометрия, восстановленная ИЗ ФАЙЛА: " + bodies0);

            // (b) Rebuild without a write: does a rebuild make the transform readable?
            doc.RebuildDocument();
            var afterRebuild = ReadElement(container, 1, step, "элемент 1 после пересборки");
            step.Observe("после пересборки БЕЗ записи: " + afterRebuild.Text);

            if (container.BodyRepositions[1] is not IBodyReposition second)
            {
                step.Fail("элемент 1 не отдаёт IBodyReposition");
                return;
            }

            // (c) Write of a KNOWN transform: it must both read and apply. The body must land in the
            // extent of the ROTATION FROM THE TRANSLATION, not of the original body: translation
            // (10,0,0) gives [10,30]×[0,10]×[0,5], 180° about Z gives [−30,−10]…[−10,0]. This is what
            // separates "element 1 is the second feature" from "element 1 is the first".
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

            // (d) REPEATED write of the SAME transform. It is now certainly written — and if the geometry
            // does not move on the repeat, the written value WAS that transform: so the zero read before
            // the write was a READ defect, not an object substitution.
            second.Position.InitByMatrix3D(RotationZ(180d));
            var repeated = second.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var afterRepeat = ReadElement(container, 1, step, "элемент 1 после повторной записи 180°");
            var bodies2 = Describe(BodyRows(part));
            step.Observe("повторная запись ТОГО ЖЕ 180°: Update()=" + repeated
                + ", чтение: " + afterRepeat.Text);
            step.Observe("геометрия после повторной записи: " + bodies2);

            // (e) Positive write control: a DIFFERENT transform must move the geometry.
            second.Position.InitByMatrix3D(RotationZ(90d));
            var updated90 = second.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var after90 = ReadElement(container, 1, step, "элемент 1 после записи 90°");
            var at90 = BodyRows(part).FirstOrDefault(r => Near(r, -10d, 10d, 0d, 0d, 30d, Az));
            step.Observe("запись 90° вокруг Z: Update()=" + updated90 + ", чтение: " + after90.Text);
            step.Observe("геометрия после 90°: " + Describe(BodyRows(part)));

            // (f) Third reopen: the model holds 90°, the read condition is the same — WITHOUT a write.
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

    /// <summary>Geometry comparison after a write — by the body description STRING: it carries the volume
    /// and both extents, exactly what an idempotent write must keep equal.</summary>
    private static bool SameGeometry(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>The element's input body — by <c>IBody7.BodyId</c>; the collection body index is not identity.</summary>
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

    // ══════════════════════════════════════════════════════════════ angle sweep ══

    /// <summary>Angle regression on a SINGLE feature: 0°, ±90°, 180°, near 180°, 270°, 360°, an arbitrary
    /// axis, an axis through a point. Both the derivation and the axes themselves are read — so a "wrong
    /// kind" can be attributed either to the derivation or to the numbers read.</summary>
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

    // ══════════════════════════════════════════════════════════════ verdict ══

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

    // ══════════════════════════════════════════════════════════════ instruments ══

    /// <summary>Derives the kind and parameters from the local-system axes. VERBATIM repeat of the product
    /// algorithm (<c>Api5Session.SolidRead.DeriveReposition</c>), because the probe must measure the SAME
    /// branch, not a similar one: should they diverge, the instrument would be measured, not the
    /// product.</summary>
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

    /// <summary>Collection order — by axes: unit for a translation, not for a rotation.</summary>
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

    /// <summary>Tree features — by the same route as the product: <c>EntityCollection(110)</c>
    /// (<c>o3d_operationElement</c>), not <c>GetFeature().SubFeatureCollection(...)</c>. The route is
    /// chosen by measurement, not convenience: MEASURED (first step of this probe),
    /// <c>SubFeatureCollection(true, false)</c> returns an EMPTY collection on a live model, whereas
    /// <c>EntityCollection(110)</c> returns the features — an instrument reading emptiness would declare
    /// "no features" where there are two. History: docs/decisions/probes.md#ro-route</summary>
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

    // ══════════════════════════════════════════════════════════════ matrices ══

    /// <summary>4×4 matrix with identity orientation and the given translation.</summary>
    private static double[] Translation(double x, double y, double z) => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        x, y, z, 1d,
    };

    /// <summary>Rotation about Z by an angle in degrees, 4×4 matrix by columns.</summary>
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

    /// <summary>Rotation by an angle about an arbitrary direction (normalised) — Rodrigues formula.</summary>
    private static double[] RotationAbout(double[] direction, double angleDeg)
    {
        var norm = Norm(direction);
        var (x, y, z) = (direction[0] / norm, direction[1] / norm, direction[2] / norm);
        var r = angleDeg * Math.PI / 180d;
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        var t = 1d - c;

        // Rows — Rodrigues R = t·nnᵀ + c·I + s·[n]×, where [n]× = [[0,−nz,ny],[nz,0,−nx],[−ny,nx,0]].
        // The signs of the s-terms are TESTED on the 120°-about-(1,1,1)/√3 matrix: it must be the cyclic
        // permutation [[0,0,1],[1,0,0],[0,1,0]], and the opposite sign would give exactly the INVERSE
        // rotation — turning the body the wrong way, so the probe would report a product defect where its
        // own reference is wrong (reference case 4/tan: the instrument must be clean before any
        // conclusion about the product). History: docs/decisions/probes.md#ro-rodrigues
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
            // Not the subject of this step.
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

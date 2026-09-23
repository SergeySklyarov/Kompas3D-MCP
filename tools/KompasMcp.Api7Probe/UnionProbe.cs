using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба U — ГРАНИЦА ПРИМЕНИМОСТИ обычного объединения: что продукт сам говорит об условиях
/// применимости, как он отвечает на входы вне этих условий и чем отказ отличается от ошибки вызова.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем.</b> Разнесённые тела лежат ВНЕ объявленной области применимости объединения, поэтому
/// «положительное объединение двух разнесённых кубов» — ошибочное требование, а не невыполненное
/// (решение заказчика 19.09.2026; обычное объединение КОМПАС требует пересечения или общей
/// поверхности). Проба отвечает на три вопроса о ГРАНИЦЕ: какое условие называет сам продукт, что
/// он делает на входах вне условия и не является ли отказ следствием неправильно собранного вызова.
/// </para>
/// <para>
/// <b>Три шага, и каждый нужен другому.</b>
/// </para>
/// <list type="number">
/// <item>условие применимости, прочитанное из справки ПОСТАВКИ
/// (<c>Help/KOMPAS_ru-RU.zip</c>, раздел «Булева операция над телами → Выполнение булевой операции») —
/// это утверждение о ПРОДУКТЕ, а не о приборе;</item>
/// <item>ответ ядра на разнесённые тела (<c>IBoolean</c>): отказ, названный кодом, при НЕИЗМЕННОЙ
/// геометрии — это и есть проверяемая граница, а не дефект продукта;</item>
/// <item>КОНТРОЛЬ на телах с общей гранью тем же самым вызовом: там объединение обязано работать
/// (измерено BO.7), поэтому отказ на шаге 2 отделяется от «вызов собран неверно» именно этим шагом,
/// и без него отказ не доказывал бы ничего.</item>
/// </list>
/// <para>
/// <b>Чего проба НЕ делает.</b> Она не ищет обходные маршруты объединения и не перебирает
/// предполагаемые COM-члены: по правилам проекта недокументированный маршрут не исследуется и в
/// требования не включается, а наличие имени в TLB или обёртке документированного маршрута не
/// заменяет. Шаги «второй маршрут» и «живая поверхность членов» удалены из пробы 19.09.2026 вместе
/// со снятым требованием положительного несвязного объединения.
/// </para>
/// </remarks>
internal sealed class UnionProbe
{
    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public UnionProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "disconnected-union.json"),
            Path.Combine(options.ReportDir, "disconnected-union.md"));

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
            Documentation();
            DisjointBoolean();
            ContactControl();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ U.0 ══

    /// <summary>
    /// Что продукт сам говорит об условиях применимости объединения. Это утверждение о ПРОДУКТЕ,
    /// а не о приборе: справка поставки — часть поставки.
    /// </summary>
    private void Documentation()
    {
        var step = _report.Begin("U.0", "Условия применимости объединения по справке самого продукта",
            "Считает ли КОМПАС-3D v24 объединение разнесённых тел поддерживаемой операцией?");

        const string aggregate =
            "Справка КОМПАС-3D v24, «Булева операция над телами → Выполнение булевой операции»: "
            + "«Объединение тел возможно, если они пересекаются или имеют общую поверхность, "
            + "а вычитание и пересечение возможны, только если тела пересекаются.»";
        const string parts =
            "Справка КОМПАС-3D v24, «Булевы операции над деталями → Объединение компонентов»: "
            + "«Объединяемые детали сборки должны пересекаться друг с другом или иметь совпадающие "
            + "грани.» и «Объединение деталей возможно, если каждая из них содержит по одному телу. "
            + "Многотельные детали объединить нельзя.»";
        const string partsMulti =
            "Справка КОМПАС-3D v24, «Выполнение булевой операции», п. 6: «Если в результате операции "
            + "образуется тело из нескольких частей, то после выполнения операции запускается процесс "
            + "изменения набора частей» — то есть многокусочное тело есть представление РЕЗУЛЬТАТА, "
            + "а не способ соединения несвязных тел.";

        step.Observe(aggregate);
        step.Observe(parts);
        step.Observe(partsMulti);
        step.Data["help_aggregate"] = aggregate;
        step.Data["help_union_components"] = parts;
        step.Data["help_multipart_result"] = partsMulti;
        step.Observe("Источник: Help/KOMPAS_ru-RU.zip в самой поставке v24; текст извлечён из "
            + "jstopics/cm_aggregate_oper.js и jstopics/cm_make_union_comps.js");
        step.Data["source"] = "D:\\Programs\\KOMPAS-3Dv24\\Help\\KOMPAS_ru-RU.zip";
        step.Pass("условие применимости объединения прочитано из справки поставки");
    }

    // ══════════════════════════════════════════════════════════════ U.1 ══

    /// <summary>
    /// Разнесённые тела маршрутом <c>IBoolean</c> — ответ ядра на входы ВНЕ объявленных условий
    /// применимости. Ожидание здесь — ОТКАЗ, и проверяется он не сам по себе, а вместе с контролем
    /// U.2: без контроля «отказ» неотличим от «вызов собран неверно».
    /// </summary>
    private void DisjointBoolean()
    {
        var step = _report.Begin("U.1", "Разнесённые кубы: IBoolean — ответ на входы вне условий применимости",
            "Отвергает ли ядро объединение [0,10]³ ∪ [20,30]×[0,10]×[0,10] и остаётся ли геометрия неизменной?");
        var doc = NewPart(out var part);
        try
        {
            if (!Pair(doc, part, step))
            {
                step.Fail("тела не построены");
                return;
            }

            var bodies = BodyRows(part);
            step.Observe("до операции тел " + bodies.Count + ": " + Describe(bodies));
            var (ok, note) = BooleanUnion(doc, part, bodies[0], new[] { bodies[1] }, step);
            var after = BodyRows(part);
            step.Data["accepted"] = ok;
            step.Data["note"] = note;
            step.Data["bodies_after"] = after.Count;
            step.Data["volume_after"] = after.Sum(b => b.Volume ?? 0d);
            step.Observe("после операции тел " + after.Count + ", суммарный объём " + Api5.Num(after.Sum(b => b.Volume ?? 0d))
                + " (ожидание по условиям применимости: отказ, тела 2, суммарный объём 2000)");

            if (!ok && after.Count == 2 && Math.Abs(after.Sum(b => b.Volume ?? 0d) - 2000d) < 1d)
            {
                step.Pass("измерено: отказ воспроизведён, геометрия не изменилась — " + note);
            }
            else if (ok)
            {
                step.Pass("ядро ПРИНЯЛО объединение разнесённых тел — прежнее измерение не воспроизвелось, "
                    + "и это меняет границу, а не требование");
            }
            else
            {
                step.Unknown("исход не совпал ни с отказом без изменений, ни с успехом: " + note);
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ U.2 ══

    /// <summary>
    /// Контроль на КОНТАКТНЫХ телах тем же самым вызовом. Без него отказ U.1 неотличим от
    /// «вызов собран неверно»: различие «ядро не принимает несвязные тела» и «ядро не принимает
    /// ничего» держится ровно на этом опыте, поэтому он идёт тем же <see cref="BooleanUnion"/>,
    /// что и U.1, и отличается только геометрией.
    /// </summary>
    private void ContactControl()
    {
        var step = _report.Begin("U.2", "Контроль: тот же вызов на КОНТАКТНЫХ телах",
            "Принимает ли тот же самый вызов объединение тел с общей гранью?");
        var doc = NewPart(out var part);
        try
        {
            if (!ExtrudeRect(doc, part, 0d, 10d, 0d, 10d, 10d, step, "C1")
                || !ExtrudeRect(doc, part, 10d, 20d, 0d, 10d, 10d, step, "C2"))
            {
                step.Fail("тела не построены");
                return;
            }

            var bodies = BodyRows(part);
            step.Observe("до операции тел " + bodies.Count + ": " + Describe(bodies));
            if (bodies.Count != 2)
            {
                step.Fail("ожидались два тела до операции, прочитано " + bodies.Count);
                return;
            }

            var (ok, note) = BooleanUnion(doc, part, bodies[0], new[] { bodies[1] }, step);
            var after = BodyRows(part);
            var volume = after.Sum(b => b.Volume ?? 0d);
            step.Data["accepted"] = ok;
            step.Data["note"] = note;
            step.Data["bodies_after"] = after.Count;
            step.Data["volume_after"] = volume;
            step.Data["gabarit_matches"] = after.Any(b => Near(b, 0d, 0d, 0d, 20d, 10d, 10d));
            step.Observe("после операции тел " + after.Count + ", суммарный объём " + Api5.Num(volume)
                + ": " + Describe(after));
            step.Observe("ожидание: одно тело, V=2000, габарит (0,0,0)…(20,10,10)");

            if (ok && after.Count == 1 && Math.Abs(volume - 2000d) < 1d
                && after.Any(b => Near(b, 0d, 0d, 0d, 20d, 10d, 10d)))
            {
                step.Pass("тот же вызов на контактных телах принят и дал одно тело 2000: отказ U.1 "
                    + "измеряет ограничение ядра, а не ошибку сборки вызова");
            }
            else if (!ok)
            {
                step.Fail("тот же вызов отвергнут и на контактных телах — сборка вызова недостоверна, "
                    + "отказ U.1 ничего не говорит о несвязности: " + note);
            }
            else
            {
                step.Unknown("принят, но результат не совпал с ожиданием: тел " + after.Count
                    + ", объём " + Api5.Num(volume) + ", " + note);
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private (bool Ok, string? Note) BooleanUnion(
        ksDocument3D doc, ksPart part, BodyRow target, IReadOnlyList<BodyRow> tools, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.Booleans?.Add() is not { } created || created is not IBoolean boolean)
            {
                return (false, "Booleans.Add() недоступен");
            }

            if (Transfer(target) is not IKompasAPIObject target7)
            {
                return (false, "тело-цель не переносится в API7");
            }

            var tools7 = new object[tools.Count];
            for (var i = 0; i < tools.Count; i++)
            {
                if (Transfer(tools[i]) is not { } tool7)
                {
                    return (false, "инструмент " + i + " не переносится в API7");
                }

                tools7[i] = tool7;
            }

            boolean.BaseObject = target7;
            boolean.ModifyObjects = tools7;
            boolean.BooleanType = ksBooleanType.ksUnion;
            boolean.SaveCopyBaseObject = false;
            boolean.SaveCopyModifyObjects = false;
            var updated = boolean.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("вызов: BooleanType=ksUnion, инструментов=" + tools.Count + ", Update()=" + updated);
            return updated ? (true, null) : (false, "IBoolean.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    private bool Pair(ksDocument3D doc, ksPart part, ProbeStep step) =>
        ExtrudeRect(doc, part, 0d, 10d, 0d, 10d, 10d, step, "D1")
        && ExtrudeRect(doc, part, 20d, 30d, 0d, 10d, 10d, step, "D2");

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    private void Launch()
    {
        var step = _report.Begin("U.Z0", "Свой невидимый сеанс КОМПАС-3D v24", "Сеанс поднимается сам?");
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
        step.Observe("свой процесс: " + _ownPid);
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("U.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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

    private static void TryClose(ksDocument3D? doc)
    {
        try
        {
            doc?.close();
        }
        catch (Exception)
        {
            // Не предмет этого шага.
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

    private object? Transfer(BodyRow row)
    {
        try
        {
            return _app.TransferInterface(row.Element!, 2, 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        return created;
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

                double[]? min = null;
                double[]? max = null;
                if (element is ksBody body)
                {
                    (double[] Min, double[] Max)? box = null;
                    if (Api5.SafeBool(() =>
                        {
                            var ok = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                            if (ok)
                            {
                                box = (new[] { x1, y1, z1 }, new[] { x2, y2, z2 });
                            }

                            return ok;
                        }) == true
                        && box is { } value)
                    {
                        min = value.Min;
                        max = value.Max;
                    }
                }

                rows.Add(new BodyRow(i, element, Api5.BodyVolume(element), min, max));
            }
        }
        catch (Exception)
        {
            // Возвращается прочитанное.
        }

        return rows;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is { Length: 3 } min && row.Max is { Length: 3 } max
        && Math.Abs(min[0] - x0) < 0.01 && Math.Abs(min[1] - y0) < 0.01 && Math.Abs(min[2] - z0) < 0.01
        && Math.Abs(max[0] - x1) < 0.01 && Math.Abs(max[1] - y1) < 0.01 && Math.Abs(max[2] - z1) < 0.01;

    private sealed record BodyRow(int Index, object? Element, double? Volume, double[]? Min, double[]? Max)
    {
        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>"
                : "(" + string.Join(", ", Min.Select(v => Api5.Num(v))) + ")…("
                    + string.Join(", ", Max.Select(v => Api5.Num(v))) + ")");
    }
}

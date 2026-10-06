using System.Diagnostics;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Throwaway ladder for the DOCUMENTED external-variable route: <c>IProcessWithVariables</c>.</summary>
/// <remarks>
/// Measures whether a genuine external model variable can be created headlessly on a top-level part by
/// the documented route <c>IProcess3D</c> → <c>QI(IProcessWithVariables)</c> → <c>SetControlExpression</c>.
/// Nothing here ships: a research instrument (ADR-003 §3) whose output is a fact about KOMPAS, recorded
/// with the HResult of every refused call, each candidate getting its own verdict rather than a refusal
/// inferred from a route list. History: docs/decisions/variables-material.md#reference
/// </remarks>
internal sealed class VmControlExpressionProbe
{
    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;
    private ksDocument3D? _doc;
    private ksPart? _part;

    public VmControlExpressionProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "vm-control-expression.json"),
            Path.Combine(options.ReportDir, "vm-control-expression.md"));
    }

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
            Walk();
        }
        finally
        {
            Shutdown();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("CX.0", "Сеанс КОМПАС",
            "Отвечает ли живой процесс документа на документированный маршрут внешних переменных?");
        var clock = Stopwatch.StartNew();
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
        clock.Stop();
        step.Observe("экземпляр API5 за " + clock.ElapsedMilliseconds + " мс, свой процесс " + _ownPid);
        step.Pass("сеанс поднят");
    }

    private void Walk()
    {
        var build = _report.Begin("CX.1", "Деталь 100×80×10 со эскизом",
            "Есть ли у детали эскиз, к контролам которого можно привязать переменную?");
        _doc = NewPart(build);
        if (_doc is null)
        {
            build.Fail("документ не создан");
            return;
        }

        try
        {
            if (_doc.GetPart(-1) is not ksPart part)
            {
                build.Fail("GetPart(-1) не дал верхний компонент");
                return;
            }

            _part = part;
            if (Api5.BasePlate(part, 100, 80, 10, build, "vmcx") is null)
            {
                build.Fail("базовая геометрия не построена");
                return;
            }

            var volume = Api5.Volume(part);
            build.Observe("объём базовой детали: " + Api5.Num(volume) + " мм³ (ожидание 80000)");
            build.Pass("деталь построена");

            // ── CX.2: is IProcessWithVariables reachable at all from this document? ────────────────
            var reach = _report.Begin("CX.2", "Достижим ли IProcessWithVariables от документа",
                "Отвечает ли живой объект документа на QI(IProcessWithVariables)?");
            var reachable = ReachProcess(reach, out var processWithVariables);

            // ── CX.2b: the API7 IPart7 route, which the interop shows carries AddVariable ────────
            // The help documents `IProcessWithVariables` for the PROCESS object; the interop separately
            // shows `IPart7.AddVariable(Name, Value, Note) → Variable7`. Both are candidates, and the
            // candidate that actually grows ksPart.VariableCollection is the answer — measured, not
            // inferred from the interface list.
            var part7Step = _report.Begin("CX.2b", "Маршрут API7 IPart7.AddVariable",
                "Создаёт ли документированный AddVariable внешнюю переменную у верхнего компонента?");
            ProbePart7Add(part7Step);

            // ── CX.2c: the documented DOCUMENT-level variable table ───────────────────────────────
            // `IPart7::VariableTable` → `IVariableTable` is documented as "считывать, изменять,
            // добавлять параметры таблицы переменных компонента", with editing allowed for the top
            // component. It is a different object from ksPart.VariableCollection, and the route that
            // actually carries external variables may be this one — measured, not presumed.
            var tableStep = _report.Begin("CX.2c", "Маршрут API7 IPart7.VariableTable",
                "Попадает ли переменная, добавленная в таблицу переменных, в коллекцию верхнего компонента?");
            ProbeVariableTable(tableStep);

            if (!reachable)
            {
                return;
            }

            // ── CX.3: the candidate CONTROLS the probe can reach on the document ──────────────────
            var controls = _report.Begin("CX.3", "Какие IPropertyControl достижимы headless",
                "Есть ли на документе контрол, который вообще можно связать с переменной?");
            var candidates = CollectControls(controls);
            controls.Data["candidates"] = candidates.Count;
            controls.Pass("кандидатов собрано: " + candidates.Count);

            // ── CX.4: the documented call itself, on every candidate ──────────────────────────────
            var apply = _report.Begin("CX.4", "SetControlExpression на достижимых контролах",
                "Создаёт ли документированный вызов внешнюю переменную «Глубина»?");
            var anySucceeded = false;
            foreach (var (label, control) in candidates)
            {
                var ok = Api5.SafeObject(() => processWithVariables!.SetControlExpression(
                    control, VmReferenceProbe.DepthVariable, "10"));
                apply.Observe("SetControlExpression(" + label + ", «" + VmReferenceProbe.DepthVariable + "») → "
                    + Api5.Raw(ok));
                if (ok is true)
                {
                    anySucceeded = true;
                }
            }

            if (candidates.Count == 0)
            {
                apply.Fail("ни одного IPropertyControl не достигнуто headless — связывать нечего, "
                    + "и это НАЗВАНО, а не обойдено");
                return;
            }

            // ── CX.5: did the part collection grow? The claim is the collection, not the return ────
            var verify = _report.Begin("CX.5", "Появилась ли переменная в ksPart.VariableCollection",
                "Растёт ли коллекция внешних переменных верхнего компонента от этого маршрута?");
            var result = ApplyAndVerify();
            foreach (var pair in result)
            {
                verify.Data[pair.Key] = pair.Value;
            }
            if (result.TryGetValue("external_count", out var countObj) && countObj is int count && count > 0)
            {
                verify.Pass("внешняя переменная появилась: count=" + count);
            }
            else
            {
                verify.Fail("коллекция верхнего компонента не выросла даже после документированного вызова"
                    + (anySucceeded ? " (SetControlExpression вернул TRUE)" : " (SetControlExpression не вернул TRUE)"));
            }
        }
        catch (Exception ex)
        {
            build.Fail("проба бросила " + ex.GetType().Name + ": " + ex.Message);
            build.Errors.Add(ex.ToString());
        }
        finally
        {
            try
            {
                _doc?.close();
            }
            catch (Exception)
            {
                // Session teardown is not this probe's subject.
            }
        }
    }

    private void ProbePart7Add(ProbeStep step)
    {
        if (_doc is null || _part is null)
        {
            step.Fail("документа или детали нет");
            return;
        }

        var document7 = Api5.SafeObject(() => _app.TransferInterface(_doc, 2, 0)) as IKompasDocument3D;
        if (document7 is null)
        {
            step.Fail("документ не отвечает на QI(IKompasDocument3D)");
            return;
        }

        var top = document7.TopPart;
        step.Observe("TopPart → " + Api5.RuntimeName(top));
        if (top is not IPart7 part7)
        {
            step.Fail("TopPart не отвечает на QI(IPart7) — AddVariable недостижим");
            return;
        }

        var before = _part.VariableCollection() is ksVariableCollection vc
            ? Api5.SafeInt(vc.GetCount) ?? -1
            : -1;
        step.Data["count_before"] = before;

        // LIMIT, named before the call: the third parameter of AddVariable is a NOTE («примечание»), NOT
        // an expression. Passing «Глубина*2» here cannot create a dependent formula and never could, so
        // this call does NOT test "create a dependent variable" — it only tests whether the call puts a
        // variable into the top component's collection. The dependency itself has to be made by linking
        // a control (SetControlExpression), which is the documented route.
        var made = Api5.SafeObject(() => part7.AddVariable(VmReferenceProbe.DepthVariable, 10d, "глубина эталона"));
        step.Observe("IPart7.AddVariable(«" + VmReferenceProbe.DepthVariable + "», 10) → "
            + Api5.RuntimeName(made));

        var second = Api5.SafeObject(() => part7.AddVariable(
            VmReferenceProbe.TotalVariable, 20d, VmReferenceProbe.TotalExpression));
        step.Observe("IPart7.AddVariable(«" + VmReferenceProbe.TotalVariable + "», 20, «"
            + VmReferenceProbe.TotalExpression + "») → " + Api5.RuntimeName(second));

        var rebuilt = Api5.SafeBool(() => _part.RebuildModel());
        var after = _part.VariableCollection() is ksVariableCollection vc2
            ? Api5.SafeInt(vc2.GetCount) ?? -1
            : -1;
        step.Data["rebuild_returned"] = Api5.Raw(rebuilt);
        step.Data["count_after"] = after;

        var names = new List<string>();
        if (_part.VariableCollection() is ksVariableCollection coll)
        {
            var n = Api5.SafeInt(coll.GetCount) ?? 0;
            for (var i = 0; i < n; i++)
            {
                if (Api5.SafeObject(() => coll.GetByIndex(i)) is ksVariable v)
                {
                    var name = Api5.SafeObject(() => v.name)?.ToString() ?? "?";
                    names.Add(name + "(external=" + Api5.Raw(Api5.SafeObject(() => v.external)) + ")");
                }
            }
        }

        step.Data["names"] = names;
        step.Observe("коллекция верхнего компонента " + before + " → " + after
            + (names.Count == 0 ? string.Empty : ", имена: " + string.Join(", ", names)));

        if (after > before)
        {
            step.Pass("IPart7.AddVariable наполняет коллекцию верхнего компонента: " + before + " → " + after);
        }
        else
        {
            step.Fail("IPart7.AddVariable коллекцию верхнего компонента не наполняет: " + before + " → " + after);
        }
    }

    private void ProbeVariableTable(ProbeStep step)
    {
        if (_doc is null || _part is null)
        {
            step.Fail("документа или детали нет");
            return;
        }

        var document7 = Api5.SafeObject(() => _app.TransferInterface(_doc, 2, 0)) as IKompasDocument3D;
        if (document7?.TopPart is not IPart7 part7)
        {
            step.Fail("TopPart не отвечает на QI(IPart7) — VariableTable недостижима");
            return;
        }

        var table = Api5.SafeObject(() => part7.VariableTable);
        step.Observe("IPart7.VariableTable → " + Api5.RuntimeName(table));
        if (table is not IVariableTable variableTable)
        {
            step.Fail("IPart7.VariableTable не дал IVariableTable");
            return;
        }

        var columns = Api5.SafeInt(() => variableTable.ColumnsCount) ?? -1;
        var rows = Api5.SafeInt(() => variableTable.RowsCount) ?? -1;
        step.Observe("таблица переменных до правки: колонок " + columns + ", строк " + rows);

        // DOC: AddRow/AddColumn answer an index, or -1 on failure, and create the table if the
        // component had none. The FIRST read above came from a table that did not exist yet, so its
        // -1 must not be quoted as "there are no columns" once the table is created.
        var rowsAfter = Api5.SafeInt(() => variableTable.AddRow("vm-reference"));
        step.Observe("AddRow(«vm-reference») → " + rowsAfter);
        var colAfter = Api5.SafeInt(() => variableTable.AddColumn(VmReferenceProbe.DepthVariable));
        step.Observe("AddColumn(«" + VmReferenceProbe.DepthVariable + "») → " + colAfter);

        var columnsNow = Api5.SafeInt(() => variableTable.ColumnsCount) ?? -1;
        var rowsNow = Api5.SafeInt(() => variableTable.RowsCount) ?? -1;
        step.Observe("таблица после правки: колонок " + columnsNow + ", строк " + rowsNow);

        // Write the driving value into the cell that the new column and row opened.
        var written = Api5.SafeObject(() =>
        {
            variableTable.Cell[0, 0] = 10d;
            return "written";
        });
        step.Observe("Cell[row 0, col 0] = 10 → " + Api5.Raw(written));

        var comment = Api5.SafeObject(() => variableTable.Comment[0]);
        var varName = Api5.SafeObject(() => variableTable.VarName[0]);
        step.Observe("Comment(0) → «" + Api5.Raw(comment) + "», VarName[0] → «" + Api5.Raw(varName) + "»");

        var applied = Api5.SafeObject(() => variableTable.ApplyVars(0));
        step.Observe("ApplyVars(0) → " + Api5.Raw(applied));

        var rebuilt = Api5.SafeBool(() => _part.RebuildModel());
        var count = _part.VariableCollection() is ksVariableCollection vc
            ? Api5.SafeInt(vc.GetCount) ?? -1
            : -1;
        step.Data["columns"] = columns;
        step.Data["rows"] = rows;
        step.Data["rebuild_returned"] = Api5.Raw(rebuilt);
        step.Data["part_collection_after"] = count;
        step.Observe("коллекция верхнего компонента после ApplyVars+RebuildModel: " + count);

        if (count > 0)
        {
            step.Pass("таблица переменных доводит переменную до коллекции верхнего компонента: count=" + count);
        }
        else
        {
            // The measured rows/columns above are the TABLE's own counters, not the model's variables.
            // DOC: only the top-component collection (ksPart.VariableCollection) holds model variables,
            // so a populated table with count=0 is a fact about the TABLE, and NOT evidence that model
            // variables appeared. The negative is read off the collection, never off RowsCount.
            step.Fail("таблица переменных не наполняет коллекцию верхнего компонента (count=" + count
                + "); наличие строк/колонок таблицы НЕ доказывает, что переменные модели появились,"
                + " поэтому маршрут НЕ подтверждён как источник внешних переменных");
        }
    }

    private bool ReachProcess(ProbeStep step, out IProcessWithVariables? processWithVariables)
    {
        processWithVariables = null;
        if (_doc is null)
        {
            step.Fail("документа нет");
            return false;
        }

        try
        {
            var document7 = _app.TransferInterface(_doc, 2 /* ksAPI7Dual */, 0) as IKompasDocument3D;
            step.Observe("TransferInterface(document, ksAPI7Dual) → " + Api5.RuntimeName(document7));
            if (document7 is null)
            {
                step.Fail("документ не отвечает на QI(IKompasDocument3D)");
                return false;
            }

            // ASSUMPTION, and it is named as one: the help page does NOT establish the hop
            // `TopPart → QueryInterface(IProcess3D)`. It only documents `IProcessWithVariables` as an
            // ADDITIONAL interface of the process `IProcess3D`. So a null HERE is a fact about the
            // OBJECT this probe checked — the interface was not obtained from it — and NOT a negative
            // check of the documented process route. Repeating this unconfirmed hop is deliberately not
            // ordered: it would test the probe's own premise, not the documented route.
            var process = document7.TopPart as IProcess3D;
            step.Observe("TopPart → IProcess3D: " + Api5.RuntimeName(process));
            if (process is null)
            {
                step.Fail("TopPart не отвечает на QI(IProcess3D): это факт о ПРОВЕРЕННОМ ОБЪЕКТЕ "
                    + "(у него интерфейс не получен), а НЕ отрицание документированного маршрута "
                    + "процесса. Переход TopPart → QI(IProcess3D) справкой не установлен");
                return false;
            }

            // The documented hop: IProcessWithVariables is an ADDITIONAL interface of IProcess3D,
            // obtained by IUnknown::QueryInterface. The C# cast IS that QueryInterface.
            processWithVariables = process as IProcessWithVariables;
            step.Observe("QI(IProcessWithVariables) → " + Api5.RuntimeName(processWithVariables));
            if (processWithVariables is null)
            {
                step.Fail("TopPart не отвечает на QI(IProcessWithVariables): документированного маршрута "
                    + "внешних переменных у этого объекта нет");
                return false;
            }

            step.Pass("IProcessWithVariables достижим");
            return true;
        }
        catch (Exception ex)
        {
            step.Fail("получение интерфейса бросило " + HResult.Describe(ex));
            return false;
        }
    }

    private List<(string Label, IPropertyControl Control)> CollectControls(ProbeStep step)
    {
        var found = new List<(string, IPropertyControl)>();

        // Candidate A: IProcess3D itself may present the collection of process controls. The help lists
        // IPropertyControls::Item / IPropertyControls::Add as the documented sources of a control, and
        // IPropertyControl::Value carries the control's value — but nothing in the part's own public
        // surface is documented to expose THAT collection, so every route is tried and each miss named.
        foreach (var (label, candidate) in CandidateHosts())
        {
            if (candidate is IPropertyControls collection)
            {
                var count = Api5.SafeInt(() => collection.Count);
                step.Observe("носитель «" + label + "» отвечает на QI(IPropertyControls), элементов="
                    + Api5.Raw(count));
                var n = count ?? 0;
                for (var i = 0; i < n; i++)
                {
                    // IPropertyControls::Item takes a VARIANT index (the interop exposes it as a
                    // parameterised property, so late binding is used for the index).
                    var item = Api5.SafeObject(() => collection.GetType().GetProperty("Item")
                        ?.GetValue(collection, [i]));
                    if (item is IPropertyControl control)
                    {
                        found.Add((label + "[" + i + "]", control));
                    }
                }
            }
            else
            {
                step.Observe("носитель «" + label + "» (" + Api5.RuntimeName(candidate)
                    + ") на QI(IPropertyControls) НЕ отвечает");
            }
        }

        return found;
    }

    private IEnumerable<(string Label, object Candidate)> CandidateHosts()
    {
        if (_doc is not null)
        {
            yield return ("document", _doc);
            var document7 = Api5.SafeObject(() => _app.TransferInterface(_doc, 2, 0));
            if (document7 is not null)
            {
                yield return ("document7", document7);
            }
        }

        if (_part is not null)
        {
            yield return ("part5", _part);
        }
    }

    private Dictionary<string, object?> ApplyAndVerify()
    {
        var data = new Dictionary<string, object?>();
        if (_part is null)
        {
            data["error"] = "деталь недоступна";
            return data;
        }

        var before = _part.VariableCollection() is ksVariableCollection vc
            ? Api5.SafeInt(vc.GetCount) ?? -1
            : -1;
        data["external_count_before"] = before;

        var rebuilt = Api5.SafeBool(() => _part.RebuildModel());
        data["rebuild_returned"] = Api5.Raw(rebuilt);

        var after = _part.VariableCollection() is ksVariableCollection vc2
            ? Api5.SafeInt(vc2.GetCount) ?? -1
            : -1;
        data["external_count"] = after;

        if (_part.VariableCollection() is ksVariableCollection coll)
        {
            var n = Api5.SafeInt(coll.GetCount) ?? 0;
            var names = new List<string>();
            for (var i = 0; i < n; i++)
            {
                if (Api5.SafeObject(() => coll.GetByIndex(i)) is ksVariable v)
                {
                    names.Add(Api5.SafeObject(() => v.name)?.ToString() ?? "?");
                }
            }

            data["names"] = names;
        }

        return data;
    }

    private ksDocument3D? NewPart(ProbeStep step)
    {
        try
        {
            if (_app.Document3D() is not ksDocument3D doc)
            {
                return null;
            }

            if (!doc.Create(false, false))
            {
                step.Observe("Create(false,false) → false");
                return null;
            }

            return doc;
        }
        catch (Exception ex)
        {
            step.Observe("создание бросило " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin("CX.Z", "Завершение сеанса", "Свой процесс уходит?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        var waited = 0;
        while (waited < 10000 && OwnProcessAlive())
        {
            System.Threading.Thread.Sleep(250);
            waited += 250;
        }

        step.Pass(OwnProcessAlive() ? "процесс пережил Quit()" : "свой процесс завершён через " + waited + " мс");
    }

    private bool OwnProcessAlive()
    {
        if (_ownPid == 0)
        {
            return false;
        }

        foreach (var p in Process.GetProcessesByName("KOMPAS"))
        {
            var pid = p.Id;
            p.Dispose();
            if (pid == _ownPid)
            {
                return true;
            }
        }

        return false;
    }
}

using System.Diagnostics;
using Kompas6API5;

namespace KompasMcp.Api7Probe;

/// <summary>Throwaway ladder: on which object does the DOCUMENTED <c>AddNewVariable</c> actually take?</summary>
/// <remarks>
/// The VM order needs a reference part whose EXTERNAL variables are created by documented means.
/// <c>AddNewVariable</c> is documented for <c>ksFeature</c>/<c>IFeature</c>; calling it on
/// <c>ksPart.VariableCollection()</c> and on the root <c>GetFeature()</c> both returned <c>null</c>
/// (probe VR, 06.10.2026). This probe walks the documented candidate objects — the sketch feature, the
/// extrusion feature, and the sub-feature tree — and reports, for each, whether
/// <c>AddNewVariable</c> yields a variable and whether it then surfaces in
/// <c>ksPart.VariableCollection</c>. The answer decides whether the reference can be parametrized at
/// all through API5, and is recorded as a fact about the kernel, not as a route guess.
/// </remarks>
internal sealed class VmVariableRouteProbe
{
    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public VmVariableRouteProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "vm-variable-route.json"),
            Path.Combine(options.ReportDir, "vm-variable-route.md"));
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
        var step = _report.Begin("R.0", "Сеанс КОМПАС",
            "Где документированный AddNewVariable действительно создаёт переменную?");
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
        var build = _report.Begin("R.1", "Деталь со эскизом и выдавливанием",
            "Есть ли настоящие признаки, кроме корня «Сборка»?");
        var doc = NewPart(build);
        if (doc is null)
        {
            build.Fail("документ не создан");
            return;
        }

        try
        {
            if (doc.GetPart(-1) is not ksPart part)
            {
                build.Fail("GetPart(-1) null");
                return;
            }

            var extrusion = Api5.BasePlate(part, 100, 80, 10, build, "vmroute");
            build.Observe("выдавливание: " + (extrusion is null ? "null" : Api5.RuntimeName(extrusion)));

            // Candidate 1 — the sketch FEATURE. The sketch entity's object may present as ksFeature.
            var sketch = ReadSketchEntity(part, build);
            TryCandidate(part, "эскиз", sketch, build);

            // Candidate 2 — the extrusion FEATURE object.
            TryCandidate(part, "выдавливание", extrusion, build);

            // Candidate 3 — each element of the sub-feature tree.
            TryTree(part, doc, build);

            // Candidate 4 — SetSourceVariables(true), then re-read the part collection.
            var sv = _report.Begin("R.5", "SetSourceVariables(true) на детали",
                "Разрешает ли это внешние переменные у верхнего компонента?");
            var applied = Api5.SafeBool(() => part.SetSourceVariables(true));
            sv.Observe("SetSourceVariables(true) → " + Api5.Raw(applied) + " (null = бросил)");
            var count = part.VariableCollection() is ksVariableCollection vc
                ? Api5.SafeInt(vc.GetCount) : null;
            sv.Observe("переменных у верхнего компонента: " + Api5.Raw(count));
            sv.Pass("измерено");

            // Candidate 5 — save → close → reopen in the SAME session, then read the part collection.
            // The in-session read may simply lag the write; the file on disk is the honest subject.
            var reopen = _report.Begin("R.6", "Save → close → reopen → чтение коллекции верхнего компонента",
                "Surfaces ли переменная признака в коллекции детали только ПОСЛЕ переоткрытия?");
            ProbeReopen(part, doc, reopen);
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
                doc.close();
            }
            catch (Exception)
            {
            }
        }
    }

    private static object? ReadSketchEntity(ksPart part, ProbeStep step)
    {
        // The sketch is the first NewEntity(o3d_sketch=5); read it by type from the part's entities.
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is null)
            {
                return null;
            }

            // The entity created by BasePlate is not returned, so it is found by name through the tree.
            if (part.GetFeature() is not ksFeature root
                || root.SubFeatureCollection(true, false) is not ksFeatureCollection tree)
            {
                step.Observe("дерево не прочитано");
                return null;
            }

            for (var i = 0; i < tree.GetCount(); i++)
            {
                var element = tree.GetByIndex(i);
                var name = Api5.Raw(element.GetType().GetProperty("name")?.GetValue(element));
                step.Observe("элемент дерева[" + i + "]: " + Api5.RuntimeName(element) + " имя=«" + name + "»");
                if (element is ksFeature feature
                    && (name ?? string.Empty).Contains("profile", StringComparison.OrdinalIgnoreCase))
                {
                    return feature;
                }
            }
        }
        catch (Exception ex)
        {
            step.Observe("поиск эскиза бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        return null;
    }

    private void TryCandidate(ksPart part, string label, object? candidate, ProbeStep step)
    {
        if (candidate is null)
        {
            step.Observe("кандидат «" + label + "»: объект не получен");
            return;
        }

        if (candidate is not ksFeature feature)
        {
            step.Observe("кандидат «" + label + "»: " + Api5.RuntimeName(candidate)
                + " — не ksFeature, VariableCollection недоступна");
            return;
        }

        var collection = Api5.SafeObject(() => feature.VariableCollection);
        if (collection is not ksVariableCollection variableCollection)
        {
            step.Observe("кандидат «" + label + "»: VariableCollection → " + Api5.Raw(collection));
            return;
        }

        var name = label + "_var";
        var made = Api5.SafeObject(() => variableCollection.AddNewVariable(name, 7d, string.Empty));
        var after = Api5.SafeInt(variableCollection.GetCount) ?? -1;
        step.Observe("кандидат «" + label + "»: AddNewVariable(«" + name + "», 7) → "
            + (made is null ? "null" : Api5.RuntimeName(made)) + ", count=" + after);

        // AddNewVariable returned null on this wrapper even where the count grew, so the handle is taken
        // by INDEX from the same collection — the documented GetByIndex — and its name is read back.
        var byIndex = Api5.SafeObject(() => variableCollection.GetByIndex(after - 1));
        if (byIndex is ksVariable variable)
        {
            step.Observe("кандидат «" + label + "»: GetByIndex(" + (after - 1) + ") имя=«"
                + Api5.SafeObject(() => variable.name) + "» external="
                + Api5.Raw(Api5.SafeObject(() => variable.external)) + " значение="
                + Api5.Raw(Api5.SafeDouble(() => variable.value)));
        }
        else
        {
            step.Observe("кандидат «" + label + "»: GetByIndex не дал ksVariable ("
                + Api5.Raw(byIndex) + ")");
        }

        var partCount = part.VariableCollection() is ksVariableCollection pc ? Api5.SafeInt(pc.GetCount) : null;
        var foundInPart = part.VariableCollection() is ksVariableCollection p2
            ? Api5.SafeObject(() => p2.GetByName(name, true, false))
            : null;
        step.Observe("кандидат «" + label + "»: в коллекции детали ДО RebuildModel count=" + Api5.Raw(partCount)
            + ", GetByName(«" + name + "») → " + Api5.Raw(foundInPart is null ? "null" : "найдено"));

        // The part collection is re-read only AFTER RebuildModel: the help says changes in the array do
        // not show in the model until RebuildModel is called, so a before-rebuild read proves nothing.
        var rebuilt = Api5.SafeBool(() => part.RebuildModel());
        var partCountAfter = part.VariableCollection() is ksVariableCollection pc2 ? Api5.SafeInt(pc2.GetCount) : null;
        var foundAfter = part.VariableCollection() is ksVariableCollection p3
            ? Api5.SafeObject(() => p3.GetByName(name, true, false))
            : null;
        step.Observe("кандидат «" + label + "»: RebuildModel=" + Api5.Raw(rebuilt)
            + ", в коллекции детали ПОСЛЕ count=" + Api5.Raw(partCountAfter)
            + ", GetByName(«" + name + "») → " + Api5.Raw(foundAfter is null ? "null" : "найдено"));
    }

    private void TryTree(ksPart part, ksDocument3D doc, ProbeStep step)
    {
        if (part.GetFeature() is not ksFeature root
            || root.SubFeatureCollection(true, false) is not ksFeatureCollection tree)
        {
            step.Observe("дерево не прочитано для перебора листьев");
            return;
        }

        for (var i = 0; i < tree.GetCount(); i++)
        {
            if (tree.GetByIndex(i) is ksFeature leaf)
            {
                TryCandidate(part, "лист[" + i + "]", leaf, step);
            }
        }
    }

    private void ProbeReopen(ksPart part, ksDocument3D doc, ProbeStep step)
    {
        var path = Path.Combine(_options.WorkDir, "vm-route-reopen.m3d");
        try
        {
            Directory.CreateDirectory(_options.WorkDir);
            var saved = doc.SaveAs(path);
            step.Observe("SaveAs(«" + path + "») → " + Api5.Raw(saved) + ", файл="
                + File.Exists(path));

            doc.close();

            if (_app.Document3D() is not ksDocument3D fresh)
            {
                step.Fail("повторный Document3D() не дал документ");
                return;
            }

            var opened = fresh.Open(path, true);
            step.Observe("Open(«" + path + "», true) → " + opened);
            if (!opened)
            {
                step.Fail("переоткрытие не удалось");
                return;
            }

            if (fresh.GetPart(-1) is not ksPart reopened)
            {
                step.Fail("GetPart(-1) на переоткрытом документе не дал деталь");
                return;
            }

            var count = reopened.VariableCollection() is ksVariableCollection vc
                ? Api5.SafeInt(vc.GetCount) : null;
            step.Observe("переменных у верхнего компонента ПОСЛЕ переоткрытия: " + Api5.Raw(count));

            if (reopened.VariableCollection() is ksVariableCollection coll)
            {
                var n = Api5.SafeInt(coll.GetCount) ?? 0;
                for (var i = 0; i < n; i++)
                {
                    if (Api5.SafeObject(() => coll.GetByIndex(i)) is ksVariable v)
                    {
                        step.Observe("[" + i + "] имя=«" + Api5.SafeObject(() => v.name)
                            + "» значение=" + Api5.Raw(Api5.SafeDouble(() => v.value))
                            + " external=" + Api5.Raw(Api5.SafeObject(() => v.external)));
                    }
                }
            }

            fresh.close();
            step.Data["reopened_count"] = count;
            step.Pass("переоткрытие измерено");
        }
        catch (Exception ex)
        {
            step.Fail("переоткрытие бросило " + ex.GetType().Name + ": " + ex.Message);
            step.Errors.Add(ex.ToString());
        }
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
        var step = _report.Begin("R.Z", "Завершение сеанса", "Свой процесс уходит?");
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

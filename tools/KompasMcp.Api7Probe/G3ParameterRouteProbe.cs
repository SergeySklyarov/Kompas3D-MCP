using System.Diagnostics;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Throwaway measurement: which object actually exposes an EXTRUSION's parameter variables.</summary>
/// <remarks>
/// WHY THIS PROBE EXISTS. Block G3 reads a feature's parameter variables through the route the order §3.2
/// names: <c>ksEntity.GetFeature()</c> → <c>ksFeature.VariableCollection</c>. On the acceptance reference
/// that route answered an EMPTY collection, while the VM reference probe (which built its plate with
/// <c>Api5.BasePlate</c> and read the SAME members) found parameters. "The route is empty" and "the route
/// is wrong" are different facts, so the members are measured here on three objects in ONE document:
/// the handle the builder returned, the same object seen through the API7 <c>IFeature7.Variables</c>, and
/// the entity reached from the tree (<c>EntityCollection(110)</c>). The measurement decides whether the
/// acceptance reference must be built differently — it is NOT a licence to guess a member from the TLB.
/// </remarks>
internal sealed class G3ParameterRouteProbe
{
    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private ProbeStep? _current;

    public G3ParameterRouteProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "g3-parameter-route.json"),
            Path.Combine(options.ReportDir, "g3-parameter-route.md"));

    public void Run()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        try
        {
            Measure();
        }
        catch (Exception ex)
        {
            var step = _current ?? _report.Begin("G3R.0", "сеанс КОМПАС");
            step.Fail("шаг бросил " + ex.GetType().Name + ": " + ex.Message);
            step.Errors.Add(ex.ToString());
        }
        finally
        {
            try
            {
                _app.Quit();
            }
            catch (Exception)
            {
                // The session's teardown is not the subject of this measurement.
            }
        }
    }

    private ProbeStep Begin(string id, string title, string question)
    {
        _current = _report.Begin(id, title, question);
        return _current;
    }

    private void Measure()
    {
        var session = Begin("G3R.0", "Сеанс КОМПАС",
            "Каким объектом отдаются переменные-параметры выдавливания?");
        if (_app.Document3D() is not ksDocument3D doc)
        {
            session.Fail("Document3D() не дал документ");
            return;
        }

        if (!doc.Create(false, false))
        {
            session.Fail("Create(false,false) → false");
            return;
        }

        session.Pass("документ-деталь создан");
        try
        {
            if (doc.GetPart(-1) is not ksPart part)
            {
                session.Fail("GetPart(-1) не дал верхний компонент");
                return;
            }

            var build = Begin("G3R.1", "Плита 100×80×10 через Api5.BasePlate",
                "Строится ли та же геометрия, что у эталонов VM и G3?");
            var extrusion = Api5.BasePlate(part, 100d, 80d, 10d, build, "g3");
            if (extrusion is null)
            {
                build.Fail("плита не построена");
                return;
            }

            build.Observe("объём: " + Api5.Num(Api5.Volume(part)) + " мм³ (ожидание 80000)");
            build.Pass("плита построена");

            Dump("G3R.2", "Маршрут A: ручка Api5.BasePlate → GetFeature → VariableCollection",
                extrusion, "Отдаёт ли переменные-параметры объект, которым признак был построен?");

            var api7 = Begin("G3R.3", "Маршрут B: IFeature7.Variables(false,false)",
                "Отдаёт ли переменные-параметры признак, перенесённый в API7?");
            var transferred = Api5.SafeObject(() => _app.TransferInterface(extrusion, 2 /* ksAPI7Dual */, 0));
            if (transferred is IFeature7 feature7)
            {
                DumpFeature7(feature7, api7);
            }
            else
            {
                api7.Observe("TransferInterface → " + Api5.RuntimeName(transferred));
                api7.Fail("перенесённый объект не отвечает на IFeature7");
            }

            var user = Begin("G3R.4", "Маршрут C: IPart7.AddVariable(depth, 10)",
                "Появляются ли параметры-переменные признака после создания ПОЛЬЗОВАТЕЛЬСКОЙ переменной?");
            var document7 = Api5.SafeObject(() => _app.TransferInterface(doc, 2, 0)) as IKompasDocument3D;
            var top7 = document7?.TopPart as IPart7;
            if (top7 is null)
            {
                user.Fail("TopPart не отвечает на IPart7");
            }
            else
            {
                var made = Api5.SafeObject(() => top7.AddVariable("depth", 10d, "проба G3"));
                user.Observe("AddVariable(«depth», 10) → " + Api5.RuntimeName(made));
                if (made is IVariable7 variable)
                {
                    Api5.SafeBool(() =>
                    {
                        variable.External = true;
                        return true;
                    });
                    user.Observe("External=true, прочитано: "
                        + Api5.Raw(Api5.SafeObject(() => variable.External)));
                }

                user.Pass("пользовательская переменная создана");
            }

            Dump("G3R.5", "Маршрут A (повтор после создания переменной) → GetFeature → VariableCollection",
                extrusion, "Отдаёт ли тот же объект параметры ПОСЛЕ появления пользовательской переменной?");

            var tree = Begin("G3R.6", "Маршрут D: дерево EntityCollection(110) → GetFeature → VariableCollection",
                "Отдаёт ли параметры признак, найденный в дереве, а не возвращённый построителем?");
            if (Api5.SafeObject(() => part.EntityCollection(Api5.OperationElement)) is not ksEntityCollection collection)
            {
                tree.Fail("EntityCollection(110) не дал коллекцию");
                return;
            }

            var count = Api5.SafeInt(collection.GetCount) ?? 0;
            tree.Observe("элементов дерева: " + count);
            for (var i = 0; i < Math.Max(0, count); i++)
            {
                if (Api5.SafeObject(() => collection.GetByIndex(i)) is not ksEntity entity)
                {
                    tree.Observe($"[{i}] GetByIndex не дал ksEntity");
                    continue;
                }

                tree.Observe($"[{i}] type={Api5.Raw(Api5.SafeObject(() => entity.type))} "
                    + $"name=«{Api5.Raw(Api5.SafeObject(() => entity.name))}»");
                var feature = Api5.SafeObject(() => entity.GetFeature()) as ksFeature;
                if (feature is null)
                {
                    tree.Observe("    GetFeature → " + Api5.RuntimeName(feature));
                    continue;
                }

                var parameters = Api5.SafeObject(() => feature.VariableCollection) as ksVariableCollection;
                tree.Observe("    GetFeature → " + Api5.RuntimeName(feature) + ", переменных: "
                    + (parameters is null ? "коллекции нет" : (Api5.SafeInt(parameters.GetCount)?.ToString() ?? "?")));
            }

            tree.Pass("дерево прочитано");
        }
        finally
        {
            try
            {
                doc.close();
            }
            catch (Exception)
            {
                // Closing the throwaway document is not part of the measurement.
            }
        }
    }

    private void Dump(string id, string title, ksEntity entity, string question)
    {
        var step = Begin(id, title, question);
        var feature = Api5.SafeObject(() => entity.GetFeature()) as ksFeature;
        step.Observe("GetFeature() → " + Api5.RuntimeName(feature));
        if (feature is null)
        {
            step.Fail("GetFeature не дал ksFeature");
            return;
        }

        var collection = Api5.SafeObject(() => feature.VariableCollection) as ksVariableCollection;
        step.Observe("VariableCollection → " + Api5.RuntimeName(collection));
        if (collection is null)
        {
            step.Fail("VariableCollection не дал коллекцию");
            return;
        }

        var count = Api5.SafeInt(collection.GetCount) ?? -1;
        step.Observe("переменных: " + count);
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            if (Api5.SafeObject(() => collection.GetByIndex(i)) is not ksVariable variable)
            {
                step.Observe($"[{i}] GetByIndex не дал ksVariable");
                continue;
            }

            step.Observe($"[{i}] name=«{Api5.Raw(Api5.SafeObject(() => variable.name))}» "
                + $"parameterNote=«{Api5.Raw(Api5.SafeObject(() => variable.parameterNote))}» "
                + $"value={Api5.Raw(Api5.SafeDouble(() => variable.value))} "
                + $"external={Api5.Raw(Api5.SafeObject(() => variable.external))} "
                + $"expression=«{Api5.Raw(Api5.SafeObject(() => variable.Expression))}»");
        }

        step.Data["count"] = count;
        step.Pass("маршрут измерен: переменных " + count);
    }

    private static void DumpFeature7(IFeature7 feature, ProbeStep step)
    {
        var raw = Api5.SafeObject(() =>
            typeof(IFeature7).GetProperty("Variables")?.GetValue(feature, new object[] { false, false }));
        if (raw is not Array array)
        {
            step.Observe("Variables(false,false) → " + Api5.Raw(raw));
            step.Fail("Variables(false,false) не дал массив");
            return;
        }

        step.Observe("IFeature7.Variables(false,false) — элементов " + array.Length);
        foreach (var element in array)
        {
            if (element is not IVariable7 variable)
            {
                continue;
            }

            step.Observe($"  «{Api5.Raw(Api5.SafeObject(() => variable.Name))}» / параметр "
                + $"«{Api5.Raw(Api5.SafeObject(() => variable.ParameterNote))}» значение="
                + $"{Api5.Raw(Api5.SafeDouble(() => variable.Value))} внешняя="
                + $"{Api5.Raw(Api5.SafeObject(() => variable.External))} выражение=«"
                + $"{Api5.Raw(Api5.SafeObject(() => variable.Expression))}»");
        }

        step.Data["count"] = array.Length;
        step.Pass("маршрут B измерен: элементов " + array.Length);
    }
}

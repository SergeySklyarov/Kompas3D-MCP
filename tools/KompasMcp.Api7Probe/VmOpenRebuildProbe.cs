using System.Diagnostics;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Measures which write+rebuild pair moves the geometry of the SAVED reference file.</summary>
/// <remarks>
/// The reference is built in a fresh document, where a value write plus <c>ksDocument3D.RebuildDocument</c>
/// moves the body. The shipping adapter does the same two things on an OPENED file — and the acceptance run
/// measured the volume NOT moving, with the expression <c>depth*2</c> left unevaluated. So the file and the
/// write handle are both crossed here, on the saved file. Nothing ships: this instrument decides what the
/// adapter must call. History: docs/decisions/variables-material.md#rebuild
/// </remarks>
internal sealed class VmOpenRebuildProbe
{
    private const double BaseDepthMm = 10d;
    private const double ChangedDepthMm = 20d;
    private const string DepthVariable = "depth";
    private const string TotalVariable = "full_depth";

    private readonly ProbeReport _report;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;
    private ProbeStep? _current;

    public VmOpenRebuildProbe(ProbeReport report, Options options)
    {
        _report = report;
        _ = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "vm-open-rebuild.json"),
            Path.Combine(options.ReportDir, "vm-open-rebuild.md"));
    }

    public void Run(string path)
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        Launch();
        try
        {
            Measure(path);
        }
        finally
        {
            Shutdown();
        }
    }

    private ProbeStep Begin(string id, string title, string question)
    {
        _current = _report.Begin(id, title, question);
        return _current;
    }

    private void Launch()
    {
        var step = Begin("OR.0", "Сеанс КОМПАС", "Что двигает геометрию СОХРАНЁННОГО эталона?");
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
        step.Observe("свой процесс " + _ownPid);
        step.Pass("сеанс поднят");
    }

    private void Measure(string path)
    {
        var step = Begin("OR.1", "Открытие сохранённого эталона",
            "Читаются ли из файла переменные и их выражения?");
        ksDocument3D? doc = null;
        try
        {
            doc = _app.Document3D() as ksDocument3D;
            if (doc is null || !doc.Open(path, true))
            {
                step.Fail("файл не открыт: " + path);
                return;
            }

            if (doc.GetPart(-1) is not ksPart part)
            {
                step.Fail("GetPart(-1) не дал деталь");
                return;
            }

            var document7 = Api5.SafeObject(() => _app.TransferInterface(doc, 2, 0)) as IKompasDocument3D;
            var part7 = document7?.TopPart as IPart7;
            var massInertia = part7 as IMassInertiaParam7;
            step.Observe("TopPart → " + Api5.RuntimeName(part7) + ", QI(IMassInertiaParam7) → "
                + Api5.RuntimeName(massInertia));
            DumpCollection(part, step);
            step.Observe("объём при открытии: " + Api5.Num(Api5.Volume(part)) + " мм³ (ожидание 80000)");
            step.Pass("эталон открыт");

            var api5Depth = Api5.SafeObject(() => part.VariableCollection() is ksVariableCollection c
                ? c.GetByName(DepthVariable, true, false)
                : null) as ksVariable;
            var api7Depth = Api5.SafeObject(() => part.VariableCollection() is ksVariableCollection c
                ? c.GetByName(DepthVariable, true, false)
                : null);

            // ── Pair 1: the API5 handle, value only — what the adapter did before ──────────────────
            var p1 = Begin("OR.2", "API5-ручка: только value + RebuildDocument",
                "Двигает ли геометрию запись одного value через API5-ручку?");
            var w1 = Api5.SafeBool(() =>
            {
                api5Depth!.value = ChangedDepthMm;
                return true;
            });
            var r1 = Api5.SafeBool(() => doc.RebuildDocument());
            Report(p1, part, api5Depth, "API5.value=20", w1, r1);

            // ── Pair 2: the API5 handle, value AND expression — the documented "число" assignment ──
            var p2 = Begin("OR.3", "API5-ручка: value и Expression + RebuildDocument",
                "Двигает ли геометрию запись документированного выражения-числа через ту же ручку?");
            var w2 = Api5.SafeBool(() =>
            {
                api5Depth!.Expression = ChangedDepthMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            });
            var r2 = Api5.SafeBool(() => doc.RebuildDocument());
            Report(p2, part, api5Depth, "API5.Expression=\"20\"", w2, r2);

            // ── Pair 3: the API7 handle — the pair the reference was built with ────────────────────
            var p3 = Begin("OR.4", "API7-ручка: Value + RebuildDocument",
                "Двигает ли геометрию запись через API7-ручку?");
            Api5.SafeBool(() =>
            {
                api5Depth!.Expression = BaseDepthMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            });
            Api5.SafeBool(() => doc.RebuildDocument());
            p3.Observe("сброс к depth=10: объём " + Api5.Num(Api5.Volume(part)) + " мм³");

            var v7 = FindApi7Variable(part7, DepthVariable, step);
            p3.Observe("API7-ручка «" + DepthVariable + "»: " + Api5.RuntimeName(v7));
            var w3 = Api5.SafeBool(() =>
            {
                v7!.Value = ChangedDepthMm;
                return true;
            });
            var r3 = Api5.SafeBool(() => doc.RebuildDocument());
            Report(p3, part, api5Depth, "API7.Value=20", w3, r3);

            doc.close();
        }
        catch (Exception ex)
        {
            var failed = _current ?? step;
            failed.Fail("шаг бросил " + ex.GetType().Name + ": " + ex.Message);
            failed.Errors.Add(ex.ToString());
        }
    }

    private static IVariable7? FindApi7Variable(IPart7? part7, string name, ProbeStep step)
    {
        if (part7 is not IFeature7 feature)
        {
            step.Observe("QI(IFeature7) не получен");
            return null;
        }

        var raw = Api5.SafeObject(() =>
            typeof(IFeature7).GetProperty("Variables")?.GetValue(feature, new object[] { false, false }));
        if (raw is not Array array)
        {
            return null;
        }

        foreach (var element in array)
        {
            if (element is IVariable7 variable
                && string.Equals(Api5.SafeObject(() => variable.Name)?.ToString(), name, StringComparison.Ordinal))
            {
                return variable;
            }
        }

        return null;
    }

    private void Report(
        ProbeStep step, ksPart part, ksVariable? api5Depth, string label, bool? write, bool? rebuild)
    {
        var volume = Api5.Volume(part);
        var value = api5Depth is null ? null : Api5.SafeDouble(() => api5Depth.value);
        step.Observe(label + " → " + Api5.Raw(write) + "; перестройка → " + Api5.Raw(rebuild)
            + "; прочитано value=" + Api5.Num(value) + "; объём " + Api5.Num(volume) + " мм³");
        DumpCollection(part, step);
        step.Data["write"] = write;
        step.Data["rebuild"] = rebuild;
        step.Data["value_read"] = value;
        step.Data["volume"] = volume;
        if (volume is { } v && Math.Abs(v - 100d * 80d * ChangedDepthMm) < 0.01)
        {
            step.Pass("геометрия пошла за переменной");
        }
        else
        {
            step.Fail("геометрия НЕ пошла за переменной при этой паре");
        }
    }

    private static void DumpCollection(ksPart part, ProbeStep step)
    {
        if (part.VariableCollection() is not ksVariableCollection collection)
        {
            step.Observe("коллекция не получена");
            return;
        }

        var count = Api5.SafeInt(collection.GetCount) ?? -1;
        for (var i = 0; i < Math.Max(0, count); i++)
        {
            if (Api5.SafeObject(() => collection.GetByIndex(i)) is ksVariable v)
            {
                step.Observe("  [" + i + "] «" + Api5.SafeObject(() => v.name) + "» значение="
                    + Api5.Raw(Api5.SafeDouble(() => v.value)) + " выражение=«"
                    + Api5.SafeObject(() => v.Expression) + "» external="
                    + Api5.Raw(Api5.SafeObject(() => v.external)));
            }
        }
    }

    private void Shutdown()
    {
        var step = Begin("OR.Z", "Завершение сеанса", "Свой процесс уходит?");
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

using System.Diagnostics;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Measures which unit the two DOCUMENTED density getters of a part actually return.</summary>
/// <remarks>
/// <b>The question.</b> The shipping adapter reads <c>ksPart.GetDensity()</c>, whose SDK v24 page
/// (<c>kspart_getdensity.html</c>) names «плотность (г/куб.мм)», but a live run reads steel as <c>7.85</c>
/// — the value in g/cm3 — and the adapter multiplies by 1000 to publish kg/m3. The review
/// (<c>VARIABLES_MATERIAL_REWORK_REVIEW_20261006.md</c> §1) requires the divergence to be settled against
/// DOCUMENTATION, not against the experiment: either an official v24 source that really establishes g/cm3
/// for that getter, or the one alternative documented getter
/// <c>IPart7</c> → QI(<c>IMassInertiaParam7</c>) → <c>Density</c> (<c>imassinertiaparam7_density.html</c>,
/// also «плотность тела (г/куб.мм)»). If neither getter returns the documented unit, the raw reading is
/// kept and the non-confirmation is NAMED instead of publishing a normalised density of our own.
/// <b>Two densities.</b> Both getters are read on two pre-set densities, and the body volume is measured
/// independently, so the mass that follows from the published density is checked arithmetically.
/// History: docs/decisions/variables-material.md#units
/// </remarks>
internal sealed class VmDensityUnitsProbe
{
    /// <summary>Two densities fixed BEFORE the run, in the unit <c>SetMaterial</c> documents (g/cm3).</summary>
    private static readonly (string Name, double KgPerM3)[] Materials =
    {
        ("Сталь 45 ГОСТ 1050-2013", 7850.0),
        ("Латунь ЛС59-1 ГОСТ 15527-2004", 8500.0),
    };

    private const double WidthMm = 100d;
    private const double HeightMm = 80d;
    private const double DepthMm = 10d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;
    private ProbeStep? _current;

    public VmDensityUnitsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "vm-density-units.json"),
            Path.Combine(options.ReportDir, "vm-density-units.md"));
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
            Measure();
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
        var step = Begin("DU.0", "Сеанс КОМПАС", "Какую единицу отдают оба документированных getter плотности?");
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

    private void Measure()
    {
        var build = Begin("DU.1", "Деталь 100×80×10 для измерения плотности",
            "На каком объекте сравниваются оба getter?");
        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(build);
            if (doc?.GetPart(-1) is not ksPart part)
            {
                build.Fail("документ-деталь не создан");
                return;
            }

            if (Api5.BasePlate(part, WidthMm, HeightMm, DepthMm, build, "du") is null)
            {
                build.Fail("базовая геометрия не построена");
                return;
            }

            var volume = Api5.Volume(part);
            build.Observe("объём детали: " + Api5.Num(volume) + " мм³ (ожидание 80000)");
            build.Pass("деталь построена");

            var massInertia = MassInertia(doc, build);

            var table = new List<Dictionary<string, object?>>();
            foreach (var (name, kgPerM3) in Materials)
            {
                var step = Begin("DU.2." + table.Count, "Плотность " + Api5.Num(kgPerM3) + " кг/м³",
                    "Какую единицу отдают ksPart.GetDensity и IMassInertiaParam7.Density?");
                var row = MeasureOne(part, massInertia, name, kgPerM3, volume, step);
                table.Add(row);
            }

            var summary = Begin("DU.3", "Вывод по единице",
                "Согласуется ли хотя бы один документированный getter со своей страницей?");
            Summarize(table, summary);

            // ── DU.4: the API7 setter, whose page names г/куб.мм — a DIFFERENT unit from the API5 setter's
            // г/куб.см for the same quantity. If the API7 setter/getter pair is self-consistent in
            // г/куб.мм, a documented route to the documented unit exists after all. The kernel's own Mass
            // is the arbiter: 80000 mm3 at 0.00785 г/мм3 is 628 g, at 0.00785 г/см3 it is 0.628 g.
            var cross = Begin("DU.4", "Согласованность пары API7 SetMaterial/Density",
                "Трактует ли IMassInertiaParam7.SetMaterial свой аргумент как документированные г/куб.мм?");
            ProbeApi7Setter(part, massInertia, cross);
        }
        catch (Exception ex)
        {
            var failed = _current ?? build;
            failed.Fail("шаг бросил " + ex.GetType().Name + ": " + ex.Message);
            failed.Errors.Add(ex.ToString());
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch (Exception)
            {
                // Session teardown is not this probe's subject.
            }
        }
    }

    private Dictionary<string, object?> MeasureOne(
        ksPart part, IMassInertiaParam7? massInertia, string name, double kgPerM3, double? volume,
        ProbeStep step)
    {
        var row = new Dictionary<string, object?>
        {
            ["material_name"] = name,
            ["requested_kg_per_m3"] = kgPerM3,
            ["documented_g_per_mm3"] = kgPerM3 / 1e6,
            ["documented_g_per_cm3"] = kgPerM3 / 1e3,
        };

        // DOC <c>kspart_setmaterial.html</c>: the density argument of SetMaterial is in g/cm3.
        var written = Api5.SafeBool(() => part.SetMaterial(name, kgPerM3 / 1e3));
        Api5.SafeBool(() => part.Update());
        step.Observe("ksPart.SetMaterial(«" + name + "», " + Api5.Num(kgPerM3 / 1e3)
            + " г/см³) → " + Api5.Raw(written));

        var api5Raw = Api5.SafeDouble(() => part.density);
        row["kspart_getdensity_raw"] = api5Raw;
        row["kspart_getdensity_unit_matched"] = UnitOf(api5Raw, kgPerM3);
        step.Observe("ksPart.GetDensity() = " + Api5.Num(api5Raw)
            + " (страница называет г/куб.мм → ожидание " + Api5.Num(kgPerM3 / 1e6) + "; "
            + "г/куб.см → " + Api5.Num(kgPerM3 / 1e3) + ")");

        var api7Raw = massInertia is null ? null : Api5.SafeDouble(() => massInertia.Density);
        row["imassinertiaparam7_density_raw"] = api7Raw;
        row["imassinertiaparam7_density_unit_matched"] = UnitOf(api7Raw, kgPerM3);
        step.Observe("IMassInertiaParam7.Density = " + Api5.Num(api7Raw)
            + " (страница называет г/куб.мм → ожидание " + Api5.Num(kgPerM3 / 1e6) + "; "
            + "г/куб.см → " + Api5.Num(kgPerM3 / 1e3) + ")");

        var material7 = massInertia is null ? null : Api5.SafeObject(() => massInertia.Material);
        var mass7 = massInertia is null ? null : Api5.SafeDouble(() => massInertia.Mass);
        var volume7 = massInertia is null ? null : Api5.SafeDouble(() => massInertia.Volume);
        row["imassinertiaparam7_material"] = material7;
        row["imassinertiaparam7_mass"] = mass7;
        row["imassinertiaparam7_volume"] = volume7;
        step.Observe("IMassInertiaParam7: Material=«" + Api5.Raw(material7) + "», Mass=" + Api5.Num(mass7)
            + ", Volume=" + Api5.Num(volume7));

        // The mass that follows from the PUBLISHED density, checked against the kernel's own mass. Only
        // arithmetic on two independently read quantities — no reference table of the server's.
        if (api5Raw is { } raw && volume is { } v)
        {
            var publishedKgPerM3 = raw * 1e3;
            var computedMassKg = v * 1e-9 * publishedKgPerM3;
            row["mass_from_published_density_kg"] = computedMassKg;
            step.Observe("масса из опубликованной плотности: " + v + " мм³ × " + publishedKgPerM3
                + " кг/м³ = " + computedMassKg.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture) + " кг"
                + (mass7 is { } km ? " (ядро: " + Api5.Num(km) + ")" : string.Empty));
        }

        step.Data.Clear();
        foreach (var pair in row)
        {
            step.Data[pair.Key] = pair.Value;
        }

        var api5Documented = UnitOf(api5Raw, kgPerM3) == "g/mm3";
        var api7Documented = UnitOf(api7Raw, kgPerM3) == "g/mm3";
        if (api5Documented || api7Documented)
        {
            step.Pass("хотя бы один getter вернул документированную г/куб.мм");
        }
        else
        {
            step.Fail("ни один getter не вернул документированную г/куб.мм: "
                + "ksPart.GetDensity=" + Api5.Num(api5Raw) + ", IMassInertiaParam7.Density="
                + Api5.Num(api7Raw) + " — расхождение со справкой НАЗВАНО, а не сглажено");
        }

        return row;
    }

    private static void Summarize(List<Dictionary<string, object?>> table, ProbeStep step)
    {
        var api5Units = table.Select(r => r["kspart_getdensity_unit_matched"] as string).Distinct().ToList();
        var api7Units = table.Select(r => r["imassinertiaparam7_density_unit_matched"] as string).Distinct().ToList();
        step.Data["kspart_getdensity_units"] = api5Units;
        step.Data["imassinertiaparam7_density_units"] = api7Units;

        var api5 = api5Units.Count == 1 ? api5Units[0] : "mixed";
        var api7 = api7Units.Count == 1 ? api7Units[0] : "mixed";
        step.Observe("ksPart.GetDensity(): " + api5 + " на обеих плотностях");
        step.Observe("IMassInertiaParam7.Density: " + api7 + " на обеих плотностях");

        if (api7 == "g/mm3")
        {
            step.Pass("документированный маршрут IPart7→QI(IMassInertiaParam7)→Density отдаёт "
                + "документированную г/куб.мм — он и должен быть источником публикуемой плотности");
        }
        else if (api5 == "g/cm3" && api7 == "g/cm3")
        {
            step.Fail("ОБА документированных getter отдают г/куб.см при странице «г/куб.мм»: "
                + "расхождение со справкой в 1000 раз, и документированного источника г/куб.см нет. "
                + "Собственная переинтерпретация единицы не публикуется: критерий остаётся открытым, "
                + "сырое измерение сохраняется");
        }
        else
        {
            step.Fail("единицы расходятся между плотностями или между getter'ами: api5=" + api5
                + ", api7=" + api7);
        }
    }

    /// <summary>Write the steel density through the API7 setter in ITS documented unit and let the kernel's
    /// own Mass say which unit the pair really uses.</summary>
    /// <remarks>DOC <c>imassinertiaparam7_setmaterial.html</c>: «плотность материала (г/куб.мм)» — a
    /// different unit from <c>kspart_setmaterial.html</c> «(г/куб.см)» for the same quantity. The two pages
    /// cannot both describe the installed build, so the pair is measured: 80000 mm3 at 0.00785 г/мм3 is
    /// 628 g, at 0.00785 г/см3 it is 0.628 g.</remarks>
    private void ProbeApi7Setter(ksPart part, IMassInertiaParam7? massInertia, ProbeStep step)
    {
        const string Name = "Сталь 45 ГОСТ 1050-2013";
        const double DocumentedGPerMm3 = 7850.0 / 1e6;

        if (massInertia is null)
        {
            step.Fail("IMassInertiaParam7 недостижим — пару проверить нечем");
            return;
        }

        var set = Api5.SafeBool(() => massInertia.SetMaterial(Name, DocumentedGPerMm3));
        Api5.SafeBool(() => part.Update());
        var api7Raw = Api5.SafeDouble(() => massInertia.Density);
        var api5Raw = Api5.SafeDouble(() => part.density);
        var mass = Api5.SafeDouble(() => massInertia.Mass);
        var volume = Api5.SafeDouble(() => massInertia.Volume);

        step.Observe("IMassInertiaParam7.SetMaterial(«" + Name + "», " + Api5.Num(DocumentedGPerMm3)
            + " г/мм³) → " + Api5.Raw(set));
        step.Observe("IMassInertiaParam7.Density = " + Api5.Num(api7Raw)
            + ", ksPart.GetDensity() = " + Api5.Num(api5Raw)
            + ", Mass = " + Api5.Num(mass) + ", Volume = " + Api5.Num(volume));
        step.Observe("арбитр: масса 80000 мм³ при 0.00785 г/мм³ = 628, при 0.00785 г/см³ = 0.628");

        step.Data["set_returned"] = set;
        step.Data["imassinertiaparam7_density_raw"] = api7Raw;
        step.Data["kspart_getdensity_raw"] = api5Raw;
        step.Data["mass"] = mass;
        step.Data["volume"] = volume;

        // The kernel's Mass decides: ~628 means the argument was taken as г/мм³ (the documented unit of
        // the API7 page); ~0.628 means it was taken as г/см³ (the API5 page's unit).
        if (mass is { } m && Math.Abs(m - 628.0) < 1.0)
        {
            step.Pass("API7-пара согласована в документированных г/куб.мм: масса 628 г, Density="
                + Api5.Num(api7Raw));
        }
        else if (mass is { } m2 && Math.Abs(m2 - 0.628) < 1e-3)
        {
            step.Fail("API7-пара трактует аргумент как г/куб.см, а не как документированные г/куб.мм: "
                + "масса " + Api5.Num(m2) + " г — страница SetMaterial противоречит ядру так же, как "
                + "страница Density");
        }
        else
        {
            step.Fail("ядро отдало массу " + Api5.Num(mass) + " г — ни одной из двух трактовок не "
                + "соответствует; состояние НАЗВАНО, а не подогнано");
        }
    }

    /// <summary>Which of the two candidate units the raw reading matches, for the requested density.</summary>
    private static string UnitOf(double? raw, double kgPerM3)
    {
        if (raw is null)
        {
            return "not_read";
        }

        if (Math.Abs(raw.Value - kgPerM3 / 1e6) <= Math.Max(kgPerM3 / 1e6 * 1e-3, 1e-12))
        {
            return "g/mm3";
        }

        if (Math.Abs(raw.Value - kgPerM3 / 1e3) <= Math.Max(kgPerM3 / 1e3 * 1e-3, 1e-12))
        {
            return "g/cm3";
        }

        return "other";
    }

    /// <summary>QI(<c>IMassInertiaParam7</c>) on the top component, through the documented
    /// <c>IPart7</c> route.</summary>
    private IMassInertiaParam7? MassInertia(ksDocument3D doc, ProbeStep step)
    {
        var doc7 = Api5.SafeObject(() => _app.TransferInterface(doc, 2 /* ksAPI7Dual */, 0)) as IKompasDocument3D;
        if (doc7?.TopPart is not IPart7 part7)
        {
            step.Observe("TopPart не отвечает на QI(IPart7) — IMassInertiaParam7 недостижим");
            return null;
        }

        // DOC <c>ipart7.html</c>: IMassInertiaParam7 is one of the ADDITIONAL interfaces of the component,
        // obtained by IUnknown::QueryInterface — the C# cast IS that QueryInterface.
        var massInertia = part7 as IMassInertiaParam7;
        step.Observe("QI(IMassInertiaParam7) → " + Api5.RuntimeName(massInertia));
        return massInertia;
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
        var step = Begin("DU.Z", "Завершение сеанса", "Свой процесс уходит?");
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

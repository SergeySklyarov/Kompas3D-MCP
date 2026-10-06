using System.Diagnostics;
using System.Globalization;
using Kompas6API5;

namespace KompasMcp.Api7Probe;

/// <summary>Measures whether the API5 MCI route returns the density in its <c>bitVector</c> units.</summary>
/// <remarks>
/// <b>The question.</b> Both density getters the adapter tried — <c>ksPart.GetDensity()</c> and
/// <c>IPart7</c>→QI(<c>IMassInertiaParam7</c>)→<c>Density</c> — read a value consistent with g/cm3 while
/// their pages name g/mm3, and no v24 source establishes g/cm3. A third, DOCUMENTED route was never
/// applied to density: <c>ksPart.CalcMassInertiaProperties(bitVector)</c>, whose <c>r</c> member is
/// «Плотность материала» and whose units are set by the ARGUMENT, exactly as the project already does
/// for volume (<c>KompasUnits.MassMmKg</c>).
/// <b>DOC.</b> <c>kspart_calcmassinertiaproperties.html</c> — <c>bitVector</c> «определяет размерность
/// длины, размерность массы»; <c>ksmassinertiaparam.html</c> note 3 — «Размерность длины и размерность
/// массы, возвращаемых интерфейсом, данных … устанавливается при получении интерфейса»;
/// <c>ksmassinertiaparam_props.html</c> — <c>r</c> is «Плотность материала»; <c>mtypes.html</c> — the
/// constants <c>ST_MIX_*</c>. The reading is therefore a reading of DOCUMENTED text, not an experimental
/// factor: the server never rescales, the unit comes from the argument.
/// <b>Expectations are written BEFORE the run.</b> For a 100×80×10 part (80000 mm3 = 8e-5 m3):
/// at <c>ST_MIX_M|ST_MIX_KG</c> <c>r</c> must be the density in kg/m3, <c>v</c> in m3, <c>m</c> in kg;
/// at <c>ST_MIX_MM|ST_MIX_KG</c> <c>r</c> in kg/mm3, <c>v</c> in mm3, <c>m</c> in kg; and the ratio of
/// the two <c>r</c> readings must be the factor between kg/m3 and kg/mm3 (1e9), not 1000.
/// History: docs/decisions/variables-material.md#units
/// </remarks>
internal sealed class VmDensityMciProbe
{
    // DOC mtypes.html «Размерности и типы тел для расчета МЦХ»: length ST_MIX_SM=0, ST_MIX_MM=0x1,
    // ST_MIX_DM=0x2, ST_MIX_M=0x3; mass ST_MIX_GR=0, ST_MIX_KG=0x10. The constants are written as the
    // page gives them; no value is guessed.
    private const int MixMetresKilograms = 0x3 | 0x10;

    private const int MixMillimetresKilograms = 0x1 | 0x10;

    /// <summary>Factor between kg/m3 and kg/mm3 — the ratio the two readings must show.</summary>
    private const double CubicMetreToCubicMillimetre = 1e9;

    /// <summary>Two densities fixed BEFORE the run, in the unit <c>SetMaterial</c> documents (g/cm3).</summary>
    private static readonly (string Name, double KgPerM3)[] Materials =
    {
        ("Сталь 45 ГОСТ 1050-2013", 7850.0),
        ("Латунь ЛС59-1 ГОСТ 15527-2004", 8500.0),
    };

    private const double WidthMm = 100d;
    private const double HeightMm = 80d;
    private const double DepthMm = 10d;
    private const double VolumeMm3 = WidthMm * HeightMm * DepthMm;
    private const double VolumeM3 = VolumeMm3 * 1e-9;

    private const double RelativeTolerance = 1e-6;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;
    private ProbeStep? _current;

    public VmDensityMciProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "vm-density-mci.json"),
            Path.Combine(options.ReportDir, "vm-density-mci.md"));
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
        var step = Begin("DM.0", "Сеанс КОМПАС",
            "Отдаёт ли API5-маршрут МЦХ плотность в единицах, заданных аргументом?");
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
        var build = Begin("DM.1", "Деталь 100×80×10 для измерения плотности",
            "На каком объекте читаются МЦХ по аргументу?");
        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(build);
            if (doc?.GetPart(-1) is not ksPart part)
            {
                build.Fail("документ-деталь не создан");
                return;
            }

            if (Api5.BasePlate(part, WidthMm, HeightMm, DepthMm, build, "dm") is null)
            {
                build.Fail("базовая геометрия не построена");
                return;
            }

            var volume = Api5.Volume(part);
            build.Observe("объём детали: " + Api5.Num(volume) + " мм³ (ожидание 80000)");
            build.Pass("деталь построена");

            var table = new List<Dictionary<string, object?>>();
            foreach (var (name, kgPerM3) in Materials)
            {
                var step = Begin("DM.2." + table.Count, "Плотность " + Api5.Num(kgPerM3) + " кг/м³",
                    "Совпадает ли r с ожиданием в единицах, заданных bitVector, в обеих комбинациях?");
                var row = MeasureOne(part, name, kgPerM3, step);
                table.Add(row);
            }

            var roundTrip = Begin("DM.3", "Сохранение, закрытие и переоткрытие",
                "Даёт ли r при M|KG то же значение после переоткрытия без повторной записи?");
            var roundTripRow = SaveReopenMeasure(part, doc, roundTrip);
            doc = null;

            var summary = Begin("DM.4", "Вывод по единице",
                "Подтверждено ли прочтение справки на предзаданных числах во всех комбинациях?");
            Summarize(table, roundTripRow, summary);
        }
        catch (Exception ex)
        {
            var failed = _current ?? build;
            failed.Fail("шаг бросил " + ex.GetType().Name + ": " + ex.Message);
            failed.Errors.Add(ex.ToString());
        }
        finally
        {
            if (doc is not null)
            {
                try
                {
                    doc.close();
                }
                catch (Exception)
                {
                    // Session teardown is not this probe's subject.
                }
            }
        }
    }

    /// <summary>One density, read through the MCI route at both documented unit combinations.</summary>
    private Dictionary<string, object?> MeasureOne(ksPart part, string name, double kgPerM3, ProbeStep step)
    {
        var row = new Dictionary<string, object?>
        {
            ["material_name"] = name,
            ["requested_kg_per_m3"] = kgPerM3,
            ["expected_r_m_kg_kg_per_m3"] = kgPerM3,
            ["expected_r_mm_kg_kg_per_mm3"] = kgPerM3 / CubicMetreToCubicMillimetre,
            ["expected_mass_kg"] = kgPerM3 * VolumeM3,
        };

        // DOC kspart_setmaterial.html: the density argument of SetMaterial is in g/cm3.
        var written = Api5.SafeBool(() => part.SetMaterial(name, kgPerM3 / 1e3));
        Api5.SafeBool(() => part.Update());
        step.Observe("ksPart.SetMaterial(«" + name + "», " + Api5.Num(kgPerM3 / 1e3)
            + " г/см³) → " + Api5.Raw(written));

        var metres = ReadPartMci(part, MixMetresKilograms, step, "M|KG");
        var millimetres = ReadPartMci(part, MixMillimetresKilograms, step, "MM|KG");

        row["r_m_kg"] = metres.R;
        row["m_m_kg"] = metres.M;
        row["v_m_kg"] = metres.V;
        row["r_mm_kg"] = millimetres.R;
        row["m_mm_kg"] = millimetres.M;
        row["v_mm_kg"] = millimetres.V;

        // Criteria, written before the run and evaluated on the measured numbers.
        var rMetresOk = Close(metres.R, kgPerM3);
        var rMillimetresOk = Close(millimetres.R, kgPerM3 / CubicMetreToCubicMillimetre);
        var vMetresOk = Close(metres.V, VolumeM3);
        var vMillimetresOk = Close(millimetres.V, VolumeMm3);
        var massOk = Close(metres.M, kgPerM3 * VolumeM3);
        var ratioOk = metres.R is { } ra && millimetres.R is { } rb && rb != 0
            && Close(ra / rb, CubicMetreToCubicMillimetre);
        var selfConsistentM = SelfConsistent(metres.R, metres.M, metres.V);
        var selfConsistentMm = SelfConsistent(millimetres.R, millimetres.M, millimetres.V);

        row["criterion_r_m_kg"] = rMetresOk;
        row["criterion_r_mm_kg"] = rMillimetresOk;
        row["criterion_v_m_kg"] = vMetresOk;
        row["criterion_v_mm_kg"] = vMillimetresOk;
        row["criterion_mass_kg"] = massOk;
        row["criterion_ratio_1e9"] = ratioOk;
        row["criterion_self_consistent_m_kg"] = selfConsistentM;
        row["criterion_self_consistent_mm_kg"] = selfConsistentMm;

        var all = rMetresOk && rMillimetresOk && vMetresOk && vMillimetresOk && massOk && ratioOk
            && selfConsistentM && selfConsistentMm;

        step.Data.Clear();
        foreach (var pair in row)
        {
            step.Data[pair.Key] = pair.Value;
        }

        if (all)
        {
            step.Pass("r совпала с ожиданием в единицах bitVector в обеих комбинациях, отношение = 1e9, "
                + "m = r·v и m совпала с аналитической массой");
        }
        else
        {
            step.Fail("прочтение не подтверждено: " + Describe(row));
        }

        return row;
    }

    /// <summary>Save the part, close it, reopen it and read <c>r</c> at M|KG without rewriting anything.</summary>
    private Dictionary<string, object?> SaveReopenMeasure(ksPart part, ksDocument3D doc, ProbeStep step)
    {
        var row = new Dictionary<string, object?>();
        const double ExpectedKgPerM3 = 8500.0; // the last density written in DM.2

        var path = Path.Combine(_options.WorkDir, "vm-density-mci.m3d");
        var saved = Api5.SafeBool(() => doc.SaveAs(path));
        step.Observe("SaveAs(«" + path + "») → " + Api5.Raw(saved));
        doc.close();

        var reopened = NewOpen(path, step);
        if (reopened?.GetPart(-1) is not ksPart reopenedPart)
        {
            step.Fail("файл не переоткрыт — значение r после reopen не проверено");
            row["reopened"] = false;
            return row;
        }

        row["reopened"] = true;
        var metres = ReadPartMci(reopenedPart, MixMetresKilograms, step, "M|KG после reopen");
        row["r_m_kg_after_reopen"] = metres.R;
        row["expected_kg_per_m3"] = ExpectedKgPerM3;

        var nameAfter = Api5.SafeObject(() => reopenedPart.material);
        row["material_after_reopen"] = nameAfter;
        step.Observe("материал после reopen: «" + Api5.Raw(nameAfter) + "»");

        var ok = Close(metres.R, ExpectedKgPerM3);
        row["criterion_r_after_reopen"] = ok;
        if (ok)
        {
            step.Pass("r при M|KG после переоткрытия = " + Api5.Num(metres.R)
                + " кг/м³ — то же значение без повторной записи");
        }
        else
        {
            step.Fail("r при M|KG после переоткрытия = " + Api5.Num(metres.R)
                + ", ожидание " + Api5.Num(ExpectedKgPerM3));
        }

        try
        {
            reopened.close();
        }
        catch (Exception)
        {
            // Teardown is not the subject.
        }

        return row;
    }

    private static void Summarize(
        List<Dictionary<string, object?>> table, Dictionary<string, object?> roundTrip, ProbeStep step)
    {
        var ratios = new List<double>();
        foreach (var row in table)
        {
            if (row["r_m_kg"] is double ra && row["r_mm_kg"] is double rb && rb != 0)
            {
                ratios.Add(ra / rb);
            }
        }

        step.Data["ratios_r_m_kg_over_mm_kg"] = ratios;
        step.Observe("отношения r(M|KG)/r(MM|KG): "
            + string.Join(", ", ratios.Select(r => r.ToString("0.############", CultureInfo.InvariantCulture))));
        step.Observe("ожидаемое отношение между кг/м³ и кг/мм³: " + Api5.Num(CubicMetreToCubicMillimetre)
            + " (множитель 1000 «по опыту» дал бы 1000)");

        var everyCriterion = true;
        foreach (var row in table)
        {
            foreach (var key in row.Keys.Where(k => k.StartsWith("criterion_", StringComparison.Ordinal)))
            {
                if (row[key] is bool value && !value)
                {
                    everyCriterion = false;
                }
            }
        }

        if (roundTrip["criterion_r_after_reopen"] is bool reopened && !reopened)
        {
            everyCriterion = false;
        }

        step.Data["all_criteria_met"] = everyCriterion;
        if (everyCriterion)
        {
            step.Pass("прочтение §3 подтверждено на предзаданных числах во всех комбинациях: единицу r "
                + "задаёт аргумент bitVector, пересчёт сервером не нужен");
        }
        else
        {
            step.Fail("прочтение §3 опровергнуто хотя бы одним критерием — исход отрицательный");
        }
    }

    /// <summary>Read <c>r</c>, <c>m</c> and <c>v</c> of the top component at the given documented selector.</summary>
    private static (double? R, double? M, double? V) ReadPartMci(
        ksPart part, int bitVector, ProbeStep step, string label)
    {
        try
        {
            if (part.CalcMassInertiaProperties((uint)bitVector) is not ksMassInertiaParam properties)
            {
                step.Observe("CalcMassInertiaProperties(0x" + bitVector.ToString("X", CultureInfo.InvariantCulture)
                    + ") [" + label + "] вернул не ksMassInertiaParam");
                return (null, null, null);
            }

            var r = Api5.SafeDouble(() => properties.r);
            var m = Api5.SafeDouble(() => properties.m);
            var v = Api5.SafeDouble(() => properties.v);
            step.Observe("[" + label + "] 0x" + bitVector.ToString("X", CultureInfo.InvariantCulture)
                + ": r=" + Api5.Num(r) + ", m=" + Api5.Num(m) + ", v=" + Api5.Num(v));
            return (r, m, v);
        }
        catch (Exception ex)
        {
            step.Observe("CalcMassInertiaProperties(0x" + bitVector.ToString("X", CultureInfo.InvariantCulture)
                + ") [" + label + "] бросил " + ex.GetType().Name + ": " + ex.Message);
            return (null, null, null);
        }
    }

    private static bool Close(double? actual, double expected, double relativeTolerance = RelativeTolerance)
        => actual is { } value && Math.Abs(value - expected) <= Math.Abs(expected) * relativeTolerance;

    /// <summary><c>m = r · v</c> inside one combination — the structure is self-consistent.</summary>
    private static bool SelfConsistent(double? r, double? m, double? v)
    {
        if (r is not { } density || m is not { } mass || v is not { } volume)
        {
            return false;
        }

        var expected = density * volume;
        return Math.Abs(mass - expected) <= Math.Abs(expected) * RelativeTolerance;
    }

    private static string Describe(Dictionary<string, object?> row)
    {
        var failed = row.Keys
            .Where(k => k.StartsWith("criterion_", StringComparison.Ordinal) && row[k] is bool value && !value)
            .ToList();
        return "не выполнены: " + string.Join(", ", failed)
            + " | r(M|KG)=" + Api5.Num(row["r_m_kg"] as double?)
            + ", r(MM|KG)=" + Api5.Num(row["r_mm_kg"] as double?)
            + ", m=" + Api5.Num(row["m_m_kg"] as double?)
            + ", v(M|KG)=" + Api5.Num(row["v_m_kg"] as double?)
            + ", v(MM|KG)=" + Api5.Num(row["v_mm_kg"] as double?);
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

    private ksDocument3D? NewOpen(string path, ProbeStep step)
    {
        try
        {
            if (_app.Document3D() is not ksDocument3D doc)
            {
                return null;
            }

            if (!doc.Open(path, true))
            {
                step.Observe("Open(«" + path + "») → false");
                return null;
            }

            return doc;
        }
        catch (Exception ex)
        {
            step.Observe("открытие бросило " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private void Shutdown()
    {
        var step = Begin("DM.Z", "Завершение сеанса", "Свой процесс уходит?");
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

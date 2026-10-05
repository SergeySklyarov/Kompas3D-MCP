using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe B5 — calibration of three families of the last mandatory queue: the sweep operation
/// (SM-04), the loft (SM-05), and the shell (SM-13).</summary>
/// <remarks>
/// <b>Why a separate probe rather than adapter code right away.</b> Order B5 (§7) requires closing two
/// open questions BY MEASUREMENT, not by choosing a route: <c>OQ-A1</c> — what creates the BASE sweep
/// operation, and <c>OQ-A12</c> — what the values of the thin-wall direction are. Both decide the row's
/// route, so they are measured before the code. Plus §8 requires confirming units with a separate number
/// for each new mode, and §9 gives analytical references that must be checked, not substituted.
/// <b>What has already been measured by reading the official help (not by this probe).</b>
/// <c>obj3dtype.html</c> documents the type correspondence:
/// <c>o3d_baseEvolution = 45 → ksBaseEvolutionDefinition / IEvolution</c>,
/// <c>o3d_baseLoft = 30 → ksBaseLoftDefinition / ILoft</c>,
/// <c>o3d_shellOperation = 43 → ksShellDefinition / IShell</c>.
/// At the same time <c>ievolutions_add.html</c> lists as admissible for <c>IEvolutions::Add</c> only
/// <c>o3d_bossEvolution</c> and <c>o3d_cutEvolution</c>, and <c>ilofts_add.html</c> for <c>ILofts::Add</c> —
/// only <c>o3d_bossLoft</c> and <c>o3d_cutLoft</c>. The BASE type is in neither list.
/// Therefore "create a base operation via API7 Add" is not a documented route, and the probe must either
/// confirm or refute this — and name the outcome.
/// <b>What the probe does NOT do.</b> It does not look for undocumented workarounds and does not probe
/// presumed COM members: only the members listed by the official help of the target version (SDK pages
/// <c>ievolution_propers</c>, <c>iloft_propers</c>, <c>ishell_propers</c>) and their API5 counterparts
/// declared in the interop are checked. A member found in the interop is marked "member found in the
/// interface" and does not make the route official.
/// History: docs/decisions/probes.md#b5</remarks>
internal sealed class B5Probe
{
    private const short BaseEvolution = 45;   // obj3dtype.html: o3d_baseEvolution = 45
    private const short BaseLoft = 30;        // obj3dtype.html: o3d_baseLoft = 30
    private const short BossEvolutionType = 46; // obj3dtype.html: o3d_bossEvolution = 46, present in ievolutions_add
    private const short BossLoftType = 31;      // obj3dtype.html: o3d_bossLoft = 31, present in ilofts_add
    private const short ShellOperation = 43;  // obj3dtype.html: o3d_shellOperation = 43
    private const short PlaneYoz = 3;         // X = 0

    /// <summary>
    /// <c>OperationElement = 110</c> — the collection by which the feature tree is addressed
    /// (<c>KompasObjectTypes.OperationElement</c> in the adapter). The route through
    /// <c>SubFeatureCollection</c> yields <c>ksFeature</c> and does not read definitions from it.
    /// </summary>
    private const int OperationElement = 110;

    private const double Pi = Math.PI;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public B5Probe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "b5-sweep-loft-shell.json"),
            Path.Combine(options.ReportDir, "b5-sweep-loft-shell.md"));

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
            SweepStraight();
            SweepShiftModes();
            PathLengthUnits();
            LoftTwoSections();
            ShellDirection();
            ShellThicknessAndFaces();
            Api7Routes();
            BossRoute();
            Api7LoftRoute();
            Api7ShellRoute();
            InteropSurface();
            TreeReadBack();
            EditInPlace();
            LoftInputEdit();
            LoftShrinkCreatedThree();
            ShellFaceSetEdit();
            SweepInputEdit();
            SweepInputStores();
            CouplingChains();
            CouplingBeforeFirstUpdate();
            CouplingsAcrossSectionWrite();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.0 ══

    /// <summary>The basis of the route, read from the official help of the target version. This is a statement
    /// about the DOCUMENTATION, not about the product, and it is needed to distinguish "the route is
    /// documented" from "the member was found in the interface".</summary>
    private void Documentation()
    {
        var step = _report.Begin("B5.0", "Основание маршрута по официальной справке v24",
            "Какой маршрут создания базовой кинематической операции и базовой операции по сечениям документирован?");

        const string types =
            "obj3dtype.html: o3d_baseEvolution = 45 → ksBaseEvolutionDefinition / IEvolution; "
            + "o3d_baseLoft = 30 → ksBaseLoftDefinition / ILoft; "
            + "o3d_shellOperation = 43 → ksShellDefinition / IShell.";
        const string evAdd =
            "ievolutions_add.html: «Допустимыми значениями EvolutionType являются: o3d_bossEvolution, "
            + "o3d_cutEvolution для коллекции операций IModelContainer::Evolutions; o3d_EvolutionSurface "
            + "для коллекции поверхностей ISurfaceContainer::EvolutionSurfaces». o3d_baseEvolution в "
            + "списке ОТСУТСТВУЕТ.";
        const string loftAdd =
            "ilofts_add.html: «Допустимыми значениями LoftType являются: o3d_bossLoft, o3d_cutLoft для "
            + "коллекции операций IModelContainer::Lofts; o3d_LoftSurface для коллекции поверхностей "
            + "ISurfaceContainer::LoftSurfaces». o3d_baseLoft в списке ОТСУТСТВУЕТ.";
        const string shellAdd =
            "ishells_add.html: «Add() — Метод позволяет создать новый интерфейс операции оболочка». "
            + "Тип не принимается вовсе, ограничения по типу нет.";
        const string container =
            "imodelcontainer_evolutions/lofts/shells.html: свойства Evolutions → IEvolutions, "
            + "Lofts → ILofts, Shells → IShells, все «доступно только для чтения».";

        step.Observe(types);
        step.Observe(evAdd);
        step.Observe(loftAdd);
        step.Observe(shellAdd);
        step.Observe(container);
        step.Data["obj3dtype"] = types;
        step.Data["ievolutions_add"] = evAdd;
        step.Data["ilofts_add"] = loftAdd;
        step.Data["ishells_add"] = shellAdd;
        step.Data["container_props"] = container;
        step.Data["source"] = "https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ (страницы читаны по проводу 20.09.2026)";
        step.Pass("основание маршрута прочитано; базовые типы в списках Add отсутствуют — маршрут "
            + "создания базовых операций проверяется измерением ниже");
    }

    // ══════════════════════════════════════════════════════════════ B5.1 ══

    /// <summary>Reference A of the sweep operation: a Ø20 circle along a straight 100 mm segment.
    /// Expectation <c>S × L = π·10²·100 = 31415.926535897932</c> mm³. The number is obtained neither from
    /// the bounding box (20×20×100 = 40000) nor from the reading "radius = 20" (four times larger) —
    /// hence it is discriminating.</summary>
    private void SweepStraight()
    {
        var step = _report.Begin("B5.1", "API5 NewEntity(45) + ksBaseEvolutionDefinition: окружность Ø20 по отрезку 100",
            "Создаётся ли базовое тело кинематической операции и равен ли объём π·10²·100?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P1", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathLine(part, "T1", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
            {
                step.Fail("профиль или траектория не построены");
                return;
            }

            var ok = Sweep(doc, part, step, "T1", "P1", shift: 0);
            var volume = TotalVolume(part);
            var expected = Pi * 100d * 100d;
            step.Data["volume_mm3"] = volume;
            step.Data["expected_mm3"] = expected;
            step.Data["bodies"] = Api5.BodyCount(part);
            step.Observe("измерено: тел " + Api5.BodyCount(part) + ", объём " + Api5.Num(volume)
                + "; ожидание " + Api5.Num(expected) + " (габарит 20×20×100 = 40000, «радиус=20» = "
                + Api5.Num(Pi * 400d * 100d) + ")");
            if (!ok)
            {
                step.Fail("кинематическая операция не создана");
                return;
            }

            if (volume is { } v && Math.Abs(v - expected) < 0.01)
            {
                step.Pass("базовое тело создано, объём совпал с аналитикой — маршрут NewEntity(45) "
                    + "работает на v24");
            }
            else
            {
                step.Unknown("тело создано, но объём " + Api5.Num(volume) + " не равен ожиданию "
                    + Api5.Num(expected) + " — расхождение разбирается, а не сглаживается");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.2 ══

    /// <summary>
    /// Reference B, and it is also discriminating: on a STRAIGHT trajectory the modes "parallel to
    /// itself" and "orthogonal to the trajectory" yield the same body, so the row
    /// <c>SM-04.base.mode_orthogonal</c> is indistinguishable on a straight line and cannot be closed.
    /// Here the trajectory is an R50 arc through 90°, the arc length is <c>50·π/2 = 78.53981633974483</c>,
    /// and with the orthogonal orientation the volume equals <c>S × L = 24674.011002723397</c>.
    /// </summary>
    private void SweepShiftModes()
    {
        var step = _report.Begin("B5.2", "Различающая постановка: дуга R50/90°, режимы движения сечения",
            "Отличается ли ортогональный режим от параллельного на дуге, и чему равен объём каждого?");
        var expectedOrthogonal = Pi * 100d * 50d * Pi / 2d;
        step.Data["expected_orthogonal_mm3"] = expectedOrthogonal;
        step.Observe("ожидание при ортогональном режиме: S × L = " + Api5.Num(Pi * 100d)
            + " × " + Api5.Num(50d * Pi / 2d) + " = " + Api5.Num(expectedOrthogonal));

        var measured = new Dictionary<int, double?>();
        foreach (var (mode, name) in new[] { (2, "ортогонально (ksEvShiftOrtogonal)"), (0, "параллельно (ksEvShiftParallel)") })
        {
            var doc = NewPart(out var part);
            try
            {
                if (!BuildProfile(part, "P2", PlaneYoz, 0d, 0d, 10d, step)
                    || !BuildPathArc(part, "T2", Api5.PlaneXoy, 0d, 50d, 50d, 0d, 0d, 50d, 50d, step))
                {
                    step.Observe(name + ": профиль или дуга не построены");
                    continue;
                }

                var created = Sweep(doc, part, step, "T2", "P2", shift: mode);
                var volume = TotalVolume(part);
                measured[mode] = volume;
                step.Observe(name + ": создано=" + created + ", тел " + Api5.BodyCount(part)
                    + ", объём " + Api5.Num(volume));
            }
            finally
            {
                TryClose(doc);
            }
        }

        step.Data["volume_orthogonal_mm3"] = measured.GetValueOrDefault(2);
        step.Data["volume_parallel_mm3"] = measured.GetValueOrDefault(0);

        if (measured.GetValueOrDefault(2) is { } orth && measured.GetValueOrDefault(0) is { } par)
        {
            step.Data["difference_mm3"] = Math.Abs(orth - par);
            step.Observe("разница режимов: " + Api5.Num(Math.Abs(orth - par)) + " мм³");
            if (Math.Abs(orth - par) < 0.01)
            {
                step.Fail("режимы дали ОДИН объём на дуге — режим не применяется, и это отказ, а не успех");
            }
            else if (Math.Abs(orth - expectedOrthogonal) < 0.01)
            {
                step.Pass("ортогональный режим измерен и совпал с S × L; параллельный отличается на "
                    + Api5.Num(Math.Abs(orth - par)) + " мм³ — режим различает");
            }
            else
            {
                step.Unknown("режимы различимы, но ортогональный не совпал с ожиданием: "
                    + Api5.Num(orth) + " против " + Api5.Num(expectedOrthogonal));
            }
        }
        else
        {
            step.Unknown("не удалось измерить оба режима — сравнение неполно");
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.3 ══

    /// <summary>The units of <c>GetPathLength(bitVector)</c>. The argument is a <c>ST_MIX_*</c> bit mask; zero is
    /// forbidden. A straight segment of a known length gives the reference: the measured value is compared
    /// with the analytical one, and a factor of 1000 would separate millimetres from metres.</summary>
    private void PathLengthUnits()
    {
        var step = _report.Begin("B5.3", "GetPathLength(bitVector): что означает переданная единица",
            "Возвращает ли GetPathLength(1) миллиметры для отрезка 100 мм?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P3", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathLine(part, "T3", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
            {
                step.Fail("профиль или траектория не построены");
                return;
            }

            if (part.NewEntity(BaseEvolution) is not ksEntity entity
                || entity.GetDefinition() is not ksBaseEvolutionDefinition definition)
            {
                step.Fail("определение кинематической операции не получено");
                return;
            }

            definition.SetSketch(Find(part, "P3"));
            if (!AttachPath(part, definition, "T3", step))
            {
                step.Fail("траектория не присоединена");
                return;
            }

            // The first revision read the length BEFORE the build and got 0 — that is a probe defect, not a
            // fact about the product: GetPathLength answers from the built operation. So the operation is
            // built here, and the build result is published.
            var created = Api5.SafeBool(entity.Create) == true;
            part.RebuildModel();
            doc.RebuildDocument();
            step.Data["created"] = created;
            step.Observe("операция построена: " + created);

            foreach (var (bits, label) in new[]
                     {
                         (1u, "ST_MIX_LENGTH_MM (1)"),
                         (16u, "ST_MIX_MASS_KG (16)"),
                     })
            {
                var value = Api5.SafeDouble(() => definition.GetPathLength(bits));
                step.Data["length_bits_" + bits] = value;
                step.Observe("GetPathLength(" + bits + ") [" + label + "] = " + Api5.Num(value));
            }

            step.Data["expected_mm"] = 100d;
            var mm = (double?)step.Data["length_bits_1"];
            if (mm is { } value1 && Math.Abs(value1 - 100d) < 0.01)
            {
                step.Pass("GetPathLength(1) вернул 100 для отрезка 100 мм — единица подтверждена числом");
            }
            else
            {
                step.Unknown("GetPathLength(1) = " + Api5.Num(mm) + " вместо 100 — единица НЕ подтверждена, "
                    + "и это записывается как неподтверждённая, а не подгоняется");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.4 ══

    /// <summary>
    /// Reference for the loft: a truncated pyramid 40×40 → 20×20 at height 30.
    /// <c>V = h/3 · (A₁ + A₂ + √(A₁A₂)) = 10 · (1600 + 400 + 800) = 28000</c> mm³.
    /// Discriminating controls: "mean area × height" = 30000 (difference 2000), a prism on the lower
    /// section = 48000, on the upper = 12000. Additionally the number of sections is measured: concentric
    /// parallel sections do not tell the ORDER apart by volume, so the order is checked by a separate setup,
    /// not by this number.
    /// </summary>
    private void LoftTwoSections()
    {
        var step = _report.Begin("B5.4", "API5 NewEntity(30) + ksBaseLoftDefinition: пирамида 40×40 → 20×20, h30",
            "Создаётся ли тело по двум сечениям и равен ли объём 28000?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S1", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S2", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Fail("сечения не построены");
                return;
            }

            if (part.NewEntity(BaseLoft) is not ksEntity entity
                || entity.GetDefinition() is not ksBaseLoftDefinition definition)
            {
                step.Fail("определение операции по сечениям не получено");
                return;
            }

            var sections = Api5.SafeObject(() => definition.Sketchs());
            step.Observe("Sketchs() вернул: " + Api5.RuntimeName(sections));
            var added = AddSections(sections, new[] { Find(part, "S1"), Find(part, "S2") }, step);
            step.Data["sections_added"] = added;

            var created = Api5.SafeBool(entity.Create) == true;
            part.RebuildModel();
            doc.RebuildDocument();
            var volume = TotalVolume(part);
            step.Data["created"] = created;
            step.Data["volume_mm3"] = volume;
            step.Data["expected_mm3"] = 28000d;
            step.Observe("измерено: создано=" + created + ", тел " + Api5.BodyCount(part)
                + ", объём " + Api5.Num(volume) + "; ожидание 28000; «средняя площадь» дала бы 30000");

            if (!created)
            {
                step.Fail("операция по сечениям не создана");
                return;
            }

            if (volume is { } v && Math.Abs(v - 28000d) < 0.01)
            {
                step.Pass("объём совпал с формулой усечённой пирамиды — маршрут NewEntity(30) работает");
            }
            else
            {
                step.Unknown("тело создано, объём " + Api5.Num(volume) + " не равен 28000 — разбирается");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.5 ══

    /// <summary>
    /// OQ-A12 — the VALUES of the thin-wall direction. In API7 <c>IShell.ThinType</c> is declared as
    /// <c>long</c>, in API5 <c>ksShellDefinition.thinType</c> as <c>bool</c>: the types differ, the values
    /// are unknown. The VOLUME tells them apart, not the text of the answer. A 100×80×10 box with the top
    /// face removed, t = 2: "inward" gives 21632, "outward" — 24832, a difference of 3200 mm³.
    /// </summary>
    private void ShellDirection()
    {
        var step = _report.Begin("B5.5", "OQ-A12: значения thinType по объёму короба 100×80×10 с удалённой верхней гранью",
            "Какое значение thinType даёт 21632 (внутрь) и какое 24832 (наружу)?");
        step.Data["expected_inward_mm3"] = 21632d;
        step.Data["expected_outward_mm3"] = 24832d;
        step.Observe("ожидание: внутрь 21632 = 80000 − 96·76·8; наружу 24832 = 104·84·12 − 80000");

        foreach (var thinType in new[] { false, true })
        {
            var doc = NewPart(out var part);
            try
            {
                if (!BuildBox(part, "BOX", 100d, 80d, 10d, step, out var faces))
                {
                    step.Observe("thinType=" + thinType + ": короб не построен");
                    continue;
                }

                var created = Shell(doc, part, step, thickness: 2d, thinType: thinType,
                    removeTopFace: true, faces: faces);
                var volume = TotalVolume(part);
                step.Data["volume_thin_" + thinType] = volume;
                step.Data["created_thin_" + thinType] = created;
                step.Observe("thinType=" + thinType + ": создано=" + created + ", объём " + Api5.Num(volume));
            }
            finally
            {
                TryClose(doc);
            }
        }

        var whenFalse = (double?)step.Data["volume_thin_False"];
        var whenTrue = (double?)step.Data["volume_thin_True"];
        if (whenFalse is { } a && whenTrue is { } b && Math.Abs(a - b) > 0.01)
        {
            step.Data["difference_mm3"] = Math.Abs(a - b);
            // The classification does NOT presume which half corresponds to which value: both magnitudes
            // are compared with the analytical pair {21632 inward, 24832 outward}, and the correspondence
            // is named by measurement. The first revision expected "false = inward" and got the opposite —
            // the error was in the EXPECTATION, not in the measurement.
            var falseIsInward = Math.Abs(a - 21632d) < 0.01;
            var trueIsInward = Math.Abs(b - 21632d) < 0.01;
            if (falseIsInward != trueIsInward)
            {
                step.Data["thin_false_means"] = falseIsInward ? "внутрь (21632)" : "наружу (24832)";
                step.Data["thin_true_means"] = trueIsInward ? "внутрь (21632)" : "наружу (24832)";
                step.Pass("thinType=false → " + (falseIsInward ? "внутрь 21632" : "наружу 24832")
                    + ", thinType=true → " + (trueIsInward ? "внутрь 21632" : "наружу 24832")
                    + "; OQ-A12 закрыт объёмом, отличие " + Api5.Num(Math.Abs(a - b)) + " мм³");
            }
            else
            {
                step.Unknown("обе величины совпали с одной и той же стороной — классификация невозможна: "
                    + Api5.Num(a) + " / " + Api5.Num(b));
            }
        }
        else
        {
            step.Unknown("оба значения дали один объём — направление не применяется, либо короб собран неверно");
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.6 ══

    /// <summary>The thickness and the list of removed faces. <c>t = 4</c> with an open shell inward gives a cavity
    /// 92×72×6 = 39744 and a volume 80000 − 39744 = 40256; the difference from <c>t = 2</c> equals 18624 mm³.
    /// An empty face list gives a CLOSED shell 36224 — this is checked, not assumed.</summary>
    private void ShellThicknessAndFaces()
    {
        var step = _report.Begin("B5.6", "Толщина t=4 и пустой список граней оболочки",
            "Даёт ли t=4 объём 40256, а пустой список граней — замкнутую оболочку 36224?");
        step.Data["expected_t4_mm3"] = 40256d;
        step.Data["expected_closed_mm3"] = 36224d;
        step.Observe("ожидание: t=4 открытая 40256 = 80000 − 92·72·6; без удалённых граней 36224 = 80000 − 96·76·6");

        // thinType=true — INWARD: that is how B5.5 measured it (24832/21632), and here it is not assumed
        // anew but taken from the measurement. The first revision passed false and got 53056 — a number
        // correct for "outward" (108·88·14 − 80000 = 53056); what was wrong was the expectation 40256.
        foreach (var (thickness, removeTop, inward, label, key) in new[]
                 {
                     (4d, true, true, "t=4, верхняя грань удалена, внутрь", "t4_inward"),
                     (2d, false, true, "t=2, грани не удалены, внутрь", "closed_inward"),
                     (2d, false, false, "t=2, грани не удалены, наружу", "closed_outward"),
                 })
        {
            var doc = NewPart(out var part);
            try
            {
                if (!BuildBox(part, "BOX", 100d, 80d, 10d, step, out var faces))
                {
                    step.Observe(label + ": короб не построен");
                    continue;
                }

                var created = Shell(doc, part, step, thickness, thinType: inward, removeTopFace: removeTop,
                    faces: faces);
                var volume = TotalVolume(part);
                step.Data["volume_" + key] = volume;
                step.Data["bodies_" + key] = Api5.BodyCount(part);
                step.Data["created_" + key] = created;
                step.Observe(label + ": создано=" + created + ", тел " + Api5.BodyCount(part)
                    + ", объём " + Api5.Num(volume));
            }
            finally
            {
                TryClose(doc);
            }
        }

        var t4Inward = (double?)step.Data["volume_t4_inward"];
        var closedInward = (double?)step.Data["volume_closed_inward"];
        var closedOutward = (double?)step.Data["volume_closed_outward"];
        if (t4Inward is { } a && Math.Abs(a - 40256d) < 0.01)
        {
            step.Pass("t=4 внутрь дал 40256 = 80000 − 92·72·6 — толщина подтверждена вторым числом");
        }
        else
        {
            step.Unknown("t=4 внутрь: измерено " + Api5.Num(t4Inward) + " против ожидания 40256");
        }

        step.Data["closed_inward_mm3"] = closedInward;
        step.Data["closed_outward_mm3"] = closedOutward;
        step.Observe("замкнутая оболочка (грани не удалены): внутрь " + Api5.Num(closedInward)
            + " против ожидания 36224; наружу " + Api5.Num(closedOutward)
            + " против ожидания 24832");
    }

    // ══════════════════════════════════════════════════════════════ B5.7 ══

    /// <summary>
    /// The API7 routes. Exactly what is documented is checked: the container properties
    /// <c>Evolutions</c>/<c>Lofts</c>/<c>Shells</c> and the <c>Add</c> method of each collection. The base
    /// types 45 and 30 are not in the <c>Add</c> lists, so the outcome for them is named BY NAME rather
    /// than passed off as a documented route.
    /// </summary>
    private void Api7Routes()
    {
        var step = _report.Begin("B5.7", "API7: свойства контейнера и Add у трёх коллекций",
            "Доступны ли Evolutions/Lofts/Shells и что отвечает Add на базовых типах 45 и 30?");
        var doc = NewPart(out var part);
        try
        {
            if (Container(doc) is not { } container)
            {
                step.Fail("контейнер API7 не получен");
                return;
            }

            var evolutions = Api5.SafeObject(() => container.Evolutions);
            var lofts = Api5.SafeObject(() => container.Lofts);
            var shells = Api5.SafeObject(() => container.Shells);
            step.Data["evolutions_type"] = Api5.RuntimeName(evolutions);
            step.Data["lofts_type"] = Api5.RuntimeName(lofts);
            step.Data["shells_type"] = Api5.RuntimeName(shells);
            step.Observe("Evolutions → " + Api5.RuntimeName(evolutions));
            step.Observe("Lofts → " + Api5.RuntimeName(lofts));
            step.Observe("Shells → " + Api5.RuntimeName(shells));

            var evolutionBase = Api5.SafeObject(() => ((IEvolutions)evolutions!).Add((ksObj3dTypeEnum)45));
            step.Data["add_45_type"] = Api5.RuntimeName(evolutionBase);
            step.Observe("IEvolutions.Add(45) → " + Api5.RuntimeName(evolutionBase)
                + " (документированные значения Add: 46, 47; 45 в списке нет)");

            var loftBase = Api5.SafeObject(() => ((ILofts)lofts!).Add((ksObj3dTypeEnum)30));
            step.Data["add_30_type"] = Api5.RuntimeName(loftBase);
            step.Observe("ILofts.Add(30) → " + Api5.RuntimeName(loftBase)
                + " (документированные значения Add: 31, 32; 30 в списке нет)");

            var shell = Api5.SafeObject(() => ((IShells)shells!).Add());
            step.Data["shell_add_type"] = Api5.RuntimeName(shell);
            step.Observe("IShells.Add() → " + Api5.RuntimeName(shell) + " (тип не принимается)");

            if (shell is IShell)
            {
                step.Pass("маршрут оболочки через API7 подтверждён: Shells.Add() вернул IShell");
            }
            else
            {
                step.Unknown("Shells.Add() не вернул IShell — маршрут оболочки остаётся за API5");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.8 ══

    /// <summary>
    /// The ATTACHED (not deprecated) interfaces. The v24 help on the pages
    /// <c>ksbaseevolutiondefinition.html</c> and <c>ksbaseloftdefinition.html</c> states plainly:
    /// «Данный интерфейс устарел. Рекомендуется использовать вместо него интерфейс
    /// ksBossLoftDefinition» — i.e. the base interfaces on which steps B5.1…B5.4 are built are declared
    /// DEPRECATED. The recommended ones are <c>ksBossEvolutionDefinition</c> (type 46) and
    /// <c>ksBossLoftDefinition</c> (type 31), and both types ARE in the documented lists
    /// <c>IEvolutions::Add</c> / <c>ILofts::Add</c>.
    /// </summary>
    /// <remarks>The numbers here are THE SAME as in the base steps, and that is not a repetition: the references
    /// were chosen to be discriminating (31415.926535897932 versus the bounding-box 40000 for the sweep;
    /// 28000 versus the "mean area" 30000 for the pyramid), so a matching volume proves that the 46/31
    /// route builds THE SAME body, not "something got built". Without this comparison a route change would
    /// be a change to an unmeasured one.</remarks>
    private void BossRoute()
    {
        var step = _report.Begin("B5.8",
            "Приклеенные интерфейсы 46/31: тот же объём, что у базовых 45/30?",
            "Работает ли рекомендованный справкой маршрут NewEntity(46)/NewEntity(31) и даёт ли он те же тела?");
        step.Data["expected_sweep_mm3"] = 31415.926535897932d;
        step.Data["expected_loft_mm3"] = 28000d;
        step.Data["doc_base_evolution"] = "ksbaseevolutiondefinition.html: «Данный интерфейс устарел. "
            + "Рекомендуется использовать вместо него интерфейс ksBossLoftDefinition» (в тексте страницы "
            + "именно так; для кинематического элемента ожидался ksBossEvolutionDefinition — расхождение "
            + "в самой справке, записано как есть)";
        step.Data["doc_base_loft"] = "ksbaseloftdefinition.html: «Данный интерфейс устарел. Рекомендуется "
            + "использовать вместо него интерфейс ksBossLoftDefinition»";
        step.Data["doc_add_lists"] = "ievolutions_add.html допускает o3d_bossEvolution (46) и o3d_cutEvolution "
            + "(47); ilofts_add.html — o3d_bossLoft (31) и o3d_cutLoft (32). То есть у приклеенных типов "
            + "документирован И фабричный маршрут API7, чего у базовых 45/30 нет";

        BossSweep(step);
        BossLoft(step);

        // The classification MUST be here, not in the sub-steps: otherwise the step measures and stays
        // silent, and a silent probe reads as "not checked". The first revision of B5.8 did exactly that
        // and returned verdict=unknown with two correct volumes — a probe defect, not a product fact.
        var sweep = (double?)step.Data["boss_volume_mm3"];
        var loft = (double?)step.Data["boss_loft_volume_mm3"];
        var sweepOk = sweep is { } s && Math.Abs(s - 31415.926535897932d) < 1e-6;
        var loftOk = loft is { } l && Math.Abs(l - 28000d) < 0.01;

        if (sweepOk && loftOk)
        {
            step.Pass("рекомендованный справкой маршрут работает и строит ТЕ ЖЕ тела: кинематический "
                + "элемент 46 дал " + Api5.Num(sweep) + " (эталон 31415.926535897932), элемент по "
                + "сечениям 31 дал " + Api5.Num(loft) + " (эталон 28000). SetLoftParam и GetLoftParam "
                + "вернули True");
        }
        else
        {
            step.Fail("маршрут 46/31 дал другие числа: развёртка " + Api5.Num(sweep)
                + " (эталон 31415.926535897932), пирамида " + Api5.Num(loft) + " (эталон 28000)");
        }
    }

    /// <summary>The type 46 sweep element by the same references as steps B5.1/B5.3.</summary>
    private void BossSweep(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P4", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathLine(part, "T4", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
            {
                step.Observe("профиль или траектория не построены — шаг не состоялся");
                return;
            }

            if (part.NewEntity(BossEvolutionType) is not ksEntity entity
                || entity.GetDefinition() is not ksBossEvolutionDefinition definition)
            {
                step.Fail("NewEntity(46) не дал ksBossEvolutionDefinition");
                return;
            }

            step.Data["boss_definition"] = "ksBossEvolutionDefinition получен";
            definition.SetSketch(Find(part, "P4"));
            definition.sketchShiftType = 2; // orthogonal, as in B5.1
            var attached = AttachPathTo(part, Api5.SafeObject(definition.PathPartArray), "T4", step);
            step.Data["boss_path_attached"] = attached;

            var created = Api5.SafeBool(entity.Create) == true;
            Api5.SafeBool(entity.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volume = TotalVolume(part);
            step.Data["boss_created"] = created;
            step.Data["boss_volume_mm3"] = volume;
            step.Observe("NewEntity(46): создано=" + created + ", тел " + Api5.BodyCount(part)
                + ", объём " + Api5.Num(volume) + "; ожидание 31415.926535897932");

            var length = Api5.SafeDouble(() => definition.GetPathLength(1u));
            step.Data["boss_path_length_mm"] = length;
            step.Observe("GetPathLength(1) ПОСЛЕ построения: " + Api5.Num(length));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>The type 31 loft element by the same reference as step B5.4.</summary>
    private void BossLoft(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S3", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S4", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Observe("сечения не построены — шаг не состоялся");
                return;
            }

            if (part.NewEntity(BossLoftType) is not ksEntity entity
                || entity.GetDefinition() is not ksBossLoftDefinition definition)
            {
                step.Fail("NewEntity(31) не дал ksBossLoftDefinition");
                return;
            }

            step.Data["boss_loft_definition"] = "ksBossLoftDefinition получен";

            // SetLoftParam(closed, flipVertex, autoPath) — a documented method
            // (ksbaseloftdefinition_setloftparam.html): closed is the TRAJECTORY closure flag, flipVertex
            // is reserved, autoPath is automatic trajectory generation.
            var loftParam = Api5.SafeBool(() => definition.SetLoftParam(false, false, true));
            step.Data["set_loft_param"] = loftParam;
            step.Observe("SetLoftParam(closed=false, flipVertex=false, autoPath=true) = " + loftParam);

            var added = AddSections(Api5.SafeObject(definition.Sketchs),
                new[] { Find(part, "S3"), Find(part, "S4") }, step);
            step.Data["boss_sections_added"] = added;

            var created = Api5.SafeBool(entity.Create) == true;
            Api5.SafeBool(entity.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volume = TotalVolume(part);
            step.Data["boss_loft_created"] = created;
            step.Data["boss_loft_volume_mm3"] = volume;
            step.Observe("NewEntity(31): создано=" + created + ", тел " + Api5.BodyCount(part)
                + ", объём " + Api5.Num(volume) + "; ожидание 28000");

            // Read-back: GetLoftParam must return what was set. This is a control of "was it written",
            // not "was it applied": application is judged by the volume.
            var readBack = Api5.SafeObject(() => definition.GetLoftParam(out _, out _, out _));
            step.Data["get_loft_param"] = readBack is bool b ? b : null;
            step.Observe("GetLoftParam() = " + readBack);
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.9 ══

    /// <summary>
    /// The API7 route of the loft. The mandatory row <c>SM-05.base.mode_couplings</c> requires
    /// <b>section correspondence chains</b>, and API5 has none at all (divergence D3) — so the row's
    /// route must be API7. The v24 help: <c>ilofts_add.html</c> — «Допустимыми значениями
    /// LoftType являются o3d_bossLoft, o3d_cutLoft для коллекции операций IModelContainer::Lofts»,
    /// «после получения нового интерфейса нужно задать параметры операции и вызвать метод
    /// IModelObject::Update»; <c>iloft_sketchs.html</c> — <c>Sketchs</c> is of type <c>VARIANT</c> and is
    /// <b>set</b> (<c>put_Sketchs</c>) as a <c>SAFEARRAY</c> of <c>LPDISPATCH</c> objects
    /// (<c>VT_ARRAY | VT_DISPATCH</c>); <c>iloft_addcoupling.html</c> — <c>AddCoupling()</c> returns
    /// <c>ICoupling</c>.
    /// </summary>
    /// <remarks>The reference is the same as in B5.4/B5.8 — a truncated pyramid of 28000 mm³, and that is not a
    /// repetition: a match proves that the API7 route builds THE SAME body. Additionally what the row
    /// requires API7 for at all is measured: does the correspondence chain exist as an object, and is its
    /// count readable.</remarks>
    private void Api7LoftRoute()
    {
        var step = _report.Begin("B5.9",
            "API7 ILoft: Sketchs как SAFEARRAY, AddCoupling, Closed, BuildingType",
            "Выражается ли цепочка соответствий (обязательная строка SM-05.base.mode_couplings) и совпадает ли объём с 28000?");
        step.Data["expected_mm3"] = 28000d;

        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S5", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S6", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Observe("сечения не построены — шаг не состоялся");
                return;
            }

            if (Container(doc) is not { } container)
            {
                step.Fail("контейнер API7 не получен");
                return;
            }

            var lofts = Api5.SafeObject(() => container.Lofts);
            if (lofts is not ILofts collection)
            {
                step.Fail("container.Lofts не привёлся к ILofts: " + Api5.RuntimeName(lofts));
                return;
            }

            var loft = Api5.SafeObject(() => collection.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (loft is null)
            {
                step.Fail("ILofts.Add(31) не вернул ILoft");
                return;
            }

            // The sections are transferred into API7: Sketchs takes a SAFEARRAY of IDispatch pointers, and
            // an untransferred object is a value API7 will not see. The same technique as measured on
            // IChamfer.BaseObjects (transfer + assignment of object[]).
            var first = TransferTo7(Find(part, "S5"));
            var second = TransferTo7(Find(part, "S6"));
            step.Data["sections_transferred"] = first is not null && second is not null;
            step.Observe("перенос сечений в API7: " + Api5.RuntimeName(first) + ", " + Api5.RuntimeName(second));

            loft.Sketchs = new object[] { first!, second! };
            var readBack = Api5.SafeObject(() => loft.Sketchs);
            step.Data["sketchs_readback_type"] = Api5.RuntimeName(readBack);
            step.Data["sketchs_readback_count"] = (readBack as Array)?.Length;
            step.Observe("после присваивания Sketchs → " + Api5.RuntimeName(readBack)
                + ", элементов " + ((readBack as Array)?.Length.ToString() ?? "не прочитано"));

            var closedSet = Api5.SafeBool(() =>
            {
                loft.Closed = false;
                return loft.Closed;
            });
            step.Data["closed_roundtrip"] = closedSet;
            step.Observe("Closed: записано false, прочитано " + closedSet);

            var updated = Api5.SafeBool(loft.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volume = TotalVolume(part);
            step.Data["created"] = updated;
            step.Data["volume_mm3"] = volume;
            step.Data["bodies"] = Api5.BodyCount(part);
            step.Observe("ILoft.Update()=" + updated + ", тел " + Api5.BodyCount(part)
                + ", объём " + Api5.Num(volume) + "; ожидание 28000");

            // What the row requires API7 for: the correspondence chain as an object.
            var coupling = Api5.SafeObject(loft.AddCoupling);
            step.Data["coupling_type"] = Api5.RuntimeName(coupling);
            step.Observe("AddCoupling() → " + Api5.RuntimeName(coupling));

            var couplings = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["couplings_count"] = couplings;
            step.Observe("CouplingsCount = " + (couplings?.ToString() ?? "не прочитано"));

            var buildingBegin = IndexedInt(loft, "BuildingType", true);
            var buildingEnd = IndexedInt(loft, "BuildingType", false);
            step.Data["building_type_begin"] = buildingBegin;
            step.Data["building_type_end"] = buildingEnd;
            step.Observe("BuildingType(true)=" + (buildingBegin?.ToString() ?? "не прочитано")
                + ", BuildingType(false)=" + (buildingEnd?.ToString() ?? "не прочитано")
                + " (ksLoftAuto=0, ksLoftByNormal=1, ksLoftByObject=2, ksLoftCupola=3)");

            if (volume is { } v && Math.Abs(v - 28000d) < 0.01 && couplings is > 0)
            {
                step.Pass("API7-маршрут строит то же тело (28000) и цепочка соответствий существует: "
                    + "AddCoupling вернул " + Api5.RuntimeName(coupling) + ", CouplingsCount=" + couplings);
            }
            else
            {
                step.Fail("API7-маршрут: объём " + Api5.Num(volume) + " (эталон 28000), "
                    + "CouplingsCount=" + (couplings?.ToString() ?? "не прочитано"));
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.10 ══

    /// <summary>
    /// The API7 shell route: <c>IShells.Add()</c> (no type accepted — <c>ishells_add.html</c>),
    /// <c>IShell.Thickness</c> (double), <c>IShell.ThinType</c> (<b>long</b>, whereas API5
    /// <c>thinType</c> is <b>bool</b>), <c>IShell.DeletedFaces</c> (SAFEARRAY), <c>IShell.SetFaces</c>.
    /// </summary>
    /// <remarks>What is measured here is measured nowhere else: <b>which <c>long</c> value corresponds to
    /// "inward"</b>. The classification goes by the ANALYTICAL pair {21632 inward, 24832 outward} on a
    /// 100×80×10 box with the top face removed, not by the text of the answer. The empty face list is
    /// checked separately: expectation 36224, and in B5.6 on API5 that outcome did NOT come out (measured
    /// 80000) — here it is checked on API7, and the divergence stays a divergence, not hushed up.</remarks>
    private void Api7ShellRoute()
    {
        var step = _report.Begin("B5.10",
            "API7 IShell: ThinType(long), Thickness, DeletedFaces — какой стороне какое значение",
            "Даёт ли API7 пару 21632/24832, замкнутую 36224 и второе число толщины 40256?");
        step.Data["expected_inward_mm3"] = 21632d;
        step.Data["expected_outward_mm3"] = 24832d;
        step.Data["expected_closed_inward_mm3"] = 36224d;
        step.Data["expected_inward_t4_mm3"] = 40256d;
        step.Observe("ожидание: внутрь 21632 = 80000 − 96·76·8; наружу 24832 = 104·84·12 − 80000; "
            + "замкнутая внутрь 36224 = 80000 − 96·76·6; t=4 внутрь 40256 = 80000 − 92·72·6");

        var inward = Api7ShellPosting(step, 2d, 1, true, "t=2, верхняя грань удалена, ThinType=1", "inward_t2");
        var outward = Api7ShellPosting(step, 2d, 0, true, "t=2, верхняя грань удалена, ThinType=0", "outward_t2");
        var closed = Api7ShellPosting(step, 2d, 1, false, "t=2, грани НЕ удалены, ThinType=1", "closed_t2");
        var thick4 = Api7ShellPosting(step, 4d, 1, true, "t=4, верхняя грань удалена, ThinType=1", "inward_t4");

        var inwardOk = Near(inward.Volume, 21632d);
        var outwardOk = Near(outward.Volume, 24832d);
        if (inwardOk && outwardOk)
        {
            step.Data["thin_1_means"] = "внутрь (21632)";
            step.Data["thin_0_means"] = "наружу (24832)";
            step.Observe("API7: ThinType=1 → внутрь " + Api5.Num(inward.Volume)
                + ", ThinType=0 → наружу " + Api5.Num(outward.Volume)
                + "; отличие " + Api5.Num(Difference(inward.Volume, outward.Volume)) + " мм³");
        }

        // The closed shell: on API5 (step B5.6) this outcome gave 80000 against an expectation of 36224,
        // and that divergence stays open. Here it is checked on a DIFFERENT API — not to "smooth it over"
        // but to separate "API5 did not apply it" from "the shell works this way".
        if (Near(closed.Volume, 36224d))
        {
            step.Data["closed_result"] = "36224 — замкнутая оболочка подтверждена на API7";
            step.Observe("замкнутая (пустой DeletedFaces) дала " + Api5.Num(closed.Volume)
                + " — совпала с 36224; на API5 тот же исход дал 80000 (B5.6), расхождение API5 остаётся открытым");
        }
        else
        {
            step.Data["closed_result"] = "не 36224: " + Api5.Num(closed.Volume);
            step.Observe("замкнутая (пустой DeletedFaces) дала " + Api5.Num(closed.Volume)
                + " вместо 36224 — исход назван, а не сглажен");
        }

        if (Near(thick4.Volume, 40256d))
        {
            step.Observe("t=4 внутрь дал 40256 — толщина подтверждена вторым числом и на API7; "
                + "отличие от t=2 равно " + Api5.Num(Difference(thick4.Volume, inward.Volume)) + " мм³");
        }
        else
        {
            step.Observe("t=4 внутрь дал " + Api5.Num(thick4.Volume) + " вместо 40256");
        }

        var directionOk = inwardOk && outwardOk;
        var thicknessOk = Near(thick4.Volume, 40256d);
        if (directionOk && thicknessOk)
        {
            step.Pass("направление и толщина подтверждены на API7 объёмом: внутрь 21632, наружу 24832 "
                + "(отличие 3200), t=4 внутрь 40256 (отличие от t=2 18624); замкнутая дала "
                + Api5.Num(closed.Volume));
        }
        else
        {
            step.Fail("API7-оболочка: внутрь " + Api5.Num(inward.Volume) + " (21632), наружу "
                + Api5.Num(outward.Volume) + " (24832), t=4 " + Api5.Num(thick4.Volume) + " (40256)");
        }
    }

    /// <summary>
    /// One shell setup via API7: <c>IShells.Add()</c> → <c>Thickness</c>/<c>ThinType</c> →
    /// <c>DeletedFaces</c> → <c>Update()</c> → rebuild → volume and face count.
    /// </summary>
    private (double? Volume, int? Faces) Api7ShellPosting(
        ProbeStep step, double thickness, int thinType, bool removeTop, string label, string key)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBox(part, "BOX7", 100d, 80d, 10d, step, out var faces))
            {
                step.Observe(label + ": короб не построен");
                return (null, null);
            }

            if (Container(doc) is not { } container)
            {
                step.Observe(label + ": контейнер API7 не получен");
                return (null, null);
            }

            if (Api5.SafeObject(() => container.Shells) is not IShells collection)
            {
                step.Observe(label + ": container.Shells не IShells");
                return (null, null);
            }

            if (Api5.SafeObject(collection.Add) is not IShell shell)
            {
                step.Observe(label + ": IShells.Add() не вернул IShell");
                return (null, null);
            }

            shell.Thickness = thickness;
            // NOTE: in the interop IShell.ThinType is declared NOT as a number but as the type
            // ksDirectionTypeEnum (extrusion direction), although in COM it is c_long. An artifact of the
            // wrapper's typing; the value stays numeric, and its meaning is measured by VOLUME, not by the
            // type name.
            shell.ThinType = (ksDirectionTypeEnum)thinType;

            if (removeTop)
            {
                var top = TopFace(faces);
                var transferred = top?.Element is null ? null : TransferTo7(top.Element);
                if (transferred is null)
                {
                    step.Observe(label + ": верхняя грань не перенесена в API7");
                    return (null, null);
                }

                shell.DeletedFaces = new object[] { transferred };
                step.Observe(label + ": DeletedFaces ← верхняя грань (" + Api5.RuntimeName(transferred) + ")");
            }
            else
            {
                step.Observe(label + ": DeletedFaces НЕ задаётся (пустой список = замкнутая)");
            }

            var updated = Api5.SafeBool(shell.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volume = TotalVolume(part);
            var faceCount = Api5.FaceCount(part);
            var thinBack = Api5.SafeInt(() => (int)shell.ThinType);
            step.Data["volume_" + key] = volume;
            step.Data["faces_" + key] = faceCount;
            step.Data["updated_" + key] = updated;
            step.Data["thin_back_" + key] = thinBack;
            step.Observe(label + ": Update=" + updated + ", тел " + Api5.BodyCount(part)
                + ", граней " + faceCount + ", объём " + Api5.Num(volume)
                + ", ThinType прочитано обратно " + (thinBack?.ToString() ?? "не прочитано"));
            return (volume, faceCount);
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Comparison with the analytical reference at the volume tolerance (0.01 mm³ abs. / 1e-6 rel.).</summary>
    private static bool Near(double? measured, double expected) =>
        measured is { } value && Math.Abs(value - expected) <= Math.Max(0.01d, Math.Abs(expected) * 1e-6d);

    private static double? Difference(double? a, double? b) =>
        a is { } x && b is { } y ? Math.Abs(x - y) : null;


    // ══════════════════════════════════════════════════════════════ B5.11 ══

    /// <summary>The interop surface for the three families, read by <b>reflection over the managed types</b>
    /// (<c>typeof(ILoft).GetMembers()</c>), not by guessing. It is needed because some API7 members are
    /// indexed properties (<c>BuildingType(BeginSection)</c>, <c>ICoupling.Position(Index)</c>), and the
    /// C# form of accessing them is not visible from the help: the help gives COM syntax.</summary>
    /// <remarks>This is a statement about the <b>interop surface</b>, not about the documentation and not about the
    /// product: the presence of a member here grants no documented status and does not enter the
    /// documentation verdict.</remarks>
    private void InteropSurface()
    {
        var step = _report.Begin("B5.11",
            "C#-поверхность interop: ILoft, ILofts, IShell, IShells, ICoupling, IEvolution, IEvolutions",
            "Как называются члены в управляемом interop — особенно свойства с индексом?");

        var surface = new Dictionary<string, string[]>();
        foreach (var (label, type) in new (string, Type)[]
                 {
                     ("ILoft", typeof(ILoft)),
                     ("ILofts", typeof(ILofts)),
                     ("IShell", typeof(IShell)),
                     ("IShells", typeof(IShells)),
                     ("ICoupling", typeof(ICoupling)),
                     ("IEvolution", typeof(IEvolution)),
                     ("IEvolutions", typeof(IEvolutions)),
                 })
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            surface[label] = members;
            step.Data["members_" + label] = members;
            step.Observe(label + ": " + string.Join(", ", members));
        }

        var hasIndexed = surface["ILoft"].Any(n => n.Contains("BuildingType", StringComparison.Ordinal))
                         && surface["ICoupling"].Any(n => n.Contains("Position", StringComparison.Ordinal));
        if (hasIndexed)
        {
            step.Pass("поверхность interop прочитана; свойства с индексом видны как "
                + string.Join(", ", surface["ILoft"].Where(n => n.Contains("BuildingType", StringComparison.Ordinal)))
                + " / "
                + string.Join(", ", surface["ICoupling"].Where(n => n.Contains("Position", StringComparison.Ordinal))));
        }
        else
        {
            step.Fail("в interop не видны ожидаемые члены: BuildingType="
                + string.Join(",", surface["ILoft"]) + " | Position="
                + string.Join(",", surface["ICoupling"]));
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.12 ══

    /// <summary>Read-back FROM THE TREE: how the three new features appear in the document tree and whether their
    /// parameters are read back. The order requires exactly this: the <c>read</c> row must read the MODEL,
    /// not retell the creation response — ten B4 rows already failed on that, and the failure was right.</summary>
    /// <remarks>
    /// The step measures for each family what cannot be derived from the name: (1) the TYPE NUMBER of the
    /// feature in the tree and its name in <c>ksObj3dTypeEnum</c>; (2) whether the tree element answers
    /// <c>ksEntity</c> and what <c>GetDefinition()</c> returns; (3) whether the family's parameters are
    /// read BACK and equal to those written; (4) whether the feature is in the API7 collection
    /// (<c>Evolutions</c>/<c>Lofts</c>/<c>Shells</c>) — that is the route of reading from the model, not
    /// from the creation response. The classification stands IN THIS SAME step, not in a sub-step: a
    /// silent probe reads as "not checked".
    /// </remarks>
    private void TreeReadBack()
    {
        var step = _report.Begin("B5.12",
            "Обратное чтение из дерева: тип признака и параметры кинематики, сечений и оболочки",
            "Видны ли три семейства в дереве СВОИМ типом и читаются ли их параметры ИЗ МОДЕЛИ?");

        var sweepFound = SweepTreeReadBack(step);
        var loftFound = LoftTreeReadBack(step);
        var shellFound = ShellTreeReadBack(step);

        step.Data["sweep_recognised"] = sweepFound;
        step.Data["loft_recognised"] = loftFound;
        step.Data["shell_recognised"] = shellFound;
        if (sweepFound && loftFound && shellFound)
        {
            step.Pass("все три семейства видны в дереве своим типом и читаются обратно из модели");
        }
        else
        {
            step.Fail("не распознано из дерева: "
                + (sweepFound ? "" : "кинематика ")
                + (loftFound ? "" : "сечения ")
                + (shellFound ? "" : "оболочка"));
        }
    }

    /// <summary>Sweep: after <c>NewEntity(45)</c> the feature is searched FOR IN THE TREE, not taken from the
    /// creation handle. Expectation — <c>o3d_baseEvolution</c> and the definition <c>ksBaseEvolutionDefinition</c>.</summary>
    private bool SweepTreeReadBack(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P1", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathLine(part, "T1", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step)
                || !Sweep(doc, part, step, "T1", "P1", shift: 2))
            {
                step.Observe("кинематика: операция не создана — читать из дерева нечего");
                return false;
            }

            var tree = FeatureTree(part, step);
            var rows = tree.Select(e => DescribeEntity(e.Index, e.Entity)).ToArray();
            step.Data["sweep_tree"] = rows;
            step.Observe("кинематика: дерево — " + string.Join(" | ", rows));

            var found = tree.Select(e => e.Entity)
                .FirstOrDefault(e => e.GetDefinition() is ksBaseEvolutionDefinition or ksBossEvolutionDefinition);
            var definition = found?.GetDefinition();
            if (definition is null)
            {
                step.Observe("кинематика: в дереве НЕТ признака с определением кинематической операции");
                return false;
            }

            step.Data["sweep_tree_type"] = found!.type;
            step.Data["sweep_tree_type_name"] = Api5.ObjectTypeName(found.type);
            step.Data["sweep_definition_interfaces"] = InterfaceAnswers(definition);
            step.Observe("определение отвечает интерфейсам: " + InterfaceAnswers(definition));

            // The parameters are read BACK and compared with those written: 2 = orthogonal (written above).
            // The branch is chosen BY THE INTERFACE ANSWER, not by the type number: for these families the
            // tree number and the creation number do not coincide (measured here: created with
            // NewEntity(45), but 46 in the tree), and this is the same defect already caught on the hole
            // (52 → 583) and the rotation.
            var asBase = definition as ksBaseEvolutionDefinition;
            var asBoss = definition as ksBossEvolutionDefinition;
            var read = asBase is not null
                ? ReadSweepBack(
                    () => (int)asBase.sketchShiftType,
                    () => Api5.SafeObject(asBase.PathPartArray) is ksEntityCollection p ? p.GetCount() : null,
                    () => asBase.GetPathLength(1u),
                    () => asBase.GetSketch(),
                    step)
                : asBoss is not null
                    ? ReadSweepBack(
                        () => (int)asBoss.sketchShiftType,
                        () => Api5.SafeObject(asBoss.PathPartArray) is ksEntityCollection p ? p.GetCount() : null,
                        () => asBoss.GetPathLength(1u),
                        () => asBoss.GetSketch(),
                        step)
                    : false;

            var operationResult = EvolutionOperationResult(doc, step);
            step.Data["sweep_operation_result"] = operationResult;
            step.Observe("OperationResult из API7: " + (operationResult?.ToString() ?? "не прочитано"));

            step.Observe(read
                ? "кинематика распознана из дерева, параметры равны записанным"
                : "кинематика: параметры обратно не сошлись — см. значения выше");
            return read;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Three quantities of the sweep operation, read BACK from the tree definition, and compared with what
    /// was written: <c>sketchShiftType</c> = 2 (orthogonal), one trajectory part, length 100 mm.</summary>
    /// <remarks>
    /// The accessors are passed as delegates because <c>ksBaseEvolutionDefinition</c> and
    /// <c>ksBossEvolutionDefinition</c> have no common interface with these members, and which of the two
    /// answers is measured, not assumed.
    /// </remarks>
    private static bool ReadSweepBack(
        Func<int> shift,
        Func<int?> pathParts,
        Func<double> pathLength,
        Func<object?> sketch,
        ProbeStep step)
    {
        var shiftValue = Api5.SafeInt(shift);
        step.Data["sweep_shift_read"] = shiftValue;
        step.Observe("sketchShiftType прочитан обратно: " + (shiftValue?.ToString() ?? "не прочитано")
            + " (записано 2 = ортогонально)");

        var pathCount = SafeCount(pathParts);
        step.Data["sweep_path_parts_read"] = pathCount;
        step.Observe("PathPartArray() прочитан обратно: " + (pathCount?.ToString() ?? "не прочитано")
            + " частей (записана 1)");

        var length = Api5.SafeDouble(pathLength);
        step.Data["sweep_path_length_read"] = length;
        step.Observe("GetPathLength(1) прочитан обратно: " + Api5.Num(length) + " (эталон 100)");

        var sketchRead = Api5.SafeObject(sketch);
        step.Data["sweep_sketch_read"] = Api5.RuntimeName(sketchRead);
        step.Observe("GetSketch() → " + Api5.RuntimeName(sketchRead));

        return shiftValue == 2 && pathCount == 1 && length is { } l && Math.Abs(l - 100d) < 0.01;
    }

    /// <summary>Loft: the feature is created by the adapter route (<c>ILofts.Add(31)</c>), so it is read by the
    /// adapter route too — from the document COLLECTION, not from the creation handle. The tree is read in
    /// the same pass so that the type number under which the feature lands in the tree becomes visible.</summary>
    private bool LoftTreeReadBack(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S5", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S6", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Observe("сечения: эскизы не построены");
                return false;
            }

            if (Container(doc) is not { } container)
            {
                step.Observe("сечения: контейнер API7 не получен");
                return false;
            }

            if (container.Lofts is not ILofts lofts)
            {
                step.Observe("сечения: container.Lofts не привёлся к ILofts");
                return false;
            }

            var created = Api5.SafeObject(() => lofts.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (created is null)
            {
                step.Observe("сечения: ILofts.Add(31) не вернул ILoft");
                return false;
            }

            created.Sketchs = new object[] { TransferTo7(Find(part, "S5"))!, TransferTo7(Find(part, "S6"))! };
            Api5.SafeBool(created.Update);
            part.RebuildModel();
            doc.RebuildDocument();

            var tree = FeatureTree(part, step);
            var rows = tree.Select(e => DescribeEntity(e.Index, e.Entity)).ToArray();
            step.Data["loft_tree"] = rows;
            step.Observe("сечения: дерево — " + string.Join(" | ", rows));

            var loftRow = tree.FirstOrDefault(e => e.Entity.type == BossLoftType);
            if (loftRow.Entity is not null)
            {
                step.Data["loft_tree_type"] = loftRow.Entity.type;
                step.Data["loft_tree_type_name"] = Api5.ObjectTypeName(loftRow.Entity.type);
            }

            // READING FROM THE MODEL: the feature is taken from the document COLLECTION anew, not from the handle above.
            var count = Api5.SafeInt(() => lofts.Count);
            step.Data["loft_collection_count"] = count;
            step.Observe("ILofts.Count = " + (count?.ToString() ?? "не прочитано"));

            if (count is not > 0)
            {
                step.Observe("сечения: коллекция ILofts пуста — читать нечего");
                return false;
            }

            var read = IndexedObject(lofts, "Loft", 0) as ILoft;
            if (read is null)
            {
                step.Observe("сечения: ILofts.Loft(0) не вернул ILoft");
                return false;
            }

            step.Data["loft_reader_type"] = read.GetType().Name;
            var sections = Api5.SafeObject(() => read.Sketchs) as Array;
            var closed = Api5.SafeBool(() => read.Closed);
            var couplings = Api5.SafeInt(() => read.CouplingsCount);
            var buildingBegin = IndexedInt(read, "BuildingType", true);
            step.Data["loft_sections_read"] = sections?.Length;
            step.Data["loft_closed_read"] = closed;
            step.Data["loft_couplings_read"] = couplings;
            step.Data["loft_building_begin_read"] = buildingBegin;
            step.Observe("из модели: Sketchs=" + (sections?.Length.ToString() ?? "не прочитано")
                + " Closed=" + (closed?.ToString() ?? "не прочитано")
                + " CouplingsCount=" + (couplings?.ToString() ?? "не прочитано")
                + " BuildingType(true)=" + (buildingBegin?.ToString() ?? "не прочитано"));

            var ok = sections?.Length == 2 && closed == false && buildingBegin == 0;
            step.Observe(ok
                ? "сечения распознаны из коллекции документа, параметры равны записанным"
                : "сечения: параметры из модели не сошлись");
            return ok;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Shell: after <c>NewEntity(43)</c> the feature is searched for in the tree, and the parameters are
    /// read back from the definition — the same three quantities that were written (<c>thickness</c>,
    /// <c>thinType</c>, the number of faces in <c>FaceArray</c>).
    /// </summary>
    private bool ShellTreeReadBack(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBox(part, "BOX", 100d, 80d, 10d, step, out var faces))
            {
                step.Observe("оболочка: короб не построен");
                return false;
            }

            if (!Shell(doc, part, step, thickness: 2d, thinType: true, removeTopFace: true, faces: faces))
            {
                step.Observe("оболочка: операция не создана");
                return false;
            }

            var tree = FeatureTree(part, step);
            var rows = tree.Select(e => DescribeEntity(e.Index, e.Entity)).ToArray();
            step.Data["shell_tree"] = rows;
            step.Observe("оболочка: дерево — " + string.Join(" | ", rows));

            var found = tree.Select(e => e.Entity)
                .FirstOrDefault(e => e.GetDefinition() is ksShellDefinition);
            if (found?.GetDefinition() is not ksShellDefinition definition)
            {
                step.Observe("оболочка: в дереве НЕТ признака с определением ksShellDefinition");
                return false;
            }

            step.Data["shell_tree_type"] = found.type;
            step.Data["shell_tree_type_name"] = Api5.ObjectTypeName(found.type);
            step.Data["shell_definition_interfaces"] = InterfaceAnswers(definition);
            step.Observe("оболочка: определение отвечает интерфейсам: " + InterfaceAnswers(definition));

            var thickness = Api5.SafeDouble(() => definition.thickness);
            var thinType = Api5.SafeBool(() => definition.thinType);
            var faceCount = Api5.SafeObject(definition.FaceArray) is ksEntityCollection removed
                ? removed.GetCount()
                : (int?)null;
            step.Data["shell_thickness_read"] = thickness;
            step.Data["shell_thin_type_read"] = thinType;
            step.Data["shell_faces_read"] = faceCount;
            step.Observe("из модели: thickness=" + Api5.Num(thickness)
                + " thinType=" + (thinType?.ToString() ?? "не прочитано")
                + " FaceArray=" + (faceCount?.ToString() ?? "не прочитано") + " граней");

            var shellFromCollection = ShellFromCollection(doc, step);
            step.Data["shell_collection_thickness"] = shellFromCollection?.Thickness;
            step.Data["shell_collection_thin_type"] = shellFromCollection?.ThinType;
            step.Observe("IShell из коллекции: Thickness="
                + Api5.Num(shellFromCollection?.Thickness) + " ThinType="
                + (shellFromCollection?.ThinType.ToString() ?? "не прочитано")
                + " DeletedFaces=" + ShellDeletedFaceCount(shellFromCollection));

            var ok = thickness is { } t && Math.Abs(t - 2d) < 0.001
                     && thinType == true && faceCount == 1;
            step.Observe(ok
                ? "оболочка распознана из дерева, параметры равны записанным"
                : "оболочка: параметры обратно не сошлись (thickness=" + Api5.Num(thickness)
                  + ", thinType=" + (thinType?.ToString() ?? "—")
                  + ", граней=" + (faceCount?.ToString() ?? "—") + ")");
            return ok;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>The feature tree by THE SAME route the server itself uses and by which the B1–B4 acceptance was
    /// measured: <c>EntityCollection(OperationElement = 110)</c>.</summary>
    /// <remarks>
    /// The first revision of this step went through <c>SubFeatureCollection(true, false)</c> — and that was
    /// a <b>probe defect</b>, not a product fact: that route yields <c>ksFeature</c>, which is <b>NOT
    /// castable</b> to <c>ksEntity</c>, so <c>GetDefinition()</c> is not readable from it at all, and
    /// <c>ksFeature.type</c> returns 105 (<c>o3d_entity</c>) for every element in a row. The step honestly
    /// reported "the tree has NO feature with a definition" for the sweep and the shell, although the
    /// names (verbatim product feature names: «Элемент по траектории:1», «Оболочка:1») stood in the tree.
    /// The route was replaced with the measured one.
    /// </remarks>
    private static List<(int Index, ksEntity Entity)> FeatureTree(ksPart part, ProbeStep step)
    {
        var list = new List<(int, ksEntity)>();
        if (part.EntityCollection(OperationElement) is not ksEntityCollection collection)
        {
            step.Observe("EntityCollection(110) не вернул ksEntityCollection");
            return list;
        }

        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            if (collection.GetByIndex(i) is ksEntity entity)
            {
                list.Add((i, entity));
            }
        }

        step.Observe("EntityCollection(110): элементов " + list.Count + " из " + count);
        return list;
    }

    /// <summary>A counter that decides for itself what to return: <c>null</c> means "not read", not zero.</summary>
    private static int? SafeCount(Func<int?> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>Whether the definition answers the sweep-operation interface. Both interfaces are accepted.</summary>
    private static bool IsEvolutionDefinition(object? definition) =>
        definition is ksBaseEvolutionDefinition or ksBossEvolutionDefinition;

    /// <summary>Whether the definition answers the loft interface. Both are accepted.</summary>
    private static bool IsLoftDefinition(object? definition) =>
        definition is ksBaseLoftDefinition or ksBossLoftDefinition;

    /// <summary>A tree feature in a form suitable for the report: index, name, type number and definition.</summary>
    private static string DescribeEntity(int index, ksEntity entity)
    {
        return "#" + index + " «" + (entity.name ?? "без имени") + "» type=" + entity.type
               + " (" + Api5.ObjectTypeName(entity.type) + ") → "
               + InterfaceAnswers(entity.GetDefinition());
    }

    /// <summary>Which KNOWN interfaces the definition answers. Needed because <c>GetType().Name</c> of a COM object
    /// is always <c>__ComObject</c> and says nothing about the route.</summary>
    /// <remarks>The first revision of the report printed the managed type name — and showed "__ComObject" even for
    /// the shell definition, which in fact casts to <c>ksShellDefinition</c> and is readable
    /// (thickness=2, thinType=true, FaceArray=1). That is a <b>probe defect</b>, not a product fact: the
    /// RCW class name is neither a confirmation nor a refutation of the route. The check must be by the
    /// QUESTION "does the interface answer", not by the class name.</remarks>
    private static string InterfaceAnswers(object? definition)
    {
        if (definition is null)
        {
            return "нет определения";
        }

        var answers = new List<string>();
        foreach (var (label, type) in new (string, Type)[]
                 {
                     ("ksBaseEvolutionDefinition", typeof(ksBaseEvolutionDefinition)),
                     ("ksBossEvolutionDefinition", typeof(ksBossEvolutionDefinition)),
                     ("ksBaseLoftDefinition", typeof(ksBaseLoftDefinition)),
                     ("ksBossLoftDefinition", typeof(ksBossLoftDefinition)),
                     ("ksShellDefinition", typeof(ksShellDefinition)),
                 })
        {
            try
            {
                if (type.IsInstanceOfType(definition))
                {
                    answers.Add(label);
                }
            }
            catch (Exception)
            {
                // A cast failure is neither "the interface is present" nor "the interface is absent":
                // it is not counted in either direction.
            }
        }

        return answers.Count == 0 ? "ни один известный интерфейс не отвечает" : string.Join("+", answers);
    }

    /// <summary>
    /// <c>IEvolution.OperationResult</c> — the documented answer about the operation kind. It is searched
    /// for in the document COLLECTION: the API5 definition has no such member, and an unread field must
    /// remain unread rather than become zero.
    /// </summary>
    private int? EvolutionOperationResult(ksDocument3D doc, ProbeStep step)
    {
        if (Container(doc) is not { } container)
        {
            return null;
        }

        var evolutions = Api5.SafeObject(() => container.Evolutions);
        step.Observe("container.Evolutions → " + Api5.RuntimeName(evolutions));
        var count = evolutions is null ? null : Api5.SafeInt(() => IndexedCount(evolutions!));
        step.Observe("Evolutions.Count = " + (count?.ToString() ?? "не прочитано"));
        if (count is not > 0)
        {
            return null;
        }

        var first = IndexedObject(evolutions!, "Evolution", 0);
        if (first is null)
        {
            step.Observe("Evolutions.Evolution(0) не прочитан");
            return null;
        }

        var result = IndexedGet(first, "OperationResult");
        var asInt = result is null ? (int?)null : Convert.ToInt32(result);
        step.Observe("Evolution(0).OperationResult = " + (asInt?.ToString() ?? "не прочитано"));
        return asInt;
    }

    private IShell? ShellFromCollection(ksDocument3D doc, ProbeStep step)
    {
        if (Container(doc) is not { } container)
        {
            return null;
        }

        var shells = Api5.SafeObject(() => container.Shells);
        step.Observe("container.Shells → " + Api5.RuntimeName(shells));
        if (shells is null)
        {
            return null;
        }

        var count = Api5.SafeInt(() => IndexedCount(shells));
        step.Observe("Shells.Count = " + (count?.ToString() ?? "не прочитано"));
        return count is > 0 ? IndexedObject(shells, "Shell", 0) as IShell : null;
    }

    private static int? ShellDeletedFaceCount(IShell? shell)
    {
        if (shell is null)
        {
            return null;
        }

        var deleted = Api5.SafeObject(() => shell.DeletedFaces) as Array;
        return deleted?.Length;
    }

    /// <summary>The value of the <c>Count</c> property of an API7 collection, read via the interop name.</summary>
    private static int IndexedCount(object collection)
    {
        var value = collection.GetType().GetProperty("Count")?.GetValue(collection);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// An integer-indexed member via reflection over the managed interop type —
    /// <c>get_Loft(int)</c>, <c>get_Shell(int)</c>. <c>null</c> means "not read".
    /// </summary>
    private static object? IndexedObject(object target, string name, int index)
    {
        var method = target.GetType().GetMethod("get_" + name, new[] { typeof(int) });
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(target, new object[] { index });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A non-indexed property read by reflection: <c>OperationResult</c> on <c>IEvolution</c>.</summary>
    private static object? IndexedGet(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name)?.GetValue(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.13 ══

    /// <summary>EDIT IN PLACE: the same feature, a changed parameter, a rebuild — and the volume.</summary>
    /// <remarks>The order requires two things of the <c>edit</c> action: the edit changes <b>THE SAME</b> feature
    /// and yields the expected geometry, and the request carries <b>all</b> the mode's parameters —
    /// otherwise "exactly what was asked changed" is indistinguishable from "this also changed".
    /// Each setup takes TWO AND THREE differing numbers on ONE AND THE SAME feature: for the sweep on the
    /// arc <c>orthogonal → parallel → orthogonal</c> (24674.011002723353 → 15707.963267948984 →
    /// 24674.011002723353), for the shell <c>t = 2 inward → t = 4 inward → t = 4 outward</c>
    /// (21632 → 40256 → 42304). The return to the first value is not decoration: it shows that THIS VERY
    /// feature is edited rather than a new one created, and that the edit is reversible.
    /// The feature reference is taken FROM THE TREE after creation, not from the creation handle: the
    /// handle is the creation response, and the order forbids passing it off as a read.</remarks>
    private void EditInPlace()
    {
        var step = _report.Begin("B5.13",
            "Правка на месте: тот же признак, изменённый параметр, перестроение",
            "Меняет ли правка параметра ТОТ ЖЕ признак и даёт ли ожидаемую геометрию?");

        var sweepOk = SweepEditInPlace(step);
        var loftOk = LoftEditInPlace(step);
        var shellOk = ShellEditInPlace(step);

        step.Data["sweep_edited"] = sweepOk;
        step.Data["loft_edited"] = loftOk;
        step.Data["shell_edited"] = shellOk;
        if (sweepOk && loftOk && shellOk)
        {
            step.Pass("правка на месте подтверждена объёмом у всех трёх семейств; у кинематики и "
                + "оболочки — тремя различающимися числами на одном признаке");
        }
        else
        {
            step.Fail("правка на месте не подтверждена: "
                + (sweepOk ? string.Empty : "кинематика ")
                + (loftOk ? string.Empty : "сечения ")
                + (shellOk ? string.Empty : "оболочка"));
        }
    }

    /// <summary>Sweep: <c>orthogonal → parallel → orthogonal</c> on the R50/90° arc. The discriminating strength of
    /// the modes is measured here (B5.2): <c>8966.047734774369</c> mm³.</summary>
    private bool SweepEditInPlace(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P2", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathArc(part, "T2", Api5.PlaneXoy, 0d, 50d, 50d, 0d, 0d, 50d, 50d, step)
                || !Sweep(doc, part, step, "T2", "P2", shift: 2))
            {
                step.Observe("кинематика: операция не создана — править нечего");
                return false;
            }

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsEvolutionDefinition(e.GetDefinition()));
            var definition = entity?.GetDefinition();
            if (entity is null || definition is null)
            {
                step.Observe("кинематика: признак не найден в дереве — править нечего");
                return false;
            }

            var read = definition is ksBaseEvolutionDefinition baseRead
                ? (Func<int>)(() => baseRead.sketchShiftType)
                : (Func<int>)(() => ((ksBossEvolutionDefinition)definition).sketchShiftType);
            var write = definition is ksBaseEvolutionDefinition baseWrite
                ? (Action<int>)(value => baseWrite.sketchShiftType = (short)value)
                : (Action<int>)(value => ((ksBossEvolutionDefinition)definition).sketchShiftType = (short)value);

            var created = TotalVolume(part);
            var orthogonal = Pi * 100d * 50d * Pi / 2d;
            var parallel = 15707.963267948984d;

            var toParallel = EditSweepShift(doc, part, entity, write, read, 0, step);
            var backToOrthogonal = EditSweepShift(doc, part, entity, write, read, 2, step);

            step.Data["sweep_created_mm3"] = created;
            step.Data["sweep_shift_after_edit"] = toParallel.Volume;
            step.Data["sweep_shift_after_return"] = backToOrthogonal.Volume;

            var ok = Near(created, orthogonal) && Near(toParallel.Volume, parallel)
                     && Near(backToOrthogonal.Volume, orthogonal)
                     && toParallel.Shift == 0 && backToOrthogonal.Shift == 2;
            step.Observe(ok
                ? "кинематика: правка режима меняет ТОТ ЖЕ признак и обратима ("
                  + Api5.Num(created) + " → " + Api5.Num(toParallel.Volume) + " → "
                  + Api5.Num(backToOrthogonal.Volume) + ")"
                : "кинематика: правка не подтвердилась (создано " + Api5.Num(created)
                  + ", после правки " + Api5.Num(toParallel.Volume) + ", после возврата "
                  + Api5.Num(backToOrthogonal.Volume) + ")");
            return ok;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>One edit of the section-motion mode on an already found feature.</summary>
    private static (int? Shift, double? Volume) EditSweepShift(
        ksDocument3D doc, ksPart part, ksEntity entity, Action<int> write, Func<int> read, int shift,
        ProbeStep step)
    {
        write(shift);
        var updated = Api5.SafeBool(entity.Update) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var readBack = Api5.SafeInt(read);
        var volume = TotalVolume(part);
        step.Observe("правка sketchShiftType=" + shift + ": Update=" + updated + ", прочитано обратно "
            + (readBack?.ToString() ?? "не прочитано") + ", объём " + Api5.Num(volume));
        return (readBack, volume);
    }

    /// <summary>Loft: editing the <b>INPUT</b> — re-binding the sections on an already built feature.</summary>
    /// <remarks>
    /// <b>Editing <c>Closed</c> on a built feature is ACCEPTED AND NOT APPLIED, and this is measured, not
    /// assumed.</b> <c>ILoft.Closed = true</c> returns <c>Update() = True</c>, but the read-back gives
    /// <c>False</c>, and the volume stays <c>28000</c> — i.e. "accepted" here does NOT mean "applied".
    /// This is exactly the case the rule was established for: <c>Update() = true</c> and a successful
    /// HRESULT mean "accepted", not "applied". Therefore <c>closed</c> is NOT declared an editable
    /// parameter of the loft: an edit that is accepted and stays silent is not an edit.
    /// What is actually edited is the <b>input</b>: <c>ILoft.Sketchs</c> is re-bound to a different set of
    /// sections. The setup is discriminating: S5 (40×40) + S6 (20×20) give a truncated pyramid <c>28000</c>,
    /// while S5 (40×40) + S7 (40×40) give a prism <c>h/3·(A₁ + A₂ + √(A₁A₂)) = 10·(1600+1600+1600) =
    /// 48000</c>. Returning to S6 returns <c>28000</c>: THE SAME feature is edited, and the edit is
    /// reversible.
    /// </remarks>
    private bool LoftEditInPlace(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S5", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S6", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step)
                || !BuildSquareOnOffsetPlane(part, "S7", Api5.PlaneXoy, 30d, 0d, 0d, 40d, step)
                || Container(doc) is not { } container
                || container.Lofts is not ILofts lofts)
            {
                step.Observe("сечения: подготовка не состоялась");
                return false;
            }

            var created = Api5.SafeObject(() => lofts.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (created is null)
            {
                step.Observe("сечения: ILofts.Add(31) не вернул ILoft");
                return false;
            }

            created.Sketchs = new object[] { TransferTo7(Find(part, "S5"))!, TransferTo7(Find(part, "S6"))! };
            Api5.SafeBool(created.Update);
            part.RebuildModel();
            doc.RebuildDocument();

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsLoftDefinition(e.GetDefinition()));
            var ordinal = entity is null ? null : OrdinalInTree(part, step, entity, IsLoftDefinition);
            if (ordinal is not int index || index < 0 || index >= (Api5.SafeInt(() => lofts.Count) ?? 0))
            {
                step.Observe("сечения: признак не сопоставлен с элементом коллекции Lofts");
                return false;
            }

            // The edit goes by the COLLECTION element found by ordinal, not by the creation handle.
            var target = IndexedObject(lofts, "Loft", index) as ILoft;
            if (target is null)
            {
                step.Observe("сечения: Loft(" + index + ") не вернул ILoft");
                return false;
            }

            var before = TotalVolume(part);

            // A negative result that must be recorded: Closed is accepted and not applied. It is the very
            // reason why closed is not declared an editable parameter.
            var closedAttempt = EditLoftClosed(doc, part, target, true, step);
            step.Data["loft_closed_write_accepted"] = closedAttempt.Volume is not null;
            step.Data["loft_closed_read_back"] = closedAttempt.Closed;
            step.Data["loft_closed_volume_mm3"] = closedAttempt.Volume;

            var toPrism = EditLoftSections(doc, part, target, "S5", "S7", step);
            var backToPyramid = EditLoftSections(doc, part, target, "S5", "S6", step);

            step.Data["loft_created_mm3"] = before;
            step.Data["loft_sections_edited_mm3"] = toPrism.Volume;
            step.Data["loft_sections_returned_mm3"] = backToPyramid.Volume;

            var ok = Near(before, 28000d) && Near(toPrism.Volume, 48000d)
                     && Near(backToPyramid.Volume, 28000d)
                     && toPrism.Sections == 2 && backToPyramid.Sections == 2;
            step.Observe(ok
                ? "сечения: правка ВХОДА (перепривязка сечений) меняет ТОТ ЖЕ признак и обратима ("
                  + Api5.Num(before) + " → " + Api5.Num(toPrism.Volume) + " → "
                  + Api5.Num(backToPyramid.Volume) + "); правка Closed принимается и НЕ применяется"
                : "сечения: правка входа не подтвердилась (создано " + Api5.Num(before)
                  + ", после перепривязки " + Api5.Num(toPrism.Volume) + ", после возврата "
                  + Api5.Num(backToPyramid.Volume) + ")");
            return ok;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Re-binding the sections on an already built feature: the same feature, a different INPUT.</summary>
    /// <remarks>
    /// <b>Reads BETWEEN the steps are mandatory, and this is a probe fix, not decoration.</b> The first
    /// revision read <c>loft.Sketchs</c> only AFTER the rebuild, and the outcome "the write landed in the
    /// model" was indistinguishable from "the write landed only in the wrapper and was cancelled by the
    /// first update": both give a readable section count, and only a read BEFORE the update tells them
    /// apart. Therefore the section count is published three times — right after the assignment, after
    /// <c>Update()</c>, and after the rebuild.
    /// </remarks>
    private (int? Sections, double? Volume) EditLoftSections(
        ksDocument3D doc, ksPart part, ILoft loft, string first, string second, ProbeStep step)
        => EditLoftSectionsCore(doc, part, loft, new[] { first, second }, null, first + second, step);

    /// <summary>The same re-binding, but applied by <c>ksEntity.Update()</c> — the API5-entity call by which the
    /// SHELL definition write is applied (step B5.14, measured to work) and by which the product itself
    /// applies the edit. A separate setup is needed because probe B5.13 applies <c>ILoft.Update()</c>, and
    /// the divergence "the probe applies, the product does not" must be separated by the CALL, not by a
    /// guess.</summary>
    private (int? Sections, double? Volume) EditLoftSectionsViaEntity(
        ksDocument3D doc, ksPart part, ILoft loft, ksEntity entity, string first, string second, ProbeStep step)
        => EditLoftSectionsCore(doc, part, loft, new[] { first, second }, entity, "entity_" + first + second, step);

    /// <summary>Re-binding to a DIFFERENT NUMBER of sections. A separate setup because the section count is exactly
    /// the input that the product reads as 3 → 2 → 3: in probe B5.13 the number did not change (2 → 2), so
    /// "not applied" there is indistinguishable from "the number cannot be changed".</summary>
    private (int? Sections, double? Volume) EditLoftSectionCount(
        ksDocument3D doc, ksPart part, ILoft loft, string[] names, ProbeStep step)
        => EditLoftSectionsCore(doc, part, loft, names, null, "count_" + string.Join("-", names), step);

    /// <summary>The common mechanics: transfer the sections, write, read BETWEEN the steps, return count and volume.</summary>
    private (int? Sections, double? Volume) EditLoftSectionsCore(
        ksDocument3D doc, ksPart part, ILoft loft, string[] names, ksEntity? applyVia, string key, ProbeStep step)
    {
        var transferred = new object[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            if (TransferTo7(Find(part, names[i])) is not { } transferredOne)
            {
                step.Observe("перепривязка " + string.Join("+", names) + ": сечение «" + names[i]
                    + "» не перенесено в API7 — значение не дошло, а не пустой список");
                return (null, null);
            }

            transferred[i] = transferredOne;
        }

        loft.Sketchs = transferred;
        var afterWrite = (Api5.SafeObject(() => loft.Sketchs) as Array)?.Length;
        var updated = applyVia is null
            ? Api5.SafeBool(loft.Update) == true
            : Api5.SafeBool(applyVia.Update) == true;
        var afterUpdate = (Api5.SafeObject(() => loft.Sketchs) as Array)?.Length;
        part.RebuildModel();
        doc.RebuildDocument();
        var sections = (Api5.SafeObject(() => loft.Sketchs) as Array)?.Length;
        var volume = TotalVolume(part);
        step.Observe("перепривязка " + string.Join("+", names) + " через "
            + (applyVia is null ? "ILoft.Update()" : "ksEntity.Update()") + ": после присваивания "
            + (afterWrite?.ToString() ?? "не прочитано") + ", Update=" + updated + ", после Update "
            + (afterUpdate?.ToString() ?? "не прочитано") + ", после перестроения "
            + (sections?.ToString() ?? "не прочитано") + ", объём " + Api5.Num(volume));
        step.Data["sections_after_write_" + key] = afterWrite;
        step.Data["sections_after_update_" + key] = afterUpdate;
        return (sections, volume);
    }

    /// <summary>Loft: WHAT exactly is edited on an existing feature — the section REFERENCES or their NUMBER.</summary>
    /// <remarks>
    /// <b>Why a separate step.</b> Step B5.13 measures that section re-binding at the SAME number
    /// (2 → 2) is applied: <c>28000 → 48000 → 28000</c>. The product, however, edits the SET of sections,
    /// i.e. also changes the number, and its row <c>B5S.01</c> reads "before the write 3, after assigning
    /// <c>ILoft.Sketchs</c> 2, after <c>ksEntity.Update()</c> 3". Two divergences — the application CALL
    /// (<c>ksEntity.Update()</c> versus <c>ILoft.Update()</c>) and the section NUMBER — must be separated
    /// by measurement, not by argument: otherwise "the product cannot" and "the API cannot" are
    /// indistinguishable.
    /// <b>Phase 1 — a positive control on THIS SAME feature</b>: re-binding 2 → 2 via
    /// <c>ILoft.Update()</c> must give <c>48000</c>. Without it, the negative outcome of phase 2 is
    /// indistinguishable from "the probe sees no changes on this object".
    /// <b>Phase 2 — two arms.</b> Arm A: the same re-binding 2 → 2, but applied by
    /// <c>ksEntity.Update()</c> — the call by which the product applies. Arm B: re-binding to THREE
    /// sections via <c>ILoft.Update()</c> — what the product does in essence. Arm C: returning the section
    /// number back, so that "it grew" is distinguishable from "it grew and shrank".
    /// <b>The discriminating setup.</b> S5 (40×40) and S7 (40×40) give a prism <c>48000</c>, S5 + S6
    /// (20×20) — a truncated pyramid <c>28000</c>. The triple S5 + S8 (10×10 at z=15) + S7 gives a smooth
    /// surface through three sections; its magnitude is RECORDED, not derived by a formula: with three
    /// sections the feature builds not the sum of linear segments (measured at the 20.09.2026 acceptance —
    /// <c>17114.2857130106</c> versus the rejected sum <c>22000</c>).
    /// <b>Why the step may "pass" on a negative outcome.</b> The step's question is "is the edit applied at
    /// a different section number", and both answers to it are a measurement. Protection against
    /// self-deception is in two places here: the phase 1 positive control and the requirement that EVERY
    /// read be non-<c>null</c> ("not read" is not a negative outcome).
    /// </remarks>
    private void LoftInputEdit()
    {
        var step = _report.Begin("B5.20",
            "Сечения: правится ли ЧИСЛО сечений, или только ссылки при том же числе",
            "Применяется ли перепривязка на ДРУГОМ числе сечений, если перепривязка при том же числе применяется?");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S5", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S6", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step)
                || !BuildSquareOnOffsetPlane(part, "S7", Api5.PlaneXoy, 30d, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S8", Api5.PlaneXoy, 15d, 0d, 0d, 10d, step)
                || Container(doc) is not { } container
                || container.Lofts is not ILofts lofts)
            {
                step.Observe("сечения: подготовка не состоялась");
                step.Fail("сечения: подготовка не состоялась");
                return;
            }

            var created = Api5.SafeObject(() => lofts.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (created is null)
            {
                step.Observe("сечения: ILofts.Add(31) не вернул ILoft");
                step.Fail("сечения: ILofts.Add(31) не вернул ILoft");
                return;
            }

            created.Sketchs = new object[] { TransferTo7(Find(part, "S5"))!, TransferTo7(Find(part, "S6"))! };
            Api5.SafeBool(created.Update);
            part.RebuildModel();
            doc.RebuildDocument();

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsLoftDefinition(e.GetDefinition()));
            var ordinal = entity is null ? null : OrdinalInTree(part, step, entity, IsLoftDefinition);
            if (entity is null || ordinal is not int index || index < 0
                || index >= (Api5.SafeInt(() => lofts.Count) ?? 0))
            {
                step.Observe("сечения: признак не сопоставлен с элементом коллекции Lofts");
                step.Fail("сечения: признак не сопоставлен с элементом коллекции Lofts");
                return;
            }

            var target = IndexedObject(lofts, "Loft", index) as ILoft;
            if (target is null)
            {
                step.Observe("сечения: Loft(" + index + ") не вернул ILoft");
                step.Fail("сечения: Loft(" + index + ") не вернул ILoft");
                return;
            }

            var createdVolume = TotalVolume(part);

            // ── PHASE 1: a positive control on THIS SAME feature ────────────────────────────────────
            var control = EditLoftSections(doc, part, target, "S5", "S7", step);
            var controlBack = EditLoftSections(doc, part, target, "S5", "S6", step);

            // ── PHASE 2, ARM A: the same section number, but applied by ksEntity.Update() ───────────
            // The entity is re-addressed from the tree BEFORE the edit — exactly as the product does it
            // (ReAddressFromTree): holding it from the creation moment would measure the wrong call.
            var entityNow = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsLoftDefinition(e.GetDefinition())) ?? entity;
            var viaEntity = EditLoftSectionsViaEntity(doc, part, target, entityNow, "S5", "S7", step);
            var viaEntityBack = EditLoftSectionsViaEntity(doc, part, target, entityNow, "S5", "S6", step);

            // ── PHASE 2, ARMS B and C: a DIFFERENT section number, applied via ILoft.Update() ───────
            var threeSections = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);
            var backToTwo = EditLoftSectionCount(doc, part, target, new[] { "S5", "S6" }, step);

            step.Data["loft_input_created_mm3"] = createdVolume;
            step.Data["loft_input_control_mm3"] = control.Volume;
            step.Data["loft_input_control_back_mm3"] = controlBack.Volume;
            step.Data["loft_input_via_entity_mm3"] = viaEntity.Volume;
            step.Data["loft_input_via_entity_back_mm3"] = viaEntityBack.Volume;
            step.Data["loft_input_via_entity_sections"] = viaEntity.Sections;
            step.Data["loft_input_three_mm3"] = threeSections.Volume;
            step.Data["loft_input_three_sections"] = threeSections.Sections;
            step.Data["loft_input_back_to_two_mm3"] = backToTwo.Volume;
            step.Data["loft_input_back_to_two_sections"] = backToTwo.Sections;

            var controlWorks = Near(createdVolume, 28000d) && Near(control.Volume, 48000d)
                               && Near(controlBack.Volume, 28000d) && control.Sections == 2;
            if (!controlWorks)
            {
                step.Fail("положительный контроль не сработал: перепривязка при том же числе сечений "
                    + "объём не сдвинула (" + Api5.Num(createdVolume) + " → " + Api5.Num(control.Volume)
                    + " → " + Api5.Num(controlBack.Volume) + "), поэтому судить о числе сечений нельзя");
                return;
            }

            if (threeSections.Sections is null || viaEntity.Volume is null || viaEntity.Sections is null)
            {
                step.Fail("чтение не дало числа: после перепривязки на трёх — "
                    + (threeSections.Sections?.ToString() ?? "не прочитано") + ", через ksEntity.Update() — "
                    + (viaEntity.Sections?.ToString() ?? "не прочитано") + ". «Не прочитано» — не "
                    + "отрицательный исход, вопрос остался не измеренным");
                return;
            }

            var entityApplies = Near(viaEntity.Volume, 48000d) && viaEntity.Sections == 2;
            var countApplies = threeSections.Sections == 3;
            var countBackApplies = countApplies && backToTwo.Sections == 2;

            var entityPart = entityApplies
                ? "Плечо A: ksEntity.Update() ту же перепривязку ПРИМЕНЯЕТ (объём " + Api5.Num(viaEntity.Volume)
                  + ", возврат " + Api5.Num(viaEntityBack.Volume) + ") — значит расхождение с продуктом "
                  + "живёт не в вызове применения"
                : "Плечо A: ksEntity.Update() ту же перепривязку НЕ применяет (объём "
                  + Api5.Num(viaEntity.Volume) + " вместо " + Api5.Num(48000d) + "), тогда как ILoft.Update() "
                  + "на том же объекте её применил — расхождение живёт в ВЫЗОВЕ применения";
            var countPart = countApplies
                ? "Плечо B: число сечений ПРАВИТСЯ — записано три, прочитано 3, объём "
                  + Api5.Num(threeSections.Volume) + (countBackApplies
                      ? ", возврат к двум дал 2 и " + Api5.Num(backToTwo.Volume)
                      : ", а возврат к двум не подтвердился (прочитано "
                        + (backToTwo.Sections?.ToString() ?? "не прочитано") + ")")
                : "Плечо B: число сечений НЕ правится — записано три, прочитано "
                  + threeSections.Sections + ", объём остался " + Api5.Num(threeSections.Volume)
                  + "; запись принята и отменена первым же обновлением, поэтому «набор сечений» у "
                  + "существующего признака этим маршрутом невыразим";

            step.Pass("правка входа у элемента по сечениям разведена по ВЫЗОВУ и по ЧИСЛУ сечений. "
                + "Перепривязка при том же числе через ILoft.Update() применяется и обратима ("
                + Api5.Num(createdVolume) + " → " + Api5.Num(control.Volume) + " → "
                + Api5.Num(controlBack.Volume) + "). " + entityPart + ". " + countPart + ".");
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Writing the section set INTO THE API5 DEFINITION — <c>ksBaseLoftDefinition.Sketchs()</c> →
    /// <c>ksEntityCollection</c>, <c>Clear()</c> then <c>Add()</c> per sketch. It publishes how many
    /// elements there were BEFORE <c>Clear()</c> and how many there were after <c>Add()</c>: "cleared an
    /// empty one" and "cleared a non-empty one" are different facts, and the first revision lost that
    /// distinction.
    /// </summary>
    private static (bool Cleared, int Added, int? After, string Note) WriteDefinitionSections(
        ksEntity entity, IReadOnlyList<ksEntity?> sections, ProbeStep step)
    {
        try
        {
            var collection = entity.GetDefinition() switch
            {
                ksBaseLoftDefinition baseDefinition => baseDefinition.Sketchs() as ksEntityCollection,
                ksBossLoftDefinition bossDefinition => bossDefinition.Sketchs() as ksEntityCollection,
                _ => null,
            };
            if (collection is null)
            {
                return (false, 0, null, "Sketchs() определения не привёлся к ksEntityCollection");
            }

            var before = Api5.SafeInt(collection.GetCount);
            var cleared = Api5.SafeBool(() => collection.Clear()) == true;
            var added = 0;
            foreach (var section in sections)
            {
                if (section is not null && Api5.SafeBool(() => collection.Add(section)) == true)
                {
                    added++;
                }
            }

            var after = Api5.SafeInt(collection.GetCount);
            return (cleared, added, after, "до Clear() " + (before?.ToString() ?? "не прочитано")
                + ", Clear=" + cleared + ", добавлено " + added + ", стало "
                + (after?.ToString() ?? "не прочитано"));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            return (false, 0, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// The section count read THROUGH THE API5 DEFINITION — by the same member the product reads them with
    /// (<c>ksBaseLoftDefinition.Sketchs()</c> in <c>LoftSectionRefs</c>, from which both
    /// <c>kompas_get_feature</c> and the reference set for editing take them). Separately from the read via
    /// <c>ILoft</c>: these two reads may have a DIFFERENT effect on the feature state, and the difference
    /// must be measurable. <c>null</c> means "not read", not zero.
    /// </summary>
    private static int? DefinitionSectionCount(ksEntity entity)
    {
        try
        {
            var collection = entity.GetDefinition() switch
            {
                ksBaseLoftDefinition baseDefinition => baseDefinition.Sketchs(),
                ksBossLoftDefinition bossDefinition => bossDefinition.Sketchs(),
                _ => null,
            };
            return collection is ksEntityCollection entities ? entities.GetCount() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>
    /// A feature CREATED on THREE sections is reduced to TWO — does the probe reproduce what row
    /// <c>B5S.01</c> reads?
    /// </summary>
    /// <remarks>
    /// <b>Why a separate step.</b> Step B5.20 separated the input edit by the call and by the section
    /// number and showed that both <c>ksEntity.Update()</c> and changing the section NUMBER work — but in
    /// the probe the feature was created on TWO sections and the number first GREW (2 → 3 → 2). The
    /// product, however, reads "before the write 3, after assigning 2, after <c>ksEntity.Update()</c> 3"
    /// on a feature created on THREE. So the configuration "created on N, reduced to M" has not yet been
    /// measured by the probe, and carrying B5.20's conclusion over to the product is forbidden: the
    /// configurations differ.
    /// <b>What exactly differs and why it may matter.</b> In B5.20 the feature's first <c>Update()</c> was
    /// called with TWO sections; here — with THREE. If the section set written into the definition at the
    /// first build remains the owner of the section NUMBER, then the set cannot be reduced to a smaller
    /// one but can be grown: in B5.20 the first build fixed 2 and the write of 3 won; here the first build
    /// will fix 3, and the write of 2 must lose. This is a checkable guess, and it is either confirmed by a
    /// number or rejected.
    /// <b>A positive control inside the setup.</b> Arm D2 returns the ORIGINAL set of three sections: if it
    /// too fails to hold 3, then "the probe stopped seeing changes on this object", and the negative
    /// outcome of D1/D3 cannot be read as a fact.
    /// </remarks>
    private void LoftShrinkCreatedThree()
    {
        var step = _report.Begin("B5.21",
            "Сечения: признак, созданный на ТРЁХ, сводится к ДВУМ",
            "Воспроизводит ли прибор на признаке, созданном на трёх сечениях, отмену записи двух?");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S5", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S6", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step)
                || !BuildSquareOnOffsetPlane(part, "S7", Api5.PlaneXoy, 30d, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S8", Api5.PlaneXoy, 15d, 0d, 0d, 10d, step)
                || Container(doc) is not { } container
                || container.Lofts is not ILofts lofts)
            {
                step.Observe("сечения: подготовка не состоялась");
                step.Fail("сечения: подготовка не состоялась");
                return;
            }

            var created = Api5.SafeObject(() => lofts.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (created is null)
            {
                step.Observe("сечения: ILofts.Add(31) не вернул ILoft");
                step.Fail("сечения: ILofts.Add(31) не вернул ILoft");
                return;
            }

            // The feature is created on THREE sections — exactly as in the product (S5 40×40 @0,
            // S8 10×10 @15, S7 40×40 @30).
            created.Sketchs = new object[]
            {
                TransferTo7(Find(part, "S5"))!, TransferTo7(Find(part, "S8"))!, TransferTo7(Find(part, "S7"))!,
            };
            Api5.SafeBool(created.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var createdVolume = TotalVolume(part);

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsLoftDefinition(e.GetDefinition()));
            var ordinal = entity is null ? null : OrdinalInTree(part, step, entity, IsLoftDefinition);
            if (entity is null || ordinal is not int index || index < 0
                || index >= (Api5.SafeInt(() => lofts.Count) ?? 0))
            {
                step.Observe("сечения: признак не сопоставлен с элементом коллекции Lofts");
                step.Fail("сечения: признак не сопоставлен с элементом коллекции Lofts");
                return;
            }

            var target = IndexedObject(lofts, "Loft", index) as ILoft;
            if (target is null)
            {
                step.Observe("сечения: Loft(" + index + ") не вернул ILoft");
                step.Fail("сечения: Loft(" + index + ") не вернул ILoft");
                return;
            }

            step.Observe("признак создан на трёх сечениях: объём " + Api5.Num(createdVolume));

            // ── D1: reduction to two via ILoft.Update() ─────────────────────────────────────────
            var shrunk = EditLoftSections(doc, part, target, "S5", "S7", step);

            // ── D2: POSITIVE CONTROL — returning the original set of three ─────────────────────────
            var restored = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);

            // ── D3: reduction to two via ksEntity.Update() ──────────────────────────────────────
            var shrunkViaEntity = EditLoftSectionsViaEntity(doc, part, target, entity, "S5", "S7", step);

            // ── D4: the same set via a RE-TRANSFERRED container ─────────────────────────────────
            // The product edits through a container obtained by a TRANSFER AT THE MOMENT OF EDIT:
            // Api7Bridge caches it by (document, revision) and re-transfers it when the revision changes,
            // and creating a feature bumps the revision. The probe has so far edited through the SAME
            // container the feature was CREATED with. If "only its own container edits", then it is exactly
            // here that the write stops being applied — and that would be a ROUTE difference, not an API
            // property.
            var restoredAgain = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);
            var otherContainer = Container(doc);
            var otherLoft = otherContainer?.Lofts is ILofts otherLofts
                ? IndexedObject(otherLofts, "Loft", index) as ILoft
                : null;
            step.Observe("повторный перенос документа: контейнер "
                + (otherContainer is null ? "не получен" : "получен") + ", ILoft по индексу " + index
                + " " + (otherLoft is null ? "не получен" : "получен")
                + (ReferenceEquals(otherLoft, target) ? " — тот же объект, что держали" : " — ДРУГОЙ объект"));
            var viaOtherContainer = otherLoft is null
                ? ((int? Sections, double? Volume))(null, null)
                : EditLoftSectionsViaEntity(doc, part, otherLoft, entity, "S5", "S7", step);

            // ── D5: READING THE API5 DEFINITION BEFORE THE EDIT ─────────────────────────────────
            // The product reads the sections TWICE and differently: via ILoft (the edit) and via the API5
            // DEFINITION — ksBaseLoftDefinition.Sketchs() (LoftSectionRefs, i.e. kompas_get_feature and the
            // reference set for editing). The probe has so far not touched the definition at all. If
            // reading the definition materializes its own section set in it, then that very read makes the
            // definition the owner of the section NUMBER — and then "the write is cancelled" is a
            // consequence of the READ, not of the write. This is a checkable guess, and it is checked here.
            var restoredThird = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);
            var definitionCount = DefinitionSectionCount(entity);
            step.Observe("сечений через определение API5 (ksBaseLoftDefinition.Sketchs()): "
                + (definitionCount?.ToString() ?? "не прочитано"));
            var afterDefinitionRead = EditLoftSectionsViaEntity(doc, part, target, entity, "S5", "S7", step);

            // ── D6: A FIX CANDIDATE — write the set INTO BOTH STORES ────────────────────────────
            // If a single READ makes the definition the owner of the section NUMBER, then the write must
            // land in it too: then both stores say the same, and there is no "which to believe". This is
            // exactly what is checked here, not "maybe it will help".
            var restoredFourth = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);
            var definitionWrite = WriteDefinitionSections(
                entity, new[] { Find(part, "S5"), Find(part, "S7") }, step);
            step.Observe("запись набора в определение API5: " + definitionWrite.Note);
            var bothStores = EditLoftSectionsViaEntity(doc, part, target, entity, "S5", "S7", step);

            step.Data["loft_shrink_created_mm3"] = createdVolume;
            step.Data["loft_shrink_created_sections"] = 3;
            step.Data["loft_shrink_two_mm3"] = shrunk.Volume;
            step.Data["loft_shrink_two_sections"] = shrunk.Sections;
            step.Data["loft_shrink_restored_mm3"] = restored.Volume;
            step.Data["loft_shrink_restored_sections"] = restored.Sections;
            step.Data["loft_shrink_via_entity_mm3"] = shrunkViaEntity.Volume;
            step.Data["loft_shrink_via_entity_sections"] = shrunkViaEntity.Sections;
            step.Data["loft_shrink_restored_again_sections"] = restoredAgain.Sections;
            step.Data["loft_shrink_other_container_same_object"] = ReferenceEquals(otherLoft, target);
            step.Data["loft_shrink_via_other_container_mm3"] = viaOtherContainer.Volume;
            step.Data["loft_shrink_via_other_container_sections"] = viaOtherContainer.Sections;
            step.Data["loft_shrink_restored_third_sections"] = restoredThird.Sections;
            step.Data["loft_shrink_definition_count"] = definitionCount;
            step.Data["loft_shrink_after_definition_read_mm3"] = afterDefinitionRead.Volume;
            step.Data["loft_shrink_after_definition_read_sections"] = afterDefinitionRead.Sections;
            step.Data["loft_shrink_restored_fourth_sections"] = restoredFourth.Sections;
            step.Data["loft_shrink_definition_write"] = definitionWrite.Note;
            step.Data["loft_shrink_both_stores_mm3"] = bothStores.Volume;
            step.Data["loft_shrink_both_stores_sections"] = bothStores.Sections;

            if (createdVolume is null || shrunk.Sections is null || restored.Sections is null)
            {
                step.Fail("чтение не дало числа: создано " + Api5.Num(createdVolume) + ", сведено "
                    + (shrunk.Sections?.ToString() ?? "не прочитано") + ", возвращено "
                    + (restored.Sections?.ToString() ?? "не прочитано") + " — вопрос не измерен");
                return;
            }

            // Control: returning the ORIGINAL set must hold 3 and restore the original volume.
            var controlWorks = restored.Sections == 3 && Near(restored.Volume, createdVolume.Value);
            if (!controlWorks)
            {
                step.Fail("положительный контроль не сработал: возврат исходных трёх сечений дал "
                    + restored.Sections + " сечений и объём " + Api5.Num(restored.Volume)
                    + " вместо " + Api5.Num(createdVolume) + ", поэтому судить о сведении к двум нельзя");
                return;
            }

            var shrinkApplies = shrunk.Sections == 2;
            var shrinkViaEntityApplies = shrunkViaEntity.Sections == 2;
            var otherContainerApplies = viaOtherContainer.Sections == 2;
            var otherContainerPart = viaOtherContainer.Sections is null
                ? "Через ПОВТОРНО ПЕРЕНЕСЁННЫЙ контейнер: чтение не дало числа"
                : otherContainerApplies
                    ? "Через ПОВТОРНО ПЕРЕНЕСЁННЫЙ контейнер: сведение ПРИМЕНИЛОСЬ (прочитано 2, объём "
                      + Api5.Num(viaOtherContainer.Volume) + ") — контейнер не при чём"
                    : "Через ПОВТОРНО ПЕРЕНЕСЁННЫЙ контейнер: сведение НЕ применилось (прочитано "
                      + viaOtherContainer.Sections + ", объём " + Api5.Num(viaOtherContainer.Volume)
                      + " вместо " + Api5.Num(48000d) + "), тогда как через контейнер СОЗДАНИЯ оно "
                      + "применяется — значит правит только тот контейнер, которым признак создан";
            step.Pass("признак, созданный на ТРЁХ сечениях, "
                + (shrinkApplies
                    ? "СВОДИТСЯ к двум: записано два, прочитано 2, объём " + Api5.Num(shrunk.Volume)
                      + " вместо " + Api5.Num(createdVolume)
                    : "НЕ сводится к двум: записано два, прочитано " + shrunk.Sections + ", объём "
                      + Api5.Num(shrunk.Volume) + " — прежний")
                + "; возврат исходных трёх удержал 3 и объём " + Api5.Num(restored.Volume)
                + " (контроль). Через ksEntity.Update(): прочитано "
                + (shrunkViaEntity.Sections?.ToString() ?? "не прочитано") + ", объём "
                + Api5.Num(shrunkViaEntity.Volume) + (shrinkViaEntityApplies
                    ? " — тот же исход, что через ILoft.Update()"
                    : " — исход ДРУГОЙ, чем через ILoft.Update()")
                + ". " + otherContainerPart + "."
                + " После ЧТЕНИЯ определения API5 (сечений через определение: "
                + (definitionCount?.ToString() ?? "не прочитано") + ") сведение дало "
                + (afterDefinitionRead.Sections?.ToString() ?? "не прочитано") + " сечений и объём "
                + Api5.Num(afterDefinitionRead.Volume)                 + (afterDefinitionRead.Sections == 2
                    ? " — чтение определения правке не мешает"
                    : " — то есть ЧТЕНИЕ определения и есть то, после чего запись перестаёт "
                      + "применяться")
                + ". Кандидат в исправление — запись набора В ОБА ХРАНИЛИЩА: определение ["
                + definitionWrite.Note + "], затем ILoft + Update() — дал "
                + (bothStores.Sections?.ToString() ?? "не прочитано") + " сечений и объём "
                + Api5.Num(bothStores.Volume) + (bothStores.Sections == 2
                    ? " — запись в определение снимает отмену"
                    : " — и запись в определение отмену НЕ снимает"));
        }
        finally
        {
            TryClose(doc);
        }
    }

    private static (bool? Closed, double? Volume) EditLoftClosed(
        ksDocument3D doc, ksPart part, ILoft loft, bool value, ProbeStep step)
    {
        var updated = Api5.SafeBool(() =>
        {
            loft.Closed = value;
            return loft.Update();
        }) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var readBack = Api5.SafeBool(() => loft.Closed);
        var volume = TotalVolume(part);
        step.Observe("правка Closed=" + value + ": Update=" + updated + ", прочитано обратно "
            + (readBack?.ToString() ?? "не прочитано") + ", объём " + Api5.Num(volume));
        return (readBack, volume);
    }

    /// <summary>Shell: <c>t = 2 inward → t = 4 inward → t = 4 outward → t = 2 inward</c> on one feature.</summary>
    /// <remarks>
    /// Three differing numbers: <c>21632</c>, <c>40256</c> (the difference from t = 2 equals <c>18624</c>)
    /// and <c>53056</c>. The return to <c>21632</c> shows that THIS VERY feature is edited, not a new one
    /// created.
    /// <b>The expectation for "t = 4 outward" was wrong, and it is fixed here rather than fitted to the
    /// measurement.</b> The first revision expected <c>42304 = 104·84·14 − 80000</c> — and got
    /// <c>53056.000000000015</c>. Checking the expectation by the same model that gave the correct
    /// <c>24832</c> at t = 2: outward, material is added along ALL three axes, so the outer box at t = 4 is
    /// <c>(100+2·4)×(80+2·4)×(10+4) = 108·88·14 = 133056</c>, and the cavity is the original box
    /// <c>80000</c>, i.e. <c>53056</c>. The number <c>42304</c> came from <c>104·84·14</c>, where the
    /// thickness was added to the outer dimensions as <c>t = 2</c> — the error was in the EXPECTATION, not
    /// in the measurement, and the measurement confirmed it.
    /// </remarks>
    private bool ShellEditInPlace(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBox(part, "BOX", 100d, 80d, 10d, step, out var faces)
                || !Shell(doc, part, step, thickness: 2d, thinType: true, removeTopFace: true, faces: faces))
            {
                step.Observe("оболочка: операция не создана — править нечего");
                return false;
            }

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => e.GetDefinition() is ksShellDefinition);
            if (entity?.GetDefinition() is not ksShellDefinition definition)
            {
                step.Observe("оболочка: признак с определением ksShellDefinition в дереве не найден");
                return false;
            }

            var created = TotalVolume(part);
            var t4In = EditShell(doc, part, entity, definition, 4d, true, step);
            var t4Out = EditShell(doc, part, entity, definition, 4d, false, step);
            var backToT2 = EditShell(doc, part, entity, definition, 2d, true, step);

            step.Data["shell_created_mm3"] = created;
            step.Data["shell_t4_inward_mm3"] = t4In.Volume;
            step.Data["shell_t4_outward_mm3"] = t4Out.Volume;
            step.Data["shell_back_to_t2_mm3"] = backToT2.Volume;

            var ok = Near(created, 21632d) && Near(t4In.Volume, 40256d) && Near(t4Out.Volume, 53056d)
                     && Near(backToT2.Volume, 21632d)
                     && t4In.Thickness == 4d && t4In.ThinType == true
                     && t4Out.ThinType == false && backToT2.Thickness == 2d;
            step.Observe(ok
                ? "оболочка: правка толщины и направления меняет ТОТ ЖЕ признак и обратима ("
                  + Api5.Num(created) + " → " + Api5.Num(t4In.Volume) + " → "
                  + Api5.Num(t4Out.Volume) + " → " + Api5.Num(backToT2.Volume) + ")"
                : "оболочка: правка не подтвердилась (создано " + Api5.Num(created)
                  + ", t=4 внутрь " + Api5.Num(t4In.Volume) + ", t=4 наружу " + Api5.Num(t4Out.Volume)
                  + ", обратно " + Api5.Num(backToT2.Volume) + ")");
            return ok;
        }
        finally
        {
            TryClose(doc);
        }
    }

    private static (double? Thickness, bool? ThinType, double? Volume) EditShell(
        ksDocument3D doc,
        ksPart part,
        ksEntity entity,
        ksShellDefinition definition,
        double thickness,
        bool thinType,
        ProbeStep step)
    {
        // BOTH mode parameters are written, not only the one being changed: the request must carry the
        // whole mode, otherwise "exactly what was asked changed" is indistinguishable from "this also
        // changed".
        definition.thickness = thickness;
        definition.thinType = thinType;
        var updated = Api5.SafeBool(entity.Update) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var readThickness = Api5.SafeDouble(() => definition.thickness);
        var readThinType = Api5.SafeBool(() => definition.thinType);
        var volume = TotalVolume(part);
        step.Observe("правка толщина=" + Api5.Num(thickness) + " направление=" + thinType
            + ": Update=" + updated + ", прочитано обратно " + Api5.Num(readThickness) + " / "
            + (readThinType?.ToString() ?? "не прочитано") + ", объём " + Api5.Num(volume));
        return (readThickness, readThinType, volume);
    }

    /// <summary>Shell: whether the SET of removed faces is editable on an already built feature.</summary>
    /// <remarks>
    /// <b>Why a separate step.</b> The mandatory row <c>SM-13.shell.mode_remove_faces</c> requires the
    /// <c>edit</c> action, while editing the thickness and direction (step B5.13) does not touch the face
    /// set: there <c>thickness</c> and <c>thinType</c> were changed and the set stayed the same. The
    /// <c>Clear() + Add()</c> route over <c>ksEntityCollection</c> measuredly did NOT work for the FILLET
    /// (row <c>FL04r</c>: the set collapsed, the volume returned to the plate), so carrying it over to the
    /// shell by analogy is forbidden — it is checked here.
    /// <b>The setup is discriminating, and its INPUT is what discriminates.</b> A 100×80×10 box, shell
    /// <c>t = 2</c> inward, top face removed → <c>21632</c>. Adding a SECOND face (the bottom, 100×80) to
    /// the set makes the cavity through: <c>(100·80 − 96·76)·10 = 7040</c>. The difference of <c>14 592</c>
    /// mm³ is not tolerance noise, so "the set was applied" is distinguishable from "the set was ignored".
    /// <b>The negative control stands here too and is mandatory.</b> Re-writing THE SAME set must leave
    /// <c>21632</c>: if the repeat write moves the volume, the probe does not tell "applied" from "rebuilt
    /// from scratch", and both previous magnitudes prove nothing.
    /// </remarks>
    private void ShellFaceSetEdit()
    {
        var step = _report.Begin("B5.14",
            "Оболочка: правка НАБОРА удаляемых граней на существующем признаке",
            "Меняет ли правка набора граней геометрию того же признака, обратима ли она и видит ли прибор повторную запись того же набора?");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildBox(part, "BOX", 100d, 80d, 10d, step, out var boxFaces)
                || !Shell(doc, part, step, thickness: 2d, thinType: true, removeTopFace: true, faces: boxFaces))
            {
                step.Observe("оболочка: операция не создана — править нечего");
                step.Fail("оболочка: операция не создана");
                return;
            }

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => e.GetDefinition() is ksShellDefinition);
            if (entity?.GetDefinition() is not ksShellDefinition definition)
            {
                step.Observe("оболочка: признак с определением ksShellDefinition в дереве не найден");
                step.Fail("оболочка: признак в дереве не найден");
                return;
            }

            var top = TopFace(boxFaces);
            var afterShell = ReadFaces(part);
            var bottom = afterShell.Where(f => f.Area is not null)
                .OrderByDescending(f => f.Area).FirstOrDefault();

            // The face is chosen by its MEASURED area, not by its index in the collection: indices are not
            // stable between runs, whereas area is a property of the body.
            step.Data["faces_after_shell"] = afterShell.Count;
            step.Data["face_areas_after_shell_mm2"] = afterShell
                .Where(f => f.Area is not null)
                .Select(f => Math.Round(f.Area!.Value, 4))
                .ToArray();
            step.Observe("граней после оболочки " + afterShell.Count + "; площади "
                + string.Join("/", afterShell.Where(f => f.Area is not null)
                    .Select(f => Api5.Num(f.Area!.Value))) + "; снятая верхняя площадь "
                + Api5.Num(top?.Area) + "; добавляемая нижняя площадь " + Api5.Num(bottom?.Area));

            if (top is null || bottom is null)
            {
                step.Observe("грани для постановки не найдены");
                step.Fail("грани для постановки не найдены");
                return;
            }

            var created = TotalVolume(part);
            var createdFaces = afterShell.Count;

            // Edit A — ADDING a second face to the already removed one (the Add() route in FaceArray()).
            var added = EditShellFaceSet(doc, part, entity, definition, new[] { bottom }, clear: false, step);
            // Edit B — the set is assembled ANEW: Clear() + Add(top). Return to 21632.
            var rebuilt = EditShellFaceSet(doc, part, entity, definition, new[] { top }, clear: true, step);
            // Negative control — the same write once more: the volume must stay unchanged.
            var control = EditShellFaceSet(doc, part, entity, definition, new[] { top }, clear: true, step);

            step.Data["shell_faceset_created_mm3"] = created;
            step.Data["shell_faceset_created_faces"] = createdFaces;
            step.Data["shell_faceset_added_mm3"] = added.Volume;
            step.Data["shell_faceset_added_faces"] = added.Faces;
            step.Data["shell_faceset_rebuilt_mm3"] = rebuilt.Volume;
            step.Data["shell_faceset_control_mm3"] = control.Volume;

            var ok = Near(created, 21632d) && Near(added.Volume, 7040d)
                     && Near(rebuilt.Volume, 21632d) && Near(control.Volume, 21632d);
            if (ok)
            {
                step.Pass("набор удаляемых граней правится на ТОМ ЖЕ признаке и обратим ("
                    + Api5.Num(created) + " → " + Api5.Num(added.Volume) + " → "
                    + Api5.Num(rebuilt.Volume) + "); повторная запись того же набора объём не двигает");
            }
            else
            {
                step.Fail("правка набора граней не подтвердилась (создано " + Api5.Num(created)
                    + ", после добавления грани " + Api5.Num(added.Volume) + ", после пересборки набора "
                    + Api5.Num(rebuilt.Volume) + ", контроль " + Api5.Num(control.Volume) + ")");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// One edit of the removed-face set: <c>Clear()</c> (if asked), then <c>Add()</c> per face, then
    /// <c>Update()</c> and a rebuild. <b>Separate facts</b> are returned — the face count in the body and
    /// the volume — because with a non-working route <c>Update()</c> still answers <c>true</c> (measured on
    /// the fillet, <c>FL04r</c>).
    /// </summary>
    private static (int? Faces, double? Volume) EditShellFaceSet(
        ksDocument3D doc,
        ksPart part,
        ksEntity entity,
        ksShellDefinition definition,
        IReadOnlyList<FaceRow> faces,
        bool clear,
        ProbeStep step)
    {
        var holder = Api5.SafeObject(definition.FaceArray);
        var cleared = false;
        var added = 0;
        if (holder is ksEntityCollection collection)
        {
            if (clear)
            {
                cleared = Api5.SafeBool(() => collection.Clear()) == true;
            }

            foreach (var face in faces)
            {
                if (face.Element is not null && Api5.SafeBool(() => collection.Add(face.Element!)) == true)
                {
                    added++;
                }
            }
        }
        else
        {
            step.Observe("FaceArray() не привёлся к ksEntityCollection — писать некуда");
        }

        var updated = Api5.SafeBool(entity.Update) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var count = ReadFaces(part).Count;
        var volume = TotalVolume(part);
        step.Observe((clear ? "пересборка набора" : "добавление грани") + ": Clear=" + cleared
            + ", Add=" + added + " из " + faces.Count + ", Update=" + updated
            + ", граней в теле " + count + ", объём " + Api5.Num(volume));
        return (count, volume);
    }

    // ══════════════════════════════════════════════════════════════ B5.17 ══

    /// <summary>Section correspondence chains — what the mandatory row <c>SM-05.base.mode_couplings</c> requires
    /// the API7 route for.</summary>
    /// <remarks>
    /// <b>The documentary basis (SDK help v24, checked over the wire 20.09.2026).</b>
    /// <c>iloft_addcoupling.html</c> — <c>LPDISPATCH AddCoupling()</c>, «Возвращаемое значение —
    /// указатель на интерфейс <c>ICoupling</c>»; <c>icoupling_count.html</c> — <c>Count</c>
    /// (<c>long</c>, read-only) «Количество сечений в цепочке»; <c>icoupling_position.html</c> —
    /// <c>Position</c> (<c>double</c>) «Величина смещения точки вдоль контура сечения в %», input
    /// parameter «<c>long Index</c> — индекс сечения в цепочке»; <c>icoupling_positionoffset.html</c> —
    /// the same in mm; <c>icoupling_setpoint.html</c> / <c>icoupling_getpoint.html</c> —
    /// <c>SetPoint(Index, X, Y, Z)</c> / <c>GetPoint(Index, &amp;X, &amp;Y, &amp;Z)</c> in mm;
    /// <c>iloft_deletecoupling.html</c> — <c>DeleteCoupling(Index)</c>; <c>iloft_clearcouplings.html</c> —
    /// <c>ClearCouplings()</c>; <c>iloft_coupling.html</c> — <c>Coupling(Index)</c> (read-only).
    /// <b>Why "the chain was created" is not enough.</b> The mandatory row speaks of a <b>DEFINITE
    /// correspondence</b>, not of the existence of an object. So the discriminating strength is measured:
    /// shifting the point on the second section along the contour (0 % → 25 %, i.e. to the next corner of
    /// the square) must change the body if the chain's content is really applied. The reverse (25 % → 0 %)
    /// must return the previous volume — a zero that was never shifted does not discriminate (§11).
    /// <b>The three ways to set the point are measured separately</b> — <c>Position</c> (%), <c>PositionOffset</c>
    /// (mm) and <c>SetPoint</c> (coordinates, mm): each has its own number, and "express a correspondence" is
    /// not considered proven until the way that will enter the contract is measured.
    /// <b>The <c>Position</c> / <c>PositionOffset</c> pair is read FROM ONE MOMENT</b> (§8): they are two
    /// projections of one quantity, and the divergence between them is measured on one state.
    /// </remarks>
    private void CouplingChains()
    {
        var step = _report.Begin("B5.17",
            "Цепочки соответствия сечений: AddCoupling/ICoupling, чтение из модели, различающая сила",
            "Чем выражается «определённое соответствие сечений» (SM-05.base.mode_couplings) и меняет ли содержимое цепочки тело?");
        step.Data["expected_mm3"] = 28000d;

        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S7", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S8", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Observe("сечения не построены — шаг не состоялся");
                return;
            }

            if (Container(doc) is not { } container)
            {
                step.Fail("контейнер API7 не получен");
                return;
            }

            if (Api5.SafeObject(() => container.Lofts) is not ILofts collection)
            {
                step.Fail("container.Lofts не привёлся к ILofts");
                return;
            }

            var loft = Api5.SafeObject(() => collection.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (loft is null)
            {
                step.Fail("ILofts.Add(31) не вернул ILoft");
                return;
            }

            var first = TransferTo7(Find(part, "S7"));
            var second = TransferTo7(Find(part, "S8"));
            loft.Sketchs = new object[] { first!, second! };
            loft.Closed = false;

            var beforeAdd = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["couplings_before_add"] = beforeAdd;
            step.Observe("до AddCoupling: CouplingsCount = " + Text(beforeAdd));

            var (_, baseVolume) = ApplyChain(doc, part, loft, "эталон без цепочек", step);
            step.Data["base_volume_mm3"] = baseVolume;

            // ── (1) AddCoupling → ICoupling. Count = «количество сечений в цепочке». ──
            var chain = Api5.SafeObject(loft.AddCoupling) as ICoupling;
            step.Data["chain_type"] = Api5.RuntimeName(chain);
            if (chain is null)
            {
                step.Fail("AddCoupling() не вернул ICoupling — цепочку выразить нечем");
                return;
            }

            var chainSections = Api5.SafeInt(() => chain.Count);
            step.Data["chain_count"] = chainSections;
            step.Observe("ICoupling.Count (сечений в цепочке) = " + Text(chainSections)
                + "; сечений у признака 2");

            var afterAdd = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["couplings_after_add"] = afterAdd;
            step.Observe("после AddCoupling: CouplingsCount = " + Text(afterAdd));

            for (var i = 0; i < 2; i++)
            {
                var index = i;
                var section = Api5.SafeObject(() => chain.Sketch[index]);
                step.Data["chain_sketch_" + index] = Api5.RuntimeName(section);
                step.Observe("ICoupling.Sketch(" + index + ") → " + Api5.RuntimeName(section));
            }

            // ── (2) Correspondence by contour-length fraction: 0 % on both sections. ──
            var wrote = TryWrite(() =>
            {
                chain.Position[0] = 0d;
                chain.Position[1] = 0d;
            });
            step.Data["position_written"] = wrote;
            var percentPair = ReadPercentPair(chain);
            step.Data["position_after_write"] = percentPair;
            step.Observe("Position: записано 0 / 0, прочитано " + percentPair
                + "; PositionOffset в тот же момент " + ReadOffsetPair(chain));

            var point0 = CouplingPointText(chain, 0);
            var point1 = CouplingPointText(chain, 1);
            step.Data["point_0"] = point0;
            step.Data["point_1"] = point1;
            step.Observe("GetPoint(0) = " + point0 + "; GetPoint(1) = " + point1);

            var (_, alignedVolume) = ApplyChain(doc, part, loft, "цепочка 0 % / 0 %", step);
            step.Data["aligned_volume_mm3"] = alignedVolume;

            // The GetPoint PRECONDITION is measured separately: on a just-added chain (before the first
            // Update) GetPoint answered with a failure. Here the same call is repeated AFTER the Update,
            // and the difference reads as a precondition, not as «GetPoint does not work».
            step.Data["point_0_after_update"] = CouplingPointText(chain, 0);
            step.Data["point_1_after_update"] = CouplingPointText(chain, 1);
            step.Observe("после Update: GetPoint(0) = " + CouplingPointText(chain, 0)
                + ", GetPoint(1) = " + CouplingPointText(chain, 1)
                + " — тот же вызов ДО Update отвечал отказом");

            // ── (3) Discriminating control: the second section's point is shifted along the contour. ──
            TryWrite(() => chain.Position[1] = 25d);
            var (_, twistedVolume) = ApplyChain(doc, part, loft, "цепочка 0 % / 25 %", step);
            step.Data["twisted_volume_mm3"] = twistedVolume;
            step.Data["twist_difference_mm3"] = Difference(alignedVolume, twistedVolume);
            step.Observe("сдвиг точки на 25 % контура: объём " + Api5.Num(alignedVolume) + " → "
                + Api5.Num(twistedVolume) + ", разность " + Api5.Num(Difference(alignedVolume, twistedVolume)));

            // Reverse: returning the point must bring back the previous body.
            TryWrite(() => chain.Position[1] = 0d);
            var (_, returnedVolume) = ApplyChain(doc, part, loft, "возврат точки на 0 %", step);
            step.Data["returned_volume_mm3"] = returnedVolume;

            // ── (4) Second way to set the point: PositionOffset in mm. ──
            TryWrite(() => chain.PositionOffset[1] = 5d);
            var (_, offsetVolume) = ApplyChain(doc, part, loft, "PositionOffset = 5 мм на втором сечении", step);
            step.Data["offset_volume_mm3"] = offsetVolume;
            step.Data["position_after_offset"] = ReadPercentPair(chain);
            step.Data["offset_after_offset"] = ReadOffsetPair(chain);
            step.Observe("PositionOffset: записано 5 мм, прочитано " + ReadOffsetPair(chain)
                + "; Position в тот же момент " + ReadPercentPair(chain));
            step.Observe("GetPoint(1) при PositionOffset = 5 мм: " + CouplingPointText(chain, 1));

            // ── (5) Third way: SetPoint with coordinates in mm. ──
            var setPoint = Api5.SafeBool(() => chain.SetPoint(1, 10d, 10d, 30d));
            var (_, pointVolume) = ApplyChain(doc, part, loft, "SetPoint(1; 10; 10; 30)", step);
            step.Data["setpoint_accepted"] = setPoint;
            step.Data["setpoint_volume_mm3"] = pointVolume;
            step.Data["point_after_setpoint"] = CouplingPointText(chain, 1);
            step.Data["position_after_setpoint"] = ReadPercentPair(chain);
            step.Data["offset_after_setpoint"] = ReadOffsetPair(chain);
            step.Observe("SetPoint вернул " + setPoint + "; GetPoint(1) = " + CouplingPointText(chain, 1)
                + "; Position " + ReadPercentPair(chain) + "; PositionOffset " + ReadOffsetPair(chain));

            // The SetPoint/GetPoint coordinate frame is MEASURED as a NUMBER, not derived from a name:
            // square S8 is drawn in LOCAL coordinates 0…20 (perimeter 80 mm). Point (20; 5) is 5 mm along
            // the contour, and it reads as 6.25 % (5/80) with PositionOffset = 5; the square's centre
            // (10; 10), fed to SetPoint, projected onto the contour at (20; 10) — 10 mm = 12.5 %. The
            // third coordinate came out as 30 — the plane offset, not the local zero.
            step.Data["frame_evidence"] = "локальные 0…20, периметр 80 мм: 5 мм ↔ 6.25 %, 10 мм ↔ 12.5 %";
            step.Observe("рамка координат: локальные координаты эскиза сечения (периметр 80 мм — "
                + "5 мм читается как 6.25 %, 10 мм как 12.5 %), поданная точка проецируется на контур, "
                + "третья координата равна смещению плоскости (30), а не локальному нулю");

            // The 0 %/0 % correspondence reproduces the AUTOMATIC one: the volume matched the no-chain baseline.
            step.Observe("цепочка 0 % / 0 % дала " + Api5.Num(alignedVolume) + " — тот же объём, что без "
                + "цепочки (" + Api5.Num(baseVolume) + "): явное соответствие ЗАМЕЩАЕТ автоматическое, "
                + "а не добавляется к нему");

            // ── (6) Lifecycle: read by index, Delete, DeleteCoupling, ClearCouplings. ──
            var byIndex = Api5.SafeObject(() => loft.Coupling[0]) as ICoupling;
            step.Data["coupling_by_index_type"] = Api5.RuntimeName(byIndex);
            step.Data["coupling_by_index_count"] = byIndex is null ? null : Api5.SafeInt(() => byIndex.Count);
            step.Observe("ILoft.Coupling(0) → " + Api5.RuntimeName(byIndex) + ", Count = "
                + Text(step.Data["coupling_by_index_count"] as int?));

            // ICoupling.Delete() is called FIRST, while the chain is still in the list. In the first
            // revision of this step it stood AFTER DeleteCoupling(0), that is, it deleted an already
            // deleted object, and the failure (False) read as a property of the product. This is a
            // harness defect of class 4/tan: the harness described its own state, not the object. Fixed
            // by order, not by interpretation.
            var chainDeleted = Api5.SafeBool(chain.Delete);
            var afterChainDelete = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["chain_delete_accepted"] = chainDeleted;
            step.Data["couplings_after_chain_delete"] = afterChainDelete;
            step.Observe("ICoupling.Delete()=" + chainDeleted + " (цепочка ещё в списке) → CouplingsCount = "
                + Text(afterChainDelete));

            var secondChain = Api5.SafeObject(loft.AddCoupling) as ICoupling;
            var thirdChain = Api5.SafeObject(loft.AddCoupling) as ICoupling;
            step.Data["second_chain_type"] = Api5.RuntimeName(secondChain);
            step.Data["third_chain_type"] = Api5.RuntimeName(thirdChain);
            var twoChains = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["couplings_two"] = twoChains;
            step.Observe("после двух AddCoupling: CouplingsCount = " + Text(twoChains));

            var deletedByIndex = Api5.SafeBool(() => loft.DeleteCoupling(0));
            var afterDeleteByIndex = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["deletecoupling_accepted"] = deletedByIndex;
            step.Data["couplings_after_deletecoupling"] = afterDeleteByIndex;
            step.Observe("DeleteCoupling(0)=" + deletedByIndex + " → CouplingsCount = " + Text(afterDeleteByIndex));

            var cleared = Api5.SafeBool(loft.ClearCouplings);
            var afterClear = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["clearcouplings_accepted"] = cleared;
            step.Data["couplings_after_clear"] = afterClear;
            step.Observe("ClearCouplings()=" + cleared + " → CouplingsCount = " + Text(afterClear));

            var (_, finalVolume) = ApplyChain(doc, part, loft, "после снятия цепочек", step);
            step.Data["final_volume_mm3"] = finalVolume;

            // ── Verdict: the chain is created, read and DISCRIMINATES. ──
            var differs = alignedVolume is { } av && twistedVolume is { } tv && Math.Abs(tv - av) > 0.01;
            var returns = alignedVolume is { } rv0 && returnedVolume is { } rv1 && Math.Abs(rv1 - rv0) <= 0.01;
            var empties = afterChainDelete == 0 && afterDeleteByIndex == 1 && afterClear == 0;

            if (chainSections == 2 && differs && returns && empties)
            {
                step.Pass("цепочка соответствия создаётся (ICoupling), число сечений в ней читается из "
                    + "модели, содержимое РАЗЛИЧАЕТ: сдвиг точки на 25 % контура дал "
                    + Api5.Num(alignedVolume) + " → " + Api5.Num(twistedVolume) + " (разность "
                    + Api5.Num(Difference(alignedVolume, twistedVolume)) + "), возврат точки вернул "
                    + Api5.Num(returnedVolume) + "; Delete/DeleteCoupling/ClearCouplings опустошают список");
            }
            else
            {
                step.Fail("цепочки: сечений в цепочке " + Text(chainSections) + ", различает " + differs
                    + ", возврат " + returns + ", опустошение " + empties
                    + " (объёмы " + Api5.Num(alignedVolume) + " / " + Api5.Num(twistedVolume)
                    + " / " + Api5.Num(returnedVolume) + "; после Delete "
                    + Text(afterChainDelete) + ", после DeleteCoupling " + Text(afterDeleteByIndex)
                    + ", после Clear " + Text(afterClear) + ")");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.18 ══

    /// <summary>Chains set BEFORE the first <c>Update()</c> — the order an adapter may use.</summary>
    /// <remarks>
    /// <b>Why a separate step.</b> Step B5.17 measured a chain added AFTER the first <c>Update()</c>.
    /// A different order matters to the adapter: the API7 factory is documented as «задать
    /// параметры операции и вызвать <c>IModelObject::Update</c>» (<c>ilofts_add.html</c>), that is, the
    /// chain must be set BEFORE building. The order is not derived from convenience — it is measured:
    /// if a chain before the first <c>Update()</c> is not accepted, the adapter must build twice, and
    /// that is stated plainly.
    /// <b>The discriminating setup is the same as in B5.17</b> — shifting the second section's point by
    /// 25 % of the contour. The value MEASURED by B5.17: <c>28000 → 20000</c>. A match here means the
    /// order does not change the result; a divergence means it does, and then the divergence is
    /// published.
    /// </remarks>
    private void CouplingBeforeFirstUpdate()
    {
        var step = _report.Begin("B5.18",
            "Цепочка до первого Update(): тот же результат, что после него?",
            "Можно ли задать соответствие в одном построении (Sketchs + цепочка → Update), как документирует фабрика API7?");
        step.Data["expected_twisted_mm3"] = 20000d;

        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S9", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S10", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Observe("сечения не построены — шаг не состоялся");
                return;
            }

            if (Container(doc) is not { } container)
            {
                step.Fail("контейнер API7 не получен");
                return;
            }

            if (Api5.SafeObject(() => container.Lofts) is not ILofts collection)
            {
                step.Fail("container.Lofts не привёлся к ILofts");
                return;
            }

            var loft = Api5.SafeObject(() => collection.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (loft is null)
            {
                step.Fail("ILofts.Add(31) не вернул ILoft");
                return;
            }

            loft.Sketchs = new object[] { TransferTo7(Find(part, "S9"))!, TransferTo7(Find(part, "S10"))! };
            loft.Closed = false;

            // The chain is set BEFORE the first build.
            var chain = Api5.SafeObject(loft.AddCoupling) as ICoupling;
            step.Data["chain_type"] = Api5.RuntimeName(chain);
            var countBeforeUpdate = chain is null ? null : Api5.SafeInt(() => chain.Count);
            step.Data["chain_count_before_update"] = countBeforeUpdate;
            step.Observe("AddCoupling ДО первого Update: " + Api5.RuntimeName(chain)
                + ", Count = " + Text(countBeforeUpdate));

            var written = chain is not null && TryWrite(() =>
            {
                chain.PositionOffset[0] = 0d;
                chain.PositionOffset[1] = 20d;
            }) == true;
            step.Data["offsets_written"] = written;
            var offsets = chain is null ? "нет цепочки" : ReadOffsetPair(chain);
            step.Data["offsets_read_back"] = offsets;
            step.Observe("PositionOffset: записано 0 / 20 мм, прочитано " + offsets);

            var updated = Api5.SafeBool(loft.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volume = TotalVolume(part);
            step.Data["updated"] = updated;
            step.Data["volume_mm3"] = volume;
            var couplings = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["couplings_count"] = couplings;
            step.Observe("Update()=" + updated + ", CouplingsCount = " + Text(couplings)
                + ", объём " + Api5.Num(volume));

            // 20 mm = 25 % of the 80 mm contour — the same discriminating setup as in B5.17 (20000).
            var changed = volume is { } v && Math.Abs(v - 28000d) > 0.01;
            if (chain is not null && couplings == 1 && changed)
            {
                step.Pass("цепочка задаётся ДО первого Update() и применяется в одном построении: "
                    + "CouplingsCount = 1, объём " + Api5.Num(volume) + " вместо 28000 (смещение 20 мм "
                    + "= 25 % контура 80 мм) — порядок «параметры, затем Update» работает");
            }
            else
            {
                step.Fail("цепочка до первого Update(): CouplingsCount = " + Text(couplings)
                    + ", объём " + Api5.Num(volume) + " (эталон 28000, ожидание смещения 20000)");
            }
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ B5.19 ══

    /// <summary>Do correspondence chains survive a repeated assignment of <c>ILoft.Sketchs</c>?</summary>
    /// <remarks>
    /// <b>Why this step.</b> The first run of the first-queue tool (20.09.2026,
    /// <c>scratch/b5-first-contact.py</c>) produced something unexpected: editing <c>section_refs</c> on a
    /// feature that CARRIES a chain passed without a failure, while the chain checks were absent from the
    /// response. The failure had been written on the assumption «the chain survives section replacement»;
    /// the measurement did not confirm it. There are two possible explanations, and they are different:
    /// either assigning <c>Sketchs</c> drops the chains, or <c>CouplingsCount</c> was not read at that
    /// moment (the failure's condition is «read and greater than zero», and an unreadable value does not
    /// trip it). Only reading can distinguish them — which is what this step is busy with.
    /// <b>What is NOT checked here.</b> It is not checked whether the kernel behaves «correctly»: a FACT
    /// is measured, and it is published as is. If the chains are dropped, this means that a silent
    /// «leave as it was» after a section change is inexpressible in principle, not merely by our rule.
    /// </remarks>
    private void CouplingsAcrossSectionWrite()
    {
        var step = _report.Begin("B5.19",
            "Переживают ли цепочки повторное присваивание ILoft.Sketchs?",
            "Сбрасывает ли замена набора сечений цепочки соответствия, или они остаются на признаке?");
        step.Data["expected_twisted_mm3"] = 20000d;

        var doc = NewPart(out var part);
        try
        {
            if (!BuildSquare(part, "S11", Api5.PlaneXoy, 0d, 0d, 40d, step)
                || !BuildSquareOnOffsetPlane(part, "S12", Api5.PlaneXoy, 30d, 0d, 0d, 20d, step))
            {
                step.Observe("сечения не построены — шаг не состоялся");
                return;
            }

            if (Container(doc) is not { } container)
            {
                step.Fail("контейнер API7 не получен");
                return;
            }

            if (Api5.SafeObject(() => container.Lofts) is not ILofts collection)
            {
                step.Fail("container.Lofts не привёлся к ILofts");
                return;
            }

            var loft = Api5.SafeObject(() => collection.Add((ksObj3dTypeEnum)BossLoftType)) as ILoft;
            if (loft is null)
            {
                step.Fail("ILofts.Add(31) не вернул ILoft");
                return;
            }

            var first = TransferTo7(Find(part, "S11"));
            var second = TransferTo7(Find(part, "S12"));
            var sections = new object[] { first!, second! };
            loft.Sketchs = sections;
            loft.Closed = false;

            var chain = Api5.SafeObject(loft.AddCoupling) as ICoupling;
            var placed = chain is not null && TryWrite(() =>
            {
                chain.PositionOffset[0] = 0d;
                chain.PositionOffset[1] = 20d;
            }) == true;
            var updated = Api5.SafeBool(loft.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volumeWithChain = TotalVolume(part);
            var countWithChain = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["chain_placed"] = placed;
            step.Data["count_with_chain"] = countWithChain;
            step.Data["volume_with_chain_mm3"] = volumeWithChain;
            step.Observe("цепочка 0 / 20 мм задана: Update()=" + updated + ", CouplingsCount = "
                + Text(countWithChain) + ", объём " + Api5.Num(volumeWithChain));

            // The same set of sections fed AGAIN — exactly what an edit of section_refs does.
            loft.Sketchs = new object[] { first!, second! };
            var countAfterWrite = Api5.SafeInt(() => loft.CouplingsCount);
            step.Data["count_after_sketchs_write"] = countAfterWrite;
            step.Observe("после ПОВТОРНОГО присваивания того же набора сечений: CouplingsCount = "
                + Text(countAfterWrite));

            var updatedAgain = Api5.SafeBool(loft.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volumeAfterWrite = TotalVolume(part);
            step.Data["volume_after_sketchs_write_mm3"] = volumeAfterWrite;
            step.Observe("после перестроения: Update()=" + updatedAgain + ", объём "
                + Api5.Num(volumeAfterWrite));

            if (countWithChain is null || countAfterWrite is null)
            {
                step.Fail("CouplingsCount не прочитался: до записи сечений " + Text(countWithChain)
                    + ", после " + Text(countAfterWrite) + " — вопрос остался неразличённым");
                return;
            }

            var cleared = countAfterWrite == 0;
            step.Data["chains_cleared_by_sketchs_write"] = cleared;
            step.Pass("измерено: цепочка на признаке читалась как " + countWithChain + ", а после "
                + "повторного присваивания того же набора сечений — как " + countAfterWrite
                + " (объём " + Api5.Num(volumeWithChain) + " → " + Api5.Num(volumeAfterWrite) + "). "
                + (cleared
                    ? "Присваивание ILoft.Sketchs СБРАСЫВАЕТ цепочки, поэтому «оставить соответствие "
                      + "как было» после замены сечений невыразимо в принципе, а не только по правилу "
                      + "сервера"
                    : "Присваивание ILoft.Sketchs цепочки СОХРАНЯЕТ, поэтому молчаливое «оставить как "
                      + "было» возможно, и запрет на него — правило сервера, а не свойство ядра"));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Write the chain state, rebuild and read the volume — «one change at a time» (§8): exactly one
    /// parameter changes, the rest are held fixed. The volume is read FROM THE BODY, not from the call's
    /// response.</summary>
    private (bool? Updated, double? Volume) ApplyChain(
        ksDocument3D doc, ksPart part, ILoft loft, string label, ProbeStep step)
    {
        var updated = Api5.SafeBool(loft.Update);
        part.RebuildModel();
        doc.RebuildDocument();
        var volume = TotalVolume(part);
        step.Observe(label + ": Update()=" + updated + ", объём " + Api5.Num(volume));
        return (updated, volume);
    }

    /// <summary>Contour-length fractions on both sections in ONE read.</summary>
    private static string ReadPercentPair(ICoupling coupling) =>
        ReadPair(index => Api5.SafeDouble(() => coupling.Position[index]));

    /// <summary>Contour offsets in mm on both sections in ONE read.</summary>
    private static string ReadOffsetPair(ICoupling coupling) =>
        ReadPair(index => Api5.SafeDouble(() => coupling.PositionOffset[index]));

    private static string ReadPair(Func<int, double?> read) =>
        Text(read(0)) + " / " + Text(read(1));

    /// <summary>The chain point's coordinates — <c>ICoupling.GetPoint(Index, X, Y, Z)</c>.</summary>
    private static string CouplingPointText(ICoupling coupling, int index)
    {
        try
        {
            return coupling.GetPoint(index, out var x, out var y, out var z)
                ? "(" + Api5.Num(x) + "; " + Api5.Num(y) + "; " + Api5.Num(z) + ")"
                : "отказ GetPoint";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "не прочитано";
        }
    }

    /// <summary>Write into a COM object: <c>false</c> — «the call was rejected», not «wrote false».</summary>
    private static bool? TryWrite(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static string Text(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано";

    private static string Text(double? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "не прочитано";

    /// <summary>B5.15 — Kinematics: are the INPUTS of an existing feature edited — profile and path.</summary>
    /// <remarks>
    /// <b>Why a separate step.</b> For a sweep operation the mode (B5.13) is not the only input:
    /// the body is defined by the profile and the path, and the <c>edit</c> action of the mandatory
    /// rows <c>SM-04</c> applies to the feature as a whole. The conclusion from FILLET or EXTRUDE
    /// cannot be carried over here: on extrude <c>SetSketch</c> is MEASURED NOT to apply (row
    /// <c>L11</c>: returns <c>true</c>, the previous sketch is read back), but that is a fact about
    /// extrude, not about sweep.
    /// <b>A negative outcome must be controllable, otherwise it is indistinguishable from a harness
    /// defect.</b> A POSITIVE CONTROL sits on the same feature here: changing the section-motion mode
    /// (MEASURED by step B5.13) must move the volume <c>24674.011002723353 → 15707.963267948984</c>.
    /// Only after that does «rebinding the input did not move the volume» read as a fact about the
    /// product: a harness that sees a parameter change on this very object also sees the absence of a
    /// change.
    /// <b>The setup discriminates each input separately.</b> Changing the PROFILE to Ø10 would give
    /// <c>π·25·(50·π/2) = 6168.502750680849</c>; changing the PATH to a 100 segment —
    /// <c>π·100·100 = 31415.926535897932</c>. Both values differ from the original by more than the
    /// tolerance by an order of magnitude, so «input applied» is distinguishable from «input ignored».
    /// Separately recorded is whether the object being substituted was FOUND: substituting <c>null</c>
    /// would look like «did not apply».
    /// </remarks>
    private void SweepInputEdit()
    {
        var step = _report.Begin("B5.15",
            "Кинематика: правятся ли ВХОДЫ существующего признака — профиль и траектория",
            "Меняет ли перепривязка профиля и траектории геометрию того же признака, если смена режима на нём объём меняет?");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P2", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathArc(part, "T2", Api5.PlaneXoy, 0d, 50d, 50d, 0d, 0d, 50d, 50d, step)
                || !Sweep(doc, part, step, "T2", "P2", shift: 2))
            {
                step.Observe("кинематика: операция не создана — править нечего");
                step.Fail("кинематика: операция не создана");
                return;
            }

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsEvolutionDefinition(e.GetDefinition()));
            if (entity?.GetDefinition() is not { } definition)
            {
                step.Observe("кинематика: признак в дереве не найден");
                step.Fail("кинематика: признак в дереве не найден");
                return;
            }

            // New inputs are prepared BEFOREHAND: the edit is applied to an already-built feature,
            // and the sketches must exist before it.
            if (!BuildProfile(part, "P4", PlaneYoz, 0d, 0d, 5d, step)
                || !BuildPathLine(part, "T4", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
            {
                step.Observe("кинематика: новые входы не построены");
                step.Fail("кинематика: новые входы не построены");
                return;
            }

            var created = TotalVolume(part);

            // ── PHASE 1: prove the harness sees changes ON THIS feature ──────────────────────────
            // Without this phase a negative outcome of phase 2 is indistinguishable from «the
            // harness does not see changes».
            var modeControl = EditSweepShiftMode(doc, part, entity, definition, 0, step);
            var modeControlBack = EditSweepShiftMode(doc, part, entity, definition, 2, step);

            // ── PHASE 2: inputs. Read back is NOT ONLY the volume, but the profile itself ────────
            var profileEdit = EditSweepProfile(doc, part, entity, definition, "P4", step);
            var pathEdit = EditSweepPath(doc, part, entity, definition, "T4", step);

            // ── PHASE 3: did the feature accept a mode edit AFTER the input edits ────────────────
            // This is a separate question: «the input did not apply» and «the input did not apply and
            // broke the feature» are different facts, and the second matters more to the caller.
            var modeAfterInputs = EditSweepShiftMode(doc, part, entity, definition, 0, step);

            step.Data["sweep_input_created_mm3"] = created;
            step.Data["sweep_input_mode_control_mm3"] = modeControl;
            step.Data["sweep_input_mode_control_back_mm3"] = modeControlBack;
            step.Data["sweep_profile_edited_mm3"] = profileEdit.Volume;
            step.Data["sweep_profile_read_back"] = profileEdit.ProfileName;
            step.Data["sweep_profile_expected_if_applied_mm3"] = 6168.502750680849d;
            step.Data["sweep_path_edited_mm3"] = pathEdit.Volume;
            step.Data["sweep_path_read_back"] = pathEdit.Names;
            step.Data["sweep_path_expected_if_applied_mm3"] = 31415.926535897932d;
            step.Data["sweep_input_mode_after_inputs_mm3"] = modeAfterInputs;

            var controlWorks = Near(created, 24674.011002723353d)
                               && Near(modeControl, 15707.963267948984d)
                               && Near(modeControlBack, 24674.011002723353d);
            if (!controlWorks)
            {
                step.Fail("положительный контроль не сработал: смена режима на этом признаке объём не "
                    + "сдвинула (" + Api5.Num(created) + " → " + Api5.Num(modeControl) + " → "
                    + Api5.Num(modeControlBack) + "), поэтому судить о входах нельзя");
                return;
            }

            // «Did not apply» is a match WITH THE CONTROL, not the absence of a number: null would
            // mean «not read», and it must not be taken for a negative outcome.
            // «Did not apply» is a match WITH THE BASELINE, not the absence of a number: null would
            // mean «not read», and it must not be taken for a negative outcome. NaN makes the
            // comparison false, so an unreadable baseline cannot «confirm» a negative outcome.
            var createdValue = created ?? double.NaN;
            var profileNotApplied = profileEdit.Volume is double pv && Near(pv, createdValue)
                                    && string.Equals(profileEdit.ProfileName, "P2", StringComparison.Ordinal);
            var pathNotApplied = pathEdit.Volume is double tv && Near(tv, createdValue);
            // After the write the path holder enumerates a DIFFERENT name than before it: so the
            // write into the collection DID reach it, but the body did not change — this is
            // «accepted, not applied», not «did not reach».
            var pathHolderChanged = !string.Equals(pathEdit.Names, "T2", StringComparison.Ordinal);
            var degraded = !Near(modeAfterInputs, 15707.963267948984d);

            step.Data["sweep_path_holder_changed"] = pathHolderChanged;
            step.Data["sweep_feature_degraded"] = degraded;

            if (!profileNotApplied || !pathNotApplied)
            {
                step.Fail("входы повели себя неоднозначно: профиль " + Api5.Num(profileEdit.Volume)
                    + " (прочитан «" + (profileEdit.ProfileName ?? "не прочитан") + "»), траектория "
                    + Api5.Num(pathEdit.Volume) + " (держатель «" + (pathEdit.Names ?? "не прочитан")
                    + "») при эталоне " + Api5.Num(created));
                return;
            }

            step.Pass("входы существующей кинематической операции НЕ правятся, и запись в них НЕ "
                + "безвредна. Профиль: SetSketch ПРИНЯТ, а определение по-прежнему отвечает «"
                + (profileEdit.ProfileName ?? "не прочитан") + "» вместо «P4», объём остался "
                + Api5.Num(created) + " вместо " + Api5.Num(6168.502750680849d) + ". Траектория: "
                + "«Clear+Add» ПРИНЯТЫ и держатель перечисляет «" + (pathEdit.Names ?? "не прочитан") + "» вместо "
                + "«T2», но объём остался " + Api5.Num(created) + " вместо "
                + Api5.Num(31415.926535897932d) + ". Смена РЕЖИМА до этих записей применялась и была "
                + "обратима (" + Api5.Num(created) + " → " + Api5.Num(modeControl) + " → "
                + Api5.Num(modeControlBack) + "), а ПОСЛЕ них применятся перестала ("
                + Api5.Num(modeAfterInputs) + " вместо " + Api5.Num(15707.963267948984d) + ")"
                + (degraded ? " — то есть принятая и не применённая запись входа оставляет признак в "
                    + "состоянии, где последующая правка параметра уже не применяется"
                    : " — порядок не воспроизвёлся, признак выжил"));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>B5.22 — Kinematics: is the INPUT edited through the SECOND store (<c>IEvolution.Sketch</c>).</summary>
    /// <remarks>
    /// <b>The reason is a MEASURED fact of a NEIGHBOURING family, not a guess.</b> For a loft the
    /// input lives in TWO stores (the API5 definition and the API7 operation object), and a write to
    /// one of them is cancelled by the very first update until both are written (step B5.21). A sweep
    /// is arranged the same way, and <c>IEvolution</c> has a DOCUMENTED property <c>Sketch</c> —
    /// «Сечение. Эскиз» (<c>ievolution_propers.html</c>, interface composition: <c>OperationResult</c>,
    /// <c>Sketch</c>, <c>SketchShiftType</c> and the method <c>GetPathLength</c>). Step B5.15 did not
    /// touch this property: it wrote ONLY to the API5 definition. So the conclusion «the profile is not
    /// edited» was, before this probe, verified on ONE store, and here it is not restated but checked —
    /// otherwise a harness defect would read as a fact about the product.
    /// <b>The positive control is mandatory and comes BEFORE the experiments.</b> Editing the MODE
    /// through the definition on this same feature does apply (MEASURED by B5.15), so «the input did
    /// not apply» here is distinguishable from «the harness does not see changes».
    /// <b>The discriminating baselines are named in advance and all four outcomes are distinct:</b>
    /// nothing applied — <c>24674.011002723353</c>; only the path applied (line 100) —
    /// <c>31415.926535897932</c>; only the profile applied (R5 along the arc) —
    /// <c>6168.502750680849</c>; both applied (R5 along line 100) — <c>7853.981633974483</c>.
    /// </remarks>
    private void SweepInputStores()
    {
        var step = _report.Begin("B5.22",
            "Кинематика: правится ли ВХОД через ВТОРОЕ хранилище — API7 IEvolution.Sketch",
            "Шаг B5.15 писал вход только в определение API5 и получил «принято и не применено». У IEvolution есть документированное свойство Sketch: применяется ли запись в него и что тогда отвечает определение API5?");

        const double ArcOrthogonal = 24674.011002723353d;
        const double ArcParallel = 15707.963267948984d;
        const double ProfileR5ByArc = 6168.502750680849d;
        const double PathLine100 = 31415.926535897932d;
        const double BothApplied = 7853.981633974483d;

        // ── Part 1: the feature is created, the edit goes through BOTH stores ────────────────────
        var doc = NewPart(out var part);
        try
        {
            if (!BuildProfile(part, "P2", PlaneYoz, 0d, 0d, 10d, step)
                || !BuildPathArc(part, "T2", Api5.PlaneXoy, 0d, 50d, 50d, 0d, 0d, 50d, 50d, step)
                || !Sweep(doc, part, step, "T2", "P2", shift: 2))
            {
                step.Observe("кинематика: операция не создана — править нечего");
                step.Fail("кинематика: операция не создана");
                return;
            }

            if (!BuildProfile(part, "P4", PlaneYoz, 0d, 0d, 5d, step)
                || !BuildPathLine(part, "T4", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
            {
                step.Fail("кинематика: новые входы не построены");
                return;
            }

            var entity = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsEvolutionDefinition(e.GetDefinition()));
            if (entity?.GetDefinition() is not { } definition)
            {
                step.Observe("кинематика: признак в дереве не найден");
                step.Fail("кинематика: признак в дереве не найден");
                return;
            }

            var created = TotalVolume(part);
            step.Observe("признак создан: объём " + Api5.Num(created)
                + " (эталон ортогонально на дуге R50/90° — " + Api5.Num(ArcOrthogonal) + ")");

            // ── PHASE 1: positive control ────────────────────────────────────────────────────────
            var modeViaDefinition = EditSweepShiftMode(doc, part, entity, definition, 0, step);
            var modeBack = EditSweepShiftMode(doc, part, entity, definition, 2, step);
            var controlWorks = Near(created, ArcOrthogonal) && Near(modeViaDefinition, ArcParallel)
                               && Near(modeBack, ArcOrthogonal);
            if (!controlWorks)
            {
                step.Fail("положительный контроль не сработал: смена режима на этом признаке объём не "
                    + "сдвинула (" + Api5.Num(created) + " → " + Api5.Num(modeViaDefinition) + " → "
                    + Api5.Num(modeBack) + "), поэтому судить о входе нельзя");
                return;
            }

            // ── SECOND STORE: the API7 operation object ──────────────────────────────────────────
            var container = Container(doc);
            var evolutions = container is null ? null : Api5.SafeObject(() => container.Evolutions);
            var evolution = evolutions is null ? null : IndexedObject(evolutions, "Evolution", 0) as IEvolution;
            var sketchProperty = evolution?.GetType().GetProperty("Sketch");
            var shiftProperty = evolution?.GetType().GetProperty("SketchShiftType");
            var evolutionCount = evolutions is null
                ? (int?)null
                : Api5.SafeInt(() => IndexedCount(evolutions));
            step.Observe("container.Evolutions → " + Api5.RuntimeName(evolutions) + ", элементов "
                + (evolutionCount?.ToString() ?? "не прочитано") + "; Evolution(0) → "
                + Api5.RuntimeName(evolution));
            // EMPTY AND UNREAD ARE DISTINGUISHED DIRECTLY. The previous revision printed one word for
            // both cases, and that is exactly the conflation that makes «silence» read as a statement:
            // «the property returned null» and «could not be read» are different facts about the
            // product.
            var sketchBefore = ReadEvolutionProperty(evolution, sketchProperty, out var sketchReadError);
            step.Observe("IEvolution.Sketch: объявленный тип "
                + (sketchProperty?.PropertyType.FullName ?? "свойство не найдено")
                + ", значение → " + Api5.RuntimeName(sketchBefore)
                + " (имя «" + (EvolutionSectionName(sketchBefore, step) ?? "не прочитано") + "»)"
                + (sketchReadError is null ? "" : ", чтение бросило " + sketchReadError));

            // ── ARM A: write to API7 ONLY ────────────────────────────────────────────────────────
            var transferredProfile = TransferTo7(Find(part, "P4"));
            var api7Written = WriteEvolutionProperty(evolution, sketchProperty, transferredProfile, step);
            var api7Updated = evolution is null ? (bool?)null : Api5.SafeBool(evolution.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volumeAfterApi7 = TotalVolume(part);
            var sketchAfterApi7 = ReadEvolutionProperty(evolution, sketchProperty, out _);
            var definitionAfterApi7 = ReadSketchName(definition);
            var sectionNameAfterApi7 = EvolutionSectionName(sketchAfterApi7, step);
            step.Observe("рука A (только API7): записано=" + api7Written + ", Update=" + api7Updated
                + ", объём " + Api5.Num(volumeAfterApi7) + " (если применено — " + Api5.Num(ProfileR5ByArc)
                + "); IEvolution.Sketch читается " + Api5.RuntimeName(sketchAfterApi7)
                + " (имя «" + (sectionNameAfterApi7 ?? "не прочитано")
                + "»), определение API5 — «" + (definitionAfterApi7 ?? "не прочитано") + "»");

            // ── ARM B: write to BOTH stores (as for a loft) ──────────────────────────────────────
            var definitionArm = EditSweepProfile(doc, part, entity, definition, "P4", step);
            var api7WrittenAgain = WriteEvolutionProperty(
                evolution, sketchProperty, TransferTo7(Find(part, "P4")), step);
            var api7UpdatedAgain = evolution is null ? (bool?)null : Api5.SafeBool(evolution.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volumeBoth = TotalVolume(part);
            var sketchAfterBoth = ReadEvolutionProperty(evolution, sketchProperty, out _);
            var sectionNameAfterBoth = EvolutionSectionName(sketchAfterBoth, step);
            step.Observe("рука B (оба хранилища): определение пишет «"
                + (definitionArm.ProfileName ?? "не прочитано") + "», затем API7 записано="
                + api7WrittenAgain + ", Update=" + api7UpdatedAgain + ", объём " + Api5.Num(volumeBoth)
                + " (если применено — " + Api5.Num(ProfileR5ByArc) + "); IEvolution.Sketch читается "
                + Api5.RuntimeName(sketchAfterBoth) + " (имя «" + (sectionNameAfterBoth ?? "не прочитано")
                + "»)");

            // ── ARM C: mode through the API7 store ───────────────────────────────────────────────
            var shiftWritten = WriteEvolutionProperty(evolution, shiftProperty, 0, step);
            var shiftUpdated = evolution is null ? (bool?)null : Api5.SafeBool(evolution.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volumeApi7Shift = TotalVolume(part);
            step.Observe("рука C (режим через API7): записано=" + shiftWritten + ", Update=" + shiftUpdated
                + ", объём " + Api5.Num(volumeApi7Shift) + " (параллельно — " + Api5.Num(ArcParallel)
                + ", ортогонально — " + Api5.Num(ArcOrthogonal) + ")");

            // ── ARM D: path through the API5 definition AFTER the section has settled ───────────
            var pathArm = EditSweepPath(doc, part, entity, definition, "T4", step);
            step.Observe("рука D (траектория через API5 после правок): объём " + Api5.Num(pathArm.Volume)
                + " (если применено — " + Api5.Num(PathLine100) + ", если нет — "
                + Api5.Num(ArcOrthogonal) + " или " + Api5.Num(ArcParallel) + ")");

            step.Data["sweep_stores_created_mm3"] = created;
            step.Data["sweep_stores_control_mode_mm3"] = modeViaDefinition;
            step.Data["sweep_stores_control_back_mm3"] = modeBack;
            step.Data["sweep_stores_api7_sketch_type"] = sketchProperty?.PropertyType.FullName;
            step.Data["sweep_stores_sketch_before"] = sketchBefore is null
                ? "свойство вернуло null" : Api5.RuntimeName(sketchBefore);
            step.Data["sweep_stores_sketch_before_name"] = EvolutionSectionName(sketchBefore, step);
            step.Data["sweep_stores_sketch_before_error"] = sketchReadError;
            step.Data["sweep_stores_api7_write_accepted"] = api7Written;
            step.Data["sweep_stores_api7_update"] = api7Updated;
            step.Data["sweep_stores_after_api7_mm3"] = volumeAfterApi7;
            step.Data["sweep_stores_sketch_after_api7"] = sketchAfterApi7 is null
                ? "свойство вернуло null" : Api5.RuntimeName(sketchAfterApi7);
            step.Data["sweep_stores_sketch_after_api7_name"] = sectionNameAfterApi7;
            step.Data["sweep_stores_definition_after_api7"] = definitionAfterApi7;
            step.Data["sweep_stores_definition_write_name"] = definitionArm.ProfileName;
            step.Data["sweep_stores_both_mm3"] = volumeBoth;
            step.Data["sweep_stores_sketch_after_both"] = sketchAfterBoth is null
                ? "свойство вернуло null" : Api5.RuntimeName(sketchAfterBoth);
            step.Data["sweep_stores_sketch_after_both_name"] = sectionNameAfterBoth;
            step.Data["sweep_stores_api7_shift_write"] = shiftWritten;
            step.Data["sweep_stores_api7_shift_mm3"] = volumeApi7Shift;
            step.Data["sweep_stores_path_after_mm3"] = pathArm.Volume;
            step.Data["sweep_stores_expected_profile_applied_mm3"] = ProfileR5ByArc;
            step.Data["sweep_stores_expected_path_applied_mm3"] = PathLine100;
            step.Data["sweep_stores_expected_both_applied_mm3"] = BothApplied;

            var api7Applies = Near(volumeAfterApi7, ProfileR5ByArc);
            var bothApply = Near(volumeBoth, ProfileR5ByArc);
            var pathApplies = Near(pathArm.Volume, PathLine100);

            // ── Part 2: the path BEFORE the first access to API7 ────────────────────────────────
            var doc2 = NewPart(out var part2);
            double? beforeTransfer = null;
            double? profileBeforeTransfer = null;
            string? pathHolderBefore = null;
            try
            {
                if (BuildProfile(part2, "P2", PlaneYoz, 0d, 0d, 10d, step)
                    && BuildPathArc(part2, "T2", Api5.PlaneXoy, 0d, 50d, 50d, 0d, 0d, 50d, 50d, step)
                    && Sweep(doc2, part2, step, "T2", "P2", shift: 2)
                    && BuildProfile(part2, "P4", PlaneYoz, 0d, 0d, 5d, step)
                    && BuildPathLine(part2, "T4", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
                {
                    var entity2 = FeatureTree(part2, step).Select(e => e.Entity)
                        .FirstOrDefault(e => IsEvolutionDefinition(e.GetDefinition()));
                    if (entity2?.GetDefinition() is { } definition2)
                    {
                        // THE CONTAINER IS NOT REQUESTED HERE EVEN ONCE: the experiment answers the
                        // question whether the input is edited while the feature has NOT yet been
                        // transferred to API7.
                        var pathFirst = EditSweepPath(doc2, part2, entity2, definition2, "T4", step);
                        pathHolderBefore = pathFirst.Names;
                        beforeTransfer = pathFirst.Volume;
                        var profileFirst = EditSweepProfile(
                            doc2, part2, entity2, definition2, "P4", step);
                        profileBeforeTransfer = profileFirst.Volume;
                        step.Observe("рука E (входы ДО первого обращения к API7): траектория дала "
                            + Api5.Num(beforeTransfer) + " (если применено — " + Api5.Num(PathLine100)
                            + "), затем профиль дал " + Api5.Num(profileBeforeTransfer)
                            + " (если применено — " + Api5.Num(BothApplied) + ", только профиль — "
                            + Api5.Num(ProfileR5ByArc) + ")");
                    }
                    else
                    {
                        step.Observe("рука E не поставлена: признак во втором документе не найден");
                    }
                }
                else
                {
                    step.Observe("рука E не поставлена: заготовка второго документа не построена");
                }
            }
            finally
            {
                TryClose(doc2);
            }

            step.Data["sweep_stores_before_transfer_path_mm3"] = beforeTransfer;
            step.Data["sweep_stores_before_transfer_path_names"] = pathHolderBefore;
            step.Data["sweep_stores_before_transfer_profile_mm3"] = profileBeforeTransfer;

            var pathAppliesBeforeTransfer = Near(beforeTransfer, PathLine100);

            // Step verdict: the outcome must be UNAMBIGUOUS, not «looks like». Arm A's volume that
            // matched NEITHER the «not applied» baseline NOR the «applied» baseline means the feature
            // changed in an unclear way, and the stores cannot be judged by such a number.
            if (!Near(volumeAfterApi7, ArcOrthogonal) && !api7Applies)
            {
                step.Fail("неоднозначный исход руки A: объём " + Api5.Num(volumeAfterApi7)
                    + " не совпал ни с «не применено» (" + Api5.Num(ArcOrthogonal) + "), ни с "
                    + "«применено» (" + Api5.Num(ProfileR5ByArc) + ")");
                return;
            }

            if (api7Applies)
            {
                step.Pass("вход кинематической операции ПРАВИТСЯ — но через ДРУГОЕ хранилище. Запись "
                    + "IEvolution.Sketch применяется: " + Api5.Num(created) + " → "
                    + Api5.Num(volumeAfterApi7) + " при эталоне " + Api5.Num(ProfileR5ByArc)
                    + ", IEvolution.Sketch читается " + Api5.RuntimeName(sketchAfterApi7)
                    + ", а определение API5 отвечает «" + (definitionAfterApi7 ?? "не прочитано")
                    + "». Значит вывод шага B5.15 «профиль не правится» верен ТОЛЬКО для маршрута "
                    + "определения API5: SetSketch принят и не применён, потому что владелец входа — "
                    + "объект операции API7. Траектория через API5: " + Api5.Num(pathArm.Volume)
                    + (pathApplies ? " — применена" : " — не применена (у IEvolution документированного "
                        + "члена траектории нет: только Sketch, SketchShiftType, OperationResult, "
                        + "GetPathLength)"));
                return;
            }

            step.Pass("вход кинематической операции не правится НИ ЧЕРЕЗ ОДНО из двух хранилищ, и это "
                + "теперь измерено на ОБОИХ, а не на одном. Рука A — только API7: запись "
                + "IEvolution.Sketch принята=" + api7Written + ", Update=" + api7Updated
                + ", объём " + Api5.Num(volumeAfterApi7) + " вместо " + Api5.Num(ProfileR5ByArc)
                + " (не применено), IEvolution.Sketch читается " + Api5.RuntimeName(sketchAfterApi7)
                + ", определение API5 отвечает «" + (definitionAfterApi7 ?? "не прочитано") + "». "
                + "Рука B — ОБА хранилища подряд: " + Api5.Num(volumeBoth) + " — тоже не применено. "
                + "Рука C — КОНТРОЛЬ ПОСЛЕ этих записей, а не до них: правка режима через хранилище "
                + "API7 применилась (" + Api5.Num(volumeApi7Shift) + "), значит признак жив и прибор "
                + "видит изменения — «не применено» отличимо от «признак умер». Рука D — траектория "
                + "через API5 после правок: " + Api5.Num(pathArm.Volume) + " вместо "
                + Api5.Num(PathLine100) + ". Рука E — входы ДО первого обращения к API7: траектория "
                + Api5.Num(beforeTransfer) + " (держатель перечисляет «" + (pathHolderBefore ?? "не прочитано")
                + "», то есть запись ДОШЛА), затем профиль " + Api5.Num(profileBeforeTransfer)
                + " вместо " + Api5.Num(BothApplied) + " — ни порядок, ни хранилище исхода не меняют. "
                + "Правка РЕЖИМА при этом работает на обоих маршрутах: определение API5 "
                + Api5.Num(modeViaDefinition) + ", объект API7 " + Api5.Num(volumeApi7Shift));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>The name of the section sketch that the API7 STORE HOLDS. The reverse transfer is mandatory: the
    /// object arrives as <c>System.__ComObject</c>, and the check <c>is ksEntity</c> by managed type does
    /// not recognise it. These are different things, and the first revision of the harness took «name not
    /// read» for «property empty» — that is, it passed a harness defect off as a fact about the product.
    /// It is exactly this transfer that distinguishes the two outcomes: «the write was cancelled» (the
    /// previous name remained in the store) and «the write was kept, but the body did not follow it»
    /// (the name is new, the volume is the previous one).</summary>
    private string? EvolutionSectionName(object? value, ProbeStep step)
    {
        if (value is null)
        {
            return null;
        }

        if (value is ksEntity direct)
        {
            return direct.name;
        }

        // DOCUMENTED path: the property is declared as IModelObject, and it has Name. Read FIRST,
        // because it does not require a reverse transfer at all.
        if (value is IModelObject modelObject)
        {
            try
            {
                if (modelObject.Name as string is { Length: > 0 } named)
                {
                    return named;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                step.Observe("IModelObject.Name через API7: " + HResult.Describe(ex));
            }
        }

        var back = Api5.SafeObject(() => _app.TransferInterface(value, 2, 0));
        if (back is ksEntity transferred)
        {
            return transferred.name;
        }

        step.Observe("обратный перенос сечения API7 → API5 не дал ksEntity: " + Api5.RuntimeName(back));
        return null;
    }

    /// <summary>Read a property of an API7 object, DISTINGUISHING empty from unread: a <c>null</c> return without
    /// an error means «the property returned empty», while a non-empty <paramref name="error"/> means
    /// «could not be read». Conflating these two outcomes would turn a harness failure into a fact about
    /// the product.</summary>
    private static object? ReadEvolutionProperty(
        object? target, PropertyInfo? property, out string? error)
    {
        error = null;
        if (target is null || property is null)
        {
            error = "свойство или объект не прочитаны";
            return null;
        }

        try
        {
            return property.GetValue(target);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            error = HResult.Describe(ex);
            return null;
        }
    }

    /// <summary>Write a property of an API7 object through the managed interop type. <c>null</c> — «there was
    /// nowhere to write» (the property was not found or the object was not read), <c>false</c> — «the
    /// write was rejected». Distinguishing them is mandatory: conflating them would turn a harness
    /// failure into a product failure.</summary>
    private static bool? WriteEvolutionProperty(
        object? target, PropertyInfo? property, object? value, ProbeStep step)
    {
        if (target is null || property is null || value is null)
        {
            step.Observe("запись " + (property?.Name ?? "свойства") + ": писать некуда ("
                + (target is null ? "объект не прочитан" : property is null ? "свойство не найдено" : "значение не получено")
                + ")");
            return null;
        }

        try
        {
            var converted = value;
            if (value is int number && property.PropertyType != typeof(object))
            {
                converted = property.PropertyType.IsEnum
                    ? Enum.ToObject(property.PropertyType, number)
                    : Convert.ChangeType(number, property.PropertyType);
            }

            property.SetValue(target, converted);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
                                       or TargetInvocationException or ArgumentException)
        {
            step.Observe("запись " + property.Name + ": " + HResult.Describe(ex));
            return false;
        }
    }

    /// <summary>Change the section-motion mode on an existing feature — positive control.</summary>
    private static double? EditSweepShiftMode(
        ksDocument3D doc, ksPart part, ksEntity entity, object definition, int shift, ProbeStep step)
    {
        var written = false;
        try
        {
            if (definition is ksBaseEvolutionDefinition baseDefinition)
            {
                baseDefinition.sketchShiftType = (short)shift;
                written = true;
            }
            else if (definition is ksBossEvolutionDefinition bossDefinition)
            {
                bossDefinition.sketchShiftType = (short)shift;
                written = true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Observe("запись режима " + shift + ": " + HResult.Describe(ex));
        }

        var updated = Api5.SafeBool(entity.Update) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var volume = TotalVolume(part);
        step.Observe("контроль режима " + shift + ": записано=" + written + ", Update=" + updated
            + ", объём " + Api5.Num(volume));
        return volume;
    }

    /// <summary>Rebinding the profile of an existing sweep operation. Separately reported is whether the sketch
    /// being substituted was FOUND: substituting <c>null</c> would look like «did not apply», that is,
    /// the negative outcome would be a harness defect. And separately — the profile NAME read back from
    /// the definition after the edit: restating the request is not proof.</summary>
    private static (string? ProfileName, bool? Found, double? Volume) EditSweepProfile(
        ksDocument3D doc, ksPart part, ksEntity entity, object definition, string profileName, ProbeStep step)
    {
        var sketch = Find(part, profileName);
        var applied = false;
        try
        {
            if (definition is ksBaseEvolutionDefinition baseDefinition)
            {
                baseDefinition.SetSketch(sketch);
                applied = true;
            }
            else if (definition is ksBossEvolutionDefinition bossDefinition)
            {
                bossDefinition.SetSketch(sketch);
                applied = true;
            }
            else
            {
                step.Observe("определение не отвечает ни ksBaseEvolutionDefinition, ни ksBossEvolutionDefinition");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Observe("SetSketch(" + profileName + "): " + HResult.Describe(ex));
        }

        var updated = Api5.SafeBool(entity.Update) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var volume = TotalVolume(part);
        var readBack = ReadSketchName(definition);
        step.Observe("перепривязка профиля " + profileName + ": эскиз найден=" + (sketch is not null)
            + ", SetSketch вызван=" + applied + ", Update=" + updated
            + ", прочитан профиль «" + (readBack ?? "не прочитан") + "», объём " + Api5.Num(volume));
        return (readBack, sketch is not null, volume);
    }

    /// <summary>Rebinding the path of an existing sweep operation. Separately reported is how many elements the
    /// holder had BEFORE <c>Clear()</c> and WHAT THEY ARE NAMED after: «cleared an empty one» and
    /// «cleared a non-empty one» are different facts, and the name proves that the substituted sketch
    /// did not remain.</summary>
    private static (int? Parts, string? Names, double? Volume) EditSweepPath(
        ksDocument3D doc, ksPart part, ksEntity entity, object definition, string pathName, ProbeStep step)
    {
        object? holder = null;
        if (definition is ksBaseEvolutionDefinition baseDefinition)
        {
            holder = Api5.SafeObject(baseDefinition.PathPartArray);
        }
        else if (definition is ksBossEvolutionDefinition bossDefinition)
        {
            holder = Api5.SafeObject(bossDefinition.PathPartArray);
        }

        var before = -1;
        var beforeNames = string.Empty;
        var cleared = false;
        var added = 0;
        var pathFound = false;
        if (holder is ksEntityCollection collection)
        {
            before = Api5.SafeInt(collection.GetCount) ?? -1;
            beforeNames = ReadCollectionNames(collection);
            cleared = Api5.SafeBool(() => collection.Clear()) == true;
            var path = Find(part, pathName);
            pathFound = path is not null;
            if (path is not null && Api5.SafeBool(() => collection.Add(path)) == true)
            {
                added++;
            }
        }
        else
        {
            step.Observe("PathPartArray() не привёлся к ksEntityCollection — писать некуда");
        }

        var updated = Api5.SafeBool(entity.Update) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        var after = holder is ksEntityCollection readCollection
            ? Api5.SafeInt(readCollection.GetCount)
            : null;
        var afterNames = holder is ksEntityCollection namedCollection
            ? ReadCollectionNames(namedCollection)
            : string.Empty;
        var volume = TotalVolume(part);
        step.Observe("перепривязка траектории " + pathName + ": было элементов " + before
            + " («" + beforeNames + "»), Clear=" + cleared + ", эскиз найден=" + pathFound
            + ", Add=" + added + ", стало элементов " + (after?.ToString() ?? "не прочитано")
            + " («" + afterNames + "»), Update=" + updated + ", объём " + Api5.Num(volume));
        return (after, afterNames, volume);
    }

    /// <summary>The names of the collection's elements, comma-separated. An empty string — «not a single one read».</summary>
    /// <remarks>
    /// <b><c>refresh()</c> is NOT called here, and this is a MEASURED correction, not style.</b> The first
    /// revision called <c>refresh()</c> before enumerating — and got SEVEN names
    /// («Ось X,Ось Y,Ось Z,P2,T2,P4,T4») where <c>GetCount()</c> without <c>refresh()</c> answers
    /// <c>1</c>: the holder obtained from <c>PathPartArray()</c> starts enumerating the PART's entities
    /// after <c>refresh()</c>, not the path's elements. This is a harness defect: it would show a foreign
    /// set as «the path did not change». Therefore the enumeration follows exactly the same route as the
    /// <c>GetCount()</c> by which the element count is checked.
    /// </remarks>
    private static string ReadCollectionNames(ksEntityCollection collection)
    {
        var names = new List<string>();
        try
        {
            var count = collection.GetCount();
            for (var i = 0; i < count; i++)
            {
                if (collection.GetByIndex(i) is ksEntity entity)
                {
                    names.Add(entity.name);
                }
            }
        }
        catch (Exception)
        {
            // What was read is returned.
        }

        return string.Join(",", names);
    }

    /// <summary>The name of the profile sketch read FROM THE DEFINITION after the edit: restating the request is no good.</summary>
    private static string? ReadSketchName(object definition)
    {
        try
        {
            var sketch = definition switch
            {
                ksBaseEvolutionDefinition baseDefinition => baseDefinition.GetSketch(),
                ksBossEvolutionDefinition bossDefinition => bossDefinition.GetSketch(),
                _ => null,
            };
            return (sketch as ksEntity)?.name;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The feature's ordinal among its same-family siblings in the tree — the same address as the API7 collection's.</summary>
    private static int? OrdinalInTree(ksPart part, ProbeStep step, ksEntity target, Func<object?, bool> isKind)
    {
        var ordinal = 0;
        foreach (var (_, entity) in FeatureTree(part, step))
        {
            if (!isKind(entity.GetDefinition()))
            {
                continue;
            }

            if (ReferenceEquals(entity, target))
            {
                return ordinal;
            }

            ordinal++;
        }

        return null;
    }

    /// <summary>Transfer an API5 object to API7 the same way as measured on chamfer and fillet:
    /// <c>TransferInterface(object, ksAPI7Dual, 0)</c>. An untransferred object will not be seen by API7, so
    /// <c>null</c> here means «the value did not arrive», not «an empty list».
    /// </summary>
    private object? TransferTo7(object? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return _app.TransferInterface(element, 2, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Read an indexed property through reflection by the <b>managed</b> interop type. Needed because the
    /// C# form of such properties is not visible from the help (the help gives COM syntax), and guessing it
    /// is forbidden. Returns <c>null</c> if the member was not found or the call did not happen — and that
    /// is «not read», not zero.</summary>
    private static int? IndexedInt(object target, string name, bool index)
    {
        var type = target.GetType();
        foreach (var parameterType in new[] { typeof(bool), typeof(short), typeof(int) })
        {
            try
            {
                var method = type.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { parameterType }, null);
                if (method is null)
                {
                    continue;
                }

                var raw = method.Invoke(target, new object[] { Convert.ChangeType(index, parameterType) });
                return raw is null ? null : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException
                                           or FormatException or InvalidOperationException)
            {
                // Try the next parameter type: in interop the VARIANT_BOOL type is sometimes bool, sometimes short.
            }
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    /// <summary>
    /// Attaching the path. The method is set not by a guess: first the RUNTIME type of what
    /// <c>PathPartArray()</c> returned is read, and only then is the attempt made. If the type is not the
    /// expected one, that is recorded as a measurement rather than papered over with another call.
    /// </summary>
    private static bool AttachPath(ksPart part, ksBaseEvolutionDefinition definition, string pathName, ProbeStep step)
    {
        var holder = Api5.SafeObject(definition.PathPartArray);
        step.Observe("PathPartArray() → " + Api5.RuntimeName(holder));
        return AttachPathTo(part, holder, pathName, step);
    }

    /// <summary>The same binding, but for a holder obtained by any route: for a glued sweep feature (46) the holder
    /// is taken from <c>ksBossEvolutionDefinition</c>, and repeating the runtime-type analysis a second time
    /// would mean measuring the same thing with two harnesses.</summary>
    private static bool AttachPathTo(ksPart part, object? holder, string pathName, ProbeStep step)
    {
        if (holder is null)
        {
            return false;
        }

        var names = holder.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Add", StringComparison.Ordinal) || n.Contains("Count", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
        step.Data["path_holder_members"] = names;
        step.Observe("члены держателя траектории: " + string.Join(", ", names));

        var entity = Find(part, pathName);
        if (entity is null)
        {
            return false;
        }

        if (holder is ksEntityCollection collection)
        {
            var added = Api5.SafeBool(() => collection.Add(entity));
            step.Observe("ksEntityCollection.Add(" + pathName + ") = " + added);
            return added == true;
        }

        var method = holder.GetType().GetMethod("Add", new[] { entity.GetType() });
        if (method is null)
        {
            step.Observe("метод Add(" + entity.GetType().Name + ") у держателя не найден");
            return false;
        }

        try
        {
            method.Invoke(holder, new object[] { entity });
            step.Observe("Add(" + pathName + ") вызван отражённо");
            return true;
        }
        catch (Exception ex)
        {
            step.Observe("Add(" + pathName + ") отражённо: " + HResult.Describe(ex));
            return false;
        }
    }

    private static bool AddSections(object? sections, IReadOnlyList<ksEntity?> sectionEntities, ProbeStep step)
    {
        if (sections is null)
        {
            step.Observe("Sketchs() пуст");
            return false;
        }

        var names = sections.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Add", StringComparison.Ordinal) || n.Contains("Count", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
        step.Data["sketchs_holder_members"] = names;
        step.Observe("члены держателя сечений: " + string.Join(", ", names));

        var added = 0;
        foreach (var entity in sectionEntities)
        {
            if (entity is null)
            {
                continue;
            }

            if (sections is ksEntityCollection collection)
            {
                if (Api5.SafeBool(() => collection.Add(entity)) == true)
                {
                    added++;
                }

                continue;
            }

            var method = sections.GetType().GetMethod("Add", new[] { entity.GetType() });
            if (method is null)
            {
                continue;
            }

            try
            {
                method.Invoke(sections, new object[] { entity });
                added++;
            }
            catch (Exception ex)
            {
                step.Observe("Add сечения: " + HResult.Describe(ex));
            }
        }

        return added == sectionEntities.Count;
    }

    private static bool Sweep(ksDocument3D doc, ksPart part, ProbeStep step, string pathName, string profileName, int shift)
    {
        if (part.NewEntity(BaseEvolution) is not ksEntity entity
            || entity.GetDefinition() is not ksBaseEvolutionDefinition definition)
        {
            step.Observe("определение кинематической операции не получено");
            return false;
        }

        definition.SetSketch(Find(part, profileName));
        definition.sketchShiftType = (short)shift;
        if (!AttachPath(part, definition, pathName, step))
        {
            return false;
        }

        var created = Api5.SafeBool(entity.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        return created;
    }

    private static bool Shell(
        ksDocument3D doc, ksPart part, ProbeStep step, double thickness, bool thinType, bool removeTopFace,
        IReadOnlyList<FaceRow> faces)
    {
        if (part.NewEntity(ShellOperation) is not ksEntity entity
            || entity.GetDefinition() is not ksShellDefinition definition)
        {
            step.Observe("определение оболочки не получено");
            return false;
        }

        definition.thickness = thickness;
        definition.thinType = thinType;

        var array = Api5.SafeObject(definition.FaceArray);
        step.Observe("FaceArray() → " + Api5.RuntimeName(array));
        if (array is not null && removeTopFace && TopFace(faces) is { } top)
        {
            if (array is ksEntityCollection collection)
            {
                step.Observe("FaceArray.Add(верхняя грань) = " + Api5.SafeBool(() => collection.Add(top.Element!)));
            }
            else
            {
                var method = array.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
                if (method is not null && top.Element is not null)
                {
                    try
                    {
                        method.Invoke(array, new[] { top.Element });
                        step.Observe("FaceArray.Add(верхняя грань) вызван отражённо");
                    }
                    catch (Exception ex)
                    {
                        step.Observe("FaceArray.Add: " + HResult.Describe(ex));
                    }
                }
                else
                {
                    step.Observe("у FaceArray нет Add с одним параметром");
                }
            }
        }

        var created = Api5.SafeBool(entity.Create) == true;
        // Update() = true means «accepted», not «applied»: the result is judged by the volume, and this
        // field is published separately so that «accepted» is not read as «applied».
        var updated = Api5.SafeBool(entity.Update) == true;
        step.Observe("оболочка: Create=" + created + ", Update=" + updated);
        part.RebuildModel();
        doc.RebuildDocument();
        return created;
    }

    private static FaceRow? TopFace(IReadOnlyList<FaceRow> faces) =>
        faces.Where(f => f.Area is not null).OrderByDescending(f => f.Area).FirstOrDefault();

    private static bool BuildProfile(
        ksPart part, string name, short planeType, double cx, double cy, double radius, ProbeStep step) =>
        OnPlane(part, name, planeType, 0d, editor => editor.ksCircle(cx, cy, radius, 1), step);

    private static bool BuildSquare(
        ksPart part, string name, short planeType, double u0, double v0, double size, ProbeStep step) =>
        OnPlane(part, name, planeType, 0d,
            editor =>
            {
                editor.ksLineSeg(u0, v0, u0 + size, v0, 1);
                editor.ksLineSeg(u0 + size, v0, u0 + size, v0 + size, 1);
                editor.ksLineSeg(u0 + size, v0 + size, u0, v0 + size, 1);
                editor.ksLineSeg(u0, v0 + size, u0, v0, 1);
            }, step);

    private static bool BuildSquareOnOffsetPlane(
        ksPart part, string name, short basePlane, double offset, double u0, double v0, double size, ProbeStep step) =>
        OnPlane(part, name, basePlane, offset,
            editor =>
            {
                editor.ksLineSeg(u0, v0, u0 + size, v0, 1);
                editor.ksLineSeg(u0 + size, v0, u0 + size, v0 + size, 1);
                editor.ksLineSeg(u0 + size, v0 + size, u0, v0 + size, 1);
                editor.ksLineSeg(u0, v0 + size, u0, v0, 1);
            }, step);

    private static bool BuildPathLine(
        ksPart part, string name, short planeType, double x1, double y1, double x2, double y2, ProbeStep step) =>
        OnPlane(part, name, planeType, 0d, editor => editor.ksLineSeg(x1, y1, x2, y2, 1), step);

    private static bool BuildPathArc(
        ksPart part, string name, short planeType, double xc, double yc, double radius,
        double x1, double y1, double x2, double y2, ProbeStep step) =>
        OnPlane(part, name, planeType, 0d,
            editor => editor.ksArcByPoint(xc, yc, radius, x1, y1, x2, y2, 1, 1), step);

    /// <summary>A sketch on a plane (basic or offset). Returns the success flag; the reason for a failure stays in
    /// the step.</summary>
    private static bool OnPlane(
        ksPart part, string name, short planeType, double offset, Action<ksDocument2D> draw, ProbeStep step)
    {
        ksEntity plane;
        if (Math.Abs(offset) < 1e-9)
        {
            if (part.GetDefaultEntity(planeType) is not ksEntity basic)
            {
                step.Observe(name + ": базовой плоскости " + planeType + " нет");
                return false;
            }

            plane = basic;
        }
        else
        {
            if (part.NewEntity(Api5.PlaneOffset) is not ksEntity offsetPlane
                || offsetPlane.GetDefinition() is not ksPlaneOffsetDefinition offsetDefinition)
            {
                step.Observe(name + ": смещённая плоскость не создана");
                return false;
            }

            offsetDefinition.SetPlane(part.GetDefaultEntity(planeType));
            offsetDefinition.offset = offset;
            offsetDefinition.direction = true;
            if (Api5.SafeBool(offsetPlane.Create) != true)
            {
                step.Observe(name + ": смещённая плоскость не создалась");
                return false;
            }

            plane = offsetPlane;
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Observe(name + ": эскиз не создан");
            return false;
        }

        sketch.name = name;
        definition.SetPlane(plane);
        if (Api5.SafeBool(sketch.Create) != true)
        {
            step.Observe(name + ": эскиз не создался");
            return false;
        }

        if (definition.BeginEdit() is ksDocument2D editor)
        {
            draw(editor);
        }

        definition.EndEdit();
        return true;
    }

    private static bool BuildBox(
        ksPart part, string prefix, double width, double depth, double height, ProbeStep step,
        out List<FaceRow> faces)
    {
        faces = new List<FaceRow>();
        if (!OnPlane(part, prefix + "-profile", Api5.PlaneXoy, 0d,
                editor =>
                {
                    editor.ksLineSeg(0d, 0d, width, 0d, 1);
                    editor.ksLineSeg(width, 0d, width, depth, 1);
                    editor.ksLineSeg(width, depth, 0d, depth, 1);
                    editor.ksLineSeg(0d, depth, 0d, 0d, 1);
                }, step))
        {
            return false;
        }

        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return false;
        }

        definition.SetSketch(Find(part, prefix + "-profile"));
        definition.directionType = 0;
        definition.SetSideParam(true, 0, height, 0d, false);
        Api5.SafeBool(extrusion.Create);
        part.RebuildModel();
        faces = ReadFaces(part);
        step.Observe(prefix + ": граней прочитано " + faces.Count + ", объём " + Api5.Num(TotalVolume(part)));
        return faces.Count > 0;
    }

    private static List<FaceRow> ReadFaces(ksPart part)
    {
        var rows = new List<FaceRow>();
        try
        {
            if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
            {
                return rows;
            }

            faces.refresh();
            var count = faces.GetCount();
            for (var i = 0; i < count; i++)
            {
                var element = faces.GetByIndex(i);
                if (element is null)
                {
                    continue;
                }

                var face = element as ksFaceDefinition
                    ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;
                var area = face is null ? (double?)null : Api5.SafeDouble(() => face.GetArea(Api5.LengthMm));
                rows.Add(new FaceRow(i, element, area));
            }
        }
        catch (Exception)
        {
            // What was read is returned.
        }

        return rows;
    }

    private static ksEntity? Find(ksPart part, string name)
    {
        if (part.EntityCollection(0) is not ksEntityCollection entities)
        {
            return null;
        }

        entities.refresh();
        var count = entities.GetCount();
        for (var i = 0; i < count; i++)
        {
            if (entities.GetByIndex(i) is ksEntity entity
                && string.Equals(entity.name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        return null;
    }

    private static double? TotalVolume(ksPart part)
    {
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return null;
            }

            bodies.refresh();
            var count = bodies.GetCount();
            double sum = 0d;
            var any = false;
            for (var i = 0; i < count; i++)
            {
                if (bodies.GetByIndex(i) is { } element && Api5.BodyVolume(element) is { } volume)
                {
                    sum += volume;
                    any = true;
                }
            }

            return any ? sum : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Launch()
    {
        var step = _report.Begin("B5.Z0", "Свой невидимый сеанс КОМПАС-3D v24", "Сеанс поднимается сам?");
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
        var step = _report.Begin("B5.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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
            // Not the subject of this step.
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

    private sealed record FaceRow(int Index, object? Element, double? Area);
}

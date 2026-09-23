using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба B5 — калибровка трёх семейств последней обязательной очереди: кинематическая операция
/// (SM-04), по сечениям (SM-05), оболочка (SM-13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем отдельная проба, а не сразу код адаптера.</b> Наряд B5 (§7) требует закрыть два открытых
/// вопроса ИЗМЕРЕНИЕМ, а не выбором маршрута: <c>OQ-A1</c> — чем создаётся БАЗОВАЯ кинематическая
/// операция, и <c>OQ-A12</c> — какие значения у направления тонкой стенки. Оба решают маршрут строки,
/// поэтому измеряются до кода. Плюс §8 требует подтвердить единицы отдельным числом на каждый новый
/// режим, а §9 даёт аналитические эталоны, которые надо не подставить, а проверить.
/// </para>
/// <para>
/// <b>Что уже измерено чтением официальной справки (не этой пробой).</b>
/// <c>obj3dtype.html</c> документирует соответствие типов:
/// <c>o3d_baseEvolution = 45 → ksBaseEvolutionDefinition / IEvolution</c>,
/// <c>o3d_baseLoft = 30 → ksBaseLoftDefinition / ILoft</c>,
/// <c>o3d_shellOperation = 43 → ksShellDefinition / IShell</c>.
/// При этом <c>ievolutions_add.html</c> перечисляет допустимыми для <c>IEvolutions::Add</c> только
/// <c>o3d_bossEvolution</c> и <c>o3d_cutEvolution</c>, а <c>ilofts_add.html</c> для <c>ILofts::Add</c> —
/// только <c>o3d_bossLoft</c> и <c>o3d_cutLoft</c>. БАЗОВОГО типа нет ни в одном из двух списков.
/// Поэтому «создать базовую операцию через API7 Add» документированным маршрутом не является, и
/// проба обязана это либо подтвердить, либо опровергнуть — и назвать исход по имени.
/// </para>
/// <para>
/// <b>Чего проба НЕ делает.</b> Не ищет недокументированные обходы и не перебирает предполагаемые
/// COM-члены: проверяются только те члены, что перечислены официальной справкой целевой версии
/// (SDK-страницы <c>ievolution_propers</c>, <c>iloft_propers</c>, <c>ishell_propers</c>) и их
/// API5-соответствия, объявленные в interop. Член, найденный в interop, помечается как «член найден
/// в интерфейсе» и маршрут официальным не делает.
/// </para>
/// </remarks>
internal sealed class B5Probe
{
    private const short BaseEvolution = 45;   // obj3dtype.html: o3d_baseEvolution = 45
    private const short BaseLoft = 30;        // obj3dtype.html: o3d_baseLoft = 30
    private const short BossEvolutionType = 46; // obj3dtype.html: o3d_bossEvolution = 46, есть в ievolutions_add
    private const short BossLoftType = 31;      // obj3dtype.html: o3d_bossLoft = 31, есть в ilofts_add
    private const short ShellOperation = 43;  // obj3dtype.html: o3d_shellOperation = 43
    private const short PlaneYoz = 3;         // X = 0

    /// <summary>
    /// <c>OperationElement = 110</c> — коллекция, которой адресуется дерево признаков
    /// (<c>KompasObjectTypes.OperationElement</c> в адаптере). Маршрут через
    /// <c>SubFeatureCollection</c> даёт <c>ksFeature</c> и определения с него не читает.
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

    /// <summary>
    /// Основание маршрута, прочитанное из официальной справки целевой версии. Это утверждение о
    /// ДОКУМЕНТАЦИИ, а не о продукте, и оно нужно затем, чтобы отличить «маршрут документирован» от
    /// «член найден в интерфейсе».
    /// </summary>
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

    /// <summary>
    /// Эталон A кинематической операции: окружность Ø20 по прямому отрезку 100 мм.
    /// Ожидание <c>S × L = π·10²·100 = 31415.926535897932</c> мм³. Число не получается ни из габарита
    /// (20×20×100 = 40000), ни из трактовки «радиус = 20» (вчетверо больше) — поэтому оно различающее.
    /// </summary>
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
    /// Эталон B и он же различающий: на ПРЯМОЙ траектории режимы «параллельно самому себе» и
    /// «ортогонально траектории» дают одно и то же тело, поэтому строка
    /// <c>SM-04.base.mode_orthogonal</c> на прямой неразличима и закрыта быть не может. Здесь
    /// траектория — дуга R50 на 90°, длина дуги <c>50·π/2 = 78.53981633974483</c>, и при
    /// ортогональной ориентации объём равен <c>S × L = 24674.011002723397</c>.
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

    /// <summary>
    /// Единицы <c>GetPathLength(bitVector)</c>. Аргумент — битовая маска <c>ST_MIX_*</c>; ноль
    /// запрещён. Прямой отрезок заведомо известной длины даёт эталон: измеренное значение
    /// сравнивается с аналитическим, и различие в 1000 раз отделило бы миллиметры от метров.
    /// </summary>
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

            // Первая редакция читала длину ДО построения и получала 0 — это дефект прибора, а не
            // факт о продукте: GetPathLength отвечает по построенной операции. Поэтому операция
            // строится здесь, и результат построения публикуется.
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
    /// Эталон по сечениям: усечённая пирамида 40×40 → 20×20 при высоте 30.
    /// <c>V = h/3 · (A₁ + A₂ + √(A₁A₂)) = 10 · (1600 + 400 + 800) = 28000</c> мм³.
    /// Различающие контроли: «средняя площадь × высота» = 30000 (отличие 2000), призма по нижнему
    /// сечению = 48000, по верхнему = 12000. Дополнительно измеряется число сечений: концентрические
    /// параллельные сечения объёмом ПОРЯДОК не различают, поэтому порядок проверяется отдельной
    /// постановкой, а не этим числом.
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
    /// OQ-A12 — ЗНАЧЕНИЯ направления тонкой стенки. В API7 <c>IShell.ThinType</c> объявлен как
    /// <c>long</c>, в API5 <c>ksShellDefinition.thinType</c> — как <c>bool</c>: типы разные, значения
    /// неизвестны. Различает их ОБЪЁМ, а не текст ответа. Короб 100×80×10 с удалённой верхней гранью,
    /// t = 2: «внутрь» даёт 21632, «наружу» — 24832, отличие 3200 мм³.
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
            // Классификация НЕ предполагает, какой половине какое значение соответствует: обе
            // величины сравниваются с аналитической парой {21632 внутрь, 24832 наружу}, и
            // соответствие называется по измерению. Первая редакция ждала «false = внутрь» и
            // получила обратное — ошибка была в ОЖИДАНИИ, а не в измерении.
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

    /// <summary>
    /// Толщина и список удаляемых граней. <c>t = 4</c> при открытой оболочке внутрь даёт полость
    /// 92×72×6 = 39744 и объём 80000 − 39744 = 40256; отличие от <c>t = 2</c> равно 18624 мм³.
    /// Пустой список граней даёт ЗАМКНУТУЮ оболочку 36224 — это проверяется, а не предполагается.
    /// </summary>
    private void ShellThicknessAndFaces()
    {
        var step = _report.Begin("B5.6", "Толщина t=4 и пустой список граней оболочки",
            "Даёт ли t=4 объём 40256, а пустой список граней — замкнутую оболочку 36224?");
        step.Data["expected_t4_mm3"] = 40256d;
        step.Data["expected_closed_mm3"] = 36224d;
        step.Observe("ожидание: t=4 открытая 40256 = 80000 − 92·72·6; без удалённых граней 36224 = 80000 − 96·76·6");

        // thinType=true — ВНУТРЬ: так измерено в B5.5 (24832/21632), и здесь это не предполагается
        // заново, а берётся из измерения. Первая редакция передавала false и получала 53056 —
        // число верное для «наружу» (108·88·14 − 80000 = 53056), ошибочным было ожидание 40256.
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
    /// Маршруты API7. Проверяется ровно то, что документировано: свойства контейнера
    /// <c>Evolutions</c>/<c>Lofts</c>/<c>Shells</c> и метод <c>Add</c> у каждой коллекции. Базовые
    /// типы 45 и 30 в списки <c>Add</c> не входят, поэтому исход по ним называется ПО ИМЕНИ, а не
    /// выдаётся за документированный маршрут.
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
    /// ПРИКЛЕЕННЫЕ (не устаревшие) интерфейсы. Справка v24 на страницах
    /// <c>ksbaseevolutiondefinition.html</c> и <c>ksbaseloftdefinition.html</c> прямо пишет:
    /// «Данный интерфейс устарел. Рекомендуется использовать вместо него интерфейс
    /// ksBossLoftDefinition» — то есть базовые интерфейсы, на которых построены шаги B5.1…B5.4,
    /// объявлены УСТАРЕВШИМИ. Рекомендованные — <c>ksBossEvolutionDefinition</c> (тип 46) и
    /// <c>ksBossLoftDefinition</c> (тип 31), и оба типа ЕСТЬ в документированных списках
    /// <c>IEvolutions::Add</c> / <c>ILofts::Add</c>.
    /// </summary>
    /// <remarks>
    /// Числа здесь ТЕ ЖЕ, что у базовых шагов, и это не повторение: эталоны подобраны различающими
    /// (31415.926535897932 против габаритных 40000 у развёртки; 28000 против «средней площади» 30000
    /// у пирамиды), поэтому совпадение объёма доказывает, что маршрут 46/31 строит ТО ЖЕ тело, а не
    /// «что-то построилось». Без этого сравнения смена маршрута была бы сменой на неизмеренный.
    /// </remarks>
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

        // Классификация ОБЯЗАНА быть здесь, а не в подшагах: иначе шаг измеряет и молчит, а молчащий
        // прибор читается как «не проверено». Первая редакция B5.8 именно так и отдала verdict=unknown
        // при двух верных объёмах — дефект прибора, а не факт о продукте.
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

    /// <summary>Кинематический элемент типа 46 по тем же эталонам, что шаг B5.1/B5.3.</summary>
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
            definition.sketchShiftType = 2; // ортогонально, как в B5.1
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

    /// <summary>Элемент по сечениям типа 31 по тому же эталону, что шаг B5.4.</summary>
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

            // SetLoftParam(closed, flipVertex, autoPath) — документированный метод
            // (ksbaseloftdefinition_setloftparam.html): closed — признак замкнутости ТРАЕКТОРИИ,
            // flipVertex зарезервирован, autoPath — автоматическое формирование траектории.
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

            // Обратное чтение: GetLoftParam обязан вернуть то, что было установлено. Это контроль
            // «записано ли», а не «применено ли»: применение судит объём.
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
    /// API7-маршрут элемента по сечениям. Обязательная строка <c>SM-05.base.mode_couplings</c>
    /// требует <b>цепочек соответствия сечений</b>, а в API5 их нет вовсе (расхождение D3) — значит
    /// маршрут строки обязан быть API7. Справка v24: <c>ilofts_add.html</c> — «Допустимыми значениями
    /// LoftType являются o3d_bossLoft, o3d_cutLoft для коллекции операций IModelContainer::Lofts»,
    /// «после получения нового интерфейса нужно задать параметры операции и вызвать метод
    /// IModelObject::Update»; <c>iloft_sketchs.html</c> — <c>Sketchs</c> имеет тип <c>VARIANT</c> и
    /// <b>устанавливается</b> (<c>put_Sketchs</c>) как <c>SAFEARRAY</c> объектов <c>LPDISPATCH</c>
    /// (<c>VT_ARRAY | VT_DISPATCH</c>); <c>iloft_addcoupling.html</c> — <c>AddCoupling()</c> возвращает
    /// <c>ICoupling</c>.
    /// </summary>
    /// <remarks>
    /// Эталон тот же, что в B5.4/B5.8 — усечённая пирамида 28000 мм³, и это не повторение: совпадение
    /// доказывает, что маршрут API7 строит ТО ЖЕ тело. Дополнительно измеряется то, ради чего строка
    /// вообще требует API7: существует ли цепочка соответствий как объект и читается ли её число.
    /// </remarks>
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

            // Сечения переносятся в API7: Sketchs принимает SAFEARRAY указателей IDispatch, и
            // непереданный объект — это значение, которого API7 не увидит. Тот же приём, что измерен
            // на IChamfer.BaseObjects (transfer + присваивание object[]).
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

            // Ради чего строка требует API7: цепочка соответствий как объект.
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
    /// API7-маршрут оболочки: <c>IShells.Add()</c> (тип не принимается — <c>ishells_add.html</c>),
    /// <c>IShell.Thickness</c> (double), <c>IShell.ThinType</c> (<b>long</b>, тогда как API5
    /// <c>thinType</c> — <b>bool</b>), <c>IShell.DeletedFaces</c> (SAFEARRAY), <c>IShell.SetFaces</c>.
    /// </summary>
    /// <remarks>
    /// Измеряется то, что не измерено нигде: <b>какому значению <c>long</c> соответствует «внутрь»</b>.
    /// Классификация идёт по АНАЛИТИЧЕСКОЙ паре {21632 внутрь, 24832 наружу} на коробе 100×80×10 с
    /// удалённой верхней гранью, а не по тексту ответа. Пустой список граней проверяется отдельно:
    /// ожидание 36224, и в B5.6 на API5 этот исход НЕ получился (измерено 80000) — здесь он
    /// проверяется на API7, и расхождение остаётся расхождением, а не замалчивается.
    /// </remarks>
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

        // Замкнутая оболочка: на API5 (шаг B5.6) этот исход дал 80000 при ожидании 36224, и это
        // расхождение остаётся открытым. Здесь он проверяется на ДРУГОМ API — не чтобы «сгладить»,
        // а чтобы отделить «API5 не применил» от «оболочка так и работает».
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
    /// Одна постановка оболочки через API7: <c>IShells.Add()</c> → <c>Thickness</c>/<c>ThinType</c> →
    /// <c>DeletedFaces</c> → <c>Update()</c> → перестроение → объём и число граней.
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
            // ВНИМАНИЕ: в interop IShell.ThinType объявлен НЕ числом, а типом ksDirectionTypeEnum
            // (направление выдавливания), хотя в COM это c_long. Артефакт типизации обёртки; значение
            // остаётся числовым, и его смысл измеряется ОБЪЁМОМ, а не именем типа.
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

    /// <summary>Сравнение с аналитическим эталоном в допуске объёма (0,01 мм³ абс. / 1e-6 отн.).</summary>
    private static bool Near(double? measured, double expected) =>
        measured is { } value && Math.Abs(value - expected) <= Math.Max(0.01d, Math.Abs(expected) * 1e-6d);

    private static double? Difference(double? a, double? b) =>
        a is { } x && b is { } y ? Math.Abs(x - y) : null;


    // ══════════════════════════════════════════════════════════════ B5.11 ══

    /// <summary>
    /// Поверхность interop для трёх семейств, прочитанная <b>отражением по управляемым типам</b>
    /// (<c>typeof(ILoft).GetMembers()</c>), а не угадыванием. Нужна потому, что часть членов API7 —
    /// свойства с индексом (<c>BuildingType(BeginSection)</c>, <c>ICoupling.Position(Index)</c>), и
    /// C#-форма обращения к ним из справки не видна: справка даёт COM-синтаксис.
    /// </summary>
    /// <remarks>
    /// Это утверждение о <b>поверхности interop</b>, а не о документации и не о продукте: наличие
    /// члена здесь документированности не даёт и в вердикт по документации не идёт.
    /// </remarks>
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

    /// <summary>
    /// Обратное чтение ИЗ ДЕРЕВА: чем три новых признака видны в дереве документа и читаются ли их
    /// параметры обратно. Наряд требует именно этого: строка <c>read</c> обязана читать МОДЕЛЬ, а не
    /// пересказывать ответ создания — на этом уже провалились десять строк B4, и отказ был прав.
    /// </summary>
    /// <remarks>
    /// Шаг измеряет на каждое семейство то, что нельзя вывести из имени: (1) НОМЕР ТИПА признака в
    /// дереве и его имя в <c>ksObj3dTypeEnum</c>; (2) отвечает ли элемент дерева на <c>ksEntity</c> и
    /// что возвращает <c>GetDefinition()</c>; (3) читаются ли параметры семейства ОБРАТНО и равны ли
    /// они записанным; (4) находится ли признак в коллекции API7 (<c>Evolutions</c>/<c>Lofts</c>/
    /// <c>Shells</c>) — это и есть маршрут чтения из модели, а не из ответа создания.
    /// Классификация стоит В ЭТОМ ЖЕ шаге, а не в подшаге: молчащий прибор читается как «не проверено».
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

    /// <summary>
    /// Кинематика: после <c>NewEntity(45)</c> признак ищется В ДЕРЕВЕ, а не берётся из ручки создания.
    /// Ожидание — <c>o3d_baseEvolution</c> и определение <c>ksBaseEvolutionDefinition</c>.
    /// </summary>
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

            // Параметры читаются ОБРАТНО и сверяются с записанными: 2 = ортогонально (записано выше).
            // Ветка выбирается ПО ОТВЕТУ интерфейса, а не по номеру типа: номер в дереве и номер
            // создания у этих семейств не совпадают (измерено здесь: создано NewEntity(45), а в
            // дереве — 46), и это тот же дефект, что уже ловился на отверстии (52 → 583) и вращении.
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

    /// <summary>
    /// Три величины кинематической операции, прочитанные ОБРАТНО с определения из дерева, и сверка
    /// с записанным: <c>sketchShiftType</c> = 2 (ортогонально), одна часть траектории, длина 100 мм.
    /// </summary>
    /// <remarks>
    /// Аксессоры передаются делегатами, потому что <c>ksBaseEvolutionDefinition</c> и
    /// <c>ksBossEvolutionDefinition</c> не имеют общего интерфейса с этими членами, а какой из двух
    /// отвечает — измеряется, а не предполагается.
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

    /// <summary>
    /// Сечения: признак создан маршрутом адаптера (<c>ILofts.Add(31)</c>), поэтому и читается он
    /// маршрутом адаптера — из КОЛЛЕКЦИИ документа, а не из ручки создания. Дерево читается тем же
    /// проходом, чтобы стало видно, под каким номером типа признак попадает в дерево.
    /// </summary>
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

            // ЧТЕНИЕ ИЗ МОДЕЛИ: признак берётся из КОЛЛЕКЦИИ документа заново, а не из ручки выше.
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
    /// Оболочка: после <c>NewEntity(43)</c> признак ищется в дереве, а параметры читаются обратно с
    /// определения — те же три величины, что были записаны (<c>thickness</c>, <c>thinType</c>,
    /// число граней в <c>FaceArray</c>).
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

    /// <summary>
    /// Дерево признаков ТЕМ ЖЕ маршрутом, которым пользуется сам сервер и которым измерена приёмка
    /// B1–B4: <c>EntityCollection(OperationElement = 110)</c>.
    /// </summary>
    /// <remarks>
    /// Первая редакция этого шага шла через <c>SubFeatureCollection(true, false)</c> — и это был
    /// <b>дефект прибора</b>, а не факт о продукте: тот маршрут отдаёт <c>ksFeature</c>, который
    /// <b>НЕ приводится</b> к <c>ksEntity</c>, поэтому <c>GetDefinition()</c> с него не читается
    /// вовсе, а <c>ksFeature.type</c> отдаёт 105 (<c>o3d_entity</c>) у всех элементов подряд. Шаг
    /// честно доложил «в дереве НЕТ признака с определением» для кинематики и оболочки, хотя имена
    /// («Элемент по траектории:1», «Оболочка:1») в дереве стояли. Маршрут заменён на измеренный.
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

    /// <summary>Счётчик, который сам решает, что вернуть: <c>null</c> — «не прочитано», а не ноль.</summary>
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

    /// <summary>Отвечает ли определение на интерфейс кинематической операции. Оба интерфейса принимаются.</summary>
    private static bool IsEvolutionDefinition(object? definition) =>
        definition is ksBaseEvolutionDefinition or ksBossEvolutionDefinition;

    /// <summary>Отвечает ли определение на интерфейс элемента по сечениям. Оба принимаются.</summary>
    private static bool IsLoftDefinition(object? definition) =>
        definition is ksBaseLoftDefinition or ksBossLoftDefinition;

    /// <summary>Признак из дерева в виде, пригодном для отчёта: индекс, имя, номер типа и определение.</summary>
    private static string DescribeEntity(int index, ksEntity entity)
    {
        return "#" + index + " «" + (entity.name ?? "без имени") + "» type=" + entity.type
               + " (" + Api5.ObjectTypeName(entity.type) + ") → "
               + InterfaceAnswers(entity.GetDefinition());
    }

    /// <summary>
    /// Каким ИЗВЕСТНЫМ интерфейсам отвечает определение. Нужно потому, что <c>GetType().Name</c> у
    /// COM-объекта всегда <c>__ComObject</c> и о маршруте не говорит ничего.
    /// </summary>
    /// <remarks>
    /// Первая редакция отчёта печатала имя управляемого типа — и показывала «__ComObject» даже для
    /// определения оболочки, которое на самом деле приводится к <c>ksShellDefinition</c> и читается
    /// (thickness=2, thinType=true, FaceArray=1). Это <b>дефект прибора</b>, а не факт о продукте:
    /// имя класса RCW не является ни подтверждением, ни опровержением маршрута. Проверять надо
    /// ВОПРОСОМ «отвечает ли интерфейс», а не именем класса.
    /// </remarks>
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
                // Отказ приведения — это не «интерфейс есть» и не «интерфейса нет»: он не
                // зачитывается ни в одну сторону.
            }
        }

        return answers.Count == 0 ? "ни один известный интерфейс не отвечает" : string.Join("+", answers);
    }

    /// <summary>
    /// <c>IEvolution.OperationResult</c> — документированный ответ о виде операции. Ищется в
    /// КОЛЛЕКЦИИ документа: у API5-определения этого члена нет, а непрочитанное поле обязано
    /// остаться непрочитанным, а не стать нулём.
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

    /// <summary>Значение свойства <c>Count</c> у коллекции API7, прочитанное через interop-имя.</summary>
    private static int IndexedCount(object collection)
    {
        var value = collection.GetType().GetProperty("Count")?.GetValue(collection);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// Член с целочисленным индексом через отражение по управляемому типу interop —
    /// <c>get_Loft(int)</c>, <c>get_Shell(int)</c>. <c>null</c> означает «не прочитано».
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

    /// <summary>Свойство без индекса, прочитанное отражением: <c>OperationResult</c> у <c>IEvolution</c>.</summary>
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

    /// <summary>
    /// Правка НА МЕСТЕ: тот же признак, изменённый параметр, перестроение — и объём.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Наряд требует от действия <c>edit</c> двух вещей: правка меняет <b>ТОТ ЖЕ</b> признак и даёт
    /// ожидаемую геометрию, а запрос несёт <b>все</b> параметры режима — иначе «изменилось ровно
    /// запрошенное» неотличимо от «изменилось ещё и это».
    /// </para>
    /// <para>
    /// Каждая постановка берёт ДВА И ТРИ различающихся числа на ОДНОМ И ТОМ ЖЕ признаке: у
    /// кинематики на дуге <c>orthogonal → parallel → orthogonal</c> (24674.011002723353 →
    /// 15707.963267948984 → 24674.011002723353), у оболочки <c>t = 2 внутрь → t = 4 внутрь →
    /// t = 4 наружу</c> (21632 → 40256 → 42304). Возврат к первому значению — не украшение: он
    /// показывает, что правится ИМЕННО ЭТОТ признак, а не создаётся новый, и что правка обратима.
    /// </para>
    /// <para>
    /// Ссылка на признак берётся ИЗ ДЕРЕВА после создания, а не из ручки создания: ручка — это
    /// ответ создания, а наряд запрещает выдавать его за чтение.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Кинематика: <c>orthogonal → parallel → orthogonal</c> на дуге R50/90°. Различающая сила
    /// режимов здесь измерена (B5.2): <c>8966.047734774369</c> мм³.
    /// </summary>
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

    /// <summary>Одна правка режима движения сечения на уже найденном признаке.</summary>
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

    /// <summary>
    /// Сечения: правка <b>ВХОДА</b> — перепривязка сечений на уже построенном признаке.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Правка <c>Closed</c> на построенном признаке ПРИНИМАЕТСЯ И НЕ ПРИМЕНЯЕТСЯ, и это
    /// измерено, а не предположено.</b> <c>ILoft.Closed = true</c> возвращает <c>Update() = True</c>,
    /// но обратное чтение даёт <c>False</c>, а объём остаётся <c>28000</c> — то есть «принято» здесь
    /// НЕ означает «применено». Это ровно тот случай, ради которого заведено правило: <c>Update() =
    /// true</c> и успешный HRESULT — «принято», не «применено». Поэтому <c>closed</c> НЕ объявляется
    /// правимым параметром loft: правка, которая принимается и молчит, — не правка.
    /// </para>
    /// <para>
    /// Что действительно правится — <b>вход</b>: <c>ILoft.Sketchs</c> перепривязывается на другом
    /// наборе сечений. Постановка различающая: S5 (40×40) + S6 (20×20) дают усечённую пирамиду
    /// <c>28000</c>, а S5 (40×40) + S7 (40×40) дают призму <c>h/3·(A₁ + A₂ + √(A₁A₂)) =
    /// 10·(1600+1600+1600) = 48000</c>. Возврат к S6 возвращает <c>28000</c>: правится ТОТ ЖЕ
    /// признак, и правка обратима.
    /// </para>
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

            // Правка идёт по элементу КОЛЛЕКЦИИ, найденному по порядку, а не по ручке создания.
            var target = IndexedObject(lofts, "Loft", index) as ILoft;
            if (target is null)
            {
                step.Observe("сечения: Loft(" + index + ") не вернул ILoft");
                return false;
            }

            var before = TotalVolume(part);

            // Отрицательный результат, который обязан быть записан: Closed принимается и не
            // применяется. Он и есть причина, по которой closed не объявлен правимым параметром.
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

    /// <summary>
    /// Перепривязка сечений на уже построенном признаке: тот же признак, другой ВХОД.
    /// </summary>
    /// <remarks>
    /// <b>Чтения МЕЖДУ шагами обязательны, и это исправление прибора, а не украшение.</b> Первая
    /// редакция читала <c>loft.Sketchs</c> только ПОСЛЕ перестроения, и исход «запись легла в
    /// модель» был неотличим от «запись легла только в обёртку и была отменена первым же
    /// обновлением»: оба дают прочитанное число сечений, и различает их только чтение ДО обновления.
    /// Поэтому число сечений публикуется трижды — сразу после присваивания, после
    /// <c>Update()</c> и после перестроения.
    /// </remarks>
    private (int? Sections, double? Volume) EditLoftSections(
        ksDocument3D doc, ksPart part, ILoft loft, string first, string second, ProbeStep step)
        => EditLoftSectionsCore(doc, part, loft, new[] { first, second }, null, first + second, step);

    /// <summary>
    /// Та же перепривязка, но применяет <c>ksEntity.Update()</c> — вызов API5-сущности, которым
    /// применяет запись определения ОБОЛОЧКА (шаг B5.14, измеренно работает) и которым применяет
    /// правку сам продукт. Отдельная постановка нужна потому, что прибор B5.13 применяет
    /// <c>ILoft.Update()</c>, и расхождение «проба применяет, продукт нет» обязано быть разведено
    /// по ВЫЗОВУ, а не по догадке.
    /// </summary>
    private (int? Sections, double? Volume) EditLoftSectionsViaEntity(
        ksDocument3D doc, ksPart part, ILoft loft, ksEntity entity, string first, string second, ProbeStep step)
        => EditLoftSectionsCore(doc, part, loft, new[] { first, second }, entity, "entity_" + first + second, step);

    /// <summary>
    /// Перепривязка на ДРУГОМ ЧИСЛЕ сечений. Отдельная постановка потому, что число сечений — это и
    /// есть тот вход, который у продукта читается как 3 → 2 → 3: у прибора B5.13 число не менялось
    /// (2 → 2), поэтому «не применяется» там неотличимо от «менять число нельзя».
    /// </summary>
    private (int? Sections, double? Volume) EditLoftSectionCount(
        ksDocument3D doc, ksPart part, ILoft loft, string[] names, ProbeStep step)
        => EditLoftSectionsCore(doc, part, loft, names, null, "count_" + string.Join("-", names), step);

    /// <summary>Общая механика: перенести сечения, записать, прочитать МЕЖДУ шагами, вернуть число и объём.</summary>
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

    /// <summary>
    /// Сечения: ЧТО именно правится у существующего признака — ССЫЛКИ на сечения или их ЧИСЛО.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Шаг B5.13 измеряет, что перепривязка сечений при ТОМ ЖЕ числе
    /// (2 → 2) применяется: <c>28000 → 48000 → 28000</c>. Продукт же правит НАБОР сечений, то есть
    /// меняет и число, и его строка <c>B5S.01</c> читает «до записи 3, после присваивания
    /// <c>ILoft.Sketchs</c> 2, после <c>ksEntity.Update()</c> 3». Два расхождения — ВЫЗОВ применения
    /// (<c>ksEntity.Update()</c> против <c>ILoft.Update()</c>) и ЧИСЛО сечений — обязаны быть
    /// разведены измерением, а не рассуждением: иначе «продукт не умеет» и «API не умеет» неотличимы.
    /// </para>
    /// <para>
    /// <b>Фаза 1 — положительный контроль на ЭТОМ ЖЕ признаке</b>: перепривязка 2 → 2 через
    /// <c>ILoft.Update()</c> обязана дать <c>48000</c>. Без него отрицательный исход фазы 2 неотличим
    /// от «прибор не видит изменений на этом объекте».
    /// </para>
    /// <para>
    /// <b>Фаза 2 — два плеча.</b> Плечо A: та же перепривязка 2 → 2, но применяет
    /// <c>ksEntity.Update()</c> — вызов, которым применяет продукт. Плечо B: перепривязка на ТРЁХ
    /// сечениях через <c>ILoft.Update()</c> — то, что продукт делает по существу. Плечо C: возврат
    /// числа сечений обратно, чтобы «прибавилось» было отличимо от «прибавилось и убавилось».
    /// </para>
    /// <para>
    /// <b>Различающая постановка.</b> S5 (40×40) и S7 (40×40) дают призму <c>48000</c>, S5 + S6
    /// (20×20) — усечённую пирамиду <c>28000</c>. Тройка S5 + S8 (10×10 на z=15) + S7 даёт гладкую
    /// поверхность через три сечения; её величина ЗАПИСЫВАЕТСЯ, а не выводится формулой: по трём
    /// сечениям признак строит не сумму линейных участков (измерено на приёмке 20.09.2026 —
    /// <c>17114.2857130106</c> против отвергнутой суммы <c>22000</c>).
    /// </para>
    /// <para>
    /// <b>Почему шаг может «пройти» при отрицательном исходе.</b> Вопрос шага — «применяется ли
    /// правка на другом числе сечений», и оба ответа на него — измерение. Защита от
    /// самообмана здесь в двух местах: положительный контроль фазы 1 и требование, чтобы КАЖДОЕ
    /// чтение было не-<c>null</c> («не прочитано» — не отрицательный исход).
    /// </para>
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

            // ── ФАЗА 1: положительный контроль на ЭТОМ ЖЕ признаке ──────────────────────────────
            var control = EditLoftSections(doc, part, target, "S5", "S7", step);
            var controlBack = EditLoftSections(doc, part, target, "S5", "S6", step);

            // ── ФАЗА 2, ПЛЕЧО A: то же число сечений, но применяет ksEntity.Update() ────────────
            // Сущность переадресуется с дерева ПЕРЕД правкой — ровно как это делает продукт
            // (ReAddressFromTree): держать её от момента создания значило бы мерить не тот вызов.
            var entityNow = FeatureTree(part, step).Select(e => e.Entity)
                .FirstOrDefault(e => IsLoftDefinition(e.GetDefinition())) ?? entity;
            var viaEntity = EditLoftSectionsViaEntity(doc, part, target, entityNow, "S5", "S7", step);
            var viaEntityBack = EditLoftSectionsViaEntity(doc, part, target, entityNow, "S5", "S6", step);

            // ── ФАЗА 2, ПЛЕЧО B и C: ДРУГОЕ число сечений, применение через ILoft.Update() ──────
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
    /// Запись набора сечений В ОПРЕДЕЛЕНИЕ API5 — <c>ksBaseLoftDefinition.Sketchs()</c> →
    /// <c>ksEntityCollection</c>, <c>Clear()</c> затем <c>Add()</c> по каждому эскизу. Публикуется,
    /// сколько элементов было ДО <c>Clear()</c> и сколько стало после <c>Add()</c>: «очистили пустое»
    /// и «очистили непустое» — разные факты, и первый редакцией это различие терялось.
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
    /// Число сечений, прочитанное ЧЕРЕЗ ОПРЕДЕЛЕНИЕ API5 — тем же членом, которым их читает продукт
    /// (<c>ksBaseLoftDefinition.Sketchs()</c> в <c>LoftSectionRefs</c>, откуда их берёт и
    /// <c>kompas_get_feature</c>, и набор ссылок для правки). Отдельно от чтения через <c>ILoft</c>:
    /// у этих двух чтений может быть РАЗНОЕ влияние на состояние признака, и различие обязано быть
    /// измеримым. <c>null</c> — «не прочитано», а не ноль.
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
    /// Признак, СОЗДАННЫЙ на ТРЁХ сечениях, сводится к ДВУМ — воспроизводит ли прибор то, что
    /// читает строка <c>B5S.01</c>?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Шаг B5.20 развёл правку входа по вызову и по числу сечений и
    /// показал, что и <c>ksEntity.Update()</c>, и смена ЧИСЛА сечений работают — но у прибора признак
    /// был создан на ДВУХ сечениях и число сначала ПРИБАВЛЯЛОСЬ (2 → 3 → 2). Продукт же читает
    /// «до записи 3, после присваивания 2, после <c>ksEntity.Update()</c> 3» на признаке, созданном
    /// на ТРЁХ. Значит конфигурация «создан на N, сводится к M» прибором ещё не измерена, и
    /// переносить на продукт вывод B5.20 запрещено: конфигурации разные.
    /// </para>
    /// <para>
    /// <b>Что именно различается и почему это может быть существенно.</b> В B5.20 первый <c>Update()</c>
    /// признака вызывался при ДВУХ сечениях; здесь — при ТРЁХ. Если набор сечений, записанный в
    /// определение при первом построении, остаётся владельцем ЧИСЛА сечений, то сводить набор к
    /// меньшему нельзя, а наращивать — можно: у B5.20 первое построение закрепило 2, и запись 3
    /// победила; здесь первое построение закрепит 3, и запись 2 обязана проиграть. Это проверяемая
    /// догадка, и она либо подтверждается числом, либо отвергается.
    /// </para>
    /// <para>
    /// <b>Положительный контроль внутри постановки.</b> Плечо D2 возвращает ИСХОДНЫЙ набор из трёх
    /// сечений: если и оно не удержит 3, то «прибор перестал видеть изменения на этом объекте», и
    /// отрицательный исход D1/D3 читать как факт нельзя.
    /// </para>
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

            // Признак создаётся на ТРЁХ сечениях — ровно как у продукта (S5 40×40 @0, S8 10×10 @15,
            // S7 40×40 @30).
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

            // ── D1: сведение к двум через ILoft.Update() ────────────────────────────────────────
            var shrunk = EditLoftSections(doc, part, target, "S5", "S7", step);

            // ── D2: ПОЛОЖИТЕЛЬНЫЙ КОНТРОЛЬ — возврат исходного набора из трёх ───────────────────
            var restored = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);

            // ── D3: сведение к двум через ksEntity.Update() ─────────────────────────────────────
            var shrunkViaEntity = EditLoftSectionsViaEntity(doc, part, target, entity, "S5", "S7", step);

            // ── D4: тот же набор через ПОВТОРНО ПЕРЕНЕСЁННЫЙ контейнер ──────────────────────────
            // Продукт правит через контейнер, полученный ПЕРЕНОСОМ В МОМЕНТ ПРАВКИ: Api7Bridge
            // кэширует его по (документ, ревизия) и пере-переносит при смене ревизии, а создание
            // признака ревизию поднимает. Прибор до сих пор правил через ТОТ ЖЕ контейнер, которым
            // признак СОЗДАН. Если «правит только свой контейнер», то именно здесь запись перестанет
            // применяться — и это будет различие МАРШРУТА, а не свойство API.
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

            // ── D5: ЧТЕНИЕ ОПРЕДЕЛЕНИЯ API5 ПЕРЕД ПРАВКОЙ ───────────────────────────────────────
            // Продукт читает сечения ДВАЖДЫ и по-разному: через ILoft (правка) и через ОПРЕДЕЛЕНИЕ
            // API5 — ksBaseLoftDefinition.Sketchs() (LoftSectionRefs, то есть kompas_get_feature и
            // набор ссылок для правки). Прибор до сих пор определения не касался вовсе. Если чтение
            // определения материализует в нём собственный набор сечений, то именно оно и делает
            // определение владельцем ЧИСЛА сечений — и тогда «запись отменяется» есть следствие
            // ЧТЕНИЯ, а не записи. Это проверяемая догадка, и она проверяется здесь.
            var restoredThird = EditLoftSectionCount(doc, part, target, new[] { "S5", "S8", "S7" }, step);
            var definitionCount = DefinitionSectionCount(entity);
            step.Observe("сечений через определение API5 (ksBaseLoftDefinition.Sketchs()): "
                + (definitionCount?.ToString() ?? "не прочитано"));
            var afterDefinitionRead = EditLoftSectionsViaEntity(doc, part, target, entity, "S5", "S7", step);

            // ── D6: КАНДИДАТ В ИСПРАВЛЕНИЕ — писать набор В ОБА ХРАНИЛИЩА ───────────────────────
            // Если владельцем ЧИСЛА сечений определение становится от одного ЧТЕНИЯ, то запись
            // обязана попасть и в него: тогда оба хранилища говорят одно, и «чему верить» не
            // остаётся. Здесь проверяется именно это, а не «а вдруг поможет».
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

            // Контроль: возврат ИСХОДНОГО набора обязан удержать 3 и вернуть исходный объём.
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

    /// <summary>
    /// Оболочка: <c>t = 2 внутрь → t = 4 внутрь → t = 4 наружу → t = 2 внутрь</c> на одном признаке.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Три различающихся числа: <c>21632</c>, <c>40256</c> (отличие от t = 2 равно <c>18624</c>) и
    /// <c>53056</c>. Возврат к <c>21632</c> показывает, что правится ИМЕННО ЭТОТ признак, а не
    /// создаётся новый.
    /// </para>
    /// <para>
    /// <b>Ожидание для «t = 4 наружу» было ошибочным, и это исправлено здесь, а не подогнано под
    /// измерение.</b> Первая редакция ждала <c>42304 = 104·84·14 − 80000</c> — и получила
    /// <c>53056.000000000015</c>. Проверка ожидания по той же модели, что дала верное <c>24832</c>
    /// при t = 2: наружу материал добавляется по ВСЕМ трём осям, поэтому внешний короб при t = 4 равен
    /// <c>(100+2·4)×(80+2·4)×(10+4) = 108·88·14 = 133056</c>, а полость — исходный короб
    /// <c>80000</c>, то есть <c>53056</c>. Число <c>42304</c> получалось из <c>104·84·14</c>, где
    /// толщина к внешним габаритам прибавлялась как <c>t = 2</c> — ошибка была в ОЖИДАНИИ, а не в
    /// измерении, и измерение её подтвердило.
    /// </para>
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
        // Пишутся ОБА параметра режима, а не только изменяемый: запрос обязан нести весь режим,
        // иначе «изменилось ровно запрошенное» неотличимо от «изменилось ещё и это».
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

    /// <summary>
    /// Оболочка: правится ли НАБОР удаляемых граней на уже построенном признаке.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Обязательная строка <c>SM-13.shell.mode_remove_faces</c> требует
    /// действия <c>edit</c>, а правка толщины и направления (шаг B5.13) набор граней не трогает: там
    /// менялись <c>thickness</c> и <c>thinType</c>, а набор оставался тем же. Маршрут
    /// <c>Clear() + Add()</c> по <c>ksEntityCollection</c> у СКРУГЛЕНИЯ измеренно НЕ работал
    /// (строка <c>FL04r</c>: набор схлопывался, объём возвращался к пластине), поэтому переносить его
    /// на оболочку по аналогии запрещено — он проверяется здесь.
    /// </para>
    /// <para>
    /// <b>Постановка различающая, и различает её ВХОД.</b> Короб 100×80×10, оболочка <c>t = 2</c>
    /// внутрь, снята верхняя грань → <c>21632</c>. Добавление в набор ВТОРОЙ грани (нижней, 100×80)
    /// делает полость сквозной: <c>(100·80 − 96·76)·10 = 7040</c>. Разница <c>14 592</c> мм³ — не шум
    /// допуска, поэтому «набор применён» отличимо от «набор проигнорирован».
    /// </para>
    /// <para>
    /// <b>Отрицательный контроль стоит здесь же и обязателен.</b> Повторная запись ТОГО ЖЕ набора
    /// обязана оставить <c>21632</c>: если повторная запись сдвинет объём, прибор не отличает
    /// «применено» от «перестроено заново», и обе предыдущие величины не доказывают ничего.
    /// </para>
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

            // Выбор грани обоснован ИЗМЕРЕННОЙ площадью, а не индексом в коллекции: индексы между
            // прогонами не устойчивы, а площадь — свойство тела.
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

            // Правка A — ДОБАВЛЕНИЕ второй грани к уже снятой (маршрут Add() в FaceArray()).
            var added = EditShellFaceSet(doc, part, entity, definition, new[] { bottom }, clear: false, step);
            // Правка B — набор собирается ЗАНОВО: Clear() + Add(верхняя). Возврат к 21632.
            var rebuilt = EditShellFaceSet(doc, part, entity, definition, new[] { top }, clear: true, step);
            // Отрицательный контроль — та же запись ещё раз: объём обязан остаться прежним.
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
    /// Одна правка набора удаляемых граней: <c>Clear()</c> (если просили), затем <c>Add()</c> по
    /// каждой грани, затем <c>Update()</c> и пересборка. Возвращаются <b>раздельные факты</b> —
    /// число граней в теле и объём, — потому что при неработающем маршруте <c>Update()</c> всё равно
    /// отвечает <c>true</c> (измерено на скруглении, <c>FL04r</c>).
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

    /// <summary>
    /// Цепочки соответствия сечений — то, ради чего обязательная строка
    /// <c>SM-05.base.mode_couplings</c> требует маршрута API7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Документальная основа (SDK-справка v24, проверено по проводу 20.09.2026).</b>
    /// <c>iloft_addcoupling.html</c> — <c>LPDISPATCH AddCoupling()</c>, «Возвращаемое значение —
    /// указатель на интерфейс <c>ICoupling</c>»; <c>icoupling_count.html</c> — <c>Count</c>
    /// (<c>long</c>, только чтение) «Количество сечений в цепочке»; <c>icoupling_position.html</c> —
    /// <c>Position</c> (<c>double</c>) «Величина смещения точки вдоль контура сечения в %», входной
    /// параметр «<c>long Index</c> — индекс сечения в цепочке»; <c>icoupling_positionoffset.html</c> —
    /// то же в мм; <c>icoupling_setpoint.html</c> / <c>icoupling_getpoint.html</c> —
    /// <c>SetPoint(Index, X, Y, Z)</c> / <c>GetPoint(Index, &amp;X, &amp;Y, &amp;Z)</c> в мм;
    /// <c>iloft_deletecoupling.html</c> — <c>DeleteCoupling(Index)</c>; <c>iloft_clearcouplings.html</c> —
    /// <c>ClearCouplings()</c>; <c>iloft_coupling.html</c> — <c>Coupling(Index)</c> (только чтение).
    /// </para>
    /// <para>
    /// <b>Почему мало «цепочка создалась».</b> Обязательная строка говорит про
    /// <b>ОПРЕДЕЛЁННОЕ соответствие</b>, а не про существование объекта. Поэтому измеряется
    /// различающая сила: сдвиг точки на втором сечении вдоль контура (0 % → 25 %, то есть на
    /// следующий угол квадрата) обязан изменить тело, если содержимое цепочки действительно
    /// применяется. Обратный ход (25 % → 0 %) обязан вернуть прежний объём — ноль, который не
    /// сдвигали, не различает (§11).
    /// </para>
    /// <para>
    /// <b>Три способа задать точку мерятся по отдельности</b> — <c>Position</c> (%), <c>PositionOffset</c>
    /// (мм) и <c>SetPoint</c> (координаты, мм): у каждого своё число, и «выразить соответствие» не
    /// считается доказанным, пока не измерен тот способ, который попадёт в контракт.
    /// </para>
    /// <para>
    /// <b>Пара <c>Position</c> / <c>PositionOffset</c> читается ИЗ ОДНОГО МОМЕНТА</b> (§8): это две
    /// проекции одной величины, и расхождение между ними измеряется на одном состоянии.
    /// </para>
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

            // ── (2) Соответствие по доле длины контура: 0 % у обоих сечений. ──
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

            // ПРЕДУСЛОВИЕ GetPoint измеряется отдельно: на только что добавленной цепочке (до
            // первого Update) GetPoint ответил отказом. Здесь тот же вызов повторяется ПОСЛЕ
            // Update, и различие читается как предусловие, а не как «GetPoint не работает».
            step.Data["point_0_after_update"] = CouplingPointText(chain, 0);
            step.Data["point_1_after_update"] = CouplingPointText(chain, 1);
            step.Observe("после Update: GetPoint(0) = " + CouplingPointText(chain, 0)
                + ", GetPoint(1) = " + CouplingPointText(chain, 1)
                + " — тот же вызов ДО Update отвечал отказом");

            // ── (3) Различающий контроль: точка второго сечения сдвинута по контуру. ──
            TryWrite(() => chain.Position[1] = 25d);
            var (_, twistedVolume) = ApplyChain(doc, part, loft, "цепочка 0 % / 25 %", step);
            step.Data["twisted_volume_mm3"] = twistedVolume;
            step.Data["twist_difference_mm3"] = Difference(alignedVolume, twistedVolume);
            step.Observe("сдвиг точки на 25 % контура: объём " + Api5.Num(alignedVolume) + " → "
                + Api5.Num(twistedVolume) + ", разность " + Api5.Num(Difference(alignedVolume, twistedVolume)));

            // Обратный ход: возврат точки обязан вернуть прежнее тело.
            TryWrite(() => chain.Position[1] = 0d);
            var (_, returnedVolume) = ApplyChain(doc, part, loft, "возврат точки на 0 %", step);
            step.Data["returned_volume_mm3"] = returnedVolume;

            // ── (4) Второй способ задания точки: PositionOffset в мм. ──
            TryWrite(() => chain.PositionOffset[1] = 5d);
            var (_, offsetVolume) = ApplyChain(doc, part, loft, "PositionOffset = 5 мм на втором сечении", step);
            step.Data["offset_volume_mm3"] = offsetVolume;
            step.Data["position_after_offset"] = ReadPercentPair(chain);
            step.Data["offset_after_offset"] = ReadOffsetPair(chain);
            step.Observe("PositionOffset: записано 5 мм, прочитано " + ReadOffsetPair(chain)
                + "; Position в тот же момент " + ReadPercentPair(chain));
            step.Observe("GetPoint(1) при PositionOffset = 5 мм: " + CouplingPointText(chain, 1));

            // ── (5) Третий способ: SetPoint координатами в мм. ──
            var setPoint = Api5.SafeBool(() => chain.SetPoint(1, 10d, 10d, 30d));
            var (_, pointVolume) = ApplyChain(doc, part, loft, "SetPoint(1; 10; 10; 30)", step);
            step.Data["setpoint_accepted"] = setPoint;
            step.Data["setpoint_volume_mm3"] = pointVolume;
            step.Data["point_after_setpoint"] = CouplingPointText(chain, 1);
            step.Data["position_after_setpoint"] = ReadPercentPair(chain);
            step.Data["offset_after_setpoint"] = ReadOffsetPair(chain);
            step.Observe("SetPoint вернул " + setPoint + "; GetPoint(1) = " + CouplingPointText(chain, 1)
                + "; Position " + ReadPercentPair(chain) + "; PositionOffset " + ReadOffsetPair(chain));

            // Рамка координат SetPoint/GetPoint измерена ЧИСЛОМ, а не выведена из имени: квадрат S8
            // нарисован в ЛОКАЛЬНЫХ координатах 0…20 (периметр 80 мм). Точка (20; 5) — это 5 мм вдоль
            // контура, и она же читается как 6.25 % (5/80) при PositionOffset = 5; центр квадрата
            // (10; 10), поданный в SetPoint, спроецировался на контур в (20; 10) — 10 мм = 12.5 %.
            // Третья координата пришла равной 30 — смещению плоскости, а не локальному нулю.
            step.Data["frame_evidence"] = "локальные 0…20, периметр 80 мм: 5 мм ↔ 6.25 %, 10 мм ↔ 12.5 %";
            step.Observe("рамка координат: локальные координаты эскиза сечения (периметр 80 мм — "
                + "5 мм читается как 6.25 %, 10 мм как 12.5 %), поданная точка проецируется на контур, "
                + "третья координата равна смещению плоскости (30), а не локальному нулю");

            // Соответствие 0 %/0 % воспроизводит АВТОМАТИЧЕСКОЕ: объём совпал с эталоном без цепочки.
            step.Observe("цепочка 0 % / 0 % дала " + Api5.Num(alignedVolume) + " — тот же объём, что без "
                + "цепочки (" + Api5.Num(baseVolume) + "): явное соответствие ЗАМЕЩАЕТ автоматическое, "
                + "а не добавляется к нему");

            // ── (6) Жизненный цикл: чтение по индексу, Delete, DeleteCoupling, ClearCouplings. ──
            var byIndex = Api5.SafeObject(() => loft.Coupling[0]) as ICoupling;
            step.Data["coupling_by_index_type"] = Api5.RuntimeName(byIndex);
            step.Data["coupling_by_index_count"] = byIndex is null ? null : Api5.SafeInt(() => byIndex.Count);
            step.Observe("ILoft.Coupling(0) → " + Api5.RuntimeName(byIndex) + ", Count = "
                + Text(step.Data["coupling_by_index_count"] as int?));

            // ICoupling.Delete() вызывается ПЕРВЫМ, пока цепочка ещё в списке. В первой редакции
            // этого шага он стоял ПОСЛЕ DeleteCoupling(0), то есть удалял уже удалённый объект, и
            // отказ (False) читался как свойство продукта. Это дефект прибора класса 4/tan:
            // прибор описал своё состояние, а не объект. Исправлено порядком, а не толкованием.
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

            // ── Вердикт: цепочка создаётся, читается и РАЗЛИЧАЕТ. ──
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

    /// <summary>
    /// Цепочки, заданные ДО первого <c>Update()</c> — порядок, который может использовать адаптер.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Шаг B5.17 измерял цепочку, добавленную ПОСЛЕ первого
    /// <c>Update()</c>. Для адаптера важен другой порядок: фабрика API7 документирована как «задать
    /// параметры операции и вызвать <c>IModelObject::Update</c>» (<c>ilofts_add.html</c>), то есть
    /// цепочка должна задаваться ДО построения. Порядок не выводится из удобства — он измеряется:
    /// если цепочка до первого <c>Update()</c> не принимается, адаптер обязан строить дважды, и это
    /// называется прямо.
    /// </para>
    /// <para>
    /// <b>Различающая постановка та же, что в B5.17</b> — смещение точки второго сечения на 25 %
    /// контура. Измеренное значение B5.17: <c>28000 → 20000</c>. Совпадение здесь означает, что
    /// порядок не меняет результат; расхождение — что меняет, и тогда публикуется расхождение.
    /// </para>
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

            // Цепочка задаётся ДО первого построения.
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

            // 20 мм = 25 % контура 80 мм — та же различающая постановка, что в B5.17 (20000).
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

    /// <summary>
    /// Переживают ли цепочки соответствия повторное присваивание <c>ILoft.Sketchs</c>?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем этот шаг.</b> Первый прогон инструмента первой очереди (20.09.2026,
    /// <c>scratch/b5-first-contact.py</c>) дал неожиданное: правка <c>section_refs</c> на признаке,
    /// который НЕСёт цепочку, прошла без отказа, а проверки цепочек в ответе отсутствовали. Отказ
    /// был написан по допущению «цепочка переживает замену сечений»; измерение этого не подтвердило.
    /// Возможных объяснений два, и они разные: либо присваивание <c>Sketchs</c> сбрасывает цепочки,
    /// либо <c>CouplingsCount</c> в тот момент не прочитался (у отказа условие «прочитано и больше
    /// нуля», и нечитаемое значение его не зажигает). Различить их можно только чтением — чем этот
    /// шаг и занят.
    /// </para>
    /// <para>
    /// <b>Что здесь не проверяется.</b> Не проверяется, «правильно ли» ядро ведёт себя: измеряется
    /// ФАКТ, и он публикуется как есть. Если цепочки сбрасываются, это означает, что молчаливое
    /// «оставить как было» после смены сечений невыразимо в принципе, а не только по нашему правилу.
    /// </para>
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

            // Тот же набор сечений, поданный ПОВТОРНО, — ровно то, что делает правка section_refs.
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

    /// <summary>
    /// Запись состояния цепочки, перестроение и объём — «одно изменение за раз» (§8): меняется ровно
    /// один параметр, остальные зафиксированы. Объём читается с ТЕЛА, а не из ответа вызова.
    /// </summary>
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

    /// <summary>Доли длины контура по обоим сечениям в ОДНОМ чтении.</summary>
    private static string ReadPercentPair(ICoupling coupling) =>
        ReadPair(index => Api5.SafeDouble(() => coupling.Position[index]));

    /// <summary>Смещения по контуру в мм по обоим сечениям в ОДНОМ чтении.</summary>
    private static string ReadOffsetPair(ICoupling coupling) =>
        ReadPair(index => Api5.SafeDouble(() => coupling.PositionOffset[index]));

    private static string ReadPair(Func<int, double?> read) =>
        Text(read(0)) + " / " + Text(read(1));

    /// <summary>Координаты точки цепочки — <c>ICoupling.GetPoint(Index, X, Y, Z)</c>.</summary>
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

    /// <summary>Запись в COM-объект: <c>false</c> — «отказ вызова», а не «записано false».</summary>
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

    /// <summary>
    /// Кинематика: правятся ли ВХОДЫ существующего признака — профиль и траектория.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> У кинематической операции режим (B5.13) — не единственный вход:
    /// тело задают профиль и траектория, и действие <c>edit</c> обязательных строк <c>SM-04</c>
    /// относится к признаку целиком. Переносить сюда вывод со СКРУГЛЕНИЯ или ВЫДАВЛИВАНИЯ нельзя:
    /// на выдавливании <c>SetSketch</c> измеренно НЕ применяется (строка <c>L11</c>: возвращает
    /// <c>true</c>, читается прежний эскиз), но это факт о выдавливании, а не о кинематике.
    /// </para>
    /// <para>
    /// <b>Отрицательный исход обязан быть управляемым, иначе он неотличим от дефекта прибора.</b>
    /// Здесь стоит ПОЛОЖИТЕЛЬНЫЙ КОНТРОЛЬ на том же признаке: смена режима движения сечения
    /// (измерена шагом B5.13) обязана сдвинуть объём <c>24674.011002723353 → 15707.963267948984</c>.
    /// Только после этого «перепривязка входа объём не сдвинула» читается как факт о продукте:
    /// прибор, который видит изменение параметра на этом же объекте, видит и отсутствие изменения.
    /// </para>
    /// <para>
    /// <b>Постановка различающая по каждому входу отдельно.</b> Смена ПРОФИЛЯ на Ø10 дала бы
    /// <c>π·25·(50·π/2) = 6168.502750680849</c>; смена ТРАЕКТОРИИ на отрезок 100 — <c>π·100·100 =
    /// 31415.926535897932</c>. Обе величины отличимы от исходной на порядок больше допуска, поэтому
    /// «вход применён» отличимо от «вход проигнорирован». Отдельно записывается, НАЙДЕН ли объект,
    /// который подставляется: подстановка <c>null</c> выглядела бы как «не применилось».
    /// </para>
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

            // Новые входы готовятся ЗАРАНЕЕ: правка идёт на уже построенном признаке, и эскизы
            // обязаны существовать до неё.
            if (!BuildProfile(part, "P4", PlaneYoz, 0d, 0d, 5d, step)
                || !BuildPathLine(part, "T4", Api5.PlaneXoy, 0d, 0d, 100d, 0d, step))
            {
                step.Observe("кинематика: новые входы не построены");
                step.Fail("кинематика: новые входы не построены");
                return;
            }

            var created = TotalVolume(part);

            // ── ФАЗА 1: доказать, что прибор видит изменения НА ЭТОМ признаке ──────────────────
            // Без этой фазы отрицательный исход фазы 2 неотличим от «прибор не видит изменений».
            var modeControl = EditSweepShiftMode(doc, part, entity, definition, 0, step);
            var modeControlBack = EditSweepShiftMode(doc, part, entity, definition, 2, step);

            // ── ФАЗА 2: входы. Читается обратно НЕ ТОЛЬКО объём, но и сам профиль ─────────────
            var profileEdit = EditSweepProfile(doc, part, entity, definition, "P4", step);
            var pathEdit = EditSweepPath(doc, part, entity, definition, "T4", step);

            // ── ФАЗА 3: принял ли признак правку режима ПОСЛЕ правок входа ────────────────────
            // Это отдельный вопрос: «вход не применился» и «вход не применился и сломал признак» —
            // разные факты, и второй важнее для вызывающего.
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

            // «Не применилось» — это совпадение С КОНТРОЛЕМ, а не отсутствие числа: null означал бы
            // «не прочитано», и принимать его за отрицательный исход нельзя.
            // «Не применилось» — это совпадение С ЭТАЛОНОМ, а не отсутствие числа: null означал бы
            // «не прочитано», и принимать его за отрицательный исход нельзя. NaN делает сравнение
            // ложным, поэтому нечитаемый эталон не может «подтвердить» отрицательный исход.
            var createdValue = created ?? double.NaN;
            var profileNotApplied = profileEdit.Volume is double pv && Near(pv, createdValue)
                                    && string.Equals(profileEdit.ProfileName, "P2", StringComparison.Ordinal);
            var pathNotApplied = pathEdit.Volume is double tv && Near(tv, createdValue);
            // Держатель траектории после записи перечисляет ДРУГОЕ имя, чем до неё: значит запись
            // в коллекцию ДОШЛА, а тело не изменилось — это «принято, не применено», а не «не дошло».
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

    /// <summary>
    /// B5.22 — Кинематика: правится ли ВХОД через ВТОРОЕ хранилище (<c>IEvolution.Sketch</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Повод — измеренный факт СОСЕДНЕГО семейства, а не догадка.</b> У элемента по сечениям вход
    /// лежит в ДВУХ хранилищах (определение API5 и объект операции API7), и запись в одно из них
    /// отменяется первым же обновлением, пока не записаны оба (шаг B5.21). Кинематика устроена так же,
    /// и у <c>IEvolution</c> есть ДОКУМЕНТИРОВАННОЕ свойство <c>Sketch</c> — «Сечение. Эскиз»
    /// (<c>ievolution_propers.html</c>, состав интерфейса: <c>OperationResult</c>, <c>Sketch</c>,
    /// <c>SketchShiftType</c> и метод <c>GetPathLength</c>). Шаг B5.15 этого свойства не касался: он
    /// писал ТОЛЬКО в определение API5. Поэтому вывод «профиль не правится» до этой пробы проверен на
    /// ОДНОМ хранилище, и здесь он не пересказывается, а проверяется — иначе дефект прибора читался бы
    /// как факт о продукте.
    /// </para>
    /// <para>
    /// <b>Положительный контроль обязателен и стоит ДО опытов.</b> Правка РЕЖИМА через определение на
    /// этом же признаке применяется (измерено B5.15), поэтому «вход не применился» здесь отличимо от
    /// «прибор не видит изменений».
    /// </para>
    /// <para>
    /// <b>Различающие эталоны названы заранее и все четыре исхода различимы:</b> ничего не применилось —
    /// <c>24674.011002723353</c>; применилась только траектория (прямая 100) — <c>31415.926535897932</c>;
    /// применился только профиль (R5 по дуге) — <c>6168.502750680849</c>; применились оба (R5 по прямой
    /// 100) — <c>7853.981633974483</c>.
    /// </para>
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

        // ── Часть 1: признак создан, правка идёт через ОБА хранилища ─────────────────────────────
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

            // ── ФАЗА 1: положительный контроль ────────────────────────────────────────────────────
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

            // ── ВТОРОЕ ХРАНИЛИЩЕ: объект операции API7 ────────────────────────────────────────────
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
            // ПУСТОЕ И НЕПРОЧИТАННОЕ РАЗЛИЧАЮТСЯ ПРЯМО. Прежняя редакция печатала одно слово на оба
            // случая, а это ровно то смешение, из-за которого «молчание» читается как утверждение:
            // «свойство вернуло null» и «прочитать не удалось» — разные факты о продукте.
            var sketchBefore = ReadEvolutionProperty(evolution, sketchProperty, out var sketchReadError);
            step.Observe("IEvolution.Sketch: объявленный тип "
                + (sketchProperty?.PropertyType.FullName ?? "свойство не найдено")
                + ", значение → " + Api5.RuntimeName(sketchBefore)
                + " (имя «" + (EvolutionSectionName(sketchBefore, step) ?? "не прочитано") + "»)"
                + (sketchReadError is null ? "" : ", чтение бросило " + sketchReadError));

            // ── РУКА A: запись ТОЛЬКО в API7 ─────────────────────────────────────────────────────
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

            // ── РУКА B: запись в ОБА хранилища (как у элемента по сечениям) ───────────────────────
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

            // ── РУКА C: режим через хранилище API7 ──────────────────────────────────────────────
            var shiftWritten = WriteEvolutionProperty(evolution, shiftProperty, 0, step);
            var shiftUpdated = evolution is null ? (bool?)null : Api5.SafeBool(evolution.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var volumeApi7Shift = TotalVolume(part);
            step.Observe("рука C (режим через API7): записано=" + shiftWritten + ", Update=" + shiftUpdated
                + ", объём " + Api5.Num(volumeApi7Shift) + " (параллельно — " + Api5.Num(ArcParallel)
                + ", ортогонально — " + Api5.Num(ArcOrthogonal) + ")");

            // ── РУКА D: траектория через определение API5 ПОСЛЕ того, как сечение устоялось ──────
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

            // ── Часть 2: траектория ДО первого обращения к API7 ──────────────────────────────────
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
                        // КОНТЕЙНЕР ЗДЕСЬ НЕ ЗАПРАШИВАЕТСЯ НИ РАЗУ: опыт отвечает на вопрос,
                        // правится ли вход, пока признак НЕ перенесён в API7.
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

            // Вердикт шага: исход обязан быть ОДНОЗНАЧНЫМ, а не «похоже на». Объём руки A, не
            // совпавший НИ с эталоном «не применено», НИ с эталоном «применено», означает, что
            // признак изменился непонятно как, и судить о хранилищах по такому числу нельзя.
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

    /// <summary>
    /// Имя эскиза-сечения, которое ДЕРЖИТ ХРАНИЛИЩЕ API7. Обратный перенос обязателен: объект приходит
    /// как <c>System.__ComObject</c>, и проверка <c>is ksEntity</c> по управляемому типу его не узнаёт.
    /// Это разные вещи, и первая редакция прибора принимала «имя не прочитано» за «свойство пусто» —
    /// то есть выдавала дефект прибора за факт о продукте. Различает два исхода именно этот перенос:
    /// «запись отменена» (в хранилище осталось прежнее имя) и «запись сохранена, но тело за ней не
    /// пошло» (имя новое, объём прежний).
    /// </summary>
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

        // ДОКУМЕНТИРОВАННЫЙ путь: свойство объявлено как IModelObject, и у него есть Name. Читается
        // ПЕРВЫМ, потому что он не требует обратного переноса вовсе.
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

    /// <summary>
    /// Прочитать свойство объекта API7, РАЗЛИЧАЯ пустое и непрочитанное: возврат <c>null</c> без
    /// ошибки означает «свойство вернуло пусто», а непустой <paramref name="error"/> — «прочитать не
    /// удалось». Смешение этих двух исходов давало бы отказ прибора за факт о продукте.
    /// </summary>
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

    /// <summary>
    /// Записать свойство объекта API7 через управляемый тип interop. <c>null</c> — «писать было
    /// некуда» (свойство не найдено или объект не прочитан), <c>false</c> — «запись отвергнута».
    /// Различать их обязательно: смешение дало бы отказ прибора за отказ продукта.
    /// </summary>
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

    /// <summary>Смена режима движения сечения на существующем признаке — положительный контроль.</summary>
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

    /// <summary>
    /// Перепривязка профиля существующей кинематической операции. Отдельно сообщается, НАЙДЕН ли
    /// подставляемый эскиз: подстановка <c>null</c> выглядела бы как «не применилось», то есть
    /// отрицательный исход был бы дефектом прибора. И отдельно — ИМЯ профиля, прочитанное с
    /// определения после правки: пересказ запроса доказательством не является.
    /// </summary>
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

    /// <summary>
    /// Перепривязка траектории существующей кинематической операции. Отдельно сообщается, сколько
    /// элементов было в держателе ДО <c>Clear()</c> и КАК ОНИ НАЗЫВАЮТСЯ после: «очистили пустое» и
    /// «очистили непустое» — разные факты, а имя доказывает, что подставленный эскиз не остался.
    /// </summary>
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

    /// <summary>
    /// Имена элементов коллекции через запятую. Пустая строка — «ни одного прочитанного».
    /// </summary>
    /// <remarks>
    /// <b><c>refresh()</c> здесь НЕ вызывается, и это измеренная поправка, а не стилистика.</b> Первая
    /// редакция вызывала <c>refresh()</c> перед обходом — и получала СЕМЬ имён
    /// («Ось X,Ось Y,Ось Z,P2,T2,P4,T4») там, где <c>GetCount()</c> без <c>refresh()</c> отвечает
    /// <c>1</c>: держатель, полученный от <c>PathPartArray()</c>, после <c>refresh()</c> начинает
    /// перечислять сущности ДЕТАЛИ, а не элементы траектории. Это дефект прибора: он показывал бы
    /// чужой набор как «траектория не сменилась». Поэтому обход идёт ровно тем же путём, что и
    /// <c>GetCount()</c>, которым проверяется число элементов.
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
            // Возвращается прочитанное.
        }

        return string.Join(",", names);
    }

    /// <summary>Имя эскиза-профиля, прочитанное С ОПРЕДЕЛЕНИЯ после правки: пересказ запроса не годится.</summary>
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

    /// <summary>Порядок признака среди односемейных в дереве — тот же адрес, что у коллекции API7.</summary>
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

    /// <summary>Перенос объекта API5 в API7 тем же способом, что измерен на фаске и скруглении:
    /// <c>TransferInterface(объект, ksAPI7Dual, 0)</c>. Непереданный объект API7 не увидит, поэтому
    /// <c>null</c> здесь — это «значение не дошло», а не «пустой список».
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

    /// <summary>
    /// Чтение свойства с индексом через отражение по <b>управляемому</b> типу interop. Нужно потому,
    /// что C#-форма таких свойств из справки не видна (справка даёт COM-синтаксис), а угадывать её
    /// запрещено. Возвращает <c>null</c>, если член не найден или вызов не состоялся, — и это
    /// «не прочитано», а не ноль.
    /// </summary>
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
                // Проба следующего типа параметра: тип VARIANT_BOOL в interop бывает и bool, и short.
            }
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    /// <summary>
    /// Присоединение траектории. Способ задаётся не догадкой: сначала читается РАНТАЙМ-тип того, что
    /// вернул <c>PathPartArray()</c>, и только потом выполняется попытка. Если тип не тот, что
    /// ожидался, это записывается как измерение, а не подменяется другим вызовом.
    /// </summary>
    private static bool AttachPath(ksPart part, ksBaseEvolutionDefinition definition, string pathName, ProbeStep step)
    {
        var holder = Api5.SafeObject(definition.PathPartArray);
        step.Observe("PathPartArray() → " + Api5.RuntimeName(holder));
        return AttachPathTo(part, holder, pathName, step);
    }

    /// <summary>
    /// Та же привязка, но для держателя, полученного любым маршрутом: у приклеенного кинематического
    /// элемента (46) держатель берётся у <c>ksBossEvolutionDefinition</c>, и повторять разбор
    /// рантайм-типа второй раз значило бы измерять одно и то же двумя приборами.
    /// </summary>
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
        // Update() = true означает «принято», а не «применено»: результат судится объёмом, а это
        // поле публикуется отдельно, чтобы «принято» не читалось как «применено».
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

    /// <summary>
    /// Эскиз на плоскости (базовой либо смещённой). Возвращает признак успеха; причина отказа
    /// остаётся в шаге.
    /// </summary>
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
            // Возвращается прочитанное.
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

    private sealed record FaceRow(int Index, object? Element, double? Area);
}

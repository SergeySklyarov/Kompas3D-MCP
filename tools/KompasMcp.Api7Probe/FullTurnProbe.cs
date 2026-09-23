using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// F.1 — правда ли, что развёртка вращения насыщается на 180° и полный оборот недостижим.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему этот опыт нужен отдельно.</b> Вращение SM-03 создаётся прямой фабрикой API7
/// (<c>IModelContainer.Rotateds.Add</c>), и запись <c>Angle[true] = 360</c> давала объём, равный
/// ПОЛОВИНЕ цилиндра. Из этого был сделан вывод «развёртка насыщается на 180°», он попал в схему
/// (<c>schemas/kompas_rotated.json</c>, <c>maximum: 180</c>), в адаптер
/// (<c>ValidateRotatedCommand</c>) и в строку покрытия как факт о продукте. При этом сама матрица
/// углов, на которой вывод держится, измеряла только <c>CutOffByPoint</c> — член, и <b>ни один</b>
/// угол в ней не был записан как <c>360</c> при полном наборе параметров развёртки.
/// </para>
/// <para>
/// <b>Что здесь измеряется.</b> Профиль в этом опыте устроен так, что полный оборот и полуоборот
/// РАЗЛИЧАЮТСЯ ОБЪЁМОМ при любой оси и любом направлении: ось лежит на <b>границе</b> профиля, то
/// есть развёртка на 360° даёт цилиндр π·r²·h, а на 180° — его половину. Прошлая матрица этого не
/// гарантировала: её профиль тоже стоял одной стороной на оси, но запись 360 всё равно давала
/// половину, и различить «насыщение» от «читается второй слот» по одному числу было нельзя.
/// </para>
/// <para>
/// <b>Гипотеза, которая проверяется.</b> <c>IRotated.Angle</c> — индексированная пара
/// <c>Angle(Boolean Normal)</c>. Прошлый опыт писал <c>Angle[true] = 360, Angle[false] = 0</c>. Если
/// ядро читает угол со стороны, противоположной той, где лежит материал, то корректная запись
/// полного оборота — <c>Angle[true] = 360, Angle[false] = 360</c> с <c>Direction = dtBoth</c>: это
/// ровно то, что делает <b>файл поставки</b> <c>BEARING 410</c> (R.22: <c>Angle[true]=180,
/// Angle[false]=180, Direction=dtBoth</c>). Три величины различаются по объёму и потому не могут
/// быть спутаны: 360/0, 180/180 и 360/360.
/// </para>
/// <para>
/// <b>Что опыт НЕ делает.</b> Он не меняет продукт и не пишет ни в одну строку покрытия: он
/// измеряет. Вывод опыта — либо маршрут полного оборота (тогда его переносят в адаптер), либо
/// подтверждённое насыщение (тогда статус строки понижается). Оба исхода законны; недопустим только
/// третий — оставить закрытую строку, доказательство которой опровергнуто.
/// </para>
/// </remarks>
internal sealed class FullTurnProbe
{
    private const double RadiusMm = 20d;
    private const double HeightMm = 40d;
    private const short AxisStyle = 6;

    private static double FullTurnVolume => Math.PI * RadiusMm * RadiusMm * HeightMm;

    private static double HalfTurnVolume => FullTurnVolume / 2d;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private int _ownPid;

    public FullTurnProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        const string stem = "full-turn";
        report.Flush(
            Path.Combine(options.ReportDir, stem + ".json"),
            Path.Combine(options.ReportDir, stem + ".md"));
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
            AnglePairMatrix();
            BossHalfAngleRoute();
            ChangeOnSameFeature();
            PersistenceAndReopen();
            DirectionMatrix();
            BossOntoPlate();
            BossProtrudingReference();
            CutFullTurnOnPlate();
            CutFullTurnContained();
            BossUnionOnPlate();
            MultiBodyTarget();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("F.0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        _clock.Restart();
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

        step.Observe("создан экземпляр API5 за " + _clock.ElapsedMilliseconds + " мс");
        step.Observe("процессов KOMPAS.exe было до запуска: " + _pidsBefore.Count);

        var document = (ksDocument3D)_app.Document3D();
        _doc = document;
        document.Create(true, true);
        step.Observe("создан невидимый документ-деталь");
        step.Pass("сеанс поднят");
    }

    /// <summary>Processes present now that were absent before this run started.</summary>
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

    private void Shutdown()
    {
        var step = _report.Begin("F.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _doc?.close();
        }
        catch (Exception ex)
        {
            step.Observe("документ не закрылся: " + ex.GetType().Name);
        }

        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        // `Quit()` is asynchronous: the local server may still be winding down when the call
        // returns, and a single sample of the process list then reports a teardown race as "the
        // process outlived Quit()" — the instrument describing itself. Wait, bounded, instead.
        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 10000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        // Only a process that did NOT exist before the run may be judged a leak.
        var left = NewProcesses();
        if (waited > 0)
        {
            step.Observe("процессы ушли за " + waited + " мс после Quit()");
        }

        step.Observe("своих процессов до запуска: " + _pidsBefore.Count
            + ", новых после: " + left.Count + (left.Count == 0 ? string.Empty : " (" + string.Join(", ", left) + ")"));
        if (_ownPid != 0 && left.Contains(_ownPid))
        {
            step.Fail("свой процесс " + _ownPid + " пережил Quit()");
            return;
        }

        step.Pass("новых процессов не осталось");
    }

    // ══════════════════════════════════════════════════════════ experiments ══

    /// <summary>
    /// The angle-pair matrix: three writes that can be told apart by volume alone.
    /// </summary>
    /// <remarks>
    /// Every case builds in its OWN document, so a number cannot be inherited from the previous case.
    /// The reading is the volume, and the three expectations are distinct: a full turn is
    /// π·r²·h = 50265.4824574367, a half turn is half of it, and nothing is <c>null</c>.
    /// </remarks>
    private void AnglePairMatrix()
    {
        var step = _report.Begin("F.1", "Пара Angle(true/false): какая запись даёт полный оборот",
            "Даёт ли Angle[true]=360, Angle[false]=360 с dtBoth полный цилиндр вместо половины?");

        var cases = new (string Label, double Normal, double Reverse, ksDirectionTypeEnum Direction)[]
        {
            ("Angle[true]=360, Angle[false]=0, dtNormal — как писал прошлый опыт",
                360d, 0d, ksDirectionTypeEnum.dtNormal),
            ("Angle[true]=360, Angle[false]=360, dtBoth — как в файле поставки BEARING 410",
                360d, 360d, ksDirectionTypeEnum.dtBoth),
            ("Angle[true]=180, Angle[false]=180, dtBoth — полуоборот с обеими сторонами",
                180d, 180d, ksDirectionTypeEnum.dtBoth),
            ("Angle[true]=360, Angle[false]=360, dtNormal",
                360d, 360d, ksDirectionTypeEnum.dtNormal),
            ("Angle[true]=90, Angle[false]=90, dtBoth — четверть, контроль линейности",
                90d, 90d, ksDirectionTypeEnum.dtBoth),
        };

        var results = new List<(string Label, string Reading)>();
        foreach (var (label, normal, reverse, direction) in cases)
        {
            var reading = BuildInOwnDocument(normal, reverse, direction, step);
            var step2 = _report.Begin("F.1" + (char)('a' + results.Count), "Вариант угла: " + label,
                "Какой объём даёт эта запись пары Angle?");
            try
            {
                step2.Observe(reading);
                step2.Pass("измерено: " + ClassifyVolume(reading));
            }
            catch (Exception ex)
            {
                step2.Fail("бросил " + Describe(ex));
            }

            results.Add((label, reading));
        }

        step.Observe(string.Join(" || ", results.Select(r => r.Label + " → " + r.Reading)));

        // The verdict of the step is the FALSIFIABILITY of the old claim, not a preference for a
        // route: if any write produced a full cylinder the saturation claim is refuted; if every write
        // produced half a cylinder it holds for this profile and this route.
        var fullCylinder = results.Any(r => ClassifyVolume(r.Reading).StartsWith("ПОЛНЫЙ", StringComparison.Ordinal));

        if (fullCylinder)
        {
            step.Pass("насыщение ОПРОВЕРГНУТО: полный цилиндр π·r²·h получен — маршрут полного оборота "
                + "существует, и он назван в наблюдениях");
        }
        else
        {
            step.Pass("насыщение подтверждено для этого профиля и этого маршрута: ни одна из "
                + "перебранных записей пары Angle не дала полного цилиндра");
        }

        // ── the same profile and axis, but the OTHER factory kind ────────────────────────────────
        //
        // The previous probe's reference route (R.25) built with `o3d_bossRotated`, not `o3d_baseRotated`,
        // and it is that route whose 360° write produced half a cylinder. If the kind is what changes the
        // angle's meaning, the two readings must diverge here on an identical profile and axis; if they
        // agree, the kind is not the explanation and the difference lay elsewhere.
        var boss = _report.Begin("F.1f", "Тот же профиль и ось, но фабрика boss против base",
            "Даёт ли o3d_bossRotated при 360° ту же геометрию, что o3d_baseRotated?");
        var baseReading = BuildInOwnDocument(360d, 0d, ksDirectionTypeEnum.dtNormal, boss);
        var bossReading = BuildInOwnDocument(360d, 0d, ksDirectionTypeEnum.dtNormal, boss, ksObj3dTypeEnum.o3d_bossRotated);
        boss.Observe("o3d_baseRotated при 360/0 dtNormal → " + baseReading);
        boss.Observe("o3d_bossRotated при 360/0 dtNormal → " + bossReading);

        var sameGeometry = ClassifyVolume(baseReading).StartsWith("ПОЛНЫЙ", StringComparison.Ordinal)
            && ClassifyVolume(bossReading).StartsWith("ПОЛНЫЙ", StringComparison.Ordinal);
        var bothHalf = ClassifyVolume(baseReading).StartsWith("ПОЛОВИНА", StringComparison.Ordinal)
            && ClassifyVolume(bossReading).StartsWith("ПОЛОВИНА", StringComparison.Ordinal);
        boss.Pass(sameGeometry
            ? "вид фабрики на геометрию при 360° не влияет: обе дали полный цилиндр"
            : bothHalf
                ? "обе дали половину — вид фабрики тоже не объясняет разницу"
                : "виды РАСХОДЯТСЯ по геометрии при одном и том же профиле, оси и угле — это и есть "
                    + "причина прежнего чтения «половина»");
    }

    /// <summary>
    /// The change 360 → 180 → 360 on the SAME feature, which is what the task file asks for.
    /// </summary>
    private void ChangeOnSameFeature()
    {
        var step = _report.Begin("F.2", "Смена угла у ТОГО ЖЕ признака: 360 → 180 → 360",
            "Меняется ли геометрия признака при смене угла, а не только записанное число?");

        var case2 = _report.Begin("F.2a", "Признак и плита для смены угла", "Ставится ли опыт?");
        ksDocument3D doc;
        ksPart part;
        KompasAPI7.IRotated rotation;
        IAxis3D? axis;
        try
        {
            doc = NewPart(out part);
            var sketch = ProfileSketch(doc, "F2-profile");
            axis = BuildAxis(doc, part, case2);
            if (axis is null)
            {
                throw new InvalidOperationException("ось вращения не построена — опыт не поставлен");
            }

            rotation = CreateRotation(part, doc, sketch, axis, 360d, 0d, ksDirectionTypeEnum.dtNormal, case2)
                ?? throw new InvalidOperationException("признак не создан");
            case2.Pass("признак создан: 360/0 dtNormal, ожидание полного цилиндра");
        }
        catch (Exception ex)
        {
            case2.Fail("постановка опыта не удалась: " + Describe(ex));
            return;
        }

        try
        {
            var v360 = Api5.Volume(part);
            step.Observe("исходный угол: V=" + Api5.Num(v360) + " (ожидание полного оборота "
                + Api5.Num(FullTurnVolume) + ", полуоборота " + Api5.Num(HalfTurnVolume) + ")");
            step.Observe("  форма: " + DescribeShape(part, step));

            SetAngle(doc, part, rotation, 180d, 0d, step, "180/0");
            var v180 = Api5.Volume(part);
            step.Observe("после 180/0: V=" + Api5.Num(v180) + " (снято с признака="
                + Api5.Num(Diff(v360, v180)) + ")");
            step.Observe("  форма: " + DescribeShape(part, step));

            SetAngle(doc, part, rotation, 360d, 0d, step, "360/0");
            var v360b = Api5.Volume(part);
            step.Observe("снова 360/0: V=" + Api5.Num(v360b) + " (снято с признака="
                + Api5.Num(Diff(v180, v360b)) + ")");

            var wentDown = v360 is not null && v180 is not null
                && v180.Value < v360.Value - Tolerance(v360.Value);
            var cameBack = v360 is not null && v360b is not null
                && Math.Abs(v360b.Value - v360.Value) <= Tolerance(v360.Value);

            if (wentDown && cameBack)
            {
                step.Pass("угол действительно управляет признаком: 360→180 уменьшило объём, "
                    + "180→360 вернуло прежний");
                return;
            }

            step.Fail("смена угла не подтверждена как управляющая признаком: 360→180 уменьшило="
                + wentDown + ", 180→360 вернуло=" + cameBack);
        }
        catch (Exception ex)
        {
            step.Fail("опыт бросил " + Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Save → close → reopen with the angle re-read from the reopened model.
    /// </summary>
    private void PersistenceAndReopen()
    {
        var step = _report.Begin("F.3", "Сохранение и переоткрытие полного оборота",
            "Переживает ли полный оборот закрытие документа и читается ли угол заново?");
        var path = Path.Combine(_options.WorkDir, "F3-full-turn.m3d");

        ksDocument3D doc;
        ksPart part;
        KompasAPI7.IRotated rotation;
        try
        {
            doc = NewPart(out part);
            var sketch = ProfileSketch(doc, "F3-profile");
            var axis = BuildAxis(doc, part, step)
                ?? throw new InvalidOperationException("ось вращения не построена — опыт не поставлен");
            rotation = CreateRotation(part, doc, sketch, axis, 360d, 360d, ksDirectionTypeEnum.dtBoth, step)
                ?? throw new InvalidOperationException("признак не создан");
        }
        catch (Exception ex)
        {
            step.Fail("постановка опыта не удалась: " + Describe(ex));
            return;
        }

        try
        {
            var before = Api5.Volume(part);
            var angleBefore = Api5.SafeDouble(() => rotation.Angle[true]);
            var angleReverseBefore = Api5.SafeDouble(() => rotation.Angle[false]);
            step.Observe("до save: V=" + Api5.Num(before) + ", Angle[true]=" + Api5.Num(angleBefore)
                + ", Angle[false]=" + Api5.Num(angleReverseBefore));

            var saved = Api5.SafeBool(() => doc.SaveAs(path));
            var closed = Api5.SafeBool(() => doc.close());
            step.Observe("save→close: SaveAs=" + Api5.Raw(saved) + " (" + Path.GetFileName(path)
                + "), close=" + Api5.Raw(closed));

            if (saved != true)
            {
                step.Fail("SaveAs отказал — переоткрытие непоставимо");
                return;
            }

            var reader = (ksDocument3D)_app.Document3D();
            try
            {
                var opened = Api5.SafeBool(() => reader.Open(path, true));
                step.Observe("reopen: Open=" + Api5.Raw(opened));
                if (opened != true || reader.GetPart(-1) is not ksPart reopened)
                {
                    step.Fail("файл не открылся заново");
                    return;
                }

                var after = Api5.Volume(reopened);
                step.Observe("reopen: тел " + Api5.BodyCount(reopened) + ", V=" + Api5.Num(after)
                    + " (до save=" + Api5.Num(before) + ")");
                step.Observe("  форма: " + DescribeShape(reopened, step));

                var held = before is not null && after is not null
                    && Math.Abs(after.Value - before.Value) <= Tolerance(before.Value);
                if (held)
                {
                    step.Pass("объём пережил переоткрытие: " + Api5.Num(after));
                }
                else
                {
                    step.Fail("объём не совпал после переоткрытия: до=" + Api5.Num(before)
                        + ", после=" + Api5.Num(after));
                }
            }
            finally
            {
                TryClose(reader);
            }
        }
        catch (Exception ex)
        {
            step.Fail("опыт бросил " + Describe(ex));
        }
    }

    /// <summary>
    /// The direction matrix at the angle that produced the largest volume.
    /// </summary>
    /// <remarks>
    /// A direction that does not build is a fact about that direction and is reported as such, never
    /// compared as if it were a position — the same rule R.26.sector had to learn when its first
    /// version read a failed build as a moved sector.
    /// </remarks>
    private void DirectionMatrix()
    {
        var step = _report.Begin("F.4", "Направление при полном обороте",
            "Строит ли полный оборот какое-нибудь направление, кроме normal?");

        var directions = new[]
        {
            ksDirectionTypeEnum.dtNormal,
            ksDirectionTypeEnum.dtReverse,
            ksDirectionTypeEnum.dtBoth,
            ksDirectionTypeEnum.dtMiddlePlane,
        };

        foreach (var direction in directions)
        {
            var reading = BuildInOwnDocument(360d, 360d, direction, step);
            step.Observe("Direction=" + direction + " при 360/360 → " + reading);
        }

        step.Pass("направления перебраны при полном обороте — что построилось, сказано выше");
    }

    // ══════════════════════════════════════════════════════════════ building ══

    /// <summary>
    /// Решает ли вид фабрики и сторона оси, что означает записанный угол.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Что здесь проверяется.</b> Прежний эталонный маршрут (R.25) строил вращение фабрикой
    /// <c>o3d_bossRotated</c> и записывал <c>Angle[true] = 360, Angle[false] = 0</c>; объём выходил
    /// ровно <c>π·r²·h</c>. Затем на ТОМ ЖЕ признаке угол менялся на <c>180</c> — и объём падал
    /// ровно вдвое. Из этого был сделан вывод «развёртка насыщается на 180°».
    /// </para>
    /// <para>
    /// <b>Почему вывод мог быть неверен.</b> Достаточно, чтобы материал признака стоял по ДРУГУЮ
    /// сторону оси, чем в опыте F.1: тогда один и тот же записанный угол отсчитывается в другую
    /// сторону, и «четверть» и «половина» меняются местами. Опыт F.2 показал ровно этот эффект —
    /// там плоскость эскиза F.1 развернула ось так, что <c>360</c> дало четверть, а <c>180</c> —
    /// половину. Значит сторона оси решает, и прежнее чтение «180 = половина» могло быть чтением
    /// «180 = полный оборот по другую сторону оси».
    /// </para>
    /// <para>
    /// Здесь обе стороны перебираются ЯВНО, одной и той же парой точек, различающейся только знаком
    /// координаты: это исключает вопрос о том, куда смотрит нормаль плоскости эскиза.
    /// </para>
    /// </remarks>
    private void BossHalfAngleRoute()
    {
        var step = _report.Begin("F.5", "Сторона оси: как читается угол по разные её стороны",
            "Зависит ли смысл записанного угла от того, по какую сторону оси стоит материал?");

        foreach (var (label, sign) in new[] { ("v от −20 до +20 (как в F.1)", 1d), ("v от +20 до −20 (обратный порядок точек)", -1d) })
        {
            var reading360 = BuildWithSignedAxis(360d, 0d, sign, step);
            var reading180 = BuildWithSignedAxis(180d, 0d, sign, step);
            step.Observe(label + ": при 180/0 → " + reading180);
            step.Observe(label + ": при 360/0 → " + reading360);
        }

        step.Pass("обе стороны оси измерены при 180° и 360° — что читается, сказано выше");
    }

    /// <summary>Строит вращение с осью, направленной по указанному знаку координаты v.</summary>
    private string BuildWithSignedAxis(
        double angleNormal, double angleReverse, double sign, ProbeStep host)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(out var part);
            var sketch = ProfileSketch(doc, "F5-profile");
            var axis = BuildAxis(doc, part, host, sign);
            if (axis is null)
            {
                return "ось не построена";
            }

            var rotation = CreateRotation(
                part, doc, sketch, axis, angleNormal, angleReverse, ksDirectionTypeEnum.dtNormal, host);
            if (rotation is null)
            {
                return "признак не создан";
            }

            var volume = Api5.Volume(part);
            return "тел=" + Api5.BodyCount(part) + ", V=" + Api5.Num(volume)
                + " (" + ClassifyVolumeOf(volume) + ")";
        }
        catch (Exception ex)
        {
            return "бросил " + Describe(ex);
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// A boss built ONTO an existing extruded plate, using this probe's own working builders.
    /// </summary>
    /// <remarks>
    /// This case exists because the separate BossFuseProbe could not build a boss at all — its
    /// instrument was defective, which the empty-document control exposed. Here the profile, axis and
    /// creation code are the ones already measured to produce a full cylinder (F.1a/F.1f), so a
    /// failure here is a statement about the product rather than about the probe.
    /// </remarks>
    private void BossOntoPlate()
    {
        var step = _report.Begin("F.6", "Бобышка на готовую плиту (рабочий прибор этого опыта)",
            "Сращивается ли вращение-бобышка с телом, когда прибор заведомо исправен?");

        const double plateHalf = 60d;
        const double plateThickness = 10d;
        var plateVolume = (2d * plateHalf) * (2d * plateHalf) * plateThickness;
        var protruding = BossFusedDelta(plateThickness);

        step.Observe("ожидание ДО опыта: плита " + (2 * plateHalf) + "×" + (2 * plateHalf) + "×"
            + plateThickness + " V=" + Api5.Num(plateVolume));
        step.Observe("  ИСПРАВЛЕНО 18.09.2026: ось цилиндра лежит В плоскости верхней грани плиты"
            + " (BuildAxis даёт ось вдоль Y при z=0), плита занимает z∈[0," + plateThickness + "]."
            + " Прежнее ожидание π·R²·(H−t)=" + Api5.Num(Math.PI * RadiusMm * RadiusMm * (HeightMm - plateThickness))
            + " описывало цилиндр, СТОЯЩИЙ на плите, которого этот прибор не строит.");
        step.Observe("  пересечение = сечение z∈[0," + plateThickness + "], площадь "
            + Api5.Num(BossFusedSlabArea(plateThickness)) + " × длина " + Api5.Num(HeightMm)
            + " = " + Api5.Num(BossFusedSlabArea(plateThickness) * HeightMm));
        step.Observe("  СЛИЛОСЬ → V=" + Api5.Num(plateVolume + protruding)
            + " (ΔV=" + Api5.Num(protruding) + "), тел 1→1");
        step.Observe("  ВТОРОЕ ТЕЛО → V=" + Api5.Num(plateVolume + FullTurnVolume) + ", тел 1→2");

        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(out var part);
            var plate = Api5.BasePlate(part, 2 * plateHalf, 2 * plateHalf, plateThickness, step, "F6");
            if (plate is null)
            {
                step.Fail("плита не построена");
                return;
            }

            var volumeBefore = Api5.Volume(part);
            var bodiesBefore = Api5.BodyCount(part);
            step.Observe("до бобышки: тел=" + bodiesBefore + ", V=" + Api5.Num(volumeBefore));

            var sketch = ProfileSketch(doc, "F6-profile");
            var axis = BuildAxis(doc, part, step);
            if (axis is null)
            {
                step.Fail("ось не построена");
                return;
            }

            var rotation = CreateRotation(part, doc, sketch, axis, 360d, 0d,
                ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_bossRotated);
            if (rotation is null)
            {
                step.Fail("признак бобышки не создан (Update() не подтвердил)");
                return;
            }

            var volumeAfter = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);
            var delta = Diff(volumeBefore, volumeAfter);

            step.Observe("после бобышки: тел=" + bodiesBefore + "→" + bodiesAfter
                + ", V=" + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                + ", изменение=" + Api5.Num(delta));
            step.Observe("форма: " + DescribeShape(part, step));

            // The four outcomes are named separately so the report states WHICH one happened rather
            // than leaving a bare number for a reader to interpret.
            var fused = delta is not null && Math.Abs(delta.Value - protruding) <= Tolerance(protruding)
                && bodiesAfter == bodiesBefore;
            var wholeAsSecondBody = delta is not null
                && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume)
                && bodiesAfter > bodiesBefore;
            var noGain = delta is not null && Math.Abs(delta.Value) <= Tolerance(1d);

            if (fused)
            {
                step.Pass("СРАЩИВАНИЕ ЕСТЬ: прирост " + Api5.Num(delta) + " = объём тела минус "
                    + "пересечение " + Api5.Num(protruding) + ", тел " + bodiesAfter + " (одно тело)");
            }
            else if (wholeAsSecondBody)
            {
                step.Observe("прирост равен ЦЕЛОМУ цилиндру " + Api5.Num(FullTurnVolume)
                    + " и тел стало " + bodiesAfter + " — материал добавлен ВТОРЫМ ТЕЛОМ, "
                    + "сращивания с плитой не произошло");
                step.Fail("сращивания нет: признак построен, но как отдельное тело (тел "
                    + bodiesBefore + "→" + bodiesAfter + ")");
            }
            else if (noGain)
            {
                step.Fail("прироста нет при непустой части тела вне плиты "
                    + Api5.Num(FullTurnVolume - protruding) + " — сращивание не произошло");
            }
            else
            {
                step.Fail("исход не назван: изменение " + Api5.Num(delta)
                    + ", тел " + bodiesBefore + "→" + bodiesAfter);
            }
        }
        catch (Exception ex)
        {
            step.Fail("опыт бросил " + Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// The work order's reference for native fusion: plate x,y∈[−60,60], z∈[0,10]; the cylinder
    /// stands ON the plate (z∈[0,40]) and protrudes 30 mm, so a correct union adds π·20²·30.
    /// </summary>
    /// <remarks>
    /// This differs from <see cref="BossOntoPlate"/> in the axis position: there the profile spans
    /// v∈[−20,20] and the cylinder ends up centred on the origin; here the profile spans v∈[0,40] and
    /// the axis runs along Y from the origin. In BOTH cases the axis lies in the sketch plane (z=0),
    /// which is the plane of the plate's upper face — so the intersection is the cylinder slab
    /// z∈[0,10], and the expected gain is <see cref="BossFusedDelta"/>, not π·R²·(H−t).
    /// </remarks>
    private void BossProtrudingReference()
    {
        var step = _report.Begin("F.7", "Эталон наряда: выступ 30 мм над плитой",
            "Добавляет ли родная бобышка ровно выступающую часть?");

        const double plateHalf = 60d;
        const double plateThickness = 10d;
        var plateVolume = (2d * plateHalf) * (2d * plateHalf) * plateThickness;
        var protruding = BossFusedDelta(plateThickness);

        step.Observe("ожидание ДО опыта: плита 120×120×10 V=" + Api5.Num(plateVolume));
        step.Observe("  ИСПРАВЛЕНО 18.09.2026: ось цилиндра (BuildAxisSpan, точки (0,0,0)→(0,40,0)) лежит"
            + " В плоскости верхней грани плиты, а не перпендикулярно ей. Пересечение — сечение"
            + " z∈[0," + plateThickness + "], а не «выступ 30 мм». Прежнее ожидание "
            + Api5.Num(Math.PI * RadiusMm * RadiusMm * (HeightMm - plateThickness))
            + " описывало цилиндр, стоящий на плите.");
        step.Observe("  ΔV при сращивании = " + Api5.Num(protruding)
            + " = " + Api5.Num(FullTurnVolume) + " − "
            + Api5.Num(BossFusedSlabArea(plateThickness) * HeightMm) + " (пересечение)");
        step.Observe("  СЛИЛОСЬ → V=" + Api5.Num(plateVolume + protruding) + " (тел 1→1)");
        step.Observe("  ВТОРОЕ ТЕЛО → V=" + Api5.Num(plateVolume + FullTurnVolume) + " (тел 1→2)");

        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(out var part);
            var plate = Api5.BasePlate(part, 2 * plateHalf, 2 * plateHalf, plateThickness, step, "F7");
            if (plate is null)
            {
                step.Fail("плита не построена");
                return;
            }

            var volumeBefore = Api5.Volume(part);
            var bodiesBefore = Api5.BodyCount(part);
            step.Observe("до бобышки: тел=" + bodiesBefore + ", V=" + Api5.Num(volumeBefore));

            // Profile spans v∈[0,40] and u∈[0,20], axis along Y at (0,0,0)→(0,40,0): the sweep is a
            // full cylinder R20 of length 40 whose axis lies IN the sketch plane z=0.
            var sketch = ProfileSketchSpan(doc, "F7-profile", 0d, HeightMm);
            var axis = BuildAxisSpan(doc, part, 0d, HeightMm, step);
            if (axis is null)
            {
                step.Fail("ось не построена");
                return;
            }

            var rotation = CreateRotation(part, doc, sketch, axis, 360d, 0d,
                ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_bossRotated);
            if (rotation is null)
            {
                step.Fail("признак бобышки не создан (Update() не подтвердил)");
                return;
            }

            var volumeAfter = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);
            var delta = Diff(volumeBefore, volumeAfter);

            step.Observe("после бобышки: тел=" + bodiesBefore + "→" + bodiesAfter
                + ", V=" + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                + ", изменение=" + Api5.Num(delta));
            step.Observe("форма: " + DescribeShape(part, step));

            if (delta is not null && Math.Abs(delta.Value - protruding) <= Tolerance(protruding)
                && bodiesAfter == bodiesBefore)
            {
                step.Pass("СРАЩИВАНИЕ ЕСТЬ: прирост " + Api5.Num(delta) + " = объём тела минус "
                    + "пересечение " + Api5.Num(protruding) + ", тел " + bodiesAfter);
            }
            else if (delta is not null && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume)
                     && bodiesAfter > bodiesBefore)
            {
                step.Observe("прирост равен ЦЕЛОМУ цилиндру и тел стало " + bodiesAfter
                    + " — материал добавлен ВТОРЫМ ТЕЛОМ");
                step.Fail("сращивания нет: признак построен как отдельное тело");
            }
            else
            {
                step.Fail("исход не назван: изменение " + Api5.Num(delta)
                    + ", тел " + bodiesBefore + "→" + bodiesAfter);
            }
        }
        catch (Exception ex)
        {
            step.Fail("опыт бросил " + Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Вырезание вращением на полный оборот: снимает ли <c>o3d_cutRotated</c> при 360° весь
    /// цилиндр, как это делает <c>o3d_baseRotated</c>, или только половину?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Опыт поставлен 18.09.2026 после приёмки SM-03 через MCP. Строка приёмки «cut 360° снимает
    /// цилиндр насквозь» упала: <c>base</c> при 360° дал ровно полный цилиндр
    /// (<c>50265.4824574366</c>), а <c>cut</c> на плите 120×120×40 снял ровно ПОЛОВИНУ
    /// (<c>25132.7412287183</c>) при прочитанном обратно угле 360. Разница между видами операции —
    /// это утверждение о продукте, и оно проверяется здесь на приборе, независимом от адаптера.
    /// </para>
    /// <para>
    /// Разбираются три возможные причины, и опыт различает их, а не выбирает удобную:
    /// <list type="number">
    /// <item><b>форма профиля.</b> В приёмке профиль — прямоугольник u∈[0,20], стоящий ПО ОДНУ
    /// сторону оси. Для <c>cut</c> это могло бы означать, что вырезается только одна половина
    /// развёртки. Проверяется профиль u∈[−20,+20] с осью по его середине: при 360° обе половины
    /// обязаны сойтись в полный цилиндр;</item>
    /// <item><b>пара Angle.</b> Проверяются обе записи: <c>(360,0)</c> и <c>(360,360)</c> — если
    /// разница есть, причина в паре, а не в виде операции;</item>
    /// <item><b>угол как таковой.</b> Тот же <c>cut</c> при 180° обязан снять половину. Если 180° и
    /// 360° снимают одинаково, угол на <c>cut</c> не действует в полном диапазоне — и это
    /// измеренная граница, которую нельзя выдать за поддержку полного оборота.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Ожидание при ярлыке «полный оборот» — <c>V = 576000 − 50265.4824574366</c>, при «половина» —
    /// <c>V = 576000 − 25132.7412287183</c>. Обе величины записаны ДО опыта.
    /// </para>
    /// </remarks>
    private void CutFullTurnOnPlate()
    {
        var step = _report.Begin("F.8", "Вырезание вращением на полный оборот: сколько снимает cut",
            "Снимает ли o3d_cutRotated при 360° весь цилиндр, как base, или только половину?");

        const double plateHalf = 60d;
        const double plateThickness = 40d;
        var plateVolume = (2d * plateHalf) * (2d * plateHalf) * plateThickness;

        step.Observe("плита 120×120×40, V=" + Api5.Num(plateVolume));
        step.Observe("  ПОЛНЫЙ оборот → V=" + Api5.Num(plateVolume - FullTurnVolume)
            + " (снято " + Api5.Num(FullTurnVolume) + ")");
        step.Observe("  ПОЛОВИНА      → V=" + Api5.Num(plateVolume - HalfTurnVolume)
            + " (снято " + Api5.Num(HalfTurnVolume) + ")");

        // Три постановки: профиль по одну сторону оси (как в приёмке) и по обе; пара (360,0) и (360,360).
        var cases = new (string Label, double U0, double U1, double AngleNormal, double AngleReverse)[]
        {
            ("профиль u∈[0,20], пара (360,0) — как в приёмке SM-03", 0d, RadiusMm, 360d, 0d),
            ("профиль u∈[−20,20] по обе стороны оси, пара (360,0)", -RadiusMm, RadiusMm, 360d, 0d),
            ("профиль u∈[0,20], пара (360,360)", 0d, RadiusMm, 360d, 360d),
            ("профиль u∈[0,20], пара (180,0) — контроль половины", 0d, RadiusMm, 180d, 0d),
            ("профиль u∈[0,20], пара (270,0) — между половиной и полным", 0d, RadiusMm, 270d, 0d),
            ("профиль u∈[−20,0] — материал С ДРУГОЙ стороны оси, пара (360,0)",
                -RadiusMm, 0d, 360d, 0d),
        };

        foreach (var (label, u0, u1, angleNormal, angleReverse) in cases)
        {
            ksDocument3D? doc = null;
            try
            {
                doc = NewPart(out var part);
                var plate = Api5.BasePlate(part, 2 * plateHalf, 2 * plateHalf, plateThickness, step, "F8");
                if (plate is null)
                {
                    step.Observe(label + ": плита не построена");
                    continue;
                }

                var volumeBefore = Api5.Volume(part);
                var bodiesBefore = Api5.BodyCount(part);
                step.Observe(label + ": до вырезания — тел=" + bodiesBefore
                    + ", V=" + Api5.Num(volumeBefore)
                    + (Math.Abs((volumeBefore ?? 0d) - plateVolume) <= Tolerance(plateVolume)
                        ? " (плита на месте)" : " (ВНИМАНИЕ: это не ожидаемый объём плиты)"));
                var sketch = ProfileSketchBetween(doc, "F8-profile", u0, u1);
                var axis = BuildAxisSpan(doc, part, 0d, HeightMm, step);
                if (axis is null)
                {
                    step.Observe(label + ": ось не построена");
                    continue;
                }

                var rotation = CreateRotation(part, doc, sketch, axis, angleNormal, angleReverse,
                    ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_cutRotated);
                if (rotation is null)
                {
                    step.Observe(label + ": признак не создан (Update() не подтвердил)");
                    continue;
                }

                var volumeAfter = Api5.Volume(part);
                var change = Diff(volumeBefore, volumeAfter);
                var angleRead = Api5.SafeDouble(() => rotation.Angle[true]);
                var verdict = change is null ? "изменение не измерено"
                    : Math.Abs(change.Value + FullTurnVolume) <= Tolerance(FullTurnVolume)
                        ? "СНЯТ полный оборот"
                        : Math.Abs(change.Value + HalfTurnVolume) <= Tolerance(HalfTurnVolume)
                            ? "снята ПОЛОВИНА оборота"
                            : Math.Abs(change.Value - FullTurnVolume) <= Tolerance(FullTurnVolume)
                                ? "ПРИБАВЛЕН полный оборот (не вырезание!)"
                                : Math.Abs(change.Value) <= Tolerance(1d)
                                    ? "объём не изменился"
                                    : "ни то, ни другое";
                step.Observe(label + ": V=" + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                    + ", изменение=" + Api5.Num(change)
                    + ", угол_прочитан=" + Api5.Num(angleRead)
                    + " → " + verdict);
            }
            catch (Exception ex)
            {
                step.Observe(label + ": бросил " + Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        step.Pass("вырезание измерено во всех постановках — исход назван выше по каждой строке");

        // ── контроль: ТА ЖЕ геометрия, но вид base ───────────────────────────────────────────────
        // Без этого контроля «cut не выражает 360°» смешивалось бы с «профиль u∈[0,20] не выражает
        // 360°». Контроль ставит на ту же плиту base-вращение ТЕМ ЖЕ ПРОФИЛЕМ: если base даёт
        // полный цилиндр при 360° и половину при 180°, различие принадлежит ВИДУ операции, а не
        // геометрии профиля.
        //
        // ДЕФЕКТ ПРИБОРА, ИСПРАВЛЕН 18.09.2026. Первая редакция этого контроля подставляла профиль
        // u∈[0,20] ПОСТОЯННО, независимо от того, какой профиль стоял в контролируемой строке.
        // Для большинства строк совпадение случайно, но для профилей u∈[−20,20] и u∈[−20,0] контроль
        // мерил ДРУГУЮ геометрию и подтверждал не то, что заявлял: «тем же профилем» было описанием
        // намерения, а не поведения. Это тот же класс, что и дефект вида операции в CreateRotation —
        // прибор описывал себя вместо объекта. Ниже профиль контроля ЗЕРКАЛИТ профиль каждой строки.
        foreach (var (angleNormal, u0, u1, label) in new[]
        {
            (360d, 0d, RadiusMm, "360°, профиль u∈[0,20] — как в приёмке SM-03"),
            (180d, 0d, RadiusMm, "180°, профиль u∈[0,20] — контроль половины"),
            (360d, -RadiusMm, RadiusMm, "360°, профиль u∈[−20,20] по обе стороны оси"),
            (360d, -RadiusMm, 0d, "360°, профиль u∈[−20,0] — материал с другой стороны оси"),
        })
        {
            ksDocument3D? doc = null;
            try
            {
                doc = NewPart(out var part);
                var plate = Api5.BasePlate(part, 2 * plateHalf, 2 * plateHalf, plateThickness, step, "F8b");
                if (plate is null)
                {
                    step.Observe("контроль base " + label + ": плита не построена");
                    continue;
                }

                var sketch = ProfileSketchBetween(doc, "F8b-profile", u0, u1);
                var axis = BuildAxisSpan(doc, part, 0d, HeightMm, step);
                if (axis is null)
                {
                    step.Observe("контроль base " + label + ": ось не построена");
                    continue;
                }

                var rotation = CreateRotation(part, doc, sketch, axis, angleNormal, 0d,
                    ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_baseRotated);
                if (rotation is null)
                {
                    step.Observe("контроль base " + label + ": признак не создан");
                    continue;
                }

                var volume = Api5.Volume(part);
                var bodies = Api5.BodyCount(part);
                // Сектор пропорционален доле профиля, реально образующей материал: профиль
                // u∈[−20,20] на полном обороте даёт тот же цилиндр, что и u∈[0,20] (вторая половина
                // ложится на первую), но при НЕПОЛНОМ угле он даёт вдвое больше, потому что
                // развернулись обе половины. Поэтому ожидание — |u1−u0|, нормированное на радиус,
                // с насыщением на 360°.
                var spanRatio = Math.Min(Math.Abs(u1 - u0), RadiusMm) / RadiusMm;
                var expected = plateVolume + FullTurnVolume
                    * (Math.Min(angleNormal, 360d) / 360d) * spanRatio;
                step.Observe("контроль base " + label + ": тел=" + bodies + ", V=" + Api5.Num(volume)
                    + ", ожидание плита+сектор=" + Api5.Num(expected)
                    + (volume is null ? "" : Math.Abs(volume.Value - expected) <= Tolerance(expected)
                        ? " → СОВПАЛО" : " → РАСХОЖДЕНИЕ"));
            }
            catch (Exception ex)
            {
                step.Observe("контроль base " + label + ": бросил " + Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }
    }

    /// <summary>
    /// F.9 — вырезание полным оборотом на плите, ПОЛНОСТЬЮ охватывающей развёрнутое тело.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему прежний опыт F.8 не мог решить этот вопрос.</b> Плита в F.8 строилась
    /// <c>Api5.BasePlate(120,120,40)</c>, то есть выдавливанием от плоскости эскиза В ОДНУ сторону:
    /// <c>z∈[0,40]</c>. Развёрнутое тело того же опыта идёт <c>z∈[−20,20]</c> — развёртка
    /// симметрична относительно плоскости эскиза (измерено F.6/F.7: профиль <c>v∈[0,40]</c> дал
    /// габарит <c>z∈[−20,20]</c>). Значит с плитой пересекается только половина тела
    /// (<c>z∈[0,20]</c>), и «360° снимает половину цилиндра» — ПРАВИЛЬНЫЙ ответ для той постановки,
    /// а не насыщение развёртки. Контроль F.8 сравнивал ПРИБАВЛЕННЫЙ объём целого цилиндра со
    /// СНЯТЫМ объёмом пересечения: это разные величины, и совпадение или расхождение между ними
    /// ничего не говорит о законе развёртки.
    /// </para>
    /// <para>
    /// Здесь тело помещено ВНУТРЬ плиты целиком: ось Z, профиль на плоскости XOZ, плита
    /// <c>z∈[0,40]</c>. Тогда снятый объём равен объёму тела и пропорционален углу.
    /// </para>
    /// </remarks>
    private void CutFullTurnContained()
    {
        var step = _report.Begin("F.9", "Вырезание вращением: плита, ПОЛНОСТЬЮ охватывающая тело",
            "Снимает ли o3d_cutRotated при 360° весь цилиндр, когда цилиндр целиком внутри плиты?");

        const double plateHalf = 60d;
        const double plateThickness = 40d;
        var plateVolume = 2d * plateHalf * (2d * plateHalf) * plateThickness;

        step.Observe("ПОСТАНОВКА (§4 наряда): плита x,y∈[−60,60], z∈[0,40], V=" + Api5.Num(plateVolume));
        step.Observe("  тело: ось Z, R" + Api5.Num(RadiusMm) + ", z∈[0," + Api5.Num(HeightMm)
            + "], профиль на плоскости XOZ u∈[0," + Api5.Num(RadiusMm) + "], v∈[−" + Api5.Num(HeightMm)
            + ",0] (локальная ось v плоскости XOZ идёт против +Z — измерено первым прогоном F.9)");
        step.Observe("  аналитика: тело " + Api5.Num(FullTurnVolume) + ", 180° → " + Api5.Num(HalfTurnVolume)
            + ", 90° → " + Api5.Num(FullTurnVolume / 4d));

        // ── §4: контроль геометрии тем же профилем и осью, рабочим base-маршрутом, в СВОЁМ документе
        ksDocument3D? controlDoc = null;
        try
        {
            controlDoc = NewPart(out var controlPart);
            var controlSketch = ProfileSketchOn(controlDoc, "F9-control-profile", Api5.PlaneXoz,
                0d, RadiusMm, -HeightMm, 0d);
            var controlAxis = BuildAxisThrough(controlPart, step, 0d, 0d, 0d, 0d, 0d, HeightMm, "контроль");
            if (controlAxis is null)
            {
                step.Observe("КОНТРОЛЬ: ось не построена");
            }
            else
            {
                var control = CreateRotation(controlPart, controlDoc, controlSketch, controlAxis,
                    360d, 0d, ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_baseRotated);
                if (control is null)
                {
                    step.Observe("КОНТРОЛЬ: тело не построено (Update() не подтвердил)");
                }
                else
                {
                    var controlVolume = Api5.Volume(controlPart);
                    step.Observe("КОНТРОЛЬ (свой документ, base, тот же профиль и ось): V="
                        + Api5.Num(controlVolume) + ", ожидание " + Api5.Num(FullTurnVolume)
                        + (controlVolume is null ? "" : Math.Abs(controlVolume.Value - FullTurnVolume)
                            <= Tolerance(FullTurnVolume) ? " → СОВПАЛО" : " → РАСХОЖДЕНИЕ"));
                    step.Observe("КОНТРОЛЬ форма: " + DescribeShape(controlPart, step));
                }
            }
        }
        catch (Exception ex)
        {
            step.Observe("КОНТРОЛЬ бросил " + Describe(ex));
        }
        finally
        {
            TryClose(controlDoc);
        }

        // ── основной опыт: тот же угол на плите, охватывающей тело целиком ──────────────────────
        var removed = new Dictionary<double, double>();
        var verdictFailed = false;
        foreach (var angle in new[] { 360d, 180d, 90d })
        {
            ksDocument3D? doc = null;
            try
            {
                doc = NewPart(out var part);
                if (Api5.BasePlate(part, 2d * plateHalf, 2d * plateHalf, plateThickness, step, "F9") is null)
                {
                    step.Observe("угол " + Api5.Num(angle) + ": плита не построена");
                    continue;
                }

                var volumeBefore = Api5.Volume(part);
                var bodiesBefore = Api5.BodyCount(part);
                step.Observe("угол " + Api5.Num(angle) + ": плита — тел=" + bodiesBefore + ", V="
                    + Api5.Num(volumeBefore) + ", габарит " + Bounds(part)
                    + (volumeBefore is null || Math.Abs(volumeBefore.Value - plateVolume) > Tolerance(plateVolume)
                        ? " (ВНИМАНИЕ: это не ожидаемый объём плиты)" : " (плита на месте)"));

                var sketch = ProfileSketchOn(doc, "F9-profile", Api5.PlaneXoz, 0d, RadiusMm, -HeightMm, 0d);
                var axis = BuildAxisThrough(part, step, 0d, 0d, 0d, 0d, 0d, HeightMm, "угол " + Api5.Num(angle));
                if (axis is null)
                {
                    step.Observe("угол " + Api5.Num(angle) + ": ось не построена");
                    continue;
                }

                var rotation = CreateRotation(part, doc, sketch, axis, angle, 0d,
                    ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_cutRotated);
                if (rotation is null)
                {
                    step.Observe("угол " + Api5.Num(angle) + ": признак не создан (Update() не подтвердил)");
                    continue;
                }

                var volumeAfter = Api5.Volume(part);
                var change = Diff(volumeBefore, volumeAfter);
                var expectedRemoval = FullTurnVolume * (angle / 360d);
                var matched = change is not null
                    && Math.Abs(-change.Value - expectedRemoval) <= Tolerance(expectedRemoval);
                step.Observe("угол " + Api5.Num(angle) + ": V=" + Api5.Num(volumeBefore) + "→"
                    + Api5.Num(volumeAfter) + ", снято="
                    + (change is null ? "не измерено" : Api5.Num(-change.Value))
                    + ", ожидание снятия " + Api5.Num(expectedRemoval)
                    + (change is null ? "" : matched ? " → СОВПАЛО" : " → РАСХОЖДЕНИЕ"));
                step.Observe("угол " + Api5.Num(angle) + ": форма после — " + DescribeShape(part, step)
                    + ", тел=" + Api5.BodyCount(part));

                if (change is not null)
                {
                    removed[angle] = -change.Value;
                }

                if (!matched)
                {
                    verdictFailed = true;
                }
            }
            catch (Exception ex)
            {
                step.Observe("угол " + Api5.Num(angle) + ": бросил " + Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        // Вердикт условный: прежняя редакция этого шага вызывала Pass() безусловно, то есть
        // «измерено» выдавалось за «выполнено» независимо от чисел. Здесь он следует за числами.
        if (verdictFailed || removed.Count < 3)
        {
            step.Fail("вырезание полным оборотом на плите, охватывающей тело целиком, не дало "
                + "аналитического снятия по каждому углу: " + string.Join(", ",
                    removed.OrderByDescending(p => p.Key)
                        .Select(p => Api5.Num(p.Key) + "° → " + Api5.Num(p.Value))));
        }
        else
        {
            step.Pass("снятие равно объёму тела, пропорционально углу и совпало с аналитикой на всех "
                + "трёх углах: " + string.Join(", ",
                    removed.OrderByDescending(p => p.Key)
                        .Select(p => Api5.Num(p.Key) + "° → " + Api5.Num(p.Value))));
        }
    }

    /// <summary>
    /// F.10 — бобышка вращением на существующем теле с видом операции, СООТВЕТСТВУЮЩИМ фабрике.
    /// </summary>
    /// <remarks>
    /// Опыт F.6/F.7 записывал <c>ksOperationNewBody</c> и получал второе тело — то есть ровно то,
    /// что и просил. Здесь та же геометрия прогоняется дважды: с <c>Union</c> и с <c>NewBody</c>.
    /// Различие исходов при неизменной геометрии и есть ответ на вопрос «дело в виде операции или
    /// в постановке».
    /// </remarks>
    private void BossUnionOnPlate()
    {
        var step = _report.Begin("F.10", "Бобышка вращением на теле: Union против NewBody",
            "Сращивается ли o3d_bossRotated с плитой, когда записан ksOperationUnion?");

        const double plateHalf = 60d;
        const double plateThickness = 10d;
        var plateVolume = 2d * plateHalf * (2d * plateHalf) * plateThickness;
        var overlap = Math.PI * RadiusMm * RadiusMm * plateThickness;
        var expected = FullTurnVolume - overlap;

        step.Observe("ПОСТАНОВКА (§5.2 наряда): плита x,y∈[−60,60], z∈[0,10], V=" + Api5.Num(plateVolume));
        step.Observe("  тело: ось Z, R" + Api5.Num(RadiusMm) + ", z∈[−20,20]; пересечение с плитой "
            + Api5.Num(overlap) + " (10 из 40 мм), вне плиты " + Api5.Num(expected));
        step.Observe("  ожидание СЛИЛОСЬ: V=" + Api5.Num(plateVolume + expected)
            + " (ΔV=" + Api5.Num(expected) + "), тел 1→1");
        step.Observe("  ожидание ВТОРОЕ ТЕЛО: V=" + Api5.Num(plateVolume + FullTurnVolume)
            + " (ΔV=" + Api5.Num(FullTurnVolume) + "), тел 1→2");

        var fused = false;
        foreach (var (result, label) in new (ksOperationResultEnum, string)[]
                 {
                     (ksOperationResultEnum.ksOperationUnion, "OperationResult=Union(0)"),
                     (ksOperationResultEnum.ksOperationNewBody, "OperationResult=NewBody(1) — контроль прежнего поведения"),
                 })
        {
            ksDocument3D? doc = null;
            try
            {
                doc = NewPart(out var part);
                if (Api5.BasePlate(part, 2d * plateHalf, 2d * plateHalf, plateThickness, step, "F10") is null)
                {
                    step.Observe(label + ": плита не построена");
                    continue;
                }

                var volumeBefore = Api5.Volume(part);
                var bodiesBefore = Api5.BodyCount(part);
                step.Observe(label + ": плита — тел=" + bodiesBefore + ", V=" + Api5.Num(volumeBefore)
                    + ", габарит " + Bounds(part));

                var sketch = ProfileSketchOn(doc, "F10-profile", Api5.PlaneXoz,
                    0d, RadiusMm, -RadiusMm, RadiusMm);
                var axis = BuildAxisThrough(part, step, 0d, 0d, -RadiusMm, 0d, 0d, RadiusMm, label);
                if (axis is null)
                {
                    step.Observe(label + ": ось не построена");
                    continue;
                }

                var rotation = CreateRotation(part, doc, sketch, axis, 360d, 0d,
                    ksDirectionTypeEnum.dtNormal, step, ksObj3dTypeEnum.o3d_bossRotated, result);
                if (rotation is null)
                {
                    step.Observe(label + ": признак не создан (Update() не подтвердил)");
                    continue;
                }

                var volumeAfter = Api5.Volume(part);
                var bodiesAfter = Api5.BodyCount(part);
                var delta = Diff(volumeBefore, volumeAfter);
                var isFused = delta is not null && bodiesAfter == bodiesBefore
                    && Math.Abs(delta.Value - expected) <= Tolerance(expected);
                var isSeparate = delta is not null && bodiesAfter == bodiesBefore + 1
                    && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume);

                step.Observe(label + ": тел=" + bodiesBefore + "→" + bodiesAfter
                    + ", V=" + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                    + ", ΔV=" + Api5.Num(delta)
                    + " → " + (isFused ? "СЛИЛОСЬ" : isSeparate ? "ВТОРОЕ ТЕЛО" : "ни то, ни другое"));
                step.Observe(label + ": форма — " + DescribeShape(part, step));

                if (result == ksOperationResultEnum.ksOperationUnion && isFused)
                {
                    fused = true;
                }
            }
            catch (Exception ex)
            {
                step.Observe(label + ": бросил " + Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        if (fused)
        {
            step.Pass("Union сращивает: прирост равен выступающей части, число тел не выросло");
        }
        else
        {
            step.Fail("Union не сращивает: прирост или число тел не совпали с ожиданием сращивания");
        }
    }

    /// <summary>
    /// F.11 — выбор целевого тела: boss и cut вращением в детали с ПОСТОРОННИМ телом.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Строки <c>SM-03.boss_rotated.full_turn</c> и <c>SM-03.cut_rotated.full_turn</c> объявляют
    /// зависимости <c>dep.bodies.multibody</c> и <c>dep.selection.unambiguous</c>. Значит вопрос
    /// «какое тело тронуто» обязан быть измерен, а не выведен из однотельной постановки.
    /// </para>
    /// <para>
    /// Измеряется не «выбор» как настройка, а поведение: <c>IRotated</c>/<c>IRotated1</c> селектора
    /// тела не объявляют вовсе (проверено по интероп-сборке: у них нет ни <c>chooseType</c>, ни
    /// <c>ChooseBodies</c>; они есть только у API5-определений <c>ksBossRotatedDefinition</c> и
    /// <c>ksCutRotatedDefinition</c>). Поэтому здесь фиксируется, к какому телу ядро отнесло
    /// операцию: если оно берёт ПЕРЕСЕКАЕМОЕ тело, а не первое в коллекции, то поведение
    /// однозначно и может быть заявлено; если первое — операцию нельзя выполнять вслепую.
    /// </para>
    /// </remarks>
    private void MultiBodyTarget()
    {
        var step = _report.Begin("F.11", "Целевое тело boss/cut в детали с посторонним телом",
            "К какому телу ядро относит вращение, когда тел два и инструмент пересекает только одно?");

        const double plateHalf = 60d;
        const double plateThickness = 10d;
        var plateVolume = 2d * plateHalf * (2d * plateHalf) * plateThickness;
        var bossDelta = BossFusedDeltaAxial(plateThickness);
        var foreignVolume = 40d * 40d * plateThickness;

        step.Observe("ПОСТАНОВКА: тело A — плита x,y∈[−60,60], z∈[0,10], V=" + Api5.Num(plateVolume));
        step.Observe("  тело B (постороннее) — блок x,y∈[40,80], z∈[0,10], V=" + Api5.Num(foreignVolume)
            + "; инструмент (ось Z в начале координат, R" + Api5.Num(RadiusMm)
            + ") пересекает ТОЛЬКО A");

        var cases = new (string Label, ksObj3dTypeEnum Kind, ksOperationResultEnum Result, double V0,
            double V1, double ExpectedDelta)[]
        {
            // Пересечение с ЦЕЛЕВЫМ телом, а не объём инструмента: boss-цилиндр z∈[−20,20] пересекает
            // плиту z∈[0,10] на 10 мм; cut-цилиндр z∈[0,40] — тоже на 10 мм. Снять или добавить
            // больше, чем пересечение, нельзя, и ожидание обязано считаться от пересечения.
            ("boss, Union", ksObj3dTypeEnum.o3d_bossRotated, ksOperationResultEnum.ksOperationUnion,
                -RadiusMm, RadiusMm, bossDelta),
            ("cut, Cut", ksObj3dTypeEnum.o3d_cutRotated, ksOperationResultEnum.ksOperationCut,
                -HeightMm, 0d, -Math.PI * RadiusMm * RadiusMm * plateThickness),
        };

        var verdictFailed = false;
        foreach (var (label, kind, result, v0, v1, expectedDelta) in cases)
        {
            ksDocument3D? doc = null;
            try
            {
                doc = NewPart(out var part);
                if (Api5.BasePlate(part, 2d * plateHalf, 2d * plateHalf, plateThickness, step, "F11A") is null
                    || !ExtrudeRect(doc, part, 40d, 80d, 40d, 80d, plateThickness, step, "F11B"))
                {
                    step.Observe(label + ": два тела не построены");
                    continue;
                }

                var bodiesBefore = Api5.BodyCount(part);
                var volumesBefore = BodyVolumes(part);
                step.Observe(label + ": тел=" + bodiesBefore + ", объёмы по телам ["
                    + string.Join("; ", volumesBefore.Select(Api5.Num)) + "]");

                var sketch = ProfileSketchOn(doc, "F11-profile", Api5.PlaneXoz, 0d, RadiusMm, v0, v1);
                var axis = BuildAxisThrough(part, step, 0d, 0d, v0, 0d, 0d, v1, label);
                if (axis is null)
                {
                    step.Observe(label + ": ось не построена");
                    continue;
                }

                var rotation = CreateRotation(part, doc, sketch, axis, 360d, 0d,
                    ksDirectionTypeEnum.dtNormal, step, kind, result);
                if (rotation is null)
                {
                    step.Observe(label + ": признак не создан (Update() не подтвердил)");
                    continue;
                }

                var bodiesAfter = Api5.BodyCount(part);
                var volumesAfter = BodyVolumes(part);
                step.Observe(label + ": тел=" + bodiesBefore + "→" + bodiesAfter + ", объёмы по телам ["
                    + string.Join("; ", volumesAfter.Select(Api5.Num)) + "]");

                // Сопоставление по ОБЪЁМАМ, а не по позиции: измерено F.11, что КОМПАС ПЕРЕСТАВЛЯЕТ
                // тела в коллекции после операции (144000/16000 → 16000/181699.111843077). Сравнение
                // по индексу читало бы перестановку как «тело A изменилось на −128000».
                var before = volumesBefore.Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
                var after = volumesAfter.Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
                var countOk = bodiesAfter == bodiesBefore && before.Count == after.Count;
                var deltas = countOk
                    ? before.Zip(after, (b, a) => a - b).ToList()
                    : new List<double>();
                var touched = deltas.Count(d => Math.Abs(d) > Tolerance(1d));
                var deltaSum = deltas.Sum();
                var matched = countOk && touched == 1
                    && Math.Abs(deltaSum - expectedDelta) <= Tolerance(expectedDelta);

                step.Observe(label + ": изменение по сопоставленным объёмам — "
                    + (deltas.Count == 0 ? "не измерено" : string.Join("; ", deltas.Select(d => Api5.Num(d))))
                    + ", суммарно " + Api5.Num(deltaSum) + ", ожидание " + Api5.Num(expectedDelta)
                    + (countOk ? "" : " (число тел/объёмов не совпало)"));
                step.Observe(label + ": тронуто тел " + touched + " (ожидание 1), число тел сохранилось → "
                    + (bodiesAfter == bodiesBefore) + ", исход → " + (matched ? "СОВПАЛО" : "РАСХОЖДЕНИЕ"));

                if (!matched)
                {
                    verdictFailed = true;
                }
            }
            catch (Exception ex)
            {
                step.Observe(label + ": бросил " + Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        if (verdictFailed)
        {
            step.Fail("в детали с посторонним телом изменение не совпало с пересечением целевого тела");
        }
        else
        {
            step.Pass("операция отнесена к ПЕРЕСЕКАЕМОМУ телу, а не к первому в коллекции: тронуто "
                + "ровно одно тело, на величину пересечения, число тел сохранилось");
        }
    }

    /// <summary>A rectangle u∈[u0,u1], v∈[v0,v1] on XOY, base-extruded <c>thickness</c> along +Z.</summary>
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
        definition.SetSideParam(true, 0 /* etBlind */, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        return created;
    }

    /// <summary>Per-body volumes in BodyCollection order — WHICH body changed, not only by how much.</summary>
    private static List<double?> BodyVolumes(ksPart part)
    {
        var volumes = new List<double?>();
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return volumes;
            }

            for (var i = 0; i < bodies.GetCount(); i++)
            {
                volumes.Add(Api5.BodyVolume(bodies.GetByIndex(i)));
            }
        }
        catch (Exception)
        {
            // A collection that stops enumerating yields the volumes read so far.
        }

        return volumes;
    }

    /// <summary>A rectangular profile on the named default plane: u∈[u0,u1], v∈[v0,v1].</summary>
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
        part.RebuildModel();
        doc.RebuildDocument();
        return sketch;
    }

    /// <summary>An axis through two points given in MODEL coordinates (used for the Z axis).</summary>
    private IAxis3D? BuildAxisThrough(
        ksPart part, ProbeStep step,
        double x1, double y1, double z1, double x2, double y2, double z2, string label)
    {
        try
        {
            if (_app.TransferInterface(part, 2, 0) is not IModelObject part7)
            {
                step.Observe(label + ": деталь не переносится в API7");
                return null;
            }

            if (part7 is not IModelContainer container || part7 is not IAuxiliaryGeomContainer auxiliary
                || auxiliary.Axes3D is not { } axes)
            {
                step.Observe(label + ": Axes3D недостижим");
                return null;
            }

            var p1 = MakePoint(container, x1, y1, z1);
            var p2 = MakePoint(container, x2, y2, z2);
            if (p1 is null || p2 is null)
            {
                step.Observe(label + ": точки оси не создались");
                return null;
            }

            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                step.Observe(label + ": Axes3D.Add не отдал IAxis3DBy2Points");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;
            var updated = Api5.SafeBool(by2.Update);
            step.Observe(label + ": ось (" + Api5.Num(x1) + "," + Api5.Num(y1) + "," + Api5.Num(z1)
                + ")→(" + Api5.Num(x2) + "," + Api5.Num(y2) + "," + Api5.Num(z2)
                + "), Update()=" + Api5.Raw(updated));
            return updated == true ? by2 : null;
        }
        catch (Exception ex)
        {
            step.Observe(label + ": ось бросила " + Describe(ex));
            return null;
        }
    }

    /// <summary>The main body's bounding box in model coordinates, or the reason it is missing.</summary>
    private static string Bounds(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return "тела нет";
            }

            return body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? "x[" + Api5.Num(x1) + "," + Api5.Num(x2) + "] y[" + Api5.Num(y1) + "," + Api5.Num(y2)
                    + "] z[" + Api5.Num(z1) + "," + Api5.Num(z2) + "]"
                : "габарит не прочитан";
        }
        catch (Exception ex)
        {
            return "габарит бросил " + Describe(ex);
        }
    }

    /// <summary>A rectangular profile spanning u∈[u0,u1] with v∈[0,Height], axis on u=0.</summary>
    private static ksEntity ProfileSketchBetween(ksDocument3D doc, string name, double u0, double u1)
    {
        var part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости XOY нет");
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
            editor.ksLineSeg(u0, 0d, u1, 0d, 1);
            editor.ksLineSeg(u1, 0d, u1, HeightMm, 1);
            editor.ksLineSeg(u1, HeightMm, u0, HeightMm, 1);
            editor.ksLineSeg(u0, HeightMm, u0, 0d, 1);
        }

        definition.EndEdit();
        part.RebuildModel();
        doc.RebuildDocument();
        return sketch;
    }

    /// <summary>A rectangular profile spanning v∈[v0,v1] with its left edge on the axis at u=0.</summary>
    private static ksEntity ProfileSketchSpan(ksDocument3D doc, string name, double v0, double v1)    {
        var part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости XOY нет");
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
            editor.ksLineSeg(0d, v0, RadiusMm, v0, 1);
            editor.ksLineSeg(RadiusMm, v0, RadiusMm, v1, 1);
            editor.ksLineSeg(RadiusMm, v1, 0d, v1, 1);
            editor.ksLineSeg(0d, v1, 0d, v0, 1);
        }

        definition.EndEdit();
        part.RebuildModel();
        doc.RebuildDocument();
        return sketch;
    }

    /// <summary>An axis on u=0 spanning v∈[v0,v1] in model coordinates.</summary>
    private IAxis3D? BuildAxisSpan(ksDocument3D doc, ksPart part, double v0, double v1, ProbeStep step)
    {
        try
        {
            if (_app.TransferInterface(part, 2, 0) is not IModelObject part7)
            {
                step.Observe("ось: деталь не переносится в API7");
                return null;
            }

            if (part7 is not IModelContainer container || part7 is not IAuxiliaryGeomContainer auxiliary)
            {
                step.Observe("ось: QI(IModelContainer)/QI(IAuxiliaryGeomContainer) не прошёл");
                return null;
            }

            if (auxiliary.Axes3D is not { } axes)
            {
                step.Observe("ось: Axes3D → null");
                return null;
            }

            var p1 = MakePoint(container, 0d, v0, 0d);
            var p2 = MakePoint(container, 0d, v1, 0d);
            if (p1 is null || p2 is null)
            {
                step.Observe("ось: точки не создались");
                return null;
            }

            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                step.Observe("ось: Axes3D.Add не отдал IAxis3DBy2Points");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;
            var updated = Api5.SafeBool(by2.Update);
            step.Observe("ось: Update()=" + Api5.Raw(updated) + ", Valid="
                + Api5.Raw(Api5.SafeBool(() => by2.Valid)));
            return updated == true ? by2 : null;
        }
        catch (Exception ex)
        {
            step.Observe("ось бросила " + Describe(ex));
            return null;
        }
    }

    /// <summary>Builds one rotation in a document of its own and reports the volume it produced.</summary>
    private string BuildInOwnDocument(
        double angleNormal, double angleReverse, ksDirectionTypeEnum direction, ProbeStep host,
        ksObj3dTypeEnum kind = ksObj3dTypeEnum.o3d_baseRotated)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = NewPart(out var part);
            var sketch = ProfileSketch(doc, "F1-profile");
            var axis = BuildAxis(doc, part, host);
            if (axis is null)
            {
                return "ось не построена";
            }

            var rotation = CreateRotation(part, doc, sketch, axis, angleNormal, angleReverse, direction, host, kind);
            if (rotation is null)
            {
                return "признак не создан";
            }

            var volume = Api5.Volume(part);
            var bodies = Api5.BodyCount(part);
            var shape = "";

            // The shape reading is taken for the full-turn candidates only: a half cylinder and a
            // full one differ by their bounding box, and that difference is the independent evidence
            // that the volume is not the only thing that changed.
            if (volume is not null && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
            {
                shape = ", " + DescribeShape(part, host);
            }

            return "тел=" + bodies + ", V=" + Api5.Num(volume) + " (" + ClassifyVolumeOf(volume) + ")"
                + shape;
        }
        catch (Exception ex)
        {
            return "бросил " + Describe(ex);
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Creates a fresh invisible part document.</summary>
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

    /// <summary>
    /// The reference profile: a rectangle <c>u∈[0,20]</c>, <c>v∈[-20,20]</c> with the axis on its
    /// left edge.
    /// </summary>
    /// <remarks>
    /// The axis lies ON the profile boundary on purpose. With the axis on the boundary a full turn
    /// sweeps the full cylinder π·r²·h and a half turn exactly half of it, so the two are
    /// distinguishable by volume for every direction — which is the property the previous angle
    /// matrix did not have.
    /// </remarks>
    private static ksEntity ProfileSketch(ksDocument3D doc, string name)
    {
        var part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости XOY нет");
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        var v0 = -HeightMm / 2d;
        var v1 = HeightMm / 2d;
        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(0d, v0, RadiusMm, v0, 1);
            editor.ksLineSeg(RadiusMm, v0, RadiusMm, v1, 1);
            editor.ksLineSeg(RadiusMm, v1, 0d, v1, 1);
            editor.ksLineSeg(0d, v1, 0d, v0, 1);
        }

        definition.EndEdit();
        part.RebuildModel();
        doc.RebuildDocument();
        return sketch;
    }

    /// <summary>
    /// The rotation axis as an API7 object, on the line <c>u = 0</c> in model coordinates.
    /// </summary>
    /// <remarks>
    /// <c>Axes3D</c> is declared on <c>IAuxiliaryGeomContainer</c>, not on <c>IModelContainer</c>:
    /// asking the model container finds nothing and would report an absent axis. The QI is measured,
    /// not assumed (R.13/R.18).
    /// </remarks>
    private IAxis3D? BuildAxis(ksDocument3D doc, ksPart part, ProbeStep step, double sign = 1d)
    {
        try
        {
            if (_app.TransferInterface(part, 2 /* ksAPI7Dual */, 0) is not IModelObject part7)
            {
                step.Observe("деталь не переносится в API7 как IModelObject");
                return null;
            }

            if (part7 is not IModelContainer container || part7 is not IAuxiliaryGeomContainer auxiliary)
            {
                step.Observe("деталь не отвечает QI(IModelContainer)/QI(IAuxiliaryGeomContainer)");
                return null;
            }

            if (auxiliary.Axes3D is not { } axes)
            {
                step.Observe("Axes3D → null");
                return null;
            }

            var p1 = MakePoint(container, 0d, sign * -HeightMm / 2d, 0d);
            var p2 = MakePoint(container, 0d, sign * HeightMm / 2d, 0d);
            if (p1 is null || p2 is null)
            {
                step.Observe("точки оси не создались");
                return null;
            }

            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                step.Observe("Axes3D.Add не отдал IAxis3DBy2Points");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;

            // Update() only after both points are supplied — measured in R.13: an axis updated before
            // its points arrive reads Valid=False and never joins the tree.
            var updated = Api5.SafeBool(by2.Update);
            var valid = Api5.SafeBool(() => by2.Valid);
            step.Observe("ось: Update()=" + Api5.Raw(updated) + ", Valid=" + Api5.Raw(valid));
            return updated == true ? by2 : null;
        }
        catch (Exception ex)
        {
            step.Observe("построение оси бросило " + Describe(ex));
            return null;
        }
    }

    /// <summary>Creates the rotation through the API7 factory and writes the full parameter set.</summary>
    private KompasAPI7.IRotated? CreateRotation(
        ksPart part,
        ksDocument3D doc,
        ksEntity sketch,
        IAxis3D axis,
        double angleNormal,
        double angleReverse,
        ksDirectionTypeEnum direction,
        ProbeStep step,
        ksObj3dTypeEnum kind = ksObj3dTypeEnum.o3d_baseRotated,
        ksOperationResultEnum? resultOverride = null)
    {
        try
        {
            var container = (_app.TransferInterface(doc, 2, 0) as KompasAPI7.IKompasDocument3D)?.TopPart
                as IModelContainer;
            if (container?.Rotateds is not { } rotateds)
            {
                step.Observe("фабрика Rotateds недостижима");
                return null;
            }

            if ((rotateds.Add(kind) as IModelObject) as KompasAPI7.IRotated
                is not { } rotation)
            {
                step.Observe("Rotateds.Add не отдал QI(IRotated)");
                return null;
            }

            rotation.Profile = _app.TransferInterface(sketch, 2, 0) as IModelObject;
            rotation.Axis = axis;

            // The pair is written and read back: a write the object silently discards is
            // indistinguishable from one it honours unless the read-back is recorded.
            rotation.Angle[true] = angleNormal;
            rotation.Angle[false] = angleReverse;
            rotation.Direction = direction;
            rotation.RotatedType[true] = ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;
            var operationResult = resultOverride ?? OperationResultOf(kind);
            if (rotation is IRotated1 rotated1)
            {
                // The kind is set by the FACTORY, not by this member — measured in R.26. It is written
                // for the tree's sake and read back below.
                //
                // ИСПРАВЛЕНО 18.09.2026 (второй раз). Прежняя правка заменила жёсткий
                // ksOperationNewBody на пару «cut → Cut, всё остальное → NewBody». Этого мало и это
                // было НЕВЕРНО по существу для boss: вид операции boss требует ksOperationUnion, а
                // получал NewBody. Опыты F.6/F.7 (бобышка на плите) записаны с NewBody — то есть
                // измеряли ровно то, что и должны были измерить при «новом теле»: второе тело и
                // прирост в целый цилиндр. Вывод «сращивания нет» опирался на опыт, который
                // сращивания и не запрашивал. Теперь вид операции выводится из вида фабрики.
                rotated1.OperationResult = operationResult;
            }

            var operationResultReadBack = rotation is IRotated1 readBack1
                ? Api5.Raw(Api5.SafeEnum(() => readBack1.OperationResult))
                : "нет QI(IRotated1)";

            step.Observe("записано: Angle[true]=" + Api5.Num(angleNormal)
                + ", Angle[false]=" + Api5.Num(angleReverse)
                + ", Direction=" + direction
                + ", вид фабрики=" + kind
                + ", OperationResult=" + operationResult
                + "; прочитано обратно: Angle[true]=" + Api5.Num(Api5.SafeDouble(() => rotation.Angle[true]))
                + ", Angle[false]=" + Api5.Num(Api5.SafeDouble(() => rotation.Angle[false]))
                + ", Direction=" + Api5.Raw(Api5.SafeEnum(() => rotation.Direction))
                + ", OperationResult=" + operationResultReadBack);

            var updated = Api5.SafeBool(rotation.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("Update()=" + Api5.Raw(updated));
            return updated == true ? rotation : null;
        }
        catch (Exception ex)
        {
            step.Observe("создание бросило " + Describe(ex));
            return null;
        }
    }

    /// <summary>Changes the angle pair on an EXISTING feature and rebuilds ITS OWN document.</summary>
    /// <remarks>
    /// The document is a parameter, not the probe's shared <c>_doc</c>. The first version rebuilt
    /// <c>_doc</c> — a different document from the one that holds the feature — and read an unchanged
    /// volume, which looks exactly like "the angle does nothing". That was this probe describing
    /// itself, and it is the same defect class as probe defect 14.
    /// </remarks>
    private void SetAngle(
        ksDocument3D doc,
        ksPart part,
        KompasAPI7.IRotated rotation,
        double normal,
        double reverse,
        ProbeStep step,
        string label)
    {
        rotation.Angle[true] = normal;
        rotation.Angle[false] = reverse;
        var updated = Api5.SafeBool(rotation.Update);

        try
        {
            part.RebuildModel();
            doc.RebuildDocument();
        }
        catch (Exception ex)
        {
            step.Observe("перестроение после " + label + " бросило " + Describe(ex));
        }

        step.Observe("  " + label + ": Update()=" + Api5.Raw(updated)
            + ", прочитано Angle[true]=" + Api5.Num(Api5.SafeDouble(() => rotation.Angle[true]))
            + ", Angle[false]=" + Api5.Num(Api5.SafeDouble(() => rotation.Angle[false])));
    }

    /// <summary>
    /// Cross-section area of the boss-cylinder slab lying inside a plate that occupies <c>z∈[0,t]</c>,
    /// for the geometry this probe actually builds: the cylinder's axis lies IN the plate's upper face
    /// plane (z=0).
    /// </summary>
    /// <remarks>
    /// Derivation, not a fit: ∫₀ᵗ 2√(R²−z²) dz = t·√(R²−t²) + R²·asin(t/R).
    /// </remarks>
    private static double BossFusedSlabArea(double plateThickness) =>
        plateThickness * Math.Sqrt(RadiusMm * RadiusMm - plateThickness * plateThickness)
        + RadiusMm * RadiusMm * Math.Asin(plateThickness / RadiusMm);

    /// <summary>
    /// The volume a fusing boss adds: the whole cylinder minus the slab of it inside the plate.
    /// </summary>
    /// <remarks>
    /// The FIRST edition of F.6/F.7 wrote <c>π·R²·(H−t)</c>, which is the answer for a cylinder
    /// STANDING on the plate, axis perpendicular to it. The probe never built that — <c>BuildAxis</c>
    /// puts the axis along Y inside the sketch plane. The stale expectation is why both steps failed
    /// on a correct fusion after the operation-result defect was fixed: the EXPECTATION was wrong, not
    /// the product (§4 of the B2 наряд).
    /// </remarks>
    private static double BossFusedDelta(double plateThickness) =>
        FullTurnVolume - BossFusedSlabArea(plateThickness) * HeightMm;

    /// <summary>
    /// The same quantity when the cylinder's axis is PERPENDICULAR to the plate (axis along Z), so the
    /// intersection is the full disc over the plate's thickness: <c>π·R²·t</c>.
    /// </summary>
    /// <remarks>
    /// The two differ by geometry, not by taste: with the axis lying IN the plate's face plane the
    /// cross-section is a circular segment (<see cref="BossFusedSlabArea"/>), with the axis
    /// perpendicular to it the cross-section is the whole disc. F.6/F.7 build the first, F.10/F.11
    /// the second.
    /// </remarks>
    private static double BossFusedDeltaAxial(double plateThickness) =>
        FullTurnVolume - Math.PI * RadiusMm * RadiusMm * plateThickness;

    /// <summary>
    /// The operation result that MATCHES the factory kind. Written into <c>IRotated1.OperationResult</c>.
    /// </summary>
    /// <remarks>
    /// The mapping is read off <c>ksOperationResultEnum</c> and the three factories, not chosen by
    /// symmetry: <c>ksOperationUnion = 0</c>, <c>ksOperationNewBody = 1</c>, <c>ksOperationCut = 2</c>.
    /// Writing <c>NewBody</c> for a <c>boss</c> factory asks for a NEW body, which is exactly the
    /// outcome F.6/F.7 recorded and then reported as "boss does not fuse".
    /// </remarks>
    private static ksOperationResultEnum OperationResultOf(ksObj3dTypeEnum kind) => kind switch
    {
        ksObj3dTypeEnum.o3d_bossRotated => ksOperationResultEnum.ksOperationUnion,
        ksObj3dTypeEnum.o3d_cutRotated => ksOperationResultEnum.ksOperationCut,
        _ => ksOperationResultEnum.ksOperationNewBody,
    };

    // ══════════════════════════════════════════════════════════════ readings ══

    /// <summary>
    /// An independent shape reading: the cylindrical faces with radius and height, plus the bounding
    /// box. A full cylinder R20 H40 has bounds 40×40×40; a half cylinder has 40×40×20.
    /// </summary>
    private static string DescribeShape(ksPart part, ProbeStep step)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return "тела нет";
            }

            var bounds = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2)
                ? "габарит x[" + Api5.Num(x1) + "," + Api5.Num(x2) + "] y[" + Api5.Num(y1) + ","
                    + Api5.Num(y2) + "] z[" + Api5.Num(z1) + "," + Api5.Num(z2) + "]"
                : "габарит не прочитан";

            var cylinders = new List<string>();
            if (body.FaceCollection() is ksFaceCollection faces)
            {
                for (var f = 0; f < faces.GetCount(); f++)
                {
                    if (faces.GetByIndex(f) is not ksFaceDefinition face)
                    {
                        continue;
                    }

                    try
                    {
                        if (face.GetSurface() is ksSurface surface && surface.IsCylinder()
                            && surface.GetSurfaceParam() is ksCylinderParam cylinder)
                        {
                            cylinders.Add("r=" + Api5.Num(cylinder.radius) + " h=" + Api5.Num(cylinder.height));
                        }
                    }
                    catch (COMException)
                    {
                        // A face whose parameters КОМПАС withheld is not a reason to abort the walk.
                    }
                }
            }

            return "цилиндрических граней " + cylinders.Count
                + (cylinders.Count == 0 ? string.Empty : " (" + string.Join("; ", cylinders) + ")")
                + ", " + bounds;
        }
        catch (Exception ex)
        {
            step.Observe("чтение формы бросило " + Describe(ex));
            return "форма не прочитана";
        }
    }

    private static string ClassifyVolume(string reading)
    {
        var marker = "V=";
        var start = reading.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return reading;
        }

        var end = reading.IndexOf(' ', start);
        var text = end < 0 ? reading[(start + marker.Length)..] : reading[(start + marker.Length)..end];
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? ClassifyVolumeOf(value)
            : reading;
    }

    /// <summary>Names a volume against the two analytically known values, or reports it as neither.</summary>
    private static string ClassifyVolumeOf(double? volume)
    {
        if (volume is null)
        {
            return "не прочитано (тела нет?)";
        }

        if (Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            return "ПОЛНЫЙ цилиндр π·r²·h=" + FullTurnVolume.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (Math.Abs(volume.Value - HalfTurnVolume) <= Tolerance(HalfTurnVolume))
        {
            return "ПОЛОВИНА цилиндра π·r²·h/2";
        }

        var quarter = FullTurnVolume / 4d;
        if (Math.Abs(volume.Value - quarter) <= Tolerance(quarter))
        {
            return "ЧЕТВЕРТЬ цилиндра π·r²·h/4";
        }

        return "ни полный, ни половина, ни четверть";
    }

    private static double? Diff(double? before, double? after) =>
        before is not null && after is not null ? after - before : null;

    private static IPoint3D? MakePoint(IModelContainer container, double x, double y, double z)
    {
        try
        {
            if (container.Points3D is not { } points
                || points.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            point.Update();
            return point;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryClose(ksDocument3D? doc)
    {
        try
        {
            doc?.close();
        }
        catch (Exception)
        {
            // Not this step's subject.
        }
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}

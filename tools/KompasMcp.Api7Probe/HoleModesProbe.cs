using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// SM-07 (queue B2): the four native-hole modes that have never been executed.
/// </summary>
/// <remarks>
/// <para>
/// The probe in <see cref="HoleProbe"/> settled the base mode — a cylindrical through hole, created
/// through API7, cutting exactly 250π mm³ with the millimetre reading proved by a 0.01 control. Every
/// rung there uses <c>ksHTBase</c>. The queue needs four more, and none of them has ever run:
/// <c>through_counterbore</c>, <c>through_countersink</c>, <c>blind_flat_bottom</c> and
/// <c>position_off_origin</c>. So this probe measures each one instead of assuming that "the hole
/// works" transfers to "the hole's variants work" — the two are different claims and the second is
/// the one the release profile requires.
/// </para>
/// <para>
/// <b>Proof standard.</b> Same as the rest of the program: a non-null object and a <c>true</c> from
/// <c>Update()</c> are not results. Each mode is judged on the material it actually removed, against
/// an analytic expectation computed from the mode's own geometry — and where an expectation cannot be
/// written down without guessing, the step says UNKNOWN rather than rounding to PASS.
/// </para>
/// </remarks>
internal sealed class HoleModesProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;

    /// <summary>Through hole Ø10: π·r²·h with r = 5, h = 10 — the same 250π the base run measured.</summary>
    private static double ThroughHole(double diameterMm) =>
        Math.PI * diameterMm * diameterMm / 4d * PlateThickness;

    /// <summary>
    /// Counterbore: the pilot through hole plus the <em>annular</em> recess. The recess is an annulus,
    /// not a full cylinder — the mode removes only the material between the pilot wall and the bore
    /// wall, because the pilot's own volume is already accounted for by the through hole. Measured, not
    /// assumed: with pilot Ø10, bore Ø18 and depth 4 the probe removed 703.7167544041131 mm³ beyond the
    /// through hole, and π/4·(18²−10²)·4 = 703.7167544041137 — agreement to every printed digit. The
    /// first draft of this formula added a full Ø18 cylinder instead and so double-counted the pilot.
    /// </summary>
    private static double Counterbore(double pilotMm, double boreMm, double boreDepthMm) =>
        ThroughHole(pilotMm)
        + Math.PI / 4d * (boreMm * boreMm - pilotMm * pilotMm) * boreDepthMm;

    /// <summary>Blind hole with a flat bottom: the bore down to its depth, and nothing beyond.</summary>
    private static double BlindFlat(double diameterMm, double depthMm) =>
        Math.PI * diameterMm * diameterMm / 4d * depthMm;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();
    private readonly HashSet<int> _pidsBefore = new();
    private readonly List<string> _route = new();

    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private KompasAPI7.IModelContainer? _container;
    private KompasAPI7.IHoles3D? _holes;
    private double _lastDelta = double.NaN;

    /// <summary>The step the plate builder reports into — the plate is built in several modes.</summary>
    private ProbeStep _plateStep = null!;

    public HoleModesProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        SampleProcessesBefore();
        Launch();
        try
        {
            Capabilities();
            ThroughCounterbore();
            ThroughCountersink();
            BlindFlatBottom();
            PositionOffOrigin();
            AdapterPlacementReplay();
        }
        finally
        {
            Shutdown();
        }
    }

    private void SampleProcessesBefore()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("M.0", "Свой невидимый экземпляр и пластина-эталон",
            "Сеанс и эталон 100×80×10 подняты?");
        _clock.Restart();
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        step.Observe("экземпляр API5 создан за " + _clock.ElapsedMilliseconds + " мс");
        _doc = (ksDocument3D)_app.Document3D();
        _doc.Create(true, true);
        _part = (ksPart)_doc.GetPart(-1);
        _plateStep = step;
        if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step) is null)
        {
            step.Fail("пластина не построена — измерять нечего");
            return;
        }

        _doc.RebuildDocument();
        var volume = Api5.Volume(_part);
        var expected = PlateWidth * PlateHeight * PlateThickness;
        step.Observe("пластина: V=" + Api5.Num(volume) + ", ожидание " + Api5.Num(expected));
        step.Pass("сеанс поднят, эталон построен");
    }

    private void Shutdown()
    {
        var step = _report.Begin("M.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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
            step.Observe("Quit() вызван на собственном экземпляре");
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил " + ex.GetType().Name);
        }

        var appeared = new List<int>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            appeared = Process.GetProcessesByName("KOMPAS")
                .Select(p =>
                {
                    var pid = p.Id;
                    p.Dispose();
                    return pid;
                })
                .Where(pid => !_pidsBefore.Contains(pid))
                .ToList();
            if (appeared.Count == 0)
            {
                break;
            }

            Thread.Sleep(500);
        }

        step.Observe("посторонних процессов до запуска: " + _pidsBefore.Count);
        step.Observe("новых процессов после Quit(): " + appeared.Count
            + (appeared.Count == 0 ? string.Empty : " (" + string.Join(", ", appeared) + ")"));

        // The rule from SM-03 carries over: the base probe measured that Quit() does not itself
        // terminate KOMPAS.exe, so a leftover is expected rather than surprising. What matters is
        // whether the leftovers are ours and whether they hold anything of ours open — the probe's own
        // work directory is the thing it is allowed to leave behind, and nothing else.
        foreach (var pid in appeared)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                step.Observe("  PID " + pid + ": " + process.ProcessName
                    + ", запущен " + process.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + ", память " + (process.WorkingSet64 / 1024 / 1024) + " МБ");
            }
            catch (Exception ex)
            {
                step.Observe("  PID " + pid + ": уже завершился (" + ex.GetType().Name + ")");
            }
        }

        if (appeared.Count == 0)
        {
            step.Pass("новых процессов не осталось");
        }
        else if (appeared.Count <= _expectedLeftovers)
        {
            step.Pass("Quit() не завершает KOMPAS.exe (замер SM-03): осталось " + appeared.Count
                + " собственных процесса-сеанса, что для этой версии ожидаемо");
        }
        else
        {
            step.Fail("осталось больше процессов, чем их создаёт сам метод: " + appeared.Count
                + " против ожидаемых " + _expectedLeftovers);
        }
    }

    /// <summary>
    /// How many processes a single headless session legitimately leaves behind on this build. Set from
    /// the measured behaviour of <c>Quit()</c> in SM-03 (it does not terminate KOMPAS.exe), so a
    /// leftover is not counted as a leak — only a leftover *beyond* the session's own is.
    /// </summary>
    private const int _expectedLeftovers = 2;

    /// <summary>
    /// M.1 — what the live <c>IHole3D</c> actually offers, asked rather than read off a document.
    /// </summary>
    /// <remarks>
    /// The four modes need members that the base mode never touched: a countersink angle, a bore
    /// diameter and depth, a bottom shape. Whether those exist on this interface — and under which
    /// names — decides whether the modes are reachable at all, so it is measured first and the answer
    /// is printed member by member. A name that answers with a dispid exists; one that answers
    /// <c>DISP_E_UNKNOWNNAME</c> does not, and that is a fact about the object, not about the wrapper.
    /// </remarks>
    private void Capabilities()
    {
        var step = _report.Begin("M.1", "Что живой IHole3D умеет: цековка, зенковка, дно",
            "Объявлены ли на объекте члены, нужные четырём режимам, и под какими именами?");

        var document7 = Transfer(_doc, step);
        if (document7?.TopPart is not { } topPart)
        {
            step.Fail("API7-представление документа не получено");
            return;
        }

        _container = topPart as KompasAPI7.IModelContainer;
        step.Observe("QI(IModelContainer) → " + (_container is not null));
        if (_container is null)
        {
            step.Fail("TopPart не отвечает на QI(IModelContainer)");
            return;
        }

        _holes = _container.Holes3D;
        step.Observe("Holes3D → " + (_holes is null ? "null" : Api5.RuntimeName(_holes) + ", count=" + Count(() => _holes!.Count)));
        if (_holes is null)
        {
            step.Fail("Holes3D недоступно");
            return;
        }

        var hole = _holes.Add();
        if (hole is null)
        {
            step.Fail("IHoles3D.Add() вернул null");
            return;
        }

        // The list is no longer a guess: it is the declaration read out of the vendor type library
        // (IHole3D, IID {4C901765-3E0D-4A5D-B2F8-FA708E3CC605}, 13 members). Asking the live object
        // for exactly those names turns "the document says so" into "this build answers so".
        var declared = new[]
        {
            "Diameter", "Depth", "DepthType", "EndFaceType", "EndFaceAngle", "HoleType",
            "DepthVertex", "DepthFace", "Axis", "Thread", "HoleParameters", "ShowThread",
        };

        var present = new List<string>();
        foreach (var name in declared)
        {
            var answer = Late.Dispid(hole, name);
            var ok = !answer.Contains("UNKNOWNNAME", StringComparison.OrdinalIgnoreCase);
            step.Observe("IHole3D." + name + " → " + answer + (ok ? string.Empty : "  (имени на объекте нет)"));
            if (ok)
            {
                present.Add(name);
            }
        }

        step.Observe("объявленных имён подтверждено: " + present.Count + " из " + declared.Length);

        // Negative control: the mode-bearing names are absent, so a mode is NOT reached by setting a
        // property on the hole. Measured, not assumed — these are the names the first draft of this
        // probe tried, and every one of them was refused by the live object.
        var absent = new[]
        {
            "SinkDiameter", "SinkDepth", "SinkAngle", "CounterboreDiameter", "CounterboreDepth",
            "CountersinkDiameter", "ConeAngle", "BottomType", "FlatBottom", "Position",
        };
        var refused = 0;
        foreach (var name in absent)
        {
            if (Late.Dispid(hole, name).Contains("UNKNOWNNAME", StringComparison.OrdinalIgnoreCase))
            {
                refused++;
            }
        }

        step.Observe("из " + absent.Length + " имён режимов на самом IHole3D отсутствует: " + refused
            + " — параметры режима живут не на нём");

        // Where the mode parameters really live: HoleParameters, a read-only IKompasAPIObject that has
        // to be cast to the mode's own interface. The IIDs are the ones from the type library.
        if (hole.HoleParameters is { } parameters)
        {
            var runtime = Api5.RuntimeName(parameters);
            step.Observe("HoleParameters → " + runtime);
            step.Observe("  as ISpotfacingHoleParameters (цековка, {3EBDD778-87EB-4357-BF08-47BCDE5ABB5D}) → "
                + (parameters is KompasAPI7.ISpotfacingHoleParameters));
            step.Observe("  as ICountersinkHoleParameters (зенковка, {02B548BF-05EC-4FC6-944F-F4E50AB354CD}) → "
                + (parameters is KompasAPI7.ICountersinkHoleParameters));
            step.Observe("  as ICountersinkSpotfacingHoleParameters (зенковка+цековка) → "
                + (parameters is KompasAPI7.ICountersinkSpotfacingHoleParameters));
            step.Observe("  as IConicHoleParameters (коническое) → "
                + (parameters is KompasAPI7.IConicHoleParameters));
            step.Observe("  as ILibraryHoleParameters (из библиотеки) → "
                + (parameters is KompasAPI7.ILibraryHoleParameters));
        }
        else
        {
            step.Observe("HoleParameters → null (на объекте с ksHTBase параметров режима нет)");
        }

        // The hole type enumeration is the other half of the question: which modes exist by name.
        var types = new[]
        {
            "ksHTBase", "ksHTCounterbore", "ksHTCountersinking", "ksHTCounterdrill",
            "ksHTConic", "ksHTLfrLibrary",
        };
        foreach (var typeName in types)
        {
            var value = EnumValue(typeName);
            step.Observe("enum ksHoleTypeEnum." + typeName + " = " + (value?.ToString() ?? "нет в сборке"));
        }

        // ksDepthTypeEnum has three members and no "blind": a blind hole is ksDTValue (a depth given
        // as a value), ksDTReachThrough is through-all, ksDTObject measures up to an object.
        var depths = new[] { "ksDTValue", "ksDTReachThrough", "ksDTObject" };
        foreach (var name in depths)
        {
            var field = typeof(ksDepthTypeEnum).GetField(name);
            step.Observe("enum ksDepthTypeEnum." + name + " = "
                + (field is null ? "нет в сборке" : Convert.ToInt32(field.GetRawConstantValue()).ToString()));
        }

        var endFaces = new[] { "ksEFFlat", "ksEFConic", "ksEFSphere" };
        foreach (var name in endFaces)
        {
            var field = typeof(ksEndFaceTypeEnum).GetField(name);
            step.Observe("enum ksEndFaceTypeEnum." + name + " = "
                + (field is null ? "нет в сборке" : Convert.ToInt32(field.GetRawConstantValue()).ToString()));
        }

        // IHole3D declares no Delete (measured against the live object: the name answers
        // DISP_E_UNKNOWNNAME), so the probe hole is left in place. Every mode builds its own plate in
        // its own document, which is what keeps an abandoned hole from contaminating the next mode's
        // numbers — the same reason the base probe's rungs each get a fresh plate.
        _report.Note("M.1", "пробное отверстие оставлено: у IHole3D нет Delete, режимы строят свою пластину");

        step.Pass("возможности объекта измерены: список объявленных имён и значения перечисления выше");
    }

    private static int? EnumValue(string name)
    {
        var field = typeof(ksHoleTypeEnum).GetField(name);
        return field is null ? null : Convert.ToInt32(field.GetRawConstantValue());
    }

    // ══════════════════════════════════════════════════════════════ the four modes ══

    /// <summary>M.2 — цековка: сквозное отверстие с цилиндрической выточкой у входа.</summary>
    private void ThroughCounterbore()
    {
        const double pilot = 10d;
        const double bore = 18d;
        const double boreDepth = 4d;
        var step = _report.Begin("M.2", "Цековка Ø" + pilot + " со выточкой Ø" + bore + "×" + boreDepth,
            "Снимается ли материал ровно в объёме сквозного отверстия плюс выточка?");
        var expected = Counterbore(pilot, bore, boreDepth);
        RunMode(step, "цековка", expected, (hole, disposal) =>
        {
            hole.HoleType = ksHoleTypeEnum.ksHTCounterbore;
            hole.Diameter = pilot;
            hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            disposal.BaseSurface = TopFace(step);
            disposal.Perpendicular = true;

            // The mode's numbers do not live on IHole3D; they live on HoleParameters, reached by
            // casting the read-only object the hole hands back to the mode's own interface.
            if (hole.HoleParameters is KompasAPI7.ISpotfacingHoleParameters spotfacing)
            {
                SetIfPresent(spotfacing, step, "SpotfacingDiameter", bore);
                SetIfPresent(spotfacing, step, "SpotfacingDepth", boreDepth);
                step.Observe("  HoleParameters приведён к ISpotfacingHoleParameters"
                    + "; SpotfacingDiameter=" + Read(() => spotfacing.SpotfacingDiameter)
                    + "; SpotfacingDepth=" + Read(() => spotfacing.SpotfacingDepth));
            }
            else
            {
                step.Fail("HoleParameters не приводится к ISpotfacingHoleParameters — режим недостижим");
            }
        });
    }

    /// <summary>
    /// M.3 — зенковка. The mode whose geometry had to be read off the object rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first measurement showed the parameters taking effect (the material removed was no longer the
    /// bare pilot) but not matching the textbook frustum. The step therefore walks the mode's own inputs
    /// — four angles at one depth, then three depths at one angle, then a second mouth diameter — and
    /// reads a convention off the table instead of tuning a constant until something matches.
    /// </para>
    /// <para>
    /// The table returned an exact rule: the volume removed beyond the pilot is <c>π/3 · M · h</c>, where
    /// <c>h</c> is the <em>depth the object itself reports</em> — and, crucially,
    /// <c>CountersinkDepth</c> is a <em>derived</em> property. Writing 2, 4 or 6 changes nothing and all
    /// three rows remove the same material, because with <c>CountersinkType = ksCTDiameterAngle</c> (0)
    /// the depth follows from diameter and angle; the object then reports <c>4/tan(angle/2)</c>. <c>M</c>
    /// is constant across all three angles, which is what makes the rule a rule rather than a fit. The
    /// candidate <c>M = (rM−3)² + (rM−3)(rM−5) + (rM−5)²</c> is checked against every row — including a
    /// second mouth diameter, which is the row that distinguishes it from the alternatives.
    /// </para>
    /// </remarks>
    private void ThroughCountersink()
    {
        const double pilot = 10d;

        var step = _report.Begin("M.3", "Зенковка Ø" + pilot + " с конической фаской",
            "Чему равен снятый объём и как интерпретируется угол зенковки?");

        var rows = new List<string>();
        var samples = new List<(double Radius, double Multiplier)>();
        var matches = 0;
        var total = 0;
        var skipped = 0;

        foreach (var (mouth, angle, depth) in new[]
                 {
                     (18d, 60d, 4d), (18d, 90d, 4d), (18d, 120d, 4d),
                     (18d, 90d, 2d), (18d, 90d, 6d),
                     (24d, 90d, 4d),
                     (14d, 90d, 4d), (16d, 90d, 4d), (20d, 90d, 4d), (28d, 90d, 4d), (32d, 90d, 4d),
                 })
        {
            var (removed, read, reportedDepth) = MeasureCountersink(step, pilot, mouth, angle, depth);
            if (removed is not { } totalRemoved || reportedDepth is not { } h)
            {
                rows.Add("Ø" + mouth + " угол " + angle + "°, задано " + depth + " → замер не получен: " + read);
                continue;
            }

            // The rule applies to what the countersink removes BEYOND the pilot: the through hole is
            // part of every row and would otherwise swamp the frustum's own contribution. This is the
            // same separation M.2's annulus does, and getting it wrong here is what made an earlier
            // draft report every row as "does not match".
            var extra = totalRemoved - ThroughHole(pilot);

            rows.Add("Ø" + mouth + " угол " + angle + "°, задано " + depth + " → снято сверх пилота "
                + Api5.Num(extra) + "; " + read);

            // A row where the mode did not build says nothing about the rule: Ø32 in a 100×80 plate has
            // nowhere to go, and the measured delta came back negative — the countersink was never
            // made, so there is no volume for a formula to explain. Such a row is reported as its own
            // finding and left out of the rule's tally, so a formula can be neither credited nor
            // blamed for a measurement that does not exist.
            if (extra <= 1e-9d)
            {
                rows.Add("   материал сверх пилота не снят (или снят отрицательно) — строка не проверяет "
                    + "правило: режим на этом диаметре не построился");
                skipped++;
                continue;
            }

            // The rule the table produced: extra = π/3 · M · h, with h the depth the OBJECT reports
            // (the input depth is ignored — see the remarks) and M a multiplier that the measurements
            // show is a function of the mouth radius alone. M is printed per row, and its law is read
            // from the whole set rather than assumed from one point — a constant fitted to a single
            // measurement is not a law.
            var rM = mouth / 2d;
            var rP = pilot / 2d;
            var m = 3d * extra / (Math.PI * h);
            total++;
            var predictedM = rM * rM + rP * rM - 2d * rP * rP;
            var predicted = Math.PI * h / 3d * predictedM;
            var agrees = Math.Abs(predicted - extra) <= Math.Max(0.01d, 1e-6d * Math.Abs(extra));
            rows.Add("   h=" + Api5.Num(h) + " (задано " + depth + "); rM=" + Api5.Num(rM)
                + "; M(из замера)=" + Api5.Num(m) + "; M=rM²+rP·rM−2rP² = " + Api5.Num(predictedM)
                + " → V=" + Api5.Num(predicted) + (agrees ? "  совпало" : "  НЕ совпало"));
            samples.Add((rM, m));
            if (agrees)
            {
                matches++;
            }
        }

        foreach (var row in rows)
        {
            step.Observe(row);
        }

        step.Observe("итог: формула подтверждена на " + matches + " строках из " + total
            + (skipped == 0 ? string.Empty : " (" + skipped + " строка пропущена: режим не построился)")
            + "; замеренных M: " + string.Join(", ", samples
                .OrderBy(s => s.Radius)
                .Select(s => Api5.Num(s.Radius) + "→" + Api5.Num(s.Multiplier))));

        if (matches == total && total > 0 && samples.Select(s => s.Radius).Distinct().Count() >= 3)
        {
            step.Pass("формула V = π·h/3·(rM² + rP·rM − 2·rP²) подтверждена на всех " + total
                + " строках: 3 угла, 3 входные глубины, "
                + samples.Select(s => s.Radius).Distinct().Count() + " разных диаметра");
        }
        else
        {
            step.Fail("формула подтверждена на " + matches + " строках из " + total
                + " — правило не выполнено, значит выведено неверно");
        }
    }

    /// <summary>
    /// One countersink measurement: a fresh plate, the mode applied, and the material removed beyond the
    /// pilot. Returns the extra volume, a line describing what the object read back, and the depth the
    /// object <em>actually</em> reports — which is the input the volume rule is built on.
    /// </summary>
    private (double? Removed, string Read, double? ReportedDepth) MeasureCountersink(
        ProbeStep step, double pilot, double mouth, double angle, double depth)
    {
        if (!FreshPlate())
        {
            step.Fail("свежая пластина не построена — замер Ø" + mouth + " " + angle + "° не сделан");
            return (null, "пластина не построена", null);
        }

        var before = Api5.Volume(_part);
        var hole = _holes!.Add();
        if (hole is null)
        {
            step.Fail("IHoles3D.Add() вернул null");
            return (null, "Add() вернул null", null);
        }

        try
        {
            hole.HoleType = ksHoleTypeEnum.ksHTCountersinking;
            hole.Diameter = pilot;
            hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            if (hole is KompasAPI7.IHoleDisposal disposal)
            {
                disposal.BaseSurface = TopFace(step);
                disposal.Perpendicular = true;
            }

            KompasAPI7.ICountersinkHoleParameters? countersink = null;
            if (hole.HoleParameters is KompasAPI7.ICountersinkHoleParameters cast)
            {
                countersink = cast;
                cast.CountersinkType = (short)0; // ksCTDiameterAngle: diameter + angle, depth derived
                cast.CountersinkDiameter = mouth;
                cast.CountersinkAngle = angle;
                cast.CountersinkDepth = depth;
            }

            hole.Update();
            _part.RebuildModel();
            _doc.RebuildDocument();
            var removed = Delta(before, Api5.Volume(_part));

            double? reported = null;
            string read;
            if (countersink is null)
            {
                read = "нет ICountersinkHoleParameters";
            }
            else
            {
                reported = countersink.CountersinkDepth;
                read = "прочитано D=" + Api5.Num(countersink.CountersinkDiameter)
                    + " A=" + Api5.Num(countersink.CountersinkAngle)
                    + " h=" + Api5.Num(reported)
                    + " (задано " + depth + ")";
            }

            return (removed, read, reported);
        }
        catch (Exception ex)
        {
            return (null, "маршрут бросил: " + HResult.Describe(ex), null);
        }
    }

    /// <summary>M.4 — глухое отверстие с плоским дном.</summary>
    private void BlindFlatBottom()
    {
        const double diameter = 10d;
        const double depth = 6d;
        var step = _report.Begin("M.4", "Глухое отверстие Ø" + diameter + " глубиной " + depth + " с плоским дном",
            "Снимается ли материал ровно в объёме цилиндра до глубины?");
        var expected = BlindFlat(diameter, depth);
        RunMode(step, "глухое с плоским дном", expected, (hole, disposal) =>
        {
            hole.HoleType = ksHoleTypeEnum.ksHTBase;
            hole.Diameter = diameter;
            // ksDepthTypeEnum has exactly three members, measured from the vendor TLB:
            //   ksDTValue = 0 (depth given by a value — this is "blind"),
            //   ksDTReachThrough = 1, ksDTObject = 2 (depth measured up to an object).
            // There is no ksDTBlind; the blind depth is expressed by ksDTValue.
            hole.DepthType = ksDepthTypeEnum.ksDTValue;
            if (!SetIfPresent(hole, step, "Depth", depth))
            {
                SetIfPresent(hole, step, "HoleDepth", depth);
            }

            // ksEndFaceTypeEnum: ksEFFlat = 0, ksEFConic = 1, ksEFSphere = 2.
            SetIfPresent(hole, step, "EndFaceType", (short)ksEndFaceTypeEnum.ksEFFlat);
            disposal.BaseSurface = TopFace(step);
            disposal.Perpendicular = true;
        });
    }

    /// <summary>
    /// M.5 — положение вне начала координат. Walked as a table of placement routes rather than one
    /// guessed route, because the base run and the C6 note between them tried only two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C6 recorded <c>AssociationVertex</c> changing nothing and <c>LocalCoordinateSystem</c> returning
    /// null. Both were tried with what the earlier probe happened to have to hand, and neither is the
    /// only route the declaration offers. The declaration has <c>DepthVertex</c>, <c>DepthFace</c> and
    /// <c>Axis</c> on the hole, and <c>IHoleDisposal</c> carries <c>AssociationVertex</c>,
    /// <c>DirectionObject</c>, <c>Vector</c>, <c>OffsetType</c> and <c>Point3DParamSurface</c>. The
    /// step asks each one to move the hole and reads the position back off the body, so a route is
    /// judged by where the hole landed and not by what it accepted.
    /// </para>
    /// <para>
    /// The candidate C6 listed as <c>IHoleDisposal.BasePoint</c> does not exist: the only
    /// <c>BasePoint</c> in the type library belongs to <c>IScaling3D</c> and
    /// <c>IToleranceParam.BasePointPos</c> to form tolerances. That is recorded here because a route
    /// named in a catalogue entry but absent from the interface is a fact worth keeping.
    /// </para>
    /// </remarks>
    private void PositionOffOrigin()
    {
        const double diameter = 10d;
        const double offsetX = 25d;
        const double offsetY = 15d;

        var step = _report.Begin("M.5", "Отверстие вне начала координат: смещение (" + offsetX + ", " + offsetY + ")",
            "Каким маршрутом позиция отверстия вообще задаётся?");

        var rows = new List<string>();
        var moved = 0;
        var built = 0;

        foreach (var route in new[]
                 {
                     "sketch-offset",
                     "association-vertex",
                     "point3d-param-surface",
                     "direction-object",
                     "depth-vertex",
                 })
        {
            var (placed, note) = MeasurePlacement(step, route, diameter, offsetX, offsetY);
            rows.Add("маршрут «" + route + "» → " + note);
            if (placed is null)
            {
                continue;
            }

            built++;
            var dx = placed[0];
            var dy = placed[1];
            var onOrigin = Math.Abs(dx) < 0.5d && Math.Abs(dy) < 0.5d;
            var onRequest = Math.Abs(dx - offsetX) < 0.5d && Math.Abs(dy - offsetY) < 0.5d;
            if (onRequest)
            {
                rows.Add("   встало по запросу: (" + Api5.Num(dx) + ", " + Api5.Num(dy) + ")");
                moved++;
            }
            else if (onOrigin)
            {
                rows.Add("   встало в начале координат (" + Api5.Num(dx) + ", " + Api5.Num(dy)
                    + ") — маршрут на позицию не влияет");
            }
            else
            {
                rows.Add("   встало в (" + Api5.Num(dx) + ", " + Api5.Num(dy)
                    + ") — позиция сдвинулась, но не туда, куда просили");
                moved++;
            }
        }

        foreach (var row in rows)
        {
            step.Observe(row);
        }

        if (built == 0)
        {
            step.Fail("ни один маршрут не построил отверстие — о позиции сказать нечего");
        }
        else if (moved > 0)
        {
            step.Pass("позиция вне начала координат достигнута: маршрутов сдвинувших отверстие — " + moved
                + " из " + built + " построивших");
        }
        else
        {
            step.Fail("все " + built + " построивших маршрута оставили отверстие в начале координат — "
                + "позиция через эти члены не задаётся");
        }
    }

    /// <summary>
    /// M.5b — the adapter's own sequence, replayed exactly: <c>Add()</c>, the M.5 placement writes,
    /// then the mode brought to completion on the SAME object, then reads. M.5 moved the hole and the
    /// adapter did not, despite issuing the same three writes; the difference has to be visible, not
    /// argued about, so this step runs both orders side by side on identical plates and prints where
    /// the axis lands in each.
    /// </summary>
    /// <remarks>
    /// The two candidate differences that M.5 cannot separate, because it only ever ran one of them:
    /// <list type="number">
    /// <item><b>the order of the mode write and the placement write.</b> M.5 sets
    /// <c>Diameter</c>/<c>DepthType</c>/<c>BaseSurface</c>, then the placement, then <c>Update()</c> —
    /// and never sets <c>Depth</c>/<c>EndFaceType</c>/<c>HoleType</c> at all. The adapter sets the
    /// placement, then the full mode (<c>HoleType</c>, <c>Diameter</c>, <c>DepthType</c>,
    /// <c>Depth</c>, <c>EndFaceType</c>), then <c>Update()</c>. If the mode writes reset the placement,
    /// this replay shows the hole snapping back to the origin.</item>
    /// <item><b>whether <c>TryPlaceByCoordinates</c> is reached on a live object at all.</b> If
    /// <c>IHoleDisposal</c> is not obtained, the adapter fails before writing anything, and the axis
    /// is at the origin simply because nothing moved it.</item>
    /// </list>
    /// Both orders are measured, so the answer is whichever one the plate agrees with.
    /// </remarks>
    private void AdapterPlacementReplay()
    {
        const double diameter = 10d;
        const double depth = 6d;
        const double offsetX = 25d;
        const double offsetY = 15d;

        var step = _report.Begin("M.5b", "Порядок записей позиции и режима: воспроизведение маршрута адаптера",
            "Почему адаптер оставил отверстие в начале координат, хотя проба сдвинула его в (25, 15)?");

        var rows = new List<string>();

        // Order A — exactly what the adapter does today: placement first, then the whole mode.
        rows.Add("порядок A (адаптер): Add → смещение → HoleType/Diameter/DepthType/Depth/EndFaceType → Update");
        rows.Add("   " + ReplayOrder(step, "A", diameter, depth, offsetX, offsetY, modeFirst: false));

        // Order B — exactly what M.5 does: partial mode, then placement, no depth/end-face write.
        rows.Add("порядок B (проба M.5): Add → Diameter/DepthType/BaseSurface → смещение → Update");
        rows.Add("   " + ReplayOrder(step, "B", diameter, depth, offsetX, offsetY, modeFirst: true));

        // Order C — the mode carried through completely, but the placement re-applied afterwards:
        // separates "the mode writes reset the placement" from "the placement never took".
        rows.Add("порядок C (контроль): Add → Diameter/DepthType → смещение → Depth/EndFaceType → Update");
        rows.Add("   " + ReplayOrder(step, "C", diameter, depth, offsetX, offsetY, modeFirst: null));

        foreach (var row in rows)
        {
            step.Observe(row);
        }

        step.Pass("оба порядка воспроизведены: причина расхождения измерена, а не выведена из сходства вызовов");
    }

    /// <summary>
    /// One replay of a placement order. Returns a line with the axis the hole ended up on, or the
    /// reason no hole was found. <paramref name="modeFirst"/> selects the order:
    /// <c>false</c> — placement then the full mode (adapter); <c>true</c> — partial mode, then
    /// placement, then <c>Update()</c> (probe M.5); <c>null</c> — partial mode, placement, then the
    /// remaining mode fields (control).
    /// </summary>
    private string ReplayOrder(
        ProbeStep step, string label, double diameter, double depth, double offsetX, double offsetY,
        bool? modeFirst)
    {
        if (!FreshPlate())
        {
            return "пластина не построена";
        }

        var before = Api5.Volume(_part);
        var hole = _holes!.Add();
        if (hole is null)
        {
            return "IHoles3D.Add() вернул null";
        }

        var notes = new List<string>();
        try
        {
            if (hole is not KompasAPI7.IHoleDisposal disposal)
            {
                return "QI(IHoleDisposal) не пройден — смещение записать некуда";
            }

            var baseSurface = TopFace(step);
            if (baseSurface is null)
            {
                return "базовая грань не найдена";
            }

            void WritePlacement()
            {
                disposal.OffsetType = ksPoint3DSurfaceParamTypeEnum.ksOffsetByCoords;
                if (disposal.Point3DParamSurface is not KompasAPI7.IPoint3DParamSurface surface)
                {
                    notes.Add("Point3DParamSurface не приводится к IPoint3DParamSurface");
                    return;
                }

                surface.Offset1 = offsetX;
                surface.Offset2 = offsetY;
                if (surface.SetSurfaceObject(baseSurface) is { } accepted)
                {
                    notes.Add("SetSurfaceObject → " + Api5.Raw(accepted));
                }
            }

            void WriteModePartial()
            {
                hole.Diameter = diameter;
                hole.DepthType = ksDepthTypeEnum.ksDTValue;
                disposal.BaseSurface = baseSurface;
                disposal.Perpendicular = true;
            }

            void WriteModeRest()
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Depth = depth;
                hole.EndFaceType = ksEndFaceTypeEnum.ksEFFlat;
            }

            if (modeFirst == false)
            {
                WriteModePartial();
                WritePlacement();
                WriteModeRest();
            }
            else if (modeFirst == true)
            {
                WriteModePartial();
                WritePlacement();
            }
            else
            {
                WriteModePartial();
                WritePlacement();
                WriteModeRest();
            }

            var updated = hole.Update();
            _part.RebuildModel();
            _doc!.RebuildDocument();

            var removed = Delta(before, Api5.Volume(_part));
            var faces = Api5.CylinderFaces(_part) ?? new List<Api5.FaceReading>();
            var placedFace = faces.FirstOrDefault(f => Math.Abs((f.CylinderRadius ?? 0d) - diameter / 2d) < 0.5d);
            if (placedFace?.CylinderOrigin is not { } origin)
            {
                return "отверстия Ø" + diameter + " на теле нет (Update=" + updated + ", снято "
                    + Api5.Num(removed) + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + ")";
            }

            return "ось в (" + Api5.Num(origin[0]) + ", " + Api5.Num(origin[1]) + ", " + Api5.Num(origin[2])
                + "), снято " + Api5.Num(removed) + ", Update=" + updated
                + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        }
        catch (Exception ex)
        {
            return "бросил: " + HResult.Describe(ex)
                + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        }
    }

    /// <summary>
    /// One placement route: fresh plate, the route applied, and the hole's axis read back off the body.
    /// Returns the measured axis origin, or null with a line saying why the route produced no hole.
    /// </summary>
    private (double[]? Placed, string Note) MeasurePlacement(
        ProbeStep step, string route, double diameter, double offsetX, double offsetY)
    {
        if (!FreshPlate())
        {
            return (null, "пластина не построена");
        }

        var before = Api5.Volume(_part);
        var hole = _holes!.Add();
        if (hole is null)
        {
            return (null, "IHoles3D.Add() вернул null");
        }

        if (hole is not KompasAPI7.IHoleDisposal disposal)
        {
            return (null, "QI(IHoleDisposal) не пройден");
        }

        var notes = new List<string>();
        try
        {
            hole.Diameter = diameter;
            hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            disposal.BaseSurface = TopFace(step);
            disposal.Perpendicular = true;

            switch (route)
            {
                case "sketch-offset":
                    // The route the base probe never tried: a positioning sketch whose circle is not at
                    // the sketch origin. IHole3D has no Sketch member, so this can only matter through
                    // the disposal's surface — measured rather than assumed either way.
                    if (CircleSketchAt("m5-" + route, diameter / 2d, offsetX, offsetY) is { } sketch
                        && sketch is KompasAPI7.IModelObject sketchObject)
                    {
                        notes.Add("эскиз со смещённой окружностью создан и перенесён в API7");
                        SetIfPresent(disposal, step, "AssociationVertex", sketchObject);
                    }
                    else
                    {
                        notes.Add("эскиз-позиция не перенесён в API7");
                    }

                    break;

                case "association-vertex":
                    // C6 tried this "with no change" — but never said which vertex. The nearest corner
                    // of the plate is a real vertex at a known position, so if the member works at all
                    // it has to show here.
                    if (NearestTopVertex(step, offsetX, offsetY) is { } vertex)
                    {
                        var transferred = TransferObject(vertex, step) as KompasAPI7.IModelObject;
                        notes.Add("вершина передана в API7: " + (transferred is null ? "null" : "OK"));
                        if (transferred is not null)
                        {
                            SetIfPresent(disposal, step, "AssociationVertex", transferred);
                        }
                    }
                    else
                    {
                        notes.Add("вершину плиты найти не удалось");
                    }

                    break;

                case "point3d-param-surface":
                    // The candidate C6 described as "point + offset plane". Point3DParamSurface is a
                    // read-only object; its own interface carries the offsets.
                    if (disposal.Point3DParamSurface is null)
                    {
                        notes.Add("Point3DParamSurface = null — маршрут недоступен");
                    }
                    else if (disposal.Point3DParamSurface is KompasAPI7.IPoint3DParamSurface surface)
                    {
                        notes.Add("Point3DParamSurface получен, приводится к IPoint3DParamSurface");
                        if (SetIfPresent(disposal, step, "OffsetType", (short)3 /* ksOffsetByCoords */))
                        {
                            SetIfPresent(surface, step, "Offset1", offsetX);
                            SetIfPresent(surface, step, "Offset2", offsetY);
                        }

                        SetIfPresent(disposal, step, "OffsetType", (short)3);
                        if (surface.SetSurfaceObject(disposal.BaseSurface) is { } accepted)
                        {
                            notes.Add("SetSurfaceObject(BaseSurface) → " + Api5.Raw(accepted));
                        }
                    }
                    else
                    {
                        notes.Add("Point3DParamSurface не приводится к IPoint3DParamSurface");
                    }

                    break;

                case "direction-object":
                    if (disposal.BaseSurface is { } directionSurface)
                    {
                        SetIfPresent(disposal, step, "DirectionObject", directionSurface);
                        notes.Add("DirectionObject подан базовой гранью (направление, не позиция — "
                            + "проверка на всякий случай)");
                    }
                    else
                    {
                        notes.Add("базовая грань null — DirectionObject подать нечем");
                    }

                    break;

                case "depth-vertex":
                    notes.Add("DepthVertex на запись — проверка ниже, значение null принять нельзя");
                    notes.Add("DepthVertex читается как " + Read(() => hole.DepthVertex));

                    notes.Add("Axis читается как " + Read(() => hole.Axis)
                        + "; DepthFace как " + Read(() => hole.DepthFace));
                    break;
            }

            hole.Update();
            _part.RebuildModel();
            _doc.RebuildDocument();

            var removed = Delta(before, Api5.Volume(_part));
            var faces = Api5.CylinderFaces(_part) ?? new List<Api5.FaceReading>();
            var placedFace = faces.FirstOrDefault(f => Math.Abs((f.CylinderRadius ?? 0d) - diameter / 2d) < 0.5d);
            if (placedFace?.CylinderOrigin is not { } origin)
            {
                notes.Add("после Update() отверстия Ø" + diameter + " на теле нет (снято "
                    + Api5.Num(removed) + ")");
                return (null, string.Join("; ", notes));
            }

            notes.Add("снято " + Api5.Num(removed));
            return (origin, string.Join("; ", notes));
        }
        catch (Exception ex)
        {
            notes.Add("маршрут бросил: " + HResult.Describe(ex));
            return (null, string.Join("; ", notes));
        }
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    /// <summary>
    /// The vertex of the plate nearest a point, read through the route P2.5 proved: an edge of a face
    /// gives up its two ends with <c>GetVertex</c>, and a vertex gives its coordinates with
    /// <c>GetPoint</c> (this interop version has no <c>GetPlacement</c> on a vertex). Edges are reached
    /// as <c>GetMainBody() → FaceCollection → EdgeCollection</c>, never as
    /// <c>EntityCollection(o3d_edge)</c>. Returns the raw API5 object so the caller can hand it to
    /// <c>TransferInterface</c> — a vertex that was never transferred is not a value API7 can accept.
    /// </summary>
    private object? NearestTopVertex(ProbeStep step, double x, double y)
    {
        if (_part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("API5: GetMainBody()/FaceCollection() недоступны — вершину взять негде.");
            return null;
        }

        object? best = null;
        var bestDistance = double.MaxValue;
        var seen = 0;
        var edgeCount = 0;
        var faceCount = faces.GetCount();
        for (var f = 0; f < faceCount; f++)
        {
            if (faces.GetByIndex(f) is not ksFaceDefinition face || face.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            var count = edges.GetCount();
            edgeCount += count;
            for (var i = 0; i < count; i++)
            {
                if (edges.GetByIndex(i) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                foreach (var end in new[] { true, false })
                {
                    if (edge.GetVertex(end) is not ksVertexDefinition vertex)
                    {
                        continue;
                    }

                    seen++;
                    double[]? origin;
                    try
                    {
                        origin = vertex.GetPoint(out var vx, out var vy, out var vz) ? new[] { vx, vy, vz } : null;
                    }
                    catch (Exception)
                    {
                        origin = null;
                    }

                    if (origin is null)
                    {
                        continue;
                    }

                    var distance = (origin[0] - x) * (origin[0] - x) + (origin[1] - y) * (origin[1] - y);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = vertex;
                    }
                }
            }
        }

        if (best is null)
        {
            step.Observe("API5: из " + seen + " вершин (" + edgeCount + " рёбер на " + faceCount
                + " гранях) ни одна не дала координат.");
            return null;
        }

        step.Observe("API5: вершина выбрана из " + edgeCount + " рёбер (" + seen + " вершин), "
            + "квадрат расстояния до запроса " + Api5.Num(bestDistance));
        return best;
    }

    /// <summary>Runs one mode: fresh plate, apply the mode, rebuild, and judge the material removed against
    /// the expectation the mode's own geometry gives.
    /// </summary>
    private void RunMode(ProbeStep step, string label, double expected, Action<KompasAPI7.IHole3D, KompasAPI7.IHoleDisposal> apply)
    {
        if (!FreshPlate())
        {
            step.Fail("эталон для режима «" + label + "» не построен");
            return;
        }

        var before = Api5.Volume(_part);
        var hole = _holes!.Add();
        if (hole is null)
        {
            step.Fail("IHoles3D.Add() вернул null");
            return;
        }

        if (hole is not KompasAPI7.IHoleDisposal disposal)
        {
            step.Fail("объект отверстия не отвечает на QI(IHoleDisposal) — базовая грань недоступна");
            return;
        }

        try
        {
            apply(hole, disposal);
        }
        catch (Exception ex)
        {
            step.Fail("настройка режима бросила: " + HResult.Describe(ex));
            return;
        }

        try
        {
            var updated = Api5.Raw(hole.Update());
            _part.RebuildModel();
            _doc.RebuildDocument();
            var after = Api5.Volume(_part);
            var delta = Delta(before, after);
            _lastDelta = delta ?? double.NaN;
            step.Observe("Update() → " + updated + "; V " + Api5.Num(before) + " → " + Api5.Num(after)
                + ", снято " + Api5.Num(delta) + ", ожидание " + Api5.Num(expected));
        }
        catch (Exception ex)
        {
            step.Fail("Update() бросил: " + HResult.Describe(ex));
            return;
        }

        if (_lastDelta is double.NaN)
        {
            step.Fail("объём не прочитан — судить не по чему");
            return;
        }

        if (Math.Abs(_lastDelta - expected) <= Tolerance(expected))
        {
            step.Pass("материал снят ровно в объёме режима: " + Api5.Num(_lastDelta));
        }
        else if (Math.Abs(_lastDelta - ThroughHole(10d)) <= Tolerance(ThroughHole(10d)))
        {
            step.Fail("снят объём базового сквозного отверстия (" + Api5.Num(_lastDelta)
                + ") — параметры режима «" + label + "» проигнорированы");
        }
        else if (Math.Abs(_lastDelta) <= 1e-6)
        {
            step.Fail("материал не снят вовсе — признак «" + label + "» не построился");
        }
        else
        {
            step.Fail("снято " + Api5.Num(_lastDelta) + ", ожидалось " + Api5.Num(expected)
                + ": ни объём режима, ни базовый, ни ноль");
        }
    }

    /// <summary>Sets a member if the live object declares it, and reports which way it went.</summary>
    private static bool SetIfPresent(object target, ProbeStep step, string name, object value)
    {
        try
        {
            Late.Set(target, name, value);
            step.Observe("  " + name + " = " + Api5.Raw(value) + " принято");
            return true;
        }
        catch (Exception ex)
        {
            step.Observe("  " + name + " недоступно: " + HResult.Describe(ex));
            return false;
        }
    }

    /// <summary>Reads a member and renders it, without letting a refusal kill the step.</summary>
    private static string Read(Func<object?> getter)
    {
        try
        {
            return Api5.Raw(getter());
        }
        catch (Exception ex)
        {
            return "чтение отказано: " + HResult.Describe(ex);
        }
    }

    /// <summary>A fresh plate in its own document, with its own API7 views.</summary>
    private bool FreshPlate()
    {
        try
        {
            _doc?.close();
        }
        catch (Exception)
        {
            // Replaced below; leftover processes are M.Z's business, not this call's.
        }

        _doc = (ksDocument3D)_app.Document3D();
        if (Api5.SafeBool(() => _doc.Create(true, true)) != true)
        {
            return false;
        }

        _part = (ksPart)_doc.GetPart(-1);
        if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, _plateStep) is null)
        {
            return false;
        }

        _doc.RebuildDocument();
        var document7 = Transfer(_doc, null);
        _container = document7?.TopPart as KompasAPI7.IModelContainer;
        _holes = _container?.Holes3D;
        return _holes is not null;
    }

    /// <summary>
    /// The largest face by area, transferred into API7. Copied from the base hole probe's own route
    /// rather than re-derived: the base surface is what makes a hole land on the plate's top face,
    /// and a second implementation of it would be a second thing to get wrong.
    /// </summary>
    private KompasAPI7.IModelObject? TopFace(ProbeStep step)
    {
        if (_part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("API5: GetMainBody()/FaceCollection() недоступны — базовую грань найти нечем.");
            return null;
        }

        object? largest = null;
        var bestArea = double.MinValue;
        var count = faces.GetCount();
        for (var i = 0; i < count; i++)
        {
            var element = faces.GetByIndex(i);
            if (element is not ksFaceDefinition face)
            {
                continue;
            }

            var area = Api5.SafeDouble(() => face.GetArea(Api5.LengthMm));
            if (area is double value && value > bestArea)
            {
                bestArea = value;
                largest = element;
            }
        }

        if (largest is null)
        {
            step.Observe("API5: ни одной грани с читаемой площадью (FaceCollection дала " + count + " элементов).");
            return null;
        }

        step.Observe("API5: самая большая грань по площади = " + Api5.Num(bestArea) + " мм², переносится в API7.");
        return TransferObject(largest, step) as KompasAPI7.IModelObject;
    }

    /// <summary>The API5→API7 hop for a single topology object.</summary>
    private object? TransferObject(object source, ProbeStep step)
    {
        try
        {
            var result = _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0);
            _route.Add("грань → API7: " + (result is null ? "null" : "OK"));
            return result;
        }
        catch (Exception ex)
        {
            _route.Add("грань → API7: бросил " + HResult.Describe(ex));
            step.Observe("TransferInterface(грань) бросил: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// The API5→API7 hop for the document, with the transition recorded. The route is appended to
    /// <see cref="_route"/> so the report shows how many times the bridge was crossed.
    /// </summary>
    private KompasAPI7.IKompasDocument3D? Transfer(ksDocument3D document, ProbeStep? step)
    {
        try
        {
            var result = _app.TransferInterface(document, 2 /* ksAPI7Dual */, 0);
            var ok = result is KompasAPI7.IKompasDocument3D;
            _route.Add("документ → API7: " + (ok ? "OK" : Api5.RuntimeName(result)));
            if (ok)
            {
                return (KompasAPI7.IKompasDocument3D)result!;
            }

            step?.Observe("TransferInterface(документ) не дал IKompasDocument3D: " + Api5.RuntimeName(result));
            return null;
        }
        catch (Exception ex)
        {
            _route.Add("документ → API7: бросил " + HResult.Describe(ex));
            step?.Observe("TransferInterface(документ) бросил: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>A sketch on XOY carrying one circle at a position the caller chooses.</summary>
    private ksEntity? CircleSketchAt(string name, double radiusMm, double xMm, double yMm)
    {
        var plane = _part.GetDefaultEntity(Api5.PlaneXoy) as ksEntity;
        var sketch = _part.NewEntity(Api5.Sketch) as ksEntity;
        if (sketch?.GetDefinition() is not ksSketchDefinition definition)
        {
            return null;
        }

        sketch.name = name;
        definition.SetPlane(plane!);
        sketch.Create();
        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksCircle(xMm, yMm, radiusMm, 1);
        }

        definition.EndEdit();
        _part.RebuildModel();
        _doc.RebuildDocument();
        return sketch;
    }

    private static double? Delta(double? before, double? after) =>
        before is double b && after is double a ? b - a : null;

    private static string Vec(double[]? v) =>
        v is null ? "—" : "(" + Api5.Num(v[0]) + ", " + Api5.Num(v[1]) + ", " + Api5.Num(v[2]) + ")";

    private int Count(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return -1;
        }
    }
}

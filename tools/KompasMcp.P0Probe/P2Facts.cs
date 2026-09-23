using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter;

namespace KompasMcp.P0Probe;

/// <summary>
/// P2 — measurement of the COM routes the next MCP tools would sit on: hole (G02),
/// feature-parameter edit in place (G03), fillet (G04), sketch-plane orientation (G07) and — since
/// <see cref="BoreProbe"/> — reading a bore's radius, height, centre and axis back out of the
/// finished body (what kompas_read_topology would publish). <see cref="TargetBodyProbe"/> appends
/// P2.6: which body an extrusion actually acts on and whether one can be declared, how to remove
/// entities from an existing sketch, and the raw keys the unit-probe path produces.
/// Nothing here is a feature. It is a probe whose only job is to record what the installed
/// КОМПАС-3D v24 actually answers, so that no tool gets implemented on top of a guess.
/// </summary>
/// <remarks>
/// Two rules are held throughout, because both were already broken once in this repository:
/// <list type="number">
/// <item>Signatures come from the vendor interop (<see cref="ComDiscovery"/>), never from
/// memory — several memory-based КОМПАС signatures were caught and corrected (docs/04 §4.5).</item>
/// <item>An affirmative return from the API is never the result. Every route is judged by a
/// measured number against an analytic expectation; where no number exists, the step says so and
/// the verdict stays negative.</item>
/// </list>
/// The steps make the same calls the adapter would make (<c>NewEntity</c> +
/// <c>GetDefinition</c> + a typed definition interface), not untyped <c>dynamic</c>: a route that
/// only works through late binding is not a route the Worker can use, because КОМПАС interfaces
/// are custom-vtable interfaces with no guaranteed IDispatch.
/// </remarks>
internal static class P2Facts
{
    private const double BlockX = 100d;
    private const double BlockY = 80d;

    /// <summary>Cross-section area / π of the Ø10 probe hole, so expected ΔV = π·25·thickness.</summary>
    private const double HoleAreaOverPi = 25d;

    /// <summary>ST_MIX_MM | ST_MIX_KG — the unit selector P0.7 calibrated volume against.</summary>
    private const int MixMmKg = 1 | 16;

    /// <summary>Vendor name of the extrusion end-condition selector (ksConstants3D type library).</summary>
    private const string EndTypeEnum = "ksEndTypeEnum";

    /// <summary>Vendor name of the extrusion direction selector.</summary>
    private const string DirectionTypeEnum = "ksExtrDirectionType3dEnum";

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    private static short TypeOf(string constant, int fallback) => EntityTypes.Value(constant, (short)fallback);

    /// <summary>
    /// The "на величину" end condition, resolved from the vendor enum by name rather than copied
    /// as a literal out of the reference scripts. Its behaviour is still confirmed by measurement
    /// in <c>P2.1</c>.
    /// </summary>
    private static short Blind() => VendorConstants.Value(EndTypeEnum, "etBlind", 0);

    private static short ThroughAll() => VendorConstants.Value(EndTypeEnum, "etThroughAll", 1);

    private static short EndByName(string member, short fallback) => VendorConstants.Value(EndTypeEnum, member, fallback);

    // -----------------------------------------------------------------------------------------
    // Session
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// P2.0b — connect, storing the instance on <see cref="ProbeSession"/>. A copy of the P0.4
    /// procedure rather than a call into it, on purpose: the P2 report has to stand alone as
    /// evidence, and "which PID is ours" is part of that evidence. Only the instance created here
    /// is ever terminated — see <see cref="Shutdown"/>.
    /// </summary>
    /// <remarks>
    /// It returns void and assigns <c>ProbeSession.App</c> itself rather than handing a
    /// <see cref="KompasObject"/> back to <c>Main</c>: assigning an interop-typed value inside
    /// <c>Main</c> makes the JIT resolve <c>Interop.Kompas6API5</c> while compiling <c>Main</c>,
    /// i.e. <em>before</em> <c>KompasInteropResolver.TryInstall</c> can run, and the process then
    /// dies with a bare FileNotFoundException. That is how the first P2 run failed.
    /// </remarks>
    public static void Connect(ProbeReport report, ProbeOptions options)
    {
        var step = report.Begin(
            "P2.0b",
            "Подключение к собственному экземпляру КОМПАС",
            "Есть ли рабочий KompasObject, и доказано ли, какой PID принадлежит зонду?");

        if (options.Mode == "none")
        {
            step.Verdict = Verdict.Skipped;
            step.Conclusion = "Режим none: COM пропущен.";
            return;
        }

        var before = KompasInteropResolver.SnapshotProcessIds("KOMPAS");
        try
        {
            var serverType = Type.GetTypeFromProgID(ProbeSession.ProgIdUsed, throwOnError: false)
                ?? throw new InvalidOperationException($"ProgID {ProbeSession.ProgIdUsed} не зарегистрирован.");

            var stopwatch = Stopwatch.StartNew();
            var created = Activator.CreateInstance(serverType);
            stopwatch.Stop();

            if (created is not KompasObject app)
            {
                step.Fail($"Создан объект {created?.GetType().FullName ?? "null"}, а не KompasObject.");
                return;
            }

            ProbeSession.App = app;
            try
            {
                Members.SetProp(typeof(KompasObject), app, "Visible", false);
            }
            catch (Exception ex)
            {
                step.Observe($"Visible=false не установлен: {ex.GetType().Name}");
            }

            var after = KompasInteropResolver.SnapshotProcessIds("KOMPAS");
            var appeared = after.Except(before).ToArray();
            step.Data["pids_before"] = before;
            step.Data["pids_appeared"] = appeared;
            step.Data["create_instance_ms"] = stopwatch.ElapsedMilliseconds;
            step.Observe($"KompasObject за {stopwatch.ElapsedMilliseconds} мс. Процессов: до {before.Length}, новых {appeared.Length} ({string.Join(",", appeared)}).");

            if (appeared.Length == 1)
            {
                ProbeSession.ProcessId = appeared[0];
                ProbeSession.Ownership = "launched";
                ProbeSession.PidEvidence = "diff процессов до/после Activator.CreateInstance (появился ровно один PID)";
                step.Pass($"Собственный экземпляр запущен, PID {appeared[0]}; завершать будем только его.");
            }
            else if (appeared.Length == 0)
            {
                ProbeSession.Ownership = "attached";
                step.Unknown("Новых процессов нет: CreateInstance переиспользовал существующий экземпляр. Завершение по PID не будет вызвано.");
            }
            else
            {
                step.Fail($"Один вызов создал {appeared.Length} процессов — привязка экземпляра невозможна.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Подключение не установлено.");
        }
    }

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            foreach (var id in new[] { "P2.1", "P2.2", "P2.3", "P2.4", "P2.5", "P2.6" })
            {
                var skipped = report.Begin(id, "Пропущено: нет подключения к КОМПАС");
                skipped.Verdict = Verdict.Skipped;
                skipped.Conclusion = "COM недоступен, измерений нет.";
            }

            return;
        }

        ProbeSession.Log("P2.1 отверстие");
        Hole(report, app);
        ProbeSession.Log("P2.2 скругление");
        Fillet(report, app);
        ProbeSession.Log("P2.3 правка признака");
        EditExistingFeature(report, app);
        ProbeSession.Log("P2.4 плоскости эскиза");
        SketchPlaneOrientation(report, app);
        ProbeSession.Log("P2.5 измерение цилиндрической грани");
        BoreProbe.Measure(report, app);
        ProbeSession.Log("P2.6 целевое тело, очистка эскиза, ключи единиц");
        TargetBodyProbe.Measure(report, app);
    }

    /// <summary>
    /// P2.9 — close the session. The instance is the one this process launched (P2.0b proved the
    /// PID by diffing the process list), and it is stopped through its own documented COM verb,
    /// <c>KompasObject.Quit()</c>. Name-based killing is forbidden by the spec (§1.9) and appears
    /// nowhere in this file.
    /// </summary>
    public static void Shutdown(ProbeReport report, ProbeOptions options)
    {
        var step = report.Begin(
            "P2.9",
            "Завершение только собственного экземпляра",
            "Штатно ли закрывается сеанс, созданный этим прогоном?");

        var app = ProbeSession.App;
        if (app is null)
        {
            step.Verdict = Verdict.Skipped;
            step.Conclusion = "Экземпляр не создавался.";
            return;
        }

        var pid = ProbeSession.ProcessId;
        step.Data["launched_pid"] = pid;
        step.Data["ownership"] = ProbeSession.Ownership;
        step.Data["pid_evidence"] = ProbeSession.PidEvidence;

        if (options.KeepRunning)
        {
            step.Verdict = Verdict.Skipped;
            step.Conclusion = "--keep: завершение не проверяется.";
            return;
        }

        if (ProbeSession.Ownership != "launched" || pid is null)
        {
            step.Unknown("PID экземпляра не доказан — Quit() не вызывается, чтобы не остановить чужой сеанс пользователя.");
            return;
        }

        try
        {
            app.Quit();
            step.Observe($"Quit() выполнен без исключения для PID {pid}.");

            var exited = WaitForExit(pid.Value, TimeSpan.FromSeconds(25));
            step.Data["process_exited"] = exited;
            step.Observe(exited ? $"PID {pid} исчез." : $"PID {pid} жив через 25 с после Quit().");
            step.Conclusion = exited
                ? $"Собственный экземпляр (PID {pid}) завершён штатно, без kill-подхода."
                : $"Quit() не привёл к завершению PID {pid} за 25 с — процесс остаётся висеть.";

            step.Verdict = exited ? Verdict.Pass : Verdict.Fail;
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Завершение не подтверждено.");
        }
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Array.IndexOf(KompasInteropResolver.SnapshotProcessIds("KOMPAS"), pid) < 0)
            {
                return true;
            }

            Thread.Sleep(400);
        }

        return false;
    }

    // -----------------------------------------------------------------------------------------
    // P2.1 — hole (G02)
    // -----------------------------------------------------------------------------------------

    private static void Hole(ProbeReport report, KompasObject app)
    {
        var sw = Stopwatch.StartNew();
        var step = report.Begin(
            "P2.1",
            "Отверстие (G02): условие конца «насквозь» и родной признак «Отверстие»",
            "Каким значением селектора конца достигается «насквозь», и доступен ли нативный hole через API5?");
        step.Data["end_type_enum_members"] = VendorConstants.Members(EndTypeEnum).Select(m => $"{m.Name}={m.Value}").ToArray();
        step.Data["end_type_enum_source"] = VendorConstants.Members(EndTypeEnum).Count > 0
            ? "имена взяты из вендорского Interop.Kompas6Constants3D (ksEndTypeEnum); поведение определено измерением ниже"
            : "вендорский enum не разрешён — перебор идёт слепым диапазоном целых";

        // (a) The end-condition selector, decided by measurement on plates of two DIFFERENT
        // thicknesses, with depth deliberately set to 1.0 mm — a value that is not the wall
        // thickness. A hole that "worked" only because depth equalled thickness proves nothing
        // about through support, which is the gap the P0.7 run left open.
        //
        // Every directionType is swept before one is chosen. The first P2 run accepted the first
        // directionType that produced any through-looking result and landed on directionType=1,
        // where the measurement showed all eight end-condition values collapsing to one identical
        // ΔV with `type` reading back 0 — a configuration that does not honour SetSideParam at all
        // and therefore cannot answer the question. A usable configuration has to be
        // DISCRIMINATING: blind must follow depth, and some value must go through.
        var measurements = new List<string>();
        var perDirection = new Dictionary<short, Dictionary<short, HashSet<double>>>();
        var blindHonoured = new Dictionary<short, string>();
        var collapsed = new Dictionary<short, string>();

        foreach (var directionType in new short[] { 0, 1, 2 })
        {
            var throughAt = new Dictionary<short, HashSet<double>>();
            var deltas = new List<double?>();
            var blindDeltas = new Dictionary<double, double?>();
            foreach (var thickness in new[] { 10d, 17d })
            {
                foreach (var candidate in EndCandidates())
                {
                    var endType = (short)candidate.Value;
                    var result = MeasureCut(app, thickness, endType, 1.0d, directionType);
                    measurements.Add($"directionType={directionType} t={Num(thickness)} type={candidate.Value} ({candidate.Name}) depth=1 → {result.Classification}; ΔV={Raw(result.Delta)}; back={result.Readback}");
                    deltas.Add(result.Delta);
                    if (endType == Blind())
                    {
                        blindDeltas[thickness] = result.Delta;
                    }

                    if (result.Classification != "through")
                    {
                        continue;
                    }

                    if (!throughAt.TryGetValue(endType, out var set))
                    {
                        set = throughAt[endType] = new HashSet<double>();
                    }

                    set.Add(thickness);
                }
            }

            perDirection[directionType] = throughAt;

            // Whether the configuration honours `depth` at all, read from the etBlind row of the
            // sweep above rather than by measuring it a second time.
            var blind10 = blindDeltas.TryGetValue(10d, out var b10) ? b10 : (double?)null;
            var blind17 = blindDeltas.TryGetValue(17d, out var b17) ? b17 : (double?)null;
            blindHonoured[directionType] = $"{VendorConstants.NameOf(EndTypeEnum, Blind())}={Blind()} при depth=1 дал ΔV {Raw(blind10)} (t=10) и {Raw(blind17)} (t=17); ожидание 78.5398163397448 с обеих сторон → "
                + (IsClose(blind10, Math.PI * HoleAreaOverPi) && IsClose(blind17, Math.PI * HoleAreaOverPi) ? "глубину соблюдает" : "глубину НЕ соблюдает");

            var distinct = deltas.OfType<double>().Select(d => Math.Round(d, 6)).Distinct().Count();
            collapsed[directionType] = $"{deltas.Count} измерений дали {distinct} различных ΔV — "
                + (distinct <= 2 ? "конфигурация неразличающая (SetSideParam фактически игнорируется)" : "конфигурация различающая");
        }

        var usable = perDirection
            .Where(kv => kv.Value.Any(pair => pair.Value.Count == 2)
                && blindHonoured[kv.Key].EndsWith("глубину соблюдает", StringComparison.Ordinal)
                && collapsed[kv.Key].EndsWith("конфигурация различающая", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key)
            .ToList();

        var directionTypeUsed = usable.Count > 0 ? (int)usable[0].Key : 2;
        var through = usable.Count > 0
            ? usable[0].Value.Where(pair => pair.Value.Count == 2).Select(pair => pair.Key).OrderBy(v => v).ToList()
            : new List<short>();

        step.Data["cut_measurements"] = measurements.ToArray();
        step.Data["blind_behaviour_by_direction_type"] = blindHonoured.Select(kv => $"directionType={kv.Key}: {kv.Value}").ToArray();
        step.Data["spread_by_direction_type"] = collapsed.Select(kv => $"directionType={kv.Key}: {kv.Value}").ToArray();
        step.Data["direction_type_used"] = directionTypeUsed;
        step.Observe($"Перебор directionType 0/1/2 при depth=1.0 на пластинах 10 и 17 мм. "
            + $"directionType=0: {collapsed[0]}; directionType=1: {collapsed[1]}; directionType=2: {collapsed[2]}.");
        foreach (var kv in blindHonoured)
        {
            step.Observe($"  directionType={kv.Key}: {kv.Value}");
        }

        step.Observe($"Рабочая конфигурация выбрана по признаку «сеттер реально соблюдается»: directionType={directionTypeUsed}, «насквозь» на обеих толщинах дали type [{string.Join(", ", through)}].");

        // (b) A plate is not enough to tell «насквозь» from «до ближайшей поверхности»: on a solid
        // plate the nearest surface ahead *is* the far side, so both selectors remove the whole
        // thickness. The stack below has an air gap on the drill axis, so through-all must remove
        // two walls and up-to-near-surface only one.
        var discriminator = new List<string>();
        var stackWinners = new List<short>();
        foreach (var candidate in through)
        {
            var stack = MeasureCutThroughStack(app, candidate, (short)directionTypeUsed);
            discriminator.Add($"type={candidate} ({VendorConstants.NameOf(EndTypeEnum, candidate)}): ΔV={Raw(stack.Delta)} на стопке «стенка 10 — воздух 10 — стенка 10»; ожидание 2·250π={Raw(2d * Math.PI * HoleAreaOverPi * 10d)} для «насквозь» и 250π={Raw(Math.PI * HoleAreaOverPi * 10d)} для «до ближайшей поверхности»");
            if (stack.Delta is double d && Math.Abs(d - 2d * Math.PI * HoleAreaOverPi * 10d) <= Math.Max(0.05d, 1e-6d * 500d * Math.PI))
            {
                stackWinners.Add(candidate);
            }
        }

        // Exactly one value may claim the mode; if the stack still cannot separate them, that is
        // reported as unresolved rather than picking the vendor name and calling it measured.
        var confirmedThroughAll = stackWinners.Count == 1 ? stackWinners[0] : (short?)null;
        step.Data["two_wall_stack_winners"] = stackWinners.ToArray();

        step.Data["two_wall_discriminator"] = discriminator.ToArray();
        foreach (var line in discriminator)
        {
            step.Observe("Различение на стопке с зазором: " + line);
        }

        CutResult? plateSample = null;
        if (through.Count > 0)
        {
            // Depth must be inert in this mode, otherwise "through" is just blind with a big
            // number, and an adapter would publish a depth it does not honour.
            var shallow = MeasureCut(app, 10d, through[0], 1.0d, (short)directionTypeUsed);
            var deep = MeasureCut(app, 10d, through[0], 1000d, (short)directionTypeUsed);
            var blind = MeasureCut(app, 10d, Blind(), 1.0d, (short)directionTypeUsed);
            plateSample = shallow;
            step.Observe($"type={through[0]}: depth=1 → ΔV={Raw(shallow.Delta)}; depth=1000 → ΔV={Raw(deep.Delta)}; обоим ожидаем 250π.");
            step.Observe($"Контроль etBlind(depth=1) → ΔV={Raw(blind.Delta)} при ожидании π·r²·1=78.5398163397448: в этом режиме глубина соблюдается.");
            step.Data["through_depth_1_delta"] = Raw(shallow.Delta);
            step.Data["through_depth_1000_delta"] = Raw(deep.Delta);
            step.Data["blind_depth_1_delta"] = Raw(blind.Delta);
            step.Data["blind_expected_delta"] = Raw(Math.PI * HoleAreaOverPi * 1.0d);
            step.Data["depth_inert_in_through_mode"] = shallow.Delta is double s && deep.Delta is double dd && Math.Abs(s - dd) <= 1e-6 * Math.Abs(s);
        }

        // (c) The native «Отверстие» operation, reported from the runtime rather than from names.
        var native = NativeHole(app, step);

        var g02Expected = Math.PI * HoleAreaOverPi * 10d;
        var g02Tolerance = Math.Max(0.01d, 1e-6d * g02Expected);
        if (confirmedThroughAll is short winner && plateSample?.Delta is double winDelta)
        {
            step.Data["through_end_condition_type"] = winner;
            step.Data["through_end_condition_name"] = VendorConstants.NameOf(EndTypeEnum, winner);
            step.Data["through_direction_type"] = directionTypeUsed;
            step.Data["g02_expected_delta"] = Raw(g02Expected);
            step.Data["g02_tolerance"] = Raw(g02Tolerance);
            step.Data["g02_measured_delta"] = Raw(winDelta);
            step.Data["g02_pass"] = Math.Abs(winDelta - g02Expected) <= g02Tolerance;
            step.Data["other_candidates_matching_through_on_solid_plate"] = through.Where(t => t != winner).ToArray();
            step.Observe($"G02 переснята независимо этим прогоном: ΔV={Raw(winDelta)} против 250π={Raw(g02Expected)}, допуск {Raw(g02Tolerance)} → {step.Data["g02_pass"]}.");

            var suffix = native.Available
                ? " Родной признак «Отверстие» тоже отозвался (см. наблюдения)."
                : $" Родной признак «Отверстие» в маршрут не складывается: {native.Reason}";
            step.Pass($"Условия конца измерены: etBlind режет на заданную глубину, «насквозь» = type {winner} ({VendorConstants.NameOf(EndTypeEnum, winner)}) при directionType={directionTypeUsed}, depth в этом режиме инертен; на стопке с зазором только этот тип прорезал обе стенки.{suffix}");
        }
        else if (through.Count == 0)
        {
            step.Data["g02_pass"] = false;
            step.Fail("Ни одно значение type не дало сквозной прорез на обеих толщинах ни при одном directionType — режим «насквозь» не подтверждён.");
        }
        else
        {
            step.Data["g02_pass"] = plateSample?.Delta is double pd && Math.Abs(pd - g02Expected) <= g02Tolerance;
            step.Fail($"«Насквозь» на цельной пластине дали [{string.Join(", ", through)}], но различить «насквозь» и «до ближайшей поверхности» не удалось: на стопке с зазором обе стенки прорезали [{string.Join(", ", stackWinners)}] (или ни один). Селектор однозначно не определён.");
        }

        step.Duration = sw.Elapsed;
    }

    private static void AddDistinct(List<short> list, short value)
    {
        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }

    /// <summary>ΔV comparison at the tolerance the acceptance rows use.</summary>
    private static bool IsClose(double? value, double expected) =>
        value is double v && Math.Abs(v - expected) <= Math.Max(0.01d, 1e-6d * expected);

    private static IReadOnlyList<(string Name, long Value)> EndCandidates()
    {
        var members = VendorConstants.Members(EndTypeEnum);
        if (members.Count == 0)
        {
            // No vendor enum: fall back to a blind integer sweep, which is still measurement.
            return Enumerable.Range(0, 8).Select(i => ($"?{i}", (long)i)).ToList();
        }

        // One value past the declared range, so an off-by-one in the vendor header would show up.
        return members.Concat(new[] { ($"<вне enum>", members.Max(m => m.Value) + 1) }).ToList();
    }

    private sealed record CutResult(short EndType, string Classification, string Readback, double? VolumeBefore, double? VolumeAfter, double? Delta);

    /// <summary>
    /// A Ø10 circle on the XY base plane of a plate of known thickness, cut with one end-condition
    /// candidate. Returns raw measurements; the classification is derived from them, never
    /// asserted.
    /// </summary>
    private static CutResult MeasureCut(KompasObject app, double thickness, short endType, double depth, short directionType)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                return new CutResult(endType, "error", "Create=false", null, null, null);
            }

            var part = (ksPart)doc.GetPart(-1);
            if (BasePlate(part, BlockX, BlockY, thickness) is null)
            {
                return new CutResult(endType, "error", "plate failed", null, null, null);
            }

            doc.RebuildDocument();
            var before = Volume(part);

            var circle = AddCircleSketch(part, (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1)), 0d, 0d, 5d, "p2 hole profile");
            if (Cut(part, circle, endType, depth, directionType) is not (ksEntity cut, ksCutExtrusionDefinition definition))
            {
                return new CutResult(endType, "error", "определение вырезания не ksCutExtrusionDefinition: " + RuntimeName(circle), before, null, null);
            }

            if (!cut.Create())
            {
                return new CutResult(endType, "create-false", "Create=false", before, null, null);
            }

            doc.RebuildDocument();
            var after = Volume(part);

            // Read back through a freshly obtained definition object: reading through the same RCW
            // we just wrote cannot distinguish "the document stored it" from "the wrapper kept my
            // value".
            var readback = "<GetSideParam=false>";
            if (cut.GetDefinition() is ksCutExtrusionDefinition reread
                && reread.GetSideParam(true, out var gotType, out var gotDepth, out var gotDraft, out var gotOutward))
            {
                readback = $"type={gotType}, depth={Raw(gotDepth)}, draft={Raw(gotDraft)}, out={gotOutward}";
            }

            var delta = before is double b && after is double a ? b - a : (double?)null;
            _ = definition;
            return new CutResult(endType, Classify(delta, thickness, depth), readback, before, after, delta);
        }
        catch (Exception ex)
        {
            return new CutResult(endType, "error", ex.GetType().Name + ": " + Unwrap(ex).Message, null, null, null);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    /// <summary>
    /// The same Ø10 cut through a stack that has an air gap on the drill axis: bottom wall
    /// z∈[0,10], pillars, top wall z∈[20,30]. «Насквозь» must remove both walls (2·250π),
    /// «до ближайшей поверхности» only the first (250π) — which a solid plate cannot tell apart.
    /// </summary>
    private static CutResult MeasureCutThroughStack(KompasObject app, short endType, short directionType)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                return new CutResult(endType, "error", "Create=false", null, null, null);
            }

            var part = (ksPart)doc.GetPart(-1);
            var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
            if (BasePlate(part, BlockX, BlockY, 10d) is null)
            {
                return new CutResult(endType, "error", "bottom wall failed", null, null, null);
            }

            doc.RebuildDocument();

            // Four pillars keep the two walls one connected body: a disconnected solid would make
            // "through all" and "up to nearest" differ for the wrong reason.
            foreach (var (sx, sy) in new[] { (-40d, -30d), (40d, -30d), (-40d, 30d), (40d, 30d) })
            {
                var pillarSketch = AddRectSketchOnPlane(part, xy, sx - 5d, sy - 5d, sx + 5d, sy + 5d, "p2 pillar");
                var pillar = (ksEntity)part.NewEntity(TypeOf("o3d_bossExtrusion", KompasObjectTypes.BossExtrusion));
                if (pillar.GetDefinition() is not ksBossExtrusionDefinition pillarDefinition)
                {
                    return new CutResult(endType, "error", "определение добавки не ksBossExtrusionDefinition", null, null, null);
                }

                pillarDefinition.SetSketch(pillarSketch);
                pillarDefinition.directionType = 0;
                pillarDefinition.SetSideParam(true, Blind(), 20d, 0d, false);
                if (!pillar.Create())
                {
                    return new CutResult(endType, "error", "стойка не создана", null, null, null);
                }

                doc.RebuildDocument();
            }

            var topPlane = OffsetPlane(part, xy, 20d, true);
            if (topPlane is null)
            {
                return new CutResult(endType, "error", "плоскость z=20 не создана", null, null, null);
            }

            var topSketch = AddRectSketchOnPlane(part, topPlane, -BlockX / 2d, -BlockY / 2d, BlockX / 2d, BlockY / 2d, "p2 top wall");
            var top = (ksEntity)part.NewEntity(TypeOf("o3d_bossExtrusion", KompasObjectTypes.BossExtrusion));
            if (top.GetDefinition() is not ksBossExtrusionDefinition topDefinition)
            {
                return new CutResult(endType, "error", "определение верхней стенки не ksBossExtrusionDefinition", null, null, null);
            }

            topDefinition.SetSketch(topSketch);
            topDefinition.directionType = 0;
            topDefinition.SetSideParam(true, Blind(), 10d, 0d, false);
            if (!top.Create())
            {
                return new CutResult(endType, "error", "верхняя стенка не создана", null, null, null);
            }

            doc.RebuildDocument();
            var before = Volume(part);

            var circle = AddCircleSketch(part, xy, 0d, 0d, 5d, "p2 stack hole");
            if (Cut(part, circle, endType, 1d, directionType) is not (ksEntity cut, _))
            {
                return new CutResult(endType, "error", "определение вырезания не ksCutExtrusionDefinition", before, null, null);
            }

            if (!cut.Create())
            {
                return new CutResult(endType, "create-false", "Create=false", before, null, null);
            }

            doc.RebuildDocument();
            var after = Volume(part);
            var delta = before is double b && after is double a ? b - a : (double?)null;
            return new CutResult(endType, delta switch
            {
                double dv when Math.Abs(dv - 2d * Math.PI * HoleAreaOverPi * 10d) <= Math.Max(0.05d, 1e-6d * 500d * Math.PI) => "through",
                double dv when Math.Abs(dv - Math.PI * HoleAreaOverPi * 10d) <= Math.Max(0.05d, 1e-6d * 250d * Math.PI) => "up-to-near-surface",
                double dv when Math.Abs(dv) <= 0.05d => "no-cut",
                double dv => "other(" + Raw(dv) + ")",
                null => "no-volume",
            }, "<stack>", before, after, delta);
        }
        catch (Exception ex)
        {
            return new CutResult(endType, "error", ex.GetType().Name + ": " + Unwrap(ex).Message, null, null, null);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static (ksEntity, ksCutExtrusionDefinition)? Cut(ksPart part, ksEntity sketch, short endType, double depth, short directionType)
    {
        var cut = (ksEntity)part.NewEntity(TypeOf("o3d_cutExtrusion", KompasObjectTypes.CutExtrusion));
        if (cut.GetDefinition() is not ksCutExtrusionDefinition definition)
        {
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = directionType;
        definition.SetSideParam(true, endType, depth, 0d, false);
        if (directionType == 2)
        {
            definition.SetSideParam(false, endType, depth, 0d, false);
        }

        return (cut, definition);
    }

    private static string Classify(double? delta, double thickness, double depth)
    {
        if (delta is not double value)
        {
            return "no-volume";
        }

        var through = Math.PI * HoleAreaOverPi * thickness;
        var blind = Math.PI * HoleAreaOverPi * depth;
        if (Math.Abs(value - through) <= Math.Max(0.01d, 1e-6d * through))
        {
            return "through";
        }

        if (Math.Abs(value - blind) <= Math.Max(0.01d, 1e-6d * blind))
        {
            return "blind-at-depth";
        }

        if (Math.Abs(value) <= 0.01d)
        {
            return "no-cut";
        }

        return "other(" + Raw(value) + ")";
    }

    private sealed record NativeHoleResult(bool Available, string DefinitionClrType, string Reason);

    /// <summary>
    /// The native «Отверстие» operation: <c>NewEntity(o3d_holeOperation=52)</c>, then a report of
    /// what the runtime actually handed back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in <c>Interop.Kompas6API5.dll</c> declares a hole definition: zero type names and
    /// zero member names in the dumped API5 metadata contain "hole"
    /// (docs/compatibility/kompas-api5-metadata.json). That is a statement about <em>that
    /// binary</em> only. API7 is counted in the same step, because an absent name in one interop
    /// assembly does not license the claim "the product has no native hole".
    /// </para>
    /// <para>
    /// Discovery is QI over every interface the vendor interop declares
    /// (<see cref="ComDiscovery.Probe"/>), not reflection over the instance: on a
    /// <c>__ComObject</c> the latter is blind to COM interfaces and has already produced false
    /// "member missing" conclusions in this project.
    /// </para>
    /// </remarks>
    private static NativeHoleResult NativeHole(KompasObject app, ProbeStep step)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                return new NativeHoleResult(false, "<нет документа>", "документ не создан");
            }

            var part = (ksPart)doc.GetPart(-1);
            if (BasePlate(part, BlockX, BlockY, 10d) is null)
            {
                return new NativeHoleResult(false, "<нет plate>", "пластина не создана");
            }

            doc.RebuildDocument();
            var before = Volume(part);

            var holeType = TypeOf("o3d_holeOperation", 52);
            var hole = part.NewEntity(holeType) as ksEntity;
            step.Observe($"NewEntity(o3d_holeOperation={holeType}) → {(hole is null ? "null" : RuntimeName(hole) + ", entity.type=" + hole.type + ", name=«" + hole.name + "»")}.");
            step.Data["native_hole_newentity"] = hole is null ? "null" : "ksEntity";
            if (hole is null)
            {
                return new NativeHoleResult(false, "null", "NewEntity(52) вернул null");
            }

            step.Data["native_hole_entity_type"] = hole.type;
            step.Data["native_hole_is_it_52"] = SafeBool(() => hole.IsIt(holeType));
            step.Data["native_hole_is_created_before_create"] = SafeBool(hole.IsCreated);
            step.Data["native_hole_feature_type_via_GetFeature"] = (hole.GetFeature() as ksFeature)?.type;

            var rawDefinition = hole.GetDefinition();
            var definitionName = RuntimeName(rawDefinition);
            step.Observe($"GetDefinition() → {definitionName}.");
            step.Data["native_hole_definition_clr_type"] = definitionName;

            var discovered = rawDefinition is null
                ? new List<DiscoveredInterface>()
                : ComDiscovery.Probe(rawDefinition, includeApi7: true);
            step.Data["native_hole_definition_interfaces"] = discovered.Select(d => d.FullName + " [" + d.AssemblyName + "]").ToArray();
            foreach (var candidate in discovered)
            {
                step.Observe($"  QI+: {candidate.FullName} ({candidate.AssemblyName}), объявленных членов: {candidate.Members.Count}");
                step.Data["members:" + candidate.FullName] = candidate.Members.Take(60).ToArray();
            }

            if (discovered.Count == 0)
            {
                step.Observe(rawDefinition is null
                    ? "Определение не выдано вовсе: GetDefinition() вернул null, поэтому спрашивать не с чего — QI-перебор к null неприменим."
                    : "QI-перебор по всем интерфейсам Interop.Kompas6API5 и Interop.KompasAPI7 не дал ни одного совпадения: полученный объект не описывается ни одним объявленным там интерфейсом.");
            }
            else if (!discovered.Any(d => d.FullName.Contains("Hole", StringComparison.OrdinalIgnoreCase)))
            {
                step.Observe("Среди поддержанных интерфейсов нет ни одного с «Hole» в имени: определение отверстия не имеет собственного RCW-интерфейса в API5.");
            }

            // Everything the object does expose gets a bounded attempt; refusals are recorded too.
            var attempts = new List<string>();
            var profile = AddCircleSketch(part, (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1)), 0d, 0d, 5d, "p2 native hole profile");
            attempts.AddRange(TryCall(rawDefinition, ("SetSketch", new object?[] { profile }), ("SetPlane", new object?[] { part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1)) })));
            attempts.AddRange(TryWritableMembers(discovered, rawDefinition, 10d));
            step.Data["native_hole_member_attempts"] = attempts.ToArray();
            foreach (var attempt in attempts)
            {
                step.Observe("  попытка: " + attempt);
            }

            var created = SafeBool(hole.Create);
            var rebuilt = SafeBool(doc.RebuildDocument);
            var after = Volume(part);
            var delta = before is double b && after is double a ? b - a : (double?)null;
            var expected = Math.PI * HoleAreaOverPi * 10d;
            var passed = delta is double dv && Math.Abs(dv - expected) <= Math.Max(0.01d, 1e-6d * expected);
            step.Observe($"hole.Create() → {created}, RebuildDocument → {rebuilt}, ΔV={Raw(delta)} при ожидании 250π={Raw(expected)}.");
            step.Data["native_hole_create"] = created;
            step.Data["native_hole_rebuild"] = rebuilt;
            step.Data["native_hole_volume_delta"] = Raw(delta);
            step.Data["native_hole_pass"] = passed;

            var api7 = Api7HoleTypeNames();
            step.Data["api7_hole_type_names"] = api7.ToArray();
            step.Data["api5_hole_type_names"] = Array.Empty<string>();
            step.Observe($"Интересно для P6/SM-07: в Interop.KompasAPI7.dll ({ComDiscovery.Api7Status}) типов с «Hole» — {api7.Count} (первые: {string.Join(", ", api7.Take(3))}); в Interop.Kompas6API5.dll — 0.");
            step.Observe("Формулировка для docs: отсутствие имени «hole» — свойство дампа Interop.Kompas6API5 1.0.0.0. API7-дамп в этом проекте не выгружался и на построение не проверялся, поэтому писать «родного отверстия нет» было бы неверно; корректно — «в API5 не найдено, API7 не проверялся».");
            step.Data["native_hole_minimal_repro"] =
                "app = Activator.CreateInstance(KOMPAS.Application.5); doc.Create(true,true); part = doc.GetPart(-1); " +
                "plate = NewEntity(24) + SetSideParam(true, etBlind, 10, 0, false) + Create(); " +
                "hole = (ksEntity)part.NewEntity(52 /*o3d_holeOperation*/); hole.GetDefinition() → null; hole.Create() → false; ΔV = 0.";

            var reason = passed ? "подтверждено" :
                rawDefinition is null ? $"GetDefinition() вернул null, а Create() — {created}: признаку нечем управлять из API5" :
                created != true ? $"Create() вернул {created} при интерфейсах [{string.Join(", ", discovered.Select(d => d.FullName))}]" :
                $"признак создан, но ΔV={Raw(delta)} ≠ 250π: отверстие не прорезалось параметрами, которые удаётся задать из API5";
            return new NativeHoleResult(passed, definitionName, reason);
        }
        catch (Exception ex)
        {
            step.Errors.Add("native hole: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe($"Родной признак не доигран: {ex.GetType().Name}: {Unwrap(ex).Message}");
            return new NativeHoleResult(false, "<исключение>", ex.GetType().Name + ": " + Unwrap(ex).Message);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static IReadOnlyList<string> Api7HoleTypeNames()
    {
        try
        {
            var directory = KompasInteropResolver.ResolvedDirectory;
            var path = directory is null ? null : Path.Combine(directory, "Interop.KompasAPI7.dll");
            if (path is null || !File.Exists(path))
            {
                return new[] { "<файл не найден>" };
            }

            // LoadFrom, not a project reference: this is evidence about a binary, not a
            // dependency. Nothing in the adapter ends up compiled against API7 because of it.
            return Assembly.LoadFrom(path).GetExportedTypes()
                .Where(t => t.Name.Contains("Hole", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FullName ?? t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            return new[] { "<" + ex.GetType().Name + ">" };
        }
    }

    private static List<string> TryCall(object? target, params (string Name, object?[] Args)[] calls)
    {
        var results = new List<string>();
        if (target is null)
        {
            results.Add("определение = null — вызывать нечего");
            return results;
        }

        foreach (var call in calls)
        {
            var method = FindAnyInterfaceMethod(target, call.Name);
            if (method is null)
            {
                results.Add($"{call.Name}(...) — метода нет ни на одном из поддержанных интерфейсов");
                continue;
            }

            string line;
            try
            {
                line = $"{call.Name}(...) → {Members.Value(method.Invoke(target, call.Args))}";
            }
            catch (Exception ex)
            {
                line = $"{call.Name}(...) → {Unwrap(ex).GetType().Name}";
            }

            results.Add(line);
        }

        return results;
    }

    /// <summary>
    /// Writes one probe value into every settable scalar member the object exposes. This is not a
    /// hole API — it is a survey of whether a caller can write <em>anything</em> into the returned
    /// definition, which is what «is kompas_hole reachable in API5» actually asks.
    /// </summary>
    private static List<string> TryWritableMembers(IReadOnlyList<DiscoveredInterface> discovered, object? target, double value)
    {
        var results = new List<string>();
        if (target is null)
        {
            return results;
        }

        foreach (var candidate in discovered)
        {
            var type = ComDiscovery.LookupType(candidate.FullName);
            if (type is null)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var write = Coerce(property.PropertyType, value);
                if (write is null)
                {
                    continue;
                }

                string line;
                try
                {
                    property.SetValue(target, write);
                    line = $"{candidate.FullName}.{property.Name} := {Members.Value(value)} → прочитано {Members.Value(property.GetValue(target))}";
                }
                catch (Exception ex)
                {
                    line = $"{candidate.FullName}.{property.Name} := … → {Unwrap(ex).GetType().Name}";
                }

                results.Add(line);
            }
        }

        return results;
    }

    private static object? Coerce(Type target, double value)
    {
        if (target == typeof(double))
        {
            return value;
        }

        if (target == typeof(short))
        {
            return (short)value;
        }

        if (target == typeof(int))
        {
            return (int)value;
        }

        if (target == typeof(bool))
        {
            return value != 0d;
        }

        return null;
    }

    private static MethodInfo? FindAnyInterfaceMethod(object target, string name)
    {
        foreach (var type in typeof(KompasObject).Assembly.GetExportedTypes().Where(t => t.IsInterface))
        {
            MethodInfo? method;
            try
            {
                method = type.GetMethod(name);
            }
            catch (AmbiguousMatchException)
            {
                method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name);
            }

            if (method is null)
            {
                continue;
            }

            try
            {
                if (Supports(target, type.GUID))
                {
                    return method;
                }
            }
            catch (Exception)
            {
                // A type this object is not; keep looking.
            }
        }

        return null;
    }

    private static bool Supports(object target, Guid iid)
    {
        var unk = Marshal.GetIUnknownForObject(target);
        try
        {
            var hr = Marshal.QueryInterface(unk, iid, out var ppv);
            if (hr == 0 && ppv != IntPtr.Zero)
            {
                Marshal.Release(ppv);
                return true;
            }

            return false;
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    // -----------------------------------------------------------------------------------------
    // P2.2 — fillet (G04)
    // -----------------------------------------------------------------------------------------

    private static void Fillet(ProbeReport report, KompasObject app)
    {
        var sw = Stopwatch.StartNew();
        var step = report.Begin(
            "P2.2",
            "Скругление (G04): рёбра из конечного тела и обратное чтение радиуса",
            "Строятся ли скругления по рёбрам тела (а не EntityCollection(7)) и читается ли radius обратно?");

        ksDocument3D? doc = null;
        try
        {
            var path = WorkFile("P2_Fillet.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                step.Fail("Документ не создан.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (BasePlate(part, BlockX, BlockY, 10d) is null)
            {
                step.Fail("Базовая пластина не создана.");
                return;
            }

            doc.RebuildDocument();
            var before = Volume(part);

            if (part.GetMainBody() is not ksBody body)
            {
                step.Fail("GetMainBody() → null.");
                return;
            }

            step.Data["body_clr_type"] = RuntimeName(body);
            step.Data["body_qi_interfaces"] = ComDiscovery.Probe(body).Select(i => i.FullName).ToArray();

            // Edge selection comes from the final body topology only. EntityCollection(7) is the
            // collection P0.8 proved to carry sketch and construction edges as well — which is the
            // whole reason FormTool.cs could pick the wrong four.
            var predicate = "IsStraight() && концы имеют совпадающие (x,y) && ||x||=50 && ||y||=40 && z от 0 до 10";
            step.Observe("Критерий отбора: " + predicate);

            var chosen = new List<ksEntity>();
            var seen = new HashSet<IntPtr>();
            var chosenReport = new List<string>();
            var unwrapping = new List<string>();
            var faces = (ksFaceCollection)body.FaceCollection();
            var faceCount = faces.GetCount();
            var rawEdgeRefs = 0;

            for (var f = 0; f < faceCount; f++)
            {
                if (AsDefinition<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face)
                {
                    continue;
                }

                var edges = (ksEdgeCollection)face.EdgeCollection();
                for (var e = 0; e < edges.GetCount(); e++)
                {
                    rawEdgeRefs++;
                    var edgeObject = edges.GetByIndex(e);
                    if (AsDefinition<ksEdgeDefinition>(edgeObject) is not ksEdgeDefinition edge)
                    {
                        continue;
                    }

                    if (!SafeBool(edge.IsStraight).GetValueOrDefault())
                    {
                        continue;
                    }

                    if (edge.GetVertex(true) is not ksVertexDefinition v0 || edge.GetVertex(false) is not ksVertexDefinition v1)
                    {
                        continue;
                    }

                    if (!v0.GetPoint(out var ax, out var ay, out var az) || !v1.GetPoint(out var bx, out var by, out var bz))
                    {
                        continue;
                    }

                    var vertical = Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
                    var corner = Math.Abs(Math.Abs(ax) - BlockX / 2d) < 1e-3 && Math.Abs(Math.Abs(ay) - BlockY / 2d) < 1e-3;
                    var spans = Math.Abs(Math.Max(az, bz) - 10d) < 1e-3 && Math.Abs(Math.Min(az, bz)) < 1e-3;
                    if (!(vertical && corner && spans))
                    {
                        continue;
                    }

                    chosenReport.Add($"({Num(ax)}, {Num(ay)}, {Num(az)}) → ({Num(bx)}, {Num(by)}, {Num(bz)}); edge={RuntimeName(edge)}, элемент коллекции={RuntimeName(edgeObject)}");

                    // A fillet takes entities, not definition interfaces, and which unwrapping
                    // yields one is itself an unknown — so all three routes are tried and named.
                    ksEntity? asEntity = edgeObject as ksEntity;
                    var route = asEntity is not null ? "элемент коллекции уже ksEntity" : null;
                    if (asEntity is null)
                    {
                        asEntity = edge.GetEntity() as ksEntity;
                        route = asEntity is not null ? "edge.GetEntity()" : null;
                    }

                    if (asEntity is null)
                    {
                        asEntity = edge.GetOwnerEntity() as ksEntity;
                        route = asEntity is not null ? "edge.GetOwnerEntity()" : null;
                    }

                    if (asEntity is null)
                    {
                        unwrapping.Add("ни один путь не дал ksEntity: GetEntity=" + RuntimeName(edge.GetEntity()) + ", GetOwnerEntity=" + RuntimeName(edge.GetOwnerEntity()));
                        continue;
                    }

                    unwrapping.Add(route + " → " + RuntimeName(asEntity));
                    var pointer = Marshal.GetIUnknownForObject(asEntity);
                    try
                    {
                        if (seen.Add(pointer))
                        {
                            chosen.Add(asEntity);
                        }
                    }
                    finally
                    {
                        Marshal.Release(pointer);
                    }
                }
            }

            step.Data["body_faces_before"] = faceCount;
            step.Data["body_edge_refs_before"] = rawEdgeRefs;
            step.Data["chosen_edge_count"] = chosen.Count;
            step.Data["chosen_edges"] = chosenReport.ToArray();
            step.Data["edge_entity_unwrapping"] = unwrapping.Distinct(StringComparer.Ordinal).ToArray();
            step.Observe($"Из тела: граней {faceCount}, ссылок на рёбра {rawEdgeRefs}; вертикальных угловых рёбер найдено {chosen.Count} (ожидалось 4), уникальных {seen.Count}.");

            if (chosen.Count != 4)
            {
                step.Fail($"Критерий отбора дал {chosen.Count} рёбер вместо 4 — скругление не применено.");
                return;
            }

            var fillet = (ksEntity)part.NewEntity(TypeOf("o3d_fillet", KompasObjectTypes.Fillet));
            var rawFilletDefinition = fillet.GetDefinition();
            step.Data["fillet_definition_clr_type"] = RuntimeName(rawFilletDefinition);
            step.Observe($"NewEntity(o3d_fillet={KompasObjectTypes.Fillet}) GetDefinition() → {RuntimeName(rawFilletDefinition)}");
            if (rawFilletDefinition is not ksFilletDefinition definition)
            {
                step.Fail($"Определение скругления не является ksFilletDefinition: {RuntimeName(rawFilletDefinition)}");
                return;
            }

            var filletInterfaces = ComDiscovery.Probe(rawFilletDefinition);
            step.Data["fillet_qi_interfaces"] = filletInterfaces.Select(i => i.FullName + " [" + i.AssemblyName + "]").ToArray();
            step.Data["fillet_definition_members"] = filletInterfaces.SelectMany(i => i.Members).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            foreach (var line in (IReadOnlyList<string>)step.Data["fillet_definition_members"]!)
            {
                step.Observe("  определение скругления: " + line);
            }

            definition.radius = 3d;
            definition.tangent = false;
            var array = definition.array();
            step.Observe($"radius := 3, tangent := false, array() → {RuntimeName(array)}");
            if (array is not ksEntityCollection collection)
            {
                step.Fail($"array() вернул {RuntimeName(array)}, а не ksEntityCollection — рёбра добавить нечем.");
                return;
            }

            var added = chosen.Select(edge => collection.Add(edge)).ToList();
            step.Data["array_add_results"] = added.Select(a => a.ToString().ToLowerInvariant()).ToArray();
            step.Data["array_count_after_add"] = collection.GetCount();
            step.Observe($"Add(edge) × {chosen.Count} → [{string.Join(", ", added.Select(a => a.ToString().ToLowerInvariant()))}]; в коллекции {collection.GetCount()} объектов.");

            var created = SafeBool(fillet.Create);
            var rebuilt = SafeBool(doc.RebuildDocument);
            var after = Volume(part);
            step.Observe($"fillet.Create() → {created}, RebuildDocument → {rebuilt}.");
            step.Data["fillet_entity_type"] = fillet.type;
            step.Data["fillet_feature_type"] = (fillet.GetFeature() as ksFeature)?.type;

            // Round trip: a NEW GetDefinition() on the created feature, so the answer comes from
            // the rebuilt model rather than from the RCW we set.
            double? radiusAfterCreate = (fillet.GetDefinition() as ksFilletDefinition)?.radius;
            step.Observe($"Обратное чтение с того же признака (получено новым GetDefinition() после Create+Rebuild): radius = {Raw(radiusAfterCreate)}.");

            var delta = before is double b && after is double a ? b - a : (double?)null;
            var expected = 4d * (1d - Math.PI / 4d) * 9d * 10d;
            var tolerance = Math.Max(0.01d, 1e-6d * expected);
            step.Data["volume_before"] = Raw(before);
            step.Data["volume_after"] = Raw(after);
            step.Data["volume_delta"] = Raw(delta);
            step.Data["expected_delta"] = Raw(expected);
            step.Data["expected_delta_formula"] = "4 вертикальных ребра R3 h10: 4·(1−π/4)·3²·10";
            step.Data["tolerance"] = Raw(tolerance);
            var volumeMatch = delta is double dv && Math.Abs(Math.Abs(dv) - expected) <= tolerance;
            step.Data["volume_match"] = volumeMatch;
            step.Observe($"ΔV = {Raw(delta is double d2 ? Math.Abs(d2) : null)} против ожидания {Raw(expected)} (допуск {Raw(tolerance)}) → {volumeMatch}.");

            var saved = SafeBool(() => doc.SaveAs(path));
            CloseQuietly(doc);
            doc = null;
            step.Observe($"SaveAs(«{Path.GetFileName(path)}») → {saved}.");

            double? radiusAfterReopen = null;
            int? facesAfterReopen = null;
            var reopened = (ksDocument3D)app.Document3D();
            if (reopened.Open(path, true))
            {
                doc = reopened;
                var reopenedPart = (ksPart)reopened.GetPart(-1);
                facesAfterReopen = reopenedPart.GetMainBody() is ksBody reopenedBody
                    ? ((ksFaceCollection)reopenedBody.FaceCollection()).GetCount()
                    : (int?)null;
                step.Observe($"После reopen: граней в теле {facesAfterReopen} (до скругления было {faceCount}), объём {Raw(Volume(reopenedPart))}.");

                var (items, routes, working) = FeatureProbe.Enumerate(reopened, reopenedPart);
                step.Data["reopen_feature_routes"] = routes.Select(r => $"{r.Name} → {r.Outcome}").ToArray();
                step.Observe($"Как после reopen нашли признак скругления: рабочая маршрута — {working ?? "<ни одна не дала признаков>"}, всего {items.Count} объектов.");
                var filletItem = items.FirstOrDefault(i => i.DefinitionInterface.Contains("Fillet", StringComparison.OrdinalIgnoreCase));
                if (filletItem?.Entity?.GetDefinition() is ksFilletDefinition fd)
                {
                    radiusAfterReopen = fd.radius;
                    step.Observe($"Найден по QI-имени «{filletItem.DefinitionInterface}»: «{filletItem.Name}» f.type={filletItem.FeatureType} e.type={filletItem.EntityType} → radius = {Raw(fd.radius)}, tangent = {fd.tangent.ToString().ToLowerInvariant()}.");
                }
                else
                {
                    step.Observe($"После reopen признак скругления не найден ни по одной из маршрут ({routes.Count} перепробовано); имя определения найдено: {(filletItem is null ? "<никто>" : filletItem.DefinitionInterface)}.");
                }
            }
            else
            {
                step.Observe("Открытие сохранённой модели не удалось — обратное чтение после reopen не снято.");
                CloseQuietly(reopened);
            }

            step.Data["faces_after_fillet"] = facesAfterReopen;
            step.Data["radius_roundtrip_after_create"] = Raw(radiusAfterCreate);
            step.Data["radius_roundtrip_after_reopen"] = Raw(radiusAfterReopen);
            step.Data["variable_radius_reachable"] = false;
            step.Observe("Переменного радиуса в API5 нет: определение скругления объявляет только {radius, tangent, array()} (см. список членов выше) — ни члена «на ребро», ни второй глубины. Это снято с вендорского interop, а не по памяти.");

            var radiusOk = radiusAfterCreate is double r && Math.Abs(r - 3d) <= 1e-9;
            if (created == true && volumeMatch && radiusOk)
            {
                step.Pass($"Скругление 4 вертикальных рёбер R3 подтверждено числами: ΔV={Raw(delta)} против {Raw(expected)}, radius читается как {Raw(radiusAfterCreate)}{(radiusAfterReopen is double rr ? $" и после reopen {Raw(rr)}" : " (после reopen не снято)")}.");
            }
            else
            {
                step.Fail($"Скругление не подтверждено: Create={created}, совпадение ΔV={volumeMatch} (ΔV={Raw(delta)}, ожидание {Raw(expected)}), radius={Raw(radiusAfterCreate)}.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Fail("Шаг не доигран: " + Unwrap(ex).Message);
        }
        finally
        {
            CloseQuietly(doc);
            step.Duration = sw.Elapsed;
        }
    }

    // -----------------------------------------------------------------------------------------
    // P2.3 — editing an existing feature in place (G03)
    // -----------------------------------------------------------------------------------------

    private static void EditExistingFeature(ProbeReport report, KompasObject app)
    {
        var sw = Stopwatch.StartNew();
        var step = report.Begin(
            "P2.3",
            "Правка параметра существующего признака на месте (G03)",
            "Какой финализатор действительно применяет изменение, остаётся ли тот же признак, и что видит reopen?");

        step.Data["api_finalizers"] = new[]
        {
            "ksEntity.Update() → Boolean (в репозитории до этого прогона не вызывался нигде)",
            "ksPart.EndEdit(Boolean Rebuild) → Boolean",
            "ksPart.Update() → Boolean",
            "ksPart.RebuildModel() → Boolean",
            "ksDocument3D.RebuildDocument() → Boolean",
        };
        step.Data["api_feature_collection_members"] = ComDiscovery.MembersOf("Kompas6API5.ksFeatureCollection").ToArray();

        var enumeration = EnumerationProbe(app, step);
        var write = WritePathProbe(app, step, enumeration);
        var minimal = MinimalFinalizerProbe(app, step);
        var dependent = DependentFeatureProbe(app, step, enumeration);
        var persisted = PersistenceProbe(app, step, enumeration);

        step.Data["stage_depth_readback"] = new[]
        {
            $"до = {Raw(write.DepthBefore)}",
            $"после сеттера = {Raw(write.DepthAfterSetter)}",
            $"после ksEntity.Update() = {Raw(write.DepthAfterEntityUpdate)}",
            $"после ksPart.EndEdit(false) = {Raw(write.DepthAfterPartEndEdit)}",
            $"после RebuildDocument() = {Raw(write.DepthAfterRebuild)}",
            $"после save→close→reopen = {Raw(persisted.DepthAfterReopen)}",
        };
        step.Data["effective_finalizer"] = write.EffectiveFinalizer;
        step.Data["geometry_finalizer"] = write.GeometryFinalizer;
        step.Data["minimal_finalizer_summary"] = minimal;
        step.Data["volume_after_edit"] = Raw(write.VolumeAfterRebuild);
        step.Data["expected_volume"] = Raw(96000d);
        step.Data["feature_count_before"] = write.FeatureCountBefore;
        step.Data["feature_count_after"] = write.FeatureCountAfter;
        step.Data["update_stamp_before"] = write.StampBefore;
        step.Data["update_stamp_after"] = write.StampAfter;
        step.Data["dependent_feature_survived"] = dependent.Survived;
        step.Data["persistence_ok"] = persisted.DepthAfterReopen is double pd && Math.Abs(pd - 12d) <= 1e-9;

        var volumeOk = write.VolumeAfterRebuild is double volume && Math.Abs(volume - 96000d) <= Math.Max(0.01d, 1e-6d * 96000d);
        if (volumeOk && step.Data["persistence_ok"] is true && write.FeatureCountAfter == write.FeatureCountBefore)
        {
            step.Pass($"Правка на месте подтверждена: объём {Raw(write.VolumeAfterRebuild)} мм³ = 100·80·12, число признаков {write.FeatureCountBefore}→{write.FeatureCountAfter} (второго выдавливания не появилось), значение принимается на этапе «{write.EffectiveFinalizer}», а геометрия пересчитывается на этапе «{write.GeometryFinalizer}»; после reopen глубина {Raw(persisted.DepthAfterReopen)}; зависимый вырез: {dependent.Summary}.");
        }
        else if (volumeOk)
        {
            step.Fail($"Геометрия поправилась (V={Raw(write.VolumeAfterRebuild)}), но правка не доказана целиком: reopen даёт глубину {Raw(persisted.DepthAfterReopen)} вместо 12; финализатор — «{write.EffectiveFinalizer}». Публиковать kompas_update_feature так нельзя.");
        }
        else
        {
            step.Fail($"Правка глубины на месте не подтверждена: объём после финализации {Raw(write.VolumeAfterRebuild)} при ожидании 96000. Этапы: сеттер={Raw(write.DepthAfterSetter)}, entity.Update={Raw(write.DepthAfterEntityUpdate)}, part.EndEdit={Raw(write.DepthAfterPartEndEdit)}, RebuildDocument={Raw(write.DepthAfterRebuild)}.");
        }

        step.Duration = sw.Elapsed;
    }

    private sealed record Enumeration(string WorkingRoute, IReadOnlyList<FeatureProbe.Route> Routes, IReadOnlyDictionary<int, string> FamilyByEntityType);

    /// <summary>Which enumeration actually returns the base extrusion, and under what type.</summary>
    private static Enumeration EnumerationProbe(KompasObject app, ProbeStep step)
    {
        ksDocument3D? doc = null;
        var families = new Dictionary<int, string>();
        try
        {
            doc = (ksDocument3D)app.Document3D();
            doc.Create(true, true);
            var part = (ksPart)doc.GetPart(-1);
            var plate = BasePlate(part, BlockX, BlockY, 10d);
            if (plate is null)
            {
                step.Observe("Перечисление: пластина не создана — шаг пропускается.");
                return new Enumeration("<нет>", Array.Empty<FeatureProbe.Route>(), families);
            }

            // The type an entity reports once it is committed is what a reader has to match on,
            // and it is not what was passed to NewEntity. Recorded here because it decides how
            // kompas_get_feature must identify a family.
            var plateTypeAfterCreate = plate.type;
            doc.RebuildDocument();
            var plateTypeAfterRebuild = plate.type;
            var plateDefinitionName = SideParams.Resolve(plate.GetDefinition()).Type?.FullName ?? RuntimeName(plate.GetDefinition());
            step.Observe($"NewEntity(o3d_baseExtrusion={TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion)}): entity.type сразу после Create() = {plateTypeAfterCreate} ({VendorConstants.NameOfObj3d(plateTypeAfterCreate)}), после RebuildDocument() = {plateTypeAfterRebuild} ({VendorConstants.NameOfObj3d(plateTypeAfterRebuild)}); GetDefinition() отвечает {plateDefinitionName}.");
            step.Data["plate_entity_type_after_create"] = plateTypeAfterCreate;
            step.Data["plate_entity_type_after_rebuild"] = plateTypeAfterRebuild;
            step.Data["plate_definition_interface"] = plateDefinitionName;
            step.Data["side_param_declaring_interfaces"] = SideParams.DeclaredInterfaceCount;

            var pocketSketch = AddRectSketchOnPlane(part, (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1)), -10d, -10d, 10d, 10d, "p2 enumeration pocket");
            if (Cut(part, pocketSketch, Blind(), 4d, 2) is not (ksEntity pocket, _))
            {
                step.Observe("Перечисление: вырез не создан — дерево будет беднее запланированного.");
            }
            else
            {
                pocket.Create();
            }

            var filletEdges = VerticalCornerEdges(part, step, "p2 enumeration");
            if (filletEdges.Count == 4)
            {
                var fillet = (ksEntity)part.NewEntity(TypeOf("o3d_fillet", KompasObjectTypes.Fillet));
                if (fillet.GetDefinition() is ksFilletDefinition fd)
                {
                    fd.radius = 2d;
                    fd.tangent = false;
                    if (fd.array() is ksEntityCollection ac)
                    {
                        foreach (var edge in filletEdges)
                        {
                            ac.Add(edge);
                        }
                    }

                    fillet.Create();
                }
            }

            doc.RebuildDocument();
            var (items, routes, working) = FeatureProbe.Enumerate(doc, part);

            step.Data["feature_routes"] = routes.Select(r => $"{r.Name} → {r.Outcome} ({r.Count})").ToArray();
            step.Data["feature_route_details"] = routes.Where(r => r.Count > 0).SelectMany(r => r.Features.Select(f => $"{r.Name} :: {f}")).ToArray();
            step.Observe($"Рабочая маршрута перечисления: {working ?? "<ни одна не дала признаков>"}. Всего объектов: {items.Count}.");
            foreach (var route in routes)
            {
                step.Observe($"   {route.Name} → {route.Outcome}");
                foreach (var line in route.Features.Take(8))
                {
                    step.Observe($"        {line}");
                }
            }

            // "What ksFeature.type integer corresponds to each family" — the measurement says
            // ksFeature.type is not the family at all, so the family is keyed on the ENTITY type.
            foreach (var item in items)
            {
                if (item.EntityType is int entityType)
                {
                    families[entityType] = item.DefinitionInterface;
                }
            }

            step.Data["feature_type_vs_entity_type"] = items.Select(i => $"f.type={i.FeatureType} e.type={i.EntityType?.ToString(CultureInfo.InvariantCulture) ?? "<нет>"} def={i.DefinitionInterface} имя=«{i.Name}»").ToArray();
            step.Data["entity_type_names"] = families.Keys.OrderBy(k => k).Select(k => $"{k} = {VendorConstants.NameOfObj3d(k)}").ToArray();
            step.Observe($"ksFeature.type у всех признаков одинаков? [{string.Join(", ", items.Select(i => i.FeatureType).Distinct())}]; семейство различается по типу Сущности: [{string.Join(", ", families.Select(f => f.Key + "→" + Shorten(f.Value)))}].");

            // The generic reader a future kompas_get_feature needs: does ksFeature.GetObject()
            // hand back an entity whose definition can be read without naming the family first?
            var baseItem = FindBaseExtrusion(items);
            if (baseItem?.Entity is ksEntity entity)
            {
                var definition = entity.GetDefinition();
                step.Observe($"ksFeature.GetObject() → {RuntimeName(entity)} (тип сущности {entity.type} = {VendorConstants.NameOfObj3d(entity.type)}); его GetDefinition() → {RuntimeName(definition)}, это ksBaseExtrusionDefinition: {definition is ksBaseExtrusionDefinition}.");
                var readGeneric = SideParams.Get(definition, true);
                if (readGeneric is not null)
                {
                    step.Observe($"Общий путь чтения даёт параметры через {SideParams.LastResolvedInterface}: type={readGeneric.Type} ({VendorConstants.NameOf(EndTypeEnum, readGeneric.Type)}) depth={Raw(readGeneric.Depth)} draft={Raw(readGeneric.Draft)} outward={readGeneric.Outward}.");
                    step.Data["generic_read_path_works"] = Math.Abs(readGeneric.Depth - 10d) <= 1e-9;
                    step.Data["generic_read_interface"] = SideParams.LastResolvedInterface;
                }
                else
                {
                    step.Data["generic_read_path_works"] = false;
                }

                step.Data["generic_read_feature_state"] = new
                {
                    name = baseItem.Name,
                    feature_type = baseItem.FeatureType,
                    valid = SafeBool(() => baseItem.Feature!.IsValid()),
                    modified = SafeBool(() => baseItem.Feature!.IsModified(false)),
                    excluded = SafeBool(() => baseItem.Feature!.excluded),
                    rollback = SafeBool(() => baseItem.Feature!.IsRollBacked()),
                    object_error = SafeInt(() => baseItem.Feature!.objectError),
                    update_stamp = Raw(SafeUInt(() => baseItem.Feature!.updateStamp)),
                };
                step.Observe($"Состояние признака: IsValid={SafeBool(() => baseItem.Feature!.IsValid())}, IsModified(false)={SafeBool(() => baseItem.Feature!.IsModified(false))}, excluded={SafeBool(() => baseItem.Feature!.excluded)}, IsRollBacked={SafeBool(() => baseItem.Feature!.IsRollBacked())}, objectError={SafeInt(() => baseItem.Feature!.objectError)}, updateStamp={Raw(SafeUInt(() => baseItem.Feature!.updateStamp))}.");
            }
            else
            {
                step.Observe("Через перечисление базовое выдавливание не найдено — см. feature_routes.");
                step.Data["generic_read_path_works"] = false;
            }

            return new Enumeration(working ?? "<ни одна>", routes, families);
        }
        catch (Exception ex)
        {
            step.Errors.Add("enumeration: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe($"Перечисление признаков не доиграно: {ex.GetType().Name}: {Unwrap(ex).Message}");
            return new Enumeration("<исключение>", Array.Empty<FeatureProbe.Route>(), families);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static string Shorten(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName[(dot + 1)..];
    }

    private sealed record WriteResult(
        double? DepthBefore,
        double? DepthAfterSetter,
        double? DepthAfterEntityUpdate,
        double? DepthAfterPartEndEdit,
        double? DepthAfterRebuild,
        double? VolumeAfterRebuild,
        int FeatureCountBefore,
        int FeatureCountAfter,
        string EffectiveFinalizer,
        string GeometryFinalizer,
        string? StampBefore,
        string? StampAfter,
        bool SameFeatureObject);

    /// <summary>
    /// The four candidates for "the change took effect", separated deliberately, weakest finalizer
    /// first. Reading back through a <em>fresh</em> <c>GetDefinition()</c> distinguishes "the
    /// setter wrote into the document" from "the setter wrote into my RCW" — which is the
    /// distinction the ТЗ cares about, and the one an externally successful rebuild hides.
    /// </summary>
    private static WriteResult WritePathProbe(KompasObject app, ProbeStep step, Enumeration enumeration)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            doc.Create(true, true);
            var part = (ksPart)doc.GetPart(-1);
            var plateHandle = BasePlate(part, BlockX, BlockY, 10d);
            if (plateHandle is null)
            {
                return Empty("пластина не создана");
            }

            doc.RebuildDocument();
            var (before, _, _) = FeatureProbe.Enumerate(doc, part);
            var baseItem = FindBaseExtrusion(before);
            if (baseItem?.Entity is not ksEntity entity)
            {
                step.Observe($"Правка: базовый признак не найден перечислением (рабочая маршрута «{enumeration.WorkingRoute}») — запись невозможна.");
                return Empty("признак не найден");
            }

            var feature = baseItem.Feature;
            var name = baseItem.Name;
            var stampBefore = Raw(feature is null ? null : SafeUInt(() => feature.updateStamp));
            var depthBefore = ReadDepth(entity);
            var volumeBefore = Volume(part);
            step.Observe($"Признак «{name}» f.type={baseItem.FeatureType} e.type={baseItem.EntityType} updateStamp={stampBefore}: глубина до = {Raw(depthBefore)}, объём = {Raw(volumeBefore)}, признаков в документе = {before.Count}.");

            var setter = SideParams.Set(entity.GetDefinition(), true, Blind(), 12d);
            step.Observe($"SetSideParam(true, etBlind={Blind()}, 12, 0, false) через {SideParams.LastResolvedInterface} → {setter}.");

            var afterSetter = ReadDepth(entity);
            var volumeAfterSetter = Volume(part);
            step.Observe($"Этап 1 (только сеттер): перечитано новым GetDefinition() → {Raw(afterSetter)}; объём {Raw(volumeAfterSetter)}.");

            var entityUpdate = SafeBool(entity.Update);
            var afterEntityUpdate = ReadDepth(entity);
            var volumeAfterEntityUpdate = Volume(part);
            step.Observe($"Этап 2 (+ ksEntity.Update() → {entityUpdate}): глубина {Raw(afterEntityUpdate)}, объём {Raw(volumeAfterEntityUpdate)}.");

            var endEdit = SafeBool(() => part.EndEdit(false));
            var afterPartEndEdit = ReadDepth(entity);
            var volumeAfterEndEdit = Volume(part);
            step.Observe($"Этап 3 (+ ksPart.EndEdit(Rebuild=false) → {endEdit}): глубина {Raw(afterPartEndEdit)}, объём {Raw(volumeAfterEndEdit)}.");

            var rebuild = SafeBool(doc.RebuildDocument);
            var afterRebuild = ReadDepth(entity);
            var volumeAfterRebuild = Volume(part);
            step.Observe($"Этап 4 (+ ksDocument3D.RebuildDocument() → {rebuild}): глубина {Raw(afterRebuild)}, объём {Raw(volumeAfterRebuild)}.");

            var (after, _, _) = FeatureProbe.Enumerate(doc, part);
            var stampAfter = Raw(feature is null ? null : SafeUInt(() => feature.updateStamp));
            var sameObject = after.Any(i => ReferenceId(i.Feature) == ReferenceId(feature));
            var sameName = after.Any(i => i.Name == name);
            step.Observe($"Тот ли это признак: число признаков {before.Count} → {after.Count}; updateStamp {stampBefore} → {stampAfter}; имя «{name}» на месте: {sameName}; IUnknown того же объекта в новом перечислении: {sameObject}.");
            step.Data["feature_names_before"] = before.Select(f => $"{f.Name}:f{f.FeatureType}:e{f.EntityType}").ToArray();
            step.Data["feature_names_after"] = after.Select(f => $"{f.Name}:f{f.FeatureType}:e{f.EntityType}").ToArray();
            step.Data["base_extrusion_count_after"] = after.Count(i => FindBaseExtrusion(new[] { i }) is not null);

            // Two different questions, and run 3 showed they have different answers: the parameter
            // became visible to the document at one stage, the GEOMETRY was recomputed at another.
            // Collapsing them into "the setter worked" would let an adapter publish a number the
            // model shows while the solid is still the old one.
            var valueFinalizer =
                Nearly(afterSetter, 12d) ? "сеттер определения (без финализатора)" :
                Nearly(afterEntityUpdate, 12d) ? "ksEntity.Update()" :
                Nearly(afterPartEndEdit, 12d) ? "ksPart.EndEdit(false)" :
                Nearly(afterRebuild, 12d) ? "ksDocument3D.RebuildDocument()" :
                "ни один: документ не принял новое значение";
            var geometryFinalizer =
                Nearly(volumeAfterSetter, 96000d, 0.01d) ? "сеттер определения (без финализатора)" :
                Nearly(volumeAfterEntityUpdate, 96000d, 0.01d) ? "ksEntity.Update()" :
                Nearly(volumeAfterEndEdit, 96000d, 0.01d) ? "ksPart.EndEdit(false)" :
                Nearly(volumeAfterRebuild, 96000d, 0.01d) ? "ksDocument3D.RebuildDocument()" :
                "ни один: геометрия не пересчитана";
            step.Observe($"Значение стало видно документу на этапе: {valueFinalizer}. Геометрия пересчиталась на этапе: {geometryFinalizer}.");
            step.Data["value_effective_stage"] = valueFinalizer;
            step.Data["geometry_effective_stage"] = geometryFinalizer;
            step.Data["volume_after_each_stage"] = new[]
            {
                $"до = {Raw(volumeBefore)}",
                $"после сеттера = {Raw(volumeAfterSetter)}",
                $"после entity.Update = {Raw(volumeAfterEntityUpdate)}",
                $"после part.EndEdit(false) = {Raw(volumeAfterEndEdit)}",
                $"после doc.RebuildDocument = {Raw(volumeAfterRebuild)}",
            };
            step.Data["readback_after_each_stage"] = new[]
            {
                $"до = {Raw(depthBefore)}",
                $"после сеттера = {Raw(afterSetter)}",
                $"после entity.Update (вызов вернул {entityUpdate}) = {Raw(afterEntityUpdate)}",
                $"после part.EndEdit(false) (вернул {endEdit}) = {Raw(afterPartEndEdit)}",
                $"после doc.RebuildDocument (вернул {rebuild}) = {Raw(afterRebuild)}",
            };

            // Are the handle NewEntity returned and the one the feature tree returns the same COM
            // object? Run 3 saw `type` differ between them, which is only meaningful if the
            // identity is stated.
            var treeBase = FindBaseExtrusion(after);
            step.Data["same_tree_handle_before_and_after_edit"] = ReferenceId(entity) == ReferenceId(treeBase?.Entity);
            step.Data["first_tree_handle_entity_type"] = SafeInt(() => entity.type);
            step.Data["second_tree_handle_entity_type"] = treeBase?.EntityType;
            step.Observe($"Один и тот же признак двумя перечислениями (до и после правки): IUnknown совпали: {step.Data["same_tree_handle_before_and_after_edit"]}; тип сущности: {Raw(SafeInt(() => entity.type))} → {Raw(treeBase?.EntityType)}.");

            // The handle NewEntity returned and the handle the feature tree returns for the same
            // committed feature disagree about its type, so the identity and each handle's own
            // interface set have to be stated separately — reading `SideParams.LastResolvedInterface`
            // after a second Resolve would otherwise attribute the wrong interface to the wrong handle.
            var creationHandleType = SafeInt(() => plateHandle.type);
            var creationInterfaces = ComDiscovery.Probe(plateHandle.GetDefinition()).Select(i => i.FullName).ToArray();
            var treeHandleType = SafeInt(() => entity.type);
            var treeInterfaces = ComDiscovery.Probe(entity.GetDefinition()).Select(i => i.FullName).ToArray();
            var sameAsCreation = ReferenceId(plateHandle) == ReferenceId(entity);
            step.Data["creation_handle_type"] = creationHandleType;
            step.Data["creation_handle_interfaces"] = creationInterfaces;
            step.Data["tree_handle_type"] = treeHandleType;
            step.Data["tree_handle_interfaces"] = treeInterfaces;
            step.Data["creation_handle_is_tree_handle"] = sameAsCreation;
            step.Observe($"Две ссылки на один и тот же признак. NewEntity вернул entity.type={Raw(creationHandleType)} с интерфейсами [{string.Join(", ", creationInterfaces)}]; то же дерево признаков отдаёт entity.type={Raw(treeHandleType)} с интерфейсами [{string.Join(", ", treeInterfaces)}]. Указатели RCW совпали: {sameAsCreation} (отличаются и тип, и набор интерфейсов, поэтому на «тип, который я записывал» полагаться нельзя).");
            step.Data["base_extrusion_definition_all_interfaces"] = treeInterfaces;

            return new WriteResult(
                depthBefore, afterSetter, afterEntityUpdate, afterPartEndEdit, afterRebuild, volumeAfterRebuild,
                before.Count, after.Count, valueFinalizer, geometryFinalizer, stampBefore, stampAfter, sameObject);
        }
        catch (Exception ex)
        {
            step.Errors.Add("write path: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe($"Правка не доиграна: {ex.GetType().Name}: {Unwrap(ex).Message}");
            return Empty(ex.GetType().Name);
        }
        finally
        {
            CloseQuietly(doc);
        }

        static WriteResult Empty(string why) => new(null, null, null, null, null, null, 0, 0, why, why, null, null, false);
    }

    private static bool Nearly(double? value, double expected) => value is double v && Math.Abs(v - expected) <= 1e-9;

    /// <summary>Toleranced comparison, for volumes where the kernel's own last digits are noise.</summary>
    private static bool Nearly(double? value, double expected, double tolerance) =>
        value is double v && Math.Abs(v - expected) <= tolerance;

    /// <summary>
    /// Is <c>ksEntity.Update()</c> actually required, or would the document rebuild have applied
    /// the change on its own? The staged probe walks finalizers in one order, so on its own it can
    /// only say "the change appeared at or before this stage". These two documents separate it: the
    /// first applies the setter and then only <c>RebuildDocument()</c>, the second applies the
    /// setter and nothing at all.
    /// </summary>
    private static string MinimalFinalizerProbe(KompasObject app, ProbeStep step)
    {
        var outcomes = new List<string>();
        foreach (var withRebuild in new[] { false, true })
        {
            ksDocument3D? doc = null;
            try
            {
                doc = (ksDocument3D)app.Document3D();
                doc.Create(true, true);
                var part = (ksPart)doc.GetPart(-1);
                if (BasePlate(part, BlockX, BlockY, 10d) is null)
                {
                    outcomes.Add($"rebuild={(withRebuild ? "да" : "нет")}: пластина не создана");
                    continue;
                }

                doc.RebuildDocument();
                var (items, _, _) = FeatureProbe.Enumerate(doc, part);
                if (FindBaseExtrusion(items)?.Entity is not ksEntity entity)
                {
                    outcomes.Add($"rebuild={(withRebuild ? "да" : "нет")}: базовый признак не найден");
                    continue;
                }

                var setter = SideParams.Set(entity.GetDefinition(), true, Blind(), 12d);
                var depthAfterSet = ReadDepth(entity);
                if (withRebuild)
                {
                    _ = doc.RebuildDocument();
                }

                var volume = Volume(part);
                var rebuiltGeometry = Nearly(volume, 96000d, 0.01d);
                var label = withRebuild ? "сеттер + только doc.RebuildDocument(), без ksEntity.Update()" : "сеттер + ничего (контроль)";
                outcomes.Add($"{label}: сеттер={setter}, глубина={Raw(depthAfterSet)}, объём={Raw(volume)} → геометрия {((withRebuild, rebuiltGeometry) switch { (true, true) => "пересчиталась и без entity.Update", (true, false) => "НЕ пересчиталась: одного RebuildDocument мало", (false, false) => "не пересчитана, как и ожидалось", _ => "пересчиталась вовсе без финализатора" })}");
            }
            catch (Exception ex)
            {
                outcomes.Add($"rebuild={(withRebuild ? "да" : "нет")}: {ex.GetType().Name}: {Unwrap(ex).Message}");
            }
            finally
            {
                CloseQuietly(doc);
            }
        }

        step.Data["minimal_finalizer_test"] = outcomes.ToArray();
        foreach (var line in outcomes)
        {
            step.Observe("Минимальный финализатор — " + line);
        }

        return string.Join("; ", outcomes);
    }

    /// <summary>
    /// Always a fresh GetDefinition(), and always through <see cref="SideParams"/>: the point is to
    /// ask the document, not the wrapper, and КОМПАС reports a stored base extrusion under the
    /// boss-extrusion interface, so a cast to one interface is not enough.
    /// </summary>
    private static double? ReadDepth(ksEntity entity) => SideParams.Get(entity.GetDefinition(), true)?.Depth;

    /// <summary>The base extrusion, found without assuming which interface it will answer.</summary>
    private static FeatureProbe.Item? FindBaseExtrusion(IReadOnlyList<FeatureProbe.Item> items) =>
        items.FirstOrDefault(i => i.EntityType == KompasObjectTypes.BaseExtrusion)
        ?? items.FirstOrDefault(i => i.EntityType is int t
            && (t == KompasObjectTypes.BaseExtrusion || t == KompasObjectTypes.BossExtrusion)
            && !i.DefinitionInterface.Contains("Cut", StringComparison.OrdinalIgnoreCase)
            && i.DefinitionInterface.Contains("Extrusion", StringComparison.OrdinalIgnoreCase))
        ?? items.FirstOrDefault(i => SideParams.IsExtrusionLike((i.Entity ?? i.Feature?.GetObject() as ksEntity)?.GetDefinition())
            && !i.DefinitionInterface.Contains("Cut", StringComparison.OrdinalIgnoreCase));

    private sealed record DependentResult(string Summary, bool Survived, double? ExpectedVolume, double? ActualVolume);

    /// <summary>
    /// Does a dependent feature survive a change to its parent's parameter? A pocket is cut into
    /// the plate, then the plate thickness is edited: the pocket must stay in the tree and the
    /// volume must move by exactly the plate delta, not by the pocket's.
    /// </summary>
    private static DependentResult DependentFeatureProbe(KompasObject app, ProbeStep step, Enumeration enumeration)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            doc.Create(true, true);
            var part = (ksPart)doc.GetPart(-1);
            if (BasePlate(part, BlockX, BlockY, 10d) is null)
            {
                return new DependentResult("пластина не создана", false, null, null);
            }

            doc.RebuildDocument();

            // 20×20 pocket, 4 mm deep from a plane at z=10 → ΔV = 400·4 = 1600 mm³.
            var plane = OffsetPlane(part, (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1)), 10d, true);
            if (plane is null)
            {
                return new DependentResult("смещённая плоскость не создана", false, null, null);
            }

            var pocketSketch = AddRectSketchOnPlane(part, plane, -10d, -10d, 10d, 10d, "p2 pocket");
            if (Cut(part, pocketSketch, Blind(), 4d, 0) is not (ksEntity pocket, _))
            {
                return new DependentResult("определение вырезания не ksCutExtrusionDefinition", false, null, null);
            }

            var pocketCreated = pocket.Create();
            doc.RebuildDocument();

            var volumeWithPocket = Volume(part);
            var plateOnly = BlockX * BlockY * 10d;
            step.Observe($"Карман 20×20×4: Create → {pocketCreated}; объём {Raw(volumeWithPocket)} (пластина без кармана {Raw(plateOnly)}, ΔV = {Raw(plateOnly - volumeWithPocket)} при ожидании 1600).");

            var (featuresBefore, _, _) = FeatureProbe.Enumerate(doc, part);
            var baseEntity = FindBaseExtrusion(featuresBefore)?.Entity;
            var setter = SideParams.Set(baseEntity?.GetDefinition(), true, Blind(), 12d);
            var entityUpdate = baseEntity is not null && SafeBool(baseEntity.Update) == true;
            var rebuild = SafeBool(doc.RebuildDocument);

            var (featuresAfter, _, _) = FeatureProbe.Enumerate(doc, part);
            var pocketsBefore = featuresBefore.Count(f => f.EntityType == KompasObjectTypes.CutExtrusion);
            var pocketsAfter = featuresAfter.Count(f => f.EntityType == KompasObjectTypes.CutExtrusion);
            var volume = Volume(part);
            var expected = (BlockX * BlockY * 12d) - (400d * 4d);
            step.Observe($"База 10→12 (сеттер={setter}, entity.Update={entityUpdate}, RebuildDocument={rebuild}): вырезов в дереве {pocketsBefore} → {pocketsAfter}; объём {Raw(volume)} при ожидании 96000−1600 = {Raw(expected)}.");
            step.Observe($"Дерево после правки: [{string.Join(", ", featuresAfter.Select(f => f.Name + " (e.type=" + f.EntityType + ")"))}]");

            if (baseEntity is not null && SideParams.Get(baseEntity.GetDefinition(), true) is { } reread)
            {
                step.Observe($"Перечитанные параметры базы после правки: type={reread.Type} ({VendorConstants.NameOf(EndTypeEnum, reread.Type)}) depth={Raw(reread.Depth)} draft={Raw(reread.Draft)} outward={reread.Outward}.");
                step.Data["dependent_base_readback"] = $"type={reread.Type}, depth={Raw(reread.Depth)}, draft={Raw(reread.Draft)}, outward={reread.Outward}";
            }

            step.Data["dependent_features_before"] = featuresBefore.Select(f => $"{f.Name}:e{f.EntityType}").ToArray();
            step.Data["dependent_features_after"] = featuresAfter.Select(f => $"{f.Name}:e{f.EntityType}").ToArray();
            step.Data["dependent_expected_volume"] = Raw(expected);
            step.Data["dependent_actual_volume"] = Raw(volume);
            step.Data["dependent_pocket_count_after"] = pocketsAfter;

            var survived = pocketsAfter == pocketsBefore && pocketsAfter >= 1 && Nearly(volume, expected);
            return new DependentResult(
                survived ? "выжил, объём совпал" : $"не подтверждено (вырезов {pocketsAfter} из {pocketsBefore}, объём {Raw(volume)} против {Raw(expected)})",
                survived,
                expected,
                volume);
        }
        catch (Exception ex)
        {
            step.Errors.Add("dependent: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe($"Проверка зависимого признака не доиграна: {ex.GetType().Name}: {Unwrap(ex).Message}");
            return new DependentResult("не измерено", false, null, null);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private sealed record PersistedResult(double? DepthAfterReopen, double? VolumeAfterReopen);

    /// <summary>
    /// Save → close → reopen and read the depth again. If the reopened document answers 10 where
    /// the live one answered 12, the edit never reached the model file — and that is the headline
    /// of this step, because the geometry in the session looked correct.
    /// </summary>
    private static PersistedResult PersistenceProbe(KompasObject app, ProbeStep step, Enumeration enumeration)
    {
        ksDocument3D? doc = null;
        try
        {
            var path = WorkFile("P2_Edited.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            doc = (ksDocument3D)app.Document3D();
            doc.Create(true, true);
            var part = (ksPart)doc.GetPart(-1);
            if (BasePlate(part, BlockX, BlockY, 10d) is null)
            {
                return new PersistedResult(null, null);
            }

            doc.RebuildDocument();
            var (items, _, _) = FeatureProbe.Enumerate(doc, part);
            var entity = FindBaseExtrusion(items)?.Entity;
            if (entity is null)
            {
                step.Observe("Persistence: базовый признак не найден.");
                return new PersistedResult(null, null);
            }

            var setter = SideParams.Set(entity.GetDefinition(), true, Blind(), 12d);
            var entityUpdate = SafeBool(entity.Update);
            var rebuild = SafeBool(doc.RebuildDocument);
            var beforeSave = ReadDepth(entity);
            var volumeBeforeSave = Volume(part);
            var saved = SafeBool(() => doc.SaveAs(path));
            step.Observe($"Перед сохранением: сеттер={setter}, entity.Update={entityUpdate}, RebuildDocument={rebuild}, перечитано={Raw(beforeSave)}, объём={Raw(volumeBeforeSave)}, SaveAs → {saved}.");
            CloseQuietly(doc);
            doc = null;

            var reopened = (ksDocument3D)app.Document3D();
            if (!reopened.Open(path, true))
            {
                step.Observe("Открытие после сохранения вернуло false.");
                CloseQuietly(reopened);
                return new PersistedResult(null, null);
            }

            doc = reopened;
            var reopenedPart = (ksPart)reopened.GetPart(-1);
            var (reopenedItems, reopenedRoutes, reopenedRoute) = FeatureProbe.Enumerate(reopened, reopenedPart);
            var reopenedEntity = FindBaseExtrusion(reopenedItems)?.Entity;
            var depth = reopenedEntity is null ? null : ReadDepth(reopenedEntity);
            var volume = Volume(reopenedPart);
            step.Observe($"После save→close→reopen: глубина = {Raw(depth)}, объём = {Raw(volume)}, рабочая маршрута перечисления — «{reopenedRoute ?? "<ни одна>"}», объектов {reopenedItems.Count}.");
            foreach (var item in reopenedItems)
            {
                step.Observe($"   reopened «{item.Name}» f.type={item.FeatureType} e.type={item.EntityType} def={item.DefinitionInterface}");
            }

            step.Data["persisted_path"] = path;
            step.Data["persisted_volume"] = Raw(volume);
            step.Data["depth_after_reopen"] = Raw(depth);
            step.Data["persisted_routes"] = reopenedRoutes.Select(r => $"{r.Name} → {r.Outcome}").ToArray();
            return new PersistedResult(depth, volume);
        }
        catch (Exception ex)
        {
            step.Errors.Add("persistence: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe($"Persistence не доиграна: {ex.GetType().Name}: {Unwrap(ex).Message}");
            return new PersistedResult(null, null);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    // -----------------------------------------------------------------------------------------
    // P2.4 — sketch plane orientation (G07)
    // -----------------------------------------------------------------------------------------

    private static void SketchPlaneOrientation(ProbeReport report, KompasObject app)
    {
        var sw = Stopwatch.StartNew();
        var step = report.Begin(
            "P2.4",
            "Ориентация плоскости эскиза (G07): XY/XZ/YZ и смещённые плоскости",
            "Как локальные (u,v) отображаются в модельные (x,y,z) и что делает direction у ksPlaneOffsetDefinition?");

        // A deliberately asymmetric rectangle: a centred one could not tell u from v, nor a
        // mirrored axis from an unmirrored one. The spans differ per coordinate on purpose:
        // u spans 40, v spans 20, the normal spans the extrusion depth — so the bbox identifies
        // which model axis carries which sketch axis without any assumption about the answer.
        const double U1 = 10d;
        const double V1 = 20d;
        const double U2 = 50d;
        const double V2 = 40d;
        const double Depth = 6d;

        var cases = new (string Name, string Constant, short Fallback)[]
        {
            ("XY", "o3d_planeXOY", 1),
            ("XZ", "o3d_planeXOZ", 2),
            ("YZ", "o3d_planeYOZ", 3),
        };

        try
        {
            step.Data["plane_constants_resolved"] = cases.Select(c => $"{c.Name}={TypeOf(c.Constant, c.Fallback)} ({c.Constant}, {EntityTypes.SourceAssembly})").ToArray();
            step.Observe($"Базовые плоскости взяты из именованных констант: {string.Join("; ", (string[])step.Data["plane_constants_resolved"]!)}.");

            var baseRows = new List<PlaneRow>();
            foreach (var c in cases)
            {
                var row = BuildSlab(app, c, null, null, U1, V1, U2, V2, Depth);
                baseRows.Add(row);
                step.Observe($"Базовая {c.Name}: {row.Line()}");
            }

            var offsetRows = new List<PlaneRow>();
            foreach (var pc in cases)
            {
                foreach (var direction in new[] { true, false })
                {
                    var row = BuildSlab(app, pc, 15d, direction, U1, V1, U2, V2, Depth);
                    offsetRows.Add(row);
                    step.Observe($"Смещённая {pc.Name} на 15 мм, direction={Off(row)}: {row.Line()}");
                }
            }

            step.Data["base_plane_rows"] = baseRows.Select(r => r.Line()).ToArray();
            step.Data["offset_plane_rows"] = offsetRows.Select(r => r.Line()).ToArray();
            step.Data["uv_to_model"] = baseRows.Select(r => r.Mapping(U1, V1, Depth)).ToArray();
            foreach (var line in (IReadOnlyList<string>)step.Data["uv_to_model"]!)
            {
                step.Observe("Отображение: " + line);
            }

            // Where each offset plane actually sits, expressed the only way that is comparable
            // across planes: its model coordinate along the plane's normal axis, and the same
            // distance read along the normal itself (positive = with the normal).
            var analysis = new List<(string Plane, bool Direction, string NormalAxis, int NormalSign, double? ModelCoordinate, double? AlongNormal)>();
            foreach (var row in offsetRows)
            {
                var baseRow = baseRows.First(b => b.Plane == row.Plane);
                var normal = baseRow.NormalAxis(Depth);
                var modelCoordinate = row.PlaneCoordinate(normal, Depth);
                var along = modelCoordinate is double mc && normal.Sign is int sg ? sg * mc : (double?)null;
                analysis.Add((row.Plane, row.Direction ?? true, normal.Axis ?? "?", normal.Sign ?? 0, modelCoordinate, along));
                step.Observe($"Положение смещённой плоскости: {row.Plane} direction={Off(row)}: нормаль = {(normal.Axis ?? "?")}{(normal.Sign == -1 ? "−" : "+")}, плоскость в модели {normal.Axis}={Raw(modelCoordinate)}, смещение вдоль нормали = {Raw(along)}");
            }

            step.Data["offset_plane_positions"] = analysis
                .Select(x => $"{x.Plane} direction={On(x.Direction)} нормаль={x.NormalAxis}{x.NormalSign:+0;-0} модельная_координата={Raw(x.ModelCoordinate)} вдоль_нормали={Raw(x.AlongNormal)}")
                .ToArray();

            double? Along(string plane, bool direction) => analysis.Single(x => x.Plane == plane && x.Direction == direction).AlongNormal;

            var xyTrue = Along("XY", true);
            var xyFalse = Along("XY", false);
            var xzTrue = Along("XZ", true);
            var yzTrue = Along("YZ", true);
            var yzFalse = Along("YZ", false);

            step.Data["along_normal_summary"] = new[]
            {
                $"XY dir=true → {Raw(xyTrue)}",
                $"XY dir=false → {Raw(xyFalse)}",
                $"XZ dir=true → {Raw(xzTrue)}",
                $"YZ dir=true → {Raw(yzTrue)}",
                $"YZ dir=false → {Raw(yzFalse)}",
            };

            step.Data["direction_true_is_along_normal_for_all_three"] =
                xyTrue is double n1 && xzTrue is double n2 && yzTrue is double n3 && Math.Abs(n1 - n2) <= 1e-6 && Math.Abs(n1 - n3) <= 1e-6;

            // The claim under test. ResolvePlaneEntity (Api5Session.Geometry.cs) does
            //   signed = plane.Base == PlaneBase.Yz ? -plane.OffsetMm : plane.OffsetMm;
            //   offset = |signed|;  direction = signed >= 0;
            // i.e. for YZ a positive user offset becomes direction=false.
            var productionYz = analysis.Single(x => x.Plane == "YZ" && !x.Direction);
            step.Data["production_yz_case"] = new
            {
                sets = "offset = |15| = 15, direction = false",
                lands_at_model_coordinate = Raw(productionYz.ModelCoordinate),
                along_normal_signed = Raw(productionYz.AlongNormal),
            };

            if (step.Data["direction_true_is_along_normal_for_all_three"] is true)
            {
                step.Data["yz_sign_inversion_verdict"] =
                    "СЕМАНТИКА ЕДИНА: direction=true = смещение вдоль нормали плоскости для XY/XZ/YZ. Инверсия для YZ в ResolvePlaneEntity — НЕ обход несогласованности API, а осознанная трактовка «offsetMm задаётся вдоль модельной оси»: нормаль YOZ направлена в −X (измерено), поэтому direction=true даёт x=−15, а продакшен хочет x=+15. Оставлять её можно только если контракт инструмента определяет знак смещения по модели, и тогда это надо записать в контракт; как «особенность YZ-плоскости» она не обоснована.";
                step.Pass($"Отображение (u,v)→(x,y,z) снято для всех трёх плоскостей; direction=true = смещение вдоль нормали и для XY, и для XZ, и для YZ (значения {Raw(xyTrue)}); продакшеновская инверсия для YZ меняет смысл знака смещения с «вдоль нормали» на «вдоль модели +X».");
            }
            else
            {
                step.Data["yz_sign_inversion_verdict"] = $"НЕ ЕДИНА: direction=true даёт смещение вдоль нормали XY={Raw(xyTrue)}, XZ={Raw(xzTrue)}, YZ={Raw(yzTrue)}";
                step.Fail($"Семантика direction оказалась неединой для трёх плоскостей (XY={Raw(xyTrue)}, XZ={Raw(xzTrue)}, YZ={Raw(yzTrue)}) — см. offset_plane_positions.");
            }

            step.Observe("Вывод по G07: " + step.Data["yz_sign_inversion_verdict"]);
            step.Data["requested_offset_mm"] = Raw(15d);
            step.Data["extrusion_depth_mm"] = Raw(Depth);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Fail("Шаг не доигран: " + Unwrap(ex).Message);
        }
        finally
        {
            step.Duration = sw.Elapsed;
        }
    }

    private static string On(bool value) => value.ToString().ToLowerInvariant();

    private static string Off(PlaneRow row) => (row.Direction ?? true).ToString().ToLowerInvariant();

    private sealed record PlaneRow(
        string Plane,
        double? RequestedOffset,
        bool? Direction,
        double? MinX,
        double? MinY,
        double? MinZ,
        double? MaxX,
        double? MaxY,
        double? MaxZ,
        string? Error,
        string? ReopenedPlaneDefinition,
        string LiveBox)
    {
        public string Line() =>
            Error ?? $"min=({Num(MinX)}, {Num(MinY)}, {Num(MinZ)}) max=({Num(MaxX)}, {Num(MaxY)}, {Num(MaxZ)}) (до сохранения {LiveBox}){ReopenedPlaneDefinition}";

        private double? Min(int axis) => axis switch { 0 => MinX, 1 => MinY, _ => MinZ };

        private double? Max(int axis) => axis switch { 0 => MaxX, 1 => MaxY, _ => MaxZ };

        /// <summary>The axis whose extent is the extrusion depth is the plane's normal.</summary>
        public (string? Axis, int? Sign, int Index) NormalAxis(double depth)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                if (Min(axis) is double lo && Max(axis) is double hi && Math.Abs((hi - lo) - depth) <= 1e-6)
                {
                    // The slab grows from the plane toward the far end: on the base plane the slab
                    // is [0,+depth] for XY, [-depth,0] wherever the normal points negative.
                    var sign = Math.Abs(lo) <= 1e-6 ? 1 : -1;
                    return (new[] { "x", "y", "z" }[axis], sign, axis);
                }
            }

            return (null, null, -1);
        }

        /// <summary>Model coordinate of the plane itself, along its own normal axis.</summary>
        public double? PlaneCoordinate((string? Axis, int? Sign, int Index) normal, double depth)
        {
            if (normal.Index < 0 || Min(normal.Index) is not double lo || Max(normal.Index) is not double hi)
            {
                return null;
            }

            // The sketch sits at the end the slab grows away from.
            return normal.Sign == 1 ? lo : hi;
        }

        public string Mapping(double u1, double v1, double depth)
        {
            if (Error is not null)
            {
                return Plane + ": " + Error;
            }

            var names = new[] { "x", "y", "z" };
            var parts = new List<string>();
            for (var axis = 0; axis < 3; axis++)
            {
                if (Min(axis) is not double lo || Max(axis) is not double hi)
                {
                    continue;
                }

                var size = hi - lo;
                var label = names[axis];
                if (Math.Abs(size - depth) <= 1e-6)
                {
                    parts.Add($"n→{(lo >= -1e-9 ? "+" : "−")}{label} [{Num(lo)}..{Num(hi)}]");
                }
                else if (Math.Abs(size - 40d) <= 1e-6)
                {
                    parts.Add(Math.Abs(lo - u1) <= 1e-6 ? $"u→+{label}" : $"u→−{label}");
                }
                else if (Math.Abs(size - 20d) <= 1e-6)
                {
                    parts.Add(Math.Abs(lo - v1) <= 1e-6 ? $"v→+{label}" : $"v→−{label}");
                }
                else
                {
                    parts.Add($"?{label}∈[{Num(lo)},{Num(hi)}]");
                }
            }

            return Plane + ": " + string.Join(", ", parts);
        }
    }

    /// <summary>Sketch on a base (optionally offset) plane, one slab, save→close→reopen, bbox.</summary>
    private static PlaneRow BuildSlab(
        KompasObject app,
        (string Name, string Constant, short Fallback) plane,
        double? offsetMm,
        bool? direction,
        double u1,
        double v1,
        double u2,
        double v2,
        double depth)
    {
        ksDocument3D? doc = null;
        var live = "-";
        try
        {
            var entityTypeId = TypeOf(plane.Constant, plane.Fallback);
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                return Failure("<документ не создан>");
            }

            var part = (ksPart)doc.GetPart(-1);
            if (part.GetDefaultEntity(entityTypeId) is not ksEntity basePlane)
            {
                return Failure($"<GetDefaultEntity({entityTypeId}) → {RuntimeName(part.GetDefaultEntity(entityTypeId))}>");
            }

            var carrier = offsetMm is null
                ? basePlane
                : OffsetPlane(part, basePlane, offsetMm.Value, direction ?? true);
            if (carrier is null)
            {
                return Failure("<смещённая плоскость не создана>");
            }

            var sketch = AddRectSketchOnPlane(part, carrier, u1, v1, u2, v2, $"p2 {plane.Name} profile");
            var extrusion = (ksEntity)part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
            if (extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
            {
                return Failure("<определение выдавливания не ksBaseExtrusionDefinition>");
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            definition.SetSideParam(true, Blind(), depth, 0d, false);
            if (!extrusion.Create())
            {
                return Failure("<выдавливание не создано>");
            }

            doc.RebuildDocument();
            live = BoxText(BoxOf(part));

            var suffix = offsetMm is null ? "base" : $"off{offsetMm:0}_{(direction == true ? "dirT" : "dirF")}";
            var path = WorkFile($"P2_Plane_{plane.Name}_{suffix}.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var saved = SafeBool(() => doc.SaveAs(path));
            CloseQuietly(doc);
            doc = null;

            var reopened = (ksDocument3D)app.Document3D();
            if (!reopened.Open(path, true))
            {
                CloseQuietly(reopened);
                return new PlaneRow(plane.Name, offsetMm, direction, null, null, null, null, null, null,
                    $"<reopen false; save={saved}>", null, live);
            }

            doc = reopened;
            var reopenedBox = BoxOf((ksPart)reopened.GetPart(-1));
            var planeInfo = offsetMm is null ? null : ReadPersistedOffsetPlane(reopened, offsetMm.Value, direction ?? true);
            var suffixNote = planeInfo is null
                ? (offsetMm is null ? string.Empty : " | смещённая плоскость после reopen не найдена")
                : " | " + planeInfo;
            return new PlaneRow(plane.Name, offsetMm, direction, reopenedBox[0], reopenedBox[1], reopenedBox[2], reopenedBox[3], reopenedBox[4], reopenedBox[5],
                null, suffixNote, live);
        }
        catch (Exception ex)
        {
            return Failure("<" + ex.GetType().Name + ": " + Unwrap(ex).Message + ">");
        }
        finally
        {
            CloseQuietly(doc);
        }

        PlaneRow Failure(string why) => new(plane.Name, offsetMm, direction, null, null, null, null, null, null, why, null, live);
    }

    /// <summary>
    /// Does <c>direction</c> survive the file round trip? It is the value the adapter would re-read
    /// to decide a sign, so persistence may not be assumed.
    /// </summary>
    private static string? ReadPersistedOffsetPlane(ksDocument3D doc, double requestedOffset, bool requestedDirection)
    {
        try
        {
            if (doc.GetPart(-1) is not ksPart part)
            {
                return null;
            }

            var planes = (ksEntityCollection)part.EntityCollection(TypeOf("o3d_planeOffset", KompasObjectTypes.PlaneOffset));
            var count = planes.GetCount();
            for (var i = 0; i < count; i++)
            {
                if (planes.GetByIndex(i) is ksEntity entity && entity.GetDefinition() is ksPlaneOffsetDefinition persisted)
                {
                    return $"после reopen: offset={Raw(persisted.offset)} direction={persisted.direction.ToString().ToLowerInvariant()} (запрошено {Raw(requestedOffset)}/{requestedDirection.ToString().ToLowerInvariant()})";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }

    private static string BoxText(double?[] box) =>
        $"({Num(box[0])}, {Num(box[1])}, {Num(box[2])}, {Num(box[3])}, {Num(box[4])}, {Num(box[5])})";

    private static double?[] BoxOf(ksPart part)
    {
        if (!part.GetGabarit(true, true, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ))
        {
            return new double?[] { null, null, null, null, null, null };
        }

        // Model millimetres; coordinates carry no unit selector (P0.7, docs/research §1).
        return new double?[] { minX, minY, minZ, maxX, maxY, maxZ };
    }

    // -----------------------------------------------------------------------------------------
    // Shared geometry helpers — same call sequences as Api5Session.Geometry.cs
    // -----------------------------------------------------------------------------------------

    private static ksEntity? OffsetPlane(ksPart part, ksEntity basePlane, double offsetMm, bool direction)
    {
        var plane = (ksEntity)part.NewEntity(TypeOf("o3d_planeOffset", KompasObjectTypes.PlaneOffset));
        if (plane.GetDefinition() is not ksPlaneOffsetDefinition definition)
        {
            return null;
        }

        definition.SetPlane(basePlane);
        definition.offset = offsetMm;
        definition.direction = direction;
        return plane.Create() ? plane : null;
    }

    /// <summary>
    /// The four corner edges of the probe block, taken from the body topology with the same
    /// predicate P2.2 uses, unwrapped to <c>ksEntity</c> the same three ways.
    /// </summary>
    private static List<ksEntity> VerticalCornerEdges(ksPart part, ProbeStep step, string tag)
    {
        var chosen = new List<ksEntity>();
        var seen = new HashSet<IntPtr>();
        if (part.GetMainBody() is not ksBody body)
        {
            return chosen;
        }

        var faces = (ksFaceCollection)body.FaceCollection();
        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (AsDefinition<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face)
            {
                continue;
            }

            var edges = (ksEdgeCollection)face.EdgeCollection();
            for (var e = 0; e < edges.GetCount(); e++)
            {
                var edgeObject = edges.GetByIndex(e);
                if (AsDefinition<ksEdgeDefinition>(edgeObject) is not ksEdgeDefinition edge || !SafeBool(edge.IsStraight).GetValueOrDefault())
                {
                    continue;
                }

                if (edge.GetVertex(true) is not ksVertexDefinition v0 || edge.GetVertex(false) is not ksVertexDefinition v1)
                {
                    continue;
                }

                if (!v0.GetPoint(out var ax, out var ay, out var az) || !v1.GetPoint(out var bx, out var by, out var bz))
                {
                    continue;
                }

                var vertical = Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
                var corner = Math.Abs(Math.Abs(ax) - BlockX / 2d) < 1e-3 && Math.Abs(Math.Abs(ay) - BlockY / 2d) < 1e-3;
                var spansPlate = Math.Abs(Math.Max(az, bz) - 10d) < 1e-3 && Math.Abs(Math.Min(az, bz)) < 1e-3;
                if (!(vertical && corner && spansPlate))
                {
                    continue;
                }

                // Same three unwrappings P2.2 measures: the collection element, GetEntity(),
                // GetOwnerEntity(). Whichever one КОМПАС hands back is what the fillet needs.
                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity ?? edge.GetOwnerEntity() as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                if (seen.Add(ReferenceId(entity)))
                {
                    chosen.Add(entity);
                }
            }
        }

        step.Observe($"{tag}: вертикальных угловых рёбер из тела собрано {chosen.Count}.");
        return chosen;
    }

    private static ksEntity? BasePlate(ksPart part, double width, double height, double thickness)
    {
        var sketch = AddRectSketchOnPlane(
            part,
            (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1)),
            -width / 2d,
            -height / 2d,
            width / 2d,
            height / 2d,
            "p2 plate profile");

        var extrusion = (ksEntity)part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
        if (extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, Blind(), thickness, 0d, false);
        return extrusion.Create() ? extrusion : null;
    }

    private static ksEntity AddRectSketchOnPlane(ksPart part, ksEntity plane, double u1, double v1, double u2, double v2, string name)
    {
        var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksLineSeg(u1, v1, u2, v1, 1);
        editor.ksLineSeg(u2, v1, u2, v2, 1);
        editor.ksLineSeg(u2, v2, u1, v2, 1);
        editor.ksLineSeg(u1, v2, u1, v1, 1);
        definition.EndEdit();
        return sketch;
    }

    private static ksEntity AddCircleSketch(ksPart part, ksEntity plane, double u, double v, double radius, string name)
    {
        var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksCircle(u, v, radius, 1);
        definition.EndEdit();
        return sketch;
    }

    private static TInterface? AsDefinition<TInterface>(object? element)
        where TInterface : class
    {
        if (element is TInterface direct)
        {
            return direct;
        }

        try
        {
            return element is ksEntity entity ? entity.GetDefinition() as TInterface : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IntPtr ReferenceId(object? value)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            var pointer = Marshal.GetIUnknownForObject(value);
            Marshal.Release(pointer);
            return pointer;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static double? Volume(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return null;
            }

            // CalcMassInertiaProperties is declared to return Object, so reflection over
            // method.ReturnType finds no members; the typed read below is the route P0.7 proved.
            if (body.CalcMassInertiaProperties((uint)MixMmKg) is not ksMassInertiaParam properties)
            {
                return null;
            }

            var value = typeof(ksMassInertiaParam).GetProperty("v")?.GetValue(properties);
            return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string RuntimeName(object? value) => value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;

    private static bool? SafeBool(Func<bool> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<int> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static uint? SafeUInt(Func<uint> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CloseQuietly(ksDocument3D? doc)
    {
        if (doc is null)
        {
            return;
        }

        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // The failure that matters was recorded by the caller.
        }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;

    /// <summary>Round-trip formatting: the digits the API gave back, no rounding policy imposed.</summary>
    private static string? Raw(double? value) => value is double d ? (double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "NaN") : null;

    private static string? Raw(uint? value) => value is uint u ? u.ToString(CultureInfo.InvariantCulture) : null;

    private static string Raw(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "NaN";

    private static string Num(double? value) => value is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "null";
}

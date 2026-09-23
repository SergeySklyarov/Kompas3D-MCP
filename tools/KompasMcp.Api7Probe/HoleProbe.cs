using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// The measured program. API5 builds the reference plate, API7 creates and edits the native
/// «Отверстие», API5 reads the result back — the program of ADR-003 §3, in its order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Binding.</b> API5 goes through the vendor typed interop, which is what production uses and
/// what P0 proved loads and runs in x64 .NET 10. API7 also goes through a typed interop — the
/// prebuilt <c>Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll</c> — because that is the wrapper
/// a shipping adapter would get, and ADR-003 §1 wants the measurement taken on the same footing.
/// Step A7.0 shows that assembly is an older snapshot than the installed <c>Bin\kAPI7.tlb</c>, so
/// every API7 member the prebuilt wrapper does not declare is reached through that object's own
/// <c>IDispatch</c> (<see cref="Late"/>) instead: a failure on that path is a statement about the
/// product, while a failure only on the typed path is a statement about the wrapper. Which one it
/// was is recorded per member.
/// </para>
/// <para>
/// <b>Proof standard</b> (ADR-003 §3): a non-null object, a found type or <c>S_OK</c> is never
/// reported as success. Only measured volumes and read-back parameters decide, against
/// <c>max(0.01 mm³, 1e-6 × expectation)</c>, which is not widened after a failure. Where a call
/// could not be made at all, the verdict is UNKNOWN, not FAIL, and the reason is written down.
/// </para>
/// </remarks>
internal sealed class HoleProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;

    /// <summary>Volume of the plate with a through hole of the given diameter, in mm³.</summary>
    private static double ExpectedThroughHole(double diameterMillimetres)
    {
        var radius = diameterMillimetres / 2d;
        return (PlateWidth * PlateHeight * PlateThickness) - (Math.PI * radius * radius * PlateThickness);
    }

    /// <summary>Material a through hole of this diameter removes from the reference plate:
    /// π·r²·t, which for Ø10 through 10 мм is 250π.</summary>
    internal static double RemovedByHole(double diameterMillimetres)
    {
        var radius = diameterMillimetres / 2d;
        return Math.PI * radius * radius * PlateThickness;
    }

    /// <summary>Project tolerance rule: max(0.01 mm³, 1e-6 × expectation). Never widened.</summary>
    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly List<string> _transitions = new();
    private readonly Stopwatch _clock = new();

    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private ksPart _part = null!;
    private KompasAPI7.IApplication? _app7;
    private KompasAPI7.IModelContainer? _container;

    private KompasAPI7.IPart7? _api7Part;
    private KompasAPI7.IHoles3D? _holes;
    private KompasAPI7.IHole3D? _hole;

    private double _plateVolume;
    private bool _diameterIsMillimetres = true;
    private string _route = "<признак не создавался>";
    private double? _lastDelta;

    private string? _savedPath;

    public HoleProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public IReadOnlyList<string> Transitions => _transitions;

    public string? SavedPath => _savedPath;

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            Bridge();
            if (_app7 is null)
            {
                var skipped = _report.Begin("A7.4", "Родное «Отверстие» через API7", "Создаётся ли признак средствами API7?");
                skipped.Unknown("Мост A7.2 не построен — шаги API7 не выполнялись.");
                return;
            }

            Baseline();
            CreateHole();
            if (_options.CollectControls)
            {
                var tail = _report.Begin(
                    "A7.C",
                    "Режим --controls: таблица атрибуции маршрута",
                    "Какой из ингредиентов — эскиз-позиция, грань в IHoleDisposal.BaseSurface или и то и другое — объясняет совпадение чисел?");
                tail.Data["attribution"] = _report.Steps[^1].Data.GetValueOrDefault("hole_attempts");
                tail.Pass("Все кандидаты прогнаны на собственных пластинах; таблица в данных шага A7.4.");
                return;
            }

            ReportFeature();
            EditDiameter();
            SaveCloseReopen();
            ReadThroughApi5();
            RejectInvalid();
        }
        finally
        {
            if (_options.KeepRunning)
            {
                _report.Environment["keep_running"] = true;
                _report.Environment["warning_orphan_process"] = "Запрошен --keep: экземпляр КОМПАС оставлен открытым; закройте его штатно, убийство по имени запрещено.";
            }
            else
            {
                Shutdown();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════ session ══
    private void Launch()
    {
        var step = _report.Begin(
            "A7.1",
            "Запуск собственного невидимого экземпляра и доказательство PID",
            "Доказуемо ли, что зонд обращается именно к тому экземпляру, который сам и запустил?");

        try
        {
            var before = Snapshot();
            step.Observe($"Процессов KOMPAS.exe до старта: {before.Length} ({Join(before)}).");
            if (before.Length > 0)
            {
                step.Observe("На машине уже есть экземпляры КОМПАС — зонд заводит СВОЙ и работает только с ним; чужие не трогает и по имени не убивает.");
            }

            var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
                       ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован.");
            _app = (KompasObject)Activator.CreateInstance(type)!;
            _app.Visible = false;

            var created = WaitUntilNewProcess(before, 90_000);
            step.Data["pids_before"] = before;
            step.Data["created_pids"] = created;
            if (created.Length != 1)
            {
                step.Fail($"Дифф списка процессов дал {created.Length} новых PID вместо ровно одного — сеанс не атрибутируется, проба останавливается.");
                return;
            }

            _options.ProcessId = (int)created[0];
            _report.Environment["pid_launched"] = created[0];
            step.Observe($"Дифф списка процессов: ровно один новый PID {created[0]}.");

            // Independent confirmation of the same fact by a different route (P0.4).
            var windowHandle = _app.ksGetHWindow();
            var pidFromWindow = WindowPid(windowHandle);
            step.Data["hwindow"] = "0x" + windowHandle.ToString("X");
            step.Data["pid_from_hwnd"] = pidFromWindow;
            step.Observe($"ksGetHWindow() = 0x{windowHandle:X} → GetWindowThreadProcessId → PID {Api5.Raw(pidFromWindow)}.");

            var version = DescribeVersion();
            step.Data["kompas_build"] = version;
            step.Observe($"Версия приложения: {version}.");

            if (pidFromWindow == (int)created[0])
            {
                step.Pass($"Сеанс атрибутирован: PID {created[0]} подтверждён двумя независимыми способами — диффом процессов и окном COM-объекта.");
            }
            else
            {
                step.Fail($"PID разошлись: дифф процессов дал {created[0]}, окно COM-объекта — {Api5.Raw(pidFromWindow)}.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Запуск не удался: " + HResult.Describe(ex));
        }
    }

    private string DescribeVersion()
    {
        try
        {
            _app.ksGetSystemVersion(out var major, out var minor, out var release, out var build);
            var text = major + "." + minor + "." + release + "." + build;
            _report.Environment["api5_system_version"] = text;
            return text;
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }

    private void Shutdown()
    {
        var step = _report.Begin(
            "A7.Z",
            "Завершение сеанса: закрыть тестовый документ, остановить приложение, не убивая процесс по имени",
            "Остаётся ли осиротевший KOMPAS.exe после прогона?");

        try
        {
            if (_doc is not null)
            {
                step.Data["doc_close"] = Api5.Raw(Obj(() => _doc.close()));
            }

            // The task's rule is KompasObject.Quit(). It goes first precisely because it owns the
            // process: once the server is gone, any later call on the other API's object throws
            // RPC_E_DISCONNECTED, and treating that as a failure would hide a clean shutdown.
            var api5QuitError = false;
            try
            {
                _app?.Quit();
                step.Observe("KompasObject.Quit() (API5) выполнен.");
            }
            catch (Exception ex)
            {
                api5QuitError = true;
                step.Observe("API5 Quit() бросил " + HResult.Describe(ex) + " — пробуется IApplication.Quit() (API7).");
            }

            if (api5QuitError && _app7 is not null)
            {
                try
                {
                    _app7.Quit();
                    step.Observe("IApplication (API7).Quit() выполнен.");
                }
                catch (Exception ex) when (HResult.Of(ex) == HResult.RPC_E_DISCONNECTED)
                {
                    step.Observe("API7 Quit() вернул RPC_E_DISCONNECTED — сервер уже завершился, это не отказ.");
                }
                catch (Exception ex)
                {
                    step.Observe("API7 Quit() бросил " + HResult.Describe(ex));
                }
            }
            var pid = _options.ProcessId;
            if (pid is null)
            {
                step.Unknown("PID не был атрибутирован — проверять исчезновение нечего.");
                return;
            }

            var clock = Stopwatch.StartNew();
            var alive = true;
            while (clock.Elapsed < TimeSpan.FromSeconds(60))
            {
                alive = Process.GetProcessesByName("KOMPAS").Any(p => p.Id == pid.Value);
                if (!alive)
                {
                    break;
                }

                System.Threading.Thread.Sleep(250);
            }

            _report.Environment["owned_pid_alive_after_quit"] = alive;
            _report.Environment["quit_wait_ms"] = clock.ElapsedMilliseconds;
            var leftovers = Snapshot();
            _report.Environment["kompas_processes_after_shutdown"] = leftovers.Length;
            _report.Environment["kompas_pids_after_shutdown"] = leftovers;
            step.Observe($"PID {pid} жив после Quit(): {alive}; осиротевших KOMPAS.exe после прогона: {leftovers.Length} ({Join(leftovers)}).");

            if (alive)
            {
                step.Fail($"Quit() не завершил экземпляр (PID {pid} жив). Убийство по имени запрещено (ТЗ §1.6) — разбирать нужно по PID.");
            }
            else
            {
                step.Pass($"Штатное завершение: PID {pid} исчез за {clock.ElapsedMilliseconds} мс, осиротевших процессов KOMPAS.exe — {leftovers.Length}.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(HResult.Describe(ex));
            step.Fail("Завершение сеанса прошло нештатно: " + HResult.Describe(ex));
        }
    }

    // ════════════════════════════════════════════════════════════════════ the bridge ══
    private void Bridge()
    {
        var step = _report.Begin(
            "A7.2",
            "Мост API5→API7: KompasObject.ksGetApplication7() и TransferInterface()",
            "Оба интерфейса обращаются к одному сеансу, или API7 приходит из второго экземпляра?");

        try
        {
            _clock.Restart();
            var raw = _app.ksGetApplication7();
            step.Observe($"KompasObject.ksGetApplication7() → {(raw is null ? "null" : Api5.RuntimeName(raw) + " за " + _clock.ElapsedMilliseconds + " мс")}");
            _report.Environment["ks_get_application7"] = raw is null ? "null" : Api5.RuntimeName(raw);
            if (raw is null)
            {
                step.Fail("ksGetApplication7() вернул null: API7 из этого сеанса не получен. Шаги API7 не выполняются.");
                return;
            }

            _app7 = (KompasAPI7.IApplication)raw;
            _app7.Visible = false;
            step.Data["iapplication_iid"] = typeof(KompasAPI7.IApplication).GUID.ToString("B").ToUpperInvariant();
            step.Data["api7_visible"] = Api5.Raw(Flag(() => _app7.Visible));
            step.Data["api7_documents"] = Api5.Raw(Count(() => _app7.Documents.Count));
            step.Observe($"IApplication получен через QI на {step.Data["iapplication_iid"]}; Visible={step.Data["api7_visible"]}, документов={step.Data["api7_documents"]}.");

            var versionArgs = new object?[4];
            try
            {
                var ok = typeof(KompasAPI7.IApplication).GetMethod("GetSystemVersion")?.Invoke(_app7, versionArgs);
                step.Observe($"IApplication.GetSystemVersion → {Api5.Raw(ok)} [{string.Join(".", versionArgs.Select(a => Api5.Raw(a)))}].");
            }
            catch (Exception ex)
            {
                step.Observe("IApplication.GetSystemVersion: " + HResult.Describe(ex));
            }

            // One session or two? API5's main-window handle and API7's MainWindowHandle must map to
            // the same PID. MainWindowHandle is a member the prebuilt wrapper does NOT declare
            // (A7.0 measured that), so it is read through the object's own IDispatch.
            var api5Window = _app.ksGetHWindow();
            var api5Pid = WindowPid(api5Window);
            var handleProbe = Late.Dispid(_app7, "MainWindowHandle");
            var api7Handle = Late.Get(_app7, "MainWindowHandle");
            var api7Pid = api7Handle is int handle && handle != 0 ? WindowPid(new IntPtr(handle)) : null;
            step.Data["late_bind_main_window_handle"] = handleProbe;
            step.Data["api7_main_window_handle"] = Api5.Raw(api7Handle);
            step.Data["pid_via_api5_hwnd"] = api5Pid;
            step.Data["pid_via_api7_hwnd"] = api7Pid;
            step.Observe($"Окно API5 = 0x{api5Window:X} → PID {Api5.Raw(api5Pid)}; окно API7 = {Api5.Raw(api7Handle)} → PID {Api5.Raw(api7Pid)} ({handleProbe}).");

            var sameSession = api5Pid is not null && api5Pid == api7Pid && _options.ProcessId == api5Pid;
            step.Data["same_session_proved"] = sameSession;
            if (sameSession)
            {
                step.Pass($"Один сеанс доказан: {api5Pid} подтверждён и окном API5, и окном API7; второго экземпляра не заводилось.");
            }
            else
            {
                step.Unknown($"Один ли это сеанс числами не подтверждён (API5 → {Api5.Raw(api5Pid)}, API7 → {Api5.Raw(api7Pid)}); API7 при этом получен вызовом ksGetApplication7() на том же объекте API5, а вторым Activator.CreateInstance.");
            }
        }
        catch (Exception ex)
        {
            _app7 = null;
            step.Errors.Add(ex.ToString());
            step.Fail("Мост не построен: " + HResult.Describe(ex));
        }
    }

    // ═════════════════════════════════════════════════════════════ step 1: the baseline ══
    private void Baseline()
    {
        var step = _report.Begin(
            "A7.3",
            "Эталон через API5: брусок 100×80×10",
            "Совпадает ли базовая линия с уже известным числом 79999.99999999999?");

        try
        {
            _doc = (ksDocument3D)_app.Document3D();
            if (Flag(() => _doc.Create(true, true)) != true)
            {
                step.Fail("Document3D().Create(invisible=true, деталь) не вернул true.");
                return;
            }

            _part = (ksPart)_doc.GetPart(-1);
            if (Api5.BasePlate(_part, PlateWidth, PlateHeight, PlateThickness, step) is null)
            {
                step.Fail("Пластина не построена — дальше измерять нечего.");
                return;
            }

            Flag(() => _doc.RebuildDocument());
            _plateVolume = Api5.Volume(_part) ?? double.NaN;
            var expectation = PlateWidth * PlateHeight * PlateThickness;
            step.Data["baseline_volume_mm3"] = Api5.Num(_plateVolume);
            step.Data["baseline_expectation_mm3"] = Api5.Num(expectation);
            step.Data["baseline_bodies"] = Api5.BodyCount(_part);
            step.Data["baseline_faces"] = Api5.FaceCount(_part);
            step.Data["baseline_edges"] = Api5.EdgeCount(_part);
            _report.Environment["baseline_volume_mm3"] = _plateVolume;
            step.Observe($"V₀ = {Api5.Num(_plateVolume)} мм³ при аналитическом {Api5.Num(expectation)}; тел {step.Data["baseline_bodies"]}, граней {step.Data["baseline_faces"]}, рёбер {step.Data["baseline_edges"]}.");
            step.Observe("Известная базовая линия проекта — 79999.99999999999 (79999.99999999999 против 80000): это особенность ядра, допуск §3.3 из-за неё не расширяется.");

            step.Pass(double.IsNaN(_plateVolume) ? "Эталон построен, но объём не прочитан." : "Эталон готов: брусок 100×80×10 с измеренным объёмом.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Эталон не построен: " + HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════════ step 2: the native hole through API7 ══
    private void CreateHole()
    {
        var step = _report.Begin(
            "A7.4",
            "Родное «Отверстие» через API7: цилиндрическое сквозное Ø10 в центре грани 100×80",
            "Создаётся ли родовой признак операции «Отверстие» средствами API7 и режет ли он материал?");

        var attempts = new List<string>();
        try
        {
            var document7 = Transfer(_doc, "документ: API5 ksDocument3D", "API7 IKompasDocument3D", step);
            if (document7 is not KompasAPI7.IKompasDocument3D document3d)
            {
                step.Fail("TransferInterface(документ → ksAPI7Dual) не дал IKompasDocument3D — API7-представления документа нет.");
                return;
            }

            step.Data["qi_ipartdocument"] = document7 is KompasAPI7.IPartDocument;
            var part7 = document3d.TopPart;
            step.Data["top_part"] = part7 is null ? "null" : Api5.RuntimeName(part7);
            if (part7 is null)
            {
                step.Fail("IKompasDocument3D.TopPart вернул null — контейнер модельных объектов искать негде.");
                return;
            }

            // SDK documents IModelContainer as an additional interface of IPart7, obtained through
            // IUnknown::QueryInterface — so the cast IS that QI, and its result is a measurement.
            _api7Part = part7;
            _container = part7 as KompasAPI7.IModelContainer;
            step.Data["qi_imodelcontainer"] = _container is not null;
            if (_container is null)
            {
                step.Fail("IPart7 не отвечает на QI(IModelContainer): семейства модельных объектов API7 не открыты.");
                return;
            }

            step.Data["container_live_members"] = TlbScan.LiveMembers(_container, new[] { "Holes3D", "Sketchs", "Extrusions", "Points3D", "ElementaryBodies", "PipeElements" });
            _holes = _container.Holes3D;
            step.Data["holes_count_before"] = Api5.Raw(Count(() => _holes!.Count));
            _report.Environment["api7_container_live_members"] = step.Data["container_live_members"];
            if (_holes is null)
            {
                step.Fail("IModelContainer.Holes3D вернул null — семейства отверстий в контейнере нет.");
                return;
            }

            step.Data["volume_before_hole"] = Api5.Num(Api5.Volume(_part));

            // Rungs are cumulative in configuration but may create their own feature object, because
            // the hypotheses differ in what has to exist BEFORE IHoles3D.Add(): a base sketch whose
            // circles carry the positions cannot be attached afterwards — IHole3D declares no sketch
            // member at all (measured: IHole3D has 22 own members, none named Sketch).
            var context = new HoleContext(this, step);
            foreach (var rung in Rungs(step))
            {
                var outcome = context.Run(rung);
                if (outcome is not { } result || !result.Matches)
                {
                    continue;
                }

                var expectation = ExpectedThroughHole(10d);
                var removed = RemovedByHole(10d);
                // In --controls the search runs past the first hit; the headline fields belong to the
                // first candidate that matched, not to the last one.
                var firstHit = step.Conclusion is null;
                step.Data["hole_attempts"] = context.Attempts.ToArray();
                if (!firstHit)
                {
                    step.Observe("--controls: кандидат «" + rung.Name + "» тоже совпал; заголовок остаётся у первого.");
                    continue;
                }

                _route = rung.Name;
                _diameterIsMillimetres = result.DiameterReadBack is double unit && Math.Abs(unit - 10d) < Math.Abs(unit - 0.01d);
                step.Data["hole_configuration"] = rung.Name;
                step.Data["hole_volume_after_mm3"] = Api5.Num(result.Volume);
                step.Data["hole_delta_mm3"] = Api5.Num(result.Delta);
                step.Data["hole_expected_removed_mm3"] = Api5.Num(removed);
                step.Data["hole_removed_error_mm3"] = Api5.Num(Math.Abs((result.Delta ?? double.NaN) - removed));
                step.Data["hole_expectation_mm3"] = Api5.Num(expectation);
                step.Data["hole_tolerance_mm3"] = Api5.Num(Tolerance(expectation));
                step.Data["hole_volume_error_mm3"] = Api5.Num(Math.Abs((result.Volume ?? double.NaN) - expectation));
                step.Data["holes_count_after_add"] = Api5.Raw(Count(() => _holes!.Count));
                step.Observe(string.Join("; ", context.Log));
                step.Pass(
                    "Родной признак «Отверстие» создан через API7 и прорезал материал: снято "
                    + Api5.Num(result.Delta) + " мм³ при ожидании 250π = " + Api5.Num(removed)
                    + "; остаток " + Api5.Num(result.Volume) + " мм³ при ожидании 80000 − 250π = " + Api5.Num(expectation)
                    + " (ошибка " + Api5.Num(Math.Abs((result.Volume ?? double.NaN) - expectation))
                    + " мм³ при допуске ±" + Api5.Num(Tolerance(expectation)) + "). Маршрут: " + rung.Name + ".");
                if (!_options.CollectControls)
                {
                    return;
                }

                // In --controls the search keeps going after the first hit so every candidate is
                // measured on untouched material; the session then no longer describes the winning
                // plate, which is why the rest of the program is skipped in that mode.
                step.Observe("--controls: продолжение поиска ради таблицы атрибуции.");
            }

            step.Observe(string.Join("; ", context.Log));
            step.Data["hole_configuration"] = _route;
            step.Data["hole_attempts"] = context.Attempts.ToArray();
            step.Data["minimal_reproduction"] = MinimalReproduction(context.Log);
            step.Fail(
                "Ни одна конфигурация не дала ожидаемую дельту; последняя ΔV = " + Api5.Num(_lastDelta)
                + ". Это НЕ «отверстия нет в API7»: IHoles3D.Add() даёт Hole3DClass с ModelObjectType 583, "
                + "не совпало число. Минимальное воспроизведение и текст ошибки КОМПАС — в данных шага.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Проба родного отверстия остановлена исключением: " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Everything a rung is allowed to touch: the API7 families of the live document, the ability to
    /// add another hole, and the API5 helpers needed to draw a base sketch first.
    /// </summary>
    private sealed class HoleContext
    {
        public HoleContext(HoleProbe owner, ProbeStep step)
        {
            Owner = owner;
            Step = step;
        }

        private HoleProbe Owner { get; }

        private ProbeStep Step { get; }

        public List<string> Log { get; } = new();

        public List<object?[]> Attempts { get; } = new();

        public KompasAPI7.IHole3D? AddHole()
        {
            Owner._clock.Restart();
            var hole = (KompasAPI7.IHole3D?)Owner._holes!.Add();
            Log.Add("IHoles3D.Add() → " + (hole is null ? "null" : Api5.RuntimeName(hole)) + " за " + Owner._clock.ElapsedMilliseconds + " мс");
            Owner._hole = hole;
            return hole;
        }

        /// <summary>An API5 sketch whose only geometry is a circle of the hole radius, centred on the
        /// model origin — the way the КОМПАС interface itself supplies hole positions.</summary>
        public void BaseCircleSketch(string name, double radiusMm, double planeOffsetMm)
        {
            var plane = Owner._part.GetDefaultEntity(Api5.PlaneXoy) as ksEntity;
            ksEntity target = plane!;
            if (Math.Abs(planeOffsetMm) > 1e-9)
            {
                var offset = (ksEntity?)Owner._part.NewEntity(Api5.PlaneOffset);
                if (offset is not null && offset.GetDefinition() is ksPlaneOffsetDefinition offsetDefinition)
                {
                    offsetDefinition.SetPlane(plane!);
                    offsetDefinition.offset = planeOffsetMm;
                    offsetDefinition.direction = true;
                    offset.Create();
                    target = offset;
                }
            }

            var sketch = (ksEntity?)Owner._part.NewEntity(Api5.Sketch);
            if (sketch is null || sketch.GetDefinition() is not ksSketchDefinition definition)
            {
                Log.Add(name + ": эскиз не создан");
                return;
            }

            sketch.name = name;
            definition.SetPlane(target);
            sketch.Create();
            if (definition.BeginEdit() is ksDocument2D editor)
            {
                editor.ksCircle(0d, 0d, radiusMm, 1);
            }

            definition.EndEdit();
            Owner._part.RebuildModel();
            Owner._doc.RebuildDocument();
            Log.Add(name + ": окружность R" + Api5.Num(radiusMm) + " на плоскости со смещением " + Api5.Num(planeOffsetMm) + " мм нарисована");
        }

        /// <summary>
        /// Rebuilds the reference plate in its own document, with its own API7 views. Without this a
        /// control candidate cuts whatever its predecessor left and reports the result as a property
        /// of its own configuration.
        /// </summary>
        public bool FreshPlate(Rung rung)
        {
            try
            {
                Owner._doc?.close();
            }
            catch (Exception)
            {
                // The document is being replaced. Anything still open at the end of the session is
                // reported by A7.Z, which counts processes rather than trusting this call.
            }

            Owner._doc = (ksDocument3D)Owner._app.Document3D();
            if (Flag(() => Owner._doc.Create(true, true)) != true)
            {
                Log.Add("документ кандидата не создан");
                return false;
            }

            Owner._part = (ksPart)Owner._doc.GetPart(-1);
            if (Api5.BasePlate(Owner._part, PlateWidth, PlateHeight, PlateThickness, Step, rung.Prefix) is null)
            {
                Log.Add("пластина кандидата не построена");
                return false;
            }

            Flag(() => Owner._doc.RebuildDocument());
            Owner._plateVolume = Api5.Volume(Owner._part) ?? double.NaN;

            var document7 = Owner.Transfer(Owner._doc, "документ: API5 ksDocument3D", "API7 IKompasDocument3D", Step) as KompasAPI7.IKompasDocument3D;
            Owner._api7Part = document7?.TopPart;
            Owner._container = Owner._api7Part as KompasAPI7.IModelContainer;
            Owner._holes = Owner._container?.Holes3D;
            Owner._hole = null;
            if (Owner._holes is null)
            {
                Log.Add("API7-представление или Holes3D не получены");
                return false;
            }

            return true;
        }

        public (double? Volume, double? Delta, bool Matches, double? DiameterReadBack)? Run(Rung rung)
        {
            if (!FreshPlate(rung))
            {
                Attempts.Add(new object?[] { rung.Name, "пластина для кандидата не готова", null, null, false });
                return null;
            }

            var before = Api5.Volume(Owner._part);
            KompasAPI7.IHole3D? hole;
            try
            {
                rung.Prepare?.Invoke(this);
                hole = AddHole();
                if (hole is null)
                {
                    Attempts.Add(new object?[] { rung.Name, "нет объекта отверстия", null, null, false });
                    return null;
                }

                Owner._clock.Restart();
                rung.Configure(hole);
                Log.Add(rung.Name + " → настройка за " + Owner._clock.ElapsedMilliseconds + " мс");
            }
            catch (Exception ex)
            {
                Log.Add(rung.Name + " → " + HResult.Describe(ex));
                Attempts.Add(new object?[] { rung.Name, "исключение настройки", HResult.Describe(ex), null, false });
                return null;
            }

            try
            {
                var updated = Flag(() => hole.Update());
                var readBack = Real(() => hole.Diameter);
                var modelType = Count(() => (int)hole.ModelObjectType);
                var valid = Flag(() => hole.Valid);
                Owner._part.RebuildModel();
                Owner._doc.RebuildDocument();
                Owner.Settle();
                var after = Api5.Volume(Owner._part);
                var delta = before is double b && after is double a ? b - a : (double?)null;
                Owner._lastDelta = delta;
                // Two separate numbers, two separate checks: what the hole removed must equal
                // π·r²·t, and what is left must equal 80000 − 250π. Conflating them is the bug
                // that first read a correct 785.3981633974472 мм³ as a failure.
                var removed = RemovedByHole(10d);
                var expectation = ExpectedThroughHole(10d);
                var removedMatches = delta is double dv && Math.Abs(dv - removed) <= Tolerance(removed);
                var volumeMatches = after is double av && Math.Abs(av - expectation) <= Tolerance(expectation);
                var matches = removedMatches && volumeMatches;
                var error = Owner.KompasErrorText(hole);

                Step.Observe(
                    "Конфигурация «" + rung.Name + "»: Update()=" + Api5.Raw(updated)
                    + ", Diameter перечитан=" + Api5.Num(readBack)
                    + ", ModelObjectType=" + Api5.Raw(modelType)
                    + ", Valid=" + Api5.Raw(valid)
                    + ", V=" + Api5.Num(before) + " → " + Api5.Num(after)
                    + "; ΔV=" + Api5.Num(delta) + " при ожидании " + Api5.Num(removed)
                    + " (ошибка " + Api5.Num(Math.Abs((delta ?? double.NaN) - removed)) + ")"
                    + "; V после=" + Api5.Num(after) + " при ожидании " + Api5.Num(expectation)
                    + " (ошибка " + Api5.Num(Math.Abs((after ?? double.NaN) - expectation)) + ")"
                    + " → " + (matches ? "СОВПАЛО" : "не совпало")
                    + "; ошибка КОМПАС: " + (error ?? "<нет>") + ".");
                Attempts.Add(new object?[] { rung.Name, "измерено", delta, readBack, matches });
                return (after, delta, matches, readBack);
            }
            catch (Exception ex)
            {
                Log.Add(rung.Name + " → перестроение не удалось: " + HResult.Describe(ex));
                Attempts.Add(new object?[] { rung.Name, "исключение перестроения", HResult.Describe(ex), null, false });
                return null;
            }
        }
    }

    /// <summary>
    /// The server's own account of a refusal: <c>IApplication.KompasError</c> (Code, Description)
    /// plus the feature's <c>ObjectError</c>/<c>State</c>. Recorded because a rejection without the
    /// wording is a guess about why it was rejected, and ADR-003 §4 requires the diagnostics to be
    /// precise.
    /// </summary>
    private string KompasErrorText(KompasAPI7.IHole3D hole)
    {
        var parts = new List<string>();
        try
        {
            var error = _app7?.KompasError;
            if (error is not null)
            {
                parts.Add("KompasError.Code=" + Api5.Raw(Count(() => (int)error.Code)));
                parts.Add("«" + Api5.Raw(Obj(() => error.Description)) + "»");
            }
        }
        catch (Exception ex)
        {
            parts.Add("KompasError: " + HResult.Describe(ex));
        }

        try
        {
            var feature = hole.Owner;
            if (feature is not null)
            {
                parts.Add("ObjectError=" + Api5.Raw(Count(() => (int)feature.ObjectError)));
                parts.Add("State=" + Api5.Raw(Count(() => (int)feature.State)));
                parts.Add("FeatureType=" + Api5.Raw(Count(() => (int)feature.FeatureType)));
                parts.Add("updateStamp=" + Api5.Raw(Count(() => feature.UpdateStamp)));
                parts.Add("имя=«" + Api5.Raw(Obj(() => feature.Name)) + "»");
            }
        }
        catch (Exception ex)
        {
            parts.Add("Owner: " + HResult.Describe(ex));
        }

        return parts.Count == 0 ? "<нет>" : string.Join(" ", parts);
    }

    /// <summary>A rung: what must exist before the feature, and what to set on it.</summary>
    /// <summary>A candidate: what must exist before the feature, and what to set on it. The
    /// prefix names the candidate's own plate so a failure can be traced to a document.</summary>
    private sealed record Rung(string Name, string Prefix, Action<HoleContext>? Prepare, Action<KompasAPI7.IHole3D> Configure);

    /// <summary>
    /// The candidate ladder, ordered by what the previous run's numbers said. The rung that reached
    /// <c>Valid=true</c> and removed material goes first; the readings that separate the unit of
    /// <c>Diameter</c> and the necessity of each of the two base objects (position from the sketch,
    /// face from <c>IHoleDisposal.BaseSurface</c>) stay as control candidates, so the report shows
    /// which ingredient was decisive instead of asserting it.
    /// </summary>
    /// <summary>
    /// The candidate ladder, ordered by what the attribution run measured (see
    /// docs/acceptance/api7/api7-attribution.md): every candidate on its own fresh plate.
    ///
    /// <list type="bullet">
    /// <item>base surface + perpendicular, no positioning sketch → ΔV = 250π. Decisive.</item>
    /// <item>positioning sketch only, no base surface → ΔV = 0.</item>
    /// <item>neither → ΔV = 0.</item>
    /// <item>sketch + base surface → ΔV = 250π (same as the first, so the sketch contributes nothing
    /// except two extra objects in the feature tree).</item>
    /// </list>
    ///
    /// The headline route is therefore the minimal one. The metre-unit candidate stays where it is
    /// because it is the measurement that settled the unit of <c>Diameter</c>, and the sketch
    /// variants stay as the controls that show what the extra objects are worth.
    /// </summary>
    private IEnumerable<Rung> Rungs(ProbeStep step)
    {
        yield return new Rung(
            "минимальный маршрут Ø10 мм: BaseSurface(верхняя грань, перенос из API5) + Perpendicular + DepthType=сквозь",
            "a7-min",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is not KompasAPI7.IHoleDisposal disposal)
                {
                    throw new InvalidOperationException("объект отверстия не отвечает на QI(IHoleDisposal)");
                }

                disposal.BaseSurface = TopFace(step) ?? throw new InvalidOperationException("верхняя грань не перенесена в API7");
                disposal.Perpendicular = true;
            });

        yield return new Rung(
            "контроль единиц: тот же минимальный состав, но Diameter=0.01 (250π при 0.01 доказали бы метры)",
            "a7-unit",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 0.01d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is KompasAPI7.IHoleDisposal disposal)
                {
                    disposal.BaseSurface = TopFace(step);
                    disposal.Perpendicular = true;
                }
            });

        yield return new Rung(
            "контроль состава: только базовая грань, без Perpendicular",
            "a7-noperp",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is KompasAPI7.IHoleDisposal disposal)
                {
                    disposal.BaseSurface = TopFace(step);
                }
            });

        yield return new Rung(
            "контроль состава: ни эскиза, ни грани — только Diameter=10 и DepthType=сквозь",
            "a7-bare",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            });

        yield return new Rung(
            "контроль состава: только эскиз с окружностью R5 на верхней плоскости, без BaseSurface",
            "a7-sketch",
            context => context.BaseCircleSketch("a7-sketch-only", 5d, PlateThickness),
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
            });

        yield return new Rung(
            "контроль полноты: эскиз R5 + базовая грань + Perpendicular (маршрут предыдущего заголовка)",
            "a7-sketch-face",
            context => context.BaseCircleSketch("a7-sketch-face", 5d, PlateThickness),
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is KompasAPI7.IHoleDisposal disposal)
                {
                    disposal.BaseSurface = TopFace(step);
                    disposal.Perpendicular = true;
                }
            });

        yield return new Rung(
            "позиция центра через ILocalCSObject.LocalCoordinateSystem",
            "a7-lcs",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is not KompasAPI7.ILocalCSObject local)
                {
                    throw new InvalidOperationException("объект отверстия не отвечает на QI(ILocalCSObject)");
                }

                var system = local.LocalCoordinateSystem ?? throw new InvalidOperationException("LocalCoordinateSystem вернул null");
                system.X = 0d;
                system.Y = 0d;
                system.Z = 0d;
            });

        yield return new Rung(
            "база — собственная плоскость XOY из API7 (IPart7.DefaultObject), без переноса грани",
            "a7-plane",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is not KompasAPI7.IHoleDisposal disposal)
                {
                    throw new InvalidOperationException("объект отверстия не отвечает на QI(IHoleDisposal)");
                }

                var plane = Obj(() => _api7Part!.DefaultObject[ksObj3dTypeEnum.o3d_planeXOY]);
                disposal.BaseSurface = plane as KompasAPI7.IModelObject ?? throw new InvalidOperationException("DefaultObject(XOY) не выдан как IModelObject");
                disposal.Perpendicular = true;
            });

        yield return new Rung(
            "вершина привязки отдельной модельной точкой из Points3D (поверх базы из грани)",
            "a7-point",
            null,
            hole =>
            {
                hole.HoleType = ksHoleTypeEnum.ksHTBase;
                hole.Diameter = 10d;
                hole.DepthType = ksDepthTypeEnum.ksDTReachThrough;
                if (hole is not KompasAPI7.IHoleDisposal disposal || _container is null)
                {
                    throw new InvalidOperationException("нет IHoleDisposal или контейнера");
                }

                disposal.BaseSurface = TopFace(step);
                disposal.Perpendicular = true;
                if (Obj(() => _container.Points3D) is KompasAPI7.IPoints3D points && Obj(() => points.Add()) is KompasAPI7.IModelObject vertex)
                {
                    disposal.AssociationVertex = vertex;
                }
            });
    }

    private static string Describe(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            var type = value.GetType();
            var name = type.GetProperty("Name")?.GetValue(value) as string;
            var reference = type.GetProperty("Reference")?.GetValue(value);
            var modelType = type.GetProperty("ModelObjectType")?.GetValue(value);
            return type.Name
                   + (name is null ? string.Empty : " «" + name + "»")
                   + (reference is null ? string.Empty : " ref=" + Api5.Raw(reference))
                   + (modelType is null ? string.Empty : " type=" + Api5.Raw(modelType));
        }
        catch (Exception)
        {
            return value.GetType().Name;
        }
    }

    /// <summary>Name of an enum-valued API7 member; a throwing read is reported as the failure,
    /// not as an empty name.</summary>
    /// <summary>One hole element of the collection, trying the given zero-based index first and
    /// its one-based spelling second. The probe does not know which convention Holes3D answers,
    /// and reporting "null" from the wrong one would read as "the feature was lost".</summary>
    private KompasAPI7.IHole3D? HoleAt(int index)
    {
        if (_holes is null)
        {
            return null;
        }

        try
        {
            return (KompasAPI7.IHole3D?)_holes.Hole3D[index];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string EnumName<T>(Func<T> read)
    {
        try
        {
            return read()?.ToString() ?? "<нет>";
        }
        catch (Exception ex)
        {
            return "<" + HResult.Describe(ex) + ">";
        }
    }

    // ═════════════════════════════════════════════════════ step 5: edit the diameter ══
    private void EditDiameter()
    {
        var step = _report.Begin(
            "A7.6",
            "Правка существующего отверстия на Ø12, перестроение, замер",
            "Правка происходит на месте и даёт ли объём 80000 − 360π?");

        if (_hole is null)
        {
            step.Unknown("Признака нет — правка не проверялась.");
            return;
        }

        try
        {
            var before = Api5.Volume(_part);
            var identityBefore = Describe(_hole);
            var stampBefore = Count(() => _hole.Owner!.UpdateStamp);
            var featuresBefore = Api5.ReadFeatures(_part, step).Count;

            _clock.Restart();
            _hole.Diameter = Scale(12d);
            var updated = Flag(() => _hole.Update());
            var rebuilt = Flag(() => _part.RebuildModel());
            var documentRebuilt = Flag(() => _doc.RebuildDocument());
            Settle();
            var after = Api5.Volume(_part);
            var expectation = ExpectedThroughHole(12d);
            var removed = _plateVolume - (after ?? double.NaN);

            step.Data["diameter_set_to_requested_mm"] = 12d;
            step.Data["diameter_set_to_raw"] = Scale(12d);
            step.Data["diameter_read_back"] = Api5.Num(Real(() => _hole.Diameter));
            step.Data["update"] = updated;
            step.Data["rebuild_model"] = rebuilt;
            step.Data["rebuild_document"] = documentRebuilt;
            step.Data["elapsed_ms"] = _clock.ElapsedMilliseconds;
            step.Data["volume_before"] = Api5.Num(before);
            step.Data["volume_after"] = Api5.Num(after);
            step.Data["expected_volume_mm3"] = Api5.Num(expectation);
            step.Data["tolerance_mm3"] = Api5.Num(Tolerance(expectation));
            step.Data["volume_error_mm3"] = Api5.Num(Math.Abs((after ?? double.NaN) - expectation));
            step.Data["material_removed_mm3"] = Api5.Num(removed);
            step.Data["expected_removed_mm3"] = Api5.Num(360d * Math.PI);
            step.Data["same_object_identity"] = identityBefore == Describe(_hole);
            step.Data["update_stamp"] = Api5.Raw(stampBefore) + " → " + Api5.Raw(Count(() => _hole.Owner!.UpdateStamp));
            step.Data["feature_count"] = featuresBefore + " → " + Api5.ReadFeatures(_part, step).Count;
            step.Observe(
                $"Ø10→Ø12: V = {Api5.Num(before)} → {Api5.Num(after)}; ожидание {Api5.Num(expectation)} мм³ (= 80000 − 360π), допуск ±{Api5.Num(Tolerance(expectation))}, ошибка {Api5.Num(Math.Abs((after ?? double.NaN) - expectation))}; "
                + $"снято материала {Api5.Num(removed)} при ожидании {Api5.Num(360d * Math.PI)} (250π → 360π); Update()={Api5.Raw(updated)}, RebuildModel()={Api5.Raw(rebuilt)}, RebuildDocument()={Api5.Raw(documentRebuilt)}, тот же объект={Api5.Raw(step.Data["same_object_identity"])}, число признаков {step.Data["feature_count"]}.");

            if (after is double value && Math.Abs(value - expectation) <= Tolerance(expectation))
            {
                step.Pass("Правка диаметра подтверждена числом: объём совпал с 80000 − 360π в допуске §3.3; признак тот же, число признаков не изменилось.");
            }
            else
            {
                step.Fail($"Правка числом не подтверждена: V={Api5.Num(after)} против {Api5.Num(expectation)} (допуск не расширялся).");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Правка не выполнена: " + HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════ steps 6 and 7: save, close, reopen ══
    private void SaveCloseReopen()
    {
        var step = _report.Begin(
            "A7.7",
            "Save → закрыть только тестовый документ → reopen; сохранность и редактируемость признака",
            "Переживает ли родной признак цикл сохранения и остаётся ли он редактируемым?");

        if (_hole is null)
        {
            step.Unknown("Признака нет — сохранность не проверялась.");
            return;
        }

        try
        {
            var path = Path.Combine(_options.WorkDir, "api7-native-hole.m3d");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var diameterBefore = Real(() => _hole.Diameter);
            var volumeBefore = Api5.Volume(_part);
            var saved = Flag(() => _doc.SaveAs(path));
            step.Data["save_as"] = path;
            step.Data["save_returned"] = saved;
            step.Data["file_exists"] = File.Exists(path);
            if (saved != true || !File.Exists(path))
            {
                step.Fail($"Документ не сохранён (SaveAs → {Api5.Raw(saved)}, файл существует = {File.Exists(path)}).");
                return;
            }

            _savedPath = path;
            step.Data["file_sha256"] = InteropResolver.Sha256(path);
            step.Data["file_size_bytes"] = new FileInfo(path).Length;
            _report.Environment["test_model_path"] = path;
            _report.Environment["test_model_sha256"] = step.Data["file_sha256"];
            step.Artifacts.Add(path);
            step.Observe($"Сохранено: {path}; sha256={step.Data["file_sha256"]}; {step.Data["file_size_bytes"]} байт.");

            var documentsBefore = Count(() => _app7!.Documents.Count);
            var closed = Flag(() => _doc.close());
            var documentsAfterClose = Count(() => _app7!.Documents.Count);
            step.Data["close_returned"] = closed;
            step.Data["documents_before_close"] = Api5.Raw(documentsBefore);
            step.Data["documents_after_close"] = Api5.Raw(documentsAfterClose);
            step.Observe($"close() → {Api5.Raw(closed)}; документов по счёту API7 было {Api5.Raw(documentsBefore)}, стало {Api5.Raw(documentsAfterClose)} — закрыт ровно тестовый документ.");

            var reopened = (ksDocument3D)_app.Document3D();
            var opened = Flag(() => reopened.Open(path, true));
            step.Data["reopen_returned"] = opened;
            if (opened != true)
            {
                step.Fail("Open(путь, invisible=true) не вернул true — перечитывать признак нечем.");
                return;
            }

            _doc = reopened;
            _part = (ksPart)_doc.GetPart(-1);
            var document7 = Transfer(_doc, "переоткрытый документ: API5 ksDocument3D", "API7 IKompasDocument3D", step) as KompasAPI7.IKompasDocument3D;
            _container = document7?.TopPart as KompasAPI7.IModelContainer;
            _holes = _container?.Holes3D;
            var countAfterReopen = Count(() => _holes!.Count);
            step.Data["holes_count_after_reopen"] = Api5.Raw(countAfterReopen);
            _hole = countAfterReopen > 0 ? HoleAt(0) ?? HoleAt(1) : null;
            step.Data["hole_after_reopen"] = _hole is null ? "null" : Describe(_hole);
            step.Data["hole_model_object_type_after_reopen"] = Count(() => (int)_hole!.ModelObjectType);
            if (_hole is null)
            {
                step.Fail($"После перезагрузки в Holes3D ничего нет (Count = {Api5.Raw(countAfterReopen)}) — родной признак цикл сохранения не пережил.");
                return;
            }

            var diameterRead = Real(() => _hole.Diameter);
            var volumeAfterReopen = Api5.Volume(_part);
            step.Data["diameter_before_save"] = Api5.Num(diameterBefore);
            step.Data["diameter_read_after_reopen"] = Api5.Num(diameterRead);
            step.Data["volume_before_save"] = Api5.Num(volumeBefore);
            step.Data["volume_after_reopen"] = Api5.Num(volumeAfterReopen);

            // Editability after reload: one more change, measured against its own expectation.
            const double editTo = 14d;
            _hole.Diameter = Scale(editTo);
            var updated = Flag(() => _hole.Update());
            Flag(() => _part.RebuildModel());
            Flag(() => _doc.RebuildDocument());
            Settle();
            var volumeAfterEdit = Api5.Volume(_part);
            var expectation = ExpectedThroughHole(editTo);
            step.Data["editable_diameter_requested_mm"] = editTo;
            step.Data["editable_diameter_set_raw"] = Scale(editTo);
            step.Data["editable_update"] = updated;
            step.Data["editable_volume_after_edit"] = Api5.Num(volumeAfterEdit);
            step.Data["editable_expected_mm3"] = Api5.Num(expectation);
            step.Data["editable_tolerance_mm3"] = Api5.Num(Tolerance(expectation));
            step.Data["editable_diameter_read_back"] = Api5.Num(Real(() => _hole.Diameter));

            var survives = Equals(diameterRead, diameterBefore)
                           && volumeAfterReopen is double v && volumeBefore is double u && Math.Abs(v - u) <= Tolerance(u);
            var editable = volumeAfterEdit is double e && Math.Abs(e - expectation) <= Tolerance(expectation);
            step.Observe(
                $"После перезагрузки: диаметр перечитан {Api5.Num(diameterRead)} (до сохранения {Api5.Num(diameterBefore)}), объём {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfterReopen)}; "
                + $"правка на Ø{editTo:0} → V={Api5.Num(volumeAfterEdit)} при ожидании {Api5.Num(expectation)} (Update()={Api5.Raw(updated)}).");

            if (survives && editable)
            {
                step.Pass("Родной признак пережил save→close→reopen, его параметр прочитан, и он по-прежнему редактируется с совпадением чисел.");
            }
            else
            {
                step.Fail($"Сохранность/редактируемость числами не подтверждены: сохранность={survives}, редактируемость={editable}.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Цикл сохранения не пройден: " + HResult.Describe(ex));
        }
    }

    // ═════════════════════════════════════ step 8: what API5 sees, and what it does not ══
    private void ReadThroughApi5()
    {
        var step = _report.Begin(
            "A7.8",
            "Чтение результата средствами API5: какие переходы между API понадобились и что не видно",
            "Попадает ли API7-признак в то же перечисление дерева, которым пользуется API5-код?");

        try
        {
            step.Data["api5_volume_mm3"] = Api5.Num(Api5.Volume(_part));
            step.Data["api5_bodies"] = Api5.BodyCount(_part);
            step.Data["api5_faces"] = Api5.FaceCount(_part);
            step.Data["api5_edges"] = Api5.EdgeCount(_part);

            var features = Api5.ReadFeatures(_part, step);
            step.Data["api5_feature_tree"] = features.Select(f => f.Describe()).ToArray();
            foreach (var feature in features)
            {
                step.Observe("дерево API5: " + feature.Describe());
            }

            var operations = new List<string>();
            try
            {
                var collection = (ksEntityCollection?)_part.EntityCollection(Api5.OperationElement);
                var count = collection?.GetCount() ?? -1;
                step.Data["api5_entity_collection_110_count"] = count;
                for (var i = 0; i < count; i++)
                {
                    var element = collection?.GetByIndex(i);
                    operations.Add($"[{i}] {Api5.RuntimeName(element)} type={Api5.Raw((element as ksEntity)?.type)} имя=«{(element as ksEntity)?.name}»");
                }
            }
            catch (Exception ex)
            {
                operations.Add("EntityCollection(110) → " + HResult.Describe(ex));
            }

            step.Data["api5_operation_elements"] = operations;
            foreach (var line in operations)
            {
                step.Observe("operationElement: " + line);
            }

            var faces = Api5.ReadFaces(_part, step);
            step.Data["api5_face_rows"] = faces.Select(f => f.Describe()).ToArray();
            var cylinders = faces.Where(f => f.IsCylinder).ToList();
            foreach (var face in faces)
            {
                step.Observe("грань: " + face.Describe());
            }

            step.Data["api5_cylindrical_faces"] = cylinders.Count;

            // The placement verdict, computed rather than left to the reader: «at the centre of the
            // 100×80 face» means exactly one cylindrical face, radius = half the commanded diameter,
            // axis through (0, 0) — the rectangle is drawn centred on the model origin — and the
            // axis parallel to the sketch normal.
            var commandedDiameterMm = _hole is null ? (double?)null : Real(() => _hole.Diameter);
            var expectedRadius = commandedDiameterMm is double cd ? cd / 2d : (double?)null;
            var placed = cylinders.Count == 1
                         && expectedRadius is double r
                         && cylinders[0].CylinderRadius is double foundRadius
                         && Math.Abs(foundRadius - r) <= 0.01d
                         && cylinders[0].CylinderOrigin is double[] origin
                         && Math.Abs(origin[0]) <= 0.01d && Math.Abs(origin[1]) <= 0.01d
                         && cylinders[0].CylinderAxis is double[] axis
                         && Math.Abs(Math.Abs(axis[2]) - 1d) <= 0.01d;
            step.Data["placement_check_single_cylinder"] = cylinders.Count == 1;
            step.Data["placement_check_radius_matches_diameter"] = expectedRadius is double er
                && cylinders.Count == 1 && cylinders[0].CylinderRadius is double fr && Math.Abs(fr - er) <= 0.01d;
            step.Data["placement_check_axis_through_face_centre"] = cylinders.Count == 1
                && cylinders[0].CylinderOrigin is double[] o && Math.Abs(o[0]) <= 0.01d && Math.Abs(o[1]) <= 0.01d;
            step.Data["placement_check_axis_parallel_to_normal"] = cylinders.Count == 1
                && cylinders[0].CylinderAxis is double[] ax && Math.Abs(Math.Abs(ax[2]) - 1d) <= 0.01d;
            step.Data["placement_proved_at_face_centre"] = placed;
            step.Observe(
                "Проверка расположения (числами, не глазами): цилиндрических граней " + cylinders.Count
                + ", ожидался радиус " + Api5.Num(expectedRadius) + " мм, найден "
                + Api5.Num(cylinders.Count == 1 ? cylinders[0].CylinderRadius : null)
                + "; начало оси " + Api5.FaceReading.Point(cylinders.Count == 1 ? cylinders[0].CylinderOrigin : null)
                + " при центре грани (0, 0); направление " + Api5.FaceReading.Point(cylinders.Count == 1 ? cylinders[0].CylinderAxis : null)
                + " → «в центре грани 100×80»: " + (placed ? "подтверждено" : "не подтверждено") + ".");
            step.Data["api5_cylinder_radiuses_mm"] = cylinders.Select(c => c.CylinderRadius).ToArray();
            step.Data["api5_cylinder_heights_mm"] = cylinders.Select(c => c.CylinderHeight).ToArray();
            step.Data["api5_cylinder_origins_mm"] = cylinders.Select(c => Api5.FaceReading.Point(c.CylinderOrigin)).ToArray();
            step.Data["api5_cylinder_axes"] = cylinders.Select(c => Api5.FaceReading.Point(c.CylinderAxis)).ToArray();

            var holeReference = Count(() => _hole!.Reference);
            step.Data["api7_hole_reference"] = Api5.Raw(holeReference);
            if (holeReference is int reference)
            {
                var viaReference = Api5.SafeObject(() => _app.TransferReference(reference, -1));
                step.Data["transfer_reference_api7_to_api5"] = viaReference is null ? "null" : Api5.RuntimeName(viaReference);
                _transitions.Add($"TransferReference(IHole3D.Reference={reference}, docRef=-1) → {Api5.Raw(step.Data["transfer_reference_api7_to_api5"])}");
                step.Observe("TransferReference по reference отверстия → " + Api5.Raw(step.Data["transfer_reference_api7_to_api5"]) + ".");
            }

            var transferred = _hole is null ? null : Transfer(_hole, "признак: API7 IHole3D", "API5 ksEntity", step);
            step.Data["transfer_hole_to_api5"] = transferred is null ? "null" : Api5.RuntimeName(transferred);
            step.Data["transfer_hole_to_api5_entity_type"] = (transferred as ksEntity)?.type;
            step.Data["transfer_hole_to_api5_name"] = (transferred as ksEntity)?.name;
            var feature5 = (transferred as ksEntity)?.GetFeature() as ksFeature;
            step.Data["transfer_hole_api5_feature_type"] = feature5?.type;
            step.Data["transfer_hole_api5_feature_type_name"] = feature5 is null ? "<нет>" : Api5.ObjectTypeName(feature5.type);
            step.Data["transfer_hole_api5_feature_name"] = feature5?.name;
            step.Data["transfer_hole_api5_definition"] = (transferred as ksEntity)?.GetDefinition() is null ? "null" : Api5.RuntimeName((transferred as ksEntity)!.GetDefinition());
            step.Observe(
                $"TransferInterface(признак API7 → API5): {Api5.Raw(step.Data["transfer_hole_to_api5"])}, entity.type={Api5.Raw(step.Data["transfer_hole_to_api5_entity_type"])}, "
                + $"имя=«{step.Data["transfer_hole_to_api5_name"]}», ksFeature.type={Api5.Raw(step.Data["transfer_hole_api5_feature_type"])} ({step.Data["transfer_hole_api5_feature_type_name"]}), определение={step.Data["transfer_hole_api5_definition"]}.");

            var namedLikeHole = features
                .Where(f => f.Type == 52
                            || (f.Name ?? string.Empty).Contains("отверст", StringComparison.OrdinalIgnoreCase)
                            || (f.Name ?? string.Empty).Contains("hole", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var operationNamedLikeHole = operations.Any(o =>
                o.Contains("отверст", StringComparison.OrdinalIgnoreCase) || o.Contains("Hole", StringComparison.Ordinal));
            step.Data["api5_tree_names_a_hole_feature"] = namedLikeHole.Count > 0 || operationNamedLikeHole;
            step.Data["api5_all_feature_types"] = features.Select(f => f.Type).ToArray();
            step.Data["api5_distinct_feature_types"] = features.Select(f => f.Type).Distinct().ToArray();

            step.Observe("Переходы между API, потребовавшиеся в этом прогоне:");
            foreach (var line in _transitions)
            {
                step.Observe("  · " + line);
            }

            step.Data["not_visible_through_api5"] = new List<string>
            {
                "тип семейства: ksFeature.type у всех семейств = 105 (o3d_entity), отличить «Отверстие» от «Выдавливания» по номеру типа нельзя (проверено и здесь)",
                "параметры HoleType/DepthType/EndFaceType/IHoleDisposal через API5 не читаются: в Interop.Kompas6API5.dll имён с hole нет",
                "doc.FeatureCollection(...) возвращает null на любом objType — перечисление идёт только через part.GetFeature().SubFeatureCollection(...) и part.EntityCollection(110)",
            };
            step.Pass("Результат прочитан средствами API5; список переходов и перечень невидимого записаны.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Чтение средствами API5 не завершено: " + HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════════════ step 9: an invalid parameter ══
    private void RejectInvalid()
    {
        var step = _report.Begin(
            "A7.9",
            "Некорректный параметр: отказ обязан быть, документ — не измениться",
            "Отказывается ли API7 на Diameter ≤ 0, и остаётся ли документ прежним (объём + метки изменения)?");

        if (_hole is null)
        {
            step.Unknown("Признака нет — отказ не проверялся.");
            return;
        }

        var verdicts = new List<string>();
        foreach (var bad in new[] { 0d, Scale(-12d) })
        {
            try
            {
                var volumeBefore = Api5.Volume(_part);
                var stampBefore = Count(() => _hole.Owner!.UpdateStamp);
                var readBefore = Real(() => _hole.Diameter);
                var rebuildFlagBefore = Flag(() => _doc.treeNeedRebuild);

                string? thrown = null;
                try
                {
                    _hole.Diameter = bad;
                }
                catch (Exception ex)
                {
                    thrown = HResult.Describe(ex);
                }

                var updated = Flag(() => _hole.Update());
                Flag(() => _part.RebuildModel());
                Flag(() => _doc.RebuildDocument());
                Settle();

                var volumeAfter = Api5.Volume(_part);
                var stampAfter = Count(() => _hole.Owner!.UpdateStamp);
                var readAfter = Real(() => _hole.Diameter);

                // Three separate answers. The requirement names two of them; the third is what the
                // first run actually observed and must not be smoothed away.
                var unchanged = WithinBoth(volumeBefore, volumeAfter) && Equals(readAfter, readBefore);
                var rejectedInEffect = unchanged && readAfter is double ra && Math.Abs(ra - bad) > 1e-12d;
                var diagnostic = thrown ?? KompasErrorText(_hole);

                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["attempted_raw"] = bad,
                    ["setter"] = thrown ?? "принят без исключения",
                    ["update"] = updated,
                    ["volume_before"] = Api5.Num(volumeBefore),
                    ["volume_after"] = Api5.Num(volumeAfter),
                    ["diameter_read_before"] = Api5.Num(readBefore),
                    ["diameter_read_after"] = Api5.Num(readAfter),
                    ["update_stamp_before"] = Api5.Raw(stampBefore),
                    ["update_stamp_after"] = Api5.Raw(stampAfter),
                    ["tree_need_rebuild_before"] = Api5.Raw(rebuildFlagBefore),
                    ["tree_need_rebuild_after"] = Api5.Raw(Flag(() => _doc.treeNeedRebuild)),
                    // Что сравнивалось на самом деле — объём и перечитанный параметр. Их равенство
                    // НЕ означает неизменности документа: этот же замер показывает
                    // updateStamp 70 → 78, то есть дерево претерпело перестроение. Ключ поэтому
                    // называется точно, и «документ не изменился» не утверждается нигде.
                    ["geometry_and_readback_unchanged"] = unchanged,
                    ["document_state_unchanged"] = Equals(stampBefore, stampAfter),
                    ["rejected_in_effect"] = rejectedInEffect,
                    ["raised_a_diagnostic"] = thrown is not null,
                    ["diagnostic"] = diagnostic,
                };
                step.Data["bad_" + bad.ToString("0.###", CultureInfo.InvariantCulture)] = record;
                step.Observe(
                    $"Diameter={Api5.Num(bad)}: сеттер → {record["setter"]}; Update()={Api5.Raw(updated)}; V {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfter)}; "
                    + $"перечитанный диаметр {Api5.Num(readBefore)} → {Api5.Num(readAfter)}; updateStamp {Api5.Raw(stampBefore)} → {Api5.Raw(stampAfter)}; "
                    + $"диагностика: {diagnostic}");
                verdicts.Add(
                    bad + ": " + (rejectedInEffect ? "значение в модель не попало" : "значение принято")
                    + ", " + (unchanged ? "документ не изменился" : "ДОКУМЕНТ ИЗМЕНИЛСЯ")
                    + ", " + (thrown is not null ? "с исключением" : "без исключения"));

                if (rejectedInEffect && unchanged)
                {
                    step.Pass(
                        "Некорректный диаметр " + Api5.Num(bad) + " отклонён в смысле, который требует проба: в модель он не лёг "
                        + "(перечитывается прежнее " + Api5.Num(readBefore) + "), объём и перечитанный параметр не изменились. "
                        + "Форма отказа — не исключение: сеттер возвращает управление спокойно, Update() даёт true, а сервер оставляет "
                        + "липкий код ошибки (" + (diagnostic ?? "<не прочитан>") + ").");
                    RestoreValid(step);
                    return;
                }
            }
            catch (Exception ex)
            {
                step.Errors.Add("Diameter=" + Api5.Num(bad) + ": " + ex.ToString());
            }
        }

        step.Data["summary"] = string.Join("; ", verdicts);
        if (step.Verdict != Verdict.Pass && step.Verdict != Verdict.Fail)
        {
            step.Unknown("Ни один некорректный диаметр не был проверен до конца.");
        }
    }

    private void RestoreValid(ProbeStep step)
    {
        // Put the model back into the state the report describes, so the saved .m3d and the numbers
        // in the report still agree when somebody opens it by hand.
        try
        {
            _hole!.Diameter = Scale(12d);
            Flag(() => _hole.Update());
            Flag(() => _part.RebuildModel());
            Flag(() => _doc.RebuildDocument());
            step.Observe("После негативной проверки диаметр возвращён к Ø12.");
        }
        catch (Exception ex)
        {
            step.Observe("Вернуть диаметр не удалось: " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Millimetres into the unit <c>IHole3D.Diameter</c> answers in. Measured, not assumed: the
    /// candidate that removed 0.000785398166 мм³ from a 10 мм plate at <c>Diameter = 0.01</c>
    /// matches π·(0.01/2)²·10 exactly, so the unit is millimetres and this conversion is the
    /// identity. The unit candidate in the ladder keeps the metre reading tested rather than dropped.
    /// </summary>
    private double Scale(double millimetres) => _diameterIsMillimetres ? millimetres : millimetres / 1000d;

    /// <summary>
    /// The face to drill into, found with the API5 topology route the project already uses and
    /// carried into API7 by TransferInterface. That transition is one of the answers ADR-003 §3.8
    /// asks for, so it is logged rather than hidden.
    /// </summary>
    private KompasAPI7.IModelObject? TopFace(ProbeStep step)
    {
        if (_part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("API5: GetMainBody()/FaceCollection() недоступны — базовую грань найти нечем.");
            return null;
        }

        object? largest = null;
        double bestArea = double.MinValue;
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
            step.Observe("API5: ни одной гранью с читаемой площадью (FaceCollection дала " + count + " элементов).");
            return null;
        }

        step.Observe("API5: самая большая грань по площади = " + Api5.Num(bestArea) + " мм², переносится в API7.");
        return Transfer(largest, "верхняя грань: API5 ksFaceDefinition", "API7 IModelObject", step) as KompasAPI7.IModelObject;
    }

    /// <summary>
    /// What the model itself reports about the feature: its type as the model names it, its
    /// placement, and every parameter read back out of the object rather than remembered from what
    /// the probe wrote into it.
    /// </summary>
    private void ReportFeature()
    {
        var step = _report.Begin(
            "A7.5",
            "Как модель сама отчитывается о признаке: тип, расположение, параметры",
            "Родовой ли это признак операции «Отверстие» и какие его параметры перечитываются?");

        if (_hole is null)
        {
            step.Unknown("Признак не создан — отчёта о нём нет.");
            return;
        }

        try
        {
            step.Data["clr_type"] = Api5.RuntimeName(_hole);
            step.Data["name"] = Obj(() => _hole.Name);
            step.Data["model_object_type"] = Count(() => (int)_hole.ModelObjectType);
            step.Data["model_object_type_name"] = Count(() => (int)_hole.ModelObjectType) is int mot ? Api5.ObjectTypeName(mot) : "<нет>";
            step.Data["valid"] = Flag(() => _hole.Valid);
            step.Data["hole_type"] = Count(() => (int)_hole.HoleType);
            step.Data["hole_type_name"] = EnumName(() => _hole.HoleType);
            step.Data["diameter"] = Real(() => _hole.Diameter);
            step.Data["depth"] = Real(() => _hole.Depth);
            step.Data["depth_type"] = Count(() => (int)_hole.DepthType);
            step.Data["depth_type_name"] = EnumName(() => _hole.DepthType);
            step.Data["end_face_type"] = Count(() => (int)_hole.EndFaceType);
            step.Data["end_face_angle"] = Real(() => _hole.EndFaceAngle);
            step.Data["axis"] = Flag(() => _hole.Axis);
            step.Data["show_thread"] = Flag(() => _hole.ShowThread);
            step.Data["reference"] = Count(() => _hole.Reference);
            step.Data["thread"] = Describe(Obj(() => (object?)_hole.Thread));
            step.Data["hole_parameters"] = Describe(Obj(() => (object?)_hole.HoleParameters));
            step.Data["depth_vertex"] = Describe(Obj(() => (object?)_hole.DepthVertex));
            step.Data["depth_face"] = Describe(Obj(() => (object?)_hole.DepthFace));
            step.Data["owner_feature"] = DescribeOwner(step);
            step.Data["creation_route"] = _route;
            step.Data["diameter_unit_interpretation"] = _diameterIsMillimetres ? "мм" : "метры";

            if (_hole is KompasAPI7.IHoleDisposal disposal)
            {
                step.Data["disposal_base_surface"] = Describe(Obj(() => (object?)disposal.BaseSurface));
                step.Data["disposal_perpendicular"] = Flag(() => disposal.Perpendicular);
                step.Data["disposal_direction"] = Flag(() => disposal.Direction);
                step.Data["disposal_offset_type"] = Count(() => (int)disposal.OffsetType);
                step.Data["disposal_association_vertex"] = Describe(Obj(() => (object?)disposal.AssociationVertex));
            }

            if (_hole is KompasAPI7.ILocalCSObject local)
            {
                var system = Obj(() => (object?)local.LocalCoordinateSystem);
                if (system is KompasAPI7.ILocalCoordinateSystem cs)
                {
                    step.Data["local_cs_origin"] = "(" + Api5.Num(Real(() => cs.X)) + ", " + Api5.Num(Real(() => cs.Y)) + ", " + Api5.Num(Real(() => cs.Z)) + ")";
                }
            }

            step.Observe($"Модель отвечает: {Describe(_hole)}; ModelObjectType={Api5.Raw(step.Data["model_object_type"])} ({step.Data["model_object_type_name"]}); маршрут={_route}.");
            step.Observe("Значения перечитаны из самого объекта, а не те, что зонд в него записывал.");
            step.Pass("Тип, расположение и параметры признака перечитаны.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(HResult.Describe(ex));
            step.Fail("Перечитать параметры признака не удалось: " + HResult.Describe(ex));
        }
    }

    private string DescribeOwner(ProbeStep step)
    {
        try
        {
            var owner = _hole!.Owner;
            if (owner is null)
            {
                return "null";
            }

            return Describe(owner)
                   + " имя=«" + Api5.Raw(Obj(() => owner.Name)) + "»"
                   + " тип=" + Api5.Raw(Count(() => (int)owner.FeatureType))
                   + " updateStamp=" + Api5.Raw(Count(() => owner.UpdateStamp))
                   + " годен=" + Api5.Raw(Flag(() => owner.Valid))
                   + " ошибка=" + Api5.Raw(Count(() => (int)owner.ObjectError));
        }
        catch (Exception ex)
        {
            step.Observe("Owner не описан: " + HResult.Describe(ex));
            return "<" + ex.GetType().Name + ">";
        }
    }

    // ══════════════════════════════════════════════════════════════════════ plumbing ══
    /// <summary>
    /// One line per endpoint pair with a ×N count. Every candidate now rebuilds its own document, so
    /// without this the same API5→API7 line would repeat once per plate and bury the transition list
    /// ADR-003 §3.8 asks for.
    /// </summary>
    private void RecordTransfer(string line)
    {
        for (var i = 0; i < _transitions.Count; i++)
        {
            if (!string.Equals(_transitions[i], line, StringComparison.Ordinal)
                && !_transitions[i].StartsWith(line + " ×", StringComparison.Ordinal))
            {
                continue;
            }

            var marker = _transitions[i].LastIndexOf(" ×", StringComparison.Ordinal);
            _transitions[i] = marker > 0
                ? _transitions[i][..marker] + " ×" + (int.TryParse(_transitions[i][(marker + 2)..], out var n) ? n + 1 : 2)
                : _transitions[i] + " ×2";
            return;
        }

        _transitions.Add(line);
    }

    private object? Transfer(object source, string sourceLabel, string targetLabel, ProbeStep step)
    {
        var toApi5 = targetLabel.Contains("API5", StringComparison.Ordinal);
        var apiType = (int)(toApi5 ? ksAPITypeEnum.ksAPI5Auto : ksAPITypeEnum.ksAPI7Dual);
        try
        {
            var result = _app.TransferInterface(source, apiType, 0);
            RecordTransfer($"{sourceLabel} → {targetLabel}: TransferInterface(obj, {(toApi5 ? "ksAPI5Auto=1" : "ksAPI7Dual=2")}, 0) → {(result is null ? "null" : Api5.RuntimeName(result))}");
            return result;
        }
        catch (Exception ex)
        {
            RecordTransfer($"{sourceLabel} → {targetLabel}: TransferInterface бросил {HResult.Describe(ex)}");
            step.Observe($"Переход «{sourceLabel} → {targetLabel}» не удался: {HResult.Describe(ex)}");
            return null;
        }
    }

    private string MinimalReproduction(List<string> attempts) =>
        "app5 = Activator.CreateInstance(KOMPAS.Application.5); app5.Visible=false; "
        + "doc=app5.Document3D(); doc.Create(true,true); part=doc.GetPart(-1); "
        + "plate = NewEntity(5)+4×ksLineSeg(±50,±40)+NewEntity(24)+SetSketch+SetSideParam(true,etBlind,10,0,false)+Create; doc.RebuildDocument(); "
        + "app7 = app5.ksGetApplication7() as IApplication; "
        + "doc7 = app5.TransferInterface(doc, ksAPI7Dual=2, 0) as IKompasDocument3D; "
        + "part7 = doc7.TopPart; container = (IModelContainer)part7; "
        + "hole = container.Holes3D.Add(); hole.HoleType=ksHTBase; hole.Diameter=<0.01 и 10>; hole.DepthType=ksDTReachThrough; hole.Update(); part.RebuildModel(); doc.RebuildDocument(); "
        + "V = part.GetMainBody().CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v(); "
        + "последняя ΔV = " + Api5.Num(_lastDelta) + " при ожидании " + Api5.Num(ExpectedThroughHole(10d)) + ". Попытки: " + string.Join(" | ", attempts);

    private static bool WithinBoth(double? left, double? right) =>
        left is double a && right is double b && Math.Abs(a - b) <= Tolerance(b);

    private static uint[] Snapshot()
    {
        var processes = Process.GetProcessesByName("KOMPAS");
        try
        {
            return processes.Select(p => (uint)p.Id).OrderBy(id => id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string Join(uint[] ids) => string.Join(", ", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    private static uint[] WaitUntilNewProcess(uint[] before, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        var last = Snapshot();
        while (clock.ElapsedMilliseconds < timeoutMs && !last.Except(before).Any())
        {
            System.Threading.Thread.Sleep(200);
            last = Snapshot();
        }

        return last.Except(before).ToArray();
    }

    private static int? WindowPid(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        return NativeWindow.GetWindowThreadProcessId(handle, out var pid) == 0 ? null : (int)pid;
    }

    /// <summary>
    /// КОМПАС can still be mid-operation when a rebuild call returns. Draining a slice of real time
    /// between rebuild and measurement is what stops «returned true, nothing changed» from being
    /// read as an answer of the API rather than of the scheduler.
    /// </summary>
    private void Settle(int milliseconds = 600)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            // Pump the COM queue rather than sleeping: on an STA thread a bare sleep is exactly how
            // "the server is still working" turns into a hang. PumpWaitingMessages is declared only
            // in the installed type library (A7.0 measured that the prebuilt wrapper stops at
            // GetSystemVersion), so this also exercises the late-bound path on the way to a number.
            if (_app7 is null)
            {
                System.Threading.Thread.Sleep(50);
                continue;
            }

            try
            {
                Late.Call(_app7, "PumpWaitingMessages");
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    /// <summary>Reads a reference-valued member; a failure is recorded as the failure text, never
    /// as a null that could be mistaken for "the API answered null".</summary>
    private static object? Obj(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return "<" + HResult.Describe(ex) + ">";
        }
    }

    private static bool? Flag(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? Count(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? Real(Func<double> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal static class NativeWindow
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

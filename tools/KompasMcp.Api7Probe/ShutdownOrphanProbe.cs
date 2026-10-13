using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe SO — what happens when a session ends with an UNSAVED document, and which documented
/// route lets the instance go quietly.</summary>
/// <remarks>QUESTIONS. (1) Does <c>KompasObject.Quit()</c> block on a modified document, and what window
/// does it show? (2) Does <c>IKompasDocument.Close(kdDoNotSaveChanges)</c> before <c>Quit()</c> let the
/// instance go? (3) Does <c>IApplication.HideMessage</c> suppress it on its own? (4) Does the sequence
/// rescue-copy + close + quit fit the Host's 20 s window? CONTROL. The probe answers the dialog from a
/// background thread, so "quiet because a human clicked" is told from "never asked"; <c>orphan</c> never
/// answers and reproduces the hang. History: docs/decisions/host.md#shutdown-no-orphans</remarks>
internal sealed class ShutdownOrphanProbe
{
    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly string _scenario;
    private readonly bool _visible;

    private readonly List<int> _pidsBefore = new();
    private readonly Stopwatch _clock = new();

    private KompasObject _app = null!;
    private IApplication? _app7;
    private int _ownPid;

    private EvidenceLog _evidence = null!;
    private bool _quitDone;
    private Watchdog? _watchdog;

    public ShutdownOrphanProbe(ProbeReport report, Options options, string scenario, bool visible)
    {
        _report = report;
        _options = options;
        _scenario = scenario;
        _visible = visible;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "shutdown-orphans.json"),
            Path.Combine(options.ReportDir, "shutdown-orphans.md"));
    }

    public void Run()
    {
        Launch();

        var step = _report.Begin(
            "SO.1",
            "Сценарий завершения: " + _scenario,
            "Остаётся ли живой КОМПАС после завершения сеанса, и каким документированным маршрутом его нет?");

        try
        {
            var work = Path.Combine(_options.WorkDir, "shutdown-" + _scenario);
            Directory.CreateDirectory(work);
            _evidence = new EvidenceLog(Path.Combine(work, "evidence.jsonl"));
            _evidence.Write("scenario", new { scenario = _scenario, visible = _visible, own_pid = _ownPid });

            _watchdog = new Watchdog(this, _evidence);
            _watchdog.Start();

            ExecuteScenario(step, work);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Необработанное исключение: " + ex.Message);
        }
        finally
        {
            // The quit ALWAYS happens: a probe defect in the measurement path must not leave the very
            // orphan this run is about.
            if (!_quitDone)
            {
                try
                {
                    Quit(step);
                }
                catch (Exception ex)
                {
                    step.Errors.Add("Quit из finally: " + ex);
                }
            }

            _watchdog?.Stop();
            _evidence?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("SO.0", "Свой экземпляр КОМПАС-3D v24",
            "Сеанс поднимается сам и не касается чужих процессов?");
        _clock.Restart();

        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        step.Data["pids_before"] = _pidsBefore.ToArray();

        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = _visible;
        _ownPid = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid));

        step.Data["own_pid"] = _ownPid;
        step.Data["visible"] = _visible;

        try
        {
            _app7 = _app.ksGetApplication7() as IApplication;
        }
        catch (Exception ex)
        {
            step.Observe("ksGetApplication7 бросил: " + HResult.Describe(ex));
        }

        step.Data["application7"] = _app7 is not null;
        step.Pass(_ownPid == 0
            ? "свой процесс сразу не виден (PID не доказан)"
            : "свой процесс PID " + _ownPid + ", поднят за " + _clock.ElapsedMilliseconds + " мс");
    }

    // ═══════════════════════════════════════════════════════════════════ scenarios ══

    private void ExecuteScenario(ProbeStep step, string work)
    {
        ksDocument3D? document = null;
        string? clientFile = null;

        switch (_scenario)
        {
            case "orphan":
                clientFile = Path.Combine(work, "client-dirty.m3d");
                document = BuildDirtyPart(clientFile, 40d, 30d, 6d, step);
                break;

            case "orphan-existing":
            case "orphan-visible":
                clientFile = Path.Combine(work, "client-existing.m3d");
                document = BuildDirtyFromExisting(clientFile, step);
                break;

            case "close-nosave":
                clientFile = Path.Combine(work, "client-existing.m3d");
                document = BuildDirtyFromExisting(clientFile, step);
                CloseNoSave(document, step);
                break;

            case "hidemessage-yes":
                clientFile = Path.Combine(work, "client-existing.m3d");
                document = BuildDirtyFromExisting(clientFile, step);
                SetHideMessage(ksHideMessageEnum.ksHideMessageYes, step);
                break;

            case "hidemessage-no":
                clientFile = Path.Combine(work, "client-existing.m3d");
                document = BuildDirtyFromExisting(clientFile, step);
                SetHideMessage(ksHideMessageEnum.ksHideMessageNo, step);
                break;

            case "rescue":
                clientFile = Path.Combine(work, "client-existing.m3d");
                document = BuildDirtyFromExisting(clientFile, step);
                RescueCopy(document, work, step);
                break;

            case "full":
                clientFile = Path.Combine(work, "client-existing.m3d");
                document = BuildDirtyFromExisting(clientFile, step);
                FullSequence(document, clientFile, work, step);
                break;

            case "full-large":
                clientFile = Path.Combine(work, "client-large.m3d");
                document = BuildLargeDirtyPart(clientFile, step);
                FullSequence(document, clientFile, work, step);
                break;

            case "full-huge":
                clientFile = Path.Combine(work, "client-huge.m3d");
                document = BuildHugeDirtyPart(clientFile, step);
                FullSequence(document, clientFile, work, step);
                break;

            default:
                step.Fail("Неизвестный сценарий '" + _scenario + "'.");
                return;
        }

        if (clientFile is not null)
        {
            step.Data["client_file_sha256"] = File.Exists(clientFile) ? FileHashOf(clientFile) : "нет файла";
        }
    }

    /// <summary>Create a part, WRITE it to the client path (so the client file exists), then mutate it —
    /// the document is now dirty while the client file on disk holds the pre-mutation state.</summary>
    private ksDocument3D? BuildDirtyPart(string clientPath, double w, double h, double t, ProbeStep step)
    {
        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, false))
        {
            step.Fail("Document3D().Create(detail=true) не дал true.");
            return null;
        }

        var part = (ksPart)document.GetPart((short)Part_Type.pTop_Part);
        if (Api5.BasePlate(part, w, h, t, step, "so-base") is null)
        {
            step.Fail("Базовая плита не построена.");
            return null;
        }

        if (document.SaveAs(clientPath) != true)
        {
            step.Fail("SaveAs(" + clientPath + ") не дал true — файла клиента нет.");
            return null;
        }

        step.Observe("файл клиента записан: " + clientPath + " (" + new FileInfo(clientPath).Length + " байт)");

        // The mutation that makes the document dirty: a second plate.
        if (Api5.BasePlate(part, w / 2d, h / 2d, t, step, "so-dirty") is null)
        {
            step.Fail("Мутация (вторая плита) не построена — документ не стал изменённым.");
            return null;
        }

        step.Observe("документ изменён после записи файла: несохранённые изменения есть.");
        step.Data["document_dirty"] = true;
        ReportChanged(document, step);
        return document;
    }

    /// <summary>Read the DOCUMENTED <c>IKompasDocument.Changed</c> flag — "the document has been changed
    /// since the last save". It is the product-independent witness that the document really IS modified,
    /// so "Quit() did not ask" cannot be explained away by "nothing was modified".</summary>
    private void ReportChanged(ksDocument3D document, ProbeStep step)
    {
        try
        {
            var doc7 = _app.TransferInterface(document, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument;
            if (doc7 is null)
            {
                step.Observe("IKompasDocument недоступен — флаг Changed не прочитан.");
                return;
            }

            step.Observe("IKompasDocument.Changed = " + doc7.Changed);
            step.Data["kompas_changed"] = doc7.Changed;
        }
        catch (Exception ex)
        {
            step.Observe("Changed бросил: " + HResult.Describe(ex));
        }
    }

    /// <summary>Open a real file from disk and modify it — the case the customer describes in real work,
    /// as opposed to a document this process created and saved itself.</summary>
    private ksDocument3D? BuildDirtyFromExisting(string clientPath, ProbeStep step)
    {
        var source = Path.Combine(_options.ProjectRoot, "scratch", "mcp-smoke", "dep-dpu-saved.m3d");
        if (!File.Exists(source))
        {
            step.Observe("исходного документа '" + source + "' нет — сценарий пропущен.");
            step.Verdict = Verdict.Skipped;
            return null;
        }

        File.Copy(source, clientPath, overwrite: true);
        step.Observe("файл клиента: " + clientPath + " (" + new FileInfo(clientPath).Length + " байт)");

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Open(clientPath))
        {
            step.Fail("Open(" + clientPath + ") не дал true.");
            return null;
        }

        var part = (ksPart)document.GetPart((short)Part_Type.pTop_Part);
        if (Api5.BasePlate(part, 25d, 20d, 4d, step, "so-existing-dirty") is null)
        {
            step.Fail("Мутация открытого документа не построена.");
            return null;
        }

        step.Observe("открытый с диска документ изменён.");
        step.Data["document_dirty"] = true;
        ReportChanged(document, step);
        return document;
    }

    /// <summary>A document built to carry HUNDREDS of features — the "заведомо большой документ" the
    /// task asks the timing question about. Each step adds a sketch and an extrusion, so the feature
    /// count grows by two per iteration; the file size, not the iteration count, is what the rescue copy
    /// pays for.</summary>
    private ksDocument3D? BuildHugeDirtyPart(string clientPath, ProbeStep step)
    {
        const int steps = 150;
        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, false))
        {
            step.Fail("Document3D().Create(detail=true) не дал true.");
            return null;
        }

        var part = (ksPart)document.GetPart((short)Part_Type.pTop_Part);
        var built = 0;
        for (var i = 0; i < steps; i++)
        {
            var size = 30d + (i % 10);
            if (Api5.BasePlate(part, size, size, 3d, step, "so-huge-" + i) is null)
            {
                step.Observe("шаг " + i + " не построен — дальше не идём.");
                break;
            }

            built++;
        }

        if (document.SaveAs(clientPath) != true)
        {
            step.Fail("SaveAs(" + clientPath + ") не дал true.");
            return null;
        }

        var size1 = new FileInfo(clientPath).Length;
        step.Observe("большой документ записан: " + built + " шагов (эскиз+выдавливание каждый), "
            + size1 + " байт");
        step.Data["huge_steps"] = built;
        step.Data["huge_file_bytes"] = size1;

        // Mutate again so the document is dirty relative to the file just written.
        if (Api5.BasePlate(part, 12d, 12d, 3d, step, "so-huge-dirty") is null)
        {
            step.Fail("Мутация большого документа не построена.");
            return null;
        }

        step.Data["document_dirty"] = true;
        ReportChanged(document, step);
        return document;
    }

    /// <summary>Open a real, large, feature-rich document and mutate it — the timing case of the task.</summary>
    private ksDocument3D? BuildLargeDirtyPart(string clientPath, ProbeStep step)
    {
        var source = Path.Combine(_options.ProjectRoot, "scratch", "mcp-smoke", "dep-dpu-saved.m3d");
        if (!File.Exists(source))
        {
            step.Observe("большого документа '" + source + "' нет — сценарий пропущен.");
            step.Verdict = Verdict.Skipped;
            return null;
        }

        File.Copy(source, clientPath, overwrite: true);
        var size = new FileInfo(clientPath).Length;
        step.Observe("исходный документ скопирован: " + clientPath + " (" + size + " байт)");

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Open(clientPath))
        {
            step.Fail("Open(" + clientPath + ") не дал true.");
            return null;
        }

        var part = (ksPart)document.GetPart((short)Part_Type.pTop_Part);
        if (Api5.BasePlate(part, 25d, 20d, 4d, step, "so-large-dirty") is null)
        {
            step.Fail("Мутация большого документа не построена.");
            return null;
        }

        step.Observe("большой документ открыт и изменён.");
        step.Data["document_dirty"] = true;
        step.Data["client_file_bytes"] = size;
        return document;
    }

    /// <summary>(б) Close with the documented "do not save" option.</summary>
    private void CloseNoSave(ksDocument3D? document, ProbeStep step)
    {
        if (document is null)
        {
            return;
        }

        var started = _clock.ElapsedMilliseconds;
        try
        {
            var doc7 = _app.TransferInterface(document, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            if (doc7 is null)
            {
                step.Observe("TransferInterface(документ → IKompasDocument3D) вернул null.");
                return;
            }

            var ok = doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            step.Observe("IKompasDocument3D.Close(kdDoNotSaveChanges) = " + ok
                + " за " + (_clock.ElapsedMilliseconds - started) + " мс");
            step.Data["close_nosave_ok"] = ok;
            step.Data["close_nosave_ms"] = _clock.ElapsedMilliseconds - started;
        }
        catch (Exception ex)
        {
            step.Observe("Close(kdDoNotSaveChanges) бросил: " + HResult.Describe(ex));
            step.Data["close_nosave_exception"] = HResult.Describe(ex);
        }
    }

    /// <summary>(в) Turn on the documented message suppression.</summary>
    private void SetHideMessage(ksHideMessageEnum mode, ProbeStep step)
    {
        if (_app7 is null)
        {
            step.Observe("IApplication недоступен — подавление не включить.");
            return;
        }

        try
        {
            _app7.HideMessage = mode;
            var readBack = _app7.HideMessage;
            step.Observe("IApplication.HideMessage = " + mode + "; прочитано обратно: " + readBack);
            step.Data["hide_message_set"] = mode.ToString();
            step.Data["hide_message_read_back"] = readBack.ToString();
        }
        catch (Exception ex)
        {
            step.Observe("HideMessage = " + mode + " бросил: " + HResult.Describe(ex));
            step.Data["hide_message_exception"] = HResult.Describe(ex);
        }
    }

    /// <summary>(а) Rescue copy: write the CURRENT model state to the service directory, leaving the
    /// client's own file untouched. The documented route is <c>SaveAs</c> to a service path.</summary>
    private string? RescueCopy(ksDocument3D? document, string work, ProbeStep step)
    {
        if (document is null)
        {
            return null;
        }

        var service = Path.Combine(work, "rescue");
        Directory.CreateDirectory(service);
        var target = Path.Combine(
            service,
            "rescue-" + _clock.ElapsedMilliseconds + "-" + DateTime.UtcNow.ToString("HHmmssfff") + ".m3d");

        var started = _clock.ElapsedMilliseconds;
        try
        {
            var ok = document.SaveAs(target);
            var ms = _clock.ElapsedMilliseconds - started;
            step.Observe("спасательная копия SaveAs(" + target + ") = " + ok + " за " + ms + " мс");
            step.Data["rescue_copy_ms"] = ms;
            step.Data["rescue_copy_ok"] = ok;
            if (ok == true && File.Exists(target))
            {
                step.Data["rescue_copy_bytes"] = new FileInfo(target).Length;
                step.Data["rescue_copy_path"] = target;
                return target;
            }

            step.Observe("файла копии на диске нет.");
            return null;
        }
        catch (Exception ex)
        {
            step.Observe("SaveAs(спасательная копия) бросил: " + HResult.Describe(ex));
            step.Data["rescue_copy_exception"] = HResult.Describe(ex);
            return null;
        }
    }

    /// <summary>The whole §3.1 sequence: rescue copy, close without saving, suppress, quit — timed.</summary>
    private void FullSequence(ksDocument3D? document, string clientPath, string work, ProbeStep step)
    {
        if (document is null)
        {
            return;
        }

        var clientBefore = File.Exists(clientPath) ? FileHashOf(clientPath) : null;
        var sequenceStart = _clock.ElapsedMilliseconds;

        RescueCopy(document, work, step);
        CloseNoSave(document, step);
        SetHideMessage(ksHideMessageEnum.ksHideMessageYes, step);

        step.Data["sequence_ms_before_quit"] = _clock.ElapsedMilliseconds - sequenceStart;
        if (clientBefore is not null && File.Exists(clientPath))
        {
            var after = FileHashOf(clientPath);
            step.Data["client_file_unchanged"] = string.Equals(clientBefore, after, StringComparison.OrdinalIgnoreCase);
            step.Observe("файл клиента до/после последовательности совпал: "
                + string.Equals(clientBefore, after, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>(г) <c>Quit()</c> on our own instance, with the watchdog watching from outside.</summary>
    private void Quit(ProbeStep step)
    {
        _quitDone = true;
        step.Data["quit_started_ms"] = _clock.ElapsedMilliseconds;
        _watchdog?.MarkQuit();
        _evidence?.Write("quit_started", new { at_ms = _clock.ElapsedMilliseconds });

        // Windows BEFORE the quit: a dialog is only evidence if the set of windows changes.
        var before = EnumerateWindows(_ownPid);
        _evidence?.Write("window_snapshot", new
        {
            phase = "before_quit",
            alive = IsAlive(_ownPid),
            windows = before.Select(w => new { w.Class, w.Title, w.Visible, hwnd = w.Hwnd.ToInt64() }).ToArray(),
        });
        step.Data["windows_before_quit"] = before.Select(w => w.Class + "|" + w.Title + "|" + w.Visible).ToArray();

        // The dialog КОМПАС raises at shutdown is a window that did NOT exist before the quit; the
        // watchdog targets exactly that window rather than "the first visible one".
        _watchdog?.NotePreQuitWindows(before.Select(w => w.Hwnd.ToInt64()).ToHashSet());

        var quitStart = _clock.ElapsedMilliseconds;
        try
        {
            _app.Quit();
            step.Observe("Quit() вернулся за " + (_clock.ElapsedMilliseconds - quitStart) + " мс.");
            step.Data["quit_returned"] = true;
            step.Data["quit_returned_ms"] = _clock.ElapsedMilliseconds - quitStart;
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + HResult.Describe(ex));
            step.Data["quit_returned"] = false;
            step.Data["quit_exception"] = HResult.Describe(ex);
        }

        _watchdog?.MarkQuitReturned();
        var exited = WaitForExit(_ownPid, TimeSpan.FromSeconds(25));
        step.Data["process_exited_within_25s"] = exited;
        step.Observe(exited
            ? "свой процесс " + _ownPid + " ушёл за " + _clock.ElapsedMilliseconds + " мс от старта."
            : "свой процесс " + _ownPid + " ЖИВ через 25 с после Quit() — осиротел.");
        step.Data["total_ms"] = _clock.ElapsedMilliseconds;

        if (exited)
        {
            step.Pass("сценарий '" + _scenario + "': экземпляр завершился штатно, за "
                + _clock.ElapsedMilliseconds + " мс.");
        }
        else
        {
            step.Fail("сценарий '" + _scenario + "': экземпляр остался жить (PID " + _ownPid + ").");
        }
    }

    // ═══════════════════════════════════════════════════════════════════ watchdog ══

    /// <summary>Answers the modal dialog from OUTSIDE the blocked STA thread, so "the route is quiet"
    /// can be told from "somebody clicked No". It also records every top-level window of the instance,
    /// which is how the dialog is NAMED rather than assumed.</summary>
    private sealed class Watchdog
    {
        private readonly ShutdownOrphanProbe _owner;
        private readonly EvidenceLog _log;
        private readonly System.Threading.Thread _thread;
        private volatile bool _stop;
        private volatile bool _quitIssued;
        private long _quitAtMs;
        private readonly long[] _marks = { 500, 1_500, 3_000, 6_000, 10_000, 16_000 };
        private int _nextMark;
        private HashSet<long> _preQuitHwnds = new();

        public Watchdog(ShutdownOrphanProbe owner, EvidenceLog log)
        {
            _owner = owner;
            _log = log;
            _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "so-watchdog" };
        }

        public void Start() => _thread.Start();

        public void NotePreQuitWindows(HashSet<long> hwnds) => _preQuitHwnds = hwnds;

        public void MarkQuit() => _quitIssued = true;

        public void MarkQuitReturned()
        {
        }

        public void Stop() => _stop = true;

        private void Loop()
        {
            while (!_stop)
            {
                System.Threading.Thread.Sleep(150);
                try
                {
                    Tick();
                }
                catch
                {
                    // The watchdog must never take the run down with it.
                }
            }
        }

        private void Tick()
        {
            var pid = _owner._ownPid;
            if (pid == 0)
            {
                return;
            }

            var alive = IsAlive(pid);
            if (_quitIssued && _quitAtMs == 0)
            {
                _quitAtMs = _owner._clock.ElapsedMilliseconds;
            }

            if (!_quitIssued)
            {
                return;
            }

            var since = _owner._clock.ElapsedMilliseconds - _quitAtMs;
            if (_nextMark < _marks.Length && since >= _marks[_nextMark])
            {
                var windows = EnumerateWindows(pid);
                _log.Write("window_snapshot", new
                {
                    since_quit_ms = since,
                    alive,
                    windows = windows.Select(w => new { w.Class, w.Title, w.Visible, hwnd = w.Hwnd.ToInt64() }).ToArray(),
                });

                // The scenario 'orphan' deliberately never answers, so the hang is measured, not cured.
                if (!_owner._scenario.StartsWith("orphan", StringComparison.Ordinal)
                    && since >= 6_000)
                {
                    // The dialog is the window that APPEARED after the quit; a pre-existing visible
                    // window (the main frame) is not it, and closing it would measure the wrong thing.
                    var dialog = windows.FirstOrDefault(w => w.Visible && !_preQuitHwnds.Contains(w.Hwnd.ToInt64()));
                    if (dialog.Hwnd != IntPtr.Zero)
                    {
                        var clicked = DismissDialog(dialog.Hwnd);
                        _log.Write("dialog_dismiss", new { hwnd = dialog.Hwnd.ToInt64(), title = dialog.Title, clicked });
                    }
                }

                _nextMark++;
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Dispose();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return true;
            }

            System.Threading.Thread.Sleep(300);
        }

        return !IsAlive(pid);
    }

    private static string FileHashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ═══════════════════════════════════════════════════════════════════ windows ══

    internal readonly record struct WindowInfo(IntPtr Hwnd, string Class, string Title, bool Visible);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_CLOSE = 0xF060;
    private const uint WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;
    private const uint BM_CLICK = 0x00F5;

    private static List<WindowInfo> EnumerateWindows(int pid)
    {
        var found = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid)
            {
                found.Add(Describe(hwnd));
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static WindowInfo Describe(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        GetWindowTextW(hwnd, title, title.Capacity);
        var cls = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        return new WindowInfo(hwnd, cls.ToString(), title.ToString(), IsWindowVisible(hwnd));
    }

    /// <summary>Answer a modal dialog the way a user answering "do not save" would: click the button
    /// whose caption says No / Don't save; failing that, close the window.</summary>
    private static string DismissDialog(IntPtr dialog)
    {
        var clicked = new List<string>();
        EnumChildWindows(dialog, (child, _) =>
        {
            var info = Describe(child);
            var caption = info.Title.ToLowerInvariant();
            if (caption.Contains("нет") || caption.Contains("не сохран") || caption.Contains("no")
                || caption.Contains("don't") || caption.Contains("cancel") || caption.Contains("отмена"))
            {
                SendMessageW(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                clicked.Add(info.Title);
            }

            return true;
        }, IntPtr.Zero);

        if (clicked.Count == 0)
        {
            // A WPF dialog renders its buttons inside one HWND, so there is no child button to click;
            // Escape and SC_CLOSE are the generic ways to answer "do not save / cancel".
            PostMessageW(dialog, WM_KEYDOWN, new IntPtr(VK_ESCAPE), IntPtr.Zero);
            SendMessageW(dialog, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
            PostMessageW(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            clicked.Add("ESC+SC_CLOSE+WM_CLOSE");
        }

        return string.Join(", ", clicked);
    }

    /// <summary>Append-only JSONL evidence, flushed on every line: a run that wedges inside
    /// <c>Quit()</c> still leaves its observations on disk for the driver to read.</summary>
    private sealed class EvidenceLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new();

        public EvidenceLog(string path) =>
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };

        public void Write(string kind, object payload)
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(payload)?.AsObject()
                       ?? new System.Text.Json.Nodes.JsonObject();
            node["kind"] = kind;
            node["ts_utc"] = DateTimeOffset.UtcNow.ToString("O");
            lock (_gate)
            {
                _writer.WriteLine(node.ToJsonString());
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer.Dispose();
            }
        }
    }
}

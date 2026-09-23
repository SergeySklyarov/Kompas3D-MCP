using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using KompasMcp.Api5Adapter;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Api5Adapter.Sta;

namespace KompasMcp.P0Probe;

/// <summary>
/// Entry point of the P0 investigation. Runs every step on one STA thread with a message pump,
/// because that is the only configuration the Worker is allowed to use; a step that passed on a
/// pool thread would prove nothing.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = ProbeOptions.Parse(args);

        var report = new ProbeReport
        {
            Title = options.Suite == "p2"
                ? "P2 — отчёт измерения COM-маршрутов (отверстие, правка признака, скругление, плоскости эскиза, измерение цилиндрической грани, целевое тело операции, очистка существующего эскиза, ключи сырого чтения единиц)"
                : "P0 — отчёт технического исследования",
        };
        var sw = Stopwatch.StartNew();
        var reportName = options.Suite == "p2" ? "p2-probe-report" : "p0-probe-report";

        Directory.CreateDirectory(options.WorkDir);
        Console.WriteLine($"{options.Suite.ToUpperInvariant()} probe run {report.RunId} — рабочая папка: {options.WorkDir}");
        Console.WriteLine($"Режим: mode={options.Mode}, pid={(options.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "<auto>")}, keep={options.KeepRunning}");

        // Must happen before any Kompas6API5 type is first touched: the vendor interop is
        // referenced, not copied, so it only resolves from the installation directory.
        if (!KompasInteropResolver.TryInstall(out var interopReason))
        {
            Console.Error.WriteLine("[P0] " + interopReason);
            var blocked = report.Begin("P0.0", "Поиск вендорского interop");
            blocked.Fail(interopReason ?? "Interop не найден.");
            blocked.Data["probed"] = KompasInteropResolver.ProbedDirectories;
            report.Environment["interop_resolved"] = false;
            report.Write(Path.Combine(options.ReportDir, reportName + ".json"), Path.Combine(options.ReportDir, reportName + ".md"));
            return 3;
        }

        Console.WriteLine($"Interop resolution: {KompasInteropResolver.ResolvedDirectory}");
        report.Environment["interop_directory"] = KompasInteropResolver.ResolvedDirectory;
        report.Environment["interop_probed_directories"] = KompasInteropResolver.ProbedDirectories;

        using var sta = new StaExecutor("p0-sta");
        sta.Start();
        ProbeSession.Sta = sta;
        ProbeSession.Report = report;
        ProbeSession.WorkDir = options.WorkDir;

        try
        {
            if (options.Suite == "p2")
            {
                // The P2 suite is self-contained on purpose: it does not re-run the P0 steps, and
                // it does not re-emit docs/compatibility/kompas-api5-metadata.json, so a P2
                // measurement can never rewrite the API5 signature dump. P2.5 (the cylindrical-face
                // measurement) and P2.6 (target body, sketch clearing, unit-reading keys) are
                // appended after P2.1..P2.4 in P2Facts.Run and share this session.
                sta.Run(() => EnvironmentFacts.Collect(report, options, "P2.0"), "env").GetAwaiter().GetResult();

                // Connect is invoked inside the queued lambda and assigns ProbeSession.App itself.
                // Doing `ProbeSession.App = sta.Run(() => P2Facts.Connect(...))` here would make
                // the JIT resolve Interop.Kompas6API5 while compiling Main — before the interop
                // resolver is installed — and the process dies with FileNotFoundException.
                sta.Run(() =>
                {
                    P2Facts.Connect(report, options);
                    P2Facts.Run(report, options);
                    P2Facts.Shutdown(report, options);
                }, "p2").GetAwaiter().GetResult();
            }
            else
            {
                // Environment facts do not need COM, but they are collected first so a later
                // failure still leaves a usable report header.
                sta.Run(() => EnvironmentFacts.Collect(report, options), "env").GetAwaiter().GetResult();
                sta.Run(() => InteropFacts.Collect(report), "interop-metadata").GetAwaiter().GetResult();
                sta.Run(() => ConnectionFacts.Run(report, options), "connection").GetAwaiter().GetResult();
                sta.Run(() => GeometryFacts.Run(report, options), "geometry").GetAwaiter().GetResult();
                sta.Run(() => StepFacts.Run(report, options), "step").GetAwaiter().GetResult();

                // Adapter-level ground truth for the list_bodies discrepancy. Runs before the
                // attach/release steps so this step owns and cleanly closes its own instance.
                sta.Run(() => AdapterFacts.Run(report, options), "adapter-bodies").GetAwaiter().GetResult();

                // Attach is tested while our own instance is still up, and only a visible instance is
                // a candidate for ROT registration — so raise the window first, then probe.
                sta.Run(() =>
                {
                    if (ProbeSession.App is not null)
                    {
                        try
                        {
                            Members.SetProp(typeof(Kompas6API5.KompasObject), ProbeSession.App, "Visible", true);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[P0] Visible=true не установлен: " + ex.GetType().Name);
                        }
                    }

                    ReleaseFacts.Attach(report, options);
                }, "attach").GetAwaiter().GetResult();

                sta.Run(() => ReleaseFacts.Run(report, options), "release").GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            var crash = report.Begin("P0.X", "Необработанное исключение зонда");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
            Console.Error.WriteLine("[P0] UNHANDLED: " + ex);
        }

        sw.Stop();
        report.Environment["probe_total_ms"] = sw.ElapsedMilliseconds;
        report.Environment["sta_statistics"] = FormatStatistics(sta.Statistics());

        var jsonPath = Path.Combine(options.ReportDir, reportName + ".json");
        var markdownPath = Path.Combine(options.ReportDir, reportName + ".md");
        report.Write(jsonPath, markdownPath);

        Console.WriteLine();
        Console.WriteLine($"Отчёт: {markdownPath}");
        Console.WriteLine($"Отчёт (JSON): {jsonPath}");
        foreach (var step in report.Steps)
        {
            Console.WriteLine($"  [{step.Verdict.ToString().ToUpperInvariant(),-7}] {step.Id} {step.Title}");
        }

        var failed = report.Steps.Any(s => s.Verdict == Verdict.Fail);
        return failed ? 2 : 0;
    }

    private static string FormatStatistics(IReadOnlyDictionary<string, long> statistics) =>
        string.Join(", ", statistics.Select(kv => kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Command line of the probe. Deliberately tiny: it is a measurement tool, not a product.</summary>
public sealed class ProbeOptions
{
    public required string ProjectRoot { get; init; }

    public required string WorkDir { get; init; }

    public required string ReportDir { get; init; }

    /// <summary>launch | attach | none</summary>
    public required string Mode { get; init; }

    /// <summary>p0 (the original investigation) | p2 (hole, fillet, feature edit, plane orientation).</summary>
    public string Suite { get; init; } = "p0";

    public int? ProcessId { get; init; }

    /// <summary>Leave documents open and the application running for manual inspection.</summary>
    public bool KeepRunning { get; init; }

    /// <summary>Also run the destructive STEP-import reproduction (default on; it only touches scratch).</summary>
    public bool RunStepImport { get; init; } = true;

    public static ProbeOptions Parse(string[] args)
    {
        var root = FindProjectRoot();
        var mode = "launch";
        var suite = "p0";
        int? pid = null;
        var keep = false;
        var runImport = true;
        string? workOverride = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--suite" when i + 1 < args.Length:
                    suite = args[++i].ToLowerInvariant();
                    break;
                case "--mode" when i + 1 < args.Length:
                    mode = args[++i].ToLowerInvariant();
                    break;
                case "--pid" when i + 1 < args.Length:
                    pid = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "--no-import":
                    runImport = false;
                    break;
                case "--work" when i + 1 < args.Length:
                    workOverride = args[++i];
                    break;
                case "--help":
                    Console.WriteLine("P0Probe [--suite p0|p2] [--mode launch|attach|none] [--pid N] [--work DIR] [--keep] [--no-import]");
                    Environment.Exit(0);
                    break;
            }
        }

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var work = workOverride ?? Path.Combine(root, "scratch", $"{suite}-{runId}");
        var reportDir = Path.Combine(root, "docs", "acceptance", suite);

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(reportDir);

        return new ProbeOptions
        {
            ProjectRoot = root,
            WorkDir = Path.GetFullPath(work),
            ReportDir = Path.GetFullPath(reportDir),
            Mode = mode,
            Suite = suite,
            ProcessId = pid,
            KeepRunning = keep,
            RunStepImport = runImport,
        };
    }

    private static string FindProjectRoot()
    {
        var candidate = AppContext.BaseDirectory;
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate, "global.json")) && Directory.Exists(Path.Combine(candidate, "docs")))
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return Directory.GetCurrentDirectory();
    }
}

/// <summary>Static environment facts, no COM involved (P0.1).</summary>
internal static class EnvironmentFacts
{
    public static void Collect(ProbeReport report, ProbeOptions options, string id = "P0.1")
    {
        var step = report.Begin(
            id,
            "Окружение: ОС, разрядность, пути установки, регистрация COM",
            "Совпадают ли разрядности, что реально зарегистрировано, каким бинарником является сервер?");

        try
        {
            report.Environment["os"] = RuntimeInformation.OSDescription;
            report.Environment["os_version"] = Environment.OSVersion.VersionString;
            report.Environment["process_bitness"] = Environment.Is64BitProcess ? "x64" : "x86";
            report.Environment["process_bitness_64bit_os"] = Environment.Is64BitOperatingSystem;
            report.Environment["runtime"] = RuntimeInformation.FrameworkDescription;
            report.Environment["probe_work_dir"] = options.WorkDir;
            report.Environment["probe_mode"] = options.Mode;

            step.Observe($"ОС: {RuntimeInformation.OSDescription}");
            step.Observe($"Процесс зонда: {(Environment.Is64BitProcess ? "x64" : "x86")}, runtime {RuntimeInformation.FrameworkDescription}");

            if (!Environment.Is64BitProcess)
            {
                step.Fail("Зонд идёт в x86 — COM-сервер v24 x64 к нему не подключится.");
                return;
            }

            var progIds = new Dictionary<string, string?>(StringComparer.Ordinal);
            report.Environment["progids"] = progIds;
            foreach (var progId in new[] { "KOMPAS.Application", "KOMPAS.Application.5", "KOMPAS.Application.7", "KOMPAS-3D.Application" })
            {
                var type = Type.GetTypeFromProgID(progId, throwOnError: false);
                var clsid = ProgIdToClsid(progId);
                progIds[progId] = clsid;
                step.Observe($"ProgID '{progId}': CLR type={(type is null ? "<null>" : "найдена")}, CLSID={clsid ?? "<нет записи>"}");
            }

            var serverPath = ResolveLocalServer("KOMPAS.Application.5") ?? ResolveLocalServer("KOMPAS.Application.7");
            report.Environment["local_server_path"] = serverPath;
            step.Data["local_server_path"] = serverPath;

            if (serverPath is not null && File.Exists(serverPath))
            {
                var bitness = PeekPeMachine(serverPath);
                report.Environment["local_server_pe_machine"] = bitness;
                step.Observe($"LocalServer: {serverPath} (PE machine {bitness})");
                if (bitness == "0x8664" && Environment.Is64BitProcess)
                {
                    step.Pass("Разрядности сервера и процесса совпадают (x64↔x64).");
                }
                else
                {
                    step.Fail($"Разрядности не совпадают: сервер {bitness}, процесс {(Environment.Is64BitProcess ? "x64" : "x86")}.");
                }
            }
            else
            {
                step.Fail("LocalServer32 не зарегистрирован или файл не найден — КОМПАС недоступен для COM.");
            }

            var processes = Process.GetProcessesByName("KOMPAS");
            var withWindow = processes.Count(p => p.MainWindowHandle != IntPtr.Zero);
            report.Environment["kompas_processes_running"] = processes.Length;
            report.Environment["kompas_processes_with_window"] = withWindow;
            step.Data["kompas_processes_running"] = processes.Length;
            step.Data["kompas_pids"] = processes.Select(p => p.Id).ToArray();
            step.Observe($"KOMPAS.exe процессов сейчас: {processes.Length} (с окном: {withWindow}).");
            if (processes.Length > 1)
            {
                step.Observe("Несколько экземпляров → attach обязан требовать явный выбор (AMBIGUOUS_APPLICATION).");
            }

            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.Message);
            step.Fail("Сбор фактов окружения не удался: " + ex.GetType().Name);
        }
    }

    private static string? ProgIdToClsid(string progId)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.ClassesRoot;
            using var key = root.OpenSubKey(progId + "\\CLSID");
            return key?.GetValue(null) as string;
        }
        catch (Exception ex)
        {
            return "<ошибка чтения реестра: " + ex.GetType().Name + ">";
        }
    }

    private static string? ResolveLocalServer(string progId)
    {
        var clsid = ProgIdToClsid(progId);
        if (clsid is null)
        {
            return null;
        }

        try
        {
            using var root = Microsoft.Win32.Registry.ClassesRoot;
            using var key = root.OpenSubKey("CLSID\\" + clsid + "\\LocalServer32");
            var value = key?.GetValue(null) as string;
            if (value is null)
            {
                return null;
            }

            value = value.Trim();
            if (value.StartsWith('"'))
            {
                var end = value.IndexOf('"', 1);
                return end > 1 ? value[1..end] : value.Trim('"');
            }

            // Unquoted form may carry arguments after the path.
            var space = value.IndexOf(' ');
            return space > 0 ? value[..space] : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Machine word from the PE header: 0x8664 = amd64, 0x14c = i386.</summary>
    private static string PeekPeMachine(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[64];
        fs.ReadExactly(head);
        var peOffset = MemoryMarshal.Read<int>(head[0x3C..0x40]);
        fs.Seek(peOffset + 4, SeekOrigin.Begin);
        Span<byte> machine = stackalloc byte[2];
        fs.ReadExactly(machine);
        return "0x" + MemoryMarshal.Read<ushort>(machine).ToString("X4", CultureInfo.InvariantCulture);
    }
}

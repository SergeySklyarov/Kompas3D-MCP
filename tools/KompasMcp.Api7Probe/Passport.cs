using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KompasMcp.Api7Probe;

/// <summary>
/// A7.0 — the environment passport ADR-003 §4 demands before any COM result is trusted: which
/// КОМПАС actually runs, which type libraries and interop assemblies describe API7, and which of
/// the disagreeing sources the probe believes.
/// </summary>
/// <remarks>
/// The comparison the passport exists to make, done three ways:
/// <list type="bullet">
/// <item>the installed <c>Bin\kAPI7.tlb</c> — the application's own declaration, read through
/// <c>LoadTypeLibEx</c> with <c>REGKIND_NONE</c>, no wrapper involved (<see cref="TlbScan"/>);</item>
/// <item>the prebuilt <c>Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll</c>, reflected — this is
/// the wrapper the probe compiles against;</item>
/// <item>the <em>live</em> object, asked through its own <c>IDispatch</c> at step A7.4, which is the
/// only answer that cannot be an artefact of a file on disk.</item>
/// </list>
/// Where the wrapper says "no" and the type library says "yes", the gap belongs to the wrapper, and
/// ADR-003 §4 forbids reading it as a missing product feature.
/// </remarks>
internal static class Passport
{
    /// <summary>
    /// Members the online SDK v24 lists on <c>IModelContainer</c> and which ADR-003 §4 reports the
    /// prebuilt wrapper as lacking. Asked of every source independently.
    /// </summary>
    private static readonly string[] DisputedMembers = { "Holes3D", "ElementaryBodies", "PipeElements" };

    public static void Collect(ProbeReport report, Options options, string id = "A7.0")
    {
        var step = report.Begin(
            id,
            "Паспорт среды: какой КОМПАС исполняется и каким API7 он описан",
            "Какое приложение реально запускается, какими TLB и interop описан API7, и чему из расходящихся источников верит зонд?");

        try
        {
            report.Environment["os"] = RuntimeInformation.OSDescription;
            report.Environment["os_version"] = Environment.OSVersion.VersionString;
            report.Environment["process_bitness"] = Environment.Is64BitProcess ? "x64" : "x86";
            report.Environment["runtime"] = RuntimeInformation.FrameworkDescription;
            report.Environment["process_path"] = Environment.ProcessPath;
            report.Environment["probe_work_dir"] = options.WorkDir;
            report.Environment["interop_directory"] = InteropResolver.ResolvedDirectory;
            report.Environment["interop_probed_directories"] = InteropResolver.ProbedDirectories;
            report.Environment["interop_loaded_from"] = InteropResolver.LoadedFrom;
            step.Observe($"Процесс зонда: {(Environment.Is64BitProcess ? "x64" : "x86")}, {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}.");
            if (!Environment.Is64BitProcess)
            {
                step.Fail("Зонд идёт в x86 — x64-сервер КОМПАС v24 к нему не подключится.");
                return;
            }

            step.Observe("Сборка, которая исполняется: " + DescribeOwnAssembly());
            step.Observe("Путём проб на interop: " + string.Join(" | ", InteropResolver.ProbedDirectories));
            step.Observe("Разрешено в: " + InteropResolver.ResolvedDirectory);

            // ── ProgIDs, CLSIDs, the server binary ────────────────────────────────────────
            var progIds = new Dictionary<string, object?>(StringComparer.Ordinal);
            report.Environment["progids"] = progIds;
            foreach (var progId in new[] { "KOMPAS.Application", "KOMPAS.Application.5", "KOMPAS.Application.7", "KOMPAS-3D.Application" })
            {
                var clsid = InteropResolver.ProgIdToClsid(progId);
                var server = InteropResolver.LocalServerPath(progId);
                progIds[progId] = new { clsid, local_server = server };
                step.Observe($"ProgID «{progId}»: CLSID={clsid ?? "<нет записи>"}, LocalServer32={server ?? "<нет>"}.");
            }

            var exe = InteropResolver.LocalServerPath("KOMPAS.Application.5") ?? InteropResolver.LocalServerPath("KOMPAS.Application.7");
            report.Environment["local_server_path"] = exe;
            if (exe is null || !File.Exists(exe))
            {
                step.Fail("LocalServer32 не зарегистрирован или файл не найден — КОМПАС недоступен для COM.");
                return;
            }

            var machine = InteropResolver.PeekPeMachine(exe);
            var exeInfo = FileVersionInfo.GetVersionInfo(exe);
            var bin = Path.GetDirectoryName(exe)!;
            var installRoot = Path.GetDirectoryName(bin);
            report.Environment["local_server_pe_machine"] = machine;
            report.Environment["kompas_file_version"] = exeInfo.FileVersion;
            report.Environment["kompas_product_version"] = exeInfo.ProductVersion;
            report.Environment["kompas_sha256"] = InteropResolver.Sha256(exe);
            report.Environment["install_root"] = installRoot;
            step.Observe($"Сервер: {exe}; PE {machine}; FileVersion {exeInfo.FileVersion}; ProductVersion {exeInfo.ProductVersion}.");
            step.Observe($"sha256(KOMPAS.exe) = {report.Environment["kompas_sha256"]}");
            if (machine != "0x8664" || installRoot is null)
            {
                step.Fail($"Сервер не x64 (PE {machine}) или корень установки не определён — измерение теряет смысл.");
                return;
            }

            // ── The application's own type libraries ──────────────────────────────────────
            var api7Tlb = TlbScan.Read(Path.Combine(bin, "kAPI7.tlb"));
            var api5Tlb = TlbScan.Read(Path.Combine(bin, "kAPI5.tlb"));
            var tlbInfo = new List<Dictionary<string, object?>>();
            report.Environment["type_libraries"] = tlbInfo;
            foreach (var tlb in new[] { api7Tlb, api5Tlb })
            {
                var fvi = FileVersionInfo.GetVersionInfo(tlb.Path);
                tlbInfo.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = tlb.Path,
                    ["sha256"] = tlb.Sha256,
                    ["size_bytes"] = tlb.SizeBytes,
                    ["written_utc"] = tlb.WrittenUtc.ToString("O", CultureInfo.InvariantCulture),
                    ["file_version"] = fvi.FileVersion,
                    ["product_version"] = fvi.ProductVersion,
                    ["type_info_count"] = tlb.TypeInfoCount,
                    ["interface_count"] = tlb.InterfaceCount,
                    ["failure"] = tlb.Failure,
                });
                step.Observe(
                    $"TLB {Path.GetFileName(tlb.Path)}: записей type info {tlb.TypeInfoCount}, из них интерфейсов/диспинтерфейсов {tlb.InterfaceCount}; "
                    + $"изменён {tlb.WrittenUtc:O}; {tlb.SizeBytes} байт; sha256={Short(tlb.Sha256)}; версия файла {fvi.FileVersion ?? "<нет>"}.");
                if (tlb.Failure is not null)
                {
                    step.Errors.Add(Path.GetFileName(tlb.Path) + ": " + tlb.Failure);
                }
            }

            var holeEntries = api7Tlb.Entries.Where(e => e.Name.Contains("Hole", StringComparison.OrdinalIgnoreCase)).ToList();
            step.Data["tlb_declares_imodelcontainer"] = api7Tlb.Declares("IModelContainer") ? api7Tlb.KindOf("IModelContainer") : "нет";
            step.Data["tlb_declares_ihole3d"] = api7Tlb.Declares("IHole3D") ? api7Tlb.KindOf("IHole3D") : "нет";
            step.Data["tlb_declares_iholes3d"] = api7Tlb.Declares("IHoles3D") ? api7Tlb.KindOf("IHoles3D") : "нет";
            step.Data["tlb_hole_entries"] = holeEntries.Select(e => e.Name + " (" + e.Kind + ")").ToArray();
            step.Observe($"kAPI7.tlb объявляет: IModelContainer={step.Data["tlb_declares_imodelcontainer"]}, IHole3D={step.Data["tlb_declares_ihole3d"]}, IHoles3D={step.Data["tlb_declares_iholes3d"]}; записей с «Hole» — {holeEntries.Count}.");

            // The wrapper's own IID, asked back of the installed library: which type owns it, and
            // what does the library say that type declares. Nothing in these answers comes from the
            // wrapper except the question itself.
            var tlbPath = Path.Combine(bin, "kAPI7.tlb");
            var containerIid = typeof(KompasAPI7.IModelContainer).GUID;
            var containerOwner = TlbScan.OwnerOfIid(tlbPath, containerIid);
            var tlbMembers = TlbScan.MembersOf(tlbPath, containerIid, DisputedMembers);
            step.Data["tlb_owner_of_wrapper_iid_imodelcontainer"] = containerOwner;
            step.Data["tlb_iodelcontainer_members"] = tlbMembers;
            step.Observe($"IID обёртки KompasAPI7.IModelContainer = {containerIid:B}; в kAPI7.tlb им владеет «{containerOwner}».");
            foreach (var pair in tlbMembers)
            {
                step.Observe($"  kAPI7.tlb, {containerOwner}.{pair.Key}: {pair.Value}");
            }

            var holeIid = typeof(KompasAPI7.IHole3D).GUID;
            step.Data["tlb_owner_of_wrapper_iid_ihole3d"] = TlbScan.OwnerOfIid(tlbPath, holeIid);
            step.Observe($"IID обёртки KompasAPI7.IHole3D = {holeIid:B}; в kAPI7.tlb им владеет «{step.Data["tlb_owner_of_wrapper_iid_ihole3d"]}».");


            // ── The prebuilt wrapper the probe compiles against, reflected ────────────────
            // Loaded by path on purpose: the passport must be able to make this statement without
            // depending on a COM call having already forced the type to load.
            var vendor = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Interop.KompasAPI7");
            var vendorPath = InteropResolver.ResolvedDirectory is null
                ? null
                : Path.Combine(InteropResolver.ResolvedDirectory, "Interop.KompasAPI7.dll");
            if (vendor is null && vendorPath is not null && File.Exists(vendorPath))
            {
                try
                {
                    vendor = Assembly.LoadFrom(vendorPath);
                }
                catch (Exception ex)
                {
                    step.Observe("Обёртку не удалось загрузить для сверки: " + HResult.Describe(ex));
                }
            }

            if (vendor is null)
            {
                step.Observe("Interop.KompasAPI7 не загружается по найденному пути — отражательная сверка обёртки не выполнена.");
            }
            else
            {
                var vendorContainer = vendor.GetType("KompasAPI7.IModelContainer");
                var declared = vendorContainer?.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(m => m.Name)
                    .ToArray() ?? Array.Empty<string>();
                foreach (var name in DisputedMembers)
                {
                    var hit = declared.Any(n => n == name || n == "get_" + name);
                    step.Data["vendor_wrapper_iodelcontainer." + name] = hit;
                    step.Observe($"  вендорский Interop.KompasAPI7, IModelContainer.{name}: {(hit ? "объявлен" : "НЕ объявлен")}");
                }

                step.Observe($"Вендорский Interop.KompasAPI7: {vendor.FullName}; IModelContainer.GUID={vendorContainer?.GUID:N}; объявленных членов {declared.Length}.");
                var table = DisputedMembers.Select(m => new
                {
                    member = m,
                    in_vendor_wrapper = step.Data["vendor_wrapper_iodelcontainer." + m] is true,
                    in_installed_tlb = tlbMembers.TryGetValue(m, out var hit) && hit.StartsWith("объявлен", StringComparison.Ordinal),
                }).ToArray();
                step.Data["disputed_member_table"] = table;
                step.Observe(
                    "Расхождение измерено, а не предположено: "
                    + string.Join("; ", table.Select(t => $"{t.member}: в обёртке {(t.in_vendor_wrapper ? "есть" : "НЕТ")}, в kAPI7.tlb {(t.in_installed_tlb ? "объявлен" : "не объявлен")}")) + ".");
            }

            step.Data["interop_assemblies"] = InteropResolver.DescribeAssemblies();
            foreach (var assembly in InteropResolver.DescribeAssemblies())
            {
                step.Observe($"interop {assembly["full_name"]}: runtime={assembly["image_runtime"]}, pkt={assembly["public_key_token"]}, file_version={assembly["file_version"]}, types={assembly["types"]}, sha256={Short(assembly["sha256"] as string)}.");
            }

            var processes = Process.GetProcessesByName("KOMPAS");
            report.Environment["kompas_processes_before_launch"] = processes.Length;
            report.Environment["kompas_pids_before_launch"] = processes.Select(p => p.Id).ToArray();
            step.Observe($"Процессов KOMPAS.exe до запуска зонда: {processes.Length}.");
            foreach (var process in processes)
            {
                process.Dispose();
            }

            step.Observe(
                "Какую библиотеку зонд считает авторитетной: Bin\\kAPI7.tlb установленного приложения. "
                + "Prebuilt-обёртка из Libs\\PolynomLib\\Bin\\Client — снимок API более ранней давности (её файл раньше файла TLB), "
                + "и отсутствие в ней ElementaryBodies/PipeElements не читается как отсутствие функции в продукте (ADR-003 §4). "
                + "Окончательное слово — за живым объектом: его запрос к IDispatch выполняется шагом A7.4.");

            if (options.PassportOnly)
            {
                step.Observe("Режим --passport: COM-вызовы не выполнялись; живой объект по спорным именам не спрашивали.");
            }

            var passportPath = Path.Combine(options.ReportDir, "env-passport.json");
            File.WriteAllText(passportPath, System.Text.Json.JsonSerializer.Serialize(report.Environment, ProbeReport.JsonOpts), new UTF8Encoding(false));
            step.Artifacts.Add(passportPath);

            step.Pass("Паспорт собран: приложение, разрядность, ProgID/CLSID, TLB и interop с хешами; расхождение обёртки и библиотеки типов измерено напрямую.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Паспорт среды не собран: " + HResult.Describe(ex));
        }

        static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "<нет>" : hash[..Math.Min(16, hash.Length)] + "…";
    }

    /// <summary>
    /// Which binary actually runs. Recorded because twice in this repository a "mysterious COM
    /// failure" was a stale build (docs/STATUS.md), and a leftover AnyCPU output is silently a
    /// different program from the one under review.
    /// </summary>
    private static string DescribeOwnAssembly()
    {
        var assembly = typeof(Passport).Assembly;
        var path = assembly.Location;
        try
        {
            return assembly.GetName().Name + " " + assembly.GetName().Version
                   + " · " + path
                   + " · изменён " + File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture)
                   + " · sha256=" + InteropResolver.Sha256(path);
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }
}

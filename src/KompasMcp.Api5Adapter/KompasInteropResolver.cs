using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Win32;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Loads the vendor КОМПАС interop assemblies from the user's installation at run time.
/// </summary>
/// <remarks>
/// Why this exists (a P0 finding, not a hypothetical): the interop references are declared with
/// <c>Private=false</c>, so nothing from the ASCON installation is copied into our build output or
/// our package — the delivery does not redistribute licensed binaries. The consequence is that a
/// plain reference is enough to *compile* but not to *run*: the first use of a
/// <c>Kompas6API5</c> type throws <see cref="FileNotFoundException"/> unless the assembly is
/// resolved from the install directory. This class is that resolution step, and it must run
/// before any interop type is touched.
///
/// Search order is explicit and reported, because "we silently found some copy of the API" is
/// exactly the kind of ambiguity that produces a version mismatch nobody can diagnose later.
/// </remarks>
public static class KompasInteropResolver
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>Directory the interop was loaded from, once resolution has happened.</summary>
    public static string? ResolvedDirectory { get; private set; }

    public static IReadOnlyList<string> ProbedDirectories { get; private set; } = Array.Empty<string>();

    public static IReadOnlyList<string> LoadedAssemblies { get; private set; } = Array.Empty<string>();

    /// <summary>Assembly names we are willing to satisfy from the КОМПАС installation.</summary>
    private static readonly string[] OwnedPrefixes =
    {
        "Interop.Kompas",
        "Kompas6API5",
        "KompasAPI7",
        "Kompas6Constants",
        "Kompas6Constants3D",
        "stdole",
        "KAPITypes",
    };

    /// <summary>
    /// Install the resolver. Returns false when no candidate directory contains the API5 interop,
    /// in which case the caller must report KOMPAS_NOT_INSTALLED rather than continue.
    /// </summary>
    public static bool TryInstall(out string? failureReason)
    {
        lock (Gate)
        {
            if (_registered)
            {
                failureReason = null;
                return ResolvedDirectory is not null;
            }

            var candidates = CandidateDirectories();
            ProbedDirectories = candidates;

            foreach (var directory in candidates)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(directory, "Interop.Kompas6API5.dll")))
                {
                    continue;
                }

                ResolvedDirectory = Path.GetFullPath(directory);
                AssemblyLoadContext.Default.Resolving += OnResolving;
                _registered = true;
                failureReason = null;
                return true;
            }

            failureReason =
                "Interop.Kompas6API5.dll не найден ни в одном из проверенных каталогов: " +
                string.Join("; ", candidates) +
                ". Задайте KOMPAS_MCP_INTEROP_DIR или -p:KompasRoot=<путь установки>.";
            return false;
        }
    }

    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        var directory = ResolvedDirectory;
        if (directory is null || name.Name is null)
        {
            return null;
        }

        if (!OwnedPrefixes.Any(p => name.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var path = Path.Combine(directory, name.Name + ".dll");
        if (!File.Exists(path))
        {
            return null;
        }

        var loaded = context.LoadFromAssemblyPath(path);
        lock (Gate)
        {
            var list = LoadedAssemblies.ToList();
            if (!list.Contains(name.Name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(name.Name!);
                LoadedAssemblies = list;
            }
        }

        return loaded;
    }

    /// <summary>
    /// Candidate interop directories, most explicit first: environment override, then the
    /// installation located through COM registration, then the historically observed path.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories()
    {
        var list = new List<string>();

        void Add(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && !list.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(directory);
            }
        }

        Add(Environment.GetEnvironmentVariable("KOMPAS_MCP_INTEROP_DIR"));

        foreach (var progId in new[] { "KOMPAS.Application.5", "KOMPAS.Application.7" })
        {
            var executable = LocalServerPath(progId);
            if (executable is null)
            {
                continue;
            }

            var binDir = Path.GetDirectoryName(executable);
            var installRoot = binDir is null ? null : Path.GetDirectoryName(binDir);
            if (installRoot is null)
            {
                continue;
            }

            // Where the vendor puts the managed wrappers differs by module; both observed
            // locations are probed rather than assumed.
            Add(Path.Combine(installRoot, "Libs", "PolynomLib", "Bin", "Client"));
            Add(Path.Combine(installRoot, "Libs", "Interop"));
            Add(binDir);
        }

        Add(@"D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client");
        return list;
    }

    /// <summary>
    /// CLSID a ProgID resolves to in HKCR, or null. Needed to recognise a running instance in the
    /// ROT: КОМПАС registers there as <c>!{CLSID}</c>, not under a readable name, so matching by
    /// "kompas" in the display name silently finds nothing and a working attach looks impossible.
    /// </summary>
    public static string? ProgIdToClsid(string progId)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            using var clsidKey = root.OpenSubKey(progId + "\\CLSID");
            return clsidKey?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Executable registered for a ProgID, or null when it is not registered.</summary>
    public static string? LocalServerPath(string progId)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            using var clsidKey = root.OpenSubKey(progId + "\\CLSID");
            var clsid = clsidKey?.GetValue(null) as string;
            if (clsid is null)
            {
                return null;
            }

            using var server = root.OpenSubKey("CLSID\\" + clsid + "\\LocalServer32");
            var raw = server?.GetValue(null) as string;
            if (raw is null)
            {
                return null;
            }

            raw = raw.Trim();
            if (raw.StartsWith('"'))
            {
                var end = raw.IndexOf('"', 1);
                return end > 1 ? raw[1..end] : raw.Trim('"');
            }

            var space = raw.IndexOf(' ');
            return space > 0 ? raw[..space] : raw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException)
        {
            // Registry reads can fail on a locked-down machine. "Not registered" and "not
            // readable" are reported the same way here, and the probe distinguishes them.
            return null;
        }
    }

    /// <summary>
    /// Bitness self-check, run before any COM call: a 32-bit Worker against a 64-bit КОМПАС
    /// produces failures with no useful message, so it is diagnosed here instead (spec 1.3).
    /// </summary>
    public static string DescribeProcessBitness() => Environment.Is64BitProcess ? "x64" : "x86";

    public static string DescribeServerBitness(string executable)
    {
        try
        {
            using var stream = File.OpenRead(executable);
            Span<byte> head = stackalloc byte[64];
            stream.ReadExactly(head);
            var peOffset = MemoryMarshal.Read<int>(head[0x3C..0x40]);
            stream.Seek(peOffset + 4, SeekOrigin.Begin);
            Span<byte> machine = stackalloc byte[2];
            stream.ReadExactly(machine);
            return MemoryMarshal.Read<ushort>(machine) switch
            {
                0x8664 => "x64",
                0x14c => "x86",
                var other => "0x" + other.ToString("X4"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unknown (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>Processes of a given executable name, for launch-PID attribution.</summary>
    public static int[] SnapshotProcessIds(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(p => p.Id).OrderBy(id => id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

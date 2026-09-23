using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Win32;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Loads the vendor КОМПАС interop assemblies from the installation at run time.
/// </summary>
/// <remarks>
/// This is the resolution LOGIC of <c>src/KompasMcp.Api5Adapter/KompasInteropResolver.cs</c>,
/// copied rather than referenced: ADR-003 §1 keeps the probe out of the shipping graph, so it must
/// not take a project reference into <c>src/</c>. The behaviour is intentionally identical —
/// env override, then the directory COM registration points at, then the historically observed
/// path — because a probe that resolves a DIFFERENT copy of the API than the product does would be
/// measuring the wrong binary (that exact class of mistake is what docs/STATUS.md warns about).
///
/// <c>Private=false</c> in the csproj means the interop is enough to COMPILE but not to RUN: the
/// first touch of a <c>Kompas6API5</c>/<c>KompasAPI7</c> type resolves the assembly here.
/// Unlike the shipping resolver this one also owns the API7 assembly name, and it reports which
/// file each assembly actually came from — the probe has to be able to say, in the environment
/// passport, which library it trusted.
/// </remarks>
internal static class InteropResolver
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static string? ResolvedDirectory { get; private set; }

    public static IReadOnlyList<string> ProbedDirectories { get; private set; } = Array.Empty<string>();

    /// <summary>assembly simple name → absolute path it was actually loaded from.</summary>
    public static IReadOnlyDictionary<string, string> LoadedFrom { get; private set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assembly names the resolver is allowed to satisfy from the КОМПАС installation.</summary>
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

                if (!File.Exists(Path.Combine(directory, "Interop.Kompas6API5.dll"))
                    || !File.Exists(Path.Combine(directory, "Interop.KompasAPI7.dll")))
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
                "Interop.Kompas6API5.dll / Interop.KompasAPI7.dll не найдены вместе ни в одном из каталогов: "
                + string.Join("; ", candidates)
                + ". Задайте KOMPAS_MCP_INTEROP_DIR или -p:KompasRoot=<путь установки>.";
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
            var map = new Dictionary<string, string>(LoadedFrom, StringComparer.OrdinalIgnoreCase);
            map[name.Name] = path;
            LoadedFrom = map;
        }

        return loaded;
    }

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
            var binDir = executable is null ? null : Path.GetDirectoryName(executable);
            var installRoot = binDir is null ? null : Path.GetDirectoryName(binDir);
            if (installRoot is not null)
            {
                Add(Path.Combine(installRoot, "Libs", "PolynomLib", "Bin", "Client"));
                Add(Path.Combine(installRoot, "Libs", "Interop"));
                Add(binDir);
            }
        }

        Add(@"D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client");
        return list;
    }

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

    public static string? LocalServerPath(string progId)
    {
        var clsid = ProgIdToClsid(progId);
        if (clsid is null)
        {
            return null;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
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
            return null;
        }
    }

    /// <summary>PE machine word: 0x8664 = amd64. Bitness mismatch is reported by COM with a code
    /// that never mentions bitness, so the probe checks it itself (ADR-001 §1).</summary>
    public static string PeekPeMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[64];
            stream.ReadExactly(head);
            var peOffset = System.Runtime.InteropServices.MemoryMarshal.Read<int>(head[0x3C..0x40]);
            stream.Seek(peOffset + 4, SeekOrigin.Begin);
            Span<byte> machine = stackalloc byte[2];
            stream.ReadExactly(machine);
            return "0x" + System.Runtime.InteropServices.MemoryMarshal.Read<ushort>(machine).ToString("X4");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unknown(" + ex.GetType().Name + ")";
        }
    }

    /// <summary>Identity + hash of every interop assembly the probe compiled against.</summary>
    public static List<Dictionary<string, object?>> DescribeAssemblies()
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => (a.FullName ?? string.Empty).Contains("Kompas", StringComparison.OrdinalIgnoreCase)
                                 || (a.FullName ?? string.Empty).Contains("stdole", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(a => a.FullName, StringComparer.Ordinal))
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["full_name"] = assembly.FullName,
                ["image_runtime"] = assembly.ImageRuntimeVersion,
                ["location"] = assembly.Location,
            };
            var path = assembly.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                entry["sha256"] = Sha256(path);
                try
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    entry["file_version"] = info.FileVersion;
                    entry["product_version"] = info.ProductVersion;
                }
                catch (Exception ex)
                {
                    entry["file_version"] = "<" + ex.GetType().Name + ">";
                }
            }

            var key = assembly.GetName().GetPublicKeyToken();
            entry["public_key_token"] = key is { Length: > 0 } ? Convert.ToHexString(key).ToLowerInvariant() : null;
            entry["strong_named"] = key is { Length: > 0 };
            entry["types"] = SafeTypeCount(assembly);
            list.Add(entry);
        }

        return list;
    }

    private static object SafeTypeCount(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Length;
        }
        catch (ReflectionTypeLoadException ex)
        {
            return new Dictionary<string, object?>
            {
                ["partial"] = ex.Types.Count(t => t is not null),
                ["loader_errors"] = ex.LoaderExceptions.Take(3).Select(e => e?.Message).ToArray(),
            };
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}

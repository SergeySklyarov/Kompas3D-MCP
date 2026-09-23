using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Kompas6API5;
using KompasMcp.Api5Adapter.Sta;

namespace KompasMcp.P0Probe;

/// <summary>
/// The one КОМПАС instance the probe talks to. Holding it in a single place is the point:
/// the historical scripts called <c>Activator.CreateInstance</c> per run and leaked processes
/// (spec 4.3), and the probe must demonstrate the opposite behaviour.
/// </summary>
internal static class ProbeSession
{
    public static KompasObject? App;

    public static StaExecutor? Sta;

    /// <summary>PID we believe the <see cref="App"/> belongs to, plus how that was established.</summary>
    public static int? ProcessId;

    public static string PidEvidence = "none";

    /// <summary>launched | attached | unknown</summary>
    public static string Ownership = "unknown";

    public static string ProgIdUsed = "KOMPAS.Application.5";

    /// <summary>Scratch directory the probe may write into. Never a user model directory.</summary>
    public static string WorkDir { get; set; } = string.Empty;

    public static ProbeReport? Report { get; set; }

    public static void Log(string message) => Console.WriteLine("  " + message);
}

/// <summary>
/// Loads the vendor interop and records the *authoritative* API surface to
/// <c>docs/compatibility/kompas-api5-metadata.json</c> (P0.2).
/// </summary>
/// <remarks>
/// Why dump metadata instead of recalling it: every signature guess in an adapter is a bug that
/// only shows up at 3 a.m. against a real model. The interop assembly is the vendor's own
/// statement about the API of the version actually installed, so it is the source of truth here.
/// Reflection (not <c>dynamic</c>) is used throughout the probe because КОМПАС interfaces are
/// custom vtable interfaces: late binding through IDispatch is not guaranteed to exist.
/// </remarks>
internal static class InteropFacts
{
    public static void Collect(ProbeReport report)
    {
        var step = report.Begin(
            "P0.2",
            "Загрузка interop и снятие метаданных API5/API7",
            "Может ли x64 .NET-процесс загрузить вендорский interop, и каковы фактические сигнатуры?");

        try
        {
            var api5 = typeof(KompasObject).Assembly;
            step.Observe($"Kompas6API5 загружен: {api5.GetName().Name} v{api5.GetName().Version}, Location={api5.Location}");
            step.Data["api5_assembly"] = api5.FullName;

            var constants = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Kompas6Constants");
            step.Data["api5_public_types"] = api5.GetExportedTypes().Length;
            step.Data["constants_loaded"] = constants is not null;

            var metadataDir = Path.Combine(ProjectRoot(), "docs", "compatibility");
            Directory.CreateDirectory(metadataDir);
            var jsonPath = Path.Combine(metadataDir, "kompas-api5-metadata.json");

            var dump = DumpAssembly(api5);
            var tlbConstants = api5.GetExportedTypes().Count(t => t.IsEnum);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
            step.Artifacts.Add(jsonPath);
            step.Observe($"Выгружено: типов={dump["type_count"]}, из них перечислений={tlbConstants}.");

            // The members we specifically want to know about: anything that can identify the
            // process, and the entry points the vertical scenario needs.
            foreach (var typeName in new[] { "KompasObject", "ksDocument3D", "ksPart", "ksCurve3D", "ksEdgeDefinition", "ksBody", "ksSketchDefinition" })
            {
                var type = api5.GetExportedTypes().FirstOrDefault(t => t.Name == typeName);
                if (type is null)
                {
                    step.Observe($"Тип {typeName} в Kompas6API5 НЕ найден.");
                    continue;
                }

                var interesting = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Process", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("PID", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Exit", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Quit", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Visible", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Version", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("GetLength", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("GetParam", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("GetPoint", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Volume", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Area", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("LoadFrom", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("SaveAsTo", StringComparison.OrdinalIgnoreCase))
                    .Select(m => Describe(m))
                    .ToArray();

                if (interesting.Length > 0)
                {
                    step.Data["members:" + typeName] = interesting;
                    foreach (var line in interesting)
                    {
                        step.Observe($"{typeName}: {line}");
                    }
                }
            }

            step.Pass("Вендорский interop загружается в x64 .NET-процессе; метаданные выгружены на диск.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Не удалось загрузить или разобрать interop: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string ProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Directory.GetCurrentDirectory();
    }

    private static Dictionary<string, object> DumpAssembly(Assembly asm)
    {
        var types = new List<object>();
        foreach (var type in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsEnum)
            {
                var values = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var name in Enum.GetNames(type))
                {
                    var field = type.GetField(name)!;
                    values[name] = Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture);
                }

                types.Add(new { full_name = type.FullName, kind = "enum", values });
                continue;
            }

            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m switch
                {
                    MethodInfo mi => (object)new { kind = "method", name = mi.Name, signature = Describe(mi) },
                    PropertyInfo pi => new { kind = "property", name = pi.Name, type = pi.PropertyType.Name, can_read = pi.CanRead, can_write = pi.CanWrite },
                    FieldInfo fi => new { kind = "field", name = fi.Name, type = fi.FieldType.Name, constant = ConstantOf(fi) },
                    _ => (object)new { kind = "other", name = m.Name },
                })
                .ToArray();

            types.Add(new
            {
                full_name = type.FullName,
                kind = type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                interface_guid = type.GUID.ToString("N"),
                members,
            });
        }

        return new Dictionary<string, object>
        {
            ["assembly"] = asm.FullName ?? asm.GetName().Name ?? "?",
            ["file_version"] = TryFile(() => System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location).FileVersion),
            ["type_count"] = types.Count,
            ["types"] = types,
        };
    }

    private static string TryFile(Func<string?> reader)
    {
        try
        {
            return reader() ?? "unknown";
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }

    /// <summary>
    /// Field initial value, or null. <c>GetRawConstantValue</c> throws
    /// <see cref="InvalidOperationException"/> for fields that are not literals — and a
    /// tlbimp-generated module has plenty of those, so a raw call aborts the whole dump
    /// (which is how the first P0.2 run failed).
    /// </summary>
    private static string? ConstantOf(FieldInfo field)
    {
        try
        {
            return field.GetRawConstantValue()?.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Describe(MethodInfo m)
    {
        var ps = m.GetParameters().Select(p =>
        {
            var dir = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : string.Empty;
            return $"{dir}{Nice(p.ParameterType)} {p.Name}";
        });
        return $"{Nice(m.ReturnType)} {m.Name}({string.Join(", ", ps)})";
    }

    private static string Nice(Type t)
    {
        if (t.IsByRef)
        {
            return Nice(t.GetElementType()!) + "&";
        }

        if (t.IsArray)
        {
            return Nice(t.GetElementType()!) + "[]";
        }

        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name[..tick];
            }

            return name + "<" + string.Join(",", t.GetGenericArguments().Select(Nice)) + ">";
        }

        return t.Name;
    }
}

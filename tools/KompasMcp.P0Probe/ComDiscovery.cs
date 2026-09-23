using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;

namespace KompasMcp.P0Probe;

/// <summary>
/// Runtime discovery of what a КОМПАС object actually is.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> For an RCW handed back by <c>GetDefinition()</c> the CLR type name is
/// always <c>System.__ComObject</c> — it carries no information. <c>instance.GetType()
/// .GetInterfaces()</c> does not list COM interfaces either (they are grafted on demand by the
/// runtime), and reflecting that way produced false "member missing" conclusions earlier in this
/// project — see the header comment of <see cref="Members"/>. The only sound route is to take the
/// object's <c>IUnknown</c> and QueryInterface it against the interfaces the <em>vendor</em>
/// interop declares, then read the members off that static interop type.
/// <para>
/// <b>Why <c>Marshal.QueryInterface(IntPtr, …)</c> and not the <c>object</c> overload or
/// <c>Type.IsInstanceOfType</c>:</b> the <c>IntPtr</c> overload returns the HRESULT instead of
/// raising, so a negative answer is data rather than an exception, and it cannot surface as an
/// <see cref="ExecutionEngineException"/>. <c>IsInstanceOfType</c> over an RCW consults the
/// interfaces already grafted onto it, which for a fresh <c>__ComObject</c> is nothing — it would
/// report "supports nothing" for every object in the model.
/// </para>
/// <para>
/// The API7 interop is swept as well as API5: <c>Interop.Kompas6API5.dll</c> is a *reference*
/// assembly of this project and an absent name in it proves only that the name is absent from
/// that binary, not that the product lacks the feature.
/// </para>
/// </remarks>
internal static class ComDiscovery
{
    private static readonly List<InterfaceCandidate> Api5Candidates = Collect(typeof(KompasObject).Assembly);

    private static readonly Lazy<List<InterfaceCandidate>> Api7Candidates = new(CollectApi7);

    private static string? _api7LoadError;

    public static string Api7Status =>
        Api7Candidates.Value.Count > 0
            ? $"загружен {Api7Candidates.Value.Count} интерфейсов"
            : "не загружен: " + (_api7LoadError ?? "не найден");

    /// <summary>
    /// Interfaces from the vendor interop that the object answers QI for. API7 is opt-in: the sweep
    /// is an out-of-process call per interface, so it is only paid for where the answer matters
    /// (the hole definition), not for every body and edge.
    /// </summary>
    public static List<DiscoveredInterface> Probe(object? comObject, bool includeApi7 = false)
    {
        var found = new List<DiscoveredInterface>();
        if (comObject is null)
        {
            return found;
        }

        IntPtr unk;
        try
        {
            unk = Marshal.GetIUnknownForObject(comObject);
        }
        catch (Exception)
        {
            // A non-COM or already-disposed value: "no interfaces discovered" is the honest answer.
            return found;
        }

        try
        {
            Sweep(unk, Api5Candidates, found);
            if (includeApi7)
            {
                Sweep(unk, Api7Candidates.Value, found);
            }
        }
        finally
        {
            Marshal.Release(unk);
        }

        return found;
    }

    /// <summary>
    /// The names of the members the discovered interfaces declare, taken from the static interop
    /// type (never from the instance — see the class remarks).
    /// </summary>
    public static IReadOnlyList<string> MembersOf(string interfaceFullName)
    {
        var type = LookupType(interfaceFullName);
        return type is null ? Array.Empty<string>() : Describe(type);
    }

    public static Type? LookupType(string fullName)
    {
        var type = typeof(KompasObject).Assembly.GetType(fullName, throwOnError: false);
        if (type is not null)
        {
            return type;
        }

        foreach (var candidate in Api7Candidates.Value)
        {
            if (string.Equals(candidate.FullName, fullName, StringComparison.Ordinal))
            {
                return candidate.Type;
            }
        }

        return null;
    }

    /// <summary>All public instance members of an interop type as readable signatures.</summary>
    public static IReadOnlyList<string> Describe(Type type)
    {
        var lines = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var accessor = (property.CanRead ? "get" : string.Empty) + (property.CanRead && property.CanWrite ? "/" : string.Empty) + (property.CanWrite ? "set" : string.Empty);
            lines.Add($"prop {Members.Short(property.PropertyType)} {property.Name} {{{accessor}}}");
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .Where(m => !m.Name.StartsWith("get_", StringComparison.Ordinal) && !m.Name.StartsWith("set_", StringComparison.Ordinal))
                 .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            lines.Add("method " + Members.Signature(method));
        }

        return lines;
    }

    private static void Sweep(IntPtr unk, IReadOnlyList<InterfaceCandidate> candidates, List<DiscoveredInterface> sink)
    {
        foreach (var candidate in candidates)
        {
            var iid = candidate.Guid;
            IntPtr ppv;
            int hr;
            try
            {
                hr = Marshal.QueryInterface(unk, iid, out ppv);
            }
            catch (Exception ex)
            {
                sink.Add(new DiscoveredInterface(candidate.FullName, candidate.AssemblyName, "<QueryInterface бросил " + ex.GetType().Name + ">", Array.Empty<string>()));
                continue;
            }

            if (hr == 0 && ppv != IntPtr.Zero)
            {
                Marshal.Release(ppv);
                sink.Add(new DiscoveredInterface(candidate.FullName, candidate.AssemblyName, null, Describe(candidate.Type)));
            }
        }
    }

    private static List<InterfaceCandidate> Collect(Assembly assembly)
    {
        var list = new List<InterfaceCandidate>();
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (Exception)
        {
            return list;
        }

        foreach (var type in types)
        {
            if (!type.IsInterface)
            {
                continue;
            }

            Guid guid;
            try
            {
                guid = type.GUID;
            }
            catch (Exception)
            {
                continue;
            }

            if (guid == Guid.Empty)
            {
                continue;
            }

            list.Add(new InterfaceCandidate(type, assembly.GetName().Name ?? "?", guid));
        }

        return list;
    }

    private static List<InterfaceCandidate> CollectApi7()
    {
        var directory = KompasMcp.Api5Adapter.KompasInteropResolver.ResolvedDirectory;
        var path = directory is null ? null : Path.Combine(directory, "Interop.KompasAPI7.dll");
        if (path is null || !File.Exists(path))
        {
            _api7LoadError = "Interop.KompasAPI7.dll не найден рядом с interop API5";
            return new List<InterfaceCandidate>();
        }

        try
        {
            // LoadFrom, not a project reference: the API7 binary is evidence here, not a
            // dependency. Nothing in the adapter may end up compiled against it by this probe.
            return Collect(Assembly.LoadFrom(path));
        }
        catch (Exception ex)
        {
            _api7LoadError = ex.GetType().Name + ": " + ex.Message;
            return new List<InterfaceCandidate>();
        }
    }

    private sealed record InterfaceCandidate(Type Type, string AssemblyName, Guid Guid)
    {
        public string FullName => Type.FullName ?? Type.Name;
    }
}

internal sealed record DiscoveredInterface(string FullName, string AssemblyName, string? Error, IReadOnlyList<string> Members)
{
    public string Describe() => Error is null ? FullName : FullName + " " + Error;
}

/// <summary>
/// Named constants from the vendor <c>ksConstants3D</c> type library, resolved by name the same
/// way <see cref="EntityTypes"/> resolves <c>ksObj3dTypeEnum</c>.
/// </summary>
/// <remarks>
/// End-condition selectors are the case that matters: <c>SetSideParam</c> takes a bare
/// <see cref="short"/>, so the adapter would otherwise be passing numbers whose meaning nobody on
/// this repository had measured. Names come from the vendor binary; whether a given value really
/// behaves as "насквозь" is still decided by the probe (<c>P2.1</c>), not by the name.
/// </remarks>
internal static class VendorConstants
{
    private static readonly Dictionary<string, Type?> Cache = new(StringComparer.Ordinal);

    public static string? LoadError { get; private set; }

    public static short Value(string enumName, string member, short fallback)
    {
        var field = EnumType(enumName)?.GetField(member, BindingFlags.Public | BindingFlags.Static);
        if (field is null)
        {
            return fallback;
        }

        return Convert.ToInt16(field.GetRawConstantValue() ?? fallback, CultureInfo.InvariantCulture);
    }

    /// <summary>Every <c>name=value</c> pair, ordered by value — the candidate list for a sweep.</summary>
    public static IReadOnlyList<(string Name, long Value)> Members(string enumName)
    {
        var type = EnumType(enumName);
        if (type is null)
        {
            return Array.Empty<(string, long)>();
        }

        var list = new List<(string Name, long Value)>();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            try
            {
                list.Add((field.Name, Convert.ToInt64(field.GetRawConstantValue() ?? 0L, CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // Non-literal field in a tlbimp-generated module; skip rather than abort (P0.2).
            }
        }

        return list.OrderBy(e => e.Value).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public static string NameOf(string enumName, long value)
    {
        var members = Members(enumName);
        var match = members.FirstOrDefault(m => m.Value == value);
        return match.Name is null || members.Count == 0 ? "<вне enum>" : match.Name;
    }

    /// <summary>Reverse lookup in <c>ksObj3dTypeEnum</c>: a number the API returned → its name.</summary>
    public static string NameOfObj3d(long value) => NameOf("ksObj3dTypeEnum", value);

    private static Type? EnumType(string enumName)
    {
        if (Cache.TryGetValue(enumName, out var cached))
        {
            return cached;
        }

        var resolved = Resolve(enumName);
        Cache[enumName] = resolved;
        return resolved;
    }

    private static Type? Resolve(string enumName)
    {
        var directory = KompasMcp.Api5Adapter.KompasInteropResolver.ResolvedDirectory;
        foreach (var assemblyFileName in new[] { "Interop.Kompas6Constants3D.dll", "Interop.Kompas6Constants.dll" })
        {
            var path = directory is null ? null : Path.Combine(directory, assemblyFileName);
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var candidate in assembly.GetExportedTypes().Where(t => t.IsEnum))
                {
                    if (string.Equals(candidate.Name, enumName, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                LoadError = ex.GetType().Name;
            }
        }

        return null;
    }
}

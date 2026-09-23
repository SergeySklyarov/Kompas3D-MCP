using System.Reflection;
using Kompas6API5;

namespace KompasMcp.P0Probe;

/// <summary>
/// Member discovery over the vendor interop.
/// </summary>
/// <remarks>
/// <b>The trap:</b> an RCW made by <c>Activator.CreateInstance(Type.GetTypeFromProgID(…))</c> is a
/// bare <c>System.__ComObject</c>. Casting it to <c>KompasObject</c> works (the runtime
/// QueryInterfaces and grafts the interface onto that object), but
/// <c>instance.GetType().GetInterfaces()</c> does not list COM interfaces — they are not part of
/// the CLR type. An earlier revision of this probe reflected over the instance and concluded,
/// wrongly, that <c>ksDocument3D</c> has no import method and that <c>KompasObject.Visible</c>
/// does not exist. Every lookup here therefore goes through an explicit <c>typeof(…)</c>.
/// </remarks>
internal static class Members
{
    public static bool Has(Type iface, string name) =>
        iface.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy) is not null
        || iface.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    public static object? Prop(Type iface, object target, string name)
    {
        var pi = iface.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                 ?? throw new MissingMemberException(iface.FullName, name);
        try
        {
            return pi.GetValue(target);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    public static void SetProp(Type iface, object target, string name, object? value)
    {
        var pi = iface.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                 ?? throw new MissingMemberException(iface.FullName, name);
        try
        {
            pi.SetValue(target, value);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>Readable signatures of members whose name matches any keyword.</summary>
    public static IReadOnlyList<string> Matching(Type iface, params string[] keywords)
    {
        var methods = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => keywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(Signature);

        var properties = iface.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => keywords.Any(k => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.PropertyType.Name} {p.Name} {{get;set;}}");

        return methods.Concat(properties).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    public static string Signature(MethodInfo m)
    {
        var parameters = string.Join(", ", m.GetParameters().Select(p =>
            (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : string.Empty) + Short(p.ParameterType) + " " + p.Name));
        return $"{Short(m.ReturnType)} {m.Name}({parameters})";
    }

    public static string Short(Type type)
    {
        if (type.IsByRef)
        {
            return Short(type.GetElementType()!) + "&";
        }

        if (type.IsArray)
        {
            return Short(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name[..tick];
            }

            return name + "<" + string.Join(",", type.GetGenericArguments().Select(Short)) + ">";
        }

        return type.Name;
    }

    public static string Value(object? value) => value switch
    {
        null => "null",
        double d => d.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture),
        IConvertible => value.ToString()!,
        _ => value.GetType().Name,
    };
}

/// <summary>
/// Named entity types from the vendor enum, resolved once by name. The magic numbers in
/// docs/04 are an investigation map, not a contract; the adapter must use these instead.
/// </summary>
internal static class EntityTypes
{
    private static readonly Lazy<Type?> Cached = new(Lookup);

    private static Type? EnumType => Cached.Value;

    /// <summary>
    /// The object-type enum lives in the constants interop, which nothing in the probe references
    /// statically — so it is not in the load context until it is loaded by path. A first attempt
    /// searched only AppDomain and reported "enum not found", which silently fell back to magic
    /// numbers (P0.8 run 2).
    /// </summary>
    private static Type? Lookup()
    {
        foreach (var assemblyName in new[] { "Interop.Kompas6Constants3D.dll", "Interop.Kompas6Constants.dll" })
        {
            var directory = KompasMcp.Api5Adapter.KompasInteropResolver.ResolvedDirectory;
            var path = directory is null ? null : Path.Combine(directory, assemblyName);
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var assembly = Assembly.LoadFrom(path);
                var candidate = assembly.GetExportedTypes().FirstOrDefault(t => t.IsEnum && t.Name.Contains("Obj3dType", StringComparison.Ordinal));
                if (candidate is not null)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                continue;
            }
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ksObj3dTypeEnum") ?? a.GetType("Kompas6Constants3D.ksObj3dTypeEnum"))
            .FirstOrDefault(t => t is not null && t.IsEnum);
    }

    public static string SourceAssembly => EnumType?.Assembly.GetName().Name ?? "<не найден>";

    public static bool Resolved => EnumType is not null;

    public static short Value(string constant, short fallback)
    {
        if (EnumType is null)
        {
            return fallback;
        }

        var field = EnumType.GetField(constant, BindingFlags.Public | BindingFlags.Static);
        if (field is null)
        {
            return fallback;
        }

        return Convert.ToInt16(field.GetRawConstantValue() ?? fallback, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> MatchNames(params string[] needles) =>
        EnumType is null
            ? Array.Empty<string>()
            : EnumType.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => needles.Any(n => f.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Name + "=" + f.GetRawConstantValue())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
}

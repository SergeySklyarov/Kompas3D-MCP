using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;

namespace KompasMcp.P0Probe;

/// <summary>
/// Reads and writes an extrusion's end condition without deciding its family in advance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists instead of a cast.</b> The second P2 run measured that a base extrusion
/// created with <c>NewEntity(o3d_baseExtrusion=24)</c> comes back out of the document as
/// <c>ksEntity.type = 25</c> (<c>o3d_bossExtrusion</c>) answering
/// <c>ksBossExtrusionDefinition</c>. Code that casts to <c>ksBaseExtrusionDefinition</c> — which is
/// what a tool would naturally write after calling <c>NewEntity(24)</c> — therefore finds nothing
/// in a document it did not create itself. And <c>kompas_get_feature</c> cannot be written as
/// "cast by family", because the family is exactly what it is trying to discover.
/// </para>
/// <para>
/// All the extrusion definitions declare the same <c>GetSideParam</c>/<c>SetSideParam</c>, so the
/// generic route is: take the set of vendor interfaces that <em>declare</em> that method (computed
/// once from the interop assembly, in-process), QueryInterface the object against each, and invoke
/// the method from the static interop type it answered. QI is used rather than
/// <c>instance.GetType()</c> reflection for the reason documented in <see cref="ComDiscovery"/>.
/// </para>
/// </remarks>
internal static class SideParams
{
    /// <summary>(type, depth, draft, draftOutward) for one side of an extrusion.</summary>
    public sealed record Value(short Type, double Depth, double Draft, bool Outward);

    private static readonly Type[] Declarers = ResolveDeclarers();

    /// <summary>The vendor interface the last resolved definition answered for, for reporting.</summary>
    public static string LastResolvedInterface { get; private set; } = "<не разрешалось>";

    public static int DeclaredInterfaceCount => Declarers.Length;

    /// <summary>Which of the interfaces declaring GetSideParam, if any, this object answers.</summary>
    public static (Type? Type, MethodInfo? Get, MethodInfo? Set) Resolve(object? definition)
    {
        if (definition is null)
        {
            return (null, null, null);
        }

        var unk = IntPtr.Zero;
        try
        {
            unk = Marshal.GetIUnknownForObject(definition);
        }
        catch (Exception)
        {
            return (null, null, null);
        }

        try
        {
            foreach (var type in Declarers)
            {
                var iid = type.GUID;
                IntPtr ppv;
                int hr;
                try
                {
                    hr = Marshal.QueryInterface(unk, iid, out ppv);
                }
                catch (Exception)
                {
                    continue;
                }

                if (hr != 0 || ppv == IntPtr.Zero)
                {
                    continue;
                }

                Marshal.Release(ppv);
                var get = Find(type, "GetSideParam");
                if (get is null)
                {
                    continue;
                }

                LastResolvedInterface = type.FullName ?? type.Name;
                return (type, get, Find(type, "SetSideParam"));
            }
        }
        finally
        {
            Marshal.Release(unk);
        }

        return (null, null, null);
    }

    /// <summary>Reads one side's end condition, or null if this is not an extrusion-like object.</summary>
    public static Value? Get(object? definition, bool side1)
    {
        var (_, get, _) = Resolve(definition);
        if (get is null)
        {
            return null;
        }

        var parameters = get.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = side1;
        for (var i = 1; i < parameters.Length; i++)
        {
            args[i] = parameters[i].ParameterType == typeof(short).MakeByRefType() ? (short)0
                : parameters[i].ParameterType == typeof(bool).MakeByRefType() ? false
                : (object)0d;
        }

        object? returned;
        try
        {
            returned = Invoke(definition!, get, args);
        }
        catch (Exception)
        {
            return null;
        }

        if (returned is not true)
        {
            return null;
        }

        return new Value(
            Convert.ToInt16(args[1], CultureInfo.InvariantCulture),
            Convert.ToDouble(args[2], CultureInfo.InvariantCulture),
            Convert.ToDouble(args[3], CultureInfo.InvariantCulture),
            Convert.ToBoolean(args[4], CultureInfo.InvariantCulture));
    }

    /// <summary>Writes one side's end condition; returns what the API answered, or null if unreachable.</summary>
    public static bool? Set(object? definition, bool side1, short type, double depth, double draft = 0d, bool outward = false)
    {
        var (_, _, set) = Resolve(definition);
        if (set is null)
        {
            return null;
        }

        var parameters = set.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = side1;
        for (var i = 1; i < parameters.Length && i < 5; i++)
        {
            var target = parameters[i].ParameterType;
            args[i] = i switch
            {
                1 => target == typeof(int) ? (object)(int)type : (object)type,
                2 => depth,
                3 => draft,
                _ => outward,
            };
        }

        try
        {
            return Invoke(definition!, set, args) as bool?;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The family question a generic reader has to answer: is this definition something whose side
    /// parameters can be read at all.
    /// </summary>
    public static bool IsExtrusionLike(object? definition) => Resolve(definition).Type is not null;

    private static Type[] ResolveDeclarers()
    {
        var list = new List<Type>();
        foreach (var type in typeof(KompasObject).Assembly.GetExportedTypes())
        {
            if (!type.IsInterface)
            {
                continue;
            }

            try
            {
                if (type.GUID != Guid.Empty && Find(type, "GetSideParam") is not null)
                {
                    list.Add(type);
                }
            }
            catch (Exception)
            {
                // A type whose members cannot be reflected over must not abort the whole survey.
            }
        }

        // The three extrusion families first: they are the overwhelmingly common answer and each
        // candidate costs one cross-process QueryInterface.
        return list
            .OrderByDescending(t => t.Name.Contains("Extrusion", StringComparison.Ordinal))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static MethodInfo? Find(Type type, string name)
    {
        try
        {
            return type.GetMethod(name);
        }
        catch (AmbiguousMatchException)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? Invoke(object target, MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KompasMcp.InteropScan;

/// <summary>Reads an installed COM type library and prints the vtable layout of the requested
/// interfaces, in the order the library itself declares.</summary>
/// <remarks>WHY IT EXISTS: a hand-declared <c>[ComImport]</c> twin reproduces a VTABLE, and the only
/// authority for that vtable is the type library the installed build ships. TlbImp would answer the
/// same question, but it is a Windows SDK tool that may be absent and it rewrites what it cannot
/// import; <c>ITypeInfo</c> prints what the library actually declares, including the position of every
/// member, which is exactly the property a twin can get wrong.
/// INVARIANT: read-only. The library is opened with <c>REGKIND_NONE</c>, so nothing is registered and
/// the installation is not modified.
/// History: docs/decisions/assembly.md#api7-twin</remarks>
internal static class TlbLayout
{
    /// <summary><c>REGKIND_NONE</c>: load the library without registering it.</summary>
    private const int RegKindNone = 2;

    /// <summary><c>TYPEFLAG_FDUAL</c>: the interface is reachable both by vtable and by dispatch.</summary>
    private const int TypeFlagDual = 0x40;

    /// <summary>The number of slots IUnknown (3) and IDispatch (4) occupy in front of a dual
    /// interface's own members — so member <c>i</c> of such an interface sits in slot <c>7 + i</c>.</summary>
    private const int DualBaseSlots = 7;

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string szFile, int regkind, out ITypeLib pptlib);

    public static int Run(string tlbPath, IReadOnlyList<string> wanted)
    {
        if (!File.Exists(tlbPath))
        {
            Console.Error.WriteLine($"TLB_NOT_FOUND {tlbPath}");
            return 4;
        }

        ITypeLib library;
        try
        {
            LoadTypeLibEx(tlbPath, RegKindNone, out library);
        }
        catch (COMException ex)
        {
            // A library that will not open is reported, never treated as "declares nothing".
            Console.Error.WriteLine($"TLB_LOAD_FAILED {tlbPath}: 0x{ex.HResult:X8}: {ex.Message}");
            return 3;
        }

        var bytes = new FileInfo(tlbPath).Length;
        Console.WriteLine($"TLB {Path.GetFileName(tlbPath)} bytes={bytes}");

        var index = Index(library);
        foreach (var name in wanted)
        {
            if (!index.TryGetValue(name, out var found))
            {
                Console.WriteLine($"TYPE_NOT_FOUND {name}");
                continue;
            }

            Dump(name, found.Info);
        }

        return 0;
    }

    /// <summary>Every type of the library by name, with its position in the library.</summary>
    private static Dictionary<string, (int Position, ITypeInfo Info)> Index(ITypeLib library)
    {
        var count = library.GetTypeInfoCount();
        var byName = new Dictionary<string, (int, ITypeInfo)>(StringComparer.OrdinalIgnoreCase);
        for (var position = 0; position < count; position++)
        {
            library.GetTypeInfo(position, out var info);
            library.GetDocumentation(position, out var name, out _, out _, out _);
            if (!string.IsNullOrEmpty(name))
            {
                byName.TryAdd(name!, (position, info));
            }
        }

        return byName;
    }

    private static void Dump(string requested, ITypeInfo info)
    {
        info.GetTypeAttr(out var pointer);
        var attr = Marshal.PtrToStructure<TYPEATTR>(pointer);
        info.ReleaseTypeAttr(pointer);

        var dual = ((int)attr.wTypeFlags & TypeFlagDual) != 0;
        Console.WriteLine(
            $"TYPE {requested} iid={attr.guid:N} typekind={(int)attr.typekind} " +
            $"dual={dual.ToString().ToLowerInvariant()} cFuncs={attr.cFuncs} cVars={attr.cVars} " +
            $"implTypes={attr.cImplTypes}");

        for (var member = 0; member < attr.cFuncs; member++)
        {
            info.GetFuncDesc(member, out var functionPointer);
            var function = Marshal.PtrToStructure<FUNCDESC>(functionPointer);
            var parameterCount = function.cParams;
            var names = new string[parameterCount + 1];
            info.GetNames(function.memid, names, names.Length, out var nameCount);
            var memberName = nameCount > 0 && !string.IsNullOrEmpty(names[0])
                ? names[0]
                : $"memid_{function.memid}";
            var parameterTypes = ParameterTypes(function.lprgelemdescParam, parameterCount);
            info.ReleaseFuncDesc(functionPointer);

            Console.WriteLine(
                $"member|{requested}|{memberName}|slot={SlotOf(function, member, dual)} " +
                $"oVft={function.oVft} invkind={(int)function.invkind} " +
                $"funckind={(int)function.funckind} cParams={parameterCount} " +
                $"ret={(int)function.elemdescFunc.tdesc.vt} params={string.Join(",", parameterTypes)}");
        }

        if (attr.cVars > 0)
        {
            // A VARIABLE OCCUPIES NO VTABLE SLOT but still belongs to the interface's layout, so its
            // COUNT is published rather than left out: a reader who sees it knows the declaration has a
            // second kind of member, and a twin that declares only functions is then visibly incomplete.
            Console.WriteLine($"vars|{requested}|count={attr.cVars}");
        }
    }

    /// <summary>The vtable slot of one function of the interface.</summary>
    /// <remarks>MEASURED on the installed <c>kAPI7.tlb</c>: the type library's function list of a dual
    /// interface STARTS with the seven <c>IUnknown</c>/<c>IDispatch</c> members, so the list index is not
    /// the slot — it is the slot plus seven, and only for the interface's own members. <c>FUNCDESC.oVft</c>
    /// carries the byte offset directly (0, 8, 16, …), and that is what is read here; the index is kept
    /// only as a fallback for a library that leaves the offset unset.
    /// History: docs/decisions/assembly.md#api7-twin</remarks>
    private static string SlotOf(FUNCDESC function, int index, bool dual)
    {
        if (!dual)
        {
            // A dispatch-only interface has no vtable of its own, so a slot number here would be a
            // number nothing uses.
            return "none(not_dual)";
        }

        return function.oVft >= 0
            ? (function.oVft / 8).ToString(CultureInfo.InvariantCulture)
            : (DualBaseSlots + index).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>VARTYPE</c> of every parameter, in order. A pointer parameter is reported by its
    /// pointer code: the pointee is a separate reading and is not guessed here.</summary>
    private static IEnumerable<int> ParameterTypes(IntPtr array, int count)
    {
        if (array == IntPtr.Zero || count <= 0)
        {
            yield break;
        }

        var stride = Marshal.SizeOf<ELEMDESC>();
        for (var index = 0; index < count; index++)
        {
            var element = Marshal.PtrToStructure<ELEMDESC>(IntPtr.Add(array, index * stride));
            yield return element.tdesc.vt;
        }
    }
}

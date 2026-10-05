using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KompasMcp.Api7Probe;

/// <summary>Reads a KOMPAS type library (.tlb) out of the installation: which interfaces it declares, and
/// whether a named interface declares a named member.</summary>
/// <remarks>
/// Why this exists: ADR-003 §4 refuses to treat "a type is missing from
/// <c>Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll</c>" as evidence that the installed product
/// lacks the feature. That assembly is a snapshot (file date 2025-03-03) of the same API that
/// <c>Bin\kAPI7.tlb</c> describes later (2025-04-21). The application's own type library is the
/// statement about the application that does not pass through the snapshot, so the probe reads it.
/// It is opened with <c>REGKIND_NONE</c>: nothing is registered, nothing under
/// <c>D:\Programs\KOMPAS-3Dv24</c> is written or replaced.
///
/// Everything is answered with managed <c>ITypeLib</c>/<c>ITypeInfo</c> calls —
/// <c>GetTypeInfoType</c>, <c>GetDocumentation</c>, <c>GetTypeInfoOfGuid</c>,
/// <c>ITypeInfo::GetIDsOfNames</c>. The first revision of this class read <c>TYPEATTR</c> through
/// hand-computed byte offsets and produced IIDs that were obviously wrong while still running —
/// which is the exact failure a passport must not contain, because a wrong IID reads as a finding.
/// Asking the library "is there a type with this IID" and "does that type answer to this member
/// name" needs no pointer arithmetic at all.
/// </remarks>
internal static class TlbScan
{
    private const uint RegKindNone = 2;

    // TYPEKIND per oleauto.h: enum 0, record 1, module 2, interface 3, dispatch 4, coclass 5,
    // alias 6, union 7. Confusing 4 with 5 silently turns the interface inventory into a coclass
    // inventory, which is what an earlier revision of this file did.
    private const int TKindInterface = 3;
    private const int TKindDispatch = 4;

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void LoadTypeLibEx(
        [MarshalAs(UnmanagedType.LPWStr)] string file,
        uint regKind,
        [MarshalAs(UnmanagedType.Interface)] out ITypeLib typeLib);

    public sealed record Entry(string Name, string Kind);

    public sealed record Result(
        string Path,
        string Sha256,
        long SizeBytes,
        DateTime WrittenUtc,
        int TypeInfoCount,
        int InterfaceCount,
        List<Entry> Entries,
        string? Failure)
    {
        public bool Declares(string name) => Entries.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));

        public string KindOf(string name) =>
            Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))?.Kind ?? "<не объявлен>";
    }

    private static ITypeLib Open(string path)
    {
        LoadTypeLibEx(path, RegKindNone, out var lib);
        return lib;
    }

    public static Result Read(string path)
    {
        var sha = InteropResolver.Sha256(path);
        var info = new FileInfo(path);
        ITypeLib lib;
        try
        {
            lib = Open(path);
        }
        catch (Exception ex)
        {
            return new Result(path, sha, info.Length, info.LastWriteTimeUtc, 0, 0, new List<Entry>(), HResult.Describe(ex));
        }

        var entries = new List<Entry>();
        string? failure = null;
        var count = lib.GetTypeInfoCount();
        for (var i = 0; i < count; i++)
        {
            ITypeInfo? typeInfo = null;
            try
            {
                lib.GetTypeInfoType(i, out var kind);
                var kindValue = (int)kind;
                if (kindValue is not (TKindInterface or TKindDispatch))
                {
                    continue;
                }

                lib.GetTypeInfo(i, out typeInfo);
                typeInfo.GetDocumentation(-1, out var name, out _, out _, out _);
                entries.Add(new Entry(name ?? "?", kindValue == TKindInterface ? "interface" : "dispinterface"));
            }
            catch (Exception ex)
            {
                failure ??= "type info " + i + ": " + HResult.Describe(ex);
            }
            finally
            {
                if (typeInfo is not null)
                {
                    _ = Marshal.ReleaseComObject(typeInfo);
                }
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new Result(path, sha, info.Length, info.LastWriteTimeUtc, count, entries.Count, entries, failure);
    }

    /// <summary>What the installed library says about an IID the probe's wrapper declares: the name of the
    /// type that owns it, or "no such type in this library". This is the check that turns
    /// "the wrapper is missing a member" into a statement about the wrapper rather than the product.</summary>
    public static string OwnerOfIid(string path, Guid iid)
    {
        ITypeInfo? typeInfo = null;
        try
        {
            var lib = Open(path);
            lib.GetTypeInfoOfGuid(ref iid, out typeInfo);
            typeInfo.GetDocumentation(-1, out var name, out _, out _, out _);
            return name ?? "<без имени>";
        }
        catch (Exception ex)
        {
            return HResult.Of(ex) is int code && (code & 0xFFFF_0000) == unchecked((int)0x80020000)
                ? "нет типа с таким IID в этом библиотеке типов"
                : "<" + HResult.Describe(ex) + ">";
        }
        finally
        {
            if (typeInfo is not null)
            {
                _ = Marshal.ReleaseComObject(typeInfo);
            }
        }
    }

    /// <summary>Does the library's declaration of <paramref name="interfaceName"/> name each of these
    /// members? Answered by <c>ITypeInfo::GetIDsOfNames</c> on the type info the library itself
    /// hands back for the wrapper's IID — so a "yes" cannot be an artefact of the wrapper.</summary>
    public static Dictionary<string, string> MembersOf(string path, Guid interfaceIid, IReadOnlyList<string> members)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        ITypeInfo? typeInfo = null;
        try
        {
            var lib = Open(path);
            var iid = interfaceIid;
            lib.GetTypeInfoOfGuid(ref iid, out typeInfo);
        }
        catch (Exception ex)
        {
            foreach (var member in members)
            {
                result[member] = "<интерфейс не найден в библиотеке: " + HResult.Describe(ex) + ">";
            }

            return result;
        }

        try
        {
            foreach (var member in members)
            {
                try
                {
                    var ids = new int[1];
                    typeInfo.GetIDsOfNames([member], 1, ids);
                    result[member] = "объявлен, dispid=" + ids[0];
                }
                catch (Exception ex)
                {
                    result[member] = HResult.Of(ex) == HResult.DISP_E_MEMBERNOTFOUND ? "в библиотеке не объявлен" : HResult.Describe(ex);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var member in members)
            {
                result.TryAdd(member, "<" + HResult.Describe(ex) + ">");
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(typeInfo);
        }

        return result;
    }

    /// <summary>Which member NAMES does the library declare for <paramref name="interfaceName"/>? Answered by
    /// walking the type info's own member identifiers and asking it to document each one — so the
    /// list comes from the product's type library, not from a hand-written candidate list.</summary>
    /// <remarks>
    /// ASSUMPTION: a candidate list invented by a human measures his imagination. The question is not
    /// whether a quantity exists but which MEMBER the product returns it through, so the member list
    /// must be read from the library, not guessed.
    /// MEASURED: 19.09.2026, step RP.10 — ten candidate names did not include the displacement vector,
    /// yet the vector IS in the model: scanning the decompressed `.m3d` streams, the file with the
    /// displacement (7,−11,13) contains that triple of doubles four times, the control file with
    /// (1,2,3) contains none. The live feature answers <c>Position</c> dispid=804,
    /// <c>RepositionCentre</c> dispid=802, <c>CopyBoby</c> dispid=803, and the interface declares
    /// members around 800. A miss is the norm here, not the end of the range: the range is read in
    /// full, and "none" means "scanned <paramref name="max"/> identifiers and not one was documented".
    /// History: docs/decisions/probes.md#tlb-membernames
    /// </remarks>
    public static List<(int MemId, string Name)> MemberNames(string path, string interfaceName, int max = 2048)
    {
        var result = new List<(int, string)>();
        ITypeInfo? typeInfo = null;
        try
        {
            var lib = Open(path);
            var count = lib.GetTypeInfoCount();
            for (var i = 0; i < count; i++)
            {
                lib.GetTypeInfo(i, out var candidate);
                if (candidate is null)
                {
                    continue;
                }

                try
                {
                    candidate.GetDocumentation(-1, out var name, out _, out _, out _);
                    if (string.Equals(name, interfaceName, StringComparison.Ordinal))
                    {
                        typeInfo = candidate;
                        break;
                    }
                }
                catch (Exception)
                {
                    // A type without documentation is not the subject of this question.
                }

                _ = Marshal.ReleaseComObject(candidate);
            }
        }
        catch (Exception ex)
        {
            result.Add((-1, "<библиотека не открылась: " + HResult.Describe(ex) + ">"));
            return result;
        }

        if (typeInfo is null)
        {
            result.Add((-1, "<интерфейс не объявлен в библиотеке>"));
            return result;
        }

        try
        {
            for (var memId = 1; memId <= max; memId++)
            {
                try
                {
                    typeInfo.GetDocumentation(memId, out var name, out _, out _, out _);
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Add((memId, name));
                    }
                }
                catch (Exception)
                {
                    // An unoccupied identifier. Not a sign of the end of the range: some API7
                    // interfaces declare their members in the hundreds, not consecutively from one.
                }
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(typeInfo);
        }

        return result;
    }

    /// <summary>EVERY member name the type library declares anywhere. Needed as a POOL for probing a live
    /// object whose type matches no declared interface: a <c>System.__ComObject</c> has no list of
    /// its own to ask, and asking it names invented by a human means measuring the imagination.</summary>
    public static List<string> AllMemberNames(string path, int max = 2048)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        ITypeLib lib;
        try
        {
            lib = Open(path);
        }
        catch (Exception)
        {
            return new List<string>();
        }

        var count = lib.GetTypeInfoCount();
        for (var i = 0; i < count; i++)
        {
            ITypeInfo? typeInfo = null;
            try
            {
                lib.GetTypeInfoType(i, out var kind);
                if ((int)kind is not (TKindInterface or TKindDispatch))
                {
                    continue;
                }

                lib.GetTypeInfo(i, out typeInfo);
                for (var memId = 1; memId <= max; memId++)
                {
                    try
                    {
                        typeInfo.GetDocumentation(memId, out var name, out _, out _, out _);
                        if (!string.IsNullOrEmpty(name))
                        {
                            names.Add(name);
                        }
                    }
                    catch (Exception)
                    {
                        // An unoccupied identifier.
                    }
                }
            }
            catch (Exception)
            {
                // A type the library does not hand back does not enter the pool.
            }
            finally
            {
                if (typeInfo is not null)
                {
                    _ = Marshal.ReleaseComObject(typeInfo);
                }
            }
        }

        return names.ToList();
    }

    /// <summary>Asks a <em>live</em> object, through its own <c>IDispatch</c>, which member names it answers
    /// to. Neither an interop assembly nor a type library is in this path, so a name that resolves
    /// here exists in the running application even where the prebuilt wrapper predates it — the
    /// distinction ADR-003 §4 asks the probe to draw.</summary>
    public static Dictionary<string, string> LiveMembers(object comObject, IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            result[name] = Late.Dispid(comObject, name);
        }

        return result;
    }
}

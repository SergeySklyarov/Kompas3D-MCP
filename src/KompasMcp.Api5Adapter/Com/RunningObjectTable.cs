using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using KompasMcp.Api5Adapter;

namespace KompasMcp.Api5Adapter.Com;

public sealed record RotEntry(string DisplayName, object? Object, string? Error);

/// <summary>
/// Reads the running object table so <c>attach</c> can say how many КОМПАС instances are
/// reachable instead of grabbing whichever object COM hands back first (spec 1.6: several
/// instances that cannot be disambiguated must produce AMBIGUOUS_APPLICATION).
/// </summary>
/// <remarks>
/// The ROT is the only place that lists *existing* out-of-process servers without starting a new
/// one. Note what it does NOT give us: a display name is a moniker string, not a process id, so
/// PID attribution still has to be proved separately (see P0 step P0.4).
/// </remarks>
public static class RunningObjectTable
{
    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateBindCtx(int reserved, out IBindCtx ppbc);

    // The global ole32 entry point rather than IBindCtx.GetRunningObjectTable: the managed
    // IBindCtx declaration does not expose that method with the two-argument shape.
    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    /// <summary>
    /// ROT entries for one specific КОМПАС ProgID (default <c>KOMPAS.Application.5</c>), plus any
    /// entry whose display name mentions КОМПАС by text.
    /// </summary>
    /// <param name="progId">
    /// The ProgID whose CLSID identifies the instance we can actually use. Scoping to one ProgID is
    /// deliberate: one running КОМПАС registers <b>both</b> the API5 and the API7 CLSIDs, so
    /// matching every known CLSID reports a single instance as two candidates — and the API7 object
    /// cannot be cast to <c>KompasObject</c> anyway, so it can never be a valid attach target.
    /// </param>
    public static IReadOnlyList<RotEntry> EnumerateKompasEntries(string progId = "KOMPAS.Application.5")
    {
        var clsid = KompasInteropResolver.ProgIdToClsid(progId);
        var result = new List<RotEntry>();

        CreateBindCtx(0, out var bindCtx);
        try
        {
            GetRunningObjectTable(0, out var rot);
            try
            {
                rot.EnumRunning(out var enumerator);
                var monikers = new IMoniker[1];
                while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    if (moniker is null)
                    {
                        continue;
                    }

                    string name;
                    try
                    {
                        moniker.GetDisplayName(bindCtx, null, out name);
                    }
                    catch (Exception ex)
                    {
                        name = "<имя недостижимо: " + ex.GetType().Name + ">";
                    }

                    if (!MatchesProgId(name, clsid))
                    {
                        continue;
                    }

                    object? bound = null;
                    string? error = null;
                    try
                    {
                        rot.GetObject(moniker, out bound);
                    }
                    catch (Exception ex)
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}";
                    }

                    result.Add(new RotEntry(name, bound, error));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(rot);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(bindCtx);
        }

        return result;
    }

    /// <summary>
    /// Every entry in the ROT, unfiltered, up to <paramref name="max"/>.
    /// </summary>
    /// <remarks>
    /// Exists because "0 КОМПАС entries" means one of two completely different things: the table
    /// really has none, or this enumerator cannot see any. Without this count the two are
    /// indistinguishable, and a broken filter would be reported as a property of КОМПАС. A desktop
    /// with Explorer running normally has at least a few entries (Shell.Windows and similar), so a
    /// total of zero points at the enumerator, not at the CAD application.
    /// </remarks>
    public static (int Total, IReadOnlyList<string> Names) EnumerateAllEntries(int max = 40)
    {
        var names = new List<string>();
        var total = 0;

        CreateBindCtx(0, out var bindCtx);
        try
        {
            GetRunningObjectTable(0, out var rot);
            try
            {
                rot.EnumRunning(out var enumerator);
                var monikers = new IMoniker[1];
                while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    if (moniker is null)
                    {
                        continue;
                    }

                    total++;
                    if (names.Count < max)
                    {
                        try
                        {
                            moniker.GetDisplayName(bindCtx, null, out var name);
                            names.Add(name ?? "<null>");
                        }
                        catch (Exception ex)
                        {
                            names.Add("<имя недостижимо: " + ex.GetType().Name + ">");
                        }
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(rot);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(bindCtx);
        }

        return (total, names);
    }

    /// <summary>
    /// Recognises a КОМПАС ROT entry.
    /// </summary>
    /// <remarks>
    /// КОМПАС registers in the ROT under its <b>CLSID</b> — display name <c>!{6B0B5194-…}</c> —
    /// not under any readable name. Matching on "kompas"/"КОМПАС"/"ascon" therefore finds nothing
    /// and makes a working attach look impossible: that false negative was reported as a property
    /// of КОМПАС until the unfiltered ROT dump exposed it. The friendly-name test is kept only as a
    /// secondary, for instances that register by ProgID.
    /// </remarks>
    private static bool MatchesProgId(string name, string? clsid)
    {
        if (clsid is not null && name.Contains(clsid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Secondary: an instance that registered under a readable name instead of its CLSID.
        return name.Contains("kompas", StringComparison.OrdinalIgnoreCase)
            || name.Contains("КОМПАС", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ascon", StringComparison.OrdinalIgnoreCase);
    }
}

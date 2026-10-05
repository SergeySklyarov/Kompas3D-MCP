using System.Runtime.InteropServices;

namespace KompasMcp.Api5Adapter;

/// <summary>The few Win32 calls the adapter needs. COM gives no way to ask an object which process serves
/// it (proved in P0.4: <c>KompasObject</c> exposes no PID, path or version-of-process member), so
/// the main window handle that <c>ksGetHWindow()</c> returns is converted to a PID here.</summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>Whether Windows shows the window on screen. Needed because the application's COM object
    /// does not distinguish a window's existence from its visibility: MEASURED — a hidden KOMPAS window
    /// still yields a valid <c>ksGetHWindow()</c>, so "a PID is obtained from the HWND" says nothing
    /// about visibility.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Child windows (in MDI these are document windows) — so a document's visibility can be
    /// checked, not only the application frame.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>Titles of the visible child windows of this window. An empty list is the observation
    /// "there are no visible child windows", not "the check failed": an enumeration error returns null.</summary>
    public static List<string>? VisibleChildWindowTitles(IntPtr parent)
    {
        if (parent == IntPtr.Zero)
        {
            return null;
        }

        var titles = new List<string>();
        var enumerated = false;
        try
        {
            enumerated = EnumChildWindows(parent, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return true;
                }

                var buffer = new System.Text.StringBuilder(length + 1);
                if (GetWindowText(hwnd, buffer, buffer.Capacity) > 0)
                {
                    titles.Add(buffer.ToString());
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            return null;
        }

        return enumerated ? titles : null;
    }

    /// <summary>Current process bitness, for the BITNESS_MISMATCH check at startup.</summary>
    public static string CurrentProcessBitness() => Environment.Is64BitProcess ? "x64" : "x86";
}

/// <summary>Unit selectors for the measurement calls, and the one conversion the server performs.</summary>
/// <remarks>In API5 the unit is an <b>argument</b>, not a model property: <c>GetLength(bitVector)</c>,
/// <c>GetArea(bitVector)</c>, <c>CalcMassInertiaProperties(lengthBits | massBits)</c>. Values are
/// <c>ST_MIX_SM=0, ST_MIX_MM=1, ST_MIX_DM=2, ST_MIX_M=3</c> (length) and <c>ST_MIX_GR=0, ST_MIX_KG=16</c>
/// (mass), from <c>KAPITypes.ldefin2d</c>. Coordinates have no unit argument — they are model millimetres, so <c>GetPoint</c>,
/// <c>GetGabarit</c>, sketch input and transform output need no conversion. <c>0</c> is the <i>centimetre</i>
/// selector, outside the documented interval <c>[ST_MIX_MM..ST_MIX_M]</c>: the default silently returns cm —
/// the 10× discrepancy of spec 4.5 (P0.7: 100 mm reported as 10). History: docs/decisions/adapter-core.md#kompas-units</remarks>
public static class KompasUnits
{
    public const int Centimetres = 0;

    public const int Millimetres = 1;

    public const int Decimetres = 2;

    public const int Metres = 3;

    public const int Grams = 0;

    public const int Kilograms = 16;

    /// <summary>The only selector the server uses for lengths and areas.</summary>
    public const int LengthMm = Millimetres;

    /// <summary>Volume/area in mm³/mm² with mass in kg.</summary>
    public const int MassMmKg = Millimetres | Kilograms;

    /// <summary>Same measurement in metres, used by the unit self-test to prove the scale law.</summary>
    public const int MassMKg = Metres | Kilograms;

    /// <summary>mm³ from a value measured with <see cref="MassMmKg"/>: identity, kept explicit on purpose.</summary>
    public static double VolumeToCubicMillimetres(double value, int selector) => selector switch
    {
        MassMmKg => value,
        MassMKg => value * 1_000_000_000d,
        _ => throw new NotSupportedException($"Селектор объёма 0x{selector:X} не калиброван — значение не выдаётся."),
    };
}

/// <summary>Selectors telling an extrusion which body to act on. Both enumerations are the vendor's own
/// (<c>ksChooseType</c>, <c>ksChooseBodiesType</c> from <c>Interop.Kompas6Constants3D.dll</c>); values copied by
/// probe P2.6, not guessed — the members are bare <c>Int32</c>, so a wrong number silently means something else.</summary>
/// <remarks>Measured from <c>docs/acceptance/p2/p2-probe-report.md</c> step P2.6: <see cref="Bodies"/> is what the
/// kernel consults (3 reads back 3; 0 is clamped to 1, not a valid <c>ksChooseType</c>; unset default 1);
/// <see cref="Parts"/> is not decorative (parts-only, no parts: <c>Create</c> true, no volume lost);
/// <see cref="NewBody"/> on a boss created an extra body over an existing one (2→3), so only <see cref="ManualEditing"/>
/// is written. History: docs/decisions/adapter-core.md#kompas-choose</remarks>
public static class KompasChoose
{
    /// <summary><c>ksChooseType.ksChBodiesAndParts</c> — the vendor default.</summary>
    public const int BodiesAndParts = 1;

    /// <summary><c>ksChooseType.ksChParts</c> — parts only, no bodies consulted.</summary>
    public const int Parts = 2;

    /// <summary><c>ksChooseType.ksChBodies</c> — the value that makes the body list authoritative.</summary>
    public const int Bodies = 3;

    /// <summary><c>ksChooseBodiesType.ksNewBody</c> — creates a body; never written by this server.</summary>
    public const int NewBody = 0;

    /// <summary><c>ksChooseBodiesType.ksAutomaticDefinition</c> — the value read back by default.</summary>
    public const int AutomaticDefinition = 1;

    /// <summary><c>ksChooseBodiesType.ksManualEditing</c> — honour exactly the bodies offered.</summary>
    public const int ManualEditing = 2;

    /// <summary><c>ksChooseBodiesType.ksAllBodies</c>.</summary>
    public const int AllBodies = 3;
}

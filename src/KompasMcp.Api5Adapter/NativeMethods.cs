using System.Runtime.InteropServices;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// The few Win32 calls the adapter needs. COM gives no way to ask an object which process serves
/// it (proved in P0.4: <c>KompasObject</c> exposes no PID, path or version-of-process member), so
/// the main window handle that <c>ksGetHWindow()</c> returns is converted to a PID here.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Показывает ли Windows окно на экране. Нужно потому, что COM-объект приложения existence
    /// окна не отличает от его видимости: измерено, что скрытое окно КОМПАСа даёт валидный
    /// <c>ksGetHWindow()</c>, то есть «PID достаётся по HWND» о видимости не говорит ничего.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Дочерние окна (в MDI это окна документов) — чтобы проверять видимость документа, а не только рамки приложения.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Заголовки видимых дочерних окон данного окна. Пустой список — это наблюдение «видимых
    /// дочерних окон нет», а не «проверка не сработала»: ошибка перечисления возвращает null.
    /// </summary>
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

/// <summary>
/// Unit selectors for the measurement calls, and the one conversion the server performs.
/// </summary>
/// <remarks>
/// In API5 the unit is an <b>argument</b>, not a property of the model:
/// <c>GetLength(bitVector)</c>, <c>GetArea(bitVector)</c>,
/// <c>CalcMassInertiaProperties(lengthBits | massBits)</c>. The selector values are
/// <c>ST_MIX_SM=0, ST_MIX_MM=1, ST_MIX_DM=2, ST_MIX_M=3</c> for length and
/// <c>ST_MIX_GR=0, ST_MIX_KG=16</c> for mass (from <c>KAPITypes.ldefin2d</c>).
///
/// Two consequences that the whole adapter follows:
/// <list type="bullet">
/// <item>Coordinates have no unit argument: they are model millimetres, so <c>GetPoint</c>,
/// <c>GetGabarit</c>, sketch input and transform output need no conversion at all.</item>
/// <item><c>0</c> is the <i>centimetre</i> selector and is outside the documented interval
/// <c>[ST_MIX_MM..ST_MIX_M]</c>. Leaving the argument at its default silently returns cm — which
/// is the 10× discrepancy the historical scripts recorded (spec 4.5) and what P0.7 reproduced on a
/// real edge (100 mm reported as 10). This class exists so no call site can repeat that mistake.</item>
/// </list>
/// </remarks>
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

/// <summary>
/// Selectors that tell an extrusion which body it must act on. The two enumerations are the
/// vendor's own (<c>ksChooseType</c> and <c>ksChooseBodiesType</c>, both from
/// <c>Interop.Kompas6Constants3D.dll</c>) and their values were copied from that assembly by probe
/// P2.6, not guessed: the members are bare <c>Int32</c> on the interop interfaces, so a wrong
/// number compiles and silently means something else.
/// </summary>
/// <remarks>
/// Measured behaviour, all of it from <c>docs/acceptance/p2/p2-probe-report.md</c> step P2.6:
/// <list type="bullet">
/// <item><see cref="Bodies"/> is the value the kernel consults the body list with; requested 3 reads
/// back 3 after <c>Create</c>. Requesting 0 is clamped to 1 — 0 is not a valid
/// <c>ksChooseType</c> — and the default when nothing is set is also 1.</item>
/// <item><see cref="Parts"/> is not decorative: with a parts-only selector and no parts in the
/// document, <c>Create</c> returned true and no body lost any volume.</item>
/// <item><see cref="NewBody"/> on a boss made the feature create an additional body even where its
/// profile overlapped an existing one (2 bodies became 3). Nothing in this server wants that, so
/// the only value ever written here is <see cref="ManualEditing"/>.</item>
/// </list>
/// </remarks>
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

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Late-bound (IDispatch) access to a КОМПАС object.
/// </summary>
/// <remarks>
/// Two jobs, deliberately kept apart:
/// <list type="bullet">
/// <item><see cref="Dispid"/> answers "does the <em>running</em> object know this member name at
/// all?" with no interop assembly anywhere in the path. That is the distinction ADR-003 §4 asks for
/// when a wrapper and the product disagree: <c>DISP_E_MEMBERNOTFOUND</c> is a statement about the
/// application, while any other failure is a COM problem and must not be read as "the member is
/// absent".</item>
/// <item><see cref="Get"/> / <see cref="Set"/> read or write it.</item>
/// </list>
///
/// Every API7 interface in <c>kAPI7.tlb</c> derives from <c>IDispatch</c>, so name binding is
/// available no matter how old a given wrapper is. This is not the shipping style — ADR-001 §2
/// requires static <c>typeof(…)</c> lookups in <c>src/</c>, and warns that reflecting over a
/// <c>System.__ComObject</c> has already produced false "member missing" verdicts in this project.
/// Nothing here reflects over the instance: the DISPID comes from the object's own
/// <c>GetIDsOfNames</c>, so a "no" is the object's answer rather than the probe's.
/// </remarks>
internal static class Late
{
    private const ushort DispatchMethod = 1;
    private const ushort DispatchPropertyGet = 2;
    private const ushort DispatchPropertyPut = 4;
    private const ushort DispatchPropertyPutRef = 8;

    private const int DispidPropertyPut = unchecked((int)0xFFFFFFFD);
    private const int DispidPropertyPutRef = unchecked((int)0xFFFFFFFE);

    private const short VtEmpty = 0;
    private const short VtI4 = 3;
    private const short VtBstr = 8;
    private const short VtDispatch = 9;
    private const short VtBool = 11;
    private const short VtUnknown = 13;
    private const short VtR8 = 5;

    /// <summary>
    /// <c>IDispatch</c>, declared locally because <c>System.Runtime.InteropServices.ComTypes</c>
    /// ships <c>ITypeLib</c>/<c>ITypeInfo</c>/<c>ITypeComp</c> but not <c>IDispatch</c>. The four
    /// slots below are the published order after IUnknown's three; a wrong order does not fail to
    /// compile, it misroutes the next call COM makes, so it is written once and nowhere else. No
    /// <c>PreserveSig</c>: the runtime turns a failed <c>Invoke</c> into a <c>COMException</c> whose
    /// HResult the probe records.
    /// </summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchNative
    {
        void GetTypeInfoCount(out uint count);

        void GetTypeInfo(uint index, int locale, [MarshalAs(UnmanagedType.Interface)] out ITypeInfo typeInfo);

        void GetIDsOfNames(
            ref Guid interfaceIdentifier,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] names,
            uint nameCount,
            int locale,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] dispatchIds);

        void Invoke(
            int dispatchId,
            ref Guid interfaceIdentifier,
            int locale,
            ushort flags,
            ref DispParams parameters,
            [MarshalAs(UnmanagedType.Struct)] out object? result,
            IntPtr exceptionInfo,
            IntPtr argumentError);
    }

    /// <summary>
    /// DISPPARAMS with raw pointers, not a marshalled <c>object[]</c> field: an LPArray of
    /// <c>UnmanagedType.Struct</c> over reference types is only legal in the restricted legacy
    /// marshaler, and declaring it on the struct surfaced as a <see cref="TypeLoadException"/>
    /// <em>inside</em> the first COM call — which reads like a КОМПАС failure and is not one.
    /// The VARIANT array is therefore built explicitly in <see cref="BuildArguments"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public IntPtr Arguments;

        public IntPtr NamedArguments;

        public int ArgumentCount;

        public int NamedArgumentCount;
    }

    /// <summary>
    /// "dispid=N", "DISP_E_MEMBERNOTFOUND", or the failure. The last two are different answers and
    /// are never collapsed into each other.
    /// </summary>
    public static string Dispid(object comObject, string name)
    {
        try
        {
            return "dispid=" + Id(comObject, name);
        }
        catch (Exception ex)
        {
            return HResult.Of(ex) == HResult.DISP_E_MEMBERNOTFOUND ? "DISP_E_MEMBERNOTFOUND" : HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Какие имена объявляет САМ живой объект — по его собственной информации о типе, которую он
    /// отдаёт через <c>IDispatch::GetTypeInfo</c>. Это третья сторона вопроса, и она не сводится к
    /// двум первым: обёртка (снимок) и установленная библиотека типов могут описывать объект иначе,
    /// чем он сам себя. Измерено 19.09.2026: <c>IBodyReposition.Position</c> отвечает
    /// <c>GetVector</c> dispid=3006 и <c>InitByMatrix3D</c> dispid=3010, тогда как библиотека типов
    /// объявляет эти же имена под 3 и 5, а <c>IPlacement3D</c> (11 объявленных имён) объект не
    /// реализует вовсе. Опрашивать надо объект.
    /// </summary>
    /// <remarks>
    /// Перебор идёт по идентификаторам до <paramref name="max"/> и НЕ останавливается на промахах:
    /// у этих объектов члены лежат в диапазонах 500, 800, 2000, 3000 и 6500, и ранний останов
    /// превратил бы «не смотрел» в «нет».
    /// </remarks>
    public static List<(int MemId, string Name)> MemberNames(object comObject, int max = 8192)
    {
        var result = new List<(int, string)>();
        var dispatch = Dispatch(comObject);
        dispatch.GetTypeInfoCount(out var count);
        if (count == 0)
        {
            return result;
        }

        dispatch.GetTypeInfo(0, 0, out var typeInfo);
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
                    // Незанятый идентификатор.
                }
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(typeInfo);
        }

        return result;
    }

    public static object? Get(object comObject, string name)
    {
        var dispatch = Dispatch(comObject);
        var dispid = Id(comObject, name);
        var iid = Guid.Empty;
        var parameters = new DispParams { Arguments = IntPtr.Zero, NamedArguments = IntPtr.Zero, ArgumentCount = 0, NamedArgumentCount = 0 };
        dispatch.Invoke(dispid, ref iid, 0, DispatchPropertyGet, ref parameters, out var result, IntPtr.Zero, IntPtr.Zero);
        return result;
    }

    public static void Set(object comObject, string name, object? value, bool putRef = false)
    {
        var dispatch = Dispatch(comObject);
        var dispid = Id(comObject, name);
        var iid = Guid.Empty;
        var (block, named) = BuildPutArguments([value]);
        try
        {
            var parameters = new DispParams
            {
                Arguments = block,
                NamedArguments = named,
                ArgumentCount = 1,
                NamedArgumentCount = 1,
            };
            dispatch.Invoke(dispid, ref iid, 0, putRef ? DispatchPropertyPutRef : DispatchPropertyPut, ref parameters, out _, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            FreeArguments(block, 1);
            Marshal.FreeHGlobal(named);
        }
    }

    public static object? Call(object comObject, string name, params object?[] args)
    {
        var dispatch = Dispatch(comObject);
        var dispid = Id(comObject, name);
        var iid = Guid.Empty;
        var block = BuildArguments(args);
        try
        {
            var parameters = new DispParams
            {
                Arguments = block,
                NamedArguments = IntPtr.Zero,
                ArgumentCount = args.Length,
                NamedArgumentCount = 0,
            };
            dispatch.Invoke(dispid, ref iid, 0, DispatchMethod, ref parameters, out var result, IntPtr.Zero, IntPtr.Zero);
            return result;
        }
        finally
        {
            FreeArguments(block, args.Length);
        }
    }

    /// <summary>
    /// A VARIANT is 16 bytes on x64 (two shorts of type/reserved, four bytes, then an eight-byte
    /// payload). Arguments go into rgvarg in REVERSE logical order — that is the documented
    /// convention, and getting it backwards passes the right value to the wrong parameter without
    /// complaining.
    /// </summary>
    private static IntPtr BuildArguments(IReadOnlyList<object?> args)
    {
        if (args.Count == 0)
        {
            return IntPtr.Zero;
        }

        var block = Marshal.AllocHGlobal(16 * args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            WriteVariant(block + (16 * (args.Count - 1 - i)), args[i]);
        }

        return block;
    }

    private static (IntPtr Block, IntPtr Named) BuildPutArguments(object?[] single)
    {
        var block = BuildArguments(single);
        var named = Marshal.AllocHGlobal(4);
        Marshal.WriteInt32(named, DispidPropertyPut);
        return (block, named);
    }

    private static void WriteVariant(IntPtr target, object? value)
    {
        for (var offset = 0; offset < 16; offset += 8)
        {
            Marshal.WriteIntPtr(target, offset, IntPtr.Zero);
        }

        switch (value)
        {
            case null:
                Marshal.WriteInt16(target, VtEmpty);
                return;
            case bool flag:
                Marshal.WriteInt16(target, VtBool);
                Marshal.WriteInt16(target, 8, flag ? (short)-1 : (short)0);
                return;
            case int number:
                Marshal.WriteInt16(target, VtI4);
                Marshal.WriteInt32(target, 8, number);
                return;
            case short small:
                Marshal.WriteInt16(target, VtI4);
                Marshal.WriteInt32(target, 8, small);
                return;
            case double real:
                Marshal.WriteInt16(target, VtR8);
                Marshal.WriteInt64(target, 8, BitConverter.DoubleToInt64Bits(real));
                return;
            case string text:
                Marshal.WriteInt16(target, VtBstr);
                Marshal.WriteIntPtr(target, 8, Marshal.StringToBSTR(text));
                return;
            default:
                // A COM object. IUnknown is handed over and the callee QueryInterfaces for whatever
                // interface its signature wants — the same thing the typed marshaler would do.
                var pointer = Marshal.GetIUnknownForObject(value);
                Marshal.WriteInt16(target, VtUnknown);
                Marshal.WriteIntPtr(target, 8, pointer);

                // The VARIANT owns one reference; the local one is released so repeated calls do not
                // leak a reference per call.
                Marshal.Release(pointer);
                return;
        }
    }

    private static void FreeArguments(IntPtr block, int count)
    {
        if (block == IntPtr.Zero)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            VariantClear(block + (16 * i));
        }

        Marshal.FreeHGlobal(block);
    }

    private static int Id(object comObject, string name)
    {
        var dispatch = Dispatch(comObject);
        var iid = Guid.Empty;
        var ids = new int[1];
        dispatch.GetIDsOfNames(ref iid, [name], 1, 0, ids);
        return ids[0];
    }

    private static IDispatchNative Dispatch(object comObject)
    {
        if (comObject is IDispatchNative direct)
        {
            return direct;
        }

        var unknown = IntPtr.Zero;
        var dispatchPointer = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(comObject);
            var iid = typeof(IDispatchNative).GUID;
            if (Marshal.QueryInterface(unknown, iid, out dispatchPointer) != 0 || dispatchPointer == IntPtr.Zero)
            {
                throw new COMException("Объект не отвечает на QueryInterface(IDispatch): позднего связывания нет.");
            }

            return (IDispatchNative)Marshal.GetObjectForIUnknown(dispatchPointer);
        }
        finally
        {
            if (dispatchPointer != IntPtr.Zero)
            {
                Marshal.Release(dispatchPointer);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int VariantClear(IntPtr variant);
}

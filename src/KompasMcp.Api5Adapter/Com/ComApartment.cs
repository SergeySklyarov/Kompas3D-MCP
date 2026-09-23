using System.Runtime.InteropServices;

namespace KompasMcp.Api5Adapter.Com;

/// <summary>
/// The COM HRESULTs this project reasons about, written as the published literals rather than
/// remembered decimals. Anything not listed here is reported verbatim and treated as an
/// unknown outcome — an unrecognised HRESULT must never be laundered into a retryable error.
/// </summary>
public static class ComHResult
{
    public const int S_OK = 0;

    /// <summary>Call denied because the callee is still parsing input; retry is legitimate.</summary>
    public const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);

    /// <summary>Server rejected the call for retry — the canonical "come back later".</summary>
    public const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

    /// <summary>Call rejected by the message filter.</summary>
    public const int RPC_E_SERVERCALL_FILTERING = unchecked((int)0x8001010B);

    /// <summary>Out-of-process server too busy to accept the call.</summary>
    public const int RPC_E_SERVERBUSY = unchecked((int)0x800101DD);

    /// <summary>The object has disconnected: the reference is dead and the call outcome is unknowable.</summary>
    public const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);

    /// <summary>The server process died.</summary>
    public const int RPC_E_SERVERDIED = unchecked((int)0x80010110);

    /// <summary>Server process could not be started.</summary>
    public const int CO_E_SERVER_EXEC_FAILURE = unchecked((int)0x80080005);

    /// <summary>ProgID could not be parsed.</summary>
    public const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);

    /// <summary>Class not registered.</summary>
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    /// <summary>Late-bound member not found — normally an interface/version mismatch.</summary>
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);

    public static bool IsBusy(int code) =>
        code is RPC_E_CALL_REJECTED
            or RPC_E_SERVERCALL_RETRYLATER
            or RPC_E_SERVERCALL_FILTERING
            or RPC_E_SERVERBUSY;

    public static bool IsDisconnected(int code) => code is RPC_E_DISCONNECTED or RPC_E_SERVERDIED;

    public static bool IsRegistrationFailure(int code) =>
        code is CO_E_CLASSSTRING or REGDB_E_CLASSNOTREG;

    public static string Name(int code) => code switch
    {
        RPC_E_CALL_REJECTED => "RPC_E_CALL_REJECTED",
        RPC_E_SERVERCALL_RETRYLATER => "RPC_E_SERVERCALL_RETRYLATER",
        RPC_E_SERVERCALL_FILTERING => "RPC_E_SERVERCALL_FILTERING",
        RPC_E_SERVERBUSY => "RPC_E_SERVERBUSY",
        RPC_E_DISCONNECTED => "RPC_E_DISCONNECTED",
        RPC_E_SERVERDIED => "RPC_E_SERVERDIED",
        CO_E_SERVER_EXEC_FAILURE => "CO_E_SERVER_EXEC_FAILURE",
        CO_E_CLASSSTRING => "CO_E_CLASSSTRING",
        REGDB_E_CLASSNOTREG => "REGDB_E_CLASSNOTREG",
        DISP_E_MEMBERNOTFOUND => "DISP_E_MEMBERNOTFOUND",
        _ => $"0x{code:X8}",
    };

    /// <summary>Extract the HRESULT from an exception, or null when it carries none.</summary>
    public static int? From(Exception ex) => ex switch
    {
        COMException com => com.HResult,
        InvalidComObjectException => unchecked((int)0x80131501),
        _ => null,
    };
}

/// <summary>
/// Declaration of <c>IOleMessageFilter</c> exactly as published in oleidl.h: five methods in
/// this order, IUnknown-derived, no IDispatch. The order is part of the contract — a wrong
/// vtable layout does not fail to compile, it misroutes the next call COM makes on us.
/// </summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter
{
    [PreserveSig]
    int HandlePendingCall(uint uMsgRejected);

    [PreserveSig]
    int HandlePendingTimeout(uint uTimeout);

    [PreserveSig]
    int HandleNotImplemented();

    [PreserveSig]
    int RetryRejectedCall(IntPtr hwnd, uint ulRejectWhy, uint ulMilliSecOut);

    [PreserveSig]
    int MessagePending(IntPtr hwnd, uint ulPendingType, out uint ulReject);
}

/// <summary>
/// Message filter installed on the Worker's STA thread so a busy КОМПАС is handled by COM's own
/// retry mechanism — and every retry is counted. Unbounded silent retry would turn a wedged
/// КОМПАС into an unresponsive Worker, which spec 1.6 forbids.
/// </summary>
/// <remarks>
/// Return values follow the documented convention: <c>RetryRejectedCall</c> returns a delay in
/// milliseconds to retry, 0 to cancel the call, or -1 to let the default (fail) apply.
/// <c>MessagePending</c> sets <c>ulReject</c> to <c>SERVERCALL_RETRYLATER</c> (retry) or
/// <c>SERVERCALL_REJECTED</c> (fail).
/// </remarks>
public sealed class ComMessageFilter : IOleMessageFilter
{
    /// <summary>Calls rejected more often than this are a wedged server, not a transient hiccup.</summary>
    public const int MaxRetriesPerCall = 5;

    public const uint RetryDelayMs = 100;

    // SERVERCALL_ values from wtypes.h, used as MessagePending's out-parameter.
    public const uint SERVERCALL_REJECTED = 1;
    public const uint SERVERCALL_RETRYLATER = 2;

    private long _pendingCalls;
    private long _retriesGranted;
    private long _retriesDenied;
    private long _notImplemented;

    public long PendingCalls => Interlocked.Read(ref _pendingCalls);

    public long RetriesGranted => Interlocked.Read(ref _retriesGranted);

    public long RetriesDenied => Interlocked.Read(ref _retriesDenied);

    public int HandlePendingCall(uint uMsgRejected) => ComHResult.S_OK;

    public int HandlePendingTimeout(uint uTimeout) => ComHResult.S_OK;

    public int HandleNotImplemented()
    {
        Interlocked.Increment(ref _notImplemented);
        return ComHResult.S_OK;
    }

    public int RetryRejectedCall(IntPtr hwnd, uint ulRejectWhy, uint ulMilliSecOut)
    {
        Interlocked.Increment(ref _pendingCalls);

        if (!ComHResult.IsBusy(unchecked((int)ulRejectWhy)))
        {
            Interlocked.Increment(ref _retriesDenied);
            return -1;
        }

        if (ulMilliSecOut > MaxRetriesPerCall)
        {
            Interlocked.Increment(ref _retriesDenied);
            return 0;
        }

        Interlocked.Increment(ref _retriesGranted);
        return (int)RetryDelayMs;
    }

    public int MessagePending(IntPtr hwnd, uint ulPendingType, out uint ulReject)
    {
        Interlocked.Increment(ref _pendingCalls);

        // ulPendingType carries the retry attempt number; beyond the budget we fail fast so the
        // caller can report COM_BUSY instead of hanging.
        if (ulPendingType > MaxRetriesPerCall)
        {
            ulReject = SERVERCALL_REJECTED;
            Interlocked.Increment(ref _retriesDenied);
            return ComHResult.S_OK;
        }

        ulReject = SERVERCALL_RETRYLATER;
        Interlocked.Increment(ref _retriesGranted);
        return ComHResult.S_OK;
    }

    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>
    {
        ["pending_calls"] = PendingCalls,
        ["retries_granted"] = RetriesGranted,
        ["retries_denied"] = RetriesDenied,
        ["not_implemented"] = Interlocked.Read(ref _notImplemented),
    };
}

/// <summary>
/// COM apartment bootstrap for the Worker's single STA thread: initialise the apartment, install
/// the message filter, and release RCWs only where this layer owns them.
/// </summary>
public static class ComApartment
{
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint COINIT_SPEED_OVER_MEMORY = 0x8;

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    /// <summary>Initialise the calling thread as a COM STA with <paramref name="filter"/> installed.</summary>
    public static IDisposable InitializeSta(ComMessageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_SPEED_OVER_MEMORY);
        CoRegisterMessageFilter(filter, out var previous);
        return new StaCookie(previous);
    }

    /// <summary>
    /// Decrement one RCW's refcount. Only for objects this layer created and has dropped;
    /// never for an object still present in the reference registry (spec 1.6: "не освобождать
    /// совместно используемую RCW до окончания операции").
    /// </summary>
    public static int Release(object? comObject)
    {
        if (comObject is null)
        {
            return 0;
        }

        try
        {
            return Marshal.ReleaseComObject(comObject);
        }
        catch (ArgumentException)
        {
            // Not an RCW: a managed wrapper or a struct-backed value. Nothing to release.
            return 0;
        }
    }

    private sealed class StaCookie : IDisposable
    {
        private readonly IOleMessageFilter? _previous;
        private bool _disposed;

        public StaCookie(IOleMessageFilter? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                CoRegisterMessageFilter(_previous, out _);
            }
            catch (COMException)
            {
                // Best effort — the thread is going away regardless.
            }

            CoUninitialize();
        }
    }
}

using System.Reflection;
using System.Runtime.InteropServices;

namespace KompasMcp.Api7Probe;

/// <summary>
/// One dedicated STA thread with a real Win32 message pump. This is the probe's own apartment —
/// ADR-003 §2 permits the probe to own its STA provided the production Worker is not attached to
/// the same session, which is guaranteed here because the probe launches its own invisible КОМПАС
/// and never connects to the Host/Worker at all.
/// </summary>
/// <remarks>
/// The logic mirrors <c>src/KompasMcp.Api5Adapter/Sta/StaExecutor.cs</c> rather than importing it:
/// <c>BlockingCollection.Take</c> is not enough because КОМПАС is an out-of-process local server
/// that can call back into the apartment while this thread sits inside an outgoing call. The loop
/// waits on work <em>and</em> window messages together (<c>MsgWaitForMultipleObjectsEx</c> with
/// <c>MWMO_INPUTAVAILABLE</c>) and then drains messages, so neither starves the other.
/// </remarks>
internal sealed class StaPump : IDisposable
{
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint WAIT_OBJECT_0 = 0x0000_0000;
    private const int WaitSliceMs = 250;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    private readonly Queue<Action> _work = new();
    private readonly ManualResetEvent _queued = new(false);
    private readonly ManualResetEvent _shutdown = new(false);
    private readonly object _sync = new();
    private readonly Thread _thread;
    private long _executed;
    private long _pumped;
    private volatile bool _disposed;

    public StaPump(string name = "api7-probe-sta")
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        _thread = new Thread(Run) { IsBackground = true, Name = name };
        if (!_thread.TrySetApartmentState(ApartmentState.STA))
        {
            throw new InvalidOperationException("Не удалось перевести поток в STA: COM-поток невозможен.");
        }
    }

    public int StaThreadId { get; private set; }

    public bool IsOnStaThread => StaThreadId == Environment.CurrentManagedThreadId;

    public long Executed => Interlocked.Read(ref _executed);

    public long MessagesPumped => Interlocked.Read(ref _pumped);

    public void Start() => _thread.Start();

    /// <summary>
    /// Run <paramref name="work"/> on the STA thread and wait for it. Waiting happens on a pooled
    /// wait handle (not <c>WaitHandle.WaitOne</c> on the caller's own thread affinity), so a long
    /// COM call inside <paramref name="work"/> still gets its callbacks pumped here.
    /// </summary>
    public T Run<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _work.Enqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            _queued.Set();
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Run(Action work) => Run<object?>(() =>
    {
        work();
        return null;
    });

    private void Run()
    {
        StaThreadId = Environment.CurrentManagedThreadId;
        CoInitializeEx(IntPtr.Zero, 0x2 /* APARTMENTTHREADED */ | 0x8 /* SPEED_OVER_MEMORY */);
        var handles = new[] { _shutdown.SafeWaitHandle.DangerousGetHandle(), _queued.SafeWaitHandle.DangerousGetHandle() };
        try
        {
            while (true)
            {
                Drain();
                if (_disposed || _shutdown.WaitOne(0))
                {
                    Drain();
                    return;
                }

                MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, WaitSliceMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (_queued.WaitOne(0))
                {
                    _queued.Reset();
                }

                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    Interlocked.Increment(ref _pumped);
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action? item;
            lock (_sync)
            {
                item = _work.Count == 0 ? null : _work.Dequeue();
            }

            if (item is null)
            {
                return;
            }

            Interlocked.Increment(ref _executed);
            item();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Set();
        _queued.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            Console.Error.WriteLine("[api7-probe] STA-поток не завершился за 5 с — вероятен зависший COM-вызов.");
        }

        _queued.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// COM HRESULTs the probe reasons about, written as literals. Mirrors the shipping adapter's
/// table so a code is described the same way in both places; kept local for the same
/// no-src-reference reason as <see cref="StaPump"/>.
/// </summary>
internal static class HResult
{
    public const int S_OK = 0;
    public const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    public const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
    public const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int NO_ERROR = 0;

    public static string Name(int code) => code switch
    {
        S_OK => "S_OK",
        RPC_E_CALL_REJECTED => "RPC_E_CALL_REJECTED",
        RPC_E_SERVERCALL_RETRYLATER => "RPC_E_SERVERCALL_RETRYLATER",
        RPC_E_DISCONNECTED => "RPC_E_DISCONNECTED",
        DISP_E_MEMBERNOTFOUND => "DISP_E_MEMBERNOTFOUND",
        E_INVALIDARG => "E_INVALIDARG",
        E_NOTIMPL => "E_NOTIMPL",
        _ => "0x" + code.ToString("X8"),
    };

    public static int? Of(Exception ex) => ex switch
    {
        COMException com => com.HResult,
        TargetInvocationException tie when tie.InnerException is not null => Of(tie.InnerException),
        _ => null,
    };

    /// <summary>One line describing an exception, with its HRESULT when it carries one.</summary>
    public static string Describe(Exception ex)
    {
        var inner = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
        var hr = Of(inner);
        return inner.GetType().Name
                   + (inner.Message.Length == 0 ? string.Empty : ": " + Flatten(inner.Message))
                   + (hr is int code ? " [" + Name(code) + "]" : string.Empty);
    }

    private static string Flatten(string text) =>
        text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}

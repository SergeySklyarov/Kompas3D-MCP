using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using KompasMcp.Api5Adapter.Com;

namespace KompasMcp.Api5Adapter.Sta;

/// <summary>
/// One dedicated STA thread with a real Win32 message pump, plus the single work queue that all
/// COM access must go through (spec 1.6: «Все COM-ссылки создаются, используются и освобождаются
/// в одном выделенном STA… Message pump обязателен»).
/// </summary>
/// <remarks>
/// Why a pump and not <c>BlockingCollection.Take</c>: КОМПАС is an out-of-process local server.
/// While this thread blocks inside an outgoing call, КОМПАС can call back into the apartment
/// (events, aggregation, the message filter). Without a message loop those callbacks are never
/// delivered, which is how a «hung COM call» usually starts. The loop drains posted work, then
/// waits on work <em>and</em> window messages together via <c>MsgWaitForMultipleObjectsEx</c>, so
/// neither starves the other.
///
/// A running item cannot be cancelled from here — an in-flight COM call is not abortable from the
/// client side. Queued-but-unstarted items are dropped on cancellation; started ones keep their
/// caller waiting until the operation budget expires (spec 1.8: running cancel is best effort).
/// </remarks>
public sealed class StaExecutor : IDisposable
{
    private const int WaitSliceMs = 250;

    private static readonly IntPtr[] NoHandles = Array.Empty<IntPtr>();

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly EventWaitHandle _workArrived = new(false, EventResetMode.AutoReset);
    private readonly ManualResetEvent _shutdown = new(false);
    private readonly Thread _thread;
    private readonly ComMessageFilter _filter = new();
    private readonly IntPtr[] _waitHandlesRaw;

    private long _threadId;
    private long _executed;
    private long _messagesPumped;
    private long _droppedOnCancel;
    private volatile bool _disposed;

    public string Name { get; }

    public StaExecutor(string name = "kompas-sta")
    {
        Name = name;
        _thread = new Thread(RunThread)
        {
            IsBackground = true,
            Name = name,
        };

        // Must be set before the thread starts: after that the apartment cannot be changed.
        if (!_thread.TrySetApartmentState(ApartmentState.STA))
        {
            throw new InvalidOperationException("Не удалось перевести поток в STA: COM-поток невозможен.");
        }

        _waitHandlesRaw = new[] { _shutdown.SafeWaitHandle.DangerousGetHandle(), _workArrived.SafeWaitHandle.DangerousGetHandle() };
    }

    /// <summary>Managed thread id of the only thread allowed to touch КОМПАС (0 before Start).</summary>
    public int StaThreadId => (int)Interlocked.Read(ref _threadId);

    public bool IsOnStaThread => Volatile.Read(ref _threadId) == Environment.CurrentManagedThreadId;

    public ComMessageFilter MessageFilter => _filter;

    public bool IsStarted => Volatile.Read(ref _threadId) != 0;

    public IReadOnlyDictionary<string, long> Statistics() => new Dictionary<string, long>
    {
        ["sta_thread_id"] = StaThreadId,
        ["executed"] = Interlocked.Read(ref _executed),
        ["messages_pumped"] = Interlocked.Read(ref _messagesPumped),
        ["dropped_on_cancel"] = Interlocked.Read(ref _droppedOnCancel),
        ["queued"] = _queue.Count,
    };

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
    }

    /// <summary>
    /// Queue <paramref name="work"/> for the STA thread and await its result. The returned task
    /// carries the original exception so the caller can inspect its HRESULT.
    /// </summary>
    public Task<T> Run<T>(Func<T> work, string label = "work", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new WorkItem(
            label,
            cancellationToken,
            cancel: () => tcs.TrySetCanceled(cancellationToken),
            invoke: () =>
            {
                try
                {
                    tcs.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));

        _workArrived.Set();
        return tcs.Task;
    }

    public Task Run(Action work, string label = "work", CancellationToken cancellationToken = default) =>
        Run<object?>(() =>
        {
            work();
            return null;
        }, label, cancellationToken);

    /// <summary>Queue work without waiting (shutdown cleanup, best-effort releases).</summary>
    public void Post(Action work, string label = "post")
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(new WorkItem(label, CancellationToken.None, cancel: () => { }, invoke: () =>
        {
            try
            {
                work();
            }
            catch
            {
                // Nobody is awaiting posted work; killing the pump would be far worse than
                // losing a cleanup step.
            }
        }));

        _workArrived.Set();
    }

    private void RunThread()
    {
        Interlocked.Exchange(ref _threadId, Environment.CurrentManagedThreadId);
        using (ComApartment.InitializeSta(_filter))
        {
            SynchronizationContext.SetSynchronizationContext(new StaSynchronizationContext(this));
            RunLoop();
        }
    }

    private void RunLoop()
    {
        while (true)
        {
            Drain();

            var exiting = _disposed || _shutdown.WaitOne(0);
            if (exiting)
            {
                // One more pass so no awaiter is left hanging after we were asked to stop.
                Drain();
                return;
            }

            var signaled = Native.MsgWaitForMultipleObjectsEx(
                (uint)_waitHandlesRaw.Length,
                _waitHandlesRaw,
                WaitSliceMs,
                Native.QS_ALLINPUT,
                Native.MWMO_INPUTAVAILABLE);

            if (signaled == Native.WAIT_OBJECT_0 + 1)
            {
                _workArrived.Reset();
            }

            PumpMessages();
        }
    }

    private void Drain()
    {
        while (_queue.TryDequeue(out var item))
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _droppedOnCancel);
                item.Cancel();
                continue;
            }

            Interlocked.Increment(ref _executed);
            item.Invoke();
        }
    }

    /// <summary>
    /// Non-blocking message pump. Also services cross-apartment calls addressed to this thread,
    /// which is the whole reason it is here.
    /// </summary>
    private void PumpMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Interlocked.Increment(ref _messagesPumped);
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
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
        _workArrived.Set();

        if (IsStarted && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // A COM call can outlive any Join we are willing to block on. The thread is a
            // background thread so the process can still exit; say so instead of pretending the
            // apartment was torn down cleanly.
            System.Diagnostics.Trace.WriteLine(
                $"StaExecutor '{Name}': поток не завершился за 5 с — вероятен зависший COM-вызов.");
        }

        _workArrived.Dispose();
        _shutdown.Dispose();
    }

    private readonly struct WorkItem
    {
        public WorkItem(string label, CancellationToken cancellationToken, Action cancel, Action invoke)
        {
            Label = label;
            CancellationToken = cancellationToken;
            CancelAction = cancel;
            InvokeAction = invoke;
        }

        public string Label { get; }

        public CancellationToken CancellationToken { get; }

        private Action CancelAction { get; }

        private Action InvokeAction { get; }

        public void Cancel() => CancelAction();

        public void Invoke() => InvokeAction();
    }

    /// <summary>Keeps accidental <c>await</c> continuations on the STA thread.</summary>
    private sealed class StaSynchronizationContext : SynchronizationContext
    {
        private readonly StaExecutor _owner;

        public StaSynchronizationContext(StaExecutor owner) => _owner = owner;

        public override void Post(SendOrPostCallback d, object? state) => _owner.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_owner.IsOnStaThread)
            {
                d(state);
                return;
            }

            _owner.Run(() =>
            {
                d(state);
                return (object?)null;
            }, "sync-send").GetAwaiter().GetResult();
        }
    }

    private static class Native
    {
        public const uint PM_REMOVE = 0x0001;

        public const uint QS_ALLINPUT = 0x04FF;

        public const uint MWMO_INPUTAVAILABLE = 0x0004;

        public const uint WAIT_OBJECT_0 = 0x00000000;

        public const uint WAIT_TIMEOUT = 0x00000102;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
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
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint MsgWaitForMultipleObjectsEx(
            uint nCount,
            IntPtr[] pHandles,
            uint dwMilliseconds,
            uint dwWakeMask,
            uint dwFlags);
    }
}

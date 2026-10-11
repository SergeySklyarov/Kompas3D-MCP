namespace KompasMcp.Domain.Com;

/// <summary>What the latch recorded: enough for a refusal to name the failed call, the code and its name.</summary>
public sealed record ComDisconnectObservation(
    int HResult,
    string Name,
    string ExceptionType,
    string Message,
    DateTimeOffset AtUtc);

/// <summary>Session-level latch: the first connection loss observed DURING A SESSION COMMAND marks the
/// session disconnected until a successful reconnect.</summary>
/// <remarks>MEASURED: ~290 local catches over <c>COMException</c> answer a default (an empty list,
/// -1, null) — right for one failed read, wrong for a lost connection: each turns the loss into
/// "empty" and the command answers success. One observer feeds the latch from
/// <c>AppDomain.FirstChanceException</c>, which sees the exception where it is THROWN, before any
/// catch swallows it; a READ built while the latch is set is refused. INVARIANT: a MUTATION's
/// classification is unchanged. History: docs/decisions/adapter-core.md#disconnect-latch</remarks>
public sealed class ComDisconnectLatch
{
    private readonly object _gate = new();
    private ComDisconnectObservation? _observation;
    private string? _clearedReason;
    private int _armedDepth;
    private int _armedThreadId;
    private long _ignoredOutsideSession;

    /// <summary>Whether a connection loss has been observed since the last <see cref="Reset"/>.</summary>
    public bool IsSet
    {
        get
        {
            lock (_gate)
            {
                return _observation is not null;
            }
        }
    }

    /// <summary>The FIRST loss observed, or null. The first one is kept: a later swallowed failure is a
    /// consequence, and the first names the call that actually broke.</summary>
    public ComDisconnectObservation? Observation
    {
        get
        {
            lock (_gate)
            {
                return _observation;
            }
        }
    }

    /// <summary>Why the mark was lifted WITHOUT a reconnect, or null. Named so the lifting is not silent:
    /// it happens because OUR application answered a cheap call, which means the loss the observer saw
    /// belonged to a FOREIGN object (a stale ROT entry of another instance).</summary>
    public string? ClearedReason
    {
        get
        {
            lock (_gate)
            {
                return _clearedReason;
            }
        }
    }

    /// <summary>How many connection-class exceptions were seen OUTSIDE a session command and therefore NOT
    /// applied. A non-zero count is the evidence that a foreign loss was seen and deliberately ignored —
    /// without it "the mark was not set" and "nothing was seen" are the same observation.</summary>
    /// <remarks>MEASURED, and this is why the count is taken ONLY when the window is shut: an exception
    /// thrown on the STA thread inside a command is RE-THROWN on the awaiting thread, so the same loss
    /// reaches the observer twice and counting the second sighting inflated the counter with our own
    /// losses. A loss on another thread while the window is OPEN is neither recorded nor counted.
    /// History: docs/decisions/adapter-core.md#disconnect-latch</remarks>
    public long IgnoredOutsideSession => Interlocked.Read(ref _ignoredOutsideSession);

    /// <summary>Open the observation window for one session command. Nested-safe: the window closes when
    /// the last command that opened it closes its own.</summary>
    /// <param name="staThreadId">The thread that runs KOMPAS calls. Only exceptions thrown THERE belong to
    /// this session: a control-lane command enumerates the ROT on its own thread, and binding a stale
    /// entry of a DEAD FOREIGN instance raises a connection loss that is not ours.</param>
    public void Arm(int staThreadId)
    {
        lock (_gate)
        {
            _armedThreadId = staThreadId;
        }

        Interlocked.Increment(ref _armedDepth);
    }

    /// <summary>Close one observation window.</summary>
    public void Disarm()
    {
        while (true)
        {
            var current = Volatile.Read(ref _armedDepth);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _armedDepth, current - 1, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Record the exception when it is a connection loss OF THIS SESSION. Returns whether it was
    /// recorded, so a caller that wants to rethrow can decide from the same predicate the latch uses.</summary>
    public bool Observe(Exception? ex)
    {
        if (ex is null || !ComHResult.IsDisconnected(ex))
        {
            return false;
        }

        if (Volatile.Read(ref _armedDepth) <= 0)
        {
            // No session command is running: this loss belongs to a COM object that is NOT ours.
            Interlocked.Increment(ref _ignoredOutsideSession);
            return false;
        }

        if (!IsOnTheSessionThread())
        {
            // The window is open but the exception came from another thread: either the same loss
            // re-thrown on the awaiting thread, or a concurrent control-lane call. Not a foreign object
            // seen outside a session, so it is neither applied nor counted.
            return false;
        }

        lock (_gate)
        {
            _observation ??= Describe(ex);
        }

        return true;
    }

    /// <summary>Clear the mark because a session command succeeded (a reconnect): the session it described
    /// is gone.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _observation = null;
            _clearedReason = null;
        }
    }

    /// <summary>Clear the mark for a NAMED reason that is not a reconnect — OUR application answered a
    /// cheap call, so the loss belonged to a foreign object. The reason is kept and logged; a silent
    /// clearing would hide the very event the latch exists to report.</summary>
    public void ClearWithReason(string reason)
    {
        lock (_gate)
        {
            _observation = null;
            _clearedReason = reason;
        }
    }

    private bool IsOnTheSessionThread()
    {
        int armedThreadId;
        lock (_gate)
        {
            armedThreadId = _armedThreadId;
        }

        // A zero means the apartment was not started yet: nothing can be thrown from it, so an
        // exception in hand cannot be attributed to a foreign call and is treated as ours.
        return armedThreadId == 0 || Environment.CurrentManagedThreadId == armedThreadId;
    }

    private static ComDisconnectObservation Describe(Exception ex)
    {
        var code = ComHResult.From(ex);
        return new ComDisconnectObservation(
            code ?? 0,
            code is int named ? ComHResult.Name(named) : "не HRESULT",
            ex.GetType().Name,
            ex.Message,
            DateTimeOffset.UtcNow);
    }
}

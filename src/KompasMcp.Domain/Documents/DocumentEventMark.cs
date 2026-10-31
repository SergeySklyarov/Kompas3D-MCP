namespace KompasMcp.Domain.Documents;

/// <summary>Mark raised by a document event handler and consumed before the next command: the only thing the
/// handler is allowed to do.</summary>
/// <remarks>INVARIANT: raising a mark calls no COM and touches no document - the worker runs on one STA thread
/// and a call from a handler re-enters the apartment it was called from.
/// MEASURED: the model raises <c>Rebuild</c> both for our own mutation and for a foreign one, and our own
/// arrives within 400 ms of the call, with nothing later; the own window is what tells the two apart.
/// TEST: DocumentEventMarkTests holds the "one bump per burst" and "own is not external" rules.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
public sealed class DocumentEventMark
{
    /// <summary>How long after our own mutation an incoming event is treated as ours. The measured delay is
    /// under 400 ms; the margin covers a loaded machine.</summary>
    /// <remarks>LIMIT: a foreign rebuild that lands inside this window is dropped as if it were ours; the
    /// fingerprint check before the next mutation still sees it.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public const long OwnWindowMs = 1000;

    private int _pending;
    private long _ownUntilMs;

    /// <summary>Events raised but not yet consumed. Read-only view for tests and diagnostics.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Called from the handler. Several events in a row collapse into one mark.</summary>
    public void Raise() => Interlocked.Increment(ref _pending);

    /// <summary>Opens the window in which incoming events are attributed to this session.</summary>
    /// <remarks>INVARIANT: our own mutation always bumps the revision itself, so an event from it must not
    /// bump a second time.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public void OpenOwnWindow(long nowMs)
    {
        Interlocked.Exchange(ref _ownUntilMs, nowMs + OwnWindowMs);
        Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>Takes the mark, or reports that there is nothing to apply.</summary>
    /// <remarks>INVARIANT: one burst of events yields exactly one <c>true</c>; a second call without new
    /// events yields <c>false</c>. An event inside the own window is consumed and discarded.
    /// TEST: DocumentEventMarkTests.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public bool TryTake(long nowMs)
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0)
        {
            return false;
        }

        return nowMs >= Interlocked.Read(ref _ownUntilMs);
    }
}

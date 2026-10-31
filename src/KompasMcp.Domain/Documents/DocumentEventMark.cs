namespace KompasMcp.Domain.Documents;

/// <summary>Mark raised by a document event handler and consumed before the next command: the only thing the
/// handler is allowed to do.</summary>
/// <remarks>INVARIANT: raising a mark calls no COM and touches no document - the worker runs on one STA thread
/// and a call from a handler re-enters the apartment it was called from.
/// MEASURED: the model raises <c>Rebuild</c> both for our own mutation and for a foreign one; one own
/// mutation produces one or two of them, and the same event follows the open of a document.
/// TEST: DocumentEventMarkTests holds the "one bump per burst" and "own is not external" rules.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
public sealed class DocumentEventMark
{
    /// <summary>How many events one own action may raise. Measured: one or two, never three.</summary>
    /// <remarks>LIMIT: the allowance is only a fallback for a burst the in-command pump did not settle; a
    /// foreign rebuild inside it is still taken for ours.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public const int OwnBurst = 2;

    private int _pending;
    private int _ownLeft;
    private int _ownDelivered;

    /// <summary>Events raised but not yet consumed. Read-only view for tests and diagnostics.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Called from the handler. Several events in a row collapse into one mark.</summary>
    /// <remarks>INVARIANT: an event is attributed where it arrives, and the attribution is a count, not a
    /// clock. MEASURED: delivery can lag the call by more than a second, because the pump runs only between
    /// commands, so no window in milliseconds separates our own rebuild from a foreign one.
    /// TEST: DocumentEventMarkTests holds the "late own event" and "own burst" cases.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public void Raise()
    {
        if (Interlocked.Decrement(ref _ownLeft) >= 0)
        {
            Interlocked.Increment(ref _ownDelivered);
            return;
        }

        Interlocked.Increment(ref _pending);
    }

    /// <summary>Opens the burst in which incoming events are attributed to this session: after our own
    /// mutation, and after registering a document, whose open raises the same event.</summary>
    /// <remarks>INVARIANT: our own mutation always bumps the revision itself, so its events must not bump a
    /// second time. Clearing the pending count also covers an event delivered during the mutation itself,
    /// before this call runs.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public void ExpectOwnBurst()
    {
        Interlocked.Exchange(ref _ownLeft, OwnBurst);
        Interlocked.Exchange(ref _ownDelivered, 0);
        Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>Closes the own burst at the end of the command that caused it, once our own events have
    /// already been delivered there.</summary>
    /// <remarks>INVARIANT: the burst must not stay open across the gap between commands. A foreign rebuild
    /// arriving in that gap was dropped as ours and never reached the revision, while the document kept
    /// answering <c>reliable</c> - the change stayed invisible for good.
    /// MEASURED: KOMPAS posts <c>Rebuild</c> while the call is still running, so one non-blocking pump right
    /// after the mutation delivers it inside the same command; what arrives after that is foreign.
    /// LIMIT: when the pump delivered no own event (the call raised none, or delivery lagged), the burst
    /// stays open and the count-based attribution of <see cref="OwnBurst"/> holds instead.
    /// TEST: DocumentEventMarkTests holds both orders.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public bool CloseOwnBurstIfDelivered()
    {
        if (Volatile.Read(ref _ownDelivered) == 0)
        {
            return false;
        }

        Interlocked.Exchange(ref _ownLeft, 0);
        return true;
    }

    /// <summary>Takes the mark, or reports that there is nothing to apply. Ends the burst: whatever did not
    /// arrive before this command started belongs to the next one.</summary>
    /// <remarks>INVARIANT: one burst of events yields exactly one <c>true</c>; a second call without new
    /// events yields <c>false</c>. Whether a mark was ours was decided when the event arrived, so this call
    /// cannot re-classify an event that is already recorded.
    /// TEST: DocumentEventMarkTests.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    public bool TryTake()
    {
        var taken = Interlocked.Exchange(ref _pending, 0) > 0;
        Interlocked.Exchange(ref _ownLeft, 0);
        return taken;
    }
}

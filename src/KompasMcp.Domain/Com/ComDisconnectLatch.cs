namespace KompasMcp.Domain.Com;

/// <summary>What the latch recorded: enough for a refusal to name the failed call, the code and its name.</summary>
public sealed record ComDisconnectObservation(
    int HResult,
    string Name,
    string ExceptionType,
    string Message,
    DateTimeOffset AtUtc);

/// <summary>Session-level latch: the first connection loss seen ANYWHERE in the adapter marks the
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

    /// <summary>Record the exception when it is a connection loss. Returns whether it was one, so a caller
    /// that wants to rethrow can decide from the same predicate the latch uses.</summary>
    public bool Observe(Exception? ex)
    {
        if (ex is null || !ComHResult.IsDisconnected(ex))
        {
            return false;
        }

        lock (_gate)
        {
            _observation ??= Describe(ex);
        }

        return true;
    }

    /// <summary>Clear the mark. Called after a successful reconnect: the session it described is gone.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _observation = null;
        }
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

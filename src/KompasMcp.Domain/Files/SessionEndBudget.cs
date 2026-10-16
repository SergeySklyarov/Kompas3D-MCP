namespace KompasMcp.Domain.Files;

/// <summary>How much time the session-end sequence may still spend on the steps that CAN be skipped.</summary>
/// <remarks>INVARIANT: the whole session-end sequence must finish inside the Host's shutdown window, or
/// the Host kills the Worker before the Worker terminates the PID of the instance it launched - and that
/// PID is the Worker's job, so a Worker killed mid-sequence leaves the instance orphaned with no report.
/// The budget is therefore strictly SHORTER than the window and is spent in a fixed order: rescue copies
/// first (they may be skipped, and the skipped documents are NAMED), the documented close second (it
/// must run, or <c>Quit()</c> would ask about a modified document), <c>Quit()</c> and the exit wait last.
/// DOC: ikompasdocument_close.html. History: docs/decisions/host.md#shutdown-many-docs</remarks>
public sealed class SessionEndBudget
{
    /// <summary>Total time the Worker may spend ending the session.</summary>
    /// <remarks>MEASURED: the Host waits 20 s for the Worker to leave and then kills it. The budget is
    /// shorter than that window with a margin, so the Worker reaches its own exit first - killing is the
    /// worse route and is exactly what leaves the instance behind.</remarks>
    public const int DefaultTotalMs = 15_000;

    /// <summary>Time kept for the documented close, <c>Quit()</c> and the exit wait.</summary>
    /// <remarks>MEASURED: the close of one document costs tens of milliseconds and the whole tail
    /// (close + Quit + exit) is under a second on a normal session; the reserve is a bound on the
    /// pathological case, not a normal wait.</remarks>
    public const int DefaultCloseReserveMs = 5_000;

    private readonly Func<long> _clock;
    private readonly long _startMs;

    public int TotalMs { get; }

    public int CloseReserveMs { get; }

    public SessionEndBudget(Func<long>? clock = null, long? startMs = null)
        : this(DefaultTotalMs, DefaultCloseReserveMs, clock, startMs)
    {
    }

    public SessionEndBudget(int totalMs, int closeReserveMs, Func<long>? clock = null, long? startMs = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalMs, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(closeReserveMs);
        if (closeReserveMs >= totalMs)
        {
            throw new ArgumentOutOfRangeException(nameof(closeReserveMs),
                "резерв на закрытие обязан быть МЕНЬШЕ общего бюджета, иначе спасение запрещено всегда");
        }

        TotalMs = totalMs;
        CloseReserveMs = closeReserveMs;
        _clock = clock ?? (static () => Environment.TickCount64);
        _startMs = startMs ?? _clock();
    }

    public long ElapsedMs => _clock() - _startMs;

    public long RemainingMs => Math.Max(0, TotalMs - ElapsedMs);

    /// <summary>Whether a rescue copy may still be taken. False once the close, <c>Quit()</c> and the
    /// exit wait would no longer fit - the remaining documents are then NAMED instead of copied.</summary>
    public bool CanRescueMore => ElapsedMs < TotalMs - CloseReserveMs;

    /// <summary>Whether the whole budget is spent: no time is left even for the tail, so the sequence
    /// must go straight to terminating its own PID.</summary>
    public bool Exhausted => ElapsedMs >= TotalMs;
}

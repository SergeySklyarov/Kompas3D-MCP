using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Journaling;

/// <summary>Session-owner state as seen by ANY Host sharing this <c>journal_path</c>.</summary>
/// <remarks>
/// INVARIANT: "not ready to take work, but still releasing" (<see cref="Releasing"/>,
/// <see cref="Draining"/>) is a separate state from "may be taken" (<see cref="Released"/>,
/// <see cref="Free"/>). One state for both made a second Host start while the first still held the
/// Worker (MEASURED 04.10.2026). History: docs/decisions/journaling.md#ownership-model
/// </remarks>
public enum HostOwnerState
{
    /// <summary>Ownership taken, but the owner has not yet served a client request.</summary>
    Starting,

    /// <summary>The owner runs the session: it holds the Worker, the journal and the right to CAD calls.</summary>
    Serving,

    /// <summary>The owner is releasing the session: no new CAD calls, resources not yet freed. The exclusive
    /// right stays with it — another Host can NOT take the session.</summary>
    Releasing,

    /// <summary>
    /// Transport finished or the Host is shutting down abnormally: cleanup NOT confirmed. Differs from
    /// <see cref="Releasing"/> only in who initiated it, not in rights.
    /// </summary>
    Draining,

    /// <summary>Ownership removed by an EXPLICIT <c>kompas_release_session</c>, cleanup confirmed. An ordinary
    /// CAD call does NOT take ownership — an explicit <c>kompas_acquire_session</c> is required.</summary>
    Released,

    /// <summary>No owner, cleanup confirmed (transport finished cleanly). An ordinary CAD call takes ownership:
    /// a coordinated admission of a real operation, not catalog discovery.</summary>
    Free,
}

/// <summary>The owner record. Lives next to the journal; read and written under the named lock.</summary>
public sealed record HostOwnerRecord(
    int Pid,
    string Generation,
    HostOwnerState State,
    DateTimeOffset StartedUtc,
    int RequestsServed,
    int? TookOverFromPid,
    DateTimeOffset? ReleasedUtc);

/// <summary>Outcome of an ACQUIRE attempt.</summary>
public enum OwnershipOutcome
{
    /// <summary>Ownership taken by this call; a new generation was created.</summary>
    Acquired,

    /// <summary>This Host is already the owner: no second Worker, no second generation.</summary>
    AlreadyOwned,

    /// <summary>Another LIVE Host owns the session (serving/releasing/draining/starting).</summary>
    RefusedActiveOwner,

    /// <summary>The session is free but cannot be taken by an ordinary CAD call: the previous owner released
    /// EXPLICITLY, so an explicit <c>kompas_acquire_session</c> is required.</summary>
    RefusedExplicitAcquireRequired,

    /// <summary>The owner state could not be determined (record unreadable). Not treated as free.</summary>
    RefusedStateUnknown,

    /// <summary>The named lock or the record could not be obtained.</summary>
    RecordUnavailable,
}

/// <summary>Outcome of a RELEASE attempt.</summary>
public enum ReleaseOutcome
{
    /// <summary>Ownership removed by this call, state <see cref="HostOwnerState.Released"/> written.</summary>
    Released,

    /// <summary>This Host had no ownership: a repeated release is safe and touches no foreign record.</summary>
    AlreadyReleased,

    /// <summary>We are the owner but not in <see cref="HostOwnerState.Serving"/>: nothing to release.</summary>
    NotOwner,

    /// <summary>The owner record could not be updated. NOT counted as success.</summary>
    RecordUnavailable,
}

/// <summary>Observation of ownership without taking it: what <c>kompas_session_status</c> answers and what the
/// refusal decision rests on.</summary>
public sealed record SessionProbe(
    bool RecordExists,
    bool RecordUnreadable,
    bool LockUnavailable,
    HostOwnerState? State,
    int? OwnerPid,
    string? OwnerGeneration,
    int? OwnerRequestsServed,
    bool OwnerAlive,
    string? AliveReason,
    bool SelfOwned,
    bool CanAcquire,
    bool RequiresExplicitAcquire,
    string Reason,
    string? RefusalCode)
{
    /// <summary>How the owner blocking the take differs from a missing one: "releasing" is named separately from
    /// "serving", because their remedies differ.</summary>
    public string Describe() => State switch
    {
        HostOwnerState.Releasing => "releasing",
        HostOwnerState.Draining => "draining",
        HostOwnerState.Serving => "serving",
        HostOwnerState.Starting => "starting",
        _ => "none",
    };
}

/// <summary>Result of an acquire attempt.</summary>
public sealed record Acquisition(
    OwnershipOutcome Outcome,
    string? Generation,
    bool NewGeneration,
    SessionProbe? Refusal,
    string? ErrorCode,
    string? ErrorMessage,
    string? Remedy,
    int? TookOverFromPid,
    bool TookOverFromLiveOwner);

/// <summary>Result of a release attempt.</summary>
public sealed record Release(
    ReleaseOutcome Outcome,
    string? ErrorMessage,
    SessionProbe? Probe);

/// <summary>The single owner of the CAD session per <c>journal_path</c>.</summary>
/// <remarks>INVARIANT: transport start takes NO ownership — only an explicit <c>acquire</c> or the admission of
/// a REAL CAD operation does (<c>tools/list</c>, <c>initialize</c>, <c>health</c> never do).
/// INVARIANT: the owner is identified by pid AND generation; a mismatch refuses the update.
/// INVARIANT: liveness — a process with the pid that started no later than the record; undeterminable
/// counts as ALIVE (a live owner taken for dead gives two Hosts). LIMIT: idleness never strips
/// ownership from a live owner. INVARIANT: the lock is not held during COM.
/// History: docs/decisions/journaling.md#ownership-model</remarks>
public sealed class HostOwnership : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>How long to wait for the named lock when inspecting ownership.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _gate;
    private readonly DateTimeOffset _startedUtc;
    private int _requestsServed;

    private HostOwnership(Mutex gate, string recordPath, string journalPath)
    {
        _gate = gate;
        _startedUtc = DateTimeOffset.UtcNow;
        RecordPath = recordPath;
        JournalPath = journalPath;
    }

    /// <summary>Path of the journal whose ownership is being decided. Used for readable refusal texts.</summary>
    public string JournalPath { get; }

    /// <summary>Path of the owner record — next to the journal, not in the default directory.</summary>
    public string RecordPath { get; }

    /// <summary>Generation of the current acquisition. <c>null</c> — this Host holds no ownership.</summary>
    public string? Generation => Volatile.Read(ref _generation);

    private string? _generation;

    /// <summary>Whether this Host currently holds ownership.</summary>
    public bool IsOwner => Generation is not null;

    /// <summary>How many client requests this Host served in the current generation.</summary>
    public int RequestsServed => Volatile.Read(ref _requestsServed);

    public static string RecordPathFor(string journalPath) => journalPath + ".owner.json";

    /// <summary>Open ownership WITHOUT taking it. Transport start takes no ownership — see the class remarks.</summary>
    public static HostOwnership Open(string journalPath)
    {
        var full = Path.GetFullPath(journalPath);
        var recordPath = RecordPathFor(full);
        var mutex = new Mutex(initiallyOwned: false, name: MutexNameFor(full));
        return new HostOwnership(mutex, recordPath, full);
    }

    /// <summary>Read the ownership state without taking or releasing anything.</summary>
    public SessionProbe Probe()
    {
        var taken = EnterGate(out var lockProblem);
        if (!taken)
        {
            return new SessionProbe(
                RecordExists: false, RecordUnreadable: false, LockUnavailable: true,
                State: null, OwnerPid: null, OwnerGeneration: null, OwnerRequestsServed: null,
                OwnerAlive: false, AliveReason: lockProblem, SelfOwned: false,
                CanAcquire: false, RequiresExplicitAcquire: false,
                Reason: "состояние владения определить не удалось: " + lockProblem,
                RefusalCode: ErrorCodes.OwnershipStateUnknown);
        }

        try
        {
            return Classify(ReadRecord(RecordPath));
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Try to take ownership. Atomic under the named lock: reading the record, deciding and writing the
    /// new state happen inside one acquisition, so two concurrent requests cannot both get the right to
    /// work.</summary>
    /// <param name="explicitRequest">
    /// <c>true</c> — an explicit <c>kompas_acquire_session</c>; <c>false</c> — the coordinated admission
    /// of a real CAD operation. Admission after an EXPLICIT release is forbidden.
    /// </param>
    public Acquisition TryAcquire(bool explicitRequest)
    {
        var taken = EnterGate(out var lockProblem);
        if (!taken)
        {
            return Failure(OwnershipOutcome.RecordUnavailable, ErrorCodes.JournalUnavailable,
                $"Не удалось получить именованную блокировку журнала '{JournalPath}' за "
                + $"{GateTimeout.TotalSeconds:0} с: состояние владения не читается. "
                + "Работа без журнала безопасности не начинается.",
                null, null);
        }

        try
        {
            var (previous, unreadable) = ReadRecord(RecordPath);
            var probe = Classify((previous, unreadable));

            // ALREADY OWNER: no second Worker, no second generation. Checked by generation, not pid
            // alone — the pid may have been reused.
            if (previous is not null && previous.Pid == Environment.ProcessId && Generation is { } mine
                && string.Equals(previous.Generation, mine, StringComparison.Ordinal)
                && previous.State is HostOwnerState.Serving)
            {
                return new Acquisition(OwnershipOutcome.AlreadyOwned, mine, false, null, null, null, null,
                    TookOverFromPid: null, TookOverFromLiveOwner: false);
            }

            // AN UNFINISHED RELEASE IS NO REASON TO START OVER: a new acquisition would create a second
            // generation and a second Worker on top of a possibly still-live previous one.
            if (previous is not null && IsOurGeneration(previous)
                && previous.State is HostOwnerState.Releasing or HostOwnerState.Draining)
            {
                return Failure(OwnershipOutcome.RefusedActiveOwner, ErrorCodes.SessionReleaseFailed,
                    $"Освобождение сеанса не подтверждено: состояние "
                    + $"{previous.State.ToString().ToLowerInvariant()}, поколение {previous.Generation}. "
                    + "Повторный захват невозможен, пока очистка не завершена: второй Worker поверх "
                    + "возможно живого прежнего — это два Хоста на одном КОМПАС.",
                    probe,
                    "Повторите kompas_release_session: повтор продолжает очистку, а не повторяет "
                    + "разрушительное действие.");
            }

            if (unreadable)
            {
                return Failure(OwnershipOutcome.RefusedStateUnknown, ErrorCodes.OwnershipStateUnknown,
                    $"Запись владельца '{RecordPath}' не читается. Состояние сеанса не определено, "
                    + "и «не прочиталось» не означает «свободно».",
                    probe,
                    "Убедитесь, что запись владельца доступна для чтения и записи этому пользователю; "
                    + "повторите kompas_session_status и захват.");
            }

            // A FOREIGN OWNER IS NOT JUST A FOREIGN PID: a record with OUR pid but a different generation
            // belongs to another owner (several Host "identities" in one process, or a reused pid).
            if (previous is not null && !IsOurGeneration(previous)
                && previous.State is not HostOwnerState.Released and not HostOwnerState.Free
                && (previous.Pid == Environment.ProcessId || probe.OwnerAlive))
            {
                var verb = previous.State == HostOwnerState.Releasing
                    ? "освобождает сеанс"
                    : previous.State == HostOwnerState.Draining
                        ? "завершает транспорт и ещё очищает ресурсы"
                        : "ведёт сеанс";

                return Failure(OwnershipOutcome.RefusedActiveOwner, ErrorCodes.SessionOwnerActive,
                    $"Сеанс КОМПАС принадлежит другому процессу: pid {previous.Pid}, состояние "
                    + $"{previous.State.ToString().ToLowerInvariant()} ({verb}), поколение "
                    + $"{previous.Generation}, обслужено запросов {previous.RequestsServed}. "
                    + $"Основание: {probe.AliveReason}. Два Хоста с одним конфигом не выполняют "
                    + "операции одновременно.",
                    probe,
                    previous.State == HostOwnerState.Releasing
                        ? "Дождитесь окончания освобождения (повторите kompas_session_status) и "
                          + "захватите сеанс снова."
                        : "Освободите сеанс у владельца: вызовите kompas_release_session в том чате "
                          + $"(pid {previous.Pid}), затем kompas_acquire_session здесь.");
            }

            if (previous is not null && previous.State == HostOwnerState.Released && !explicitRequest)
            {
                return Failure(OwnershipOutcome.RefusedExplicitAcquireRequired, ErrorCodes.SessionNotAcquired,
                    "Сеанс освобождён владельцем явным kompas_release_session. После явного "
                    + "освобождения обычный CAD-вызов владение не берёт: новый сеанс — это новый "
                    + "контекст, а старые ссылки намеренно не оживают.",
                    probe,
                    "Вызовите kompas_acquire_session явно, затем kompas_connect и kompas_get_context: "
                    + "document_id и revision прежнего сеанса недействительны.");
            }

            var generation = Guid.NewGuid().ToString("N");
            var problem = WriteRecord(new HostOwnerRecord(
                Environment.ProcessId,
                generation,
                HostOwnerState.Serving,
                _startedUtc,
                0,
                previous?.Pid,
                null));

            if (problem is not null)
            {
                return Failure(OwnershipOutcome.RecordUnavailable, ErrorCodes.JournalUnavailable,
                    $"Запись владельца '{RecordPath}' не записана: {problem}. Владение не подтверждено, "
                    + "работа не начинается.",
                    probe,
                    "Проверьте права на каталог журнала и повторите захват.");
            }

            _generation = generation;
            return new Acquisition(
                OwnershipOutcome.Acquired,
                generation,
                NewGeneration: true,
                Refusal: null,
                ErrorCode: null,
                ErrorMessage: null,
                Remedy: null,
                TookOverFromPid: previous?.Pid,
                TookOverFromLiveOwner: probe.OwnerAlive);
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Begin the release: <see cref="HostOwnerState.Serving"/> → <see cref="HostOwnerState.Releasing"/>.
    /// The exclusive right stays with the owner — another Host cannot take the session.</summary>
    public bool BeginRelease()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current))
            {
                return false;
            }

            if (current!.State != HostOwnerState.Serving)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with { State = HostOwnerState.Releasing });
            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Confirm the release: <see cref="HostOwnerState.Releasing"/> →
    /// <see cref="HostOwnerState.Released"/>. Called AFTER the Worker is confirmed stopped and the queue,
    /// journal and Invoker are released.
    /// </summary>
    public bool CompleteRelease()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Releasing)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with
            {
                State = HostOwnerState.Released,
                ReleasedUtc = DateTimeOffset.UtcNow,
            });

            if (LastWriteProblem is null)
            {
                _generation = null;
            }

            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Abort the release: <see cref="HostOwnerState.Releasing"/> → <see cref="HostOwnerState.Serving"/>.
    /// Called when the pre-cleanup check failed: the owner stays the owner, not "almost free".</summary>
    public bool AbortRelease()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Releasing)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with { State = HostOwnerState.Serving });
            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Transport finished: <see cref="HostOwnerState.Serving"/> → <see cref="HostOwnerState.Draining"/>.
    /// This is NOT a release: resources are not yet freed.</summary>
    public bool BeginDrain()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Serving)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with { State = HostOwnerState.Draining });
            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Confirm the end of cleanup after transport shutdown: <see cref="HostOwnerState.Draining"/> →
    /// <see cref="HostOwnerState.Free"/>. An ordinary CAD call takes ownership after this.
    /// </summary>
    public bool CompleteDrain()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Draining)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with
            {
                State = HostOwnerState.Free,
                ReleasedUtc = DateTimeOffset.UtcNow,
            });

            if (LastWriteProblem is null)
            {
                _generation = null;
            }

            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Record that the Host SERVED a client request. Returns <c>false</c> if ownership is no longer
    /// ours: the caller must then refuse BY NAME and before calling KOMPAS.</summary>
    /// <remarks>A late call from an OLD generation (pid matches, generation does not) makes no update — a late
    /// callback of the old acquisition must not update the new state.</remarks>
    public bool MarkServing()
    {
        Interlocked.Increment(ref _requestsServed);

        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return Generation is not null;
        }

        try
        {
            if (Generation is not { } mine)
            {
                return false;
            }

            var (current, _) = ReadRecord(RecordPath);
            if (current is not null && !IsOurGeneration(current))
            {
                return false;
            }

            // A MISSING RECORD IS NOT COUNTED AS LOSS: one disk error would otherwise turn live calls
            // into refusals. The record is rewritten and the problem is named (LastWriteProblem).
            LastWriteProblem = WriteRecord((current ?? new HostOwnerRecord(
                Environment.ProcessId, mine, HostOwnerState.Serving, _startedUtc, 0, null, null)) with
            {
                Pid = Environment.ProcessId,
                Generation = mine,
                State = HostOwnerState.Serving,
                RequestsServed = Volatile.Read(ref _requestsServed),
            });

            return true;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Why the owner record could not be updated. Empty — it succeeded.</summary>
    public string? LastWriteProblem { get; private set; }

    /// <summary>Take the write problem so it is named EXACTLY ONCE: the same trouble on every tool call would
    /// stop being read.</summary>
    public string? TakeWriteProblem()
    {
        var problem = LastWriteProblem;
        LastWriteProblem = null;
        return problem;
    }

    /// <summary>Whether the session is still ours. Positive proof of loss is a foreign pid OR a foreign
    /// generation; a missing or unreadable record is NOT counted as loss.</summary>
    public bool StillOwned()
    {
        if (!IsOwner)
        {
            return false;
        }

        var (record, _) = ReadRecord(RecordPath);
        if (record is null)
        {
            return true;
        }

        return IsOurGeneration(record);
    }

    /// <summary>Readable "ownership lost" refusal text for a call already in progress.</summary>
    public string LostOwnershipMessage()
    {
        var (record, _) = ReadRecord(RecordPath);
        return record is null
            ? $"Владение журналом '{Path.GetFileName(RecordPath)}' перешло другому процессу."
            : $"Владение сеансом перешло процессу pid {record.Pid} поколения {record.Generation}: "
              + "этот Хост больше не владелец. Повторно работать с моделью можно только после "
              + "kompas_acquire_session и нового kompas_get_context.";
    }

    /// <summary>Turn a record into an observation. The decisions "may be taken" and "explicit acquire required"
    /// are made HERE and nowhere else: two places answering one question would diverge.</summary>
    private SessionProbe Classify((HostOwnerRecord? Record, bool Unreadable) read)
    {
        var (record, unreadable) = read;

        if (record is null)
        {
            return new SessionProbe(
                RecordExists: false, RecordUnreadable: unreadable, LockUnavailable: false,
                State: null, OwnerPid: null, OwnerGeneration: null, OwnerRequestsServed: null,
                OwnerAlive: false,
                AliveReason: unreadable ? "запись владельца не читается" : "записи владельца нет",
                SelfOwned: false,
                CanAcquire: !unreadable,
                RequiresExplicitAcquire: false,
                Reason: unreadable
                    ? "запись владельца не читается: состояние сеанса определить не удалось"
                    : "сеанс свободен: записи владельца нет, владение берётся первым CAD-вызовом",
                RefusalCode: unreadable ? ErrorCodes.OwnershipStateUnknown : null);
        }

        var self = IsOurGeneration(record);
        string aliveReason = self ? "это наш собственный процесс и наше поколение" : string.Empty;
        var alive = self;
        if (!self)
        {
            alive = IsAlive(record, out aliveReason);
        }

        if (self)
        {
            var owned = record.State is HostOwnerState.Serving or HostOwnerState.Starting
                or HostOwnerState.Releasing or HostOwnerState.Draining;

            return new SessionProbe(
                RecordExists: true, RecordUnreadable: false, LockUnavailable: false,
                State: record.State, OwnerPid: record.Pid, OwnerGeneration: record.Generation,
                OwnerRequestsServed: record.RequestsServed,
                OwnerAlive: true, AliveReason: "это наш собственный процесс и наше поколение",
                SelfOwned: owned,
                CanAcquire: !owned,
                RequiresExplicitAcquire: record.State == HostOwnerState.Released,
                Reason: owned
                    ? $"этот Хост владеет сеансом (поколение {record.Generation}, состояние "
                      + $"{record.State.ToString().ToLowerInvariant()})"
                    : record.State == HostOwnerState.Released
                        ? "этот Хост освободил сеанс явным release; для работы нужен явный acquire"
                        : "владения нет",
                RefusalCode: null);
        }

        // Free and Released mean "no ownership" regardless of the previous pid's liveness: the state is
        // written only after confirmed cleanup.
        var free = record.State is HostOwnerState.Released or HostOwnerState.Free;

        return new SessionProbe(
            RecordExists: true, RecordUnreadable: false, LockUnavailable: false,
            State: record.State, OwnerPid: record.Pid, OwnerGeneration: record.Generation,
            OwnerRequestsServed: record.RequestsServed,
            OwnerAlive: alive, AliveReason: aliveReason,
            SelfOwned: false,
            CanAcquire: free || !alive,
            RequiresExplicitAcquire: record.State == HostOwnerState.Released,
            Reason: free
                ? record.State == HostOwnerState.Released
                    ? $"сеанс свободен: владелец pid {record.Pid} снял владение явным release; "
                      + "обычный CAD-вызов владение не берёт"
                    : $"сеанс свободен: прежний владелец pid {record.Pid} завершил работу, очистка подтверждена"
                : alive
                    ? $"сеанс занят процессом pid {record.Pid} (состояние "
                      + $"{record.State.ToString().ToLowerInvariant()}): {aliveReason}"
                    : $"прежний владелец pid {record.Pid} не жив ({aliveReason}): владение можно взять",
            RefusalCode: free || !alive ? null : ErrorCodes.SessionOwnerActive);
    }

    private bool IsOurGeneration(HostOwnerRecord? record) =>
        record is not null
        && record.Pid == Environment.ProcessId
        && Generation is { } mine
        && string.Equals(record.Generation, mine, StringComparison.Ordinal);

    private Acquisition Failure(OwnershipOutcome outcome, string code, string message, SessionProbe? probe, string? remedy) =>
        new(outcome, Generation, false, probe, code, message, remedy, TookOverFromPid: probe?.OwnerPid, TookOverFromLiveOwner: probe?.OwnerAlive ?? false);

    private bool EnterGate(out string? problem)
    {
        try
        {
            if (_gate.WaitOne(GateTimeout))
            {
                problem = null;
                return true;
            }

            problem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing the lock: ownership is re-decided.
            problem = null;
            return true;
        }
    }

    private void ExitGate()
    {
        try
        {
            _gate.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The lock is not ours (dropped via AbandonedMutexException): no release needed.
        }
    }

    private string? WriteRecord(HostOwnerRecord record)
    {
        var temp = RecordPath + "." + Environment.ProcessId + ".tmp";
        try
        {
            // Atomicity via rename: other processes read the owner record, and half a line would read as
            // "no owner" — i.e. as permission to work.
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, RecordPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    private static (HostOwnerRecord? Record, bool Unreadable) ReadRecord(string recordPath)
    {
        if (!File.Exists(recordPath))
        {
            return (null, false);
        }

        try
        {
            using var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var record = JsonSerializer.Deserialize<HostOwnerRecord>(reader.ReadToEnd(), Json);
            return (record, false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, true);
        }
    }

    private static bool IsAlive(HostOwnerRecord record, out string reason)
    {
        if (record.Pid == Environment.ProcessId)
        {
            reason = "это наш собственный процесс";
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
            {
                reason = $"процесса pid {record.Pid} нет";
                return false;
            }

            var started = process.StartTime.ToUniversalTime();
            if (started > record.StartedUtc.UtcDateTime.AddSeconds(2))
            {
                // Pid reused: a process with this pid started AFTER the owner record was written.
                reason = $"pid {record.Pid} переиспользован процессом, стартовавшим позже записи";
                return false;
            }

            reason = $"процесс pid {record.Pid} жив";
            return true;
        }
        catch (ArgumentException)
        {
            reason = $"процесса pid {record.Pid} нет";
            return false;
        }
        catch (InvalidOperationException)
        {
            reason = $"процесс pid {record.Pid} завершился";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            // Could not be determined — counts as alive: the cost is asymmetric (see the class remarks).
            reason = $"живость pid {record.Pid} определить не удалось ({ex.GetType().Name}); "
                     + "владелец считается живым";
            return true;
        }
    }

    private static string MutexNameFor(string fullJournalPath)
    {
        var normalized = fullJournalPath.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return "Local\\kompas-mcp-owner-" + hash;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

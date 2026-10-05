using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Files;

namespace KompasMcp.Domain.Journaling;

public enum JournalOutcome
{
    InFlight,
    Succeeded,
    Failed,
    Cancelled,
    OutcomeUnknown,
}

/// <summary>What the journal remembers about one mutation (spec 1.8).</summary>
public sealed record JournalRecord(
    string OperationId,
    string Tool,
    string ArgumentsHash,
    string? DocumentId,
    long? BaseRevision,
    DateTimeOffset StartedUtc,
    JournalOutcome Outcome,
    string? ResultJson,
    ErrorDto? Error,
    DateTimeOffset? FinishedUtc,
    bool NeedsReconciliation);

/// <summary>Durable, append-only journal of mutations, and the source of idempotency decisions.</summary>
/// <remarks>INVARIANT: the intent record is appended and flushed BEFORE the command reaches COM; the terminal
/// state after. A crash in between leaves an InFlight entry that the next start reclassifies as
/// OutcomeUnknown. Replay for one <c>operation_id</c>: same arguments → recorded state; different →
/// OPERATION_ID_CONFLICT; unknown outcome → never auto-retried.</remarks>
public sealed class OperationJournal : IDisposable
{
    /// <summary>Purpose string of the journal's named lock. A constant because the tool verifying behaviour
    /// under a HELD lock must use the SAME name.</summary>
    public const string FileLockPurpose = "journal";

    private readonly object _gate = new();
    private readonly Dictionary<string, JournalRecord> _byOperation = new(StringComparer.Ordinal);

    /// <summary>Cross-process lock over the journal path, WRITES only. Reading is not blocked.</summary>
    private readonly NamedFileLock _fileGate;

    /// <summary>How long to wait for the cross-process lock when appending a line.</summary>
    private readonly TimeSpan _appendLockTimeout;

    /// <summary>How long to wait for the cross-process lock when reading the journal.</summary>
    private readonly TimeSpan _replayLockTimeout;

    public string Path { get; }

    public int RecoveredInFlight { get; private set; }

    /// <summary>Lines that did not parse during <see cref="Replay"/>. Printed, not swallowed: "read" and "read
    /// in full" are different claims.</summary>
    public int SkippedLines { get; private set; }

    /// <summary>The file's last line is cut off (no trailing newline and it does not parse). Expected after a
    /// hard kill; reported separately from other unparsed lines — different causes.</summary>
    public bool TornTail { get; private set; }

    public event Action<JournalRecord>? RecoveredAsUnknown;

    public OperationJournal(string path, TimeSpan? appendLockTimeout = null, TimeSpan? replayLockTimeout = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _appendLockTimeout = appendLockTimeout ?? AppendLockTimeout;
        _replayLockTimeout = replayLockTimeout ?? ReplayLockTimeout;
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        _fileGate = NamedFileLock.For(Path, FileLockPurpose);
        Replay();
        AssertWritable();
    }

    /// <summary>Verify the journal can be written AT ALL, BEFORE the server accepts calls. INVARIANT: no
    /// long-lived write handle is held, so an unwritable journal would otherwise surface on the first
    /// mutation; the error propagates and <c>Program</c> names it `JOURNAL_UNAVAILABLE`.</summary>
    private void AssertWritable()
    {
        using var stream = OpenAppend();
    }

    /// <summary>
    /// Handle for one append: open-append-close. INVARIANT: no write handle is held for the process
    /// lifetime — a second Host on the same config must still be able to READ the journal. LIMIT:
    /// <c>FileShare.ReadWrite</c> gives no write concurrency; the named lock does (see
    /// <see cref="TryAppend"/>).
    /// </summary>
    private FileStream OpenAppend() =>
        new(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1, FileOptions.None);

    private void Replay()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        // NOT File.ReadLines: it opens with FileShare.Read and forbids a live writer (defect
        // JOURNAL-REPLAY-SHARING-VIOLATION). Reading runs under the same lock as writing, so a probe
        // cannot catch half a line and call it a torn tail.
        // History: docs/decisions/journaling.md#replay-sharing
        var locked = _fileGate.Enter(_replayLockTimeout);
        if (!locked)
        {
            ReplayRanUnlocked = true;
        }

        try
        {
            ReplayCore();

            // INVARIANT: a torn tail is repaired HERE, under the lock just acquired, never unlocked —
            // unlocked repair wrote "\n" into the middle of another Host's line. Without the lock the
            // tail is not repaired: the fact is named and the first append does it (see TryAppend).
            // History: docs/decisions/journaling.md#torn-tail
            if (locked)
            {
                RepairTornTail();
            }
            else if (TornTail)
            {
                TornTailRepairSkippedUnlocked = true;
                _tornTailRepairPending = true;
            }
        }
        finally
        {
            _fileGate.Exit();
        }
    }

    /// <summary>The torn tail was NOT repaired on open because the lock could not be taken. Printed: silence is
    /// indistinguishable from "there was no tear".</summary>
    public bool TornTailRepairSkippedUnlocked { get; private set; }

    /// <summary>The tail awaits repair: a tear was found but no lock was held. Cleared by the first successful
    /// append, which repairs it under ITS OWN lock (see <see cref="TryAppend"/>).</summary>
    private bool _tornTailRepairPending;

    /// <summary>Append a newline if the file does not end with one. No-op on an intact file.</summary>
    private void RepairTornTail()
    {
        try
        {
            if (!File.Exists(Path) || FileEndsWithNewline(Path))
            {
                return;
            }

            using var stream = OpenAppend();
            stream.WriteByte((byte)'\n');
            stream.Flush();
            RepairedTornTails++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Repair failed — NAMED, not swallowed: otherwise the next append loses a line again and
            // silence hides it.
            TornTailRepairFailures++;
            LastRepairFailure = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Number of torn tails repaired on open (a newline was appended).</summary>
    public int RepairedTornTails { get; private set; }

    /// <summary>Number of failed torn-tail repairs. Printed, not silenced.</summary>
    public int TornTailRepairFailures { get; private set; }

    /// <summary>Reason of the last failed repair; null if none ran or all succeeded.</summary>
    public string? LastRepairFailure { get; private set; }

    /// <summary>The journal was read WITHOUT the cross-process lock: skip counts are not guaranteed.</summary>
    public bool ReplayRanUnlocked { get; private set; }

    private static readonly TimeSpan ReplayLockTimeout = TimeSpan.FromSeconds(10);

    private void ReplayCore()
    {
        var endsWithNewline = FileEndsWithNewline(Path);
        var lastNonEmptyLineWasSkipped = false;

        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JournalRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<JournalRecord>(line, JournalOptions);
                }
                catch (JsonException)
                {
                    // A torn tail after a hard kill is expected; it is skipped and COUNTED, so the skip
                    // is named by a number instead of staying silent.
                    SkippedLines++;
                    lastNonEmptyLineWasSkipped = true;
                    continue;
                }

                if (record is null)
                {
                    SkippedLines++;
                    lastNonEmptyLineWasSkipped = true;
                    continue;
                }

                lastNonEmptyLineWasSkipped = false;

                if (record.Outcome == JournalOutcome.InFlight)
                {
                    // INVARIANT: an in-flight record means the process died when KOMPAS may already have
                    // applied the change; it is reported with an explicit code and retry policy.
                    record = record with
                    {
                        Outcome = JournalOutcome.OutcomeUnknown,
                        NeedsReconciliation = true,
                        Error = record.Error ?? ReconcileError("Сервер завершился, не дождавшись ответа Worker: исход команды неизвестен."),
                    };
                    RecoveredInFlight++;
                    RecoveredAsUnknown?.Invoke(record);
                }

                _byOperation[record.OperationId] = record;
            }
        }

        // A tear is the last NON-EMPTY line that failed to parse with no newline after it. An unparsed
        // line in the MIDDLE is not a tear — it has another cause.
        TornTail = SkippedLines > 0 && lastNonEmptyLineWasSkipped && !endsWithNewline;
    }

    /// <summary>Whether the file ends with a newline. Reads the last byte, not the whole file: the journal grows
    /// to hundreds of thousands of lines.</summary>
    private static bool FileEndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == (byte)'\n';
    }

    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Result of asking to start a mutation. The caller must handle every case; there is no "just run
    /// it" boolean, because that is where double-application hides.</summary>
    public sealed record StartDecision(bool Proceed, JournalRecord? Existing)
    {
        public bool IsReplay => Existing is not null && Proceed == false;
    }

    /// <summary>Record the intent to mutate, or decide this call is a replay. INVARIANT: appends and flushes
    /// before returning, so the durable record exists no matter what happens next.</summary>
    public StartDecision TryBegin(string operationId, string tool, string canonicalArguments, string? documentId, long? baseRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        var hash = FileHash.ArgumentsHash(canonicalArguments);
        lock (_gate)
        {
            if (_byOperation.TryGetValue(operationId, out var existing))
            {
                if (!string.Equals(existing.ArgumentsHash, hash, StringComparison.Ordinal))
                {
                    throw new KompasContractException(
                        ErrorCodes.OperationIdConflict,
                        $"operation_id {operationId} уже использован с другими аргументами.",
                        RetryPolicy.Never,
                        details: new Dictionary<string, object?>
                        {
                            ["operation_id"] = operationId,
                            ["recorded_arguments_hash"] = existing.ArgumentsHash,
                            ["incoming_arguments_hash"] = hash,
                        });
                }

                // INVARIANT: the record IS the barrier — "already running" does not mean "may start
                // again" (MEASURED 04.10.2026: a repeated create during execution made TWO documents).
                // INVARIANT: a CLEAN failure allows a new attempt with the same operation_id; a failure
                // with partial effects or an unknown outcome is replayed as before.
                // History: docs/decisions/journaling.md#same-operation-id
                if (IsCleanFailure(existing))
                {
                    var restart = existing with
                    {
                        Outcome = JournalOutcome.InFlight,
                        ResultJson = null,
                        Error = null,
                        FinishedUtc = null,
                        NeedsReconciliation = false,
                    };

                    if (!TryAppend(restart))
                    {
                        throw JournalUnavailable();
                    }

                    _byOperation[operationId] = restart;
                    RestartsAfterCleanFailure++;
                    return new StartDecision(Proceed: true, restart);
                }

                // Proceed=false: a second KOMPAS call is not safe. The record is replayed — an
                // unfinished one answers `running`; a partial effect or unknown outcome needs
                // reconciliation.
                return new StartDecision(Proceed: false, existing);
            }

            var record = new JournalRecord(
                operationId,
                tool,
                hash,
                documentId,
                baseRevision,
                DateTimeOffset.UtcNow,
                JournalOutcome.InFlight,
                null,
                null,
                null,
                NeedsReconciliation: false);

            // INVARIANT: the intent record MUST be durable and locked, else the command does NOT leave —
            // a lost record makes a replay look like "never ran" (MEASURED: 381/400 without the lock).
            // With no lock nothing is appended and the caller gets JOURNAL_UNAVAILABLE.
            // History: docs/decisions/journaling.md#append-atomicity
            if (!TryAppend(record))
            {
                throw JournalUnavailable();
            }

            _byOperation[operationId] = record;
            return new StartDecision(true, null);
        }
    }

    /// <summary>A recorded failure after which a replay with the same operation_id applies nothing a second
    /// time: no partial effect, so no reconciliation is required.</summary>
    private static bool IsCleanFailure(JournalRecord record) =>
        record.Outcome == JournalOutcome.Failed
        && !record.NeedsReconciliation
        && record.Error?.PartialEffects != true;

    /// <summary>
    /// The "journal unavailable" refusal: the intent was NOT recorded and the command did NOT leave.
    /// One wording for two causes (lock not acquired / append failed), with the cause named in
    /// <c>details</c>.
    /// </summary>
    private KompasContractException JournalUnavailable() =>
        new(
            ErrorCodes.JournalUnavailable,
            $"Журнал операций недоступен для записи: строка намерения не записана (блокировка не " +
            $"получена за {_appendLockTimeout.TotalSeconds:0.#} с — {RefusedAppends} раз; запись на " +
            $"носитель не удалась — {AppendIoFailures} раз{Explain(LastAppendFailure)}). Намерение не " +
            "записано, команда в КОМПАС не отправлена — это чистый отказ, повтор с тем же " +
            "operation_id допустим, когда журнал снова доступен.",
            RetryPolicy.SameOperationId,
            details: new Dictionary<string, object?>
            {
                ["journal_path"] = Path,
                ["lock_timeout_s"] = _appendLockTimeout.TotalSeconds,
                ["refused_appends"] = RefusedAppends,
                ["append_io_failures"] = AppendIoFailures,
                ["last_append_failure"] = LastAppendFailure,
            });

    private static string Explain(string? failure) =>
        failure is null ? string.Empty : $": {failure}";

    /// <summary>Number of times a recorded CLEAN failure was restarted with the same operation_id. Printed:
    /// "restart allowed" and "restart never happened" are different claims.</summary>
    public int RestartsAfterCleanFailure { get; private set; }

    public bool Complete(string operationId, string? resultJson) => Finish(operationId, JournalOutcome.Succeeded, resultJson, null, needsReconciliation: false);

    public bool Fail(string operationId, ErrorDto error) => Finish(operationId, JournalOutcome.Failed, null, error, needsReconciliation: false);

    public bool Cancel(string operationId) => Finish(operationId, JournalOutcome.Cancelled, null, null, needsReconciliation: false);

    /// <summary>Record that the outcome cannot be known. Used on a budget timeout, a worker death, or a
    /// disconnect mid-call.</summary>
    public bool MarkUnknown(string operationId, string reason) =>
        Finish(operationId, JournalOutcome.OutcomeUnknown, null, ReconcileError(reason), needsReconciliation: true);

    /// <summary>The error shape for an unknown outcome. Shared with crash recovery so a command that died with
    /// the server and one that timed out are reported identically.</summary>
    private static ErrorDto ReconcileError(string reason) => new(
        ErrorCodes.OutcomeUnknown,
        reason,
        RetryPolicy.AfterReconciliation,
        null,
        true,
        new JsonObject { ["reconciliation_required"] = true });

    private bool Finish(string operationId, JournalOutcome outcome, string? resultJson, ErrorDto? error, bool needsReconciliation)
    {
        lock (_gate)
        {
            if (!_byOperation.TryGetValue(operationId, out var existing))
            {
                return false;
            }

            var updated = existing with
            {
                Outcome = outcome,
                ResultJson = resultJson ?? existing.ResultJson,
                Error = error ?? existing.Error,
                FinishedUtc = DateTimeOffset.UtcNow,
                NeedsReconciliation = needsReconciliation,
            };

            if (TryAppend(updated))
            {
                _byOperation[operationId] = updated;
                return true;
            }

            // INVARIANT: a failed TERMINAL write after a completed mutation is not a success — the
            // intent line is durable and says `in_flight`, so after a restart the journal demands
            // reconciliation. Named NeedsReconciliation; the caller gets `false`.
            // History: docs/decisions/journaling.md#terminal-write
            TerminalWriteFailures++;
            _byOperation[operationId] = updated with
            {
                NeedsReconciliation = true,
                Error = updated.Error ?? ReconcileError(
                    "Терминальная запись журнала не удалась: исход команды известен только в этом " +
                    "процессе и после перезапуска потребует согласования."),
            };
            return false;
        }
    }

    public bool TryGet(string operationId, out JournalRecord? record)
    {
        lock (_gate)
        {
            return _byOperation.TryGetValue(operationId, out record);
        }
    }

    public IReadOnlyList<JournalRecord> Recent(int limit)
    {
        lock (_gate)
        {
            return _byOperation.Values
                .OrderByDescending(r => r.StartedUtc)
                .Take(limit)
                .ToList();
        }
    }

    public int CountNeedingReconciliation()
    {
        lock (_gate)
        {
            return _byOperation.Values.Count(r => r.NeedsReconciliation);
        }
    }

    /// <summary>One record is one atomic byte write UNDER the cross-process lock: the line plus its newline go out in a single <c>Write</c> to an append-mode handle, wrapped in the named lock.</summary>
    /// <returns><c>true</c> — written under the lock; <c>false</c> — the lock was not acquired within <see cref="_appendLockTimeout"/> and NOTHING was written.</returns>
    /// <remarks>MEASURED: the lock is mandatory — <c>FileMode.Append</c> loses records with two writers (381/400, <c>scratch/_append_probe</c>).
    /// LIMIT: <c>Flush()</c> reaches the OS, not the medium; the journal guards against a process crash, not power loss.</remarks>
    private bool TryAppend(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, JournalOptions) + "\n";

        if (!_fileGate.Enter(_appendLockTimeout))
        {
            // Lock not acquired — WRITING IS FORBIDDEN; an earlier revision wrote anyway and only set a
            // diagnostic flag, replacing the guarantee with an observation.
            RefusedAppends++;
            return false;
        }

        try
        {
            // A torn-tail repair deferred at open is done HERE, under this lock; the newline goes before
            // our record in one Write, so a third writer cannot slip in.
            var prefix = string.Empty;
            if (_tornTailRepairPending)
            {
                if (!FileEndsWithNewline(Path))
                {
                    prefix = "\n";
                    RepairedTornTails++;
                }

                _tornTailRepairPending = false;
            }

            var bytes = Encoding.UTF8.GetBytes(prefix + line);
            using var stream = OpenAppend();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ObjectDisposedException)
        {
            // The append failed for a MEDIUM cause. Until 05.10.2026 this threw IOException out; the
            // meaning is the same as an unavailable lock: there is NO durable line. Named by number and
            // text. History: docs/decisions/journaling.md#append-io-failure
            AppendIoFailures++;
            LastAppendFailure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            _fileGate.Exit();
        }
    }

    /// <summary>Number of times the line WRITE itself failed (lock held, medium refused).</summary>
    public int AppendIoFailures { get; private set; }

    /// <summary>Reason of the last failed append; null if none failed.</summary>
    public string? LastAppendFailure { get; private set; }

    /// <summary>Number of appends REFUSED because the cross-process lock was not acquired. Printed: silence is
    /// indistinguishable from "there were no refusals".</summary>
    public int RefusedAppends { get; private set; }

    /// <summary>Number of terminal writes that failed AFTER a completed mutation. Such an operation stays in the
    /// journal as needing reconciliation.</summary>
    public int TerminalWriteFailures { get; private set; }

    /// <summary>How long to wait for the journal's cross-process lock when appending (default).</summary>
    private static readonly TimeSpan AppendLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Release the cross-process lock. The journal holds no file handle (see
    /// <see cref="OpenAppend"/>); the method is kept because callers own the journal through
    /// <c>using</c>.
    /// </summary>
    public void Dispose() => _fileGate.Dispose();
}

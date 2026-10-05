using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using KompasMcp.Domain.Queueing;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Idempotency and crash semantics (spec 1.8). The interesting assertions are the ones about
/// what must NOT happen: no second COM call on replay, and no "safe to retry" after a crash.</summary>
/// <remarks>History: docs/decisions/tests.md#journal-tests</remarks>
public class OperationJournalTests : IDisposable
{
    private readonly string _file;

    public OperationJournalTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "journal-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
    }

    private static string Args(int depth) => $"{{\"depth_mm\":{depth.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    [Fact]
    public void FirstCall_IsAllowedToProceed()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();

        var decision = journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);

        Assert.True(decision.Proceed);
        Assert.Null(decision.Existing);
    }

    [Fact]
    public void SameIdAndSameArguments_IsReplayedNotRedischarged()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        journal.Complete(id, """{"body_count":1}""");

        var again = journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);

        Assert.False(again.Proceed);
        Assert.Equal(JournalOutcome.Succeeded, again.Existing!.Outcome);
        Assert.Equal("""{"body_count":1}""", again.Existing.ResultJson);
    }

    /// <summary>INVARIANT: a replay while in flight does NOT allow a second dispatch. MEASURED 04.10.2026:
    /// the old journal answered <c>Proceed=true</c> to an unfinished record, and a repeated
    /// <c>kompas_create_document</c> created two documents.</summary>
    [Fact]
    public void SameIdWhileStillInFlight_IsNotAllowedToProceedAgain()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        var first = journal.TryBegin(id, "kompas_create_document", Args(10), "doc-1", 3);
        Assert.True(first.Proceed);

        // The record is still InFlight: the operation is running.
        var second = journal.TryBegin(id, "kompas_create_document", Args(10), "doc-1", 3);

        Assert.False(second.Proceed, "незавершённая операция не имеет права быть отправленной второй раз");
        Assert.NotNull(second.Existing);
        Assert.Equal(JournalOutcome.InFlight, second.Existing!.Outcome);
        Assert.True(second.IsReplay);
    }

    [Fact]
    public void SameIdDifferentArguments_Conflict()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        journal.Complete(id, null);

        var ex = Assert.Throws<KompasContractException>(() => journal.TryBegin(id, "kompas_extrude", Args(11), "doc-1", 3));

        Assert.Equal(ErrorCodes.OperationIdConflict, ex.Code);
        Assert.Equal(RetryPolicy.Never, ex.RetryPolicy);
    }

    [Fact]
    public void MarkUnknown_SurvivesReload_AndIsNeverOfferedAsRetryable()
    {
        string id;
        using (var journal = new OperationJournal(_file))
        {
            id = Guid.NewGuid().ToString();
            journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
            journal.MarkUnknown(id, "Worker не ответил");
        }

        using var reopened = new OperationJournal(_file);

        Assert.True(reopened.TryGet(id, out var record));
        Assert.Equal(JournalOutcome.OutcomeUnknown, record!.Outcome);
        Assert.Equal(RetryPolicy.AfterReconciliation, record.Error!.RetryPolicy);

        // Re-sending the same operation_id must not silently dispatch again.
        var decision = reopened.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        Assert.False(decision.Proceed);
        Assert.Equal(JournalOutcome.OutcomeUnknown, decision.Existing!.Outcome);
    }

    [Fact]
    public void CrashWhileInFlight_BecomesOutcomeUnknownOnNextStart()
    {
        string id;
        using (var journal = new OperationJournal(_file))
        {
            id = Guid.NewGuid().ToString();
            journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
            // No terminal record: this is the crash case, and the append-only file still says
            // "in flight" — which must NOT be read back as "it never happened".
        }

        using var restarted = new OperationJournal(_file);

        Assert.Equal(1, restarted.RecoveredInFlight);
        Assert.True(restarted.TryGet(id, out var recovered));
        Assert.Equal(JournalOutcome.OutcomeUnknown, recovered!.Outcome);
        Assert.True(recovered.NeedsReconciliation);
        Assert.Equal(RetryPolicy.AfterReconciliation, recovered.Error!.RetryPolicy);
    }

    [Fact]
    public void DurableRecordExistsBeforeDispatch()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();

        journal.TryBegin(id, "kompas_save_document", Args(1), "doc-1", null);

        // The append is flushed at TryBegin, not at completion: that ordering is the whole
        // guarantee, so it is asserted on the file itself rather than on in-memory state.
        Assert.True(File.Exists(_file), "журнальный файл не создан до отправки команды");
        var text = ReadWhileOpen(_file);
        Assert.Contains("in_flight", text, StringComparison.Ordinal);
        Assert.Contains(id, text, StringComparison.Ordinal);
    }

    /// <summary>Read a file the server may still hold open. File.ReadAllText asks for FileShare.Read, which
    /// Windows refuses against a live writer handle — so anything observing the journal or the logs while the
    /// server runs must open with FileShare.ReadWrite. The rule still holds for the HOST log, which keeps a
    /// handle. The "readable while a writer is live" check is a separate test —
    /// <see cref="SecondInstance_ReadsTheJournalWhileTheFirstIsWriting"/>.</summary>
    private static string ReadWhileOpen(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void CorruptedTrailingLine_IsSkippedWithoutLosingEarlierRecords()
    {
        using (var journal = new OperationJournal(_file))
        {
            var id = Guid.NewGuid().ToString();
            journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
            journal.Complete(id, null);
        }

        // Simulate a hard kill mid-write.
        File.AppendAllText(_file, "{\"operation_id\":\"truncat");

        using var restarted = new OperationJournal(_file);
        Assert.NotEmpty(restarted.Recent(10));

        // INVARIANT: the skip is NAMED by a number and the torn tail is named a tail. "The journal was read"
        // and "the journal was not read in full" are different claims — silence between them is indistinguishable.
        Assert.Equal(1, restarted.SkippedLines);
        Assert.True(restarted.TornTail, "незавершённая последняя строка обязана быть названа рваным хвостом");
    }

    [Fact]
    public void UnreadableLineInTheMiddle_IsNotReportedAsATornTail()
    {
        using (var journal = new OperationJournal(_file))
        {
            var id = Guid.NewGuid().ToString();
            journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        }

        // A garbage line IN THE MIDDLE, followed by a newline and another valid line: the cause is different
        // (file corruption, not a killed process), and calling it a torn tail would misname what was measured.
        File.AppendAllText(_file, "{ это не запись журнала }\n");
        using (var second = new OperationJournal(_file))
        {
            second.TryBegin(Guid.NewGuid().ToString(), "kompas_extrude", Args(11), "doc-1", 3);
        }

        using var restarted = new OperationJournal(_file);
        Assert.Equal(1, restarted.SkippedLines);
        Assert.False(restarted.TornTail, "неразобранная строка в середине файла — не рваный хвост");
        Assert.NotEmpty(restarted.Recent(10));
    }

    /// <summary>INVARIANT (order R1): a journal opened for writing by ANOTHER instance is readable.</summary>
    /// <remarks>MEASURED (21.09.2026): the read path (<c>File.ReadLines</c> with <c>FileShare.Read</c>)
    /// forbade the write a live writer held, and the second Host died with an unhandled <c>IOException</c>
    /// before transport start. INVARIANT: a second reader cannot know whether the writer is alive, so an
    /// unfinished record reads as <see cref="JournalOutcome.OutcomeUnknown"/> with a reconciliation demand —
    /// the crash-recovery rule, unweakened; this is why the journal owner must be one (R3).
    /// History: docs/decisions/tests.md#journal-tests</remarks>
    [Fact]
    public void SecondInstance_ReadsTheJournalWhileTheFirstIsWriting()
    {
        using var first = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        first.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);

        // A second instance on the same path — and not a single exception.
        using var second = new OperationJournal(_file);

        Assert.True(second.TryGet(id, out var seen), "второй экземпляр обязан прочитать запись первого");
        Assert.Equal(JournalOutcome.OutcomeUnknown, seen!.Outcome);
        Assert.True(seen.NeedsReconciliation);

        // Not a single unparsed line: reading runs under the same cross-process lock as writing, so half a
        // line cannot be taken for a torn tail.
        Assert.Equal(0, second.SkippedLines);
        Assert.False(second.TornTail);

        // And the first keeps writing: sharing is needed in BOTH directions, not only on read.
        first.Complete(id, """{"body_count":1}""");
        using var third = new OperationJournal(_file);
        Assert.True(third.TryGet(id, out var completed));
        Assert.Equal(JournalOutcome.Succeeded, completed!.Outcome);
    }

    /// <summary>INVARIANT (order R2): the writes of two instances do not tear each other's lines.</summary>
    /// <remarks>The write handle is no longer held for the process lifetime: each write is open-append-close
    /// in one call. The result is checked, not "the code looks shared": 400 records from two independent
    /// instances, read by a third, with no unparsed and no lost line.</remarks>
    [Fact]
    public async Task TwoInstancesAppendingConcurrently_ProduceNoTornLines()
    {
        const int perWriter = 100;
        using var first = new OperationJournal(_file);
        using var second = new OperationJournal(_file);

        void Write(OperationJournal journal, int index)
        {
            for (var i = 0; i < perWriter; i++)
            {
                var id = Guid.NewGuid().ToString();
                journal.TryBegin(id, "kompas_extrude", Args(i), "doc-" + index, i);
                journal.Complete(id, null);
            }
        }

        await Task.WhenAll(Task.Run(() => Write(first, 0)), Task.Run(() => Write(second, 1)));

        using var reader = new OperationJournal(_file);
        Assert.Equal(0, reader.SkippedLines);
        Assert.Equal(2 * perWriter, reader.Recent(10_000).Count);
    }

    /// <summary>INVARIANT (FIX A): with the cross-process lock unavailable the journal write does NOT happen
    /// — no intent recorded, no in-memory operation, no command leaves, and the refusal is NAMED.
    /// History: docs/decisions/tests.md#journal-tests</summary>
    [Fact]
    public void BeginRefused_WhenLockHeldElsewhere_NothingRecordedAndNamedRefusal()
    {
        using var journal = new OperationJournal(_file, appendLockTimeout: TimeSpan.FromMilliseconds(200));
        using var holder = new JournalLockHolder(_file);
        var id = Guid.NewGuid().ToString();

        var ex = Assert.Throws<KompasContractException>(() =>
            journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3));

        Assert.Equal(ErrorCodes.JournalUnavailable, ex.Code);
        Assert.Equal(RetryPolicy.SameOperationId, ex.RetryPolicy);
        Assert.False(journal.TryGet(id, out _),
            "неудачная запись намерения не имеет права оставлять фиктивную начатую операцию");
        Assert.Equal(1, journal.RefusedAppends);

        // And it is not on disk either: a refusal is "not recorded", not "recorded without the lock".
        var text = File.Exists(_file) ? ReadWhileOpen(_file) : string.Empty;
        Assert.DoesNotContain(id, text, StringComparison.Ordinal);
    }

    /// <summary>INVARIANT: releasing the lock restores work — the same operation_id is accepted, the intent
    /// record is durable, and a completed command is not re-dispatched.</summary>
    [Fact]
    public void BeginSucceedsAfterLockReleased_AndCompletedCommandIsNotReExecuted()
    {
        using var journal = new OperationJournal(_file, appendLockTimeout: TimeSpan.FromMilliseconds(200));
        var id = Guid.NewGuid().ToString();

        using (new JournalLockHolder(_file))
        {
            Assert.Throws<KompasContractException>(() =>
                journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3));
        }

        var decision = journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        Assert.True(decision.Proceed, "после освобождения блокировки намерение обязано записаться");
        journal.Complete(id, """{"body_count":1}""");

        var again = journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        Assert.False(again.Proceed, "уже выполненная команда не отправляется второй раз");
        Assert.Equal(JournalOutcome.Succeeded, again.Existing!.Outcome);
    }

    /// <summary>INVARIANT (FIX A, terminal write): if the outcome line did not land AFTER a completed
    /// mutation, the outcome cannot be declared recorded — in memory it is marked for reconciliation, the
    /// caller gets <c>false</c>, and the durable journal still says <c>in_flight</c>.</summary>
    [Fact]
    public void TerminalWriteFailureAfterMutation_RequiresReconciliation()
    {
        using var journal = new OperationJournal(_file, appendLockTimeout: TimeSpan.FromMilliseconds(200));
        var id = Guid.NewGuid().ToString();
        journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);

        bool recorded;
        using (new JournalLockHolder(_file))
        {
            recorded = journal.Complete(id, """{"body_count":1}""");
        }

        Assert.False(recorded, "неудачная терминальная запись обязана быть видна вызывающему");
        Assert.Equal(1, journal.TerminalWriteFailures);
        Assert.True(journal.TryGet(id, out var record));
        Assert.Equal(JournalOutcome.Succeeded, record!.Outcome);
        Assert.True(record.NeedsReconciliation,
            "исход без долговечной терминальной строки требует согласования, а не считается записанным");
        Assert.Contains("in_flight", ReadWhileOpen(_file), StringComparison.Ordinal);
    }

    /// <summary>INVARIANT (FIX H2): a torn tail is not only marked but REPAIRED — otherwise the first write
    /// of a new process glues itself to the fragment, its intent is lost, and a repeat with the same
    /// operation_id applies the mutation a SECOND time.</summary>
    [Fact]
    public void TornTail_IsRepaired_SoTheNextRecordIsNotSwallowed()
    {
        string firstId;
        using (var journal = new OperationJournal(_file))
        {
            firstId = Guid.NewGuid().ToString();
            journal.TryBegin(firstId, "kompas_extrude", Args(10), "doc-1", 3);
            journal.Complete(firstId, null);
        }

        // A hard process kill mid-write: a line with no newline.
        File.AppendAllText(_file, "{\"operation_id\":\"truncat");

        string secondId;
        using (var restarted = new OperationJournal(_file))
        {
            Assert.True(restarted.TornTail, "обрыв обязан быть назван");
            Assert.Equal(1, restarted.SkippedLines);
            Assert.Equal(1, restarted.RepairedTornTails); // the tear must be repaired on open
            Assert.Equal(0, restarted.TornTailRepairFailures);

            secondId = Guid.NewGuid().ToString();
            var decision = restarted.TryBegin(secondId, "kompas_extrude", Args(11), "doc-1", 3);
            Assert.True(decision.Proceed);
            restarted.Complete(secondId, """{"body_count":2}""");
        }

        // Intent B is not lost — that was the substance of the defect.
        using var third = new OperationJournal(_file);
        Assert.True(third.TryGet(firstId, out var first), "прежняя запись не имеет права теряться");
        Assert.Equal(JournalOutcome.Succeeded, first!.Outcome);
        Assert.True(third.TryGet(secondId, out var second));
        Assert.Equal(JournalOutcome.Succeeded, second!.Outcome);
        Assert.Equal(1, third.SkippedLines); // the torn tail is still NAMED a skip
    }

    /// <summary>INVARIANT (FIX §4, 05.10.2026): a torn tail is repaired ONLY under an acquired lock. While
    /// another writer holds the lock the repair does NOT run — else the appended newline would land inside a
    /// foreign intact record. The first write repairs the tail under ITS OWN lock.
    /// History: docs/decisions/tests.md#journal-tests</summary>
    [Fact]
    public void TornTail_IsNotRepairedWithoutTheLock_AndTheFirstAppendRepairsIt()
    {
        using (var journal = new OperationJournal(_file))
        {
            var id = Guid.NewGuid().ToString();
            journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
            journal.Complete(id, null);
        }

        File.AppendAllText(_file, "{\"operation_id\":\"truncat");
        var before = File.ReadAllBytes(_file);

        // ANOTHER THREAD holds the lock: a named Windows lock belongs to a THREAD, and a second object on the
        // same thread would take it too. So the holder is a separate thread.
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var gate = NamedFileLock.For(_file, OperationJournal.FileLockPurpose);
            gate.Enter(TimeSpan.FromSeconds(10));
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            gate.Exit();
        }) { IsBackground = true };
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)), "держатель блокировки не стартовал");

        try
        {
            using var unlocked = new OperationJournal(
                _file,
                appendLockTimeout: TimeSpan.FromMilliseconds(200),
                replayLockTimeout: TimeSpan.FromMilliseconds(200));

            Assert.True(unlocked.ReplayRanUnlocked, "блокировка занята — чтение обязано идти без неё");
            Assert.True(unlocked.TornTailRepairSkippedUnlocked,
                "без блокировки починка обязана быть НАЗВАНА пропущенной");
            Assert.Equal(0, unlocked.RepairedTornTails);
            Assert.Equal(before, File.ReadAllBytes(_file)); // THE FILE IS UNCHANGED — that is the substance of the fix

            // The holder releases the lock — and the SAME record opened without the lock must repair the tail
            // under ITS OWN lock, not glue its line to the fragment.
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));

            var secondId = Guid.NewGuid().ToString();
            var decision = unlocked.TryBegin(secondId, "kompas_extrude", Args(11), "doc-1", 3);
            Assert.True(decision.Proceed);
            unlocked.Complete(secondId, null);
            Assert.Equal(1, unlocked.RepairedTornTails);

            using var third = new OperationJournal(_file);
            Assert.True(third.TryGet(secondId, out var second), "строка после починки обязана разбираться");
            Assert.Equal(JournalOutcome.Succeeded, second!.Outcome);
            Assert.EndsWith("\n", File.ReadAllText(_file));
        }
        finally
        {
            if (!release.IsSet)
            {
                release.Set();
            }

            holder.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>INVARIANT (FIX M1): the <c>SameOperationId</c> policy on a recorded CLEAN failure is now
    /// feasible — a repeat with the same operation_id reaches re-dispatch. A partial effect is not, and the
    /// distinction is drawn by substance, not by error text.</summary>
    [Fact]
    public void CleanFailure_CanBeRetriedWithTheSameOperationId()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);
        journal.Fail(id, new ErrorDto(ErrorCodes.QueueFull, "очередь заполнена", RetryPolicy.SameOperationId,
            null, false, null));

        var again = journal.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);

        Assert.True(again.Proceed, "чистый отказ: ничего не применено, повтор тем же id обязан дойти до отправки");
        Assert.Equal(1, journal.RestartsAfterCleanFailure);

        // And this is not "the old one was forgotten": the restarted operation is again in_flight, and the
        // terminal record overrides the old failure.
        Assert.True(journal.TryGet(id, out var record));
        Assert.Equal(JournalOutcome.InFlight, record!.Outcome);
        journal.Complete(id, """{"body_count":1}""");
        Assert.True(journal.TryGet(id, out var done));
        Assert.Equal(JournalOutcome.Succeeded, done!.Outcome);
    }

    [Fact]
    public void FailureWithPartialEffects_IsNotRetriedWithTheSameOperationId()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        journal.TryBegin(id, "kompas_insert_component", Args(10), "doc-1", 3);
        journal.Fail(id, new ErrorDto(ErrorCodes.VerificationFailed, "компонент вставлен, размещение не подтверждено",
            RetryPolicy.AfterReconciliation, null, true, null));

        var again = journal.TryBegin(id, "kompas_insert_component", Args(10), "doc-1", 3);

        Assert.False(again.Proceed, "отказ с частичным эффектом: повтор применил бы мутацию второй раз");
        Assert.Equal(JournalOutcome.Failed, again.Existing!.Outcome);
        Assert.Equal(0, journal.RestartsAfterCleanFailure);
    }

    [Fact]
    public async Task Queue_IsBoundedAndRejectsWhenFull()
    {
        await using var queue = new CadCommandQueue(capacity: 2);

        queue.Enqueue(new QueuedCommand("a", "t", new CancellationTokenSource()));
        queue.Enqueue(new QueuedCommand("b", "t", new CancellationTokenSource()));
        var ex = Assert.Throws<KompasContractException>(() => queue.Enqueue(new QueuedCommand("c", "t", new CancellationTokenSource())));

        Assert.Equal(ErrorCodes.QueueFull, ex.Code);
        Assert.Equal(RetryPolicy.SameOperationId, ex.RetryPolicy);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public async Task Queue_CancelsQueuedButNotStarted_KeepsFifoOrder()
    {
        await using var queue = new CadCommandQueue(capacity: 8);
        queue.Enqueue(new QueuedCommand("first", "t", new CancellationTokenSource()));
        queue.Enqueue(new QueuedCommand("second", "t", new CancellationTokenSource()));

        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryCancelQueued("second"), "ещё не стартовавшая команда снимаются");

        var taken = await queue.DequeueAsync(default);
        Assert.Equal("first", taken!.OperationId);

        // The cancelled one is skipped when the reader reaches it, so it is never handed to COM.
        // With nothing else queued the reader then waits, and a cancelled wait is reported as
        // OperationCanceledException rather than as a null command.
        using var gate = new CancellationTokenSource(150);
        await Assert.ThrowsAsync<OperationCanceledException>(() => queue.DequeueAsync(gate.Token).AsTask());
        Assert.False(queue.IsQueued("second"));
    }

    [Fact]
    public async Task Queue_CannotCancelSomethingAlreadyHandedToTheWorker()
    {
        await using var queue = new CadCommandQueue(capacity: 4);
        queue.Enqueue(new QueuedCommand("running", "t", new CancellationTokenSource()));

        var taken = await queue.DequeueAsync(default);
        Assert.Equal("running", taken!.OperationId);

        Assert.False(queue.TryCancelQueued("running"));
    }

    /// <summary>The Host admits a mutation through this queue and then dispatches it to the Worker directly —
    /// the queue is the counter of outstanding CAD work, not the thing that executes it. A served
    /// command must therefore release its slot, or <c>capacity</c> stops meaning "concurrently
    /// waiting" and becomes a lifetime budget of mutations per process. That is not a hypothetical:
    /// the acceptance run grew past 64 mutations in one session and every later call died with
    /// QUEUE_FULL while the Worker was idle.</summary>
    [Fact]
    public async Task Queue_ServedCommandsReleaseTheirSlots_SecondHundredthMutationIsNotTheLast()
    {
        await using var queue = new CadCommandQueue(capacity: 2);

        for (var i = 0; i < 50; i++)
        {
            var id = $"op{i}";
            queue.Enqueue(new QueuedCommand(id, "kompas_extrude", new CancellationTokenSource()));
            Assert.True(queue.Complete(id), $"{id}: обслуженная команда обязана освободить слот");
        }

        Assert.Equal(0, queue.Count);
        Assert.Equal(50, queue.Statistics()["completed"]);
        Assert.Equal(50, queue.Statistics()["accepted"]);
        Assert.Equal(0, queue.Statistics()["rejected"]);
    }

    [Fact]
    public async Task Queue_WithoutRelease_StillRefusesWhenFull()
    {
        // The backpressure itself must survive the fix: a client that fires mutations faster than
        // CAD serves them is told to back off, not queued without bound (docs/03 §1.13).
        await using var queue = new CadCommandQueue(capacity: 2);
        queue.Enqueue(new QueuedCommand("a", "t", new CancellationTokenSource()));
        queue.Enqueue(new QueuedCommand("b", "t", new CancellationTokenSource()));

        var ex = Assert.Throws<KompasContractException>(() =>
            queue.Enqueue(new QueuedCommand("c", "t", new CancellationTokenSource())));

        Assert.Equal(ErrorCodes.QueueFull, ex.Code);
        Assert.Equal(2, queue.Count);

        // One completion is enough to let the refused one in.
        queue.Complete("a");
        queue.Enqueue(new QueuedCommand("c", "t", new CancellationTokenSource()));
        Assert.Equal(2, queue.Count);
    }

    /// <summary>Holds the journal's cross-process lock FROM ANOTHER THREAD: the "write without the lock"
    /// check only makes sense when someone else really holds the lock. The name comes from
    /// <see cref="OperationJournal.FileLockPurpose"/>, not typed as a literal — a private copy would diverge
    /// from the product and the check would measure a foreign lock.</summary>
    private sealed class JournalLockHolder : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly NamedFileLock _lock;
        private readonly Thread _thread;

        public JournalLockHolder(string journalPath)
        {
            _lock = NamedFileLock.For(journalPath, OperationJournal.FileLockPurpose);
            _thread = new Thread(() =>
            {
                if (!_lock.Enter(TimeSpan.FromSeconds(10)))
                {
                    return; // _acquired stays reset, and the constructor names it a refusal.
                }

                _acquired.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
                _lock.Exit();
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            if (!_acquired.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("тестовый поток не смог взять блокировку журнала");
            }
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(10));
            _lock.Dispose();
            _acquired.Dispose();
            _release.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_file))
            {
                File.Delete(_file);
            }
        }
        catch (IOException)
        {
            // Temp file cleanup is best effort.
        }
    }
}

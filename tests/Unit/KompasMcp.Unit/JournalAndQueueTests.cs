using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using KompasMcp.Domain.Queueing;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Idempotency and crash semantics (spec 1.8). The interesting assertions are the ones about
/// what must NOT happen: no second COM call on replay, and no "safe to retry" after a crash.
/// </summary>
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

    /// <summary>
    /// Read a file the server may still hold open. File.ReadAllText asks for FileShare.Read, which
    /// Windows refuses against a live writer handle — so anything that has to observe the journal
    /// or the logs while the server runs must open with FileShare.ReadWrite.
    /// </summary>
    /// <remarks>
    /// Уточнено 21.09.2026: сам журнал ручку на запись теперь НЕ держит (запись —
    /// открыть-дописать-закрыть, R2 наряда), поэтому этот путь больше не единственный способ
    /// прочитать живой журнал. Правило остаётся в силе для журнала ХОСТА, который ручку держит, и
    /// для прежних сборок поставки. Проверка «читается при живом писателе» вынесена отдельным
    /// тестом — <see cref="SecondInstance_ReadsTheJournalWhileTheFirstIsWriting"/>.
    /// </remarks>
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

        // Пропуск НАЗЫВАЕТСЯ числом, и оборванный хвост назван хвостом. «Журнал прочитан» и
        // «журнал прочитан не весь» — разные утверждения: молчание между ними неразличимо.
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

        // Мусорная строка В СЕРЕДИНЕ, за которой есть перевод строки и ещё одна годная строка:
        // причина другая (порча файла, а не убийство процесса), и назвать её рваным хвостом —
        // значит назвать измеренное не тем, чем оно является.
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

    /// <summary>
    /// R1 наряда: журнал, открытый на запись ДРУГИМ экземпляром, читается.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Это ровно тот дефект, из-за которого 21.09.2026 клиент остался без инструментов: путь чтения
    /// (<c>File.ReadLines</c> с <c>FileShare.Read</c>) запрещал запись, которую держал живой писатель,
    /// и второй Хост падал необработанным <c>IOException</c> до старта транспорта.
    /// </para>
    /// <para>
    /// ИЗМЕРЕННОЕ СЛЕДСТВИЕ, КОТОРОЕ ЗДЕСЬ НАЗВАНО, А НЕ ЗАМОЛЧАНО. Второй читатель не знает, жив
    /// ли писатель, поэтому незавершённая запись читается им как <see cref="JournalOutcome.OutcomeUnknown"/>
    /// с требованием согласования — это и есть правило восстановления после падения, и оно не
    /// ослаблено. Именно поэтому чтение живого журнала вторым Хостом — не безобидное действие, а
    /// причина, по которой владелец журнала обязан быть один (R3 наряда).
    /// </para>
    /// </remarks>
    [Fact]
    public void SecondInstance_ReadsTheJournalWhileTheFirstIsWriting()
    {
        using var first = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        first.TryBegin(id, "kompas_extrude", Args(10), "doc-1", 3);

        // Второй экземпляр на том же пути — и ни одного исключения.
        using var second = new OperationJournal(_file);

        Assert.True(second.TryGet(id, out var seen), "второй экземпляр обязан прочитать запись первого");
        Assert.Equal(JournalOutcome.OutcomeUnknown, seen!.Outcome);
        Assert.True(seen.NeedsReconciliation);

        // Ни одной неразобранной строки: чтение идёт под той же межпроцессной блокировкой, что и
        // запись, поэтому половина строки не может быть принята за рваный хвост.
        Assert.Equal(0, second.SkippedLines);
        Assert.False(second.TornTail);

        // И первый продолжает писать: совместность нужна в ОБЕ стороны, а не только на чтении.
        first.Complete(id, """{"body_count":1}""");
        using var third = new OperationJournal(_file);
        Assert.True(third.TryGet(id, out var completed));
        Assert.Equal(JournalOutcome.Succeeded, completed!.Outcome);
    }

    /// <summary>
    /// R2 наряда: записи двух экземпляров не рвут строки друг друга.
    /// </summary>
    /// <remarks>
    /// Ручка на запись больше не держится всю жизнь процесса: каждая запись — открыть-дописать-
    /// закрыть одним вызовом. Проверяется не «код выглядит совместным», а результат: 400 записей от
    /// двух независимых экземпляров, прочитанных третьим, — ни одной неразобранной строки и ни одной
    /// потерянной записи.
    /// </remarks>
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

    /// <summary>
    /// The Host admits a mutation through this queue and then dispatches it to the Worker directly —
    /// the queue is the counter of outstanding CAD work, not the thing that executes it. A served
    /// command must therefore release its slot, or <c>capacity</c> stops meaning "concurrently
    /// waiting" and becomes a lifetime budget of mutations per process. That is not a hypothetical:
    /// the acceptance run grew past 64 mutations in one session and every later call died with
    /// QUEUE_FULL while the Worker was idle.
    /// </summary>
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

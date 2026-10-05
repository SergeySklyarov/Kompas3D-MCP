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

    /// <summary>
    /// Повтор во время выполнения НЕ разрешает вторую отправку. Прежде журнал отвечал
    /// <c>Proceed=true</c> на незавершённую запись, и вызывающий отправлял команду в КОМПАС второй
    /// раз — измерено 04.10.2026: повтор <c>kompas_create_document</c> создал два документа.
    /// </summary>
    [Fact]
    public void SameIdWhileStillInFlight_IsNotAllowedToProceedAgain()
    {
        using var journal = new OperationJournal(_file);
        var id = Guid.NewGuid().ToString();
        var first = journal.TryBegin(id, "kompas_create_document", Args(10), "doc-1", 3);
        Assert.True(first.Proceed);

        // Запись ещё InFlight: операция выполняется.
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

    /// <summary>
    /// FIX A. Запись журнала при недоступной межпроцессной блокировке НЕ выполняется: намерение не
    /// записано, начатой операции в памяти нет, команда не уходит, отказ НАЗВАН. Прежняя редакция
    /// писала строку без блокировки и лишь выставляла диагностический флаг — гарантия журналирования
    /// подменялась наблюдением.
    /// </summary>
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

        // И на диске её тоже нет: отказ — это «не записано», а не «записано без блокировки».
        var text = File.Exists(_file) ? ReadWhileOpen(_file) : string.Empty;
        Assert.DoesNotContain(id, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Освобождение блокировки восстанавливает работу: тот же operation_id принимается, запись
    /// намерения долговечна, повторной отправки уже выполненной команды не происходит.
    /// </summary>
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

    /// <summary>
    /// FIX A (терминальная запись). Если строка исхода не легла ПОСЛЕ выполненной мутации, исход
    /// нельзя объявлять записанным: в памяти он помечается требующим согласования, вызывающий
    /// получает `false`, а долговечный журнал по-прежнему говорит `in_flight`.
    /// </summary>
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

    /// <summary>
    /// FIX H2. Рваный хвост НЕ только отмечается, но и ЧИНИТСЯ: иначе первая же запись нового
    /// процесса склеивается с обрывком, и её намерение теряется полностью — журнал не знает
    /// операции, и повтор с тем же operation_id выполняет мутацию ВТОРОЙ раз.
    /// </summary>
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

        // Жёсткое убийство процесса посередине записи: строка без перевода строки.
        File.AppendAllText(_file, "{\"operation_id\":\"truncat");

        string secondId;
        using (var restarted = new OperationJournal(_file))
        {
            Assert.True(restarted.TornTail, "обрыв обязан быть назван");
            Assert.Equal(1, restarted.SkippedLines);
            Assert.Equal(1, restarted.RepairedTornTails); // обрыв обязан быть починен при открытии
            Assert.Equal(0, restarted.TornTailRepairFailures);

            secondId = Guid.NewGuid().ToString();
            var decision = restarted.TryBegin(secondId, "kompas_extrude", Args(11), "doc-1", 3);
            Assert.True(decision.Proceed);
            restarted.Complete(secondId, """{"body_count":2}""");
        }

        // Намерение B не потеряно — это и было содержимым дефекта.
        using var third = new OperationJournal(_file);
        Assert.True(third.TryGet(firstId, out var first), "прежняя запись не имеет права теряться");
        Assert.Equal(JournalOutcome.Succeeded, first!.Outcome);
        Assert.True(third.TryGet(secondId, out var second));
        Assert.Equal(JournalOutcome.Succeeded, second!.Outcome);
        Assert.Equal(1, third.SkippedLines); // рваный хвост по-прежнему НАЗВАН пропуском
    }

    /// <summary>
    /// FIX M1. Политика <c>SameOperationId</c> у записанного ЧИСТОГО отказа теперь выполнима:
    /// повтор с тем же operation_id доходит до повторной отправки. Частичный эффект — не выполнима,
    /// и это различие проведено по существу, а не по тексту ошибки.
    /// </summary>
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

        // И это не «забыли прежнее»: повторно начатая операция снова in_flight, а терминальная
        // запись перекрывает прежний отказ.
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

    /// <summary>
    /// Держит межпроцессную блокировку журнала ИЗ ДРУГОГО ПОТОКА: проверка «запись без блокировки»
    /// имеет смысл только тогда, когда блокировку действительно держит кто-то ещё. Имя берётся из
    /// <see cref="OperationJournal.FileLockPurpose"/>, а не набирается строкой: своя копия имени
    /// разошлась бы с продуктом и проверка измеряла бы чужую блокировку.
    /// </summary>
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
                    return; // _acquired остаётся сброшенным, и конструктор назовёт это отказом.
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

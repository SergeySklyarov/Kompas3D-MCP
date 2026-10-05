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

/// <summary>
/// Durable, append-only journal of mutations, and the source of idempotency decisions.
/// </summary>
/// <remarks>
/// The ordering is the whole point and is not negotiable: a record is appended and flushed
/// <b>before</b> the command reaches COM, and the terminal state is appended afterwards. A crash
/// between the two leaves an <see cref="JournalOutcome.InFlight"/> entry, and the next start
/// reclassifies it as <see cref="JournalOutcome.OutcomeUnknown"/> — because the process died at a
/// moment when КОМПАС may already have applied the change. Reconstructing the record in memory
/// after the fact would make every retry after a crash look safe, which is exactly the failure the
/// spec forbids.
///
/// Replay rules for the same <c>operation_id</c>:
/// <list type="bullet">
/// <item>same arguments → return the recorded state (no second COM call, so a repeated
/// <c>kompas_extrude</c> cannot create a second feature);</item>
/// <item>different arguments → OPERATION_ID_CONFLICT;</item>
/// <item>unknown outcome → never auto-retried; the caller must reconcile against the model.</item>
/// </list>
/// </remarks>
public sealed class OperationJournal : IDisposable
{
    /// <summary>
    /// Назначение именованной блокировки журнала. Вынесено в константу, потому что тем же
    /// именем обязан пользоваться ИНСТРУМЕНТ, проверяющий поведение при удержанной блокировке:
    /// без общей константы проверка держала бы «свою» блокировку и измеряла бы не то.
    /// </summary>
    public const string FileLockPurpose = "journal";

    private readonly object _gate = new();
    private readonly Dictionary<string, JournalRecord> _byOperation = new(StringComparer.Ordinal);

    /// <summary>Межпроцессная блокировка ЗАПИСИ по пути журнала. Чтение она не запрещает.</summary>
    private readonly NamedFileLock _fileGate;

    /// <summary>Сколько ждать межпроцессную блокировку при записи строки.</summary>
    private readonly TimeSpan _appendLockTimeout;

    /// <summary>Сколько ждать межпроцессную блокировку при чтении журнала.</summary>
    private readonly TimeSpan _replayLockTimeout;

    public string Path { get; }

    public int RecoveredInFlight { get; private set; }

    /// <summary>
    /// Сколько строк журнала не разобралось при <see cref="Replay"/>. Печатается вызывающим, а не
    /// проглатывается: «журнал прочитан» и «журнал прочитан не весь» — разные утверждения.
    /// </summary>
    public int SkippedLines { get; private set; }

    /// <summary>
    /// Последняя строка файла оборвана (файл не кончается переводом строки, и эта строка не
    /// разбирается). Это ожидаемое состояние после жёсткого убийства процесса, и оно называется
    /// отдельно от прочих неразобранных строк: у них разные причины.
    /// </summary>
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

    /// <summary>
    /// Проверить, что журнал ВООБЩЕ можно писать, — ДО того, как сервер начнёт принимать вызовы.
    ///
    /// Нужна потому, что ручки на запись журнал больше не держит (см. <see cref="Append"/>): без
    /// этой проверки недоступный журнал обнаружился бы на первой же мутации, то есть уже после
    /// того, как клиент получил инструменты и решил, что сервер работает. Ошибка пробрасывается
    /// наверх, и <c>Program</c> называет её `JOURNAL_UNAVAILABLE` — молча работать без журнала
    /// безопасности запрещено.
    ///
    /// Открытие в режиме добавления ничего не пишет и содержимого не меняет.
    /// </summary>
    private void AssertWritable()
    {
        using var stream = OpenAppend();
    }

    /// <summary>
    /// Дескриптор на одну запись: открыть-дописать-закрыть.
    ///
    /// Ручка на запись НЕ держится всю жизнь процесса — и это не оптимизация, а требование
    /// совместности: пока писатель жил вместе с Хостом, второй Хост с тем же конфигом не мог даже
    /// прочитать журнал, потому что проверка режима совместного доступа идёт в обе стороны.
    /// Стоимость — один системный вызов на запись; цена пожизненной ручки — отказ второго сеанса.
    ///
    /// <c>FileShare.ReadWrite</c> объявлен на обеих сторонах — и на чтении (<see cref="Replay"/>), и
    /// на записи. СОВМЕСТНОСТЬ ЗАПИСИ ЭТО НЕ ОБЕСПЕЧИВАЕТ: она обеспечивается именованной
    /// блокировкой в <see cref="Append"/> — измерено, что без неё записи двух писателей теряются.
    /// </summary>
    private FileStream OpenAppend() =>
        new(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1, FileOptions.None);

    private void Replay()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        // НЕ File.ReadLines: он открывает файл с FileShare.Read, то есть запрещает запись живому
        // писателю, и второй Хост падал на этом необработанным IOException (дефект
        // JOURNAL-REPLAY-SHARING-VIOLATION, измерен 21.09.2026). Тот же FileShare.ReadWrite, что
        // и на записи, — тогда объявленное комментарием намерение выполняется на обеих сторонах.
        //
        // Чтение идёт под ТОЙ ЖЕ межпроцессной блокировкой, что и запись. Это не про целостность
        // файла, а про ЧЕСТНОСТЬ ЧИСЛА: без блокировки прибор, читающий живой журнал, мог бы
        // поймать половину строки и назвать её рваным хвостом — то есть доложить о порче там, где
        // шла обычная запись.
        var locked = _fileGate.Enter(_replayLockTimeout);
        if (!locked)
        {
            ReplayRanUnlocked = true;
        }

        try
        {
            ReplayCore();

            // РВАНЫЙ ХВОСТ ЧИНИТСЯ ЗДЕСЬ, А НЕ ЖДЁТ СЛЕДУЮЩЕЙ ЗАПИСИ.
            //
            // До 05.10.2026 журнал лишь ОТМЕЧАЛ обрыв (TornTail=true) и ничего не делал. Первая же
            // запись нового процесса дописывала `{…B…}\n` в режиме Append прямо за обрывком, и
            // строка становилась `{"operation_id":"trunc{…B…}` — то есть намерение B терялось
            // полностью: следующий Replay не разбирал эту строку, журнал не знал операцию вообще, и
            // повтор с тем же operation_id выполнял мутацию ВТОРОЙ раз. Ровно тот сценарий, ради
            // которого журнал заведён, был сломан.
            //
            // ПОЧИНКА — ТОЛЬКО ПОД ПОЛУЧЕННОЙ БЛОКИРОВКОЙ. Прежде она шла и тогда, когда блокировку
            // взять не удалось (ReplayRanUnlocked): перевод строки дописывался в файл, который в этот
            // момент писал ДРУГОЙ Хост, и `\n` ложился в СЕРЕДИНУ его строки — то есть починка одного
            // обрыва портила чужую целую запись (находка §4 задания 05.10.2026). Без блокировки хвост
            // НЕ чинится: факт называется (TornTailRepairSkippedUnlocked), а починку выполняет первая
            // же запись — под своей блокировкой, см. TryAppend.
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

    /// <summary>
    /// Рваный хвост НЕ починен при открытии, потому что межпроцессную блокировку взять не удалось.
    /// Печатается вызывающим: молчание об этом неотличимо от «обрывов не было», а починка чужой
    /// строки под чужим писателем — от «файл цел».
    /// </summary>
    public bool TornTailRepairSkippedUnlocked { get; private set; }

    /// <summary>
    /// Хвост ждёт починки: обрыв найден, но блокировки при открытии не было. Сбрасывается первой же
    /// удачной записью, которая чинит хвост под СВОЕЙ блокировкой (см. <see cref="TryAppend"/>).
    /// </summary>
    private bool _tornTailRepairPending;

    /// <summary>
    /// Дописать перевод строки, если файл им не кончается. Ничего не делает на целом файле.
    /// </summary>
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
            // Починка не удалась — это НАЗЫВАЕТСЯ, а не проглатывается: при следующей записи без
            // перевода строки потеря строки повторится, и молчание здесь сделало бы её
            // неотличимой от «обрывов не было».
            TornTailRepairFailures++;
            LastRepairFailure = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Сколько рваных хвостов починено при открытии (дописан перевод строки).</summary>
    public int RepairedTornTails { get; private set; }

    /// <summary>Сколько раз починка рваного хвоста не удалась. Печатается, а не молчит.</summary>
    public int TornTailRepairFailures { get; private set; }

    /// <summary>Причина последней неудавшейся починки; null, если починок не было или все удались.</summary>
    public string? LastRepairFailure { get; private set; }

    /// <summary>Чтение журнала прошло без межпроцессной блокировки: числа пропусков не гарантированы.</summary>
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
                    // Рваный хвост после жёсткого убийства процесса ожидаем; он пропускается, а не
                    // «ремонтируется». Существующая ветка сохранена, но теперь она ещё и
                    // ПОДСЧИТЫВАЕТСЯ: пропуск называется числом, а не молчит.
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
                    // A record left in flight means the process died at a moment when КОМПАС may
                    // already have applied the change. It is reported with an explicit code and retry
                    // policy so no caller has to infer them, and so a client that only reads
                    // envelope.error still cannot mistake this for a clean failure to retry.
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

        // Оборванный хвост — это НЕ «файл кончается переводом строки» и не любая неразобранная
        // строка: обрыв — это последняя НЕПУСТАЯ строка, которая не разобралась, и за которой нет
        // перевода строки. Неразобранная строка В СЕРЕДИНЕ файла называется обрывом неправильно:
        // у неё другая причина, и смешав их, отчёт назвал бы порчу журнала рваным хвостом.
        TornTail = SkippedLines > 0 && lastNonEmptyLineWasSkipped && !endsWithNewline;
    }

    /// <summary>
    /// Кончается ли файл переводом строки. Читается последний байт, а не весь файл: журнал растёт
    /// до сотен тысяч строк.
    /// </summary>
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

    /// <summary>
    /// Result of asking to start a mutation. The caller must handle every case explicitly;
    /// there is no "just run it" boolean, because that is where double-application hides.
    /// </summary>
    public sealed record StartDecision(bool Proceed, JournalRecord? Existing)
    {
        public bool IsReplay => Existing is not null && Proceed == false;
    }

    /// <summary>
    /// Record the intent to mutate, or decide that this call is a replay. Appends and flushes
    /// before returning, so the durable record exists no matter what happens next.
    /// </summary>
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

                // ЗАПИСЬ — ЭТО И ЕСТЬ ЗАПОР. «Уже выполняется» НЕ означает «можно начать ещё раз»:
                // прежде здесь возвращалось Proceed=true, и вызывающий, вызвав ExecuteAsync до
                // проверки своей таблицы незавершённых задач, отправлял в КОМПАС ВТОРУЮ команду с тем
                // же operation_id. Измерено 04.10.2026 на прогоне передачи сеанса: повтор
                // kompas_create_document во время выполнения создал ДВА документа (54640bdb… и
                // 3a487677… в одном экземпляре), то есть «повтор не повторяет мутацию» было ложью
                // ровно в том окне, ради которого повтор и разрешён.
                //
                // ЧИСТЫЙ ОТКАЗ РАЗРЕШАЕТ НОВУЮ ПОПЫТКУ С ТЕМ ЖЕ operation_id.
                //
                // Политика `SameOperationId` ЗАПИСАННОГО отказа была НЕВЫПОЛНИМА: любая уже
                // записанная запись `failed` (QUEUE_FULL, RequireSourceFile, AddFromFile=null,
                // замена не применилась) воспроизводилась ВСЕГДА, и «повторите тем же
                // operation_id» из текста ошибки не работало — клиент был вынужден брать новый id,
                // а для отказа с частичным эффектом новый id означает повторную мутацию. Теперь
                // различие проведено по существу: отказ без частичных эффектов — ничто не
                // применено, повтор безопасен; отказ с частичными эффектами или неизвестным
                // исходом — повтор воспроизведением, как прежде.
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

                // Proceed=false означает: повторное обращение к КОМПАС не безопасно. Запись
                // воспроизводится — незавершённая отвечает `running`, частичный эффект и
                // неизвестный исход требуют согласования.
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

            // ЗАПИСЬ НАМЕРЕНИЯ ОБЯЗАНА БЫТЬ ДОЛГОВЕЧНОЙ И ПОД БЛОКИРОВКОЙ, ИНАЧЕ КОМАНДА НЕ УХОДИТ.
            //
            // Прежде результат `_fileGate.Enter(...)` здесь не проверялся: строка писалась и без
            // блокировки, а факт назывался диагностическим флагом `AppendRanUnlocked`. Это заменяло
            // ГАРАНТИЮ журналирования диагностикой: потерянная запись означает, что повтор после
            // перезапуска выглядит как «не выполнялось», то есть мутация может быть применена
            // ВТОРОЙ раз (измерено в пробе scratch/_append_probe: 381 запись из 400 без блокировки).
            // Теперь при недоступной блокировке запись НЕ делается, в `_byOperation` НЕ остаётся
            // фиктивной начатой операции, и вызывающий получает именованный отказ — до того, как
            // что-либо уйдёт в Worker.
            if (!TryAppend(record))
            {
                throw JournalUnavailable();
            }

            _byOperation[operationId] = record;
            return new StartDecision(true, null);
        }
    }

    /// <summary>
    /// Записанный отказ, после которого повтор с тем же operation_id НИЧЕГО не применяет второй
    /// раз: исхода «применено частично» нет, согласование не требуется.
    /// </summary>
    private static bool IsCleanFailure(JournalRecord record) =>
        record.Outcome == JournalOutcome.Failed
        && !record.NeedsReconciliation
        && record.Error?.PartialEffects != true;

    /// <summary>
    /// Отказ «журнал недоступен»: намерение НЕ записано, команда НЕ уходила. Одна формулировка на
    /// две причины (блокировка не получена / запись не удалась), потому что для вызывающего это
    /// одно и то же состояние, а причина названа в <c>details</c>, а не спрятана.
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

    /// <summary>
    /// Сколько раз записанный ЧИСТЫЙ отказ был начат заново тем же operation_id. Печатается:
    /// «повтор разрешён» и «повтор ни разу не происходил» — разные утверждения.
    /// </summary>
    public int RestartsAfterCleanFailure { get; private set; }

    public bool Complete(string operationId, string? resultJson) => Finish(operationId, JournalOutcome.Succeeded, resultJson, null, needsReconciliation: false);

    public bool Fail(string operationId, ErrorDto error) => Finish(operationId, JournalOutcome.Failed, null, error, needsReconciliation: false);

    public bool Cancel(string operationId) => Finish(operationId, JournalOutcome.Cancelled, null, null, needsReconciliation: false);

    /// <summary>
    /// Record that the outcome cannot be known. Used on a budget timeout, a worker death, or a
    /// disconnect mid-call.
    /// </summary>
    public bool MarkUnknown(string operationId, string reason) =>
        Finish(operationId, JournalOutcome.OutcomeUnknown, null, ReconcileError(reason), needsReconciliation: true);

    /// <summary>
    /// The error shape for an unknown outcome. Shared with crash recovery so a command that died
    /// with the server and one that merely timed out are reported identically — they are the same
    /// state as far as a caller is concerned.
    /// </summary>
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

            // ТЕРМИНАЛЬНАЯ ЗАПИСЬ НЕ УДАЛАСЬ ПОСЛЕ ВЫПОЛНЕННОЙ МУТАЦИИ.
            //
            // Строка «намерение» уже долговечна и говорит `in_flight`; терминальная строка — нет.
            // Поэтому исход этой операции нельзя объявлять ЗАПИСАННЫМ: после перезапуска журнал
            // прочитает `in_flight` и потребует согласования, то есть повтор НЕ безопасен. Здесь это
            // названо в памяти явно (`NeedsReconciliation`), а вызывающий получает `false` и обязан
            // сообщить клиенту, что терминальная запись не долговечна, а не молча отдать успех.
            // Сама мутация уже выполнена — поэтому исход в памяти СОХРАНЯЕТСЯ (он известен в этом
            // процессе), но помечается как требующий согласования, а не как чисто записанный.
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

    /// <summary>
    /// Одна запись — одна атомарная запись байтов ПОД межпроцессной блокировкой: строка с переводом
    /// строки уходит одним <c>Write</c> в дескриптор, открытый в режиме добавления, и всё это
    /// (открыть-записать-закрыть) закрыто именованной блокировкой по пути журнала.
    /// </summary>
    /// <returns>
    /// <c>true</c> — строка записана под блокировкой; <c>false</c> — блокировка не получена за
    /// <see cref="_appendLockTimeout"/> и НИЧЕГО не записано. Отказ называется вызывающим, а не
    /// подменяется записью без блокировки.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Блокировка обязательна, и это измерено, а не подстраховано: <c>FileMode.Append</c> с одной
    /// записью байтов теряет записи при двух писателях (381 из 400 в пробе
    /// <c>scratch/_append_probe</c>), потому что позиция конца файла запоминается при ОТКРЫТИИ.
    /// Для журнала безопасности потерянная запись означает, что повтор после перезапуска выглядит
    /// как «не выполнялось», — то есть мутация может быть применена второй раз. Заодно не остаётся
    /// пожизненной ручки, из-за которой второй Хост не мог даже прочитать журнал.
    /// </para>
    /// <para>
    /// <b>ГРАНИЦЫ ДОЛГОВЕЧНОСТИ НАЗВАНЫ ТОЧНО.</b> <c>Flush()</c> на <see cref="FileStream"/>
    /// сбрасывает буферы управляемого потока в ОС — он НЕ обещает, что байты легли на носитель.
    /// Поэтому гарантия здесь — «строка ушла в ОС и видна другим читателям через файловую систему
    /// раньше, чем команда уйдёт в Worker», а НЕ «строка переживёт отключение питания»: для второго
    /// нужен <c>Flush(true)</c> либо запись через <c>FileOptions.WriteThrough</c>, и ни того, ни
    /// другого здесь нет. Это осознанная граница: цена долговечности при потере питания — запись на
    /// диск на каждой мутации, а потеря питания не является сценарием, ради которого журнал заведён
    /// (он заведён против ПОВТОРНОЙ ОТПРАВКИ после падения процесса).
    /// </para>
    /// </remarks>
    private bool TryAppend(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, JournalOptions) + "\n";

        if (!_fileGate.Enter(_appendLockTimeout))
        {
            // Блокировка не получена — ПИСАТЬ НЕЛЬЗЯ. Прежняя редакция писала и лишь выставляла
            // диагностический флаг: гарантия подменялась наблюдением.
            RefusedAppends++;
            return false;
        }

        try
        {
            // ПОЧИНКА РВАНОГО ХВОСТА, ОТЛОЖЕННАЯ ПРИ ОТКРЫТИИ, ДЕЛАЕТСЯ ЗДЕСЬ — ПОД ЭТОЙ БЛОКИРОВКОЙ.
            //
            // Открытие журнала могло не получить блокировку (ReplayRanUnlocked) и тогда хвост не
            // чинило: дописать `\n` без блокировки значило бы вставить перевод строки в середину
            // строки, которую в этот момент пишет ДРУГОЙ Хост. Здесь блокировка уже наша, поэтому
            // починка безопасна, и перевод строки идёт ПЕРЕД нашей записью — одной операцией Write,
            // чтобы между ними не влез третий писатель.
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
            // ЗАПИСЬ НЕ УДАЛАСЬ ПО ПРИЧИНЕ НОСИТЕЛЯ (диск полон, антивирус удержал файл, путь
            // исчез). До 05.10.2026 такой исход вообще не рассматривался: блокировка получена, а
            // сама запись бросала IOException наружу — либо операция оставалась `in_flight` в
            // памяти при НЕзаписанной строке намерения (повторы в этом процессе вечно отвечали
            // `running`, а после перезапуска журнал операции не знал вовсе), либо вызов падал
            // необработанным исключением там, где ожидался конверт.
            //
            // Смысл отказа тот же, что у недоступной блокировки: ДОЛГОВЕЧНОЙ СТРОКИ НЕТ. Поэтому
            // возвращается false, а причина называется числом и текстом.
            AppendIoFailures++;
            LastAppendFailure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            _fileGate.Exit();
        }
    }

    /// <summary>
    /// Сколько раз САМА запись строки не удалась (блокировка была получена, носитель — нет).
    /// </summary>
    public int AppendIoFailures { get; private set; }

    /// <summary>Причина последней неудавшейся записи; null, если неудач не было.</summary>
    public string? LastAppendFailure { get; private set; }

    /// <summary>
    /// Сколько раз запись журнала была ОТКАЗАНА, потому что межпроцессная блокировка не получена.
    /// Печатается вызывающим: молчание об отказе неотличимо от «отказов не было».
    /// </summary>
    public int RefusedAppends { get; private set; }

    /// <summary>
    /// Сколько раз терминальная запись не удалась ПОСЛЕ выполненной мутации. Такая операция
    /// остаётся в журнале как требующая согласования: её исход известен только этому процессу.
    /// </summary>
    public int TerminalWriteFailures { get; private set; }

    /// <summary>Сколько ждать межпроцессную блокировку журнала при записи (значение по умолчанию).</summary>
    private static readonly TimeSpan AppendLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Освободить межпроцессную блокировку. Ручки на файл журнал не держит (см.
    /// <see cref="OpenAppend"/>), поэтому закрывать больше нечего; метод сохранён, потому что
    /// вызывающие владеют журналом через <c>using</c>.
    /// </summary>
    public void Dispose() => _fileGate.Dispose();
}

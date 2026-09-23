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
    private readonly object _gate = new();
    private readonly Dictionary<string, JournalRecord> _byOperation = new(StringComparer.Ordinal);

    /// <summary>Межпроцессная блокировка ЗАПИСИ по пути журнала. Чтение она не запрещает.</summary>
    private readonly NamedFileLock _fileGate;

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

    public OperationJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        _fileGate = NamedFileLock.For(Path, "journal");
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
        var locked = _fileGate.Enter(ReplayLockTimeout);
        if (!locked)
        {
            ReplayRanUnlocked = true;
        }

        try
        {
            ReplayCore();
        }
        finally
        {
            _fileGate.Exit();
        }
    }

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

                return new StartDecision(Proceed: existing.Outcome == JournalOutcome.InFlight, existing);
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

            _byOperation[operationId] = record;
            Append(record);
            return new StartDecision(true, null);
        }
    }

    public void Complete(string operationId, string? resultJson) => Finish(operationId, JournalOutcome.Succeeded, resultJson, null, needsReconciliation: false);

    public void Fail(string operationId, ErrorDto error) => Finish(operationId, JournalOutcome.Failed, null, error, needsReconciliation: false);

    public void Cancel(string operationId) => Finish(operationId, JournalOutcome.Cancelled, null, null, needsReconciliation: false);

    /// <summary>
    /// Record that the outcome cannot be known. Used on a budget timeout, a worker death, or a
    /// disconnect mid-call.
    /// </summary>
    public void MarkUnknown(string operationId, string reason) =>
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

    private void Finish(string operationId, JournalOutcome outcome, string? resultJson, ErrorDto? error, bool needsReconciliation)
    {
        lock (_gate)
        {
            if (!_byOperation.TryGetValue(operationId, out var existing))
            {
                return;
            }

            var updated = existing with
            {
                Outcome = outcome,
                ResultJson = resultJson ?? existing.ResultJson,
                Error = error ?? existing.Error,
                FinishedUtc = DateTimeOffset.UtcNow,
                NeedsReconciliation = needsReconciliation,
            };

            _byOperation[operationId] = updated;
            Append(updated);
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
    /// <remarks>
    /// Блокировка обязательна, и это измерено, а не подстраховано: <c>FileMode.Append</c> с одной
    /// записью байтов теряет записи при двух писателях (381 из 400 в пробе
    /// <c>scratch/_append_probe</c>), потому что позиция конца файла запоминается при ОТКРЫТИИ.
    /// Для журнала безопасности потерянная запись означает, что повтор после перезапуска выглядит
    /// как «не выполнялось», — то есть мутация может быть применена второй раз. Заодно не остаётся
    /// пожизненной ручки, из-за которой второй Хост не мог даже прочитать журнал.
    /// </remarks>
    private void Append(JournalRecord record)
    {
        var line = JsonSerializer.Serialize(record, JournalOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        var locked = _fileGate.Enter(AppendLockTimeout);
        try
        {
            using var stream = OpenAppend();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        finally
        {
            if (!locked)
            {
                // Запись всё равно сделана — но её совместность НЕ доказана, и это называется.
                AppendRanUnlocked = true;
            }

            _fileGate.Exit();
        }
    }

    /// <summary>Запись журнала хотя бы раз прошла без межпроцессной блокировки.</summary>
    public bool AppendRanUnlocked { get; private set; }

    /// <summary>Сколько ждать межпроцессную блокировку журнала.</summary>
    private static readonly TimeSpan AppendLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Освободить межпроцессную блокировку. Ручки на файл журнал не держит (см.
    /// <see cref="OpenAppend"/>), поэтому закрывать больше нечего; метод сохранён, потому что
    /// вызывающие владеют журналом через <c>using</c>.
    /// </summary>
    public void Dispose() => _fileGate.Dispose();
}

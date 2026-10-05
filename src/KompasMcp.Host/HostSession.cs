using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Journaling;
using KompasMcp.Host.Catalog;

namespace KompasMcp.Host;

/// <summary>
/// Владение CAD-сеансом одного Хоста: когда он держит журнал, очередь, Invoker и Worker — и когда
/// отдаёт их другому Хосту.
/// </summary>
/// <remarks>
/// <para>
/// ЧТО ИЗМЕНИЛОСЬ 04.10.2026. Прежде Хост захватывал владение на старте транспорта и оставался
/// владельцем до смерти процесса; второй чат получал отказ уже на <c>initialize</c> и терял ВСЕ
/// инструменты. Измерено 04.10.2026: вспомогательное обнаружение инструментов заняло владение
/// одним вызовом <c>tools/list</c>, и основной чат ответил <c>SESSION_OWNER_ACTIVE</c>. Здесь
/// владение — отдельный ресурс с явным захватом и явным освобождением, а MCP-транспорт живёт
/// независимо от него.
/// </para>
/// <para>
/// ПРАВИЛА, КОТОРЫЕ ЗДЕСЬ ВЫПОЛНЕНЫ:
/// <list type="bullet">
/// <item>старт транспорта, <c>initialize</c>, <c>tools/list</c> и диагностический <c>health</c>
/// владения НЕ берут;</item>
/// <item>обычный CAD-вызов берёт владение только как согласованный допуск РЕАЛЬНОЙ операции и
/// только когда прежний владелец не снимал его ЯВНО;</item>
/// <item>освобождение — процедура из одной критической секции: запрет новых вызовов, проверка
/// занятости, проверка несохранённых документов, подтверждённая остановка Worker и только затем
/// атомарная публикация <c>released</c>;</item>
/// <item>поколение захвата меняется при каждом acquire, поэтому запоздалый callback старого
/// поколения не обновляет состояние нового владельца.</item>
/// </list>
/// </para>
/// <para>
/// БЛОКИРОВКИ. <c>_transition</c> сериализует захват и освобождение внутри процесса; межпроцессная
/// исключительность — в <see cref="HostOwnership"/>. <c>_callGate</c> защищает счётчик активных
/// CAD-вызовов и флаг <c>_releasing</c>: именно их пара даёт «CAD-запрос не может проскочить
/// между проверкой и release». Порядок один — всегда <c>_transition</c> → <c>_callGate</c>, и
/// никогда наоборот; под <c>_callGate</c> выполняется только арифметика счётчика, поэтому COM и
/// IPC никогда не идут под ним, и deadlock нечем образовать.
/// </para>
/// </remarks>
public sealed class HostSession : IAsyncDisposable
{
    public const string StatusCommand = "session.status";
    public const string AcquireCommand = "session.acquire";
    public const string ReleaseCommand = "session.release";

    public const string StatusTool = "kompas_session_status";
    public const string AcquireTool = "kompas_acquire_session";
    public const string ReleaseTool = "kompas_release_session";

    /// <summary>
    /// Диагностика, которая владение НЕ берёт: <c>health</c> и <c>capabilities</c> обязаны отвечать
    /// и тогда, когда сеансом владеет другой чат.
    /// </summary>
    private static readonly string[] DiagnosticTools = { "kompas_health", "kompas_capabilities" };

    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Предел карты записанных исходов освобождения (см. <c>_releaseOutcomes</c>). Освобождение —
    /// терминальное действие: повторы нужны в пределах одного запроса, а не за всю жизнь процесса,
    /// поэтому карта ограничена и вытесняет самый старый id. Предел назван числом, а не «примерно»:
    /// молчание о нём читалось бы как «карта не растёт».
    /// </summary>
    private const int ReleaseReplayLimit = 64;

    private readonly HostOptions _options;
    private readonly HostLog _log;
    private readonly HostOwnership _ownership;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _callGate = new();

    private WorkerSupervisor? _worker;
    private OperationJournal? _journal;
    private ToolInvoker? _invoker;
    private int _activeCalls;
    private bool _releasing;
    private bool _disposed;

    /// <summary>
    /// Записанные исходы освобождения по <c>operation_id</c> — В ПАМЯТИ ПРОЦЕССА, а не в журнале:
    /// инструменты Хоста журнала операций не ведут. Ограничения названы прямо: перезапуск Хоста
    /// историю повторов не несёт, карта ограничена <see cref="ReleaseReplayLimit"/>, а при каждом
    /// новом захвате сеанса она очищается — новое поколение есть новый контекст, и повтор прежнего
    /// id не имеет права вернуть исход освобождения, выполненного в прошлом сеансе.
    /// </summary>
    private readonly Dictionary<string, ReleaseReplay> _releaseOutcomes = new(StringComparer.Ordinal);
    private readonly Queue<string> _releaseOrder = new();

    /// <summary>Отпечаток аргументов и готовый конверт, записанные для одного <c>operation_id</c>.</summary>
    private sealed record ReleaseReplay(string Fingerprint, ResultEnvelope<JsonNode?> Outcome);

    public HostSession(HostOptions options, HostOwnership ownership, HostLog log)
    {
        _options = options;
        _ownership = ownership;
        _log = log;
    }

    /// <summary>Принадлежит ли инструмент управлению сеансом.</summary>
    public static bool IsSessionTool(string? toolName) =>
        toolName is StatusTool or AcquireTool or ReleaseTool;

    /// <summary>Держит ли этот Хост сеанс прямо сейчас.</summary>
    public bool OwnsSession => _ownership.IsOwner;

    // ---------------------------------------------------------------------------------------------
    // Маршрутизация вызовов
    // ---------------------------------------------------------------------------------------------

    public async Task<ResultEnvelope<JsonNode?>> InvokeAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var tool = ToolCatalog.All.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
        {
            return UnknownTool(toolName);
        }

        switch (tool.WorkerCommand)
        {
            case StatusCommand:
                return Status();
            case AcquireCommand:
                return await AcquireAsync(cancellationToken).ConfigureAwait(false);
            case ReleaseCommand:
                return await ReleaseAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        // ДИАГНОСТИКА НЕ ЗАНИМАЕТ СЕАНС. health и capabilities отвечают и тогда, когда владеет
        // другой чат: иначе второй чат не мог бы узнать, почему у него нет работы, — и остался бы
        // без инструментов, как до этой правки.
        if (DiagnosticTools.Contains(tool.Name, StringComparer.Ordinal))
        {
            return !_ownership.IsOwner
                ? DiagnosticWithoutOwnership(toolName)
                : await DispatchAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }

        if (!_ownership.IsOwner)
        {
            var probe = _ownership.Probe();

            // СОГЛАСОВАННЫЙ ДОПУСК РЕАЛЬНОЙ ОПЕРАЦИИ. Только когда владения нет вовсе либо прежний
            // владелец снял его неявно (транспорт завершён). После ЯВНОГО release допуск запрещён:
            // иначе «освободил» и «продолжаю работать» стали бы одним и тем же состоянием.
            if (!probe.CanAcquire)
            {
                _log.Write("warn", "cad call refused: session owned by another host", new
                {
                    tool = toolName,
                    code = probe.RefusalCode ?? ErrorCodes.SessionOwnerActive,
                    owner_pid = probe.OwnerPid,
                    owner_state = probe.State?.ToString().ToLowerInvariant(),
                    reason = probe.Reason,
                });

                return Refusal(
                    probe.RefusalCode ?? ErrorCodes.SessionOwnerActive,
                    $"Сеансом КОМПАС владеет другой Хост: {probe.Reason}. Вызов отклонён до записи "
                    + "в журнал операций и до обращения к COM.",
                    RetryPolicy.Never,
                    OwnerDetails(probe),
                    remedy: probe.State == HostOwnerState.Releasing
                        ? "Дождитесь конца освобождения: kompas_session_status, затем kompas_acquire_session."
                        : $"Освободите сеанс у владельца (pid {probe.OwnerPid}) вызовом "
                          + "kompas_release_session, затем вызовите kompas_acquire_session здесь.");
            }

            if (probe.RequiresExplicitAcquire)
            {
                _log.Write("warn", "cad call refused: explicit acquire required", new
                {
                    tool = toolName,
                    code = ErrorCodes.SessionNotAcquired,
                    reason = probe.Reason,
                });

                return Refusal(
                    ErrorCodes.SessionNotAcquired,
                    "Сеанс освобождён владельцем ЯВНО, и обычный CAD-вызов владение не берёт: "
                    + "новый сеанс — это новый контекст, прежние document_id и revision недействительны. "
                    + probe.Reason,
                    RetryPolicy.Never,
                    OwnerDetails(probe),
                    remedy: "Вызовите kompas_acquire_session, затем kompas_connect и kompas_get_context.");
            }

            var acquired = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            if (acquired.Status != OperationStatus.Succeeded)
            {
                return acquired;
            }
        }

        return await DispatchAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполнить обычный вызов: вход под счётчиком активных вызовов, чтобы освобождение не могло
    /// пройти между допуском операции и её выполнением.
    /// </summary>
    private async Task<ResultEnvelope<JsonNode?>> DispatchAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        lock (_callGate)
        {
            if (_releasing)
            {
                return Refusal(
                    ErrorCodes.SessionReleaseBusy,
                    "Сеанс освобождается: новые CAD-вызовы не принимаются.",
                    RetryPolicy.AfterReconciliation,
                    null,
                    remedy: "Дождитесь конца освобождения (kompas_session_status) и захватите сеанс снова.");
            }

            _activeCalls++;
        }

        try
        {
            // СВЕРКА ПОКОЛЕНИЯ НА КАЖДОМ ВЫЗОВЕ. Возврат false означает, что запись владельца уже
            // не наша: отказ обязан прийти до COM, иначе «единственный владелец» осталось бы словом.
            if (!_ownership.MarkServing())
            {
                var probe = _ownership.Probe();
                var message = _ownership.LostOwnershipMessage();
                _log.Write("error", "tool call refused: ownership lost", new
                {
                    tool = toolName,
                    code = probe.OwnerAlive && !probe.SelfOwned ? ErrorCodes.SessionOwnerActive : ErrorCodes.SessionNotAcquired,
                    message,
                });

                return Refusal(
                    probe.OwnerAlive && !probe.SelfOwned ? ErrorCodes.SessionOwnerActive : ErrorCodes.SessionNotAcquired,
                    message,
                    RetryPolicy.Never,
                    OwnerDetails(probe),
                    remedy: "Вызовите kompas_acquire_session, затем получите новый контекст: "
                            + "document_id и revision прежнего сеанса недействительны.");
            }

            LogOwnershipProblem();

            var invoker = _invoker;
            if (invoker is null)
            {
                return Refusal(
                    ErrorCodes.SessionNotAcquired,
                    "Хост владеет записью сеанса, но журнал и очередь не созданы: работа без журнала "
                    + "безопасности не начинается.",
                    RetryPolicy.Never,
                    null,
                    remedy: "Повторите kompas_acquire_session.");
            }

            // ВЫЗОВ, ПЕРЕЗАПУСТИВШИЙ WORKER, ОБЯЗАН ЭТО НАЗВАТЬ.
            //
            // Прежние document_id после перезапуска недействительны: новый Worker не знает ни одного
            // документа, и ссылка на прежний адресует пустоту. Клиент, увидевший «инструмент ответил»,
            // обязан узнать об этом из ответа, а не из последующего отказа (дефект H3 ревью
            // 05.10.2026, обход через промежуточный вызов).
            var restartsBefore = _worker?.RestartCount ?? 0;
            var envelope = await invoker.InvokeAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);

            var restarted = _worker;
            if (restarted is not null && restarted.RestartCount > restartsBefore)
            {
                envelope = envelope with
                {
                    Warnings = envelope.Warnings.Concat(new[]
                    {
                        "Во время этого вызова Worker перезапускался: прежние document_id недействительны, "
                        + "а состояние документов прежнего сеанса НЕИЗВЕСТНО (правки могли остаться "
                        + "несохранёнными). Прочитайте структуру заново (kompas_list_documents, "
                        + "kompas_get_context). Освобождение сеанса потребует явного подтверждения "
                        + "acknowledge_unknown_document_state=true.",
                    }).ToArray(),
                };
            }

            return envelope;
        }
        finally
        {
            lock (_callGate)
            {
                _activeCalls--;
                Monitor.PulseAll(_callGate);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // kompas_session_status
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Состояние сеанса. Не захватывает и не восстанавливает владение, не запускает Worker и не
    /// обращается к COM.
    /// </summary>
    public ResultEnvelope<JsonNode?> Status()
    {
        var probe = _ownership.Probe();
        var state = Classify(probe);

        WorkerSupervisor? worker;
        lock (_callGate)
        {
            worker = _worker;
        }

        var node = new JsonObject
        {
            ["host_pid"] = Environment.ProcessId,
            ["session_state"] = state,
            ["can_acquire"] = probe.CanAcquire,
            ["requires_explicit_acquire"] = probe.RequiresExplicitAcquire,
            ["reason"] = probe.Reason,
            ["journal_path"] = _options.JournalPath,
            ["ownership_record"] = _ownership.RecordPath,
            ["owner"] = OwnerNode(probe),
            ["this_host"] = new JsonObject
            {
                ["owns_session"] = probe.SelfOwned,
                ["generation"] = _ownership.Generation,
                ["requests_served"] = _ownership.RequestsServed,
                // КАНАЛ СОЗДАН — ЕЩЁ НЕ ЗНАЧИТ «WORKER ЗАПУЩЕН». Процесс Worker стартует на первой
                // реальной команде, а не при захвате: ожидающий клиент не порождает CAD-канал.
                // Разница названа полями, чтобы «Worker есть» не читалось как «Worker работает».
                ["worker_channel"] = worker is null ? "not_created" : "created",
                ["worker_pid"] = worker?.WorkerProcessId,
                // ЛИПКИЙ ПРИЗНАК ВИДЕН В СТАТУСЕ. Иначе «почему освобождение отказывает» приходилось
                // бы выяснять из текста отказа: неизвестное состояние документов — самостоятельное
                // состояние сеанса, а не деталь одной команды.
                ["document_state_unknown"] = worker?.DocumentStateUnknown ?? false,
                ["document_state_unknown_reason"] = worker?.DocumentStateUnknownReason,
                ["worker_restarts"] = worker?.RestartCount ?? 0,
                ["active_calls"] = Volatile.Read(ref _activeCalls),
                ["releasing"] = Volatile.Read(ref _releasing),
            },
            ["remedy"] = RemedyFor(probe, state),
        };

        if (probe.RefusalCode is { } code)
        {
            node["refusal_code"] = code;
        }

        if (probe.RecordUnreadable || probe.LockUnavailable)
        {
            // ОШИБКА ЧТЕНИЯ НЕ РАВНА СВОБОДНОМУ СЕАНСУ, и это сказано в ответе, а не выведено из
            // пустого поля: «записи нет» и «запись не читается» — разные состояния с разной ценой.
            node["state_is_unknown"] = true;
        }

        var warnings = new List<string>();
        if (_ownership.TakeWriteProblem() is { } problem)
        {
            warnings.Add("запись владельца не обновлена: " + problem);
        }

        _log.Write("info", "session status", new { state, can_acquire = probe.CanAcquire, owner_pid = probe.OwnerPid });
        return Succeeded(node, warnings, caveats: new[] { "server_state_only" });
    }

    // ---------------------------------------------------------------------------------------------
    // kompas_acquire_session
    // ---------------------------------------------------------------------------------------------

    public async Task<ResultEnvelope<JsonNode?>> AcquireAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = _ownership.TryAcquire(explicitRequest: true);

            if (result.Outcome == OwnershipOutcome.AlreadyOwned)
            {
                // ПОВТОРНЫЙ ЗАХВАТ ТЕМ ЖЕ ВЛАДЕЛЬЦЕМ: второго Worker и второго поколения нет.
                return Succeeded(new JsonObject
                {
                    ["acquired"] = true,
                    ["already_owner"] = true,
                    ["new_generation"] = false,
                    ["generation"] = result.Generation,
                    ["session_state"] = "owned_by_self",
                    ["worker_pid"] = _worker?.WorkerProcessId,
                }, warnings: new[] { "Этот Хост уже владеет сеансом: повторный Worker не создан, поколение не сменено." });
            }

            if (result.Outcome != OwnershipOutcome.Acquired)
            {
                _log.Write("warn", "session acquire refused", new
                {
                    code = result.ErrorCode,
                    outcome = result.Outcome.ToString().ToLowerInvariant(),
                    owner_pid = result.Refusal?.OwnerPid,
                    owner_state = result.Refusal?.State?.ToString().ToLowerInvariant(),
                });

                return Refusal(
                    result.ErrorCode ?? ErrorCodes.SessionNotAcquired,
                    result.ErrorMessage ?? "Захват сеанса отклонён.",
                    // «Занято другим» и «освобождение не подтверждено» — оба состояния, которые
                    // меняются сами (владелец освободит / очистка завершится), поэтому повтор
                    // осмыслен. Остальные отказы захвата повтором не лечатся.
                    result.ErrorCode is ErrorCodes.SessionOwnerActive or ErrorCodes.SessionReleaseFailed
                        ? RetryPolicy.AfterReconciliation
                        : RetryPolicy.Never,
                    result.Refusal is null ? null : OwnerDetails(result.Refusal),
                    remedy: result.Remedy);
            }

            // ПОСЛЕ ЗАХВАТА — НОВЫЙ ЖУРНАЛ, НОВАЯ ОЧЕРЕДЬ, НОВЫЙ КОНТЕКСТ. Старый кеш не
            // используется: модель могла измениться, пока сеансом владел другой Хост.
            var problem = StartResources();
            if (problem is not null)
            {
                // Владение взято, а работать нельзя: отдать сеанс, а не притвориться владельцем.
                var probe = _ownership.Probe();
                if (probe.SelfOwned && _ownership.BeginRelease())
                {
                    _ownership.CompleteRelease();
                }

                lock (_callGate)
                {
                    _releasing = false;
                    Monitor.PulseAll(_callGate);
                }

                _log.Write("error", "session acquired but resources unavailable", new { problem });
                return Refusal(
                    ErrorCodes.JournalUnavailable,
                    $"Сеанс захвачен, но журнал и очередь не созданы: {problem}. Владение возвращено.",
                    RetryPolicy.Never,
                    new JsonObject { ["problem"] = problem },
                    remedy: "Проверьте доступность journal_path и повторите kompas_acquire_session.");
            }

            _log.Write("info", "session acquired", new
            {
                generation = result.Generation,
                took_over_from_pid = result.TookOverFromPid,
                took_over_from_live_owner = result.TookOverFromLiveOwner,
                recovered_in_flight = _journal?.RecoveredInFlight ?? 0,
                skipped_lines = _journal?.SkippedLines ?? 0,
            });

            if (_journal is { RecoveredInFlight: > 0 } journal)
            {
                _log.Write("warn", "journal recovered unfinished operations as outcome_unknown", new { count = journal.RecoveredInFlight });
            }

            if (_journal is { SkippedLines: > 0 } skipped)
            {
                _log.Write("warn", "journal replay skipped unreadable lines", new
                {
                    skipped = skipped.SkippedLines,
                    torn_tail = skipped.TornTail,
                    path = _options.JournalPath,
                });
            }

            // НОВОЕ ПОКОЛЕНИЕ — НОВЫЙ КОНТЕКСТ: записанные исходы освобождения прежнего сеанса
            // обесцениваются вместе со ссылками прежнего владельца. Иначе повтор прежнего
            // operation_id вернул бы исход освобождения, выполненного ДО этого захвата, и новый
            // сеанс ответил бы «освобождён» на освобождение, которого в нём не было.
            _releaseOutcomes.Clear();
            _releaseOrder.Clear();

            return Succeeded(new JsonObject
            {
                ["acquired"] = true,
                ["already_owner"] = false,
                ["new_generation"] = true,
                ["generation"] = result.Generation,
                ["session_state"] = "owned_by_self",
                ["took_over_from_pid"] = result.TookOverFromPid,
                ["took_over_from_live_owner"] = result.TookOverFromLiveOwner,
                ["journal_path"] = _options.JournalPath,
                // НОВЫЙ КОНТЕКСТ НАЗВАН ПРЯМОЙ ИНСТРУКЦИЕЙ, а не подразумевается: старые ссылки
                // намеренно не оживают, и молчание здесь читалось бы как «можно продолжать».
                ["next"] = new JsonArray("kompas_connect", "kompas_get_context"),
            }, warnings: new[]
            {
                "Это новый сеанс: document_id, revision и ссылки прежнего владельца недействительны.",
            });
        }
        finally
        {
            _transition.Release();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // kompas_release_session
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Освобождение сеанса с воспроизведением записанного исхода по <c>operation_id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ЗАЧЕМ ЗДЕСЬ <c>operation_id</c>. Строка <c>S03b</c> прибора <c>mcp-smoke.py</c> требует от
    /// инструмента с <c>destructiveHint=true</c> объявить <c>operation_id</c> (опубликованное правило
    /// §2.1). Объявление без поведения было бы «объявлено и проглочено», поэтому поле не только
    /// объявлено, но и ИСПОЛЬЗУЕТСЯ: повтор с тем же id отвечает записанным исходом и не выполняет
    /// освобождение заново.
    /// </para>
    /// <para>
    /// ЧТО ИМЕННО ВОСПРОИЗВОДИТСЯ. Только ТЕРМИНАЛЬНЫЙ УСПЕХ. Отказы (<c>DOCUMENT_DIRTY</c>,
    /// <c>SESSION_RELEASE_BUSY</c>) и незавершённые ветки (Worker не подтвердил остановку, состояние
    /// не опубликовано) НЕ записываются: их лечение — повторить вызов, и запись заставила бы повтор
    /// вечно возвращать тот же отказ вместо продолжения очистки. Запись — не журнал: перезапуск
    /// Хоста её не несёт, и это названо, а не подразумевается.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Запомнить исход повтора и удержать карту в пределе <see cref="ReleaseReplayLimit"/>.
    /// Вызывается под <c>_transition</c>, поэтому отдельной блокировки не требует.
    /// </summary>
    private void Remember(string operationId, string fingerprint, ResultEnvelope<JsonNode?> outcome)
    {
        if (!_releaseOutcomes.ContainsKey(operationId))
        {
            _releaseOrder.Enqueue(operationId);
        }

        _releaseOutcomes[operationId] = new ReleaseReplay(fingerprint, outcome);

        while (_releaseOutcomes.Count > ReleaseReplayLimit && _releaseOrder.Count > 0)
        {
            _releaseOutcomes.Remove(_releaseOrder.Dequeue());
        }
    }

    /// <summary>Записанный исход повтора с предупреждением, что обращения к Worker и COM не было.</summary>
    private static ResultEnvelope<JsonNode?> Replayed(ResultEnvelope<JsonNode?> recorded, string operationId) =>
        recorded with
        {
            OperationId = operationId,
            Warnings = recorded.Warnings
                .Concat(new[]
                {
                    "Запись в памяти процесса: освобождение с этим operation_id уже выполнено, "
                    + "повторного обращения к Worker и COM не было.",
                })
                .ToArray(),
        };

    /// <summary>
    /// Устойчивый отпечаток аргументов вызова — тот же приём, что у журнала операций
    /// (<c>ToolInvoker.Canonical</c>): поля упорядочены по имени, поэтому «те же аргументы» — это
    /// равенство строк, а не порядок появления в JSON.
    /// </summary>
    private static string Fingerprint(JsonObject arguments)
    {
        var ordered = new JsonObject();
        foreach (var pair in arguments.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ordered[pair.Key] = pair.Value?.DeepClone();
        }

        return ordered.ToJsonString(KompJson.Options);
    }

    public async Task<ResultEnvelope<JsonNode?>> ReleaseAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationId = JsonScalars.ReadString(arguments["operation_id"]);
            var fingerprint = operationId is { Length: > 0 } ? Fingerprint(arguments) : null;

            // ПОВТОР ПО ТОМУ ЖЕ id — ЗАПИСАННЫЙ ИСХОД, И ПРОВЕРЯЕТСЯ ОН ДО ПРОЦЕДУРЫ. Иначе повтор
            // выполнил бы освобождение заново (освобождать уже нечего) и ответил бы «не этим
            // запросом» — то есть исход зависел бы от того, дошёл ли первый ответ до клиента.
            if (operationId is { Length: > 0 } && _releaseOutcomes.TryGetValue(operationId, out var recorded))
            {
                if (!string.Equals(recorded.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Refusal(
                        ErrorCodes.OperationIdConflict,
                        $"operation_id {operationId} уже отвечал на другой вызов kompas_release_session: "
                        + "аргументы не совпадают. Журнала у инструментов Хоста нет, поэтому "
                        + "совпадение проверяется по отпечатку вызова, записанному в память процесса.",
                        RetryPolicy.Never,
                        new JsonObject { ["operation_id"] = operationId },
                        remedy: "Повторите освобождение с тем же набором аргументов либо возьмите новый operation_id.");
                }

                return Replayed(recorded.Outcome, operationId);
            }

            // ЗАПИСЫВАЕТСЯ ТОЛЬКО ТЕРМИНАЛЬНЫЙ УСПЕХ: отказ (DOCUMENT_DIRTY, SESSION_RELEASE_BUSY) и
            // незавершённая ветка (Worker не подтвердил остановку, состояние не опубликовано)
            // лечатся ПОВТОРОМ того же вызова, и запись заставила бы повтор вечно возвращать тот же
            // отказ вместо продолжения очистки.
            ResultEnvelope<JsonNode?> Complete(ResultEnvelope<JsonNode?> envelope)
            {
                if (operationId is not { Length: > 0 } || envelope.Status != OperationStatus.Succeeded)
                {
                    return envelope;
                }

                var stored = envelope with { OperationId = operationId };
                Remember(operationId, fingerprint!, stored);
                return stored;
            }

            var probe = _ownership.Probe();

            if (!probe.SelfOwned)
            {
                if (!probe.CanAcquire)
                {
                    // ЧУЖОЕ ВЛАДЕНИЕ НЕ ТРОГАЕМ: «освободить чужой сеанс» было бы обходом защиты.
                    return Refusal(
                        ErrorCodes.SessionOwnerActive,
                        "Освободить нельзя: сеансом владеет другой Хост. " + probe.Reason,
                        RetryPolicy.AfterReconciliation,
                        OwnerDetails(probe),
                        remedy: $"Освобождайте сеанс у владельца (pid {probe.OwnerPid}); "
                                + "здесь вызовите kompas_session_status и дождитесь освобождения.");
                }

                // ПОВТОРНЫЙ RELEASE БЕЗ ВЛАДЕНИЯ БЕЗОПАСЕН: «уже освобождён» отличается от
                // «освобождён этим запросом» отдельным полем, а не только текстом.
                return Complete(Succeeded(new JsonObject
                {
                    ["released"] = true,
                    ["released_by_this_request"] = false,
                    ["session_state"] = Classify(probe),
                    ["reason"] = "владения у этого Хоста не было: " + probe.Reason,
                    ["owner"] = OwnerNode(probe),
                    ["remedy"] = probe.RequiresExplicitAcquire
                        ? "Сеанс свободен; для работы вызовите kompas_acquire_session."
                        : "Сеанс свободен; владение будет взято первым CAD-вызовом.",
                }));
            }

            var continuation = probe.State == HostOwnerState.Releasing;

            // ПЕРЕХОД owned → releasing: с этого момента владелец сохраняет исключительное право,
            // и другой Хост сеанс захватить не может.
            if (!continuation && !_ownership.BeginRelease())
            {
                return Refusal(
                    ErrorCodes.SessionReleaseFailed,
                    "Не удалось перевести сеанс в состояние releasing: владение не подтверждено.",
                    RetryPolicy.AfterReconciliation,
                    OwnerDetails(probe),
                    remedy: "Повторите kompas_release_session; если отказ повторяется, прочитайте "
                            + "kompas_session_status.");
            }

            // ЗАПРЕТ НОВЫХ CAD-ВЫЗОВОВ И ПРОВЕРКА ЗАНЯТОСТИ — ОДИН ЗАХВАТ ОДНОГО ЗАМКА. Вызов,
            // успевший войти до этого момента, виден в счётчике; вызов, идущий после, входа не
            // получит. Между проверкой и очисткой проскочить нечем.
            int active;
            lock (_callGate)
            {
                _releasing = true;
                active = _activeCalls;
            }

            var work = DescribePendingWork(active, out var workNode);
            if (work is not null)
            {
                // ОТКАЗ ДО ОЧИСТКИ ВОЗВРАЩАЕТ owned: владелец остаётся владельцем, а не «почти
                // свободным», иначе следующий CAD-вызов упёрся бы в полуотпущенный сеанс.
                _ownership.AbortRelease();
                lock (_callGate)
                {
                    _releasing = false;
                    Monitor.PulseAll(_callGate);
                }

                _log.Write("warn", "session release refused: work in progress", new { reason = work });
                return Refusal(
                    ErrorCodes.SessionReleaseBusy,
                    work,
                    RetryPolicy.AfterReconciliation,
                    workNode,
                    remedy: "Дождитесь завершения операций (повтор вызова с тем же operation_id вернёт "
                            + "записанный исход) и повторите kompas_release_session.");
            }

            // НЕСОХРАНЁННЫЕ ДОКУМЕНТЫ — ОТКАЗ ПО УМОЛЧАНИЮ. Никакого неявного сохранения и
            // никакого отказа от правок: закрытие документа документированным ksDocument3D.close()
            // не обещает сохранения, поэтому молча отдать сеанс значило бы потерять модель.
            //
            // РЕШЕНИЕ «МОЖНО ЛИ ОСВОБОЖДАТЬ» — ЧИСТАЯ ФУНКЦИЯ (ReleaseGuard). Сюда приходят только
            // ФАКТЫ: запускался ли Worker, жив ли канал без перезапуска, стоит ли липкий признак
            // неизвестного состояния документов, подтвердил ли клиент это состояние, прочитана ли
            // опись и сколько в ней грязных документов. Разложенное по ветвям, это правило один раз
            // уже пропустило обход (дефект H3): после обрыва промежуточный вызов поднимал новый
            // Worker, опись была пуста и «честна», и освобождение проходило.
            var worker = _worker;
            var acknowledge = JsonScalars.ReadBool(arguments["acknowledge_unknown_document_state"]) == true;

            var inventory = await InventoryAsync(cancellationToken).ConfigureAwait(false);
            var inventoryRead = inventory.Error is null;
            var dirty = inventoryRead ? DirtyDocuments(inventory.Payload) : new List<JsonObject>();

            var decision = ReleaseGuard.Decide(new ReleaseFacts(
                WorkerStarted: worker is { HasStarted: true },
                CanSendWithoutRestart: worker is null || worker.CanSendWithoutRestart,
                DocumentStateUnknown: worker?.DocumentStateUnknown == true,
                AcknowledgeUnknownDocumentState: acknowledge,
                InventoryRead: inventoryRead,
                DirtyCount: dirty.Count));

            if (!decision.Proceed)
            {
                _ownership.AbortRelease();
                lock (_callGate)
                {
                    _releasing = false;
                    Monitor.PulseAll(_callGate);
                }

                _log.Write("warn", "session release refused", new
                {
                    code = decision.RefusalCode,
                    inventory_error = inventory.Error?.Code,
                    dirty = dirty.Count,
                    document_state_unknown = worker?.DocumentStateUnknown,
                });

                return ReleaseRefusal(decision, worker, inventory.Error, dirty);
            }

            // ЯВНОЕ ПОДТВЕРЖДЕНИЕ НЕИЗВЕСТНОГО СОСТОЯНИЯ: клиент взял на себя, что правки прежнего
            // Worker могли остаться в КОМПАС несохранёнными. Это называется в предупреждении, а не
            // остаётся молчанием: «освобождено» и «правки могли пропасть» — разные утверждения.
            var acknowledgedUnknown = acknowledge && worker?.DocumentStateUnknown == true;
            var unknownReason = worker?.DocumentStateUnknownReason;

            // ОЧИСТКА: Worker — очередь, Invoker, журнал. Worker останавливается ПЕРВЫМ, потому что
            // это единственный процесс, который выполняет COM и пишет в журнал операций.
            var stop = await StopWorkerAsync().ConfigureAwait(false);
            await DisposeResourcesAsync().ConfigureAwait(false);

            // Признак снимается ТОЛЬКО теперь — после подтверждённой остановки Worker. Снять его
            // раньше значило бы объявить состояние известным по одному лишь намерению клиента.
            worker?.ClearDocumentStateUnknown("освобождение с явным подтверждением неизвестного состояния");

            var stopNode = new JsonObject
            {
                ["worker_pid"] = stop.Pid,
                ["confirmed"] = stop.Confirmed,
                ["exit_code"] = stop.ExitCode,
                ["kill_used"] = stop.KillUsed,
            };

            if (stop.Problem is not null)
            {
                stopNode["problem"] = stop.Problem;
            }

            if (!stop.Confirmed)
            {
                // ЧАСТИЧНАЯ ОЧИСТКА: владение НЕ публикуется свободным. Состояние остаётся
                // releasing, повтор продолжает очистку, а не повторяет разрушительное действие.
                _log.Write("error", "session release: worker stop not confirmed", new { worker_pid = stop.Pid, problem = stop.Problem });

                return Refusal(
                    ErrorCodes.SessionReleaseFailed,
                    $"Worker (pid {stop.Pid}) не подтвердил завершение: {stop.Problem}. Сеанс НЕ "
                    + "объявлен свободным — иначе следующий владелец начал бы работу при живом "
                    + "COM-вызове.",
                    RetryPolicy.AfterReconciliation,
                    new JsonObject
                    {
                        ["session_state"] = "releasing",
                        ["worker"] = stopNode,
                        ["safe_recovery"] = "повтор kompas_release_session продолжает очистку; "
                                            + "освобождать сеанс вручную удалением записи владельца нельзя",
                    },
                    remedy: "Повторите kompas_release_session. Убивать КОМПАС или удалять запись "
                            + "владельца для обхода этой ошибки нельзя.");
            }

            // ЛИШЬ ПОСЛЕ ПОДТВЕРЖДЁННОЙ ОЧИСТКИ — АТОМАРНАЯ ПУБЛИКАЦИЯ released.
            if (!_ownership.CompleteRelease())
            {
                _log.Write("error", "session release: state not published", new { problem = _ownership.LastWriteProblem });

                return Refusal(
                    ErrorCodes.SessionReleaseFailed,
                    "Worker остановлен, но состояние released не записано: "
                    + (_ownership.LastWriteProblem ?? "причина не названа")
                    + ". Успешным освобождением это не считается.",
                    RetryPolicy.AfterReconciliation,
                    new JsonObject
                    {
                        ["session_state"] = "releasing",
                        ["worker"] = stopNode,
                        ["safe_recovery"] = "повтор kompas_release_session продолжит с записи состояния",
                    },
                    remedy: "Повторите kompas_release_session: Worker уже остановлен, второй раз "
                            + "разрушительного действия не выполняется.");
            }

            lock (_callGate)
            {
                _releasing = false;
                Monitor.PulseAll(_callGate);
            }

            _log.Write("info", "session released", new
            {
                worker_pid = stop.Pid,
                kill_used = stop.KillUsed,
                documents_in_session = DocumentCount(inventory.Payload),
            });

            return Complete(Succeeded(new JsonObject
            {
                ["released"] = true,
                ["released_by_this_request"] = true,
                ["session_state"] = "released",
                ["worker"] = stopNode,
                ["documents_in_session"] = DocumentCount(inventory.Payload),
                ["unknown_document_state_acknowledged"] = acknowledgedUnknown,
                ["next"] = "kompas_acquire_session",
            }, warnings: BuildReleaseWarnings(stop, acknowledgedUnknown, unknownReason),
            caveats: new[] { "cad_session_ended_documents_closed" }));
        }
        finally
        {
            _transition.Release();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Завершение транспорта
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Транспорт завершён. Тот же механизм исключительности, что и у явного release, но итогом
    /// становится <see cref="HostOwnerState.Free"/>: владение снято неявно, и первый CAD-вызов
    /// следующего чата берёт его согласованным допуском.
    /// </summary>
    public async Task OnTransportEndAsync()
    {
        await _transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var probe = _ownership.Probe();
            if (!probe.SelfOwned)
            {
                // НЕ ВЛАДЕЛЕЦ: чужое поколение не трогаем. Это и есть «старый finally не вправе
                // освободить чужое поколение».
                return;
            }

            var state = probe.State;
            if (state == HostOwnerState.Serving && !_ownership.BeginDrain())
            {
                _log.Write("warn", "transport end: draining not recorded", new { record = _ownership.RecordPath });
                return;
            }

            lock (_callGate)
            {
                _releasing = true;
            }

            var stop = await StopWorkerAsync().ConfigureAwait(false);
            await DisposeResourcesAsync().ConfigureAwait(false);

            var completed = state == HostOwnerState.Releasing
                ? _ownership.CompleteRelease()
                : _ownership.CompleteDrain();

            if (!completed)
            {
                _log.Write("error", "transport end: release state not published", new
                {
                    state = state?.ToString().ToLowerInvariant(),
                    worker_confirmed = stop.Confirmed,
                    problem = _ownership.LastWriteProblem,
                });
                return;
            }

            _log.Write("info", "transport end: session freed", new
            {
                previous_state = state?.ToString().ToLowerInvariant(),
                worker_pid = stop.Pid,
                worker_confirmed = stop.Confirmed,
            });
        }
        finally
        {
            lock (_callGate)
            {
                _releasing = false;
                Monitor.PulseAll(_callGate);
            }

            _transition.Release();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Ресурсы сеанса
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Создать журнал, очередь и Invoker заново. Возвращает текст проблемы или null.
    /// </summary>
    /// <remarks>
    /// Журнал обязателен: без него перезапуск не отличает «не выполнено» от «выполнено, но ответ
    /// потерян». Поэтому недоступный журнал — отказ захвата, а не предупреждение.
    /// </remarks>
    private string? StartResources()
    {
        try
        {
            _journal = new OperationJournal(_options.JournalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"журнал операций '{_options.JournalPath}' недоступен для записи: {ex.Message}";
        }

        try
        {
            // Supervisor создаётся, но процесс Worker НЕ запускается: он стартует на первой реальной
            // команде. Ожидающий клиент поэтому не порождает CAD-канал вообще.
            _worker = new WorkerSupervisor(_options, _log);
            _invoker = new ToolInvoker(_options, _worker, _journal, _log);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _journal.Dispose();
            _journal = null;
            return $"очередь CAD-команд не создана: {ex.Message}";
        }
    }

    private async Task DisposeResourcesAsync()
    {
        if (_invoker is not null)
        {
            try
            {
                await _invoker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Write("warn", "invoker disposal failed", new { message = ex.Message });
            }

            _invoker = null;
        }

        if (_journal is not null)
        {
            _journal.Dispose();
            _journal = null;
        }

        if (_worker is not null)
        {
            try
            {
                await _worker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Write("warn", "worker disposal failed", new { message = ex.Message });
            }

            _worker = null;
        }
    }

    private async Task<WorkerSupervisor.WorkerStopResult> StopWorkerAsync()
    {
        var worker = _worker;
        if (worker is null)
        {
            // Worker не запускался: некому выполнять COM и некому писать в журнал.
            return new WorkerSupervisor.WorkerStopResult(null, true, null, false, null);
        }

        try
        {
            return await worker.StopAndConfirmAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or KompasContractException)
        {
            _log.Write("error", "worker stop failed", new { type = ex.GetType().Name, message = ex.Message });
            return new WorkerSupervisor.WorkerStopResult(worker.WorkerProcessId, false, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Опись документов сеанса — решение об освобождении без потери правок.
    /// </summary>
    private async Task<(JsonNode? Payload, ErrorDto? Error)> InventoryAsync(CancellationToken cancellationToken)
    {
        var worker = _worker;

        // WORKER НЕ ЗАПУСКАЛСЯ — КОМ-СЕАНСА НЕТ, И ДОКУМЕНТОВ НЕТ. Запускать процесс ради одной
        // описи нельзя: это породило бы CAD-канал ровно в тот момент, когда сеанс отдают.
        if (worker is null || !worker.HasStarted)
        {
            return (null, null);
        }

        // ОПИСЬ НЕ ЗАПРАШИВАЕТСЯ ПЕРЕЗАПУСКОМ WORKER.
        //
        // Прежде здесь был обычный `SendAsync` → `EnsureStartedAsync`: при сломанном канале он
        // останавливал прежний Worker (20 с ожидания, затем убийство) и поднимал НОВЫЙ. Новый
        // Worker не знает ни одного документа → опись пуста → `dirty=0` → освобождение проходит,
        // хотя состояние модели НЕИЗВЕСТНО. То есть защита DOCUMENT_DIRTY не срабатывала ровно
        // тогда, когда она и нужна (дефект H3 ревью 05.10.2026). Теперь сломанный канал — это
        // отказ описи, а не «правок нет».
        if (!worker.CanSendWithoutRestart)
        {
            _log.Write("warn", "session inventory unavailable: worker channel broken", new { worker_pid = worker.WorkerProcessId });
            return (null, new ErrorDto(
                ErrorCodes.SessionReleaseFailed,
                "Канал к Worker сломан: опись документов получить нельзя, состояние документов " +
                "НЕИЗВЕСТНО. Освобождение не может утверждать, что правок нет. Перезапуск Worker " +
                "ради описи запрещён: он потерял бы все документы сеанса и дал бы пустую опись.",
                RetryPolicy.AfterReconciliation,
                null,
                false,
                new JsonObject { ["reason"] = "worker_channel_broken" }));
        }

        try
        {
            var frame = await worker.SendWithoutRestartAsync(
                WorkerCommands.SessionInventory,
                null,
                InventoryTimeout,
                cancellationToken).ConfigureAwait(false);

            if (frame is null)
            {
                return (null, new ErrorDto(
                    ErrorCodes.SessionReleaseFailed,
                    "Опись документов не получена: канал к Worker оборвался во время запроса, " +
                    "состояние документов НЕИЗВЕСТНО.",
                    RetryPolicy.AfterReconciliation,
                    null,
                    false,
                    new JsonObject { ["reason"] = "inventory_lost_mid_request" }));
            }

            return (frame.Payload, frame.Error);
        }
        catch (KompasContractException ex)
        {
            return (null, ex.ToErrorDto());
        }
        catch (IOException ex)
        {
            return (null, new ErrorDto(ErrorCodes.WorkerUnresponsive, "Канал к Worker оборвался: " + ex.Message,
                RetryPolicy.AfterReconciliation, null, false, null));
        }
        catch (OperationCanceledException)
        {
            return (null, new ErrorDto(ErrorCodes.CancelNotConfirmed,
                "Опись документов отменена; фактическое состояние сеанса не подтверждено.",
                RetryPolicy.AfterReconciliation, null, false, null));
        }
    }

    private static List<JsonObject> DirtyDocuments(JsonNode? payload)
    {
        var dirty = new List<JsonObject>();
        if (payload is not JsonObject root || root["documents"] is not JsonArray documents)
        {
            return dirty;
        }

        foreach (var document in documents)
        {
            if (document is not JsonObject entry)
            {
                continue;
            }

            if (entry["dirty"]?.GetValue<bool>() == true || entry["dirty"]?.ToString() == "true")
            {
                dirty.Add(entry);
            }
        }

        return dirty;
    }

    private static int DocumentCount(JsonNode? payload) =>
        payload is JsonObject root && root["documents"] is JsonArray documents ? documents.Count : 0;

    /// <summary>
    /// Есть ли незавершённая работа. Пустая очередь НЕ достаточна: учитываются активные вызовы и
    /// операции, чей синхронный ответ уже ушёл, а КОМПАС ещё работает.
    /// </summary>
    private string? DescribePendingWork(int activeCalls, out JsonObject? node)
    {
        var invoker = _invoker;
        var inFlight = invoker is null ? Array.Empty<string>() : invoker.InFlightOperationIds();
        var queued = invoker is null ? 0 : QueueDepth(invoker);

        node = new JsonObject
        {
            ["active_calls"] = activeCalls,
            ["operations_in_flight"] = inFlight.Count,
            ["queued_operations"] = queued,
        };

        if (inFlight.Count > 0)
        {
            node["operation_ids"] = new JsonArray(inFlight.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        }

        if (activeCalls > 0 && inFlight.Count > 0)
        {
            return $"Освобождение отклонено: выполняется {activeCalls} вызов(ов) и "
                   + $"{inFlight.Count} операция(ий) после ответа клиенту.";
        }

        if (activeCalls > 0)
        {
            return $"Освобождение отклонено: выполняется {activeCalls} вызов(ов).";
        }

        if (inFlight.Count > 0)
        {
            return $"Освобождение отклонено: {inFlight.Count} операция(ий) ещё выполняется в КОМПАС, "
                   + "хотя ответ клиенту уже отправлен. Пустая очередь свободным сеанс не делает.";
        }

        if (queued > 0)
        {
            return $"Освобождение отклонено: в очереди CAD-команд {queued} операция(ий).";
        }

        return null;
    }

    private static long QueueDepth(ToolInvoker invoker) =>
        invoker.QueueStatistics().TryGetValue("queued", out var depth) ? depth : 0;

    private static List<string> BuildReleaseWarnings(
        WorkerSupervisor.WorkerStopResult stop,
        bool acknowledgedUnknown = false,
        string? unknownReason = null)
    {
        var warnings = new List<string>
        {
            "Сеанс КОМПАС завершён: зарегистрированные документы закрыты, следующий сеанс начнётся с "
            + "нового контекста (kompas_connect → kompas_get_context).",
        };

        if (acknowledgedUnknown)
        {
            // «ОСВОБОЖДЕНО» И «ПРАВКИ МОГЛИ ПРОПАСТЬ» — РАЗНЫЕ УТВЕРЖДЕНИЯ. Освобождение выполнено по
            // явному подтверждению клиента, и цена этого подтверждения обязана быть произнесена.
            warnings.Add(
                "Освобождение выполнено с acknowledge_unknown_document_state=true: состояние "
                + "документов прежнего Worker было НЕИЗВЕСТНО"
                + (unknownReason is null ? string.Empty : $" ({unknownReason})")
                + ". Правки прежнего Worker могли остаться в КОМПАС несохранёнными — сервер этого не "
                + "проверял и не проверяет.");
        }

        if (stop.KillUsed)
        {
            warnings.Add($"Worker (pid {stop.Pid}) снят убийством процесса: он не вышел за отведённое "
                         + "время, и его КОМПАС мог остаться запущенным. Дерево процессов не убивалось "
                         + "намеренно: launched-экземпляр КОМПАС — потомок Worker.");
        }

        return warnings;
    }

    /// <summary>
    /// Отказ освобождения по решению <see cref="ReleaseGuard"/>: код, политика повтора и подробности
    /// зависят от ПРИЧИНЫ, а не сведены к одной формулировке.
    /// </summary>
    /// <remarks>
    /// Разные причины — разные выходы: неизвестное состояние документов снимается явным
    /// подтверждением, грязные документы — сохранением или закрытием, недоступная опись — повтором.
    /// Одна общая формулировка заставила бы клиента угадывать выход, а это ровно тот случай, когда
    /// «сообщение об ошибке» перестаёт быть инструкцией.
    /// </remarks>
    private static ResultEnvelope<JsonNode?> ReleaseRefusal(
        ReleaseDecision decision,
        WorkerSupervisor? worker,
        ErrorDto? inventoryError,
        List<JsonObject> dirty)
    {
        if (decision.RefusalCode == ErrorCodes.DocumentStateUnknown)
        {
            return Refusal(
                ErrorCodes.DocumentStateUnknown,
                decision.Reason!,
                RetryPolicy.AfterReconciliation,
                new JsonObject
                {
                    ["reason"] = "document_state_unknown",
                    ["worker_restarts"] = worker?.RestartCount,
                    ["worker_state_reason"] = worker?.DocumentStateUnknownReason,
                    ["may_have_unsaved_edits"] = true,
                    ["acknowledge_parameter"] = "acknowledge_unknown_document_state",
                },
                remedy: "Сверьте модель в КОМПАС с ожиданием (объём, число тел, история признаков) и, "
                        + "если потеря правок допустима, повторите kompas_release_session с "
                        + "acknowledge_unknown_document_state=true — это ЕДИНСТВЕННЫЙ документированный "
                        + "выход: сервер не проверял, сохранились ли правки прежнего Worker.");
        }

        if (decision.RefusalCode == ErrorCodes.DocumentDirty)
        {
            return Refusal(
                ErrorCodes.DocumentDirty,
                decision.Reason!,
                RetryPolicy.Never,
                new JsonObject
                {
                    ["documents"] = new JsonArray(dirty.Select(d => (JsonNode)d.DeepClone()).ToArray()),
                    ["document_count"] = dirty.Count,
                },
                remedy: "Сохраните документы kompas_save_document (или закройте их "
                        + "kompas_close_document с явной политикой) и повторите kompas_release_session.");
        }

        return Refusal(
            ErrorCodes.SessionReleaseFailed,
            inventoryError is null
                ? decision.Reason!
                : $"Не удалось получить опись документов сеанса: {inventoryError.Message}. "
                  + "Освобождение отменено: без описи нельзя утверждать, что правок нет.",
            RetryPolicy.AfterReconciliation,
            new JsonObject { ["inventory_error"] = inventoryError?.Code ?? "inventory_not_read" },
            remedy: "Повторите kompas_release_session; если отказ повторяется, закройте "
                    + "документы kompas_close_document и повторите.");
    }

    // ---------------------------------------------------------------------------------------------
    // Классификация и оформление
    // ---------------------------------------------------------------------------------------------

    private static string Classify(SessionProbe probe)
    {
        if (probe.LockUnavailable || probe.RecordUnreadable)
        {
            return "unknown";
        }

        if (probe.SelfOwned)
        {
            return probe.State switch
            {
                HostOwnerState.Releasing or HostOwnerState.Draining => "releasing",
                _ => "owned_by_self",
            };
        }

        if (!probe.CanAcquire)
        {
            return "owned_by_other";
        }

        return probe.RequiresExplicitAcquire ? "released" : "free";
    }

    private static string RemedyFor(SessionProbe probe, string state) => state switch
    {
        "owned_by_self" => "Сеанс у этого Хоста: работайте обычными CAD-вызовами.",
        "owned_by_other" => $"Освободите сеанс у владельца (pid {probe.OwnerPid}) вызовом "
                            + "kompas_release_session, затем вызовите kompas_acquire_session здесь.",
        "releasing" => "Идёт освобождение: повторите kompas_session_status и, когда сеанс станет "
                       + "свободен, вызовите kompas_acquire_session.",
        "released" => "Сеанс свободен, но владение берётся только ЯВНО: kompas_acquire_session.",
        "free" => "Сеанс свободен: владение будет взято первым CAD-вызовом либо явным "
                  + "kompas_acquire_session.",
        _ => "Состояние не определено: проверьте доступность записи владельца "
             + "и повторите kompas_session_status.",
    };

    private static JsonObject OwnerNode(SessionProbe probe) => new()
    {
        ["pid"] = probe.OwnerPid,
        ["generation"] = probe.OwnerGeneration,
        ["state"] = probe.State?.ToString().ToLowerInvariant(),
        ["requests_served"] = probe.OwnerRequestsServed,
        ["alive"] = probe.OwnerAlive,
        ["alive_reason"] = probe.AliveReason,
        ["is_this_host"] = probe.SelfOwned,
    };

    private static JsonObject? OwnerDetails(SessionProbe? probe) => probe is null
        ? null
        : new JsonObject { ["owner"] = OwnerNode(probe), ["reason"] = probe.Reason };

    /// <summary>
    /// Диагностика без владения: отвечена локально, без Worker и без COM.
    /// </summary>
    private ResultEnvelope<JsonNode?> DiagnosticWithoutOwnership(string toolName)
    {
        var probe = _ownership.Probe();
        var state = Classify(probe);

        var node = new JsonObject
        {
            ["host_pid"] = Environment.ProcessId,
            ["cad_channel"] = "not_started",
            ["session_state"] = state,
            ["reason"] = probe.Reason,
            ["remedy"] = RemedyFor(probe, state),
        };

        if (string.Equals(toolName, "kompas_capabilities", StringComparison.Ordinal))
        {
            // КАТАЛОГ ПУБЛИКУЕТСЯ И БЕЗ ВЛАДЕНИЯ: «инструментов не видно» и «инструменты есть, но
            // сеанс не наш» — разные вещи, и смешивать их значило бы повторить дефект, из-за чего
            // второй чат остался без инструментов вовсе.
            node["tools"] = new JsonArray(ToolCatalog.All.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray());
            node["tool_count"] = ToolCatalog.All.Count;
        }
        else
        {
            node["worker"] = "not_started";
            node["queue"] = new JsonObject { ["queued"] = 0, ["capacity"] = _options.QueueCapacity };
        }

        return Succeeded(node,
            warnings: new[]
            {
                "Worker не запущен: этот Хост не владеет сеансом КОМПАС. Диагностика отвечена "
                + "локально, обращения к COM не было.",
            },
            caveats: new[] { "cad_channel_not_started" });
    }

    private void LogOwnershipProblem()
    {
        if (_ownership.TakeWriteProblem() is { } problem)
        {
            _log.Write("warn", "ownership record not refreshed", new { record = _ownership.RecordPath, problem });
        }
    }

    private static ResultEnvelope<JsonNode?> Succeeded(
        JsonNode? result,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? caveats = null) => new()
    {
        Status = OperationStatus.Succeeded,
        Result = result,
        Verification = new VerificationDto(
            VerificationLevel.ArgumentValidated,
            Array.Empty<NamedCheck>(),
            caveats is null ? new[] { "no_effect_on_model" } : caveats.ToArray()),
        Warnings = warnings?.ToList() ?? new List<string>(),
    };

    private static ResultEnvelope<JsonNode?> Refusal(
        string code,
        string message,
        RetryPolicy retryPolicy,
        JsonObject? details,
        string? remedy)
    {
        var node = (JsonObject?)details?.DeepClone() ?? new JsonObject();
        if (remedy is not null)
        {
            node["remedy"] = remedy;
        }

        return new ResultEnvelope<JsonNode?>
        {
            Status = OperationStatus.Failed,
            Verification = new VerificationDto(VerificationLevel.None, Array.Empty<NamedCheck>(), new[] { "no_effect_on_model" }),
            Error = new ErrorDto(code, message, retryPolicy, null, false, node.Count == 0 ? null : node),
            Warnings = new List<string>(),
        };
    }

    private static ResultEnvelope<JsonNode?> UnknownTool(string toolName) => new()
    {
        Status = OperationStatus.Failed,
        Verification = new VerificationDto(VerificationLevel.None, Array.Empty<NamedCheck>(), new[] { "no_effect_on_model" }),
        Error = new ErrorDto(
            ErrorCodes.CapabilityUnavailable,
            $"Инструмент '{toolName}' не зарегистрирован в этой сборке. Актуальный список даёт kompas_capabilities.",
            RetryPolicy.Never,
            null,
            false,
            new JsonObject { ["available"] = new JsonArray(ToolCatalog.All.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray()) }),
        Warnings = new List<string>(),
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeResourcesAsync().ConfigureAwait(false);
        _transition.Dispose();
    }
}

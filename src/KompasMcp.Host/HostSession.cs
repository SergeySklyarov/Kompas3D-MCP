using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Journaling;
using KompasMcp.Host.Catalog;

namespace KompasMcp.Host;

/// <summary>Ownership of one Host's CAD session: when it holds the journal, queue, Invoker and Worker —
/// and when it hands them to another Host.</summary>
/// <remarks>INVARIANT: ownership is a separate resource with explicit acquire and release, while the
/// MCP transport lives independently of it. Transport start, <c>initialize</c>, <c>tools/list</c> and
/// diagnostic <c>health</c> take NO ownership; an ordinary CAD call takes it only as a coordinated
/// admission of a REAL operation, and only when the previous owner did not release it EXPLICITLY.
/// History: docs/decisions/host.md#ownership-model</remarks>
public sealed class HostSession : IAsyncDisposable
{
    public const string StatusCommand = "session.status";
    public const string AcquireCommand = "session.acquire";
    public const string ReleaseCommand = "session.release";

    public const string StatusTool = "kompas_session_status";
    public const string AcquireTool = "kompas_acquire_session";
    public const string ReleaseTool = "kompas_release_session";
    public const string CapabilitiesTool = "kompas_capabilities";

    /// <summary>Diagnostics that take NO ownership: <c>health</c> and <c>capabilities</c> must answer
    /// even when another chat owns the session.</summary>
    private static readonly string[] DiagnosticTools = { "kompas_health", CapabilitiesTool };

    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The bound of the release-outcome map (see <c>_releaseOutcomes</c>). Release is a
    /// terminal action: replays are needed within one request, not for the process's whole life, so
    /// the map evicts the oldest id. The bound is named as a number, not "roughly".</summary>
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

    /// <summary>Release outcomes recorded by <c>operation_id</c> — IN PROCESS MEMORY, not in the
    /// journal: the Host tools keep no operation journal. LIMIT: a Host restart carries no replay
    /// history; the map is bounded by <see cref="ReleaseReplayLimit"/> and cleared on every new
    /// acquisition — a new generation is a new context.</summary>
    private readonly Dictionary<string, ReleaseReplay> _releaseOutcomes = new(StringComparer.Ordinal);
    private readonly Queue<string> _releaseOrder = new();

    /// <summary>The argument fingerprint and the ready envelope recorded for one <c>operation_id</c>.</summary>
    private sealed record ReleaseReplay(string Fingerprint, ResultEnvelope<JsonNode?> Outcome);

    public HostSession(HostOptions options, HostOwnership ownership, HostLog log)
    {
        _options = options;
        _ownership = ownership;
        _log = log;
    }

    /// <summary>Whether the tool belongs to session control.</summary>
    public static bool IsSessionTool(string? toolName) =>
        toolName is StatusTool or AcquireTool or ReleaseTool;

    /// <summary>Whether this Host holds the session right now.</summary>
    public bool OwnsSession => _ownership.IsOwner;

    // ---- Call routing ----

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

        // Diagnostics do NOT take the session: health and capabilities answer even when another chat
        // owns it, else the second chat could not learn why it has no work.
        if (DiagnosticTools.Contains(tool.Name, StringComparer.Ordinal))
        {
            var answer = !_ownership.IsOwner
                ? DiagnosticWithoutOwnership(toolName)
                : await DispatchAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);

            // INVARIANT: the catalog and the version are added HERE, on both paths, so the answer does
            // not depend on who produced the environment block. MEASURED defect: with a Worker the call
            // went to env.probe, which reports the environment only, so tools/tool_count appeared only
            // while this Host owned no session.
            // History: docs/decisions/host.md#capabilities-catalog
            return string.Equals(tool.Name, CapabilitiesTool, StringComparison.Ordinal)
                ? WithCapabilitiesCatalog(answer)
                : answer;
        }

        if (!_ownership.IsOwner)
        {
            var probe = _ownership.Probe();

            // Coordinated admission of a REAL operation: only when there is no ownership at all or the
            // previous owner released implicitly (transport ended). After an EXPLICIT release it is
            // forbidden, else "released" and "still working" become one state.
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

    /// <summary>Run an ordinary call: entry under the active-call counter, so a release cannot slip
    /// between the operation's admission and its execution.</summary>
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
            // Generation check on every call: a false return means the owner record is no longer ours,
            // and the refusal must arrive before COM.
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

            // INVARIANT: a call that restarted the Worker must name it. Previous document_id values
            // are invalid after a restart: the new Worker knows no documents. The client must learn
            // this from the response, not from a later refusal.
            // History: docs/decisions/host.md#worker-restart-warning
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

    // ---- kompas_session_status ----

    /// <summary>Session state. Does not acquire or restore ownership, does not start the Worker and
    /// does not touch COM.</summary>
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
                // A CREATED CHANNEL does not mean "the Worker started": the process starts on the
                // first real command, not at acquisition. Named by fields, so "the Worker exists"
                // does not read as "it works".
                ["worker_channel"] = worker is null ? "not_created" : "created",
                ["worker_pid"] = worker?.WorkerProcessId,
                // The STICKY FLAG is visible in the status: an unknown document state is a session
                // state of its own, not a detail dug out of one refusal text.
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
            // A READ ERROR is not the same as a FREE session: "no record" and "the record is
            // unreadable" are different states with different costs.
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

    // ---- kompas_acquire_session ----

    public async Task<ResultEnvelope<JsonNode?>> AcquireAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = _ownership.TryAcquire(explicitRequest: true);

            if (result.Outcome == OwnershipOutcome.AlreadyOwned)
            {
                // Re-acquisition by the SAME owner: no second Worker and no second generation.
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
                    // "Owned by another" and "release not confirmed" change by themselves (the owner
                    // will release / cleanup will finish), so a retry makes sense. Other acquisition
                    // refusals are not cured by a retry.
                    result.ErrorCode is ErrorCodes.SessionOwnerActive or ErrorCodes.SessionReleaseFailed
                        ? RetryPolicy.AfterReconciliation
                        : RetryPolicy.Never,
                    result.Refusal is null ? null : OwnerDetails(result.Refusal),
                    remedy: result.Remedy);
            }

            // After acquisition: a NEW journal, a NEW queue, a NEW context. No old cache is used: the
            // model may have changed while another Host owned the session.
            var problem = StartResources();
            if (problem is not null)
            {
                // Ownership taken but work impossible: hand the session back, do not pretend to own it.
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

            // A new generation is a new context: release outcomes of the previous session are
            // invalidated with the previous owner's references, else a replay of an old operation_id
            // would return an outcome from BEFORE this acquisition.
            // History: docs/decisions/host.md#new-generation
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
                // The new context is named explicitly, not implied: old references are deliberately
                // not revived, and silence would read as "you may continue".
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

    // ---- kompas_release_session ----

    /// <summary>Session release with replay of the recorded outcome by <c>operation_id</c>.</summary>
    /// <remarks>Why <c>operation_id</c> is here: line <c>S03b</c> of the <c>mcp-smoke.py</c> harness
    /// requires a tool with <c>destructiveHint=true</c> to declare it (published rule §2.1). Only a
    /// TERMINAL SUCCESS is recorded; refusals (<c>DOCUMENT_DIRTY</c>, <c>SESSION_RELEASE_BUSY</c>) and
    /// unfinished branches are NOT — their cure is to repeat the call.
    /// LIMIT: the record is not a journal.
    /// History: docs/decisions/host.md#release-replay</remarks>

    /// <summary>Remember a replay outcome and keep the map within <see cref="ReleaseReplayLimit"/>.
    /// Called under <c>_transition</c>, so it needs no separate lock.</summary>
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

    /// <summary>A recorded replay outcome with a warning that no Worker or COM call was made.</summary>
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

    /// <summary>A stable fingerprint of the call's arguments, as in the operation journal
    /// (<c>ToolInvoker.Canonical</c>): fields are ordered by name, so "the same arguments" is string
    /// equality, not JSON key order.</summary>
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

            // A replay with the same id returns the recorded outcome, checked BEFORE the procedure:
            // else the repeat would release again and the outcome would depend on whether the first
            // response reached the client.
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

            // Only a TERMINAL SUCCESS is recorded: a refusal (DOCUMENT_DIRTY, SESSION_RELEASE_BUSY)
            // and an unfinished branch are cured by REPEATING the call, and a record would make the
            // repeat forever return the same refusal.
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
                    // Another's ownership is not touched: "releasing someone else's session" would be a
                    // bypass of the protection.
                    return Refusal(
                        ErrorCodes.SessionOwnerActive,
                        "Освободить нельзя: сеансом владеет другой Хост. " + probe.Reason,
                        RetryPolicy.AfterReconciliation,
                        OwnerDetails(probe),
                        remedy: $"Освобождайте сеанс у владельца (pid {probe.OwnerPid}); "
                                + "здесь вызовите kompas_session_status и дождитесь освобождения.");
                }

                // A repeat RELEASE without ownership is safe: "already released" differs from "released
                // by this request" by a separate field, not only by text.
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

            // Transition owned → releasing: from here the owner keeps exclusive right, and another
            // Host cannot take the session.
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

            // Refusing new calls and checking busyness are ONE lock acquisition: a call that entered
            // before this point is visible in the counter, a call after gets no entry, so nothing
            // slips between the check and the cleanup.
            int active;
            lock (_callGate)
            {
                _releasing = true;
                active = _activeCalls;
            }

            var work = DescribePendingWork(active, out var workNode);
            if (work is not null)
            {
                // A refusal before cleanup returns to owned: the owner stays the owner, not "almost
                // free", else the next CAD call would hit a half-released session.
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

            // Unsaved documents are refused by default: no implicit save and no discarding of edits,
            // because ksDocument3D.close() promises no save. The "may this be released" decision is a
            // PURE function (ReleaseGuard); only FACTS come here (Worker ran, channel alive without a
            // restart, sticky flag, acknowledgement, inventory read, dirty count).
            // History: docs/decisions/host.md#release-guard
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

            // EXPLICIT acknowledgement of the unknown state: the client took on that the previous
            // Worker's edits may remain unsaved in KOMPAS. Named in a warning, not left silent.
            var acknowledgedUnknown = acknowledge && worker?.DocumentStateUnknown == true;
            var unknownReason = worker?.DocumentStateUnknownReason;

            // Cleanup: Worker, queue, Invoker, journal. The Worker stops FIRST, because it is the only
            // process that runs COM and writes to the operation journal.
            var stop = await StopWorkerAsync().ConfigureAwait(false);
            await DisposeResourcesAsync().ConfigureAwait(false);

            // The flag is cleared ONLY now, after a confirmed Worker stop: earlier would declare the
            // state known on the client's intent alone.
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
                // PARTIAL cleanup: ownership is NOT published as free. The state stays releasing, and
                // a repeat continues the cleanup instead of repeating the destructive action.
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

            // ONLY after confirmed cleanup is `released` published atomically.
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

    // ---- Transport end ----

    /// <summary>Transport ended. The same exclusion mechanism as an explicit release, but the result
    /// is <see cref="HostOwnerState.Free"/>: ownership is lifted implicitly, and the next chat's first
    /// CAD call takes it by coordinated admission.</summary>
    public async Task OnTransportEndAsync()
    {
        await _transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var probe = _ownership.Probe();
            if (!probe.SelfOwned)
            {
                // Not the owner: another's generation is not touched. This is the "an old finally may
                // not release another generation" rule.
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

    // ---- Session resources ----

    /// <summary>Create the journal, queue and Invoker anew. Returns the problem text or null.</summary>
    /// <remarks>The journal is mandatory: without it a restart cannot tell "not done" from "done but
    /// the answer was lost". An unavailable journal is therefore an acquisition refusal, not a
    /// warning.</remarks>
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
            // The Supervisor is created, but the Worker process is NOT started: it starts on the first
            // real command. A waiting client therefore spawns no CAD channel at all.
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
            // The Worker never ran: nobody to run COM and nobody to write to the journal.
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

    /// <summary>The session's document inventory — the release decision without losing edits.</summary>
    private async Task<(JsonNode? Payload, ErrorDto? Error)> InventoryAsync(CancellationToken cancellationToken)
    {
        var worker = _worker;

        // The Worker never ran: no COM session and no documents. Starting a process just for the
        // inventory is forbidden: it would spawn a CAD channel exactly when the session is handed over.
        if (worker is null || !worker.HasStarted)
        {
            return (null, null);
        }

        // INVARIANT: the inventory is NOT obtained by restarting the Worker. An ordinary
        // `SendAsync` → `EnsureStartedAsync` used to stop the old Worker (20 s wait, then kill) and
        // raise a NEW one on a broken channel; the new Worker knows no documents → empty inventory →
        // `dirty=0` → release passes although the model state is UNKNOWN. A broken channel is now an
        // inventory refusal, not "no edits". History: docs/decisions/host.md#inventory-restart
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

    /// <summary>Whether there is unfinished work. An empty queue is NOT sufficient: active calls and
    /// operations whose synchronous response has already gone while KOMPAS still works are counted.</summary>
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
            // "RELEASED" AND "EDITS MAY BE LOST" ARE DIFFERENT CLAIMS. The release was done by explicit
            // client acknowledgement, and the price of that acknowledgement must be spoken.
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

    /// <summary>A release refusal per the <see cref="ReleaseGuard"/> decision: the code, retry policy
    /// and details depend on the CAUSE, not on one wording.</summary>
    /// <remarks>Different causes have different exits: an unknown state is cleared by acknowledgement,
    /// dirty documents by saving or closing, an unavailable inventory by a retry. One common wording
    /// would force the client to guess the exit.</remarks>
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

    // ---- Classification and formatting ----

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

    /// <summary>Diagnostics without ownership: answered locally, without Worker and without COM.</summary>
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

        if (string.Equals(toolName, CapabilitiesTool, StringComparison.Ordinal))
        {
            // The catalog is added by WithCapabilitiesCatalog() on BOTH paths, not here: "no tools
            // visible" and "tools exist but the session is not ours" are different things, and so is
            // "the catalog appeared only while the Worker was absent".
            node["rot_kompas_entries"] = null;
            node["rot_kompas_entries_unavailable"] =
                "Worker не запущен: перечисление ROT не выполнялось. Ноль здесь означал бы "
                + "«записей нет» — другое утверждение.";
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

    /// <summary>Adds the tool catalog and the server version to a <c>kompas_capabilities</c> answer,
    /// whichever process produced the environment block.</summary>
    /// <remarks>INVARIANT: the catalog lives in the Host (<see cref="ToolCatalog.All"/>), so it is
    /// published without a Worker, without COM and without ownership. MEASURED defect: with a Worker the
    /// call was routed to <c>env.probe</c>, whose answer carries the environment only — one and the same
    /// tool answered a different contract depending on the session state, and the client had to read the
    /// whole <c>tools/list</c> to learn what the build supports.
    /// INVARIANT: <c>rot_kompas_entries</c> is reported as <c>null</c> with a NAMED reason when the
    /// enumeration did not happen; zero is a different statement ("there are no entries").
    /// History: docs/decisions/host.md#capabilities-catalog</remarks>
    public static ResultEnvelope<JsonNode?> WithCapabilitiesCatalog(ResultEnvelope<JsonNode?> envelope)
    {
        if (envelope.Result is not JsonObject result)
        {
            return envelope;
        }

        var node = (JsonObject)result.DeepClone();
        node["tools"] = new JsonArray(
            ToolCatalog.All.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray());
        node["tool_count"] = ToolCatalog.All.Count;
        node["server_version"] = Program.ServerVersion;

        if (!node.ContainsKey("rot_kompas_entries"))
        {
            node["rot_kompas_entries"] = null;
            node["rot_kompas_entries_unavailable"] =
                "Среда не опрошена: перечисление ROT не выполнялось, поэтому число записей неизвестно. "
                + "Ноль здесь означал бы «записей нет» — другое утверждение.";
        }

        return envelope with { Result = node };
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

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Host;

/// <summary>Owns the Worker process and the pipe to it.</summary>
/// <remarks>The Worker is a child process by design (spec 1.5): when a COM call wedges, this process
/// keeps answering <c>kompas_health</c> and <c>kompas_capabilities</c> from the journal and queue
/// state. Two consequences are enforced: a command is dispatched to the Worker <b>at most once</b>
/// — a broken pipe or an expired budget means an unknown outcome marked for reconciliation (spec 1.8);
/// restarting the Worker is allowed, killing KOMPAS is not. The pipe is read by exactly one party —
/// the <see cref="IpcRequestChannel"/> created per connection — which routes answers by request id.
/// History: docs/decisions/host.md#worker-child-process</remarks>
public sealed class WorkerSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the Worker's GRACEFUL exit before taking the process down.</summary>
    /// <remarks>MEASURED 04.10.2026 on the session-handover run: with a 5 s window the Worker did not
    /// finish the documented `KompasObject.Quit()` route for its own KOMPAS instance and was taken
    /// down by kill (`kill_used: true`). Killing is the WORSE of the two routes: it denies KOMPAS a
    /// graceful shutdown and may leave the instance running. The window is 20 s — the wait is paid
    /// once, on an explicit release, the kill cost on every handover.
    /// History: docs/decisions/host.md#shutdown-window</remarks>
    private const int GracefulShutdownWindowMs = 20_000;

    private readonly HostOptions _options;
    private readonly HostLog _log;
    private readonly SemaphoreSlim _restartGate = new(1, 1);

    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private IpcRequestChannel? _channel;

    public string PipeName { get; }

    public bool IsConnected =>
        _pipe is { IsConnected: true } &&
        _channel is { IsBroken: false } &&
        _process is { HasExited: false };

    public int? WorkerProcessId => TryProcessId();

    /// <summary>Whether the Worker process ran in this session.</summary>
    /// <remarks>"Channel created" and "Worker ran" are different claims, and the difference matters to
    /// session release: if the process never ran, no COM session exists and there is nobody to ask for
    /// the document inventory — and starting a Worker just for the inventory would spawn a CAD channel
    /// exactly when the session is being handed over.</remarks>
    public bool HasStarted { get; private set; }

    /// <summary>STICKY flag: "the session's document state is UNKNOWN".</summary>
    /// <remarks>Set as soon as a Worker that ran is lost or restarted (<see cref="MarkBroken"/>, process
    /// exit, restart in <see cref="EnsureStartedAsync"/>), cleared only by a release acknowledging the
    /// unknown state. Needed even though the channel is checked: fix H3 (05.10.2026) closed the direct bypass
    /// of <c>DOCUMENT_DIRTY</c>, but a workaround remained — a mutation overruns the budget, the channel is
    /// marked broken, the client calls ANY CAD tool, and <see cref="EnsureStartedAsync"/> raises a NEW Worker
    /// with no documents, so the next <c>kompas_release_session</c> sees a live channel, an empty inventory, and passes.
    /// History: docs/decisions/host.md#sticky-unknown</remarks>
    public bool DocumentStateUnknown { get; private set; }

    /// <summary>Why the document state was deemed unknown. Named, not implied.</summary>
    public string? DocumentStateUnknownReason { get; private set; }

    /// <summary>How many times the Worker restarted in this session (the first start does not count).</summary>
    /// <remarks>Needed by the CAD-call response: if the Worker restarted during the call, previous
    /// <c>document_id</c> values are invalid, and this must be told to the client rather than inferred
    /// by it from "the tool answered successfully".</remarks>
    public int RestartCount { get; private set; }

    /// <summary>Mark the document state unknown. A repeat call keeps the first reason.</summary>
    public void MarkDocumentStateUnknown(string reason)
    {
        DocumentStateUnknown = true;
        DocumentStateUnknownReason ??= reason;
        _log.Write("warn", "document state marked unknown", new { reason, worker_pid = WorkerProcessId });
    }

    /// <summary>Clear the unknown-state flag. Called ONLY by an explicit client action (a release with
    /// <c>acknowledge_unknown_document_state=true</c>) or when a new Worker is created: a new session
    /// generation is a new context.</summary>
    public void ClearDocumentStateUnknown(string reason)
    {
        if (!DocumentStateUnknown)
        {
            return;
        }

        DocumentStateUnknown = false;
        DocumentStateUnknownReason = null;
        _log.Write("info", "document state unknown acknowledged", new { reason });
    }

    public event Action? WorkerLost;

    public WorkerSupervisor(HostOptions options, HostLog log)
    {
        _options = options;
        _log = log;

        // A name that is unique per session and per user: a predictable name would let another
        // process of the same user connect to a server it should not be talking to.
        PipeName = $"kompas-mcp-{Environment.UserName.ToLowerInvariant()}-{Guid.NewGuid():N}";
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return;
        }

        await _restartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            // INVARIANT: restarting a Worker that ran is a LOSS of document state. A new Worker knows
            // none of the previous documents: its inventory is empty regardless of what was open and
            // unsaved, and release refuses until the flag is cleared explicitly (defect H3, review
            // 05.10.2026). History: docs/decisions/host.md#restart-state-loss
            if (HasStarted)
            {
                RestartCount++;
                MarkDocumentStateUnknown(
                    "Worker перезапущен: прежний экземпляр остановлен, документы прежнего сеанса " +
                    "новому Worker не известны");
            }

            await StopAsync().ConfigureAwait(false);
            Start();

            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The pipe object exists but was never connected. Keep no reference to it: the next
                // EnsureStartedAsync calls StopAsync first, and writing a shutdown frame into a never-connected
                // pipe raises InvalidOperationException from PipeStream.CheckWriteOperations. That exception
                // matched neither IOException nor KompasContractException, so it escaped the invoker entirely
                // ("tool call escaped the invoker") and replaced the honest WORKER_UNRESPONSIVE with a crash.
                // Measured 18.09.2026: a Host whose Worker could not start answered kompas_health with
                // status=failed and then failed kompas_connect with VERIFICATION_FAILED, hiding the real cause
                // (no KompasMcp.Worker.dll next to the apphost).
                pipe.Dispose();
                MarkBroken();
                throw new KompasContractException(
                    ErrorCodes.WorkerUnresponsive,
                    $"Worker не подключился к каналу '{PipeName}' за {ConnectTimeout.TotalSeconds:0} с.",
                    RetryPolicy.SameOperationId);
            }

            _pipe = pipe;
            _channel = new IpcRequestChannel(pipe);
            _log.Write("info", "worker connected", new { worker_pid = _process?.Id, pipe = PipeName });
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private void Start()
    {
        var executable = ResolveWorkerExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            // The Worker's stdout must never mix with the MCP frames this process writes: keep the
            // child's output redirected and forward only to the log.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Environment = { ["DOTNET_NOLOGO"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
        };

        // INVARIANT: the pipe name is passed as an argument LIST, not a glued string. The name
        // contains `Environment.UserName`; in `--pipe kompas-mcp-ivan petrov-…` the space splits it,
        // the Worker gets a truncated name, never connects, and any CAD call ends in
        // WORKER_UNRESPONSIVE for a reason not named in the answer (defect M12, review 05.10.2026).
        // ArgumentList escapes by itself; the Worker log path uses the same list, as it may also
        // contain a space. History: docs/decisions/host.md#pipe-name-args
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(PipeName);
        startInfo.ArgumentList.Add("--copies");
        startInfo.ArgumentList.Add(_options.ControlCopyDirectory);
        if (_options.WorkerLogPath is { Length: > 0 } logPath)
        {
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(logPath);
        }

        _process = Process.Start(startInfo);

        if (_process is null)
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                "Не удалось запустить процесс Worker.",
                RetryPolicy.SameOperationId);
        }

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _log.Write("worker", e.Data);
            }
        };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _log.Write("worker", e.Data);
            }
        };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        HasStarted = true;

        // INVARIANT: without EnableRaisingEvents the event NEVER fires — a subscription without the
        // flag is dead control that looks alive (defect L2, review 05.10.2026). The flag is set BEFORE
        // the subscription. History: docs/decisions/host.md#exited-events
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            // Process exit is also a loss of document state. The process may have exited gracefully on
            // shutdown or crashed; either way only the model knows what became of the open documents,
            // and the flag must name it.
            var pid = SafeProcessId(_process);
            MarkDocumentStateUnknown(
                "процесс Worker" + (pid is null ? string.Empty : $" (pid {pid})") +
                " завершился: состояние его документов неизвестно");
            _log.Write("warn", "worker exited", new { exit_code = SafeExitCode(_process) });
            WorkerLost?.Invoke();
        };
    }

    private string ResolveWorkerExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkerPath) && File.Exists(_options.WorkerPath))
        {
            return Path.GetFullPath(_options.WorkerPath);
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "KompasMcp.Worker.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new KompasContractException(
            ErrorCodes.WorkerUnresponsive,
            "Компонент KompasMcp.Worker.exe не найден рядом с Host. Укажите worker_path в конфигурации — запускать CAD-канал из неизвестного места нельзя.",
            RetryPolicy.Never);
    }

    /// <summary>Whether a command can be sent WITHOUT restarting the Worker.</summary>
    /// <remarks>"Worker ran" and "the channel is alive" are different claims, and the difference
    /// decides the inventory outcome at session release (see <see cref="SendWithoutRestartAsync"/>).</remarks>
    public bool CanSendWithoutRestart =>
        _channel is { IsBroken: false } && _pipe is { IsConnected: true } && _process is { HasExited: false };

    /// <summary>Send a command WITHOUT raising a new Worker on a broken channel.</summary>
    /// <remarks>Why a separate method: the ordinary <see cref="SendAsync"/> calls
    /// <see cref="EnsureStartedAsync"/>, which on a broken channel stops the old Worker (20 s wait, then
    /// kill) and raises a NEW one — for the inventory the worst outcome: the new Worker knows no documents,
    /// the inventory is empty, <c>dirty=0</c>, and release passes although the model state is unknown
    /// (defect H3, review 05.10.2026). It also kills a process possibly inside a COM call.
    /// History: docs/decisions/host.md#inventory-restart</remarks>
    /// <returns>null when the channel is broken or the Worker never ran: the caller must call the document state UNKNOWN, not "no edits".</returns>
    public async Task<IpcFrame?> SendWithoutRestartAsync(string command, JsonNode? payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!CanSendWithoutRestart)
        {
            return null;
        }

        var channel = _channel!;
        try
        {
            return await channel.RequestAsync(command, payload, timeout, isMutation: false, cancellationToken).ConfigureAwait(false);
        }
        catch (KompasContractException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<IpcFrame> SendAsync(string command, JsonNode? payload, TimeSpan timeout, bool isMutation, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var channel = _channel ?? throw new KompasContractException(ErrorCodes.WorkerUnresponsive, "Канал к Worker не установлен.", RetryPolicy.SameOperationId);

        try
        {
            return await channel.RequestAsync(command, payload, timeout, isMutation, cancellationToken).ConfigureAwait(false);
        }
        catch (KompasContractException ex) when (ex.Code == ErrorCodes.OutcomeUnknown || ex.Code == ErrorCodes.ApplicationDisconnected)
        {
            if (isMutation)
            {
                MarkBroken();
            }

            throw;
        }
        catch (IOException ex)
        {
            MarkBroken();
            throw new KompasContractException(
                isMutation ? ErrorCodes.OutcomeUnknown : ErrorCodes.WorkerUnresponsive,
                "Канал к Worker оборвался" + (isMutation ? " после отправки команды: исход неизвестен." : "."),
                isMutation ? RetryPolicy.AfterReconciliation : RetryPolicy.SameOperationId,
                partialEffects: isMutation,
                details: new Dictionary<string, object?> { ["io_message"] = ex.Message });
        }
    }

    /// <summary>Drop the connection so the next request restarts a fresh Worker. The old process is not
    /// killed while it might still be inside a COM call: KOMPAS owns that work, not us.</summary>
    public void MarkBroken()
    {
        var pipe = _pipe;
        _pipe = null;

        // A broken channel is a LOSS of contact with the documents, not "no edits". The Worker may
        // have been inside a COM call when the break happened: only the model knows what it applied.
        // The flag is therefore set BEFORE anyone can ask for the inventory and stays until an
        // explicit client acknowledgement.
        if (HasStarted)
        {
            MarkDocumentStateUnknown(
                "канал к Worker сломан: связь с открытыми документами потеряна, правки могли не сохраниться");
        }

        if (pipe is null)
        {
            return;
        }

        try
        {
            pipe.Dispose();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Already broken.
        }

        // Disposing the pipe ends the channel's reader; the channel itself is released by the next
        // StopAsync, which is the only place that owns its lifetime.
    }

    private int? TryProcessId()
    {
        try
        {
            return _process is { HasExited: false } ? _process.Id : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? SafeExitCode(Process? process)
    {
        try
        {
            return process is { HasExited: true } ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The process PID if it is still alive and readable; otherwise null. Does not throw on a
    /// taken-down process.</summary>
    private static int? SafeProcessId(Process? process)
    {
        try
        {
            return process is { HasExited: false } ? process.Id : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The outcome of stopping the Worker: whether the process is confirmed gone.</summary>
    /// <remarks>"Stop performed" and "the Worker no longer runs COM or writes to the journal" are
    /// different claims. Session release must rest on the SECOND, else the next owner would start
    /// working while the old Worker is still inside a COM call. <see cref="KillUsed"/> is part of the
    /// answer, not an implementation detail: a process taken down by kill may leave documents unclosed,
    /// and that must be named, not hidden.</remarks>
    public sealed record WorkerStopResult(int? Pid, bool Confirmed, int? ExitCode, bool KillUsed, string? Problem);

    /// <summary>Stop the Worker and CONFIRM that the process is gone.</summary>
    public async Task<WorkerStopResult> StopAndConfirmAsync()
    {
        var pid = TryProcessId();
        var killUsed = await StopCoreAsync().ConfigureAwait(false);
        return ConfirmStop(pid, killUsed);
    }

    public async Task StopAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
    }

    private static WorkerStopResult ConfirmStop(int? pid, bool killUsed)
    {
        if (pid is null)
        {
            // The Worker never ran: there is nothing to run COM and nobody to write to the journal.
            return new WorkerStopResult(null, true, null, false, null);
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return new WorkerStopResult(pid, process.HasExited, SafeExitCode(process), killUsed,
                process.HasExited ? null : $"процесс Worker pid {pid} существует после остановки");
        }
        catch (ArgumentException)
        {
            return new WorkerStopResult(pid, true, null, killUsed, null);
        }
        catch (InvalidOperationException)
        {
            return new WorkerStopResult(pid, true, null, killUsed, null);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            return new WorkerStopResult(pid, false, null, killUsed,
                $"подтвердить завершение Worker pid {pid} не удалось ({ex.GetType().Name})");
        }
    }

    /// <summary>Wait for the process's graceful exit without occupying a thread. true — the process exited.</summary>
    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // The process is already gone and the handle closed.
            return true;
        }
    }

    private async Task<bool> StopCoreAsync()
    {
        var channel = _channel;
        var pipe = _pipe;
        _channel = null;
        _pipe = null;

        if (pipe is not null)
        {
            if (channel is not null)
            {
                try
                {
                    // A pipe object that exists but never connected is not a pipe that can carry a
                    // frame. Writing into it throws InvalidOperationException, which used to escape
                    // this best-effort block and abort the whole tool call. See EnsureStartedAsync.
                    if (pipe.IsConnected)
                    {
                        await channel.WriteAsync(new IpcFrame
                        {
                            ProtocolVersion = IpcFrame.CurrentProtocolVersion,
                            RequestId = Guid.NewGuid().ToString("N"),
                            Kind = IpcFrameKind.Request,
                            Command = WorkerCommands.Shutdown,
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or KompasContractException or InvalidOperationException or ObjectDisposedException)
                {
                    // Best effort: the pipe may already be gone.
                }
            }

            // Close the stream first: that is what releases a reader blocked on a read, so the
            // channel can be disposed without waiting for a frame that will never come.
            try
            {
                pipe.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Already gone.
            }
        }

        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_process is null)
        {
            return false;
        }

        // INVARIANT: the process tree is NOT killed. A launched KOMPAS instance is spawned by the Worker
        // through COM, i.e. it is its CHILD, so `Kill(entireProcessTree: true)` would take KOMPAS down too —
        // exactly what the order forbids. The documented way to end a launched instance is
        // `KompasObject.Quit()` from the Worker's shutdown frames
        // (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_quit.html>); killing is a last resort for
        // an UNRESPONSIVE Worker. INVARIANT: the exit wait is ASYNCHRONOUS — a synchronous
        // `_process.WaitForExit(20_000)` under `_restartGate` blocked a pool thread and every parallel call
        // waiting on the gate for up to 20 s (defect L3, review 05.10.2026). History: docs/decisions/host.md#no-tree-kill
        var exited = await WaitForExitAsync(_process, GracefulShutdownWindowMs).ConfigureAwait(false);
        var killUsed = false;
        if (!exited)
        {
            try
            {
                _process.Kill(entireProcessTree: false);
                killUsed = true;
            }
            catch (InvalidOperationException)
            {
                // Exited between the wait and the kill.
            }

            // Let the kill complete: the release confirmation reads the process by pid, and without
            // this wait it would answer "the process is still alive" for an already-taken-down process.
            if (killUsed)
            {
                await WaitForExitAsync(_process, 2_000).ConfigureAwait(false);
            }
        }

        _process.Dispose();
        _process = null;
        return killUsed;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _restartGate.Dispose();
    }
}

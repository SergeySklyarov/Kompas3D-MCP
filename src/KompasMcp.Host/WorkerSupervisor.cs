using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Host;

/// <summary>
/// Owns the Worker process and the pipe to it.
/// </summary>
/// <remarks>
/// The Worker is a child process by design (spec 1.5): when a COM call wedges, this process keeps
/// answering <c>kompas_health</c> and <c>kompas_capabilities</c> from the journal and queue
/// state instead of being stuck inside the same call. Two consequences are enforced here:
/// <list type="bullet">
/// <item>A command may be dispatched to the Worker <b>at most once</b>. If the pipe breaks or the
/// budget expires, the outcome is unknown and the operation is marked for reconciliation — a
/// silent re-send could apply a mutation twice (spec 1.8).</item>
/// <item>Restarting the Worker is allowed; killing КОМПАС is not. The child is started with a
/// unique, session-scoped pipe name and is expected to exit on Host disconnect.</item>
/// </list>
/// The pipe is read by exactly one party: the <see cref="IpcRequestChannel"/> this supervisor
/// creates per connection. Several tool calls may be in flight at once — a long mutation and a
/// <c>kompas_health</c> probe is the case the design exists for — and the channel routes each
/// answer back by request id. Reading the pipe from the caller was the 18.09.2026 defect: two
/// concurrent callers interleaved bytes and corrupted the stream.
/// </remarks>
public sealed class WorkerSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Сколько ждать ШТАТНОГО выхода Worker'а, прежде чем снять процесс.
    /// </summary>
    /// <remarks>
    /// ИЗМЕРЕНО 04.10.2026 на прогоне передачи сеанса: при окне 5 с Worker не успевал завершить
    /// документированный маршрут `KompasObject.Quit()` для собственного экземпляра КОМПАС и снимался
    /// убийством (`kill_used: true` в ответе `kompas_release_session`). Убийство не запрещено, но
    /// это ХУДШИЙ из двух маршрутов: оно не даёт КОМПАС завершиться штатно и оставляет шанс, что
    /// экземпляр останется запущенным. Окно расширено до 20 с — цена ожидания платится один раз, на
    /// явном освобождении сеанса, а цена убийства — на каждой передаче.
    /// </remarks>
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

    /// <summary>
    /// Запускался ли процесс Worker в этом сеансе.
    /// </summary>
    /// <remarks>
    /// «Канал создан» и «Worker запускался» — разные утверждения, и разница нужна освобождению
    /// сеанса: если процесс не запускался, COM-сеанса не существует и спрашивать опись документов
    /// не у кого — а запускать Worker ради одной описи значило бы породить CAD-канал в тот самый
    /// момент, когда сеанс отдают.
    /// </remarks>
    public bool HasStarted { get; private set; }

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
                // EnsureStartedAsync calls StopAsync first, and writing a shutdown frame into a
                // never-connected pipe raises InvalidOperationException from
                // PipeStream.CheckWriteOperations. That exception matched neither IOException nor
                // KompasContractException, so it escaped the invoker entirely ("tool call escaped
                // the invoker") and replaced the honest WORKER_UNRESPONSIVE with a crash. Measured
                // 18.09.2026: a Host whose Worker could not start answered kompas_health with
                // status=failed and then failed kompas_connect with VERIFICATION_FAILED, hiding the
                // real cause (no KompasMcp.Worker.dll next to the apphost).
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

        // ИМЯ КАНАЛА ПЕРЕДАЁТСЯ СПИСКОМ АРГУМЕНТОВ, А НЕ СКЛЕЕННОЙ СТРОКОЙ.
        //
        // Имя содержит `Environment.UserName`. В строке `--pipe kompas-mcp-ivan petrov-…` пробел
        // делит его надвое: Worker получает усечённое имя, никогда не подключается, и любой
        // CAD-вызов кончается WORKER_UNRESPONSIVE по причине, которая в ответе не названа
        // (дефект M12 ревью 05.10.2026). ArgumentList экранирует сам. Путь журнала Worker идёт
        // тем же списком: он приходит из конфигурации и так же может содержать пробел.
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

        // БЕЗ EnableRaisingEvents СОБЫТИЕ НИКОГДА НЕ ПРИХОДИТ: подписка без этого флага — мёртвый
        // контроль, который выглядит как живой (дефект L2 ревью 05.10.2026). Флаг ставится ДО
        // подписки.
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
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

    /// <summary>
    /// Send one command and await its answer. A transport failure after the command was written is
    /// reported as OUTCOME_UNKNOWN for mutations: the Worker may already be inside the COM call.
    /// </summary>
    /// <summary>
    /// Можно ли отправить команду БЕЗ перезапуска Worker.
    /// </summary>
    /// <remarks>
    /// «Worker запускался» и «канал жив» — разные утверждения, и разница решает исход описи при
    /// освобождении сеанса (см. <see cref="SendWithoutRestartAsync"/>).
    /// </remarks>
    public bool CanSendWithoutRestart =>
        _channel is { IsBroken: false } && _pipe is { IsConnected: true } && _process is { HasExited: false };

    /// <summary>
    /// Отправить команду, НЕ поднимая новый Worker при сломанном канале.
    /// </summary>
    /// <remarks>
    /// <b>Зачем отдельный метод.</b> Обычный <see cref="SendAsync"/> вызывает
    /// <see cref="EnsureStartedAsync"/>, который при сломанном канале останавливает прежний Worker
    /// (20 с ожидания, затем убийство) и поднимает НОВЫЙ. Для описи документов сеанса это ровно
    /// худший из возможных исходов: новый Worker не знает ни одного документа, опись пуста,
    /// <c>dirty=0</c>, и освобождение проходит, хотя модель в неизвестном состоянии. Попутно
    /// убивается процесс, возможно находящийся внутри COM-вызова, — вопреки смыслу
    /// <see cref="MarkBroken"/> (дефект H3 ревью 05.10.2026).
    /// </remarks>
    /// <returns>
    /// null, если канал сломан или Worker не запущен: вызывающий обязан назвать состояние
    /// документов НЕИЗВЕСТНЫМ, а не «правок нет».
    /// </returns>
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

    /// <summary>
    /// Drop the connection so the next request restarts a fresh Worker. The old process is not
    /// killed while it might still be inside a COM call: КОМПАС owns that work, not us.
    /// </summary>
    public void MarkBroken()
    {
        var pipe = _pipe;
        _pipe = null;

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

    /// <summary>
    /// Итог остановки Worker: подтверждено ли, что процесса больше нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// «Остановка выполнена» и «Worker больше не выполняет COM и не пишет в журнал» — разные
    /// утверждения. Освобождение сеанса обязано опираться на ВТОРОЕ: иначе следующий владелец
    /// начал бы работать, пока прежний Worker ещё внутри COM-вызова.
    /// </para>
    /// <para>
    /// <see cref="KillUsed"/> — не деталь реализации, а часть ответа: процесс, снятый убийством,
    /// мог оставить после себя незакрытые документы, и это обязано быть названо, а не спрятано.
    /// </para>
    /// </remarks>
    public sealed record WorkerStopResult(int? Pid, bool Confirmed, int? ExitCode, bool KillUsed, string? Problem);

    /// <summary>
    /// Остановить Worker и ПОДТВЕРДИТЬ, что процесса больше нет.
    /// </summary>
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
            // Worker не был запущен: нечем выполнять COM и некому писать в журнал.
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

    /// <summary>Ждать штатного выхода процесса, не занимая поток. true — процесс вышел.</summary>
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
            // Процесс уже снят и дескриптор закрыт.
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

        // Give the Worker time to shut down its own КОМПАС instance gracefully. If it does not
        // exit, only the Worker is terminated — never the CAD application it was talking to.
        //
        // ДЕРЕВО НЕ УБИВАЕТСЯ, И ЭТО ИЗМЕРЕННОЕ РЕШЕНИЕ, А НЕ СЛУЧАЙНОСТЬ. Собственный (launched)
        // экземпляр КОМПАС порождён процессом Worker через COM, то есть является его ПОТОМКОМ:
        // `Kill(entireProcessTree: true)` снял бы и сам КОМПАС — ровно то, что наряд запрещает
        // («не убивать CAD»). Документированный маршрут завершения собственного экземпляра —
        // `KompasObject.Quit()` из кадров shutdown Worker'а
        // (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_quit.html>); убийство — крайняя
        // мера только для НЕ отвечающего Worker'а. Названное следствие: если Worker снимается
        // убийством, его КОМПАС может остаться запущенным, и это названо в ответе, а не скрыто.
        // ОЖИДАНИЕ ВЫХОДА — АСИНХРОННОЕ.
        //
        // Прежде здесь стоял синхронный `_process.WaitForExit(20_000)` под `_restartGate`: поток
        // пула и ВСЕ параллельные вызовы, ожидающие гейт, стояли до 20 с (дефект L3 ревью
        // 05.10.2026). Теперь ожидание освобождает поток, а гейт по-прежнему защищает перезапуск.
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

            // Дать убийству дойти до конца: подтверждение освобождения читает процесс по pid, и
            // без этого ожидания оно отвечало бы «процесс ещё жив» на уже снятый процесс.
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

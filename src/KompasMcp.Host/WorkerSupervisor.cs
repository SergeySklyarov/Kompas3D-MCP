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
        var arguments = $"--pipe {PipeName}" + (_options.WorkerLogPath is null ? string.Empty : $" --log \"{_options.WorkerLogPath}\"");

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            // The Worker's stdout must never mix with the MCP frames this process writes: keep the
            // child's output redirected and forward only to the log.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Environment = { ["DOTNET_NOLOGO"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
        }) ?? throw new KompasContractException(
            ErrorCodes.WorkerUnresponsive,
            "Не удалось запустить процесс Worker.",
            RetryPolicy.SameOperationId);

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
    public async Task<IpcFrame> SendAsync(string command, JsonNode? payload, TimeSpan timeout, bool isMutation, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var channel = _channel ?? throw new KompasContractException(ErrorCodes.WorkerUnresponsive, "Канал к Worker не установлен.", RetryPolicy.SameOperationId);

        try
        {
            return await channel.RequestAsync(command, payload, timeout, cancellationToken).ConfigureAwait(false);
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

    public async Task StopAsync()
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
            return;
        }

        // Give the Worker time to shut down its own КОМПАС instance gracefully. If it does not
        // exit, only the Worker is terminated — never the CAD application it was talking to.
        if (!_process.WaitForExit(5_000))
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the wait and the kill.
            }
        }

        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _restartGate.Dispose();
    }
}

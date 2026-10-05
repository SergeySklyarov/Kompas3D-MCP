using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using KompasMcp.Api5Adapter;
using KompasMcp.Api5Adapter.Sta;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Worker;

/// <summary>The COM-owning process. It exists so a wedged KOMPAS call cannot take the MCP connection with
/// it: the Host keeps answering status while this process sits inside a call (spec 1.5, 1.6).</summary>
/// <remarks>Threading contract: <see cref="StaExecutor"/> runs one STA thread with a message pump and every
/// command that touches KOMPAS is queued there, one at a time, including reads; pipe I/O runs on the thread
/// pool, so <c>sys.ping</c> and <c>env.probe</c> keep being answered while the STA lane is busy — that is what
/// lets the Host distinguish "Worker dead" from "KOMPAS busy"; a command whose budget expires is answered with
/// OUTCOME_UNKNOWN and this process asks to be restarted.</remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var options = WorkerOptions.Parse(args);
        using var log = WorkerLog.Open(options.LogPath);
        log.Write("info", "worker starting", new { pipe = options.PipeName, pid = Environment.ProcessId, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription });

        if (!KompasInteropResolver.TryInstall(out var resolverFailure))
        {
            log.Write("fatal", "interop not found", new { reason = resolverFailure, probed = KompasInteropResolver.ProbedDirectories });
            Console.Error.WriteLine("[worker] " + resolverFailure);
            return 3;
        }

        if (!Environment.Is64BitProcess)
        {
            // KOMPAS v24 is x64: a 32-bit worker would fail later with messages that do not mention
            // bitness at all.
            log.Write("fatal", "bitness mismatch", new { process = Environment.Is64BitProcess ? "x64" : "x86" });
            Console.Error.WriteLine("[worker] процесс x86, а КОМПАС v24 — x64: COM-подключение невозможно.");
            return 4;
        }

        using var sta = new StaExecutor("kompas-com-sta");
        sta.Start();

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };

        var session = new CommandDispatcher(sta, log, options.ControlCopyDirectory);
        try
        {
            Serve(options, session, log, lifetime.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            log.Write("info", "worker stopping on cancellation");
        }
        catch (Exception ex)
        {
            log.Write("fatal", "worker crashed", new { type = ex.GetType().Name, message = ex.Message });
            Console.Error.WriteLine("[worker] crash: " + ex);
            return 5;
        }
        finally
        {
            // Disconnect from an attached KOMPAS without closing it, and shut down only an instance this
            // Worker launched (spec 1.6: killing the user's KOMPAS is forbidden).
            try
            {
                sta.Run(() => session.ShutdownOwnedSessions(), "shutdown").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Write("warn", "session shutdown failed", new { type = ex.GetType().Name });
            }
        }

        return 0;
    }

    /// <summary>Serve EXACTLY ONE Host connection and exit when it ends.</summary>
    /// <remarks>MEASURED 04.10.2026: previously a wait loop created a NEW pipe after the channel dropped, so
    /// the Worker NEVER exited on Host disconnect — the Host then killed it (<c>kill_used: true</c>),
    /// contradicting both the class contract and the release requirement "confirm the Worker no longer runs
    /// COM". Exiting is safe: the Host stops the old process before starting a new one with the same pipe
    /// name, and no route provides a second connection.
    /// History: docs/decisions/worker-ipc.md#single-connection</remarks>
    private static async Task Serve(WorkerOptions options, CommandDispatcher session, WorkerLog log, CancellationToken cancellationToken)
    {
        {
            await using var pipe = CreateServer(options.PipeName);
            log.Write("info", "waiting for host connection", new { pipe = options.PipeName });

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            log.Write("info", "host connected");
            using var connectionGate = new SemaphoreSlim(1, 1);

            // A single Host connection at a time. Two concurrent clients would interleave
            // request/response frames on one stream and mix their results.
            await foreach (var frame in ReadFramesAsync(pipe, log, cancellationToken).ConfigureAwait(false))
            {
                _ = Task.Run(async () =>
                {
                    IpcFrame response;
                    try
                    {
                        response = await session.HandleAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.Write("error", "handler crashed", new { command = frame.Command, type = ex.GetType().Name, message = ex.Message });
                        response = Error(frame, new ErrorDto(
                            ErrorCodes.WorkerUnresponsive,
                            "Обработчик команды упал: " + ex.Message,
                            RetryPolicy.SameOperationId,
                            null,
                            true,
                            null));
                    }

                    await connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        await IpcChannel.WriteFrameAsync(pipe, response, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        connectionGate.Release();
                    }
                }, cancellationToken);
            }

            // THE CHANNEL ENDED — SO THE HOST IS GONE. No new connections, no new pipe: the process exits,
            // and the caller (finally in Main) closes sessions and releases COM.
            log.Write("info", "host disconnected; worker exiting");
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        // The default DACL of a pipe created by this user already limits it to that user and to
        // administrators; the session-unique name closes "another process guessed the name".
        // Buffer sizes are left to the runtime: a 16 MiB frame is written in pieces anyway.
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private static async IAsyncEnumerable<IpcFrame> ReadFramesAsync(
        NamedPipeServerStream pipe,
        WorkerLog log,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IpcFrame? frame;
            try
            {
                frame = await IpcChannel.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                log.Write("warn", "pipe read failed", new { message = ex.Message });
                yield break;
            }
            catch (KompasContractException ex)
            {
                log.Write("error", "protocol violation", new { code = ex.Code, message = ex.Message });
                yield break;
            }

            if (frame is null)
            {
                log.Write("info", "host closed the pipe");
                yield break;
            }

            yield return frame;
        }
    }

    private static IpcFrame Error(IpcFrame request, ErrorDto error) => new()
    {
        ProtocolVersion = IpcFrame.CurrentProtocolVersion,
        RequestId = request.RequestId,
        Kind = IpcFrameKind.Response,
        Command = request.Command,
        Completed = true,
        Error = error,
    };
}

/// <summary>Startup parameters of the Worker. The Host generates the pipe name per session.</summary>
public sealed class WorkerOptions
{
    public required string PipeName { get; init; }

    public string? LogPath { get; init; }

    /// <summary>Service directory for control copies. Required: a document-file copy is a write, and putting
    /// it "next to the document" means writing to the user's folder.</summary>
    public required string ControlCopyDirectory { get; init; }

    public static WorkerOptions Parse(string[] args)
    {
        string? pipe = null;
        string? log = null;
        string? copies = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--pipe":
                    pipe = args[++i];
                    break;
                case "--log":
                    log = args[++i];
                    break;
                case "--copies":
                    copies = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipe))
        {
            Console.Error.WriteLine("использование: KompasMcp.Worker --pipe <имя> --copies <каталог> [--log <файл>]");
            Environment.Exit(2);
        }

        if (string.IsNullOrWhiteSpace(copies))
        {
            // The directory is NOT substituted silently: the "next to the document" default is the very
            // defect that was fixed. Without a named directory no copies are taken at all.
            Console.Error.WriteLine("использование: KompasMcp.Worker --pipe <имя> --copies <каталог> [--log <файл>]");
            Environment.Exit(2);
        }

        return new WorkerOptions { PipeName = pipe!, LogPath = log, ControlCopyDirectory = copies! };
    }
}

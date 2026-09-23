using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace KompasMcp.Contracts.Ipc;

/// <summary>
/// Client (Host) side of a request/response session over a length-prefixed frame stream.
/// </summary>
/// <remarks>
/// This type exists because a frame stream has exactly one reader. The first version of the Host let
/// every caller run its own read loop, so two tool calls in flight at the same time read the same
/// pipe concurrently, interleaved their bytes, and turned the stream into garbage — the Host then
/// reported <c>WORKER_UNRESPONSIVE</c> ("Недопустимая длина кадра …") and, once the length prefix
/// happened to land on a valid value, a JSON parse error. Measured 18.09.2026 from the WorkBuddy
/// client, which issues tool calls concurrently; the acceptance suite called tools one at a time and
/// so never exercised the path.
///
/// Concurrency contract:
/// <list type="bullet">
/// <item>any number of callers may have a request in flight at once;</item>
/// <item>exactly one background loop reads the stream, and routes each response to its caller by
/// <see cref="IpcFrame.RequestId"/> — a caller never touches the stream to read;</item>
/// <item>writes are serialised behind one gate, because frames are not interleavable.</item>
/// </list>
/// The reader is the only party that can tell the stream is finished, so it is also the party that
/// fails every waiting request when the stream ends.
/// </remarks>
public sealed class IpcRequestChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcFrame>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;

    private volatile bool _broken;
    private int _disposed;

    public IpcRequestChannel(Stream stream)
    {
        _stream = stream;
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    /// <summary>
    /// True once the reader has stopped: the peer closed the pipe, the stream faulted, or the
    /// channel was disposed. A caller that sees this must obtain a fresh channel rather than send.
    /// </summary>
    public bool IsBroken => _broken;

    /// <summary>
    /// Send one request and await its answer. Safe to call from many callers at once.
    /// </summary>
    /// <remarks>
    /// A timeout is reported as <c>OUTCOME_UNKNOWN</c>, never as a cancellation: the peer may still
    /// be executing the command, so the caller must reconcile rather than assume nothing happened.
    /// </remarks>
    public async Task<IpcFrame> RequestAsync(string command, JsonNode? payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = new IpcFrame
        {
            ProtocolVersion = IpcFrame.CurrentProtocolVersion,
            RequestId = requestId,
            Kind = IpcFrameKind.Request,
            Command = command,
            Payload = payload,
        };

        var waiter = new TaskCompletionSource<IpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = waiter;
        try
        {
            // A request that arrives after the reader has already stopped would wait for an answer
            // nobody will deliver, so it is failed here instead. This does not race with the
            // reader's own sweep: the reader sets _broken before it walks the dictionary, so either
            // it sees this entry or this check sees its flag.
            if (_broken)
            {
                throw Disconnected();
            }

            await WriteAsync(request, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using (timeoutCts.Token.Register(static state => ((TaskCompletionSource<IpcFrame>)state!).TrySetCanceled(), waiter))
            {
                try
                {
                    return await waiter.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new KompasContractException(
                        ErrorCodes.OutcomeUnknown,
                        $"Ответ на '{command}' не получен за {timeout.TotalSeconds:0.#} с; исход команды может быть неизвестен.",
                        RetryPolicy.AfterReconciliation,
                        partialEffects: true,
                        details: new Dictionary<string, object?> { ["request_id"] = requestId });
                }
            }
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>Write one frame without waiting for an answer (shutdown notice, fire-and-forget).</summary>
    public async Task WriteAsync(IpcFrame frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcChannel.WriteFrameAsync(_stream, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await IpcChannel.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    // Clean close at a frame boundary: the peer is gone, not confused.
                    break;
                }

                if (frame.Kind == IpcFrameKind.Event)
                {
                    // No event subscribers in this build; an event is not an answer to anyone.
                    continue;
                }

                if (_pending.TryRemove(frame.RequestId, out var waiter))
                {
                    waiter.TrySetResult(frame);
                }
                // An answer nobody waits for is dropped: its caller timed out or was cancelled.
                // Treating it as a protocol violation would kill a healthy channel.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal.
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        _broken = true;

        // The reader has stopped, so no in-flight request will ever be answered. Each of them is
        // therefore "connection lost, outcome unknown" — whatever stopped the reader. A protocol
        // violation keeps its own wording, but not its WORKER_UNRESPONSIVE code: that code says
        // "the Worker is busy or hung, try again", and for a command that was already written that
        // is the one answer which could apply a mutation twice.
        var error = Disconnected(failure);

        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiter))
            {
                waiter.TrySetException(error);
            }
        }
    }

    private static KompasContractException Disconnected(Exception? cause = null)
    {
        var details = new Dictionary<string, object?>();
        if (cause is not null)
        {
            details["cause"] = cause.GetType().Name;
            details["cause_message"] = cause.Message;
        }

        return new KompasContractException(
            ErrorCodes.ApplicationDisconnected,
            cause is null
                ? "Worker закрыл соединение до ответа на команду."
                : $"Канал к Worker оборвался до ответа на команду: {cause.Message}",
            RetryPolicy.AfterReconciliation,
            partialEffects: true,
            details: details.Count == 0 ? null : details);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The reader is expected to end when the stream goes away.
        }

        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}

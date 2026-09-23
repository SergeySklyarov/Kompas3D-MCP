using System.Threading.Channels;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Queueing;

/// <summary>One queued CAD command plus the bookkeeping needed to cancel or report it.</summary>
public sealed class QueuedCommand
{
    public QueuedCommand(string operationId, string tool, CancellationTokenSource cancellation)
    {
        OperationId = operationId;
        Tool = tool;
        Cancellation = cancellation;
        EnqueuedUtc = DateTimeOffset.UtcNow;
    }

    public string OperationId { get; }

    public string Tool { get; }

    public DateTimeOffset EnqueuedUtc { get; }

    public CancellationTokenSource Cancellation { get; }

    /// <summary>Set when the command left the queue and started executing.</summary>
    public bool Started { get; set; }
}

/// <summary>
/// Bounded FIFO for CAD work, with explicit backpressure (spec 1.13: 64 commands, QUEUE_FULL).
/// </summary>
/// <remarks>
/// Two properties matter for the contract and are enforced here rather than left to chance:
/// <list type="bullet">
/// <item>The queue holds commands for <b>one</b> КОМПАС instance and hands them out one at a time,
/// so a client sending ten parallel requests gets ten sequential CAD executions instead of ten
/// concurrent COM calls from ten threads (test R04).</item>
/// <item>Cancellation removes a command that has not started. A command that has started cannot be
/// cancelled here — that is reported as CANCEL_NOT_CONFIRMED, not silently accepted (test R03).</item>
/// </list>
/// </remarks>
public sealed class CadCommandQueue : IAsyncDisposable
{
    private readonly Channel<QueuedCommand> _channel;
    private readonly int _capacity;

    /// <summary>
    /// Commands waiting their turn, indexed for cancellation. Kept separately from the channel
    /// because draining the channel to look for an id would consume the queue and reorder
    /// execution — cancellation must be a lookup, never a read.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, QueuedCommand> _pending = new(StringComparer.Ordinal);

    private long _accepted;
    private long _rejected;
    private long _cancelledInQueue;
    private long _completed;

    public CadCommandQueue(int capacity = 64)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ёмкость очереди должна быть положительной.");
        }

        _capacity = capacity;
        _channel = Channel.CreateBounded<QueuedCommand>(new BoundedChannelOptions(capacity)
        {
            // TryWrite below always gets a definite answer, so nothing can block an MCP handler:
            // a full queue becomes QUEUE_FULL immediately and the client backs off. Blocking
            // instead would turn backpressure into a timeout whose outcome is unknown.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Count => _channel.Reader.Count;

    public int Capacity => _capacity;

    public IReadOnlyDictionary<string, long> Statistics() => new Dictionary<string, long>
    {
        ["queued"] = Count,
        ["capacity"] = _capacity,
        ["accepted"] = Interlocked.Read(ref _accepted),
        ["rejected"] = Interlocked.Read(ref _rejected),
        ["cancelled_in_queue"] = Interlocked.Read(ref _cancelledInQueue),
        ["completed"] = Interlocked.Read(ref _completed),
    };

    public void Enqueue(QueuedCommand command)
    {
        _pending[command.OperationId] = command;

        if (_channel.Writer.TryWrite(command))
        {
            Interlocked.Increment(ref _accepted);
            return;
        }

        _pending.TryRemove(command.OperationId, out _);
        Interlocked.Increment(ref _rejected);
        throw new KompasContractException(
            ErrorCodes.QueueFull,
            $"Очередь CAD-команд заполнена (предел {_capacity}). Новая работа не принимается, пока не разобрана предыдущая.",
            RetryPolicy.SameOperationId,
            details: new Dictionary<string, object?> { ["queue_limit"] = _capacity, ["queued"] = Count });
    }

    /// <summary>
    /// Release one slot: the command with this id has been served — successfully, with an error or
    /// as a cancellation, in all three cases it is no longer outstanding.
    /// </summary>
    /// <remarks>
    /// This exists because the Host does not execute commands out of this queue: it enqueues for
    /// admission, then dispatches to the Worker directly (the Worker's single STA lane is what
    /// serialises CAD). Without a matching release the channel is a one-time budget of
    /// <c>capacity</c> calls per process, and a long session starts failing with QUEUE_FULL on what
    /// is its 65th mutation — measured in the acceptance run that added the multi-body U05 group.
    /// Reading the head rather than this particular id is deliberate: the channel is the counter of
    /// outstanding commands, and <paramref name="operationId"/> is removed from the cancellation
    /// index where identity actually matters.
    /// </remarks>
    public bool Complete(string operationId)
    {
        _pending.TryRemove(operationId, out _);
        if (_channel.Reader.TryRead(out var taken))
        {
            Interlocked.Increment(ref _completed);
            // Its CTS is owned by the caller's using/finally, so it is not disposed here.
            _ = taken;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cancel a command that has not started. Returns false when it is already executing or is not
    /// queued at all, so the caller reports CANCEL_NOT_CONFIRMED instead of claiming a stop
    /// (spec 1.8: running cancel is best effort, and an in-flight COM call cannot be aborted).
    /// </summary>
    /// <remarks>
    /// The pending entry is removed here: from this moment the server no longer claims the command
    /// is waiting, even though the object still sits in the channel until the reader skips it.
    /// </remarks>
    public bool TryCancelQueued(string operationId)
    {
        if (!_pending.TryGetValue(operationId, out var command) || command.Started)
        {
            return false;
        }

        _pending.TryRemove(operationId, out _);
        command.Cancellation.Cancel();
        Interlocked.Increment(ref _cancelledInQueue);
        return true;
    }

    public bool IsQueued(string operationId) => _pending.ContainsKey(operationId);

    /// <summary>
    /// Next command to execute, skipping anything cancelled after it was queued. Returns null only
    /// when the channel is closed; blocks otherwise, so callers pass a cancellation token.
    /// </summary>
    public async ValueTask<QueuedCommand?> DequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_channel.Reader.TryRead(out var command))
                {
                    continue;
                }

                _pending.TryRemove(command.OperationId, out _);

                // Cancelled while waiting: never handed to the Worker at all.
                if (!command.Cancellation.IsCancellationRequested)
                {
                    command.Started = true;
                    return command;
                }
            }
        }
        catch (ChannelClosedException)
        {
            return null;
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

using System.IO.Pipes;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The single-reader contract of the Host-Worker pipe.</summary>
/// <remarks>INVARIANT: one serialised reader owns the stream, so concurrent tool calls each get their own
/// answer. MEASURED: before 18.09.2026 every caller ran its own read loop, so two in-flight calls
/// interleaved their bytes and produced a bad frame length and a JSON parse failure. The fake Worker below
/// answers a "slow" request LATER than a "fast" one, so answers come back in reverse order — the cheapest
/// deterministic shape of the failure. History: docs/decisions/tests.md#ipc-channel</remarks>
public class IpcRequestChannelTests
{
    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectAsync()
    {
        var name = "kompas-mcp-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await connected;
        return (server, client);
    }

    /// <summary>Answers every request, out of order on purpose, through one serialised writer.</summary>
    private static async Task FakeWorkerAsync(Stream server, CancellationToken cancellationToken)
    {
        var writeGate = new SemaphoreSlim(1, 1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IpcFrame? frame;
                try
                {
                    frame = await IpcChannel.ReadFrameAsync(server, cancellationToken);
                }
                catch (Exception)
                {
                    return;
                }

                if (frame is null)
                {
                    return;
                }

                var request = frame;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(request.Command == "slow" ? 150 : 0, CancellationToken.None);
                    var response = new IpcFrame
                    {
                        ProtocolVersion = IpcFrame.CurrentProtocolVersion,
                        RequestId = request.RequestId,
                        Kind = IpcFrameKind.Response,
                        Command = request.Command,
                        Payload = new JsonObject { ["echo"] = request.Command },
                    };

                    await writeGate.WaitAsync(CancellationToken.None);
                    try
                    {
                        await IpcChannel.WriteFrameAsync(server, response, CancellationToken.None);
                    }
                    finally
                    {
                        writeGate.Release();
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            writeGate.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentRequests_EachReceiveTheirOwnAnswer()
    {
        var (server, client) = await ConnectAsync();
        using var workerStop = new CancellationTokenSource();
        var worker = FakeWorkerAsync(server, workerStop.Token);

        var channel = new IpcRequestChannel(client);
        try
        {
            const int count = 8;
            var expected = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? "slow" : "fast").ToArray();
            var calls = expected
                .Select(command => channel.RequestAsync(command, null, TimeSpan.FromSeconds(20), isMutation: false, CancellationToken.None))
                .ToArray();

            var answers = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));

            for (var i = 0; i < count; i++)
            {
                // The echo is the proof of ownership: a mis-routed frame carries the other command.
                Assert.Equal(expected[i], answers[i].Command);
                Assert.Equal(expected[i], answers[i].Payload!["echo"]!.GetValue<string>());
            }

            Assert.Equal(count, answers.Select(a => a.RequestId).Distinct().Count());
        }
        finally
        {
            workerStop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // The fake worker is allowed to end however it likes; the assertions already ran.
            }
        }
    }

    /// <summary>INVARIANT: a client cancel AFTER a mutation frame was written is NOT "the command was not sent".</summary>
    /// <remarks>MEASURED (FIX H1, review 05.10.2026): a client that believed "cancelled before dispatch"
    /// repeated the mutation with a NEW operation_id and it applied twice.
    /// History: docs/decisions/tests.md#ipc-channel</remarks>
    [Fact]
    public async Task ClientCancelsAfterTheFrameWasWritten_MutationIsOutcomeUnknownNotCancelled()
    {
        var (server, client) = await ConnectAsync();
        var channel = new IpcRequestChannel(client);
        using var workerStop = new CancellationTokenSource();

        // The peer receives the frame and does NOT answer: the command definitely left, but its outcome is
        // unknown to anyone — the state the old code called "cancelled before dispatch".
        var received = await StartSilentWorkerAsync(server, workerStop.Token);

        try
        {
            using var cancellation = new CancellationTokenSource();
            var call = channel.RequestAsync("kompas_extrude", null, TimeSpan.FromSeconds(30),
                isMutation: true, cancellation.Token);

            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(10)), "кадр не дошёл до пира");
            cancellation.Cancel();

            var error = await Assert.ThrowsAsync<KompasContractException>(
                async () => await call.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(ErrorCodes.OutcomeUnknown, error.Code);
            Assert.Equal(RetryPolicy.AfterReconciliation, error.RetryPolicy);
            Assert.True(error.PartialEffects, "команда уже отправлена: частичный эффект обязан быть назван");
        }
        finally
        {
            workerStop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>A read after the frame was written: the model did not change, but the cancel is not confirmed.</summary>
    [Fact]
    public async Task ClientCancelsAfterTheFrameWasWritten_ReadIsCancelNotConfirmed()
    {
        var (server, client) = await ConnectAsync();
        var channel = new IpcRequestChannel(client);
        using var workerStop = new CancellationTokenSource();
        var received = await StartSilentWorkerAsync(server, workerStop.Token);

        try
        {
            using var cancellation = new CancellationTokenSource();
            var call = channel.RequestAsync("kompas_list_features", null, TimeSpan.FromSeconds(30),
                isMutation: false, cancellation.Token);

            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(10)), "кадр не дошёл до пира");
            cancellation.Cancel();

            var error = await Assert.ThrowsAsync<KompasContractException>(
                async () => await call.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(ErrorCodes.CancelNotConfirmed, error.Code);
        }
        finally
        {
            workerStop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>INVARIANT: a cancel BEFORE the frame is written stays a cancel — the command did not reach
    /// the Worker, and this is the only case where "cancelled" is a confirmed state.</summary>
    [Fact]
    public async Task ClientCancelsBeforeTheFrameIsWritten_CancellationStaysACancellation()
    {
        var (server, client) = await ConnectAsync();
        var channel = new IpcRequestChannel(client);
        using var workerStop = new CancellationTokenSource();

        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await channel.RequestAsync("kompas_extrude", null, TimeSpan.FromSeconds(30),
                    isMutation: true, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            workerStop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
        }
    }

    /// <summary>A peer that reads one frame and stays silent. Returns the awaitable for frame receipt.</summary>
    private static async Task<Task<bool>> StartSilentWorkerAsync(Stream server, CancellationToken cancellationToken)
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var frame = await IpcChannel.ReadFrameAsync(server, cancellationToken);
                if (frame is not null)
                {
                    received.TrySetResult(true);
                }
            }
            catch (Exception)
            {
                // The peer goes away — the receipt awaitable stays false, and the test sees it.
            }
        }, CancellationToken.None);

        await Task.CompletedTask;
        return received.Task;
    }

    [Fact]
    public async Task ARequestInFlightWhenThePeerGoesAway_FailsInsteadOfHanging()
    {
        var (server, client) = await ConnectAsync();
        var channel = new IpcRequestChannel(client);
        using var workerStop = new CancellationTokenSource();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A peer that reads the request but never answers. The request is then genuinely in flight
        // and already written, so the failure cannot be blamed on the write: the reader is the only
        // party that can observe the end of the stream, so the reader is the party that must fail
        // every waiter. Otherwise a dead Worker looks exactly like a wedged one.
        var silentWorker = Task.Run(async () =>
        {
            try
            {
                var frame = await IpcChannel.ReadFrameAsync(server, workerStop.Token);
                if (frame is not null)
                {
                    received.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, workerStop.Token);
                }
            }
            catch (Exception)
            {
                // Expected: the far end goes away.
            }
        });

        try
        {
            var call = channel.RequestAsync("kompas_health", null, TimeSpan.FromSeconds(30), isMutation: false, CancellationToken.None);
            Assert.True(await received.Task.WaitAsync(TimeSpan.FromSeconds(10)), "the request never reached the peer");

            server.Dispose();

            var error = await Assert.ThrowsAsync<KompasContractException>(
                async () => await call.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.True(
                error.Code == ErrorCodes.ApplicationDisconnected,
                $"expected {ErrorCodes.ApplicationDisconnected}, got {error.Code}: {error.Message}");
            Assert.True(channel.IsBroken);
        }
        finally
        {
            workerStop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
            try
            {
                await silentWorker.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // The fake worker is allowed to end however it likes; the assertions already ran.
            }
        }
    }
}

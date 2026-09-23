using System.IO.Pipes;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// The single-reader contract of the Host-Worker pipe.
/// </summary>
/// <remarks>
/// This guards the ABSENCE of the 18.09.2026 defect. The Host used to let every caller run its own
/// read loop, so two tool calls in flight at once read the same stream concurrently, interleaved
/// their bytes, and produced
///   "WORKER_UNRESPONSIVE — Недопустимая длина кадра 1919951483 байт" (the four bytes were '{"pr')
/// and
///   "JsonException: 'o' is an invalid start of a value".
/// Measured from the WorkBuddy client, which issues tool calls concurrently; the acceptance suite
/// called tools one at a time and never touched the path.
///
/// The fake Worker below answers a "slow" request LATER than a "fast" one, so answers come back in
/// the opposite order to the requests. That is the cheapest deterministic shape of the failure: a
/// per-caller reader consumes someone else's frame and either mis-routes it or tears it.
/// </remarks>
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
                .Select(command => channel.RequestAsync(command, null, TimeSpan.FromSeconds(20), CancellationToken.None))
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
            var call = channel.RequestAsync("kompas_health", null, TimeSpan.FromSeconds(30), CancellationToken.None);
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

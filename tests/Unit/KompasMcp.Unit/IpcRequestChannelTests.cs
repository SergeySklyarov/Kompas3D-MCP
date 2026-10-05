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

    /// <summary>
    /// Отмена клиентом ПОСЛЕ записи кадра мутации — это НЕ «команда не отправлялась».
    /// </summary>
    /// <remarks>
    /// FIX H1 ревью 05.10.2026. Прежде любая <c>OperationCanceledException</c> с токеном клиента
    /// выходила наружу, и вызывающий писал терминальное <c>cancelled</c> без требования
    /// согласования: клиент получал «команда отменена в очереди Host и не отправлялась в КОМПАС»,
    /// хотя Worker уже выполнял команду. Клиент, поверивший ответу, повторял мутацию с НОВЫМ
    /// operation_id — и мутация применялась дважды.
    /// </remarks>
    [Fact]
    public async Task ClientCancelsAfterTheFrameWasWritten_MutationIsOutcomeUnknownNotCancelled()
    {
        var (server, client) = await ConnectAsync();
        var channel = new IpcRequestChannel(client);
        using var workerStop = new CancellationTokenSource();

        // Пир принимает кадр и НЕ отвечает: команда гарантированно ушла, но её исход никому не
        // известен. Это и есть состояние, которое прежний код называл «отменено до отправки».
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

    /// <summary>Чтение после отправки: модель не менялась, но отмена не подтверждена.</summary>
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

    /// <summary>
    /// Отмена ДО записи кадра остаётся отменой: команда в Worker не ушла, и это единственный
    /// случай, где «отменено» — подтверждённое состояние.
    /// </summary>
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

    /// <summary>Пир, который читает один кадр и молчит. Возвращает ожидание получения кадра.</summary>
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
                // Пир уходит — ожидание получения остаётся false, и тест это увидит.
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

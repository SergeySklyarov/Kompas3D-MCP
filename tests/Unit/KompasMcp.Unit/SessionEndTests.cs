using System.IO.Pipes;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>How a session ends: the Host's shutdown frame is answered BEFORE the channel is closed, and the
/// Worker runs its end sequence once and names the route it ended on.</summary>
/// <remarks>MEASURED: the Host wrote the frame and disposed the pipe in the same breath, so the Worker read
/// zero bytes, the command was unreachable and the journal could not name the reason of the end.
/// History: docs/decisions/tests.md#session-end-frame</remarks>
public class SessionEndTests
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

    /// <summary>A peer that answers the shutdown command and reports the command it saw.</summary>
    private static async Task<(Task<IpcFrame?> Seen, Task Worker)> AnsweringWorkerAsync(
        Stream server,
        CancellationToken cancellationToken)
    {
        var seen = new TaskCompletionSource<IpcFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () =>
        {
            try
            {
                var frame = await IpcChannel.ReadFrameAsync(server, cancellationToken);
                seen.TrySetResult(frame);
                if (frame is null)
                {
                    return;
                }

                var answer = new IpcFrame
                {
                    ProtocolVersion = IpcFrame.CurrentProtocolVersion,
                    RequestId = frame.RequestId,
                    Kind = IpcFrameKind.Response,
                    Command = frame.Command,
                    Completed = true,
                    Payload = new JsonObject
                    {
                        ["sessions_closed"] = true,
                        ["owned_instance_shutdown"] = new JsonArray
                        {
                            new JsonObject { ["process_id"] = 4242, ["process_exited"] = true },
                        },
                    },
                };
                await IpcChannel.WriteFrameAsync(server, answer, CancellationToken.None);
            }
            catch (Exception)
            {
                seen.TrySetResult(null);
            }
        }, CancellationToken.None);

        await Task.CompletedTask;
        return (seen.Task, worker);
    }

    [Fact]
    public async Task ShutdownFrameIsAnsweredBeforeTheChannelIsClosed()
    {
        var (server, client) = await ConnectAsync();
        using var stop = new CancellationTokenSource();
        var channel = new IpcRequestChannel(client);
        var (seen, worker) = await AnsweringWorkerAsync(server, stop.Token);
        try
        {
            var (answer, problem) = await SessionEndHandshake.SendAsync(channel, 10_000);

            Assert.Null(problem);
            Assert.NotNull(answer);
            Assert.Equal(WorkerCommands.Shutdown, answer.Command);
            Assert.True(answer.Payload!["sessions_closed"]!.GetValue<bool>());

            // The frame the answer belongs to: the reply is routed by request id, so an answer is proof
            // that the command itself arrived.
            var request = await seen.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(request);
            Assert.Equal(WorkerCommands.Shutdown, request.Command);
        }
        finally
        {
            stop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
            await Task.WhenAny(worker, Task.Delay(5000));
        }
    }

    [Fact]
    public async Task SilentPeer_NoReportButTheFrameWasDelivered()
    {
        var (server, client) = await ConnectAsync();
        using var stop = new CancellationTokenSource();
        var channel = new IpcRequestChannel(client);
        var seen = new TaskCompletionSource<IpcFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () =>
        {
            try
            {
                seen.TrySetResult(await IpcChannel.ReadFrameAsync(server, stop.Token));
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            catch (Exception)
            {
                // The far end goes away; the receipt awaitable keeps whatever was already set.
            }
        });
        try
        {
            var (answer, problem) = await SessionEndHandshake.SendAsync(channel, 300);

            Assert.Null(answer);
            Assert.Equal(ErrorCodes.OutcomeUnknown, problem);
            Assert.NotNull(await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            stop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
            await Task.WhenAny(worker, Task.Delay(5000));
        }
    }

    [Fact]
    public async Task SpentWindow_WritesNoFrameAtAll()
    {
        var (server, client) = await ConnectAsync();
        using var stop = new CancellationTokenSource();
        var channel = new IpcRequestChannel(client);
        var read = Task.Run(() => IpcChannel.ReadFrameAsync(server, stop.Token));
        try
        {
            var (answer, problem) = await SessionEndHandshake.SendAsync(channel, 0);

            Assert.Null(answer);
            Assert.Equal(ErrorCodes.OutcomeUnknown, problem);

            // A frame that nobody waits for is the defect itself: with the window already spent the Host
            // closes the channel instead of writing into it.
            var finished = await Task.WhenAny(read, Task.Delay(500)) == read;
            Assert.False(finished, "кадр записан при исчерпанном окне");
        }
        finally
        {
            stop.Cancel();
            await channel.DisposeAsync();
            server.Dispose();
        }
    }

    [Fact]
    public async Task PeerGone_NoReportAndTheDisconnectCode()
    {
        var (server, client) = await ConnectAsync();
        var channel = new IpcRequestChannel(client);
        try
        {
            server.Dispose();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!channel.IsBroken && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            var (answer, problem) = await SessionEndHandshake.SendAsync(channel, 5_000);

            Assert.Null(answer);
            Assert.Equal(ErrorCodes.ApplicationDisconnected, problem);
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    [Fact]
    public void EndState_NamesTheCommandRouteAndRemembersThatItRan()
    {
        var byCommand = new SessionEndState();
        Assert.Equal(SessionEndState.ChannelLost, byCommand.Reason);
        Assert.False(byCommand.SequenceDone);

        byCommand.Complete(byCommand: true);
        Assert.Equal(SessionEndState.ByCommand, byCommand.Reason);
        Assert.True(byCommand.SequenceDone);
        Assert.True(byCommand.CommandDelivered);
    }

    [Fact]
    public void EndState_KeepsTheCommandRouteWhenTheExitPathAlsoReports()
    {
        var byCommand = new SessionEndState();
        byCommand.Complete(byCommand: true);
        byCommand.Complete(byCommand: false);

        Assert.Equal(SessionEndState.ByCommand, byCommand.Reason);
        Assert.True(byCommand.CommandDelivered);
        Assert.True(byCommand.SequenceDone);
    }

    [Fact]
    public void EndState_ChannelRouteWhenNoCommandWasDelivered()
    {
        var lost = new SessionEndState();
        lost.Complete(byCommand: false);

        Assert.Equal(SessionEndState.ChannelLost, lost.Reason);
        Assert.True(lost.SequenceDone);
        Assert.False(lost.CommandDelivered);
    }
}

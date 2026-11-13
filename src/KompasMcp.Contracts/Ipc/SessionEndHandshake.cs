namespace KompasMcp.Contracts.Ipc;

/// <summary>The Host's LAST frame: hand the Worker the shutdown command and wait for its answer.</summary>
/// <remarks>INVARIANT: the channel is closed only after the answer or after the window expires, and the
/// caller spends the SAME window on the process-exit wait - the session end must not grow a second wait.
/// MEASURED: a write followed by an immediate close lost the whole frame - the Worker read zero bytes and
/// learned about the end only from the broken channel.
/// History: docs/decisions/host.md#client-shutdown</remarks>
public static class SessionEndHandshake
{
    /// <summary>Send the shutdown command; return the Worker's answer, or why there is none.</summary>
    public static async Task<(IpcFrame? Answer, string? Problem)> SendAsync(
        IpcRequestChannel channel,
        int windowMs)
    {
        if (windowMs <= 0)
        {
            return (null, ErrorCodes.OutcomeUnknown);
        }

        try
        {
            // isMutation: false — the command ends the session, it does not edit the model; the flag only
            // decides what a CLIENT cancellation after the write would mean, and this call passes none.
            var answer = await channel.RequestAsync(
                WorkerCommands.Shutdown,
                null,
                TimeSpan.FromMilliseconds(windowMs),
                isMutation: false,
                CancellationToken.None).ConfigureAwait(false);
            return (answer, null);
        }
        catch (KompasContractException ex)
        {
            return (null, ex.Code);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return (null, ex.GetType().Name);
        }
    }
}

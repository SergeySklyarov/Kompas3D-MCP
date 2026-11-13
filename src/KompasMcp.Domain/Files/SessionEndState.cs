namespace KompasMcp.Domain.Files;

/// <summary>How the Worker process ends: the reason its journal names, and whether the session-end sequence
/// has already run.</summary>
/// <remarks>INVARIANT: the sequence runs AT MOST ONCE per Worker process. While the shutdown frame never
/// arrived, only the process-exit path could run it; now that the command is delivered and answered, both
/// callers are possible and a second run would rescue and quit a second time.
/// History: docs/decisions/host.md#client-shutdown</remarks>
public sealed class SessionEndState
{
    /// <summary>The shutdown command was delivered: the end came by command.</summary>
    public const string ByCommand = "by_command";

    /// <summary>The channel broke without the command: the end came by the channel.</summary>
    public const string ChannelLost = "channel_lost";

    /// <summary>True once the sequence has finished; a second caller must not repeat it.</summary>
    public bool SequenceDone { get; private set; }

    /// <summary>True when the sequence ran because the Host asked for it.</summary>
    public bool CommandDelivered { get; private set; }

    /// <summary>The reason the journal names for the end of this Worker process.</summary>
    public string Reason => CommandDelivered ? ByCommand : ChannelLost;

    /// <summary>Record that the sequence finished, and on whose route.</summary>
    public void Complete(bool byCommand)
    {
        CommandDelivered |= byCommand;
        SequenceDone = true;
    }
}

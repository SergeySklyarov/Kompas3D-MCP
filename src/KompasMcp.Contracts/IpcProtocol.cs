using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KompasMcp.Contracts.Ipc;

public enum IpcFrameKind
{
    Request,
    Response,
    Event,
}

/// <summary>
/// Versioned JSON frame: 4-byte little-endian length prefix + UTF-8 payload (spec 1.5).
/// Binary artefacts travel as path+hash references, never inside a frame.
/// </summary>
public sealed record IpcFrame
{
    public const int CurrentProtocolVersion = 1;

    /// <summary>Hard cap on one frame payload (spec 1.5): 16 MiB.</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public const int LengthPrefixBytes = 4;

    public required int ProtocolVersion { get; init; }

    /// <summary>Correlates a response to a request within one pipe connection.</summary>
    public required string RequestId { get; init; }

    public required IpcFrameKind Kind { get; init; }

    /// <summary>Command name for requests, event name for events; echoed on responses.</summary>
    public required string Command { get; init; }

    /// <summary>Command payload for requests / event body.</summary>
    public JsonNode? Payload { get; init; }

    /// <summary>Transport-level failure distinct from a CAD error (Worker unreachable, oversize…).</summary>
    public ErrorDto? Error { get; init; }

    /// <summary>True when the Worker executed the command and the payload is its answer.</summary>
    public bool Completed { get; init; } = true;

    public byte[] Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, KompJson.Options);
        if (json.Length > MaxPayloadBytes)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Кадр IPC весит {json.Length} байт, лимит {MaxPayloadBytes}.",
                details: new Dictionary<string, object?> { ["frameBytes"] = json.Length });
        }

        var frame = new byte[json.Length + LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, LengthPrefixBytes), json.Length);
        json.CopyTo(frame.AsSpan(LengthPrefixBytes));
        return frame;
    }

    /// <summary>
    /// Decode a payload that was already read and length-checked by the stream reader.
    /// Rejects a protocol version this build does not speak rather than guessing field names.
    /// </summary>
    public static IpcFrame DecodePayload(ReadOnlySpan<byte> json)
    {
        var frame = JsonSerializer.Deserialize<IpcFrame>(json, KompJson.Options)
            ?? throw new KompasContractException(ErrorCodes.WorkerUnresponsive, "Пустой кадр IPC.");

        if (frame.ProtocolVersion != CurrentProtocolVersion)
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                $"Worker говорит по версии протокола {frame.ProtocolVersion}, Host ожидает {CurrentProtocolVersion}.",
                details: new Dictionary<string, object?>
                {
                    ["peer_protocol_version"] = frame.ProtocolVersion,
                    ["expected_protocol_version"] = CurrentProtocolVersion,
                });
        }

        return frame;
    }
}

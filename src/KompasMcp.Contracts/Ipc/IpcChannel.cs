using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KompasMcp.Contracts.Ipc;

/// <summary>
/// Length-prefixed frame reader/writer over an arbitrary duplex stream. Shared by Host and
/// Worker so the two ends cannot disagree about where a frame stops.
/// </summary>
/// <remarks>
/// The reader is deliberately allocation-light and strict: an oversized length prefix aborts the
/// connection rather than buffering 4 GiB, and a truncated frame raises rather than returning a
/// half-parsed object. A peer that dies mid-frame must not be able to make us invent a response.
/// </remarks>
public static class IpcChannel
{
    /// <summary>Read one frame, or null when the peer closed the pipe cleanly at a frame boundary.</summary>
    public static async Task<IpcFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[IpcFrame.LengthPrefixBytes];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcFrame.MaxPayloadBytes)
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                $"Недопустимая длина кадра {length} байт (допустимо 1…{IpcFrame.MaxPayloadBytes}); соединение разорвано.");
        }

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                "Поток оборвался в середине кадра: результат команды неизвестен.");
        }

        return IpcFrame.DecodePayload(payload);
    }

    public static async Task WriteFrameAsync(Stream stream, IpcFrame frame, CancellationToken cancellationToken)
    {
        var bytes = frame.Encode();
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            await stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
    }

    // RequestAsync used to live here: it wrote the request, then ran its own read loop until the
    // matching response arrived. That is safe for one caller and corrupt for two. When a client
    // issues tool calls concurrently — a long mutation plus a kompas_health probe is the case the
    // design exists for — both callers read the same stream, interleave their bytes, and the length
    // prefix lands mid-JSON. Measured 18.09.2026 from the WorkBuddy client:
    //   "Недопустимая длина кадра 1919951483 байт" (the four bytes were '{"pr')
    //   "JsonException: 'o' is an invalid start of a value"
    // Request/response over a shared stream now belongs to IpcRequestChannel, which owns the single
    // reader and routes answers by request id. Do not reintroduce a per-caller read loop here.

    private static async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return filled == 0;
            }

            filled += read;
        }

        return true;
    }

    public static T Payload<T>(this IpcFrame frame)
    {
        if (frame.Payload is null)
        {
            throw new KompasContractException(ErrorCodes.WorkerUnresponsive, $"Worker не прислал payload для '{frame.Command}'.");
        }

        return frame.Payload.Deserialize<T>(KompJson.Options)
            ?? throw new KompasContractException(ErrorCodes.WorkerUnresponsive, $"Payload для '{frame.Command}' не разбирается как {typeof(T).Name}.");
    }

    public static JsonNode? ToPayload<T>(T value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, KompJson.Options);
}

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KompasMcp.Contracts.Ipc;

/// <summary>Length-prefixed frame reader/writer over an arbitrary duplex stream. Shared by Host and Worker so the two
/// ends cannot disagree about where a frame stops.</summary> <remarks>The reader is deliberately allocation-light and
/// strict: an oversized length prefix aborts the connection rather than buffering 4 GiB, and a truncated frame raises
/// rather than returning a half-parsed object. A peer that dies mid-frame must not be able to make us invent a
/// response.</remarks>
public static class IpcChannel
{
    /// <summary>Why a frame stream ended or refused a frame, as <c>details["reason"]</c> of the exception.</summary>
    /// <remarks>INVARIANT: "the channel ended INSIDE a frame" and "the peer speaks another protocol" are
    /// different diagnoses with different owners: the first is what an abruptly ended Host looks like and is
    /// not a violation, the second is a real one. A named reason is what lets the reader of the journal tell
    /// them apart — the message text alone was not enough and was not even logged.
    /// History: docs/decisions/contracts.md#channel-end-reason</remarks>
    public static class EndReason
    {
        /// <summary>The stream ended before the announced frame was complete (length prefix or payload).</summary>
        public const string FrameTruncated = "frame_truncated";

        /// <summary>The length prefix announced a size no frame can have: the peer is not speaking this
        /// protocol, or the stream is misaligned.</summary>
        public const string InvalidFrameLength = "invalid_frame_length";
    }

    /// <summary>Read one frame, or null when the peer closed the pipe cleanly at a frame boundary.</summary>
    /// <remarks>INVARIANT: only a read of ZERO bytes is a clean end. A length prefix cut short is a channel
    /// that ended inside a frame and is named as such: reading it as "the peer left" used to report a
    /// truncated frame as a normal close.</remarks>
    public static async Task<IpcFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[IpcFrame.LengthPrefixBytes];
        var headerRead = await ReadUpToAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < IpcFrame.LengthPrefixBytes)
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                $"Канал оборвался на префиксе длины кадра: прочитано {headerRead} из "
                + $"{IpcFrame.LengthPrefixBytes} байт.",
                details: new Dictionary<string, object?>
                {
                    ["reason"] = EndReason.FrameTruncated,
                    ["stage"] = "length_prefix",
                    ["read_bytes"] = headerRead,
                    ["expected_bytes"] = IpcFrame.LengthPrefixBytes,
                });
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcFrame.MaxPayloadBytes)
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                $"Недопустимая длина кадра {length} байт (допустимо 1…{IpcFrame.MaxPayloadBytes}); соединение разорвано.",
                details: new Dictionary<string, object?>
                {
                    ["reason"] = EndReason.InvalidFrameLength,
                    ["frame_bytes"] = length,
                    ["max_bytes"] = IpcFrame.MaxPayloadBytes,
                });
        }

        var payload = new byte[length];
        var payloadRead = await ReadUpToAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new KompasContractException(
                ErrorCodes.WorkerUnresponsive,
                $"Поток оборвался в середине кадра: прочитано {payloadRead} из {length} байт, результат "
                + "команды неизвестен.",
                details: new Dictionary<string, object?>
                {
                    ["reason"] = EndReason.FrameTruncated,
                    ["stage"] = "payload",
                    ["read_bytes"] = payloadRead,
                    ["expected_bytes"] = length,
                });
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

    // INVARIANT: request/response over a shared stream belongs to IpcRequestChannel, which owns the
    // single reader and routes answers by request id; a per-caller read loop here lets two callers
    // interleave their bytes and corrupts the stream.
    // History: docs/decisions/contracts.md#ipc-read-loop

    private static async ValueTask<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
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

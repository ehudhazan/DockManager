// File: src/eh.DockManager.Server/Docker/DockerStreamDemuxer.cs
using System.Buffers;
using System.Runtime.CompilerServices;

namespace eh.DockManager.Server.Docker;

/// <summary>Frame streams emitted by the Docker remote API when TTY is disabled.</summary>
public enum DockerStreamType : byte
{
    Stdin = 0,
    Stdout = 1,
    Stderr = 2,
    SystemError = 3
}

/// <summary>
/// Demultiplexes (and multiplexes) the Docker remote API attach/exec/log frame format:
/// an 8-byte header (stream type, 3 reserved bytes, big-endian length) followed by the payload.
/// </summary>
public static class DockerStreamDemuxer
{
    public const int HeaderSize = 8;

    /// <summary>Reads frames from <paramref name="source"/>, yielding decoded payloads.</summary>
    public static async IAsyncEnumerable<(DockerStreamType Stream, byte[] Data)> DemuxAsync(
        Stream source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        while (true)
        {
            int read = await ReadExactAsync(source, header, cancellationToken).ConfigureAwait(false);
            if (read == 0) yield break;
            if (read < HeaderSize) throw new IOException("Truncated Docker stream header.");

            var streamType = (DockerStreamType)header[0];
            int length = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            if (length < 0) throw new IOException("Invalid Docker stream frame length.");
            if (length == 0)
            {
                yield return (streamType, Array.Empty<byte>());
                continue;
            }

            var payload = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int got = await ReadExactAsync(source, payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                if (got < length) throw new IOException("Truncated Docker stream payload.");
                var exact = new byte[length];
                Buffer.BlockCopy(payload, 0, exact, 0, length);
                yield return (streamType, exact);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <summary>Encodes a payload into the Docker frame format.</summary>
    public static byte[] Encode(DockerStreamType type, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = (byte)type;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 0;
        buffer[4] = (byte)((payload.Length >> 24) & 0xFF);
        buffer[5] = (byte)((payload.Length >> 16) & 0xFF);
        buffer[6] = (byte)((payload.Length >> 8) & 0xFF);
        buffer[7] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}

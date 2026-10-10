using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MiPushDesk.Core.Protocol;

public sealed class PushProtocolException(string message) : IOException(message);

public sealed class ProtoFields : Dictionary<int, List<object>>
{
    public ulong Number(int number, ulong missing = 0) => !TryGetValue(number, out var values) ? missing
        : values[^1] is ulong value ? value : throw new PushProtocolException("Protobuf number has the wrong type.");
    public byte[] Bytes(int number) => !TryGetValue(number, out var values) ? []
        : values[^1] is byte[] value ? value : throw new PushProtocolException("Protobuf string has the wrong type.");
    public string Text(int number) => Encoding.UTF8.GetString(Bytes(number));
}

public sealed record SlimBlob(ushort Type, ProtoFields Header, byte[] Payload)
{
    public string Command => Header.Text(5);
    public ulong Channel => Header.Number(1);
    public string Subcommand => Header.Text(6);
    public string PacketId => Header.Text(7);
}

public static class SlimProtocol
{
    public const int MaxFrameSize = 32768;
    public const int MaxPayloadSize = 1024 * 1024;
    public static byte[] Protobuf(params (int Number, object Value)[] fields)
    {
        using var output = new MemoryStream();
        foreach (var (number, value) in fields)
        {
            if (number is < 1 or >= 1 << 29) throw new PushProtocolException("Invalid protobuf field number.");
            if (value is byte[] || value is string)
            {
                var bytes = value is byte[] binary ? binary : Encoding.UTF8.GetBytes((string)value);
                Varint(output, (ulong)(number << 3 | 2));
                Varint(output, (ulong)bytes.Length);
                output.Write(bytes);
            }
            else
            {
                Varint(output, (ulong)number << 3);
                Varint(output, Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return output.ToArray();
    }
    public static ProtoFields ReadProtobuf(byte[] data)
    {
        var result = new ProtoFields();
        var cursor = 0;
        while (cursor < data.Length)
        {
            var tag = ReadVarint(data, ref cursor);
            var number = checked((int)(tag >> 3));
            if (number is < 1 or >= 1 << 29) throw new PushProtocolException("Invalid protobuf field number.");
            var wire = tag & 7;
            object value;
            if (wire == 0) value = ReadVarint(data, ref cursor);
            else
            {
                var size = wire switch
                {
                    1 => 8UL, 2 => ReadVarint(data, ref cursor), 5 => 4UL,
                    _ => throw new PushProtocolException("Unsupported protobuf wire type.")
                };
                if (size > (ulong)(data.Length - cursor)) throw new PushProtocolException("Truncated protobuf data.");
                value = data.AsSpan(cursor, (int)size).ToArray();
                cursor += (int)size;
            }
            if (!result.TryGetValue(number, out var values)) result[number] = values = [];
            values.Add(value);
        }
        return result;
    }
    private static void Varint(Stream output, ulong value)
    {
        while (value >= 128) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        output.WriteByte((byte)value);
    }
    private static ulong ReadVarint(byte[] data, ref int cursor)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 63; shift += 7)
        {
            if (cursor >= data.Length) throw new PushProtocolException("Truncated protobuf varint.");
            var current = data[cursor++];
            if (shift == 63 && current > 1) throw new PushProtocolException("Protobuf varint overflow.");
            value |= (ulong)(current & 127) << shift;
            if (current < 128) return value;
        }
        throw new PushProtocolException("Invalid protobuf varint.");
    }
    public static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (key.IsEmpty) throw new PushProtocolException("Empty RC4 key.");
        Span<byte> permutation = stackalloc byte[256];
        for (var index = 0; index < 256; index++) permutation[index] = (byte)index;
        var cursor = 0;
        for (var index = 0; index < 256; index++)
        {
            cursor = (cursor + permutation[index] + key[index % key.Length]) & 255;
            (permutation[index], permutation[cursor]) = (permutation[cursor], permutation[index]);
        }
        var position = 0;
        cursor = 0;
        var result = new byte[data.Length];
        for (var index = 0; index < data.Length; index++)
        {
            position = (position + 1) & 255;
            cursor = (cursor + permutation[position]) & 255;
            (permutation[position], permutation[cursor]) = (permutation[cursor], permutation[position]);
            result[index] = (byte)(data[index] ^ permutation[(permutation[position] + permutation[cursor]) & 255]);
        }
        CryptographicOperations.ZeroMemory(permutation);
        return result;
    }
    public static byte[] Blob(ushort type, byte[] header, byte[] payload)
    {
        var output = new byte[8 + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(output, type);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), checked((ushort)header.Length));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), checked((uint)payload.Length));
        header.CopyTo(output, 8);
        payload.CopyTo(output, 8 + header.Length);
        return output;
    }
    public static byte[] Command(string command, byte[]? payload = null, string? packetId = null, string? subcommand = null)
    {
        var fields = new List<(int, object)> { (1, 0), (5, command), (9, 0) };
        if (command == "CONN") fields.Add((3, "xiaomi.com"));
        if (packetId is not null) fields.Add((7, packetId));
        if (subcommand is not null) fields.Add((6, subcommand));
        return Blob(2, Protobuf(fields.ToArray()), payload ?? []);
    }
    public static byte[] Frame(byte[] body, byte[]? sessionKey = null)
    {
        if (body.Length > MaxFrameSize) throw new PushProtocolException("MiPush frame exceeds 32768 bytes.");
        if (sessionKey is not null) body = Rc4(sessionKey, body);
        var output = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(output, 0xC2FE);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), 5);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)body.Length);
        body.CopyTo(output, 8);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8 + body.Length), Adler32(output.AsSpan(0, 8 + body.Length)));
        return output;
    }
    public static SlimBlob ReadFrame(byte[] frame, byte[]? sessionKey = null)
    {
        if (frame.Length < 12) throw new PushProtocolException("Truncated MiPush frame.");
        var length = FrameLength(frame.AsSpan(0, 8));
        if (frame.Length != length + 12 || BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8 + length))
            != Adler32(frame.AsSpan(0, 8 + length))) throw new PushProtocolException("MiPush checksum or length mismatch.");
        if (length == 0) return new(2, ReadProtobuf(Protobuf((1, 0), (5, "PING"))), []);
        var body = frame.AsSpan(8, length).ToArray();
        if (sessionKey is not null) body = Rc4(sessionKey, body);
        if (body.Length < 8) throw new PushProtocolException("Truncated MiPush blob.");
        var type = BinaryPrimitives.ReadUInt16BigEndian(body);
        var headerSize = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2));
        var payloadSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4));
        if (type is < 1 or > 3 || (long)headerSize + payloadSize + 8 != body.Length)
            throw new PushProtocolException("Invalid MiPush blob lengths or type.");
        var fields = ReadProtobuf(body.AsSpan(8, headerSize).ToArray());
        var payload = body.AsSpan(8 + headerSize).ToArray();
        return new(type, fields, fields.Number(9) == 0 ? Unpack(payload) : payload);
    }
    public static async Task<SlimBlob> ReadFrameAsync(Stream stream, byte[]? sessionKey, TimeSpan frameTimeout, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(frameTimeout);
        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
            var length = FrameLength(header);
            var frame = new byte[length + 12];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(8), deadline.Token);
            return ReadFrame(frame, sessionKey);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("MiPush frame timed out."); }
    }
    private static int FrameLength(ReadOnlySpan<byte> header)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != 0xC2FE || BinaryPrimitives.ReadUInt16BigEndian(header[2..]) != 5)
            throw new PushProtocolException("Unexpected MiPush wire version.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        return length <= MaxFrameSize ? (int)length : throw new PushProtocolException("MiPush frame exceeds 32768 bytes.");
    }
    public static byte[] Unpack(byte[] payload)
    {
        if (!payload.AsSpan().StartsWith("PUSH"u8)) return payload;
        if (payload.Length < 15) throw new PushProtocolException("Truncated PUSH header.");
        var originalSize = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(7));
        var packedSize = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(11));
        if (packedSize != payload.Length - 15 || originalSize > MaxPayloadSize)
            throw new PushProtocolException("Invalid PUSH compression size.");
        if (payload[6] == 0)
        {
            if (originalSize != packedSize) throw new PushProtocolException("Invalid uncompressed PUSH size.");
            return payload[15..];
        }
        if (payload[6] != 2) throw new PushProtocolException("Unsupported PUSH compression algorithm.");
        using var input = new MemoryStream(payload, 15, payload.Length - 15, false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var output = new byte[originalSize];
        gzip.ReadExactly(output);
        if (gzip.ReadByte() != -1) throw new PushProtocolException("PUSH decompressed size mismatch.");
        return output;
    }
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint lower = 1, upper = 0;
        foreach (var value in data) { lower = (lower + value) % 65521; upper = (upper + lower) % 65521; }
        return upper << 16 | lower;
    }
}

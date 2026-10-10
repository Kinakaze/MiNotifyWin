using System.Buffers.Binary;
using System.Text;

namespace MiPushDesk.Core.Protocol;

public enum ThriftType : byte { Bool = 2, Byte = 3, Double = 4, I16 = 6, I32 = 8, I64 = 10, Binary = 11, Struct = 12, Map = 13, Set = 14, List = 15 }
public sealed record ThriftValue(ThriftType Type, object Value)
{
    public static ThriftValue Text(string value) => new(ThriftType.Binary, Encoding.UTF8.GetBytes(value));
    public static ThriftValue Binary(byte[] value) => new(ThriftType.Binary, value);
    public static ThriftValue Int32(int value) => new(ThriftType.I32, value);
    public static ThriftValue Int64(long value) => new(ThriftType.I64, value);
    public static ThriftValue Bool(bool value) => new(ThriftType.Bool, value);
    public static ThriftValue Struct(ThriftFields value) => new(ThriftType.Struct, value);
    public static ThriftValue Strings(IEnumerable<KeyValuePair<string, string>> values) => new(ThriftType.Map,
        new ThriftMap(ThriftType.Binary, ThriftType.Binary, values.Select(pair => (Text(pair.Key).Value, Text(pair.Value).Value)).ToList()));
}
public sealed record ThriftMap(ThriftType KeyType, ThriftType ValueType, List<(object Key, object Value)> Entries);
public sealed record ThriftList(ThriftType ItemType, List<object> Items);
public sealed class ThriftFields : SortedDictionary<short, ThriftValue>
{
    public object? Get(short number, ThriftType type)
    {
        if (!TryGetValue(number, out var field)) return null;
        return field.Type == type ? field.Value : throw new PushProtocolException($"Unexpected Thrift type for field {number}.");
    }
    public string Text(short number) => Encoding.UTF8.GetString((byte[]?)Get(number, ThriftType.Binary) ?? []);
    public byte[] Binary(short number) => (byte[]?)Get(number, ThriftType.Binary) ?? [];
    public int? Int32(short number) => (int?)Get(number, ThriftType.I32);
    public long? Int64(short number) => (long?)Get(number, ThriftType.I64);
    public bool? Bool(short number) => (bool?)Get(number, ThriftType.Bool);
    public ThriftFields Struct(short number) => (ThriftFields?)Get(number, ThriftType.Struct) ?? new();
    public Dictionary<string, string> Strings(short number)
    {
        if (Get(number, ThriftType.Map) is not ThriftMap map) return new(StringComparer.Ordinal);
        if (map.KeyType != ThriftType.Binary || map.ValueType != ThriftType.Binary)
            throw new PushProtocolException("Expected a string map.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map.Entries) result[Encoding.UTF8.GetString((byte[])key)] = Encoding.UTF8.GetString((byte[])value);
        return result;
    }
}

public static class ThriftProtocol
{
    private const int MaxItems = 4096;
    public static ThriftFields Decode(byte[] data)
    {
        if (data.Length > SlimProtocol.MaxPayloadSize) throw new PushProtocolException("Thrift payload exceeds 1 MiB.");
        var reader = new Reader(data);
        var result = (ThriftFields)reader.Read(ThriftType.Struct, 0);
        if (reader.Position != data.Length) throw new PushProtocolException("Trailing Thrift data.");
        return result;
    }
    public static byte[] Encode(ThriftFields fields)
    {
        using var output = new MemoryStream();
        Write(output, ThriftType.Struct, fields, 0);
        if (output.Length > SlimProtocol.MaxPayloadSize) throw new PushProtocolException("Thrift payload exceeds 1 MiB.");
        return output.ToArray();
    }
    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }
        private int _remaining = 20000;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - Position) throw new PushProtocolException("Invalid or truncated Thrift length.");
            var result = data.AsSpan(Position, count); Position += count; return result;
        }
        private int Count()
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(Take(4));
            return count is >= 0 and <= MaxItems ? count : throw new PushProtocolException("Thrift collection is too large.");
        }
        private ThriftType Type()
        {
            var type = (ThriftType)Take(1)[0];
            return Enum.IsDefined(type) ? type : throw new PushProtocolException("Unknown Thrift type.");
        }
        public object Read(ThriftType type, int depth)
        {
            if (depth > 24 || --_remaining < 0) throw new PushProtocolException("Thrift nesting or value limit exceeded.");
            switch (type)
            {
                case ThriftType.Bool:
                    return Take(1)[0] switch { 0 => false, 1 => true, _ => throw new PushProtocolException("Invalid Thrift boolean.") };
                case ThriftType.Byte: return unchecked((sbyte)Take(1)[0]);
                case ThriftType.Double: return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Take(8)));
                case ThriftType.I16: return BinaryPrimitives.ReadInt16BigEndian(Take(2));
                case ThriftType.I32: return BinaryPrimitives.ReadInt32BigEndian(Take(4));
                case ThriftType.I64: return BinaryPrimitives.ReadInt64BigEndian(Take(8));
                case ThriftType.Binary: return Take(BinaryPrimitives.ReadInt32BigEndian(Take(4))).ToArray();
                case ThriftType.Struct:
                    var fields = new ThriftFields();
                    while (true)
                    {
                        var fieldType = (ThriftType)Take(1)[0];
                        if (fieldType == 0) return fields;
                        var number = BinaryPrimitives.ReadInt16BigEndian(Take(2));
                        if (fields.Count >= 512 || fields.ContainsKey(number)) throw new PushProtocolException("Too many or duplicate Thrift fields.");
                        fields.Add(number, new(fieldType, Read(fieldType, depth + 1)));
                    }
                case ThriftType.Map:
                    var keyType = Type(); var valueType = Type(); var mapCount = Count();
                    var entries = new List<(object, object)>(mapCount);
                    for (var index = 0; index < mapCount; index++) entries.Add((Read(keyType, depth + 1), Read(valueType, depth + 1)));
                    return new ThriftMap(keyType, valueType, entries);
                case ThriftType.List:
                case ThriftType.Set:
                    var itemType = Type(); var listCount = Count(); var items = new List<object>(listCount);
                    for (var index = 0; index < listCount; index++) items.Add(Read(itemType, depth + 1));
                    return new ThriftList(itemType, items);
                default: throw new PushProtocolException("Unknown Thrift type.");
            }
        }
    }
    private static void Write(Stream output, ThriftType type, object value, int depth)
    {
        if (depth > 24) throw new PushProtocolException("Thrift nesting limit exceeded.");
        Span<byte> number = stackalloc byte[8];
        switch (type)
        {
            case ThriftType.Bool: output.WriteByte((bool)value ? (byte)1 : (byte)0); return;
            case ThriftType.Byte: output.WriteByte(unchecked((byte)(sbyte)value)); return;
            case ThriftType.Double: BinaryPrimitives.WriteInt64BigEndian(number, BitConverter.DoubleToInt64Bits((double)value)); output.Write(number); return;
            case ThriftType.I16: BinaryPrimitives.WriteInt16BigEndian(number, (short)value); output.Write(number[..2]); return;
            case ThriftType.I32: BinaryPrimitives.WriteInt32BigEndian(number, (int)value); output.Write(number[..4]); return;
            case ThriftType.I64: BinaryPrimitives.WriteInt64BigEndian(number, (long)value); output.Write(number); return;
            case ThriftType.Binary:
                var bytes = (byte[])value;
                BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); output.Write(number[..4]); output.Write(bytes); return;
            case ThriftType.Struct:
                foreach (var (fieldId, field) in (ThriftFields)value)
                {
                    output.WriteByte((byte)field.Type); BinaryPrimitives.WriteInt16BigEndian(number, fieldId); output.Write(number[..2]);
                    Write(output, field.Type, field.Value, depth + 1);
                }
                output.WriteByte(0); return;
            case ThriftType.Map:
                var map = (ThriftMap)value;
                output.WriteByte((byte)map.KeyType); output.WriteByte((byte)map.ValueType);
                BinaryPrimitives.WriteInt32BigEndian(number, map.Entries.Count); output.Write(number[..4]);
                foreach (var entry in map.Entries) { Write(output, map.KeyType, entry.Key, depth + 1); Write(output, map.ValueType, entry.Value, depth + 1); }
                return;
            case ThriftType.Set:
            case ThriftType.List:
                var list = (ThriftList)value;
                output.WriteByte((byte)list.ItemType); BinaryPrimitives.WriteInt32BigEndian(number, list.Items.Count); output.Write(number[..4]);
                foreach (var item in list.Items) Write(output, list.ItemType, item, depth + 1);
                return;
            default: throw new PushProtocolException("Unknown Thrift type.");
        }
    }
}

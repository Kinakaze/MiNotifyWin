using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MiPushDesk.Core;

public sealed class NotificationFeed(string file)
{
    private readonly string _clearFile = file + ".cleared";
    private readonly string _deletedFile = file + ".deleted";
    private readonly object _deletionGate = new();
    private HashSet<string> _deleted = File.Exists(file + ".deleted")
        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file + ".deleted"))! : new(StringComparer.Ordinal);
    private long _clearedThrough = File.Exists(file + ".cleared")
        ? long.Parse(File.ReadAllText(file + ".cleared"), CultureInfo.InvariantCulture) : long.MinValue;
    private long _position;
    private readonly List<byte> _pending = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    public int InvalidLines { get; private set; }
    public bool IsDeleted(string key) { lock (_deletionGate) return _deleted.Contains(key); }
    public bool Includes(PushRecord record)
    {
        lock (_deletionGate) return record.ReceivedAt.UtcTicks > Volatile.Read(ref _clearedThrough) && !_deleted.Contains(record.Key);
    }
    public void Delete(string key)
    {
        lock (_deletionGate)
        {
            var deleted = new HashSet<string>(_deleted, StringComparer.Ordinal) { key };
            AtomicFile.Write(_deletedFile, JsonSerializer.Serialize(deleted));
            _deleted = deleted;
        }
    }
    public void Restore(IEnumerable<string> keys)
    {
        lock (_deletionGate)
        {
            var deleted = new HashSet<string>(_deleted, StringComparer.Ordinal);
            deleted.ExceptWith(keys);
            if (deleted.Count == _deleted.Count) return;
            AtomicFile.Write(_deletedFile, JsonSerializer.Serialize(deleted));
            _deleted = deleted;
        }
    }
    public void Clear()
    {
        var cutoff = DateTimeOffset.UtcNow.UtcTicks;
        AtomicFile.Write(_clearFile, cutoff.ToString(CultureInfo.InvariantCulture));
        Volatile.Write(ref _clearedThrough, cutoff);
        lock (_deletionGate) { File.Delete(_deletedFile); _deleted.Clear(); }
    }
    public IReadOnlyList<PushRecord> Read(bool initial = false)
    {
        var records = new List<PushRecord>();
        if (!File.Exists(file)) return records;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position) { _position = 0; _pending.Clear(); }
        if (initial && _position == 0 && stream.Length > 8 * 1024 * 1024)
        {
            stream.Position = stream.Length - 8 * 1024 * 1024;
            while (stream.ReadByte() is var current && current != -1 && current != 10) { }
            _position = stream.Position;
        }
        stream.Position = _position;
        var buffer = new byte[65536];
        var remaining = Math.Min(stream.Length - _position, 8 * 1024 * 1024);
        while (remaining > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) break;
            _position += count;
            remaining -= count;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == 10)
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<PushRecord>(Encoding.UTF8.GetString(_pending.ToArray()), JsonData.Options);
                        if (record is { Duplicate: false } && (record.IsNotification || record.Operation == "clear") && !string.IsNullOrEmpty(record.Key)
                            && !string.IsNullOrEmpty(record.Package) && record.Title is not null && record.Description is not null
                            && record.Extra is not null && Includes(record) && _seen.Add(record.Key + "\0" + record.BodyStatus))
                        {
                            _order.Enqueue(record.Key + "\0" + record.BodyStatus);
                            if (_order.Count > 10000) _seen.Remove(_order.Dequeue());
                            records.Add(record);
                        }
                    }
                    catch (JsonException) { InvalidLines++; }
                    _pending.Clear();
                }
                else
                {
                    _pending.Add(buffer[index]);
                    if (_pending.Count > 4 * 1024 * 1024) throw new InvalidDataException("通知记录超出大小限制。");
                }
            }
        }
        return records;
    }
}

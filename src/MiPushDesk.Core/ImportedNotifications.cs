namespace MiPushDesk.Core;

public sealed class ImportedNotifications(AppPaths paths)
{
    private readonly string _file = Path.Combine(paths.Data, "imported-notifications.json");

    public List<PushRecord> Read() => File.Exists(_file)
        ? ExchangeDocument.Parse(File.ReadAllText(_file)).Notifications! : [];

    public List<PushRecord> Merge(IEnumerable<PushRecord> records)
    {
        var merged = Read().Concat(records).DistinctBy(record => record.Key)
            .OrderByDescending(record => record.ReceivedAt).Take(10000).ToList();
        AtomicFile.Write(_file, new ExchangeDocument { Notifications = merged }.ToJson());
        return merged;
    }

    public void Clear() => File.Delete(_file);
    public void Delete(string key)
    {
        if (!File.Exists(_file)) return;
        var records = Read();
        if (records.RemoveAll(record => record.Key == key) > 0)
            AtomicFile.Write(_file, new ExchangeDocument { Notifications = records }.ToJson());
    }
}

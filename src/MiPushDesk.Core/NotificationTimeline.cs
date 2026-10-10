namespace MiPushDesk.Core;

public static class NotificationTimeline
{
    public static string Slot(PushRecord record) => record.Package + "\0" + (record.NotifyId is { } notifyId
        ? "notify:" + notifyId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : record.CollapseKey.Length > 0 ? "collapse:" + record.CollapseKey : "message:" + record.Key);
    public static bool MatchesClear(PushRecord record, PushRecord command)
    {
        if (record.Package != command.Package) return false;
        var selector = command.ControlExtra;
        if (selector.TryGetValue("notifyId", out var value) && int.TryParse(value, out var notifyId) && notifyId >= -1)
            return notifyId == -1 || record.NotifyId == notifyId;
        if (selector.GetValueOrDefault("msgId", "") is { Length: > 0 } messageId) return record.MessageId == messageId;
        var title = selector.GetValueOrDefault("title", "");
        var description = selector.GetValueOrDefault("description", "");
        return (title.Length > 0 || description.Length > 0) && record.Title.Contains(title, StringComparison.Ordinal)
            && record.Description.Contains(description, StringComparison.Ordinal);
    }
    public static IReadOnlyList<PushRecord> Apply(List<PushRecord> records, PushRecord incoming, DateTimeOffset now)
    {
        if (incoming.Duplicate) return [];
        if (incoming.Operation == "clear")
        {
            var cleared = records.Where(record => MatchesClear(record, incoming)).ToArray();
            records.RemoveAll(record => MatchesClear(record, incoming));
            return cleared;
        }
        if (!incoming.IsNotification || incoming.ExpiresAt <= now) return [];
        if (incoming.Replayed && !records.Any(record => record.Key == incoming.Key)) return [];
        var replaced = records.Where(record => record.Key == incoming.Key || Slot(record) == Slot(incoming)).ToArray();
        if (incoming.Replayed && replaced.Any(record => record.Key != incoming.Key && record.ReceivedAt > incoming.ReceivedAt)) return [];
        records.RemoveAll(record => replaced.Contains(record));
        var position = records.FindIndex(record => record.ReceivedAt < incoming.ReceivedAt);
        records.Insert(position < 0 ? records.Count : position, incoming);
        if (records.Count > 2000) records.RemoveRange(2000, records.Count - 2000);
        return replaced;
    }
    public static IReadOnlyList<PushRecord> Expire(List<PushRecord> records, DateTimeOffset now)
    {
        var expired = records.Where(record => record.ExpiresAt <= now).ToArray();
        records.RemoveAll(record => record.ExpiresAt <= now);
        return expired;
    }
}

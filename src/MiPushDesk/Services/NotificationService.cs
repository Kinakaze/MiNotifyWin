using System.Threading.Channels;
using Microsoft.Win32;
using MiPushDesk.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MiPushDesk.Services;

public sealed class NotificationService : IDisposable
{
    public static string AppId => Environment.GetEnvironmentVariable("MIPUSHDESK_RENDER_DIR") is { Length: > 0 }
        ? "MiPushDesk.Preview" : "MiPushDesk.Desktop";
    private readonly AppPaths _paths;
    private readonly ImageService _images;
    private readonly Func<DeskSettings> _settings;
    private readonly Action<string, string, bool, string> _balloon;
    private readonly Action<string> _open;
    private readonly Action<string>? _captureToast;
    private readonly Channel<(PushRecord Record, int Generation)> _queue = Channel.CreateBounded<(PushRecord, int)>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ToastNotification> _active = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _generation;
    private readonly ToastNotifier _notifier;
    public NotificationService(AppPaths paths, ImageService images, Func<DeskSettings> settings,
                               Action<string, string, bool, string> balloon, Action<string> open, Action<string>? captureToast = null)
    {
        _paths = paths; _images = images; _settings = settings; _balloon = balloon; _open = open;
        _captureToast = captureToast;
        using var registration = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        registration.SetValue("DisplayName", "MiPush Desk");
        registration.DeleteValue("IconUri", false);
        _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        _ = Task.Run(DispatchAsync);
    }
    public bool Enqueue(PushRecord record) => _queue.Writer.TryWrite((record, Volatile.Read(ref _generation)));
    public Task ShowTestAsync(PushRecord record)
    {
        var settings = _settings();
        return ShowAsync(record, settings, settings.Appearance, true, Volatile.Read(ref _generation));
    }
    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var (record, generation) in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    var settings = _settings();
                    var profile = settings.Appearance;
                    if (!settings.ShouldNotify(record.Package, DateTimeOffset.Now) || profile.ToastMode == "off") continue;
                    await ShowAsync(record, settings, profile, false, generation);
                }
                catch (Exception error) { Log("failed", record.Key, $"{error.GetType().Name} 0x{error.HResult:X8}"); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ShowAsync(PushRecord record, DeskSettings settings, DisplayProfile profile, bool test, int generation)
    {
        var name = _images.AppName(record.Package, settings);
        if (!CanShow() || profile.ToastMode == "off") return;
        if (profile.ToastMode == "tray")
        {
            _balloon(name + " · " + record.Title, RichNotification.Description(record, profile), profile.PlaySound, record.Key);
            Log("submitted", record.Key, null);
            return;
        }
        var icon = profile.UseAppIcons ? _images.AppIcon(record.Package, settings) : null;
        string? picture = null, avatar = null;
        if (profile.ShowImages && profile.ToastStyle == "rich")
        {
            try
            {
                picture = await _images.NotificationImageAsync(record, profile);
                avatar = await _images.FetchAsync(RichNotification.AvatarUrl(record));
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
            { Log("image_failed", record.Key, error.GetType().Name); }
        }
        if (!CanShow()) return;
        var content = RichNotification.BuildToast(record, profile, name, icon, picture, avatarPath: avatar);
        if (_captureToast is not null) { _captureToast(content); return; }
        var xml = new XmlDocument(); xml.LoadXml(content);
        var toast = new ToastNotification(xml) { Tag = RichNotification.Tag(record), Group = RichNotification.Group(record),
            ExpirationTime = record.ExpiresAt, SuppressPopup = record.Replayed || record.ExtraValue("notification_is_summary") == "true" };
        toast.Activated += (_, _) => _open(record.Key);
        toast.Failed += (_, args) => Log("failed", record.Key, $"0x{args.ErrorCode.HResult:X8}");
        lock (_gate)
        {
            if (!CanShow()) return;
            _active[record.Key] = toast;
            while (_active.Count > 128) _active.Remove(_active.Keys.First());
            _notifier.Show(toast);
        }
        Log("submitted", record.Key, null);

        bool CanShow()
        {
            lock (_gate) return !_stop.IsCancellationRequested && generation == Volatile.Read(ref _generation) && !_removed.Contains(record.Key)
                && (record.ExpiresAt is null || record.ExpiresAt > DateTimeOffset.UtcNow)
                && (test || _settings().ShouldNotify(record.Package, DateTimeOffset.Now));
        }
    }
    private void Log(string result, string key, string? error)
    {
        File.AppendAllText(Path.Combine(_paths.Data, "notification-events.jsonl"), System.Text.Json.JsonSerializer.Serialize(new { time = DateTimeOffset.Now, result, key, error }) + Environment.NewLine);
    }
    public void Clear()
    {
        while (_queue.Reader.TryRead(out _)) { }
        lock (_gate)
        {
            _generation++;
            foreach (var toast in _active.Values) _notifier.Hide(toast);
            _active.Clear();
            _removed.Clear();
        }
        ToastNotificationManager.History.Clear(AppId);
    }
    public void Remove(PushRecord record)
    {
        lock (_gate)
        {
            _removed.Add(record.Key);
            if (_removed.Count > 10000) _removed.Remove(_removed.First());
            if (_active.Remove(record.Key, out var toast)) _notifier.Hide(toast);
        }
        if (_captureToast is null) ToastNotificationManager.History.Remove(RichNotification.Tag(record), RichNotification.Group(record), AppId);
    }
    public void Dispose()
    {
        _stop.Cancel();
        _queue.Writer.TryComplete();
        Clear();
    }
}

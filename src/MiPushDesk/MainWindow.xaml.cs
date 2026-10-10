using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiPushDesk.Core;
using MiPushDesk.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace MiPushDesk;

public sealed partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly AppStore _store;
    private readonly ImportService _imports;
    private readonly BackendService _backend;
    private readonly ImageService _images;
    private readonly NotificationService _notifications;
    private readonly NotificationFeed _feed;
    private readonly ImportedNotifications _importedNotifications;
    private readonly StartupService _startup;
    private readonly TrayIcon _tray;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly List<PushRecord> _records = [];
    private readonly HashSet<string> _read;
    private readonly Dictionary<string, Button> _navigation;
    private DeskSettings _settings;
    private string _page = "inbox";
    private string _query = "";
    private string _packageFilter = "";
    private bool _polling;
    private bool _busy;
    private bool _exiting;
    private bool _closing;
    private readonly bool _renderMode = Environment.GetEnvironmentVariable("MIPUSHDESK_RENDER_DIR") is { Length: > 0 };
    private int _retryTicks;

    public MainWindow(AppPaths paths)
    {
        _paths = paths;
        _store = new(paths);
        _imports = new(paths);
        _settings = _store.Read();
        _store.Save(_settings);
        _read = LoadRead();
        _backend = new(paths, _store);
        _images = new(paths);
        _feed = new(Path.Combine(paths.Listener, "notifications.jsonl"));
        _importedNotifications = new(paths);
        _startup = new(Environment.ProcessPath!);
        InitializeComponent();
        Title = _renderMode ? "MiPush Desk · 界面检查" : "MiPush Desk";
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "MiPushDesk.ico"));
        AppWindow.Resize(new SizeInt32(1140, 800));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }
        _navigation = new() { ["inbox"] = InboxNav, ["apps"] = AppsNav, ["appearance"] = AppearanceNav, ["settings"] = SettingsNav };
        _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this), TrayAction,
            () => (!_settings.NotificationsEnabled, _startup.IsEnabled));
        if (!_renderMode) _tray.Show();
        _notifications = new(paths, _images, _store.Read,
            (title, text, sound, key) =>
            {
                if (_renderMode) AtomicFile.Write(Path.Combine(_paths.Data, "test-balloon.json"), JsonSerializer.Serialize(new { title, text, sound, key }));
                else DispatcherQueue.TryEnqueue(() => _tray.Balloon(title, text, sound, key));
            },
            key => DispatcherQueue.TryEnqueue(() => OpenFromToast(key)),
            _renderMode ? xml => AtomicFile.Write(Path.Combine(_paths.Data, "test-toast.xml"), xml) : null);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await PollAsync();
        AppWindow.Closing += (_, arguments) =>
        {
            if (_exiting) return;
            arguments.Cancel = true;
            if (_settings.CloseToTray) AppWindow.Hide(); else Run(ExitAsync);
        };
        Root.Loaded += async (_, _) =>
        {
            try
            {
                ApplyTheme();
                var imported = _importedNotifications.Read().Where(record => !_feed.IsDeleted(record.Key)).ToList();
                foreach (var record in imported) _read.Add(record.Key);
                foreach (var record in _feed.Read(true).Concat(imported).OrderBy(record => record.ReceivedAt))
                    NotificationTimeline.Apply(_records, record, DateTimeOffset.UtcNow);
                var arguments = Environment.GetCommandLineArgs();
                var accountIndex = Array.IndexOf(arguments, "--import-account");
                if (accountIndex >= 0 && accountIndex + 1 < arguments.Length && !_store.HasAccount) _store.ImportAccount(arguments[accountIndex + 1]);
                if (arguments.Contains("--silent")) { _settings.NotificationsEnabled = false; _store.Save(_settings); }
                Navigate("inbox");
                UpdateStatus();
                WarmAppMetadata(AppCatalog.Packages(_settings).Concat(_records.Select(record => record.Package)));
                if (_renderMode)
                {
                    await RenderChecksAsync(Environment.GetEnvironmentVariable("MIPUSHDESK_RENDER_DIR")!);
                    return;
                }
                if (_store.HasAccount && _settings.AutoConnect) await _backend.StartAsync(_settings.HeartbeatSeconds, _settings.ReconnectSeconds);
                _timer.Start();
            }
            catch (Exception error) { ReportError(error); }
        };
        Closed += (_, _) => { _accountImportStop?.Cancel(); _timer.Stop(); _notifications.Dispose(); _tray.Dispose(); _images.Dispose(); };
    }
    private HashSet<string> LoadRead()
    {
        var path = Path.Combine(_paths.Data, "read.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path))! : [];
    }
    private void SaveRead() => AtomicFile.Write(Path.Combine(_paths.Data, "read.json"), JsonSerializer.Serialize(_read.TakeLast(10000)));
    private void MarkRead(PushRecord record)
    {
        if (_read.Add(record.Key)) SaveRead();
        UpdateStatus();
    }
    private async Task PollAsync()
    {
        if (_polling || _closing) return;
        _polling = true;
        try
        {
            var incoming = await Task.Run(() => _feed.Read());
            var expired = NotificationTimeline.Expire(_records, DateTimeOffset.UtcNow);
            foreach (var record in expired) _notifications.Remove(record);
            if (incoming.Count > 0) WarmAppMetadata(incoming.Select(record => record.Package));
            foreach (var record in incoming.Where(_feed.Includes))
            {
                var removed = NotificationTimeline.Apply(_records, record, DateTimeOffset.UtcNow);
                foreach (var previous in removed)
                    if (!record.Replayed || previous.Key != record.Key) _notifications.Remove(previous);
                if (record.IsNotification && !record.Replayed && _records.Contains(record) && !_read.Contains(record.Key)
                    && !_notifications.Enqueue(record)) Notify("消息已保存；弹窗队列已满。");
            }
            UpdateStatus();
            if ((incoming.Count > 0 || expired.Count > 0) && _page == "inbox") RefreshInbox();
            if (_backend.IsRunning) _retryTicks = 0;
            if (!_accountWorking && _settings.AutoConnect && _store.HasAccount && !_backend.IsRunning && ++_retryTicks >= _settings.ReconnectSeconds)
            {
                _retryTicks = 0;
                await _backend.StartAsync(_settings.HeartbeatSeconds, _settings.ReconnectSeconds);
            }
        }
        catch (Exception error) { ReportError(error); }
        finally { _polling = false; }
    }
    private void UpdateStatus()
    {
        var state = _backend.ReadState();
        var connection = state.Bound ? "已连接" : state.State switch
        {
            "connecting" or "binding" or "starting" => "连接中", "reconnecting" => "重连中", _ => "已停止接收"
        };
        var unread = _records.Count(record => !_read.Contains(record.Key));
        if (_page == "settings")
        {
            if (_connectionDetails is not null) _connectionDetails.Text = ConnectionSummary();
            UpdateAppSecretStatus(state);
        }
        _tray.Update($"MiPush Desk · {connection} · {unread} 条未读");
    }
    private void ApplyTheme()
    {
        Ui.ApplyPalette(Root, _settings.Appearance);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Ui.Brush("DeskText").Color;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Ui.Brush("DeskMuted").Color;
    }
    private void SaveSettings(bool redraw = false)
    {
        _store.Save(_settings);
        ApplyTheme();
        if (redraw) Navigate(_page);
    }
    private void Notify(string text, bool error = false)
    {
        Notice.Message = text; Notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; Notice.IsOpen = true;
    }
    private void ReportError(Exception error)
    {
        AppErrors.Record(_paths.Data, error);
        Notify(AppErrors.Describe(error), true);
    }
    private async void Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception error) { ReportError(error); }
        finally { _busy = false; }
    }
    private void Navigate_Click(object sender, RoutedEventArgs arguments) => Navigate((string)((Button)sender).Tag);
    private void Navigate(string page)
    {
        var offset = page == _page && PageHost.Content is ScrollViewer previous ? previous.VerticalOffset : 0;
        _page = page;
        _paletteSwatches.Clear();
        foreach (var (name, button) in _navigation)
        {
            button.Background = name == page ? Ui.Brush("DeskAccentSoft") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var content = (StackPanel)button.Content;
            ((FontIcon)content.Children[0]).Foreground = Ui.Brush(name == page ? "DeskAccent" : "DeskMuted");
            ((TextBlock)content.Children[1]).Foreground = Ui.Brush(name == page ? "DeskAccent" : "DeskMuted");
        }
        PageHost.Content = page switch { "inbox" => BuildInbox(), "apps" => BuildApps(), "appearance" => BuildAppearance(), _ => BuildSettings() };
        if (offset > 0 && PageHost.Content is ScrollViewer current) current.Loaded += (_, _) => current.ChangeView(null, offset, null, true);
    }
    private void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate();
        TrayIcon.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
    private void OpenFromToast(string key)
    {
        if (!_renderMode) ShowWindow();
        Navigate("inbox");
        if ((_testNotifications.GetValueOrDefault(key) ?? _records.FirstOrDefault(record => record.Key == key)) is { } record) Run(() => ShowDetailsAsync(record));
    }
    private void TrayAction(string action)
    {
        if (action.StartsWith("message:", StringComparison.Ordinal)) OpenFromToast(action[8..]);
        if (action == "show") ShowWindow();
        if (action == "pause") { _settings.NotificationsEnabled = !_settings.NotificationsEnabled; SaveSettings(); if (!_settings.NotificationsEnabled) _notifications.Clear(); Navigate(_page); }
        if (action == "startup") Run(() => { _startup.SetEnabled(!_startup.IsEnabled); if (_page == "settings") Navigate("settings"); return Task.CompletedTask; });
        if (action == "exit") Run(ExitAsync);
    }
    private async Task ExitAsync()
    {
        if (_closing) return;
        _closing = true;
        _accountImportStop?.Cancel();
        _timer.Stop();
        _notifications.Clear();
        try { await _backend.StopAsync(); }
        finally { _exiting = true; Close(); Application.Current.Exit(); }
    }
    private Task<string?> PickAsync(params string[] types)
    {
        using var picker = NativeFileDialog.Open(types);
        return Task.FromResult(picker.Show(WinRT.Interop.WindowNative.GetWindowHandle(this)));
    }
    private Task<string?> SavePathAsync(string name, string extension)
    {
        using var picker = NativeFileDialog.Save(name, extension);
        return Task.FromResult(picker.Show(WinRT.Interop.WindowNative.GetWindowHandle(this)));
    }
    private async Task QuickImportAsync()
    {
        if (await PickAsync(".json") is { } file) await ImportFileAsync(file);
    }
    private void Root_DragOver(object sender, DragEventArgs arguments)
    {
        if (arguments.DataView.Contains(StandardDataFormats.StorageItems)) { arguments.AcceptedOperation = DataPackageOperation.Copy; arguments.DragUIOverride.Caption = "打开 JSON"; }
    }
    private async void Root_Drop(object sender, DragEventArgs arguments)
    {
        try
        {
            if (!arguments.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var files = await arguments.DataView.GetStorageItemsAsync();
            foreach (var file in files.Take(4)) await ImportFileAsync(file.Path);
        }
        catch (Exception error) { ReportError(error); }
    }
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
    private static void CopyText(string text)
    {
        var data = new DataPackage(); data.SetText(text);
        Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
    }
}

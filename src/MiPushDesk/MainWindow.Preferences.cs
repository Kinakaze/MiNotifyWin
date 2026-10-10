using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MiPushDesk.Core;
using MiPushDesk.Services;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private TextBlock? _appSecretDetails;
    private Button? _appSecretRefresh;
    private int _savedAppSecretCount;
    private TextBlock? _connectionDetails;
    private UIElement BuildAppearance()
    {
        var heading = Ui.Heading("外观", Ui.Row(7,
            Ui.Button("导入", () => Run(QuickImportAsync), "DeskQuiet", "\uE8B5"),
            Ui.Button("导出", () => Run(ExportSettingsAsync), "DeskButton", "\uE74E", "ExportSettings")));
        var profile = _settings.Appearance;
        var layout = Choice(new[] { ("cards", "卡片"), ("compact", "紧凑"), ("conversation", "对话") }, profile.Layout,
            value => { profile.Layout = value; SaveSettings(true); }, "LayoutChoice");
        var mode = Choice(new[] { ("native", "Windows 通知"), ("tray", "托盘气泡"), ("off", "仅通知中心") }, profile.ToastMode,
            value => { profile.ToastMode = value; SaveSettings(); }, "ToastMode");
        var style = Choice(new[] { ("rich", "图文与按钮"), ("standard", "标题与摘要"), ("compact", "精简摘要") }, profile.ToastStyle,
            value => { profile.ToastStyle = value; SaveSettings(); }, "ToastStyle");
        var selectors = Ui.Columns(Ui.Star(), Ui.Star()); selectors.ColumnSpacing = 14;
        Ui.Add(selectors, Ui.Stack(8, Ui.Text("桌面提醒", 12, "DeskMuted"), mode));
        Ui.Add(selectors, Ui.Stack(8, Ui.Text("提醒内容", 12, "DeskMuted"), style), 1);
        var metrics = Ui.Columns(Ui.Star(), Ui.Star()); metrics.ColumnSpacing = 18;
        var font = new Slider { Minimum = 12, Maximum = 20, StepFrequency = 1, Value = profile.FontSize, Header = "界面字号" };
        var radius = new Slider { Minimum = 0, Maximum = 24, StepFrequency = 1, Value = profile.CornerRadius, Header = "列表卡片圆角" };
        font.ValueChanged += (_, _) => { profile.FontSize = font.Value; SaveSettings(); };
        radius.ValueChanged += (_, _) => { profile.CornerRadius = radius.Value; SaveSettings(); };
        font.PointerCaptureLost += (_, _) => Navigate("appearance");
        radius.PointerCaptureLost += (_, _) => Navigate("appearance");
        Ui.Add(metrics, font); Ui.Add(metrics, radius, 1);
        var options = Ui.Stack(0,
            SettingRow("应用 Logo", null, Toggle(profile.UseAppIcons, value => { profile.UseAppIcons = value; SaveSettings(true); }, "UseAppIcons")),
            Ui.Rule(), SettingRow("通知配图", null, Toggle(profile.ShowImages, value => { profile.ShowImages = value; SaveSettings(true); }, "ShowImages")),
            Ui.Rule(), DefaultImageSetting(),
            Ui.Rule(), SettingRow("提示音", null, Toggle(profile.PlaySound, value => { profile.PlaySound = value; SaveSettings(); }, "PlaySound")));
        var notificationHeading = Ui.Columns(Ui.Star(), GridLength.Auto);
        Ui.Add(notificationHeading, Ui.Text("桌面通知", 16, bold: true));
        Ui.Add(notificationHeading, TestNotificationButton(), 1);
        var previewCard = NotificationCard(PreviewRecord(), true);
        AutomationProperties.SetAutomationId(previewCard, "NotificationListPreview");
        var listOptions = Ui.Stack(0,
            SettingRow("列表布局", null, layout), Ui.Rule(),
            SettingRow("通知原色", null, Toggle(profile.UseMessageColors, value => { profile.UseMessageColors = value; SaveSettings(true); }, "MessageColors")));
        return Ui.Scroll(Ui.Stack(22, heading,
            Ui.Stack(12, notificationHeading, Ui.Card(selectors), Ui.Card(options, 4)),
            Ui.Stack(12, Ui.Text("应用界面", 16, bold: true), BuildColorControls(), Ui.Card(listOptions, 4), Ui.Card(metrics)),
            Ui.Stack(12, Ui.Text("通知列表预览", 14, bold: true), previewCard)));
    }
    private UIElement DefaultImageSetting()
    {
        var profile = _settings.Appearance;
        var preview = new Image { Width = 64, Height = 36, Stretch = Stretch.UniformToFill };
        AutomationProperties.SetAutomationId(preview, "DefaultImagePreview");
        if (_images.DefaultImage(profile) is { } file) preview.Source = new BitmapImage(new Uri(file));
        var choose = Ui.Button("选择图片", () => Run(async () =>
        {
            if (await PickAsync(".png", ".jpg", ".jpeg") is not { } selected) return;
            profile.DefaultImage = _imports.ImportIcon(selected);
            profile.ShowImages = true;
            SaveSettings(true);
        }), id: "DefaultImageChoose");
        var clear = Ui.Button("清除", () => { profile.DefaultImage = null; SaveSettings(true); }, "DeskQuiet", id: "DefaultImageClear");
        clear.IsEnabled = profile.DefaultImage is not null;
        return SettingRow("默认配图", null, Ui.Row(8, preview, choose, clear));
    }
    private ComboBox Choice(IEnumerable<(string Id, string Label)> items, string selected, Action<string> changed, string? id = null)
    {
        var picker = new ComboBox();
        foreach (var (value, label) in items) picker.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selected);
        if (id is not null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, id);
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is ComboBoxItem item) changed((string)item.Tag); };
        return picker;
    }
    private static ToggleSwitch Toggle(bool enabled, Action<bool> changed, string? id = null)
    {
        var toggle = new ToggleSwitch { IsOn = enabled };
        if (id is not null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(toggle, id);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }
    private static Grid SettingRow(string title, string? description, UIElement control)
    {
        var grid = Ui.Columns(Ui.Star(), GridLength.Auto); grid.ColumnSpacing = 22; grid.Padding = new(16, 15, 16, 15);
        var label = Ui.Stack(6, Ui.Text(title, 13, bold: true));
        label.VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(description)) label.Children.Add(Ui.Wrap(description, 11));
        Ui.Add(grid, label);
        Ui.Add(grid, control, 1); return grid;
    }
    private Task ExportSettingsAsync() => ShowJsonAsync(_imports.ExportSettings(_settings).ToJson(), "设置");
    private UIElement BuildSettings()
    {
        var heading = Ui.Heading("设置");
        var connection = SettingRow("接收服务", null,
            Ui.Button(_backend.IsRunning ? "停止接收" : "开始接收", () => Run(async () =>
            {
                if (_backend.IsRunning) { _settings.AutoConnect = false; SaveSettings(); await _backend.StopAsync(); }
                else { if (!_store.HasAccount) { await ImportSessionAsync(); return; } _settings.AutoConnect = true; SaveSettings(); await _backend.StartAsync(_settings.HeartbeatSeconds, _settings.ReconnectSeconds); }
                Navigate("settings"); UpdateStatus();
            }), _backend.IsRunning ? "DeskButton" : "DeskPrimary", id: "ConnectionControl"));
        _connectionDetails = Ui.Wrap(ConnectionSummary(), 11);
        _connectionDetails.Margin = new(16, 0, 16, 14);
        AutomationProperties.SetAutomationId(_connectionDetails, "ConnectionStatus");
        _savedAppSecretCount = _store.ReadAppCredentials().Count;
        _appSecretRefresh = Ui.Button("更新", () =>
        {
            if (_backend.RequestAppSecretRecovery()) Notify("正在更新密钥");
            UpdateStatus();
        }, id: "RecoverAppSecrets");
        _appSecretDetails = Ui.Wrap("", 11);
        _appSecretDetails.Margin = new(16, 0, 16, 14);
        AutomationProperties.SetAutomationId(_appSecretDetails, "AppSecretRecoveryStatus");
        var appSecrets = SettingRow("应用密钥", null, _appSecretRefresh);
        UpdateAppSecretStatus(_backend.ReadState());
        var reconnect = SecondsInput(_settings.ReconnectSeconds, 86400, value =>
        { _settings.ReconnectSeconds = value; SaveSettings(); _backend.UpdateTiming(_settings.HeartbeatSeconds, value); }, "ReconnectSeconds");
        var heartbeat = SecondsInput(_settings.HeartbeatSeconds, 300, value =>
        { _settings.HeartbeatSeconds = value; SaveSettings(); _backend.UpdateTiming(value, _settings.ReconnectSeconds); }, "HeartbeatSeconds");
        var timing = Ui.Stack(0, SettingRow("重连间隔", null, Ui.Row(8, reconnect, Ui.Text("s", 12, "DeskMuted"))), Ui.Rule(),
            SettingRow("心跳间隔", null, Ui.Row(8, heartbeat, Ui.Text("s", 12, "DeskMuted"))));
        var behavior = Ui.Stack(0,
            SettingRow("开机启动", null, Toggle(_startup.IsEnabled, value =>
            {
                try { _startup.SetEnabled(value); Notify(value ? "已注册开机自启动。" : "已取消开机自启动。"); }
                catch (Exception error) { ReportError(error); }
            }, "AutoStart")), Ui.Rule(),
            SettingRow("关闭到托盘", null, Toggle(_settings.CloseToTray, value => { _settings.CloseToTray = value; SaveSettings(); }, "CloseToTray")), Ui.Rule(),
            SettingRow("自动连接", null, Toggle(_settings.AutoConnect, value => { _settings.AutoConnect = value; SaveSettings(); }, "AutoConnect")));
        var quietStart = Choice(Enumerable.Range(0, 24).Select(hour => (hour.ToString(), $"{hour:00}:00")), _settings.QuietStartHour.ToString(), value => { _settings.QuietStartHour = int.Parse(value); SaveSettings(); });
        var quietEnd = Choice(Enumerable.Range(0, 24).Select(hour => (hour.ToString(), $"{hour:00}:00")), _settings.QuietEndHour.ToString(), value => { _settings.QuietEndHour = int.Parse(value); SaveSettings(); });
        quietStart.Width = 90; quietEnd.Width = 90;
        var reminders = Ui.Stack(0,
            SettingRow("桌面弹窗", "暂停仍保存通知", Toggle(_settings.NotificationsEnabled, value =>
            {
                _settings.NotificationsEnabled = value; SaveSettings(); if (!value) _notifications.Clear();
            }, "NotificationsEnabled")), Ui.Rule(),
            SettingRow("定时免打扰", null, Toggle(_settings.QuietHoursEnabled, value => { _settings.QuietHoursEnabled = value; SaveSettings(); }, "QuietHours")),
            SettingRow("免打扰时段", "相同时为全天", Ui.Row(8, quietStart, Ui.Text("至", 12, "DeskMuted"), quietEnd)));
        var transfer = Ui.Stack(0, TransferRow("设置", "外观与应用", ImportSettingsAsync, ExportSettingsAsync, "ImportSettings", "SettingsMenu"), Ui.Rule(),
            TransferRow("会话", _store.HasAccount ? "账号已导入" : File.Exists(Path.Combine(_paths.Data, "analysis.json"))
                ? "已保存分析 · 登录信息待补全" : "未导入", ImportSessionAsync, ShowSessionAsync, "ImportSession", "SessionMenu"));
        var metadata = Ui.Stack(0,
            SettingRow("小米名称与图标", null, Toggle(_settings.UseXiaomiMetadata, value =>
            {
                _settings.UseXiaomiMetadata = value; SaveSettings();
                if (value) WarmAppMetadata(AppCatalog.Packages(_settings).Concat(_records.Select(record => record.Package)));
            }, "XiaomiMetadata")), Ui.Rule(),
            SettingRow("本地缓存", null, Ui.Row(8,
                Ui.Button("更新", () => WarmAppMetadata(AppCatalog.Packages(_settings).Concat(_records.Select(record => record.Package)), true), "DeskQuiet", id: "RefreshMetadata"),
                Ui.Button("清除", () => { _images.Metadata.Clear(); Notify("缓存已清除"); }, "DeskQuiet", id: "ClearMetadata"))));
        return Ui.Scroll(Ui.Stack(22, heading, Ui.Card(Ui.Stack(0, connection, _connectionDetails, Ui.Rule(),
                appSecrets, _appSecretDetails, Ui.Rule(), timing), 4),
            Ui.Stack(10, Ui.Text("启动与窗口", 14, bold: true), Ui.Card(behavior, 4)),
            Ui.Stack(10, Ui.Text("通知与免打扰", 14, bold: true), Ui.Card(reminders, 4)),
            Ui.Stack(10, Ui.Text("应用信息", 14, bold: true), Ui.Card(metadata, 4)),
            Ui.Stack(10, Ui.Text("数据", 14, bold: true), Ui.Card(transfer, 4))));
    }
    private Grid TransferRow(string title, string description, Func<Task> import, Func<Task> export, string importId, string menuId)
    {
        var more = IconButton("更多操作", "\uE712", () => { }, menuId);
        var menu = new MenuFlyout();
        var paste = new MenuFlyoutItem { Text = "粘贴 JSON", Icon = Ui.Icon("\uE77F") };
        paste.Click += (_, _) => Run(() => ShowJsonAsync(title: "导入" + title)); menu.Items.Add(paste);
        var view = new MenuFlyoutItem { Text = "查看与导出", Icon = Ui.Icon("\uE8A5") };
        view.Click += (_, _) => Run(export); menu.Items.Add(view);
        more.Flyout = menu;
        return SettingRow(title, description, Ui.Row(4, Ui.Button("导入" + title, () => Run(import), id: importId), more));
    }
    private static NumberBox SecondsInput(int value, int maximum, Action<int> changed, string id)
    {
        var input = new NumberBox { Value = value, Minimum = 5, Maximum = maximum, SmallChange = 1, LargeChange = 10,
            Width = 124, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0 } };
        AutomationProperties.SetAutomationId(input, id);
        input.ValueChanged += (_, _) => { if (double.IsFinite(input.Value)) changed((int)Math.Round(input.Value)); };
        return input;
    }
    private string ConnectionSummary()
    {
        var state = _backend.ReadState();
        if (state.Bound) return "已连接";
        if (state.State == "reconnecting")
        {
            var seconds = Math.Max(0, (int)Math.Ceiling(((state.RetryAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds));
            return (state.ErrorReason is { Length: > 0 } reason ? reason + " · " : "") + $"{seconds} s 后重连";
        }
        return state.State switch { "connecting" or "binding" or "starting" => "连接中", "failed" => "连接失败 · " + state.LastError, _ => "已停止" };
    }
    private void UpdateAppSecretStatus(ListenerState state)
    {
        if (_appSecretDetails is null || _appSecretRefresh is null) return;
        var count = state.Pid == 0 ? _savedAppSecretCount : state.AppCredentialsCount;
        var busy = state.AppSecretRecoveryState is "waiting" or "running";
        _appSecretRefresh.IsEnabled = state.Bound && !busy;
        _appSecretRefresh.Content = busy && state.Bound ? "更新中…" : "更新";
        var saved = $"已保存 {count} 个";
        _appSecretDetails.Text = state.AppSecretRecoveryState switch
        {
            "waiting" => saved + (state.Bound ? " · 等待更新" : " · 等待连接"),
            "running" => $"获取中 · {state.AppSecretsRecovered} 个 / {state.AppSecretRecoveryPages} 页",
            "complete" => saved + (state.AppSecretRecoveryError == "history_failed" ? " · 历史更新失败"
                : state.BodiesReprocessed > 0 ? $" · 更新历史 {state.BodiesReprocessed} 条" : ""),
            "failed" => saved + " · " + (state.AppSecretRecoveryError switch
            {
                "timeout" => "更新超时", "server_rejected" => "服务器拒绝",
                "save_failed" => "保存失败", _ => "更新失败"
            }),
            _ => saved
        };
    }
    private async void WarmAppMetadata(IEnumerable<string> packages, bool force = false)
    {
        if (!_settings.UseXiaomiMetadata || _renderMode) return;
        try
        {
            await Task.WhenAll(packages.Distinct().Select(package => _images.Metadata.EnsureAsync(package, force)));
            if (_closing) return;
            if (_page == "apps") RefreshApps();
            if (_page == "inbox") RefreshInbox();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { if (!_closing) ReportError(error); }
    }
}

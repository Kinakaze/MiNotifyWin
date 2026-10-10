using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MiPushDesk.Core;
using MiPushDesk.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private async Task RenderChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var captures = new List<object>();
        try
        {
            _settings.NotificationsEnabled = false;
            _settings.AutoConnect = false;
            _store.Save(_settings);
            _records.Clear();
            var packages = new[] { "com.tencent.mobileqq", "ctrip.android.view", "com.dragon.read", "com.xiaomi.vipaccount" };
            var titles = new[] { "设计讨论组", "你的周末行程已就绪", "今天，也读一点喜欢的文字", "小米社区 · 新消息" };
            var descriptions = new[] { "今天的界面已经整理好。列表更简洁，长消息可以展开查看，重要内容也不会漏掉。",
                "上海 → 杭州 · 周六 09:30 出发。出发前，我们会提醒你确认行程与天气。",
                "阅读清单已更新。留一点安静的时间，继续昨天没有读完的故事。",
                "你关注的话题有了新的回复，可以在通知中心查看摘要。" };
            for (var index = 0; index < 7; index++)
                _records.Add(new() { Key = "render-message-" + index, Package = packages[index % packages.Length], Title = titles[index % titles.Length],
                    Description = descriptions[index % descriptions.Length], ReceivedAt = DateTimeOffset.Now.AddMinutes(-index * 7), IsNotification = true,
                    EncryptedAction = true });
            _read.Add("render-message-3");
            UpdateStatus();
            if (_settings.Appearance.Theme != "paper" || _settings.Appearance.ShowImages)
                throw new InvalidOperationException("默认应为白色且无配图。");
            if (!_settings.UseXiaomiMetadata) throw new InvalidOperationException("小米名称与图标没有默认开启。");
            if (Descendants(Root).OfType<TextBlock>().Any(label => label.Text == "快捷导入"))
                throw new InvalidOperationException("侧栏仍包含快捷导入。");
            _settings.Appearance.Layout = "cards";
            ApplyTheme();
            foreach (var page in new[] { "inbox", "apps", "appearance", "settings" })
            {
                Navigate(page);
                await CaptureAsync(page);
                if (page == "settings" && Descendants(PageHost).OfType<TextBlock>().Any(label => label.Text == "配置管理"))
                    throw new InvalidOperationException("设置中仍包含配置管理。");
            }
            if (Find<NumberBox>("ReconnectSeconds").Value != 60) throw new InvalidOperationException("默认重连间隔不是 60 秒。");
            if (Find<Button>("RecoverAppSecrets").IsEnabled
                || Find<TextBlock>("AppSecretRecoveryStatus").Text != "已保存 0 个")
                throw new InvalidOperationException("应用密钥入口没有正确显示未连接状态。");
            UpdateAppSecretStatus(new() { Pid = 1, Bound = true, AppSecretRecoveryState = "running", AppSecretRecoveryPages = 1, AppSecretsRecovered = 90 });
            if (Find<Button>("RecoverAppSecrets").IsEnabled || !Find<TextBlock>("AppSecretRecoveryStatus").Text.Contains("90"))
                throw new InvalidOperationException("应用密钥进度未显示或允许重复获取。");
            await CaptureAsync("settings-recovery-running");
            UpdateAppSecretStatus(new() { Pid = 1, Bound = true, AppCredentialsCount = 123, AppSecretRecoveryState = "complete", BodiesReprocessed = 106 });
            if (!Find<Button>("RecoverAppSecrets").IsEnabled || !Find<TextBlock>("AppSecretRecoveryStatus").Text.Contains("106"))
                throw new InvalidOperationException("应用密钥完成状态未显示或无法再次更新。");
            await CaptureAsync("settings-recovery-complete");
            UpdateAppSecretStatus(new());
            Find<NumberBox>("ReconnectSeconds").Value = 45;
            Find<NumberBox>("HeartbeatSeconds").Value = 40;
            if (_store.Read().ReconnectSeconds != 45 || _store.Read().HeartbeatSeconds != 40) throw new InvalidOperationException("连接间隔未保存。");
            Find<NumberBox>("ReconnectSeconds").Value = 60;
            Find<NumberBox>("HeartbeatSeconds").Value = 30;
            if (PageHost.Content is ScrollViewer settingsScroll) settingsScroll.ChangeView(null, 10000, null, true);
            if (Descendants(PageHost).OfType<TextBlock>().Any(label => label.Text is "JSON" or "抓包分析" or "接收记录"))
                throw new InvalidOperationException("设置仍有多余的数据入口。");
            if (Find<Button>("ImportSettings").Content is null || Find<Button>("ImportSession").Content is null)
                throw new InvalidOperationException("缺少设置与会话入口。");
            await CaptureAsync("settings-data");
            var captureFile = Path.Combine(directory, "phone-capture.json");
            AtomicFile.Write(captureFile, new ExchangeDocument
            {
                Analysis = JsonSerializer.SerializeToElement(new { credentials = new { security = (string?)null }, sessions = new[] { new { bind_extracted = true } } })
            }.ToJson());
            var captureImport = ImportFileAsync(captureFile);
            await Task.Delay(200);
            var captureDialog = OpenDialog();
            if (captureDialog.PrimaryButtonText != "保存分析" || !DialogField<TextBlock>(captureDialog, "ImportSummary").Text.Contains("抓包分析")
                || !DialogField<TextBlock>(captureDialog, "ImportGuidance").Text.Contains("security"))
                throw new InvalidOperationException("手机抓包导入没有区分分析与可连接账号。");
            await CaptureAsync("phone-import", captureDialog);
            await SaveDialogAsync(captureDialog);
            await captureImport.WaitAsync(TimeSpan.FromSeconds(5));
            if (_store.HasAccount || !File.Exists(Path.Combine(_paths.Data, "analysis.json"))
                || !Notice.Message.Contains("补全通道密钥"))
                throw new InvalidOperationException("抓包分析导入状态不正确。");
            Notice.IsOpen = false;
            var jsonTask = ShowJsonAsync(_imports.ExportSettings(_settings).ToJson());
            await Task.Delay(200);
            var jsonDialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Single();
            var jsonEditor = Descendants(jsonDialog).OfType<TextBox>().Single(element => AutomationProperties.GetAutomationId(element) == "JsonEditor");
            var jsonSummary = Descendants(jsonDialog).OfType<TextBlock>().Single(element => AutomationProperties.GetAutomationId(element) == "JsonSummary");
            if (ExchangeDocument.Parse(jsonEditor.Text).Settings is null) throw new InvalidOperationException("JSON 窗口未完整加载多行数据。");
            jsonEditor.Text = "{broken";
            InvokeJson("JsonAnalyze");
            await Task.Delay(100);
            if (!jsonSummary.Text.StartsWith("JSON 错误")) throw new InvalidOperationException("JSON 错误未在编辑器内显示。");
            var sharedRecord = new PushRecord { Key = "render-imported", Package = "com.example.check", Title = "导入的通知",
                Description = "JSON 导入的历史记录不会重新弹窗。", ReceivedAt = DateTimeOffset.Now.AddDays(-1), IsNotification = true };
            jsonEditor.Text = new ExchangeDocument { Notifications = [sharedRecord] }.ToJson();
            InvokeJson("JsonFormat");
            await Task.Delay(100);
            InvokeJson("JsonAnalyze");
            await Task.Delay(100);
            if (!jsonSummary.Text.Contains("1 条通知") || !jsonEditor.Text.Contains("导入的通知") || jsonEditor.Text.IndexOfAny(['\r', '\n']) < 0)
                throw new InvalidOperationException("JSON 格式化或分析失败：" + jsonSummary.Text);
            await CaptureAsync("json-editor", jsonDialog);
            jsonDialog.ApplyTemplate();
            var importButton = Descendants(jsonDialog).OfType<Button>().Single(button => button.Name == "PrimaryButton");
            ((IInvokeProvider)new ButtonAutomationPeer(importButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await jsonTask.WaitAsync(TimeSpan.FromSeconds(5));
            if (_records.Count(record => record.Key == sharedRecord.Key) != 1 || !_read.Contains(sharedRecord.Key)
                || _importedNotifications.Read().Single().Key != sharedRecord.Key || File.Exists(Path.Combine(_paths.Listener, "notifications.jsonl")))
                throw new InvalidOperationException("JSON 导入未保存独立历史或修改了实时文件。");
            Notice.IsOpen = false;
            void InvokeJson(string id)
            {
                var button = Descendants(jsonDialog).OfType<Button>().Single(element => AutomationProperties.GetAutomationId(element) == id);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            }
            Navigate("appearance");
            Invoke("Theme-midnight");
            await Task.Delay(100);
            if (_store.Read().Appearance.Theme != "midnight" || Root.RequestedTheme != ElementTheme.Dark) throw new InvalidOperationException("色块未切换配色。");
            Navigate("inbox");
            await CaptureAsync("inbox-dark");
            Navigate("appearance");
            Invoke("Theme-paper");
            await Task.Delay(100);
            Navigate("inbox");
            if (Descendants(PageHost).Any(element => AutomationProperties.GetAutomationId(element) is "TestNotification" or "Theme-paper" or "FilterUnread" or "MarkAllRead")
                || Descendants(PageHost).OfType<TextBlock>().Any(label => label.Text.Contains("条通知") || label.Text.Contains("条未读")))
                throw new InvalidOperationException("通知页面仍包含已移除的控件或计数。");
            if (Find<Button>("ToggleNotifications").Content is not FontIcon || Find<Button>("ClearNotifications").Content is not FontIcon)
                throw new InvalidOperationException("通知工具栏不是纯图标。");
            Invoke("ToggleNotifications");
            if (!_store.Read().NotificationsEnabled) throw new InvalidOperationException("恢复弹窗未生效。");
            Invoke("ToggleNotifications");
            if (_store.Read().NotificationsEnabled) throw new InvalidOperationException("暂停弹窗未生效。");
            var notificationMenu = (MenuFlyout)Find<Button>("NotificationMenu-render-message-0").Flyout;
            notificationMenu.ShowAt(Find<Button>("NotificationMenu-render-message-0"));
            await Task.Delay(150);
            var menuPresenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<MenuFlyoutPresenter>().Single();
            await CaptureAsync("notification-menu", menuPresenter);
            notificationMenu.Hide();
            var jsonAction = notificationMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "JSON");
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(jsonAction).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(200);
            var notificationJson = OpenDialog();
            await CaptureAsync("notification-json", notificationJson);
            if (!DialogField<TextBox>(notificationJson, "JsonEditor").Text.Contains("render-message-0"))
                throw new InvalidOperationException("通知菜单没有打开对应 JSON。");
            notificationJson.Hide();
            await Task.Delay(150);
            Find<TextBox>("SearchNotifications").Text = "行程";
            await Task.Delay(200);
            await CaptureAsync("inbox-search");
            if (_notificationList!.Items.Count != 2 || _notificationList.Items.OfType<FrameworkElement>()
                .Any(element => ((PushRecord)element.Tag).Package != "ctrip.android.view")) throw new InvalidOperationException($"搜索未按内容筛选：query={_query}, count={_notificationList.Items.Count}。");
            _query = "";
            Navigate("apps");
            Find<ToggleSwitch>("Notify-com.tencent.mobileqq").IsOn = false;
            await Task.Delay(100);
            if (!_store.Read().Apps["com.tencent.mobileqq"].Muted) throw new InvalidOperationException("应用静音未保存。");
            Find<ToggleSwitch>("Notify-com.tencent.mobileqq").IsOn = true;
            if (_appList!.Items.Count != AppCatalog.Known.Count) throw new InvalidOperationException("通知历史自动填满了应用页面。");
            Invoke("AddApp");
            await Task.Delay(150);
            var appEditor = OpenDialog();
            if (Descendants(appEditor).Any(element => AutomationProperties.GetAutomationId(element) is "AppDecryption" or "AppRegSecret")
                || Descendants(PageHost).OfType<ToggleSwitch>().Any(element => AutomationProperties.GetAutomationId(element) == "BuiltInIcons"))
                throw new InvalidOperationException("应用页面仍暴露协议密钥表单或独立图标库开关。");
            DialogField<TextBox>(appEditor, "AppPackage").Text = "com.example.managed";
            DialogField<TextBox>(appEditor, "AppName").Text = "应用测试";
            await CaptureAsync("application-editor", appEditor);
            await SaveDialogAsync(appEditor);
            if (_store.Read().Apps["com.example.managed"].Name != "应用测试") throw new InvalidOperationException("应用添加未保存。");
            Find<TextBox>("SearchApps").Text = "com.example.managed";
            await Task.Delay(150);
            if (_appList.Items.Count != 1) throw new InvalidOperationException("应用搜索未筛选。");
            Invoke("Edit-com.example.managed");
            await Task.Delay(150);
            appEditor = OpenDialog();
            DialogField<TextBox>(appEditor, "AppName").Text = "已修改";
            await SaveDialogAsync(appEditor);
            if (_store.Read().Apps["com.example.managed"].Name != "已修改") throw new InvalidOperationException("应用改名未保存。");
            Invoke("Remove-com.example.managed");
            if (_appList.Items.Count != 0 || _store.Read().Apps.ContainsKey("com.example.managed")) throw new InvalidOperationException("应用移除未生效。");
            Find<TextBox>("SearchApps").Text = "";
            Navigate("appearance");
            Find<Expander>("ColorEditor").IsExpanded = true;
            await Task.Delay(150);
            Find<TextBox>("Color-Accent").Text = "#3478B8";
            await Task.Delay(150);
            if (_store.Read().Appearance.Colors?.Accent != "#3478B8" || Ui.Brush("DeskAccent").Color != Ui.Color("#3478B8"))
                throw new InvalidOperationException("修改颜色未保存或没有立即生效。");
            Find<TextBox>("Color-Accent").Text = "#12";
            await Task.Delay(100);
            if (_store.Read().Appearance.Colors?.Accent != "#3478B8") throw new InvalidOperationException("未完成的色值覆盖了已保存颜色。");
            Find<TextBox>("Color-Accent").Text = "#3478B8";
            await CaptureAsync("appearance-colors");
            Invoke("Theme-paper");
            await Task.Delay(100);
            if (_store.Read().Appearance.Colors is not null) throw new InvalidOperationException("切换色块未重置自定义颜色。");
            var layout = Find<ComboBox>("LayoutChoice");
            layout.SelectedItem = layout.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "compact");
            await Task.Delay(100);
            if (_store.Read().Appearance.Layout != "compact") throw new InvalidOperationException("布局未保存。");
            Navigate("inbox");
            await CaptureAsync("inbox-compact");
            _settings.Appearance.Layout = "conversation";
            _settings.Appearance.Theme = "ocean";
            SaveSettings(); Navigate("inbox");
            await CaptureAsync("inbox-conversation");
            _settings.Appearance.Theme = "paper";
            _settings.Appearance.Layout = "cards";
            SaveSettings(); Navigate("inbox");
            await CaptureAsync("inbox-light");
            SaveSettings(); Navigate("appearance");
            var imageToggle = Find<ToggleSwitch>("ShowImages");
            if (imageToggle.IsOn || Descendants(Find<Border>("NotificationListPreview")).OfType<Image>().Any(image => image.Height >= 100))
                throw new InvalidOperationException("默认预览仍有配图。");
            if (Descendants(PageHost).OfType<TextBlock>().Any(label => label.Text.Contains("预览会发送真实")))
                throw new InvalidOperationException("仍包含多余预览说明。");
            Invoke("TestNotification");
            for (var attempt = 0; attempt < 20 && !File.Exists(Path.Combine(_paths.Data, "test-toast.xml")); attempt++) await Task.Delay(100);
            var plainTest = File.ReadAllText(Path.Combine(_paths.Data, "test-toast.xml"));
            if (plainTest.Contains("placement=\"hero\"")) throw new InvalidOperationException("默认测试通知仍有配图。");
            File.Delete(Path.Combine(_paths.Data, "test-toast.xml"));
            imageToggle.IsOn = true;
            await Task.Delay(100);
            Invoke("TestNotification");
            for (var attempt = 0; attempt < 20 && !File.Exists(Path.Combine(_paths.Data, "test-toast.xml")); attempt++) await Task.Delay(100);
            if (File.ReadAllText(Path.Combine(_paths.Data, "test-toast.xml")).Contains("placement=\"hero\""))
                throw new InvalidOperationException("未设置图片时使用了内置占位图。");
            File.Delete(Path.Combine(_paths.Data, "test-toast.xml"));
            Find<ToggleSwitch>("ShowImages").IsOn = false;
            await Task.Delay(100);
            if (_store.Read().Appearance.ShowImages) throw new InvalidOperationException("配图设置未保存。");
            _settings.Appearance.ShowImages = true;
            _settings.Appearance.DefaultImage = _imports.ImportIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "preview.png"));
            SaveSettings();
            Navigate("appearance");
            Root.UpdateLayout();
            if (PageHost.Content is ScrollViewer appearanceScroll) appearanceScroll.ChangeView(null, 10000, null, true);
            await CaptureAsync("appearance-preview");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 720));
            Navigate("inbox");
            await CaptureAsync("inbox-narrow");
            var search = Find<TextBox>("SearchNotifications");
            var packageFilter = Find<ComboBox>("FilterPackage");
            var searchBounds = search.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, search.ActualWidth, search.ActualHeight));
            var packageBounds = packageFilter.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, packageFilter.ActualWidth, packageFilter.ActualHeight));
            if (Math.Abs(searchBounds.Top - packageBounds.Top) > 1 || Math.Abs(searchBounds.Height - packageBounds.Height) > 1 || searchBounds.Right >= packageBounds.Left)
                throw new InvalidOperationException("搜索与筛选未对齐或发生重叠。");
            _settings.QuietHoursEnabled = true;
            _settings.QuietStartHour = _settings.QuietEndHour = 0;
            _settings.Appearance.ToastMode = "off";
            RuleFor("ctrip.android.view").Muted = true;
            SaveSettings();
            Navigate("appearance");
            Invoke("TestNotification");
            await Task.Delay(150);
            if (File.Exists(Path.Combine(_paths.Data, "test-toast.xml"))) throw new InvalidOperationException("仅通知中心模式仍发送系统通知。");
            _settings.Appearance.ToastMode = "tray";
            _settings.Appearance.ToastStyle = "compact";
            SaveSettings(); Navigate("appearance");
            Invoke("TestNotification");
            await Task.Delay(150);
            var balloon = JsonDocument.Parse(File.ReadAllText(Path.Combine(_paths.Data, "test-balloon.json")));
            if (balloon.RootElement.GetProperty("text").GetString()!.Contains('\n') || File.Exists(Path.Combine(_paths.Data, "test-toast.xml")))
                throw new InvalidOperationException("托盘测试未跟随提示模式或内容。");
            _settings.Appearance.ToastMode = "native";
            _settings.Appearance.ToastStyle = "rich";
            SaveSettings(); Navigate("appearance");
            Invoke("TestNotification");
            for (var attempt = 0; attempt < 20 && !File.Exists(Path.Combine(_paths.Data, "test-toast.xml")); attempt++) await Task.Delay(100);
            var testXml = File.ReadAllText(Path.Combine(_paths.Data, "test-toast.xml"));
            if (!testXml.Contains("launch=\"message=") || !testXml.Contains("placement=\"hero\"")
                || testXml.Contains("content=\"查看详情\"")) throw new InvalidOperationException("预览未使用真实通知模板。");
            var saved = _store.Read();
            if (saved.NotificationsEnabled || !saved.QuietHoursEnabled || saved.Appearance.ToastMode != "native" || !saved.Apps["ctrip.android.view"].Muted)
                throw new InvalidOperationException("测试通知修改了暂停或免打扰设置。");
            OpenFromToast(_testNotifications.Keys.Last());
            await Task.Delay(200);
            var detail = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().SingleOrDefault();
            if (detail is null) throw new InvalidOperationException("点击测试通知未打开详情。");
            await CaptureAsync("notification-detail", detail);
            detail.Hide();
            await Task.Delay(200);
            _settings.Appearance.FontSize = 18;
            var rich = PreviewRecord();
            rich.Key = "render-rich";
            rich.Title = "多行通知 · 表情 😀 · 操作菜单";
            rich.Description = string.Join('\n', Enumerable.Repeat("较长的通知正文在列表保留摘要，详情中可以完整选择、复制。", 5));
            rich.Extra["notification_style_type"] = "1";
            rich.Extra["notification_style_button_right_name"] = "在手机应用内查看";
            rich.Extra["notification_style_button_right_notify_effect"] = "2";
            rich.Extra["notification_style_button_right_intent_uri"] = "intent:#Intent;component=example.test/.Main;end";
            _records.Insert(0, rich);
            SaveSettings(); Navigate("inbox");
            await CaptureAsync("inbox-large-text");
            foreach (var row in _notificationList!.Items.OfType<Border>().Take(3))
            {
                if (row.ActualWidth > _notificationList.ActualWidth + 1) throw new InvalidOperationException("大字号通知横向溢出。");
                var columns = (Grid)row.Child;
                var content = (FrameworkElement)columns.Children[1];
                var menu = (FrameworkElement)columns.Children[^1];
                var contentBounds = content.TransformToVisual(row).TransformBounds(new Windows.Foundation.Rect(0, 0, content.ActualWidth, content.ActualHeight));
                var menuBounds = menu.TransformToVisual(row).TransformBounds(new Windows.Foundation.Rect(0, 0, menu.ActualWidth, menu.ActualHeight));
                if (contentBounds.Right >= menuBounds.Left) throw new InvalidOperationException("通知正文与操作菜单重叠。");
            }
            SaveSettings(); Navigate("settings");
            await CaptureAsync("settings-large-text");
            var historyFile = Path.Combine(_paths.Listener, "notifications.jsonl");
            AtomicFile.Write(historyFile, string.Join('\n', _records.Select(record => JsonSerializer.Serialize(record))) + "\n");
            var settingsBeforeClear = File.ReadAllText(_paths.Settings);
            _query = "设计讨论组"; _packageFilter = "";
            Navigate("inbox");
            var beforeDelete = _records.Count;
            Invoke("DeleteNotification-render-message-0");
            await Task.Delay(150);
            if (_records.Count != beforeDelete - 1 || _records.Any(record => record.Key == "render-message-0")
                || new NotificationFeed(historyFile).Read(true).Any(record => record.Key == "render-message-0"))
                throw new InvalidOperationException("单条删除未保存，或影响了其他通知。");
            await CaptureAsync("inbox-deleted");
            _query = "行程";
            _packageFilter = "ctrip.android.view";
            Navigate("inbox");
            Invoke("ClearNotifications");
            await Task.Delay(150);
            if (_records.Count != 0 || _read.Count != 0 || _testNotifications.Count != 0 || _notificationList!.Items.Count != 0
                || _query.Length != 0 || _packageFilter.Length != 0 || new NotificationFeed(historyFile).Read(true).Count != 0)
                throw new InvalidOperationException("清空未覆盖全部通知、已读状态或持久化历史。");
            if (_importedNotifications.Read().Count != 0) throw new InvalidOperationException("清空后仍有导入历史。");
            if (File.ReadAllText(_paths.Settings) != settingsBeforeClear) throw new InvalidOperationException("清空通知修改了应用配置。");
            await CaptureAsync("inbox-cleared");
            if (_emptyInbox is not StackPanel || _emptyInbox.Parent is Border) throw new InvalidOperationException("空通知仍显示为卡片。");
            var afterClear = new PushRecord { Key = "render-after-clear", Package = "com.example.check", Title = "新的通知",
                Description = "清空后，新消息继续正常到达。", ReceivedAt = DateTimeOffset.Now, IsNotification = true };
            File.AppendAllText(historyFile, JsonSerializer.Serialize(afterClear) + "\n");
            await PollAsync();
            if (_records.Count != 1 || _records[0].Key != afterClear.Key) throw new InvalidOperationException("清空后无法接收新通知。");
            await CaptureAsync("inbox-after-clear");
            var preview = PreviewRecord();
            var xml = RichNotification.BuildToast(preview, _settings.Appearance, "携程旅行", imagePath: Path.Combine(AppContext.BaseDirectory, "Assets", "preview.png"));
            if (!xml.Contains("placement=\"hero\"") || !xml.Contains("activationType=\"protocol\"")) throw new InvalidOperationException("复杂通知模板缺少图片或操作。");
            AtomicFile.Write(Path.Combine(directory, "toast-preview.xml"), xml);
            AtomicFile.Write(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new
            { succeeded = true, captures, search = true, appMutePersistence = true, appearancePersistence = true, imagePreference = true,
                richToastTemplate = true, quickPaletteSwitch = true, customColors = true, alignedSearch = true,
                testWhilePaused = true, notificationClick = true, clearAll = true, clearPersistence = true, receiveAfterClear = true,
                inboxControls = true, singleAppearance = true, defaultWithoutImages = true, jsonEditor = true, importedHistory = true,
                iconToolbar = true, notificationMenu = true, managedApplications = true, connectionTiming = true, largeTextRows = true,
                singleDelete = true, plainEmptyState = true, simplifiedData = true, defaultXiaomiMetadata = true,
                appSecretRecoveryStatus = true, nativeNotificationsSent = 0, receiverStarted = false }, JsonData.Options));
        }
        catch (Exception error)
        {
            AtomicFile.Write(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { succeeded = false, error = error.ToString(), captures }, JsonData.Options));
        }
        finally { await ExitAsync(); }

        async Task CaptureAsync(string name, FrameworkElement? target = null)
        {
            await Task.Delay(400);
            Root.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(target ?? Root);
            var pixels = await bitmap.GetPixelsAsync();
            var path = Path.Combine(directory, name + ".png");
            File.WriteAllBytes(path, []);
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            captures.Add(new { name, width = bitmap.PixelWidth, height = bitmap.PixelHeight });
        }
        T Find<T>(string id) where T : FrameworkElement
        {
            Root.UpdateLayout();
            return Descendants(PageHost).OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == id);
        }
        void Invoke(string id) => ((IInvokeProvider)new ButtonAutomationPeer(Find<Button>(id)).GetPattern(PatternInterface.Invoke)).Invoke();
        ContentDialog OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Single();
        T DialogField<T>(ContentDialog dialog, string id) where T : FrameworkElement => Descendants(dialog).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == id);
        async Task SaveDialogAsync(ContentDialog dialog)
        {
            var save = Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton");
            ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(200);
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Any())
                throw new InvalidOperationException("保存应用后对话框未关闭。");
        }
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        if (parent is FrameworkElement element) yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}

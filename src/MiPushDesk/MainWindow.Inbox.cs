using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MiPushDesk.Core;
using MiPushDesk.Services;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private ListView? _notificationList;
    private TextBlock? _emptyLabel;
    private StackPanel? _emptyInbox;
    private int _visibleLimit = 120;
    private readonly Dictionary<string, PushRecord> _testNotifications = [];

    private UIElement BuildInbox()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = Ui.Star() });
        var heading = Ui.Columns(Ui.Star(), GridLength.Auto);
        heading.ColumnSpacing = 16;
        Ui.Add(heading, Ui.Text("通知", 27, bold: true));
        var pause = IconButton(_settings.NotificationsEnabled ? "暂停弹窗" : "恢复弹窗",
            _settings.NotificationsEnabled ? "\uE769" : "\uE768", () => TrayAction("pause"), "ToggleNotifications");
        if (!_settings.NotificationsEnabled)
        {
            pause.Background = Ui.Brush("DeskAccentSoft");
            ((FontIcon)pause.Content).Foreground = Ui.Brush("DeskAccent");
        }
        var clear = IconButton("清空全部通知", "\uE711", () => Run(ClearNotificationsAsync), "ClearNotifications");
        Ui.Add(heading, Ui.Row(6, pause, clear), 1);
        var controlHeight = Math.Max(40, Math.Ceiling(_settings.Appearance.FontSize * 2.5));
        var search = new TextBox { PlaceholderText = "搜索通知", Text = _query, MinWidth = 160,
            Height = controlHeight, FontSize = 13 * _settings.Appearance.FontSize / 14,
            Padding = new(38, 8, 32, 8), VerticalContentAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(search, "SearchNotifications");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "搜索通知");
        search.TextChanged += (_, _) => { _query = search.Text; _visibleLimit = 120; RefreshInbox(); };
        var searchField = new Grid(); searchField.Children.Add(search);
        var searchIcon = Ui.Icon("\uE721", 14); searchIcon.IsHitTestVisible = false;
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.VerticalAlignment = VerticalAlignment.Center; searchIcon.Margin = new(14, 0, 0, 0);
        searchField.Children.Add(searchIcon);
        var packages = new ComboBox { Width = 152, Height = controlHeight, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(packages, "FilterPackage");
        packages.Items.Add(new ComboBoxItem { Content = "全部应用", Tag = "" });
        foreach (var package in _records.Select(record => record.Package).Distinct().OrderBy(package => _images.AppName(package, _settings)))
            packages.Items.Add(new ComboBoxItem { Content = _images.AppName(package, _settings), Tag = package });
        packages.SelectedItem = packages.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == _packageFilter) ?? packages.Items[0];
        packages.SelectionChanged += (_, _) => { _packageFilter = (string)((ComboBoxItem)packages.SelectedItem).Tag; RefreshInbox(); };
        var filters = Ui.Columns(Ui.Star(), GridLength.Auto);
        filters.ColumnSpacing = 10;
        Ui.Add(filters, searchField); Ui.Add(filters, packages, 1);
        var top = Ui.Stack(20, heading, filters);
        top.Margin = new(0, 0, 0, 16);
        Ui.Add(root, top);
        var itemStyle = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources[typeof(ListViewItem)] };
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 5)));
        _notificationList = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemContainerStyle = itemStyle };
        _notificationList.ItemClick += (_, arguments) =>
        {
            if (arguments.ClickedItem is FrameworkElement { Tag: PushRecord record }) Run(() => ShowDetailsAsync(record));
        };
        _notificationList.Footer = Ui.Button("加载更早通知", () => { _visibleLimit += 120; RefreshInbox(); }, "DeskQuiet");
        _emptyLabel = Ui.Text("暂无通知", 15, "DeskMuted");
        _emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyInbox = Ui.Stack(16, _emptyLabel);
        if (!_store.HasAccount) _emptyInbox.Children.Add(Ui.Button("导入会话", () => Run(ImportSessionAsync), "DeskQuiet", "\uE8B5"));
        _emptyInbox.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyInbox.VerticalAlignment = VerticalAlignment.Center;
        var body = new Grid(); body.Children.Add(_notificationList); body.Children.Add(_emptyInbox);
        Ui.Add(root, body, row: 1);
        RefreshInbox();
        return root;
    }
    private Task ClearNotificationsAsync()
    {
        _feed.Clear();
        _importedNotifications.Clear();
        _records.Clear();
        _read.Clear();
        SaveRead();
        _testNotifications.Clear();
        _notifications.Clear();
        if (!_renderMode) _tray.ClearBalloon();
        _query = "";
        _packageFilter = "";
        _visibleLimit = 120;
        Navigate("inbox");
        UpdateStatus();
        return Task.CompletedTask;
    }
    private Task DeleteNotificationAsync(PushRecord record)
    {
        _feed.Delete(record.Key);
        _importedNotifications.Delete(record.Key);
        _records.RemoveAll(item => item.Key == record.Key);
        _read.Remove(record.Key);
        SaveRead();
        _testNotifications.Remove(record.Key);
        _notifications.Remove(record);
        if (!_renderMode) _tray.ClearBalloon(record.Key);
        RefreshInbox();
        UpdateStatus();
        return Task.CompletedTask;
    }
    private void RefreshInbox()
    {
        if (_notificationList is null) return;
        var filtered = _records.Where(record => (_packageFilter.Length == 0 || record.Package == _packageFilter)
            && (_query.Length == 0 || (record.Title + " " + record.Description + " " + record.Package + " " + _images.AppName(record.Package, _settings))
                .Contains(_query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var visible = filtered.Take(_visibleLimit).ToList();
        _notificationList.ItemsSource = visible.Select(record => NotificationCard(record)).ToArray();
        if (_emptyInbox is not null) _emptyInbox.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_emptyLabel is not null) _emptyLabel.Text = _query.Length > 0 || _packageFilter.Length > 0 ? "没有匹配的通知" : "暂无通知";
        if (_notificationList.Footer is FrameworkElement footer) footer.Visibility = filtered.Count > _visibleLimit ? Visibility.Visible : Visibility.Collapsed;
    }
    private UIElement AppLogo(string package, double size = 42, bool? enabled = null)
    {
        var useIcons = enabled ?? _settings.Appearance.UseAppIcons;
        var badge = AppCatalog.Badge(package);
        var mark = Ui.Text(useIcons ? badge.Mark : "M", size * 0.32, bold: true);
        mark.Foreground = new SolidColorBrush(useIcons ? Ui.Color(badge.Color) : Ui.Brush("DeskAccent").Color);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var border = new Border { Width = size, Height = size, CornerRadius = new(size * 0.28),
            Background = Ui.Brush("DeskAccentSoft"), Child = mark, VerticalAlignment = VerticalAlignment.Top };
        if (useIcons && _images.AppIcon(package, _settings) is { } icon)
        {
            var image = new Image { Source = new BitmapImage(new Uri(icon)) { DecodePixelWidth = 128 }, Stretch = Stretch.UniformToFill };
            image.ImageFailed += (_, arguments) => ReportError(new InvalidDataException(arguments.ErrorMessage));
            border.Child = image;
        }
        return border;
    }
    private Border NotificationCard(PushRecord record, bool preview = false)
    {
        var profile = _settings.Appearance;
        var compact = profile.Layout == "compact";
        var columns = Ui.Columns(GridLength.Auto, Ui.Star(), GridLength.Auto, GridLength.Auto);
        columns.ColumnSpacing = 12;
        var logo = (FrameworkElement)AppLogo(record.Package, compact ? 28 : 32, profile.UseAppIcons);
        logo.VerticalAlignment = VerticalAlignment.Center;
        Ui.Add(columns, logo);
        var source = Ui.Text(_images.AppName(record.Package, _settings), 11, "DeskMuted");
        var title = Ui.Text(record.Title, 14, "DeskText", preview || !_read.Contains(record.Key));
        var description = Ui.Wrap(record.Description, 12, profile.Layout == "conversation" ? "DeskText" : "DeskMuted");
        description.MaxLines = compact ? 1 : 2;
        var content = Ui.Stack(compact ? 2 : 4, title);
        if (profile.Layout == "conversation")
            content.Children.Add(new Border { Background = Ui.Brush("DeskAccentSoft"), CornerRadius = new(2, 12, 12, 12), Padding = new(13, 10, 13, 10), Child = description });
        else if (record.Description.Length > 0) content.Children.Add(description);
        content.Children.Add(source);
        if (!compact && profile.ShowImages && RichNotification.AvatarUrl(record) is { } avatarUrl)
        {
            var avatar = new Image { Width = 44, Height = 44, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left };
            LoadNotificationImage(avatar, avatarUrl);
            content.Children.Add(avatar);
        }
        if (!compact && profile.ShowImages && (RichNotification.ImageUrl(record) is not null || profile.DefaultImage is not null))
        {
            var image = new Image { Height = 145, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch };
            var surface = new Border { CornerRadius = new(8), Child = image, MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Left };
            LoadNotificationPicture(image, record);
            content.Children.Add(surface);
        }
        if (profile.Layout == "conversation")
        {
            var actions = new WrapPanel { Spacing = 6 };
            foreach (var action in RichNotification.Actions(record))
            {
                if (preview) actions.Children.Add(new Border { Padding = new(10, 8, 10, 8), Child = Ui.Row(7, Ui.Icon("\uE8A7", 14), Ui.Text(action.Label, 13, "DeskMuted")) });
                else
                {
                    var button = ActionButton(action, "DeskQuiet");
                    if (profile.UseMessageColors && RichNotification.ButtonColor(record) is { } buttonColor
                        && action.Label == record.ExtraValue("notification_colorful_button_text"))
                    {
                        button.Background = new SolidColorBrush(Ui.Color(buttonColor));
                        SetContentForeground(button.Content as UIElement, Palettes.IsDark(buttonColor) ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                    }
                    actions.Children.Add(button);
                }
            }
            if (actions.Children.Count > 0) content.Children.Add(actions);
        }
        Ui.Add(columns, content, 1);
        var time = Ui.Text(Ui.Time(record.ReceivedAt), 11, "DeskMuted");
        Ui.Add(columns, time, 2);
        if (!preview)
        {
            var more = IconButton("更多操作", "\uE712", () => { }, "NotificationMenu-" + record.Key);
            more.Flyout = NotificationMenu(record);
            var delete = IconButton("删除通知", "\uE711", () => Run(() => DeleteNotificationAsync(record)), "DeleteNotification-" + record.Key);
            Ui.Add(columns, Ui.Row(0, more, delete), 3);
        }
        var card = Ui.Card(columns, 0);
        card.Padding = new(16, compact ? 9 : 12, preview ? 16 : 8, compact ? 9 : 12);
        card.MinHeight = compact ? 68 : 84;
        if (profile.UseMessageColors && RichNotification.Background(record) is { } background)
        {
            card.Background = new SolidColorBrush(Ui.Color(background));
            var foreground = Palettes.IsDark(background) ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            title.Foreground = description.Foreground = time.Foreground = new SolidColorBrush(foreground);
            source.Foreground = new SolidColorBrush(foreground);
        }
        card.Tag = record;
        card.CornerRadius = new(profile.CornerRadius);
        return card;
    }
    private MenuFlyout NotificationMenu(PushRecord record)
    {
        var menu = new MenuFlyout();
        Add("查看详情", "\uE8A5", () => Run(() => ShowDetailsAsync(record)));
        Add("复制", "\uE8C8", () => CopyText(record.Title + Environment.NewLine + record.Description));
        Add("JSON", "\uE943", () => Run(() => ShowJsonAsync(new ExchangeDocument { Notifications = [record] }.ToJson())));
        var actions = RichNotification.Actions(record);
        if (actions.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var action in actions) Add(action.Label, action.IsWeb ? "\uE8A7" : "\uE8C8", () => ActivateNotificationAction(action));
        return menu;

        void Add(string label, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = Ui.Icon(glyph) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }
    private async void LoadNotificationImage(Image image, string url)
    {
        try
        {
            var file = await _images.FetchAsync(url);
            image.Source = new BitmapImage(new Uri(file!)) { DecodePixelWidth = 900 };
            image.ImageFailed += (_, arguments) => ReportError(new InvalidDataException(arguments.ErrorMessage));
        }
        catch (Exception error) { ReportError(error); }
    }
    private async void LoadNotificationPicture(Image image, PushRecord record)
    {
        try
        {
            if (await _images.NotificationImageAsync(record, _settings.Appearance) is { } file)
                image.Source = new BitmapImage(new Uri(file)) { DecodePixelWidth = 900 };
        }
        catch (Exception error) { ReportError(error); }
    }
    private async Task ShowDetailsAsync(PushRecord record)
    {
        var preview = _testNotifications.ContainsKey(record.Key);
        if (!preview) MarkRead(record);
        var text = Ui.Wrap(record.Description, 15, "DeskText");
        text.IsTextSelectionEnabled = true;
        var body = Ui.Stack(18, Ui.Row(12, AppLogo(record.Package), Ui.Stack(5, Ui.Text(_images.AppName(record.Package, _settings), 14, bold: true),
            Ui.Text(record.ReceivedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), 11, "DeskMuted"))),
            Ui.Wrap(record.Title, 20, "DeskText"), text);
        var url = RichNotification.ImageUrl(record);
        var profile = _settings.Appearance;
        if (profile.ShowImages && (url is not null || profile.DefaultImage is not null))
        {
            var image = new Image { Height = 220, Stretch = Stretch.Uniform };
            var surface = new Border { Child = image }; body.Children.Add(surface);
            LoadNotificationPicture(image, record);
        }
        var actions = new WrapPanel { Spacing = 8 };
        foreach (var action in RichNotification.Actions(record)) actions.Children.Add(ActionButton(action));
        if (actions.Children.Count > 0) body.Children.Add(actions);
        if (!string.IsNullOrEmpty(record.PayloadText) && record.PayloadText != record.Description)
        {
            var payload = Ui.Wrap(record.PayloadText, 13, "DeskText");
            payload.IsTextSelectionEnabled = true;
            body.Children.Add(new Expander { Header = "正文", Content = payload, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        body.Children.Add(Ui.Rule());
        body.Children.Add(Ui.Wrap(record.Package, 11));
        if (record.EncryptedAction && record.BodyStatus != "decoded") body.Children.Add(Ui.Wrap(record.BodyStatus == "decrypt_failed"
            ? "解密失败，显示摘要" : "正文加密，显示摘要", 11));
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "通知详情", Content = Ui.Scroll(body),
            PrimaryButtonText = "复制", SecondaryButtonText = "JSON", CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = Root.RequestedTheme };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) CopyText(record.Title + Environment.NewLine + record.Description);
        if (result == ContentDialogResult.Secondary) await ShowJsonAsync(new ExchangeDocument { Notifications = [record] }.ToJson());
        RefreshInbox();
    }
    private Button TestNotificationButton()
    {
        var button = Ui.Button("测试通知", () => Run(TestNotificationAsync), "DeskButton", "\uE768", "TestNotification");
        return button;
    }
    private async Task TestNotificationAsync()
    {
        var record = PreviewRecord();
        record.Title = "测试通知";
        record.Description = "这是一条测试通知。\n显示方式跟随当前提醒设置。\n点击通知返回 MiPush Desk。";
        _testNotifications[record.Key] = record;
        _records.Insert(0, record);
        await _notifications.ShowTestAsync(record);
        if (_settings.Appearance.ToastMode == "off") Notify("已添加到通知列表");
    }
    private static void OpenLink(string url)
    {
        if (RichNotification.WebUrl(url) is { } valid) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(valid) { UseShellExecute = true });
    }
    private Button ActionButton(NotificationAction action, string style = "DeskButton")
    {
        var button = Ui.Button(action.Label, () => ActivateNotificationAction(action), style, action.IsWeb ? "\uE8A7" : "\uE8C8");
        button.MaxWidth = 250;
        ToolTipService.SetToolTip(button, action.IsWeb ? action.Url : "复制应用内操作");
        return button;
    }
    private void ActivateNotificationAction(NotificationAction action)
    {
        if (action.IsWeb) OpenLink(action.Url);
        else { CopyText(action.Url); Notify("已复制 Android 操作"); }
    }
    private static void SetContentForeground(UIElement? content, Windows.UI.Color color)
    {
        if (content is TextBlock text) text.Foreground = new SolidColorBrush(color);
        if (content is FontIcon icon) icon.Foreground = new SolidColorBrush(color);
        if (content is Panel panel) foreach (var child in panel.Children) SetContentForeground(child, color);
    }
    private static PushRecord PreviewRecord() => new()
    {
        Key = "preview-" + Guid.NewGuid().ToString("N"), Package = "ctrip.android.view", Title = "周末行程提醒",
        Description = "上海 → 杭州 · 周六 09:30\n请提前确认出发时间与天气。",
        ReceivedAt = DateTimeOffset.Now, IsNotification = true,
        Extra = new() { ["notification_style_button_left_name"] = "查看行程", ["notification_style_button_left_web_uri"] = "https://www.ctrip.com/" }
    };
}

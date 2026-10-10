using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MiPushDesk.Core;
using MiPushDesk.Services;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private string _appQuery = "";
    private ListView? _appList;
    private AppRule RuleFor(string package)
    {
        if (!_settings.Apps.TryGetValue(package, out var rule)) _settings.Apps[package] = rule = new();
        return rule;
    }
    private UIElement BuildApps()
    {
        var library = Ui.Button("图标库", () => { }, "DeskQuiet", "\uE8B9", "IconLibraryMenu");
        var menu = new MenuFlyout();
        var builtIn = new ToggleMenuFlyoutItem { Text = "使用内置图标", IsChecked = _settings.UseBuiltInIcons };
        builtIn.Click += (_, _) => { _settings.UseBuiltInIcons = builtIn.IsChecked; SaveSettings(); RefreshApps(); };
        var import = new MenuFlyoutItem { Text = "导入图标包…", Icon = Ui.Icon("\uE8B5") };
        import.Click += (_, _) => Run(ImportIconPackAsync);
        var restore = new MenuFlyoutItem { Text = "恢复内置应用" };
        restore.Click += (_, _) => { _settings.HiddenApps.ExceptWith(AppCatalog.Known.Keys); SaveSettings(true); };
        menu.Items.Add(builtIn); menu.Items.Add(import);
        if (_settings.LibraryIcons.Count > 0)
        {
            var remove = new MenuFlyoutItem { Text = "移除“" + _settings.IconLibraryName + "”" };
            remove.Click += (_, _) => { _settings.LibraryIcons.Clear(); _settings.IconLibraryName = ""; SaveSettings(true); };
            menu.Items.Add(remove);
        }
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(restore); library.Flyout = menu;
        ToolTipService.SetToolTip(library, $"内置 {_images.BuiltIn.Count:N0} 个包名映射");
        var heading = Ui.Heading("应用", Ui.Row(8, library, Ui.Button("添加", () => Run(AddAppAsync), "DeskButton", "\uE710", "AddApp")));
        var search = new TextBox { PlaceholderText = "搜索名称或包名", Text = _appQuery,
            Height = Math.Max(40, Math.Ceiling(_settings.Appearance.FontSize * 2.5)),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new(38, 8, 12, 8) };
        AutomationProperties.SetAutomationId(search, "SearchApps");
        search.TextChanged += (_, _) => { _appQuery = search.Text; RefreshApps(); };
        var searchRow = new Grid(); searchRow.Children.Add(search);
        var searchIcon = Ui.Icon("\uE721", 14); searchIcon.IsHitTestVisible = false;
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.VerticalAlignment = VerticalAlignment.Center; searchIcon.Margin = new(14, 0, 0, 0);
        searchRow.Children.Add(searchIcon);
        var itemStyle = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources[typeof(ListViewItem)] };
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 5)));
        _appList = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemContainerStyle = itemStyle };
        var root = new Grid { RowSpacing = 18 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = Ui.Star() });
        Ui.Add(root, heading); Ui.Add(root, searchRow, row: 1); Ui.Add(root, _appList, row: 2);
        RefreshApps();
        return root;
    }
    private void RefreshApps()
    {
        if (_appList is null) return;
        var packages = AppCatalog.Packages(_settings).Where(package => _appQuery.Length == 0
            || (package + " " + _images.AppName(package, _settings)).Contains(_appQuery, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var content = new List<UIElement>();
        foreach (var package in packages)
        {
            var row = Ui.Columns(GridLength.Auto, Ui.Star(), GridLength.Auto, GridLength.Auto); row.ColumnSpacing = 14;
            row.Padding = new(14, 12, 10, 12);
            var logo = (FrameworkElement)AppLogo(package, 32, true); logo.VerticalAlignment = VerticalAlignment.Center;
            Ui.Add(row, logo);
            var label = Ui.Stack(5, Ui.Text(_images.AppName(package, _settings), 14, bold: true), Ui.Text(package, 11, "DeskMuted"));
            ToolTipService.SetToolTip(label, package); Ui.Add(row, label, 1);
            var enabled = Toggle(!(_settings.Apps.GetValueOrDefault(package)?.Muted ?? false), value =>
            { RuleFor(package).Muted = !value; SaveSettings(); }, "Notify-" + package);
            ToolTipService.SetToolTip(enabled, "桌面弹窗"); Ui.Add(row, enabled, 2);
            Ui.Add(row, Ui.Row(2, IconButton("编辑", "\uE70F", () => Run(() => EditAppAsync(package)), "Edit-" + package),
                IconButton("从列表移除", "\uE711", () => { _settings.Apps.Remove(package); _settings.HiddenApps.Add(package); SaveSettings(); RefreshApps(); }, "Remove-" + package)), 3);
            var surface = Ui.Card(row, 0); content.Add(surface);
        }
        _appList.ItemsSource = content;
        _appList.Footer = packages.Length == 0 ? Ui.Wrap("没有匹配的应用", 14) : null;
    }
    private Task AddAppAsync() => EditAppAsync(null);
    private async Task EditAppAsync(string? existingPackage)
    {
        var rule = existingPackage is null ? new AppRule() : _settings.Apps.GetValueOrDefault(existingPackage) ?? new();
        var originalName = existingPackage is null ? "" : _images.AppName(existingPackage, _settings);
        var package = new TextBox { Header = "包名", Text = existingPackage ?? "", IsReadOnly = existingPackage is not null, PlaceholderText = "com.example.app", MaxLength = 256 };
        var name = new TextBox { Header = "名称", Text = originalName, MaxLength = 128 };
        AutomationProperties.SetAutomationId(package, "AppPackage"); AutomationProperties.SetAutomationId(name, "AppName");
        var selectedIcon = rule.Icon;
        var iconPreview = new Border { Width = 44, Height = 44, Child = existingPackage is null ? Ui.Icon("\uECAA", 24) : AppLogo(existingPackage, 44, true) };
        var iconControls = Ui.Row(10, iconPreview,
            Ui.Button("更换图标", async () =>
            {
                try
                {
                    if (await PickAsync(".png", ".jpg", ".jpeg") is not { } file) return;
                    selectedIcon = _imports.ImportIcon(file);
                    iconPreview.Child = new Image { Source = new BitmapImage(new Uri(Path.Combine(_paths.Icons, selectedIcon))), Stretch = Stretch.Uniform };
                }
                catch (Exception error) { ReportError(error); }
            }), Ui.Button("默认", () => { selectedIcon = null; iconPreview.Child = Ui.Icon("\uECAA", 24); }, "DeskQuiet"));
        var errorText = Ui.Wrap("", 12); errorText.Visibility = Visibility.Collapsed;
        var content = Ui.Stack(16, package, name, iconControls, errorText); content.Width = 450;
        var dialog = Dialog(existingPackage is null ? "添加应用" : "编辑应用", Ui.Scroll(content), "保存");
        dialog.PrimaryButtonClick += (_, arguments) =>
        {
            var deferral = arguments.GetDeferral();
            try
            {
                var packageName = package.Text.Trim();
                if (!AppStore.ValidPackage(packageName)) throw new InvalidDataException("包名格式无效。");
                var updated = RuleFor(packageName); updated.Icon = selectedIcon;
                if (name.Text.Trim() != originalName || existingPackage is null) updated.Name = name.Text.Trim();
                _settings.HiddenApps.Remove(packageName); SaveSettings();
                RefreshApps();
            }
            catch (Exception error) { arguments.Cancel = true; errorText.Visibility = Visibility.Visible; errorText.Text = AppErrors.Describe(error); }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
    private async Task ImportIconPackAsync()
    {
        if (await PickAsync(".zip") is not { } file) return;
        var imported = await Task.Run(() => IconPack.Read(file));
        var icons = await Task.Run(() => imported.Icons.ToDictionary(pair => pair.Key, pair => _imports.StoreIcon(pair.Value), StringComparer.Ordinal));
        _settings.LibraryIcons = icons; _settings.IconLibraryName = Path.GetFileNameWithoutExtension(file);
        if (_settings.IconLibraryName.Length > 128) _settings.IconLibraryName = _settings.IconLibraryName[..128];
        SaveSettings(true);
        Notify(imported.Unavailable == 0 ? $"已导入 {icons.Count} 个图标" : $"已导入 {icons.Count} 个图标 · {imported.Unavailable} 个无位图");
    }
    private static Button IconButton(string label, string glyph, Action action, string? id = null)
    {
        var button = Ui.Button(label, action, "DeskQuiet", id: id); button.Content = Ui.Icon(glyph, 14);
        button.Width = button.Height = 34; button.Padding = new(0); ToolTipService.SetToolTip(button, label); return button;
    }
    private ContentDialog Dialog(string title, UIElement content, string primary) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "取消",
        XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, DefaultButton = ContentDialogButton.Primary
    };
}

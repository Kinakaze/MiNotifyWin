using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MiPushDesk.Core;
using MiPushDesk.Services;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _accountImportStop;
    private bool _accountWorking;

    private async Task CompleteAccountAsync(ExchangeDocument document, IReadOnlyList<AccountCandidate> accounts, string name)
    {
        using var stop = new CancellationTokenSource();
        _accountImportStop = stop;
        var adb = new AdbClient(AdbClient.BundledPath);
        var busy = false;
        var imported = false;
        Task operation = Task.CompletedTask;
        var accountPicker = new ComboBox { Header = "账号", HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "选择要导入的账号", Visibility = accounts.Count > 1 ? Visibility.Visible : Visibility.Collapsed };
        foreach (var account in accounts) accountPicker.Items.Add(new ComboBoxItem { Content = account.Label, Tag = account });
        if (accounts.Count == 1) accountPicker.SelectedIndex = 0;
        var security = new PasswordBox { Header = "security", PlaceholderText = "填写，或从手机提取",
            PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 16384 };
        AutomationProperties.SetAutomationId(security, "AccountSecurity");
        AutomationProperties.SetAutomationId(accountPicker, "AccountPicker");
        var status = Ui.Wrap("", 13);
        AutomationProperties.SetAutomationId(status, "AccountImportStatus");
        var devices = new ComboBox { PlaceholderText = "选择手机", MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(devices, "AdbDevices");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "导入账号",
            PrimaryButtonText = "验证并保存", SecondaryButtonText = "查看 JSON", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        var inputs = new List<Control> { accountPicker, security, devices };
        var refresh = Ui.Button("刷新", () => Start(RefreshDevicesAsync), id: "AdbRefresh");
        var extract = Ui.Button("提取 security", () => Start(async cancellationToken =>
        {
            if (devices.SelectedItem is not ComboBoxItem { Tag: AdbDevice device }) throw new InvalidDataException("请先连接并选择手机。");
            var account = Selected() ?? throw new InvalidDataException("请选择账号。");
            security.Password = await adb.ExtractSecurityAsync(device, account, new Progress<string>(message =>
            {
                if (!stop.IsCancellationRequested) status.Text = message;
            }), cancellationToken);
            status.Text = "已提取，点击「验证并保存」。";
        }, true), "DeskPrimary", id: "AdbExtract");
        var connectAddress = new TextBox { Header = "连接地址", PlaceholderText = "IP:连接端口", MinWidth = 220 };
        var pairAddress = new TextBox { Header = "配对地址", PlaceholderText = "IP:配对端口", MinWidth = 220 };
        var pairCode = new PasswordBox { Header = "配对码", MaxLength = 6, Width = 100 };
        var connect = Ui.Button("连接", () => Start(async cancellationToken =>
        {
            await adb.ConnectAsync(connectAddress.Text.Trim(), cancellationToken);
            await RefreshDevicesAsync(cancellationToken);
        }), id: "AdbConnect");
        var pair = Ui.Button("配对", () => Start(async cancellationToken =>
        {
            await adb.PairAsync(pairAddress.Text.Trim(), pairCode.Password, cancellationToken);
            pairCode.Password = "";
            status.Text = "已配对，请填写无线调试主页的连接地址并连接。";
        }), id: "AdbPair");
        inputs.AddRange([refresh, extract, connectAddress, pairAddress, pairCode, connect, pair]);
        var wireless = new Expander
        {
            Header = "Wi-Fi 调试", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Stack(10, Ui.Wrap("手机「开发者选项 → 无线调试」。首次使用先配对。", 12),
                Ui.Row(8, pairAddress, pairCode, pair), Ui.Row(8, connectAddress, connect))
        };
        AutomationProperties.SetAutomationId(wireless, "AdbWifiOptions");
        var phone = new Expander
        {
            Header = "从手机提取", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Stack(12, Ui.Wrap("USB 连接后，开启 USB 调试并在手机授权。", 12),
                Ui.Row(8, devices, refresh), extract, wireless)
        };
        AutomationProperties.SetAutomationId(phone, "AdbOptions");
        phone.Expanding += (_, _) => Start(RefreshDevicesAsync);
        var content = Ui.Stack(14, Ui.Wrap(name.Length > 0 ? name : document.Summary(), 12), accountPicker, security, phone);
        content.Width = 540;
        var body = Ui.Stack(12, new ScrollViewer { Content = content, MaxHeight = 470, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, status);
        body.Width = 540;
        dialog.Content = body;
        accountPicker.SelectionChanged += (_, _) => SelectAccount();
        security.PasswordChanged += (_, _) => UpdateControls();
        dialog.PrimaryButtonClick += async (_, arguments) =>
        {
            var deferral = arguments.GetDeferral();
            arguments.Cancel = true;
            try
            {
                var account = Selected();
                if (account is null || busy) return;
                operation = ExecuteAsync(async cancellationToken =>
                {
                    status.Text = account.HasSignature ? "正在验证签名…" : "正在验证登录…";
                    var verified = await account.VerifyAsync(security.Password, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    document.Account = verified;
                    await ApplyExchangeAsync(document);
                    imported = true;
                }, !account.HasSignature);
                await operation;
                arguments.Cancel = !imported;
            }
            finally { deferral.Complete(); }
        };
        dialog.Closing += (_, _) => stop.Cancel();
        SelectAccount();
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally
        {
            stop.Cancel();
            await operation;
            _accountImportStop = null;
        }
        if (result == ContentDialogResult.Secondary) await ShowJsonAsync(document.ToJson(), "导入 JSON");

        AccountCandidate? Selected() => (accountPicker.SelectedItem as ComboBoxItem)?.Tag as AccountCandidate;
        void SelectAccount()
        {
            security.Password = Selected()?.Security ?? "";
            status.Text = Selected() is { } account ? account.HasSignature ? "填写或提取 security，验证后保存。"
                : "此 JSON 没有抓包签名，将通过登录验证。" : "选择要导入的账号。";
            UpdateControls();
        }
        void UpdateControls()
        {
            dialog.IsPrimaryButtonEnabled = !busy && Selected() is not null && !string.IsNullOrWhiteSpace(security.Password);
            dialog.IsSecondaryButtonEnabled = !busy;
            foreach (var input in inputs) input.IsEnabled = !busy;
        }
        async Task RefreshDevicesAsync(CancellationToken cancellationToken)
        {
            status.Text = "正在查找手机…";
            var selected = (devices.SelectedItem as ComboBoxItem)?.Tag as AdbDevice;
            var found = await adb.DevicesAsync(cancellationToken);
            devices.Items.Clear();
            foreach (var device in found) devices.Items.Add(new ComboBoxItem { Content = device.Label, Tag = device });
            devices.SelectedItem = devices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ((AdbDevice)item.Tag).Serial == selected?.Serial);
            if (devices.SelectedItem is null && found.Count == 1) devices.SelectedIndex = 0;
            status.Text = found.Count == 0 ? "未找到手机。请连接 USB，或展开 Wi-Fi 调试。" : "选择手机后点击「提取 security」。";
        }
        void Start(Func<CancellationToken, Task> action, bool pauseReceiver = false)
        {
            if (!busy && !stop.IsCancellationRequested) operation = ExecuteAsync(action, pauseReceiver);
        }
        async Task ExecuteAsync(Func<CancellationToken, Task> action, bool pauseReceiver)
        {
            busy = true;
            _accountWorking = pauseReceiver;
            UpdateControls();
            var resume = pauseReceiver && _backend.IsRunning;
            try
            {
                if (pauseReceiver) await _backend.StopAsync();
                await action(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                AppErrors.Record(_paths.Data, error);
                status.Text = error.Message;
            }
            finally
            {
                if (resume && !imported && !_closing)
                {
                    try { await _backend.StartAsync(_settings.HeartbeatSeconds, _settings.ReconnectSeconds); }
                    catch (Exception error) { ReportError(error); }
                }
                _accountWorking = false;
                busy = false;
                UpdateControls();
            }
        }
    }
}

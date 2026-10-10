using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiPushDesk.Core;
using MiPushDesk.Services;
using Windows.ApplicationModel.DataTransfer;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private async Task ShowJsonAsync(string text = "", string title = "JSON")
    {
        ExchangeDocument? pendingAccount = null;
        var editor = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Text = text,
            FontFamily = new FontFamily("Consolas"), FontSize = 13, Height = 340,
            PlaceholderText = "粘贴 JSON，或打开文件", Padding = new(12)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(editor, "JsonEditor");
        var summary = Ui.Wrap("mipush-desk / 1", 12);
        AutomationProperties.SetAutomationId(summary, "JsonSummary");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = title,
            PrimaryButtonText = "导入", CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 820d;
        var toolbar = Ui.Row(6,
            Ui.Button("粘贴", () => Execute(async () => { editor.Text = await Clipboard.GetContent().GetTextAsync(); Analyze(); }), "DeskButton", id: "JsonPaste"),
            Ui.Button("打开", () => Execute(async () => { if (await PickAsync(".json") is { } file) { editor.Text = _imports.ReadText(file); Analyze(); } }), "DeskButton", id: "JsonOpen"),
            Ui.Button("分析", () => Execute(() => { Analyze(); return Task.CompletedTask; }), "DeskQuiet", id: "JsonAnalyze"),
            Ui.Button("格式化", () => Execute(() =>
            {
                using var document = JsonDocument.Parse(editor.Text, new JsonDocumentOptions { MaxDepth = JsonData.Options.MaxDepth });
                editor.Text = JsonSerializer.Serialize(document.RootElement, JsonData.Options);
                return Task.CompletedTask;
            }), "DeskQuiet", id: "JsonFormat"),
            Ui.Button("复制", () => Execute(() => { CopyText(editor.Text); summary.Text = "已复制"; return Task.CompletedTask; }), "DeskQuiet", id: "JsonCopy"),
            Ui.Button("导出", () => Execute(async () =>
            {
                var document = ExchangeDocument.ParseImport(editor.Text);
                if (await SavePathAsync("mipush", ".json") is { } file) { AtomicFile.Write(file, document.ToJson()); summary.Text = "已导出"; }
            }), "DeskButton", id: "JsonExport"));
        var content = Ui.Stack(12, toolbar, summary, editor);
        content.Width = 680;
        dialog.Content = content;
        dialog.PrimaryButtonClick += async (_, arguments) =>
        {
            var deferral = arguments.GetDeferral();
            try
            {
                var document = ExchangeDocument.ParseImport(editor.Text);
                if (AccountCandidate.From(document).Count > 0) pendingAccount = document;
                else await ApplyExchangeAsync(document);
            }
            catch (Exception error) { arguments.Cancel = true; ShowError(error); }
            finally { deferral.Complete(); }
        };
        if (text.Length > 0)
        {
            try { Analyze(); }
            catch (Exception error) { ShowError(error); }
        }
        await dialog.ShowAsync();
        if (pendingAccount is not null) await ConfirmImportAsync(pendingAccount);

        void Analyze() => summary.Text = ExchangeDocument.ParseImport(editor.Text).Summary();
        void ShowError(Exception error)
        {
            AppErrors.Record(_paths.Data, error);
            summary.Text = error is JsonException jsonError
                ? $"JSON 错误：第 {jsonError.LineNumber + 1} 行，第 {jsonError.BytePositionInLine + 1} 列。"
                : AppErrors.Describe(error);
        }
        async void Execute(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { ShowError(error); }
        }
    }
    private Task ImportSettingsAsync() => QuickImportAsync();
    private Task ImportSessionAsync() => QuickImportAsync();
    private async Task ImportFileAsync(string file)
    {
        var document = await Task.Run(() => ExchangeDocument.ParseImport(_imports.ReadText(file)));
        await ConfirmImportAsync(document, Path.GetFileName(file));
    }
    private async Task ConfirmImportAsync(ExchangeDocument document, string name = "")
    {
        var accounts = AccountCandidate.From(document);
        if (accounts.Count > 0) { await CompleteAccountAsync(document, accounts, name); return; }
        var summary = Ui.Wrap(document.Summary(), 14);
        AutomationProperties.SetAutomationId(summary, "ImportSummary");
        var guidance = Ui.Wrap(document.Account is not null ? "导入后使用此账号接收通知。"
            : document.Analysis is not null ? _store.HasAccount ? "抓包分析会保存，继续使用当前账号。"
            : "未捕获完整登录信息。请重新抓包，导入后可补全 security。" : "导入文件中的数据。", 13);
        AutomationProperties.SetAutomationId(guidance, "ImportGuidance");
        var errorText = Ui.Wrap("", 12);
        errorText.Visibility = Visibility.Collapsed;
        var analysisOnly = document.Analysis is not null && document.Account is null && document.AppCredentials is null
            && document.Settings is null && document.Icons is null && document.Notifications is null;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "导入 JSON",
            PrimaryButtonText = analysisOnly ? "保存分析" : "导入", SecondaryButtonText = "查看 JSON", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = Ui.Stack(14, Ui.Wrap(name, 12), summary, guidance, errorText)
        };
        dialog.PrimaryButtonClick += async (_, arguments) =>
        {
            var deferral = arguments.GetDeferral();
            try { await ApplyExchangeAsync(document); }
            catch (Exception error)
            {
                arguments.Cancel = true;
                AppErrors.Record(_paths.Data, error);
                errorText.Text = AppErrors.Describe(error);
                errorText.Visibility = Visibility.Visible;
            }
            finally { deferral.Complete(); }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Secondary) await ShowJsonAsync(document.ToJson(), "导入 JSON");
    }
    private Task ShowSessionAsync()
    {
        var analysisFile = Path.Combine(_paths.Data, "analysis.json");
        var credentials = _store.ReadAppCredentials();
        var session = new ExchangeDocument
        {
            Account = _store.HasAccount ? JsonSerializer.Deserialize<JsonElement>(_store.ReadAccount()) : null,
            AppCredentials = credentials.Count > 0 ? credentials : null,
            Analysis = File.Exists(analysisFile) ? ExchangeDocument.Parse(File.ReadAllText(analysisFile)).Analysis : null
        };
        return ShowJsonAsync(session.Account is not null || session.Analysis is not null || session.AppCredentials is not null ? session.ToJson() : "", "会话");
    }

    private async Task ApplyExchangeAsync(ExchangeDocument document)
    {
        var restart = document.Account is not null || document.AppCredentials is not null;
        var wasRunning = _backend.IsRunning;
        if (restart) await _backend.StopAsync();
        try { _settings = _imports.Apply(document, _settings, _store); }
        catch
        {
            if (wasRunning) await _backend.StartAsync(_settings.HeartbeatSeconds, _settings.ReconnectSeconds);
            throw;
        }
        if (document.Notifications is { } incoming)
        {
            var imported = _importedNotifications.Merge(incoming);
            _feed.Restore(incoming.Select(record => record.Key));
            foreach (var record in imported) _read.Add(record.Key);
            SaveRead();
            var merged = _records.Concat(imported).DistinctBy(record => record.Key)
                .OrderBy(record => record.ReceivedAt).ToList();
            _records.Clear();
            foreach (var record in merged) NotificationTimeline.Apply(_records, record, DateTimeOffset.UtcNow);
        }
        if (restart)
        {
            if (_settings.AutoConnect && !_renderMode) await _backend.StartAsync(_settings.HeartbeatSeconds, _settings.ReconnectSeconds);
        }
        _backend.UpdateTiming(_settings.HeartbeatSeconds, _settings.ReconnectSeconds);
        SaveSettings(true);
        WarmAppMetadata(AppCatalog.Packages(_settings).Concat(_records.Select(record => record.Package)));
        UpdateStatus();
        Notify(document.Account is not null ? "账号已导入" : document.Analysis is not null
            ? _store.HasAccount ? "抓包分析已保存，继续使用当前账号。" : "抓包分析已保存；补全通道密钥后才能连接。"
            : "已导入 · " + document.Summary());
    }
}

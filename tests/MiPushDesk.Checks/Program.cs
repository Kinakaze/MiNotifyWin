using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using MiPushDesk.Core;

var directory = Path.Combine(Path.GetTempPath(), "MiPushDesk-Checks-" + Guid.NewGuid().ToString("N"));
var paths = new AppPaths(directory);
var checks = new List<string>();
try
{
    checks.AddRange(await ProtocolChecks.RunAsync(directory));
    checks.AddRange(await AppSecretRecoveryChecks.RunAsync(directory));
    checks.AddRange(await FeatureChecks.RunAsync(directory, args.Contains("--metadata-online")));
    Check("Empty COM errors include a code without logging message contents", () =>
    {
        var error = new COMException("", unchecked((int)0x80004005));
        Assert(AppErrors.Describe(error).Contains("操作失败") && AppErrors.Describe(error).Contains("0x80004005"), "Empty COM error has no useful description");
        AppErrors.Record(directory, new InvalidOperationException("private-notification-text", error));
        var log = File.ReadAllText(Path.Combine(directory, "ui-errors.jsonl"));
        Assert(log.Contains("0x80004005") && log.Contains("COMException"), "Diagnostic context missing");
        Assert(!log.Contains("private-notification-text"), "Error log included message contents");
    });
    var store = new AppStore(paths);
    var importer = new ImportService(paths);
    Check("Shared Android, settings and notification examples use the same format", () =>
    {
        var examples = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "examples"), "*.json");
        Assert(examples.Length == 3, "Shared format examples were not included");
        foreach (var file in examples)
        {
            var document = importer.Read(file);
            Assert(document.Account is null && document.Summary().Length > 0, "Example included real login data or no sections");
            ExchangeDocument.Parse(document.ToJson());
        }
        Assert(importer.Read(Path.Combine(AppContext.BaseDirectory, "examples", "notifications.json")).ToJson().Contains("示例通知"),
            "JSON escaped readable Chinese text");
    });
    var account = JsonSerializer.Serialize(new { uuid = "123456789@xiaomi.com/test-resource", token = "test-token-only",
        security = Convert.ToBase64String(Encoding.UTF8.GetBytes("local-testing-secret")), device_uuid = "test-device" });
    Check("DPAPI account roundtrip and no plaintext", () =>
    {
        store.SaveAccount(account);
        Assert(store.ReadAccount() == AppStore.ValidateAccount(account), "Credential roundtrip failed");
        Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(paths.Account)).Contains("test-token-only"), "Token stored in plaintext");
    });
    Check("Malformed account rejected without replacing existing", () =>
    {
        var before = File.ReadAllBytes(paths.Account);
        Expect<InvalidDataException>(() => store.SaveAccount("{\"security\":\"invalid\"}"));
        Assert(before.SequenceEqual(File.ReadAllBytes(paths.Account)), "Invalid account changed credentials");
    });
    Check("Standard JSON preserves appearance and local startup preferences", () =>
    {
        var settings = new DeskSettings { AutoConnect = false, CloseToTray = false };
        Assert(settings.Appearance.Theme == "paper", "New configuration must use light colors");
        Assert(!settings.Appearance.ShowImages, "New configuration must not include notification images");
        settings.Appearance.Colors = Palettes.For(settings.Appearance) with { Accent = "#3478B8", Background = "#FAFBFC" };
        store.Save(settings);
        var file = Path.Combine(directory, "settings-export.json");
        AtomicFile.Write(file, importer.ExportSettings(settings).ToJson());
        var document = importer.Read(file);
        document.Settings!.AutoConnect = document.Settings.CloseToTray = true;
        var imported = importer.Apply(document, settings, store);
        Assert(imported.Appearance.Theme == "paper" && !imported.AutoConnect && !imported.CloseToTray, "Import changed machine settings");
        Assert(imported.Appearance.Colors == settings.Appearance.Colors && store.Read().Appearance.Colors == settings.Appearance.Colors, "Custom colors were lost");
        Assert(!File.ReadAllText(file).Contains("test-token-only"), "Configuration leaked credentials");
    });
    Check("Invalid appearance rejected", () =>
    {
        var settings = new DeskSettings(); settings.Appearance.FontSize = double.NaN;
        Expect<InvalidDataException>(() => AppStore.Validate(settings));
        settings.Appearance.FontSize = 14; settings.Appearance.Theme = "unknown";
        Expect<InvalidDataException>(() => AppStore.Validate(settings));
        settings.Appearance.Theme = "paper";
        settings.Appearance.Colors = new() { Accent = "#12345Z" };
        Assert(!Palettes.ValidColor("#123456\n"), "Color with trailing newline accepted");
        var before = File.ReadAllBytes(paths.Settings);
        Expect<InvalidDataException>(() => store.Save(settings));
        Assert(before.SequenceEqual(File.ReadAllBytes(paths.Settings)), "Invalid color replaced saved configuration");
    });
    Check("Palette switching preserves other display preferences", () =>
    {
        var profile = new DisplayProfile { Theme = "ocean", Colors = new() { Accent = "#123456" },
            Layout = "conversation", FontSize = 18, CornerRadius = 16, ToastStyle = "compact" };
        Palettes.Select(profile, "paper");
        Assert(profile.Colors is null && profile.Theme == "paper" && !Palettes.IsDark(Palettes.For(profile).Background), "Palette selection did not reset custom colors");
        Assert(profile.Layout == "conversation" && profile.FontSize == 18 && profile.CornerRadius == 16 && profile.ToastStyle == "compact", "Changing colors reset display preferences");
        foreach (var palette in Palettes.Presets.Values) Palettes.Validate(palette);
        Assert(Palettes.IsDark(Palettes.Presets["midnight"].Background), "Dark palette uses light controls");
    });
    Check("Quiet hours across midnight and app mute", () =>
    {
        var settings = new DeskSettings { QuietHoursEnabled = true, QuietStartHour = 23, QuietEndHour = 8 };
        Assert(!settings.ShouldNotify("com.example.test", new DateTimeOffset(DateTime.Today.AddHours(1))), "Night not muted");
        Assert(settings.ShouldNotify("com.example.test", new DateTimeOffset(DateTime.Today.AddHours(12))), "Day muted");
        settings.Apps["com.example.test"] = new() { Muted = true };
        Assert(!settings.ShouldNotify("com.example.test", new DateTimeOffset(DateTime.Today.AddHours(12))), "App mute ignored");
    });
    Check("JSONL partial UTF8 records and duplicate delivery", () =>
    {
        var file = Path.Combine(directory, "feed.jsonl");
        var record = new PushRecord { Key = "one", Package = "com.example.test", Title = "通知", Description = "长文本", IsNotification = true };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record));
        File.WriteAllBytes(file, bytes[..(bytes.Length - 2)]);
        var feed = new NotificationFeed(file);
        Assert(feed.Read().Count == 0, "Incomplete record consumed");
        using (var stream = new FileStream(file, FileMode.Append)) { stream.Write(bytes[^2..]); stream.WriteByte(10); }
        Assert(feed.Read().Single().Title == "通知", "UTF8 record lost");
        File.AppendAllText(file, JsonSerializer.Serialize(record) + "\n");
        Assert(feed.Read().Count == 0, "Duplicate delivered");
    });
    Check("Clearing history persists across restart and accepts new notifications", () =>
    {
        var file = Path.Combine(directory, "clear-feed.jsonl");
        var previous = new PushRecord { Key = "before-clear", Package = "com.example.test", Title = "旧通知",
            ReceivedAt = DateTimeOffset.Now.AddMinutes(-1), IsNotification = true };
        File.WriteAllText(file, JsonSerializer.Serialize(previous) + "\n");
        var feed = new NotificationFeed(file);
        var pending = feed.Read().Single();
        feed.Clear();
        Assert(!feed.Includes(pending), "A read completed before clearing can repopulate the inbox");
        var restarted = new NotificationFeed(file);
        Assert(restarted.Read(true).Count == 0, "Cleared history returned after restart");
        var next = new PushRecord { Key = "after-clear", Package = "com.example.test", Title = "新通知",
            ReceivedAt = DateTimeOffset.Now.AddMilliseconds(1), IsNotification = true };
        File.AppendAllText(file, JsonSerializer.Serialize(next) + "\n");
        Assert(feed.Read().Single().Key == next.Key, "Clearing prevented future delivery");
        Assert(new NotificationFeed(file).Read(true).Single().Key == next.Key, "Restart did not preserve only new notifications");
    });
    Check("Rich toast XML escapes text and restricts action schemes", () =>
    {
        var record = new PushRecord { Key = "test", Title = "标题 <&\"", Description = "多行\n详情", Package = "com.example.test",
            Extra = new() { ["notification_style_button_left_name"] = "网页 & 查看", ["notification_style_button_left_web_uri"] = "https://example.com/?a=1&b=2",
                ["notification_style_button_right_name"] = "invalid", ["notification_style_button_right_web_uri"] = "file:///C:/Windows/test.exe" } };
        var imagePath = Path.Combine(directory, "image.png");
        var plain = XDocument.Parse(RichNotification.BuildToast(record, new(), "应用", imagePath: imagePath));
        Assert(!plain.Descendants("image").Any(), "Default notification includes an image");
        var document = XDocument.Parse(RichNotification.BuildToast(record, new() { ShowImages = true }, "应用", imagePath: imagePath));
        Assert(document.Descendants("text").First().Value == record.Title, "Text was not preserved");
        Assert(document.Descendants("action").Count() == 1, "Unsafe action included");
        Assert(document.Descendants("image").Any(image => image.Attribute("placement")?.Value == "hero"), "Hero missing");
        Assert(RichNotification.WebUrl("https://user:password@example.com/") is null, "Userinfo accepted");
        Assert(RichNotification.WebUrl("javascript:alert(1)") is null, "Script accepted");
        var preview = XDocument.Parse(RichNotification.BuildToast(record, new(), "应用", foregroundAction: "查看详情"));
        var foreground = preview.Descendants("action").Single(action => action.Attribute("activationType")?.Value == "foreground");
        Assert(foreground.Attribute("arguments")?.Value == preview.Root!.Attribute("launch")?.Value, "Test action does not activate the notification");
    });
    Check("Standard JSON rejects missing schema, versions, unknown fields and incomplete accounts", () =>
    {
        Expect<JsonException>(() => ExchangeDocument.Parse(account));
        Expect<JsonException>(() => ExchangeDocument.Parse("{\"schema\":\"mipush-desk\",\"analysis\":{}}"));
        Expect<InvalidDataException>(() => ExchangeDocument.Parse("{\"schema\":\"mipush-desk\",\"version\":2,\"analysis\":{}}"));
        Expect<JsonException>(() => ExchangeDocument.Parse("{\"schema\":\"mipush-desk\",\"version\":1,\"unknown\":{},\"analysis\":{}}"));
        Expect<InvalidDataException>(() => ExchangeDocument.Parse("{\"schema\":\"mipush-desk\",\"version\":1}"));
        Expect<InvalidDataException>(() => ExchangeDocument.Parse("{\"schema\":\"mipush-desk\",\"version\":1,\"account\":{\"uuid\":\"test\"}}"));
        Expect<InvalidDataException>(() => ExchangeDocument.Parse("{\"schema\":\"mipush-desk\",\"version\":1,\"notifications\":[{}]}"));
    });
    Check("Android session analysis remains incomplete and does not replace credentials", () =>
    {
        var before = File.ReadAllBytes(paths.Account);
        var document = ExchangeDocument.Parse("""
            {"schema":"mipush-desk","version":1,"exportedAt":"2026-10-09T12:00:00Z",
                "analysis":{"credentials":{"uuid":"123456789@xiaomi.com/test-resource","token":"test-token-only","security":null},"sessions":[]}}
            """);
        Assert(document.Account is null && document.Summary().Contains("尚缺 security"), "Partial session reported a complete account");
        importer.Apply(document, store.Read(), store);
        Assert(before.SequenceEqual(File.ReadAllBytes(paths.Account)), "Analysis replaced the current credentials");
        Assert(importer.Read(Path.Combine(paths.Data, "analysis.json")).Analysis.HasValue, "Analysis was not saved in standard format");
        var full = ExchangeDocument.Parse(new ExchangeDocument { Account = JsonSerializer.Deserialize<JsonElement>(account) }.ToJson());
        Assert(full.Summary() == "账号完整", "Complete account was not recognized");
        var restoredPaths = new AppPaths(Path.Combine(directory, "account-import"));
        var restoredStore = new AppStore(restoredPaths);
        new ImportService(restoredPaths).Apply(full, new(), restoredStore);
        Assert(restoredStore.ReadAccount() == AppStore.ValidateAccount(account), "Standard account import lost fields");
    });
    Check("Embedded icons roundtrip and reject mismatched content before applying", () =>
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1kAAAAASUVORK5CYII=");
        var settings = new DeskSettings();
        settings.Apps["com.example.test"] = new() { Name = "测试图标", Icon = importer.StoreIcon(png) };
        var document = ExchangeDocument.Parse(importer.ExportSettings(settings).ToJson());
        var newPaths = new AppPaths(Path.Combine(directory, "icon-import"));
        var imported = new ImportService(newPaths).Apply(document, new(), new AppStore(newPaths));
        Assert(imported.Apps["com.example.test"].Name == "测试图标", "Icon mapping roundtrip failed");
        Assert(File.ReadAllBytes(Path.Combine(newPaths.Icons, imported.Apps["com.example.test"].Icon!)).SequenceEqual(png), "Icon data changed");
        document.Icons = new() { ["../escape.png"] = Convert.ToBase64String(png) };
        Expect<InvalidDataException>(() => ExchangeDocument.Parse(document.ToJson()));
        document.Icons = new() { [new string('0', 64) + ".png"] = Convert.ToBase64String(png) };
        Expect<InvalidDataException>(() => ExchangeDocument.Parse(document.ToJson()));
        document.Icons = null;
        var missingPaths = new AppPaths(Path.Combine(directory, "missing-icons"));
        Expect<InvalidDataException>(() => new ImportService(missingPaths).Apply(document, new(), new AppStore(missingPaths)));
        Assert(!File.Exists(missingPaths.Settings), "Missing icon partially applied settings");
        var imageSettings = new DeskSettings { Appearance = new() { ShowImages = true, DefaultImage = importer.StoreIcon(png) } };
        var imageDocument = ExchangeDocument.Parse(importer.ExportSettings(imageSettings).ToJson());
        Assert(imageDocument.Icons!.Count == 1, "Default notification image was not exported");
        var imagePaths = new AppPaths(Path.Combine(directory, "default-image-import"));
        var imageImport = new ImportService(imagePaths).Apply(imageDocument, new(), new AppStore(imagePaths));
        Assert(File.ReadAllBytes(Path.Combine(imagePaths.Icons, imageImport.Appearance.DefaultImage!)).SequenceEqual(png),
            "Default notification image did not survive settings transfer");
    });
    Check("Imported notifications persist separately from the live feed and clear independently", () =>
    {
        var importedPaths = new AppPaths(Path.Combine(directory, "notification-import"));
        var live = Path.Combine(importedPaths.Listener, "notifications.jsonl");
        var record = new PushRecord { Key = "shared-history", Package = "com.example.test", Title = "Imported",
            ReceivedAt = DateTimeOffset.Now.AddDays(-1), IsNotification = true };
        var liveText = JsonSerializer.Serialize(record) + "\n";
        AtomicFile.Write(live, liveText);
        var imported = new ImportedNotifications(importedPaths);
        imported.Merge([record, record]);
        Assert(new ImportedNotifications(importedPaths).Read().Count == 1, "Imported history duplicated or did not persist");
        Assert(File.ReadAllText(live) == liveText, "Import modified the live receiver file");
        new NotificationFeed(live).Clear();
        imported.Clear();
        Assert(imported.Read().Count == 0 && new NotificationFeed(live).Read(true).Count == 0, "Clear left imported history");
        imported.Merge([record]);
        Assert(imported.Read().Single().Key == record.Key, "Explicit reimport after clearing lost old records");
    });
    Check("Startup registration and removal in isolated registry key", () =>
    {
        var key = @"Software\MiPushDesk\Checks\" + Guid.NewGuid().ToString("N");
        try
        {
            var startup = new StartupService(@"C:\Program Files\MiPush Desk\MiPushDesk.exe", key);
            Assert(startup.Command == "\"C:\\Program Files\\MiPush Desk\\MiPushDesk.exe\" --tray", "Startup command quoting wrong");
            startup.SetEnabled(true); Assert(startup.IsEnabled, "Startup registration failed");
            startup.SetEnabled(false); Assert(!startup.IsEnabled, "Startup removal failed");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    });
    Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks }, JsonData.Options));
}
finally { Directory.Delete(directory, true); }

void Check(string name, Action action) { action(); checks.Add(name); }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Expect<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

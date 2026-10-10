using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MiPushDesk.Core;

internal static class FeatureChecks
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1kAAAAASUVORK5CYII=");
    public static async Task<IReadOnlyList<string>> RunAsync(string directory, bool online)
    {
        var checks = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var records = new List<PushRecord>();
        var first = Message("first", now.AddMinutes(-4)); first.NotifyId = 9;
        var anotherApp = Message("another-app", now.AddMinutes(-3)); anotherApp.Package = "com.example.other"; anotherApp.NotifyId = 9;
        NotificationTimeline.Apply(records, first, now); NotificationTimeline.Apply(records, anotherApp, now);
        var replacement = Message("replacement", now.AddMinutes(-2)); replacement.NotifyId = 9;
        Assert(NotificationTimeline.Apply(records, replacement, now).Single() == first && records.Count == 2, "Notify ID must replace only its package");
        var replay = Message("first", first.ReceivedAt); replay.NotifyId = 9; replay.Replayed = true;
        NotificationTimeline.Apply(records, replay, now);
        Assert(records[0] == replacement, "Decrypt replay resurrected a replaced message");
        var clear = new PushRecord { Key = "clear", Package = first.Package, Operation = "clear", ReceivedAt = now.AddMinutes(-1),
            ControlExtra = new() { ["notifyId"] = "-1" } };
        Assert(NotificationTimeline.Apply(records, clear, now).Single() == replacement && records.Single() == anotherApp, "Clear crossed package boundary");
        replacement.Replayed = true; NotificationTimeline.Apply(records, replacement, now);
        Assert(records.Single() == anotherApp, "Decrypt replay resurrected a cleared message");
        var collapse = Message("collapse-one", now); collapse.CollapseKey = "thread";
        var collapsed = Message("collapse-two", now.AddSeconds(1)); collapsed.CollapseKey = "thread";
        NotificationTimeline.Apply(records, collapse, now);
        Assert(NotificationTimeline.Apply(records, collapsed, now).Single() == collapse, "Collapse key not honored");
        collapsed.ExpiresAt = now.AddSeconds(2);
        Assert(NotificationTimeline.Expire(records, now.AddSeconds(3)).Single() == collapsed, "Live expiry did not remove message");
        checks.Add("Notification replacement, package-scoped clear, replay and live expiration");

        var history = Path.Combine(directory, "timeline.jsonl");
        first.BodyStatus = "encrypted";
        replay.BodyStatus = "decoded";
        File.WriteAllText(history, string.Join('\n', new[] { first, clear, replay }.Select(record => JsonSerializer.Serialize(record, JsonData.Options).Replace("\r", "").Replace("\n", ""))) + "\n");
        records.Clear();
        foreach (var record in new NotificationFeed(history).Read(true)) NotificationTimeline.Apply(records, record, now);
        Assert(records.Count == 0, "Restart brought back a cleared encrypted message");
        clear.ControlExtra = new() { ["msgId"] = first.Key };
        Assert(NotificationTimeline.MatchesClear(first, clear), "Message ID clear missing");
        clear.ControlExtra = new() { ["title"] = "tes", ["description"] = "bod" };
        Assert(NotificationTimeline.MatchesClear(first, clear), "Title and description clear missing");
        checks.Add("Notification journal restart preserves clears and selectors");

        var deletedFile = Path.Combine(directory, "deleted-feed.jsonl");
        var deletedRecord = Message("delete-me", now);
        var retainedRecord = Message("keep-me", now.AddSeconds(1));
        File.WriteAllText(deletedFile, JsonSerializer.Serialize(deletedRecord) + "\n" + JsonSerializer.Serialize(retainedRecord) + "\n");
        var deletedFeed = new NotificationFeed(deletedFile);
        var pendingRecord = deletedFeed.Read().First();
        deletedFeed.Delete(deletedRecord.Key);
        Assert(!deletedFeed.Includes(pendingRecord) && new NotificationFeed(deletedFile).Read(true).Single().Key == retainedRecord.Key,
            "Deleting one notification returned after restart or removed its neighbor");
        var deletionPaths = new AppPaths(Path.Combine(directory, "deleted-import"));
        var importedHistory = new ImportedNotifications(deletionPaths);
        importedHistory.Merge([deletedRecord, retainedRecord]); importedHistory.Delete(deletedRecord.Key);
        Assert(new ImportedNotifications(deletionPaths).Read().Single().Key == retainedRecord.Key, "Deleted imported history returned after restart");
        deletedFeed.Restore([deletedRecord.Key]);
        Assert(new NotificationFeed(deletedFile).Read(true).Count == 2, "Explicit reimport cannot restore a deleted notification");
        checks.Add("Single notification deletion persists independently of neighbors and can be explicitly restored");

        var paths = new AppPaths(Path.Combine(directory, "features"));
        var store = new AppStore(paths);
        var importer = new ImportService(paths);
        Assert(new DeskSettings().UseXiaomiMetadata, "Xiaomi metadata must be enabled by default");
        using (var builtIn = new BuiltInIconLibrary(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcons", "delta.zip"), paths))
        {
            var common = new[] { "com.tencent.mobileqq", "com.tencent.mm", "tv.danmaku.bili", "com.dragon.read", "ctrip.android.view",
                "com.sankuai.meituan", "com.xunmeng.pinduoduo", "com.zhihu.android", "com.tencent.wework", "com.alibaba.android.rimet",
                "com.jingdong.app.mall", "com.douban.frodo", "com.xiaomi.vipaccount", "org.telegram.messenger" };
            Assert(builtIn.Count > 14000 && common.All(builtIn.Contains), "Built-in library is missing common application mappings");
            Assert(common.All(package => File.Exists(builtIn.Icon(package))), "Built-in icon could not be read from the packed library");
            Assert(builtIn.Icon("com.example.not.in.pack") is null, "Unknown package matched an unrelated icon");
        }
        checks.Add("Bundled library covers common Chinese and international applications with over 14000 mappings");
        var secret = Convert.ToBase64String("0123456789abcdef"u8);
        var credentials = new Dictionary<string, AppCredential> { [first.Package] = new() { AppId = "synthetic-app", RegId = "synthetic-registration", RegSecret = secret } };
        importer.Apply(ExchangeDocument.Parse(new ExchangeDocument { AppCredentials = credentials }.ToJson()), new(), store);
        Assert(store.ReadAppCredentials()[first.Package].RegSecret == secret && AppCatalog.Packages(store.Read()).Contains(first.Package), "Credential import lost registration or explicit app rule");
        var settingsJson = importer.ExportSettings(store.Read()).ToJson();
        Assert(!settingsJson.Contains(secret) && !settingsJson.Contains("synthetic-registration"), "Ordinary settings export included app secrets");
        first.AdditionalData = new() { ["future_protocol_field"] = JsonSerializer.SerializeToElement(new { nested = new[] { 1, 2, 3 }, text = "扩展" }) };
        first.Body = new() { ["unknown"] = new Dictionary<string, object?> { ["list"] = new object[] { true, -99L, "payload" } } };
        var importedMessage = ExchangeDocument.Parse(new ExchangeDocument { Notifications = [first] }.ToJson()).Notifications!.Single();
        Assert(importedMessage.AdditionalData!["future_protocol_field"].GetProperty("text").GetString() == "扩展"
            && JsonSerializer.Serialize(importedMessage.Body).Contains("payload"), "Notification JSON lost unknown body or envelope fields");
        checks.Add("Explicit credential exchange stays separate from settings and preserves unknown push fields");

        var zip = Path.Combine(directory, "icons.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Entry(archive, "assets/appfilter.xml", Encoding.UTF8.GetBytes("""
                <resources><item component="ComponentInfo{com.example.library/.First}" drawable="test"/>
                <item component="ComponentInfo{com.example.library/.Second}" drawable="missing"/>
                <item component="ComponentInfo{com.example.unavailable/.Main}" drawable="missing"/></resources>
                """));
            Entry(archive, "res/drawable-hdpi/test.png", [1, 2, 3]);
            Entry(archive, "res/drawable-xxxhdpi/test.png", Png);
        }
        var pack = IconPack.Read(zip);
        Assert(pack.Icons.Single().Value.SequenceEqual(Png) && pack.Unavailable == 1, "Icon pack package mapping or density preference failed");
        var settings = store.Read();
        var managed = AppCatalog.Packages(settings).ToArray();
        settings.LibraryIcons = pack.Icons.ToDictionary(pair => pair.Key, pair => importer.StoreIcon(pair.Value));
        Assert(managed.SequenceEqual(AppCatalog.Packages(settings)), "Imported icon library filled application management page");
        var importedPaths = new AppPaths(Path.Combine(directory, "library-import"));
        var importedSettings = new ImportService(importedPaths).Apply(ExchangeDocument.Parse(importer.ExportSettings(settings).ToJson()), new(), new(importedPaths));
        Assert(File.ReadAllBytes(Path.Combine(importedPaths.Icons, importedSettings.LibraryIcons["com.example.library"])).SequenceEqual(Png), "Library JSON export lost icon bytes");
        var badZip = Path.Combine(directory, "invalid-icons.zip");
        using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create)) Entry(archive, "appfilter.xml", "<!DOCTYPE resources [<!ENTITY test SYSTEM 'file:///missing'>]><resources>&test;</resources>"u8.ToArray());
        Expect<System.Xml.XmlException>(() => IconPack.Read(badZip));
        checks.Add("Standard launcher icon ZIPs, density selection, isolated library and portable JSON");

        var calls = 0;
        var fail = false;
        var redirectToHome = false;
        var handler = new StubHttp(async (request, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20, cancellation);
            if (fail) return new(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.Host == "icons.example.com") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            if (request.RequestUri.Query.Contains("com.example.missing")) return new(HttpStatusCode.NotFound);
            if (redirectToHome || request.RequestUri.Query.Contains("com.example.redirected")) return new(HttpStatusCode.OK)
            {
                RequestMessage = new(HttpMethod.Get, "https://app.mi.com/"),
                Content = new StringContent("<title>小米应用商店</title><h3>推荐应用</h3>")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent("<div class=\"app-info\"><img src=\"http://icons.example.com/app.png\"><h3>测试 &amp; 应用</h3></div>") };
        });
        using (var metadata = new AppMetadataService(paths, handler))
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => metadata.EnsureAsync("com.example.cache")));
            Assert(calls == 2 && metadata.Count == 1 && metadata.Get("com.example.cache")!.Name == "测试 & 应用", "Metadata lookup was not coalesced or parsed");
            await metadata.EnsureAsync("com.example.cache"); Assert(calls == 2, "Cache TTL ignored");
            fail = true; await metadata.EnsureAsync("com.example.cache", true);
            Assert(metadata.Get("com.example.cache")!.Name == "测试 & 应用", "Refresh failure erased cached name");
            fail = false;
            await metadata.EnsureAsync("com.example.missing"); await metadata.EnsureAsync("com.example.missing");
            Assert(calls == 4 && metadata.Get("com.example.missing")!.Status == "not_found", "Negative metadata cache ignored");
            await metadata.EnsureAsync("com.example.redirected"); await metadata.EnsureAsync("com.example.redirected");
            Assert(calls == 5 && metadata.Get("com.example.redirected") is { Status: "not_found", Name.Length: 0, Icon: null },
                "Unlisted application redirect was not cached as missing");
            var previousIcon = metadata.IconPath("com.example.cache");
            redirectToHome = true;
            await metadata.EnsureAsync("com.example.cache", true);
            Assert(calls == 6 && metadata.Get("com.example.cache")!.Name == "测试 & 应用" && metadata.IconPath("com.example.cache") == previousIcon,
                "Store redirect erased an existing cached name or icon");
        }
        checks.Add("Xiaomi unlisted application redirects are cached without replacing existing metadata");
        using (var metadata = new AppMetadataService(paths, new StubHttp((_, _) => throw new InvalidOperationException("Cache read made a network request"))))
        {
            await metadata.EnsureAsync("com.example.cache");
            var iconPath = metadata.IconPath("com.example.cache");
            Assert(File.Exists(iconPath), "Persisted metadata did not reopen");
            metadata.Clear(); Assert(metadata.Get("com.example.cache") is null && !File.Exists(iconPath), "Metadata clear left cached name or icon");
        }
        var cacheFile = Path.Combine(paths.Data, "application-cache.json");
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(new Dictionary<string, CachedAppMetadata>
        { ["com.example.permanent"] = new() { Name = "永久缓存", Status = "ok", CheckedAt = now.AddYears(-5) } }, JsonData.Options));
        using (var metadata = new AppMetadataService(paths, new StubHttp((_, _) => throw new InvalidOperationException("Permanent cache tried to refresh automatically"))))
            await metadata.EnsureAsync("com.example.permanent");
        checks.Add("Xiaomi metadata stays cached without expiration and clears its own files");

        var rich = Message("rich", now);
        rich.Extra = new()
        {
            ["notification_style_type"] = "3", ["notification_banner_image_uri"] = "https://example.com/banner.png",
            ["notification_banner_icon_uri"] = "https://example.com/avatar.png", ["notification_group"] = "thread",
            ["notification_style_button_left_name"] = "Android", ["notification_style_button_left_notify_effect"] = "2",
            ["notification_style_button_left_intent_uri"] = "intent:#Intent;component=com.example.test/.Main;end"
        };
        for (var index = 1; index <= 3; index++) { rich.Extra[$"cust_btn_{index}_n"] = "Web " + index; rich.Extra[$"cust_btn_{index}_wu"] = "https://example.com/" + index; }
        rich.Extra["notification_style_button_mid_web_uri"] = "https://example.com/mid";
        rich.Extra["notification_style_button_right_web_uri"] = "https://example.com/right";
        Assert(RichNotification.ImageUrl(rich)!.EndsWith("banner.png") && RichNotification.AvatarUrl(rich)!.EndsWith("avatar.png"), "Banner image and avatar roles mixed");
        Assert(RichNotification.Actions(rich).Count == 6, "Custom actions were lost");
        var toast = XDocument.Parse(RichNotification.BuildToast(rich, new(), "Example", foregroundAction: "详情"));
        Assert(toast.Descendants("action").Count() == 5 && !toast.Descendants("action").Any(action => action.Attribute("activationType")?.Value == "protocol"
            && action.Attribute("arguments")!.Value.StartsWith("intent:")), "Windows action cap or Android action handling failed");
        Assert(RichNotification.CleanText("A😀B", 3) == "A😀" && RichNotification.CleanText("A😀B", 2) == "A", "Unicode truncation split a surrogate pair");
        rich.Extra["notification_style_type"] = "6"; Assert(RichNotification.Style(rich) == "call", "VoIP style mislabeled as text");
        checks.Add("Rich push styles preserve avatars, custom actions, grouping and Unicode");

        if (online)
        {
            using var metadata = new AppMetadataService(new AppPaths(Path.Combine(directory, "online-metadata")));
            await metadata.EnsureAsync("com.tencent.mm");
            Assert(metadata.Get("com.tencent.mm") is { Status: "ok", Name.Length: > 0, Icon.Length: > 0 }, "Xiaomi public store lookup failed: " + metadata.Get("com.tencent.mm")?.Status);
            checks.Add("Live C# Xiaomi public store name/icon lookup");
        }
        return checks;
    }
    private static PushRecord Message(string key, DateTimeOffset received) => new()
    { Key = key, MessageId = key, Package = "com.example.test", Title = "test", Description = "body", IsNotification = true, ReceivedAt = received };
    private static void Entry(ZipArchive archive, string name, byte[] bytes) { using var output = archive.CreateEntry(name).Open(); output.Write(bytes); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); } catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }
    private sealed class StubHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using MiPushDesk.Core;
using MiPushDesk.Services;
using Windows.UI.Notifications;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiPushDesk-NativeChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var owner = CreateWindowEx(0, "STATIC", "MiPush Desk native checks", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        var checks = new List<string>();
        try
        {
            Assert(owner != 0, "Owner window creation failed");
            CheckDialog("Shared JSON picker", NativeFileDialog.Open(".json"), false);
            CheckDialog("Image picker", NativeFileDialog.Open(".png", ".jpg", ".jpeg"), false);
            CheckDialog("Shared JSON export picker", NativeFileDialog.Save("测试 JSON", ".json"), true);
            CheckIconPriority(directory);
            checks.Add("Manual icons, imported library and built-in library take priority over Xiaomi cache");
            if (arguments.Contains("--toast"))
            {
                CheckNotificationsAsync(directory).GetAwaiter().GetResult();
                checks.Add("Native notification delivered without images and cleared from Windows history");
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed = checks.Count,
                elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
                checks
            }, JsonData.Options));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (owner != 0) DestroyWindow(owner);
            Directory.Delete(directory, true);
        }

        void CheckDialog(string name, NativeFileDialog picker, bool save)
        {
            using (picker)
            {
                picker.Dialog.GetOptions(out var options);
                Assert(options.HasFlag(NativeFileDialog.Options.ForceFileSystem), "Virtual files must not be returned");
                Assert(options.HasFlag(NativeFileDialog.Options.PathMustExist), "Parent folder must exist");
                Assert(options.HasFlag(NativeFileDialog.Options.NoChangeDirectory), "Dialog must preserve the working directory");
                Assert(options.HasFlag(save ? NativeFileDialog.Options.OverwritePrompt : NativeFileDialog.Options.FileMustExist), "Open/save validation missing");
                picker.Dialog.GetFileTypeIndex(out var filter);
                Assert(filter == 1, "File type filter not selected");
                if (save)
                {
                    picker.Dialog.GetFileName(out var filename);
                    try { Assert(Marshal.PtrToStringUni(filename)?.StartsWith("测试 ", StringComparison.Ordinal) == true, "Unicode filename was lost"); }
                    finally { Marshal.FreeCoTaskMem(filename); }
                }
                var events = new DialogEvents(() => picker.Dialog.Close(NativeFileDialog.Cancelled));
                var pointer = Marshal.GetComInterfaceForObject(events, typeof(IDialogEvents));
                uint cookie;
                try { picker.Dialog.Advise(pointer, out cookie); }
                finally { Marshal.Release(pointer); }
                try
                {
                    var selected = picker.Show(owner);
                    Assert(events.Opened, "Native dialog never initialized");
                    Assert(events.Error is null, "Native cancellation failed: " + events.Error);
                    Assert(selected is null, "Cancellation must return no selection");
                    checks.Add(name + " opens and cancels");
                }
                finally { picker.Dialog.Unadvise(cookie); }
            }
        }
    }

    private static async Task CheckNotificationsAsync(string directory)
    {
        var previousRender = Environment.GetEnvironmentVariable("MIPUSHDESK_RENDER_DIR");
        Environment.SetEnvironmentVariable("MIPUSHDESK_RENDER_DIR", directory);
        try
        {
            SetCurrentProcessExplicitAppUserModelID(NotificationService.AppId);
            var paths = new AppPaths(directory);
            var settings = new DeskSettings { NotificationsEnabled = false, QuietHoursEnabled = true, QuietStartHour = 0, QuietEndHour = 0 };
            settings.Appearance.ToastMode = "native";
            settings.Apps["com.example.nativecheck"] = new() { Muted = true };
            var store = new AppStore(paths);
            store.Save(settings);
            using var images = new ImageService(paths);
            using var notifications = new NotificationService(paths, images, store.Read,
                (_, _, _, _) => throw new InvalidOperationException("Native test fell back to a balloon"), _ => { });
            var record = new PushRecord { Key = "native-check-" + Guid.NewGuid().ToString("N"), Package = "com.example.nativecheck",
                Title = "MiPush Desk · 测试通知", Description = "正在检查图文通知和系统通知中心。", ReceivedAt = DateTimeOffset.Now, IsNotification = true };
            await notifications.ShowTestAsync(record);
            await Task.Delay(4000);
            var lines = File.ReadAllLines(Path.Combine(directory, "notification-events.jsonl"));
            Assert(lines.Any(line => line.Contains("\"submitted\"")), "Native toast was not submitted");
            Assert(!lines.Any(line => line.Contains("\"failed\"")), "Windows rejected the native toast");
            var history = ToastNotificationManager.History.GetHistory(NotificationService.AppId);
            Assert(history.Any(toast => toast.Tag == RichNotification.Tag(record)), "Test notification is missing from Windows history");
            Assert(!history.Single(toast => toast.Tag == RichNotification.Tag(record)).Content.GetXml().Contains("placement=\"hero\""),
                "Default notification contains an image");
            var expected = RichNotification.BuildToast(record, settings.Appearance, images.AppName(record.Package, settings),
                images.AppIcon(record.Package, settings));
            Assert(System.Xml.Linq.XNode.DeepEquals(System.Xml.Linq.XDocument.Parse(expected),
                System.Xml.Linq.XDocument.Parse(history.Single(toast => toast.Tag == RichNotification.Tag(record)).Content.GetXml())),
                "Windows preview differs from the production notification template");
            var saved = store.Read();
            Assert(!saved.NotificationsEnabled && saved.QuietHoursEnabled && saved.Appearance.ToastMode == "native"
                && saved.Apps[record.Package].Muted, "Test changed notification preferences");
            var neighbor = new PushRecord { Key = "native-neighbor-" + Guid.NewGuid().ToString("N"), Package = record.Package,
                Title = "MiPush Desk · 保留通知", Description = "单条删除只移除所选通知。", ReceivedAt = DateTimeOffset.Now, IsNotification = true };
            await notifications.ShowTestAsync(neighbor);
            await Task.Delay(1000);
            notifications.Remove(record);
            var remaining = ToastNotificationManager.History.GetHistory(NotificationService.AppId);
            Assert(!remaining.Any(toast => toast.Tag == RichNotification.Tag(record)) && remaining.Any(toast => toast.Tag == RichNotification.Tag(neighbor)),
                "Single notification removal deleted a neighbor or left the target in Windows history");
            notifications.Clear();
            Assert(ToastNotificationManager.History.GetHistory(NotificationService.AppId).Count == 0, "Clear left notifications in Windows history");
            await CheckNotificationOptionsAsync(Path.Combine(directory, "options"));
        }
        finally { Environment.SetEnvironmentVariable("MIPUSHDESK_RENDER_DIR", previousRender); }
    }

    private static async Task CheckNotificationOptionsAsync(string directory)
    {
        var paths = new AppPaths(directory);
        var settings = new DeskSettings();
        settings.Appearance.ShowImages = true;
        settings.Appearance.UseAppIcons = false;
        using var images = new ImageService(paths);
        var xml = new List<string>();
        var balloons = new List<string>();
        using var notifications = new NotificationService(paths, images, () => settings,
            (_, text, _, _) => balloons.Add(text), _ => { }, xml.Add);
        var record = new PushRecord { Key = "options-test", Package = "com.example.options", Title = "Test", Description = "First line\nSecond line" };
        await notifications.ShowTestAsync(record);
        Assert(!xml.Last().Contains("placement=\"hero\""), "An image was invented when neither the message nor preferences supplied one");
        var imageFile = new ImportService(paths).ImportIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "preview.png"));
        settings.Appearance.DefaultImage = imageFile;
        await notifications.ShowTestAsync(record);
        Assert(xml.Last().Contains("placement=\"hero\"") && xml.Last().Contains(imageFile), "Custom default image was not used");
        settings.Appearance.ToastStyle = "standard";
        await notifications.ShowTestAsync(record);
        Assert(!xml.Last().Contains("placement=\"hero\"") && xml.Last().Contains("Second line"), "Standard notification ignored the selected content style");
        settings.Appearance.ToastStyle = "compact";
        await notifications.ShowTestAsync(record);
        Assert(!xml.Last().Contains("Second line") && !xml.Last().Contains("placement=\"hero\""), "Compact notification was not shortened");
        settings.Appearance.ToastMode = "tray";
        var nativeCount = xml.Count;
        await notifications.ShowTestAsync(record);
        Assert(xml.Count == nativeCount && balloons.Single() == "First line", "Tray test ignored the selected mode or content style");
        settings.Appearance.ToastMode = "off";
        await notifications.ShowTestAsync(record);
        Assert(xml.Count == nativeCount && balloons.Count == 1, "Notification-center-only test created a popup");

        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var request = new byte[4096];
            await stream.ReadAsync(request, timeout.Token);
            var bytes = File.ReadAllBytes(Path.Combine(paths.Icons, imageFile));
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(bytes, timeout.Token);
        }, timeout.Token);
        record.Extra["notification_bigPic_uri"] = $"http://127.0.0.1:{((System.Net.IPEndPoint)server.LocalEndpoint).Port}/message.png";
        var messageImage = await images.NotificationImageAsync(record, settings.Appearance);
        await response;
        Assert(messageImage is not null && messageImage != images.DefaultImage(settings.Appearance)
            && File.ReadAllBytes(messageImage).SequenceEqual(File.ReadAllBytes(Path.Combine(paths.Icons, imageFile))),
            "A message image did not take priority over the default image");
    }

    private static void CheckIconPriority(string directory)
    {
        const string package = "com.tencent.mm";
        var paths = new AppPaths(Path.Combine(directory, "icon-priority"));
        var importer = new ImportService(paths);
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1kAAAAASUVORK5CYII=");
        var icon = importer.StoreIcon(bytes);
        AtomicFile.Write(Path.Combine(paths.Data, "application-cache.json"), JsonSerializer.Serialize(new Dictionary<string, CachedAppMetadata>
        { [package] = new() { Name = "微信", Icon = icon, Status = "ok", CheckedAt = DateTimeOffset.UtcNow } }, JsonData.Options));
        using var images = new ImageService(paths);
        var settings = new DeskSettings();
        var builtIn = images.BuiltIn.Icon(package);
        Assert(images.AppIcon(package, settings) == builtIn, "Xiaomi cache overrode built-in library");
        settings.UseBuiltInIcons = false;
        Assert(images.AppIcon(package, settings) == images.Metadata.IconPath(package), "Xiaomi cache ignored when library is disabled");
        settings.UseBuiltInIcons = true;
        settings.LibraryIcons[package] = icon;
        Assert(images.AppIcon(package, settings) == Path.Combine(paths.Icons, icon), "Imported icon library priority failed");
        settings.Apps[package] = new() { Icon = "manual.png" };
        File.WriteAllBytes(Path.Combine(paths.Icons, "manual.png"), bytes);
        Assert(images.AppIcon(package, settings) == Path.Combine(paths.Icons, "manual.png"), "Manual icon priority failed");
        images.Metadata.Clear();
        Assert(File.Exists(Path.Combine(paths.Icons, icon)) && File.Exists(builtIn), "Clearing Xiaomi cache deleted library or imported icons");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int left, int top,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

[ComVisible(true), Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDialogEvents
{
    [PreserveSig] int OnFileOk(nint dialog);
    [PreserveSig] int OnFolderChanging(nint dialog, nint folder);
    [PreserveSig] int OnFolderChange(nint dialog);
    [PreserveSig] int OnSelectionChange(nint dialog);
    [PreserveSig] int OnShareViolation(nint dialog, nint item, out uint response);
    [PreserveSig] int OnTypeChange(nint dialog);
    [PreserveSig] int OnOverwrite(nint dialog, nint item, out uint response);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DialogEvents(Action close) : IDialogEvents
{
    public bool Opened { get; private set; }
    public Exception? Error { get; private set; }
    public int OnFileOk(nint dialog) => 0;
    public int OnFolderChanging(nint dialog, nint folder) => 0;
    public int OnFolderChange(nint dialog)
    {
        if (Opened) return 0;
        Opened = true;
        try { close(); }
        catch (Exception error) { Error = error; }
        return 0;
    }
    public int OnSelectionChange(nint dialog) => 0;
    public int OnShareViolation(nint dialog, nint item, out uint response) { response = 0; return 0; }
    public int OnTypeChange(nint dialog) => 0;
    public int OnOverwrite(nint dialog, nint item, out uint response) { response = 0; return 0; }
}

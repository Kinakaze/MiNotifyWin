using System.Security.Cryptography;

namespace MiPushDesk.Core;

public sealed class ImportService(AppPaths paths)
{
    public ExchangeDocument Read(string file) => ExchangeDocument.Parse(ReadText(file));

    public string ReadText(string file)
    {
        if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("JSON 超过 64 MiB。");
        return File.ReadAllText(file);
    }

    public ExchangeDocument ExportSettings(DeskSettings settings)
    {
        AppStore.Validate(settings);
        var icons = ReferencedImages(settings)
            .ToDictionary(name => name, name => Convert.ToBase64String(File.ReadAllBytes(Path.Combine(paths.Icons, name))));
        return new() { Settings = settings, Icons = icons };
    }

    public DeskSettings Apply(ExchangeDocument document, DeskSettings current, AppStore store)
    {
        var settings = document.Settings ?? current;
        foreach (var icon in ReferencedImages(settings))
            if (document.Icons?.ContainsKey(icon) != true && !File.Exists(Path.Combine(paths.Icons, icon)))
                throw new InvalidDataException("JSON 缺少图片：" + icon);
        if (document.Settings is not null)
        {
            settings.AutoConnect = current.AutoConnect;
            settings.CloseToTray = current.CloseToTray;
            settings.HeartbeatSeconds = current.HeartbeatSeconds;
        }
        if (document.Icons is { } icons)
            foreach (var (name, data) in icons) AtomicFile.Write(Path.Combine(paths.Icons, name), Convert.FromBase64String(data));
        if (document.Analysis is { } analysis) AtomicFile.Write(Path.Combine(paths.Data, "analysis.json"), new ExchangeDocument { Analysis = analysis }.ToJson());
        if (document.Account is { } account) store.SaveAccount(account.GetRawText());
        if (document.AppCredentials is { } incoming)
        {
            var credentials = store.ReadAppCredentials();
            foreach (var (package, credential) in incoming)
            {
                credentials[package] = credential;
                settings.Apps.TryAdd(package, new());
                settings.HiddenApps.Remove(package);
            }
            store.SaveAppCredentials(credentials);
        }
        store.Save(settings);
        return settings;
    }

    public string ImportIcon(string file)
    {
        if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new InvalidDataException("图标超过 4 MiB。");
        return StoreIcon(File.ReadAllBytes(file));
    }

    private static IEnumerable<string> ReferencedImages(DeskSettings settings) => settings.Apps.Values
        .Select(rule => rule.Icon).Concat(settings.LibraryIcons.Values).Append(settings.Appearance.DefaultImage)
        .OfType<string>().Distinct();

    public string StoreIcon(byte[] bytes)
    {
        var extension = DetectImage(bytes) ?? throw new InvalidDataException("图标需要 PNG / JPEG。");
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("图标超过 4 MiB。");
        var name = IconName(bytes, extension);
        AtomicFile.Write(Path.Combine(paths.Icons, name), bytes);
        return name;
    }

    public static string IconName(byte[] bytes, string extension) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;

    public static string? DetectImage(ReadOnlySpan<byte> bytes) => bytes.Length >= 24
        && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
        : bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? ".jpg" : null;
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MiPushDesk.Core;

namespace MiPushDesk.Services;

public sealed class ImageService(AppPaths paths) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
        { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _downloads = new(3);
    private readonly ConcurrentDictionary<string, Task<string?>> _pending = new();
    public AppMetadataService Metadata { get; } = new(paths);
    public BuiltInIconLibrary BuiltIn { get; } = new(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcons", "delta.zip"), paths);
    public string AppName(string package, DeskSettings settings) => AppCatalog.Name(package, settings,
        settings.UseXiaomiMetadata ? Metadata.Get(package)?.Name : null);
    public string? AppIcon(string package, DeskSettings settings)
    {
        if (settings.Apps.TryGetValue(package, out var rule) && rule.Icon is not null) return Path.Combine(paths.Icons, rule.Icon);
        if (settings.LibraryIcons.TryGetValue(package, out var libraryIcon)) return Path.Combine(paths.Icons, libraryIcon);
        if (settings.UseBuiltInIcons && BuiltIn.Icon(package) is { } bundledIcon) return bundledIcon;
        if (settings.UseXiaomiMetadata && Metadata.IconPath(package) is { } cachedIcon) return cachedIcon;
        return null;
    }
    public Task<string?> FetchAsync(string? url)
    {
        return url is null ? Task.FromResult<string?>(null) : _pending.GetOrAdd(url, DownloadAsync);
    }
    public string? DefaultImage(DisplayProfile profile) => profile.DefaultImage is { } name ? Path.Combine(paths.Icons, name) : null;
    public Task<string?> NotificationImageAsync(PushRecord record, DisplayProfile profile) => RichNotification.ImageUrl(record) is { } url
        ? FetchAsync(url) : Task.FromResult(DefaultImage(profile));
    private async Task<string?> DownloadAsync(string url)
    {
        await _downloads.WaitAsync();
        try
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            foreach (var extension in new[] { ".png", ".jpg" })
            {
                var existing = Path.Combine(paths.Cache, key + extension);
                if (File.Exists(existing)) return existing;
            }
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("通知图片超过 4 MiB。");
            await using var source = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[32768];
            while (true)
            {
                var count = await source.ReadAsync(buffer);
                if (count == 0) break;
                if (memory.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("通知图片超过 4 MiB。");
                memory.Write(buffer, 0, count);
            }
            var bytes = memory.ToArray();
            var suffix = ImportService.DetectImage(bytes) ?? throw new InvalidDataException("通知图片不是 PNG / JPEG。");
            var path = Path.Combine(paths.Cache, key + suffix);
            AtomicFile.Write(path, bytes);
            return path;
        }
        finally { _downloads.Release(); }
    }
    public void Dispose() { _http.Dispose(); Metadata.Dispose(); BuiltIn.Dispose(); }
}

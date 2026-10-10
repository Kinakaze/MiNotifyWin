using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiPushDesk.Core;

public sealed class CachedAppMetadata
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string Status { get; set; } = "";
}

public sealed class AppMetadataService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly Dictionary<string, CachedAppMetadata> _entries;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads = new(2);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private int _generation;
    private string CacheFile => Path.Combine(_paths.Data, "application-cache.json");
    private string IconDirectory => Path.Combine(_paths.Data, "application-icons");
    public AppMetadataService(AppPaths paths, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _http = handler is null ? new() : new(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MiPushDesk/1.1");
        _entries = File.Exists(CacheFile) ? JsonSerializer.Deserialize<Dictionary<string, CachedAppMetadata>>(File.ReadAllText(CacheFile), JsonData.Options)!
            : new(StringComparer.Ordinal);
        Directory.CreateDirectory(IconDirectory);
        foreach (var icon in _entries.Values.Select(entry => entry.Icon).OfType<string>().Distinct())
            if (!File.Exists(Path.Combine(IconDirectory, icon)) && File.Exists(Path.Combine(paths.Icons, icon)))
                File.Copy(Path.Combine(paths.Icons, icon), Path.Combine(IconDirectory, icon));
    }
    public CachedAppMetadata? Get(string package) { lock (_gate) return _entries.GetValueOrDefault(package); }
    public string? IconPath(string package) => Get(package)?.Icon is { } icon ? Path.Combine(IconDirectory, icon) : null;
    public int Count { get { lock (_gate) return _entries.Values.Count(entry => entry.Name.Length > 0); } }
    public Task EnsureAsync(string package, bool force = false)
    {
        if (!AppStore.ValidPackage(package)) return Task.CompletedTask;
        lock (_gate)
            if (!force && _entries.TryGetValue(package, out var cached) && (cached.Name.Length > 0
                || DateTimeOffset.UtcNow - cached.CheckedAt < (cached.Status == "not_found" ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(5))))
                return Task.CompletedTask;
        return _pending.GetOrAdd(package, name => new Lazy<Task>(() => LookupAsync(name))).Value;
    }
    private async Task LookupAsync(string package)
    {
        var generation = Volatile.Read(ref _generation);
        await _downloads.WaitAsync(_stop.Token);
        var result = new CachedAppMetadata { CheckedAt = DateTimeOffset.UtcNow };
        byte[]? iconBytes = null;
        try
        {
            using var response = await _http.GetAsync("https://app.mi.com/details?id=" + Uri.EscapeDataString(package), _stop.Token);
            if (response.StatusCode == HttpStatusCode.NotFound || response.RequestMessage?.RequestUri is { Host: "app.mi.com", AbsolutePath: "/" })
                result.Status = "not_found";
            else
            {
                response.EnsureSuccessStatusCode();
                var (name, imageUrl) = ParseStorePage(await response.Content.ReadAsStringAsync(_stop.Token));
                result.Name = name;
                iconBytes = await _http.GetByteArrayAsync(imageUrl, _stop.Token);
                var extension = ImportService.DetectImage(iconBytes) ?? throw new InvalidDataException("小米图标不是 PNG / JPEG。");
                result.Icon = ImportService.IconName(iconBytes, extension); result.Status = "ok";
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { result.Status = error.GetType().Name; }
        finally { _downloads.Release(); _pending.TryRemove(package, out _); }
        if (_stop.IsCancellationRequested || generation != Volatile.Read(ref _generation)) return;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || generation != Volatile.Read(ref _generation)) return;
            if (result.Status == "ok") AtomicFile.Write(Path.Combine(IconDirectory, result.Icon!), iconBytes!);
            if (result.Status != "ok" && _entries.TryGetValue(package, out var previous))
            { result.Name = previous.Name; result.Icon = previous.Icon; }
            _entries[package] = result;
            AtomicFile.Write(CacheFile, JsonSerializer.Serialize(_entries, JsonData.Options));
        }
    }
    public static (string Name, string ImageUrl) ParseStorePage(string html)
    {
        var section = Regex.Match(html, "<div\\b[^>]*class=[\"']app-info[\"'][^>]*>([\\s\\S]*)", RegexOptions.CultureInvariant);
        if (!section.Success) throw new InvalidDataException("小米商店未返回应用信息。");
        var content = section.Groups[1].Value;
        var title = Regex.Match(content, "<h3\\b[^>]*>([^<]+)</h3>", RegexOptions.CultureInvariant);
        var image = Regex.Match(content, "<img\\b[^>]*>", RegexOptions.CultureInvariant);
        var source = Regex.Match(image.Value, "\\bsrc=[\"']([^\"']+)[\"']", RegexOptions.CultureInvariant);
        var name = WebUtility.HtmlDecode(title.Groups[1].Value).Trim();
        var imageUrl = RichNotification.WebUrl(WebUtility.HtmlDecode(source.Groups[1].Value));
        if (name.Length is 0 or > 128 || imageUrl is null) throw new InvalidDataException("小米商店应用信息不完整。");
        if (imageUrl.StartsWith("http://", StringComparison.Ordinal)) imageUrl = "https://" + imageUrl[7..];
        return (name, imageUrl);
    }
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        lock (_gate)
        {
            foreach (var icon in _entries.Values.Select(entry => entry.Icon).OfType<string>().Distinct())
                File.Delete(Path.Combine(IconDirectory, icon));
            _entries.Clear(); File.Delete(CacheFile);
        }
    }
    public void Dispose() { _stop.Cancel(); _http.Dispose(); }
}

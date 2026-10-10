using System.IO.Compression;
using System.Text.Json;

namespace MiPushDesk.Core;

public sealed class BuiltInIconLibrary : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, string> _icons;
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly object _gate = new();
    public int Count => _icons.Count;

    public BuiltInIconLibrary(string archivePath, AppPaths paths)
    {
        _archive = ZipFile.OpenRead(archivePath);
        _directory = Path.Combine(paths.Data, "library-cache");
        using var input = _archive.GetEntry("index.json")!.Open();
        using var index = JsonDocument.Parse(input);
        _icons = index.RootElement.GetProperty("icons").Deserialize<Dictionary<string, string>>()!;
    }
    public bool Contains(string package) => _icons.ContainsKey(package);
    public string? Icon(string package)
    {
        if (!_icons.TryGetValue(package, out var name)) return null;
        lock (_gate)
        {
            if (_paths.TryGetValue(name, out var path)) return path;
            var entry = _archive.GetEntry(name)!;
            using var input = entry.Open();
            var bytes = new byte[checked((int)entry.Length)]; input.ReadExactly(bytes);
            var extension = ImportService.DetectImage(bytes) ?? throw new InvalidDataException("内置图标格式无效。");
            path = Path.Combine(_directory, ImportService.IconName(bytes, extension));
            if (!File.Exists(path)) AtomicFile.Write(path, bytes);
            _paths[name] = path;
            return path;
        }
    }
    public void Dispose() => _archive.Dispose();
}

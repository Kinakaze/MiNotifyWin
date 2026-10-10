using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MiPushDesk.Core;

public sealed record IconPackResult(Dictionary<string, byte[]> Icons, int Unavailable);

public static class IconPack
{
    public static IconPackResult Read(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        if (archive.Entries.Count > 30000) throw new InvalidDataException("图标包文件过多。");
        var filter = archive.Entries.Where(entry => Path.GetFileName(entry.FullName) == "appfilter.xml")
            .OrderBy(entry => entry.FullName.Contains("assets/", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault()
            ?? throw new InvalidDataException("图标包缺少 appfilter.xml。");
        if (filter.Length > 4 * 1024 * 1024) throw new InvalidDataException("appfilter.xml 过大。");
        using var xml = XmlReader.Create(filter.Open(), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024, CloseInput = true });
        var mappings = Parse(XDocument.Load(xml));
        var images = archive.Entries.Where(entry => new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(entry.FullName).ToLowerInvariant()))
            .GroupBy(entry => Path.GetFileNameWithoutExtension(entry.FullName), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => Density(entry.FullName)).ThenBy(entry => entry.FullName, StringComparer.Ordinal).First(), StringComparer.Ordinal);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var unavailable = 0;
        long totalBytes = 0;
        foreach (var (package, drawable) in mappings)
        {
            if (!images.TryGetValue(drawable, out var entry)) { unavailable++; continue; }
            if (!loaded.TryGetValue(drawable, out var bytes))
            {
                if (entry.Length is < 4 or > 4 * 1024 * 1024 || (totalBytes += entry.Length) > 64 * 1024 * 1024)
                    throw new InvalidDataException("图标包图片超过大小限制。");
                using var source = entry.Open();
                bytes = new byte[(int)entry.Length]; source.ReadExactly(bytes);
                if (source.ReadByte() != -1) throw new InvalidDataException("图标包图片长度不符。");
                if (ImportService.DetectImage(bytes) is null) { unavailable++; continue; }
                loaded[drawable] = bytes;
            }
            result[package] = bytes;
        }
        if (result.Count == 0) throw new InvalidDataException("图标包没有可用的 PNG / JPEG 图标。");
        return new(result, unavailable);
    }
    public static Dictionary<string, string> Parse(XDocument document)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.Descendants("item"))
        {
            var component = (string?)item.Attribute("component") ?? "";
            var drawable = (string?)item.Attribute("drawable") ?? "";
            var match = Regex.Match(component, @"^ComponentInfo\{([^/{}]+)/[^{}]+\}$", RegexOptions.CultureInvariant);
            if (!match.Success || !AppStore.ValidPackage(match.Groups[1].Value)
                || !Regex.IsMatch(drawable, @"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)) continue;
            mappings.TryAdd(match.Groups[1].Value, drawable);
            if (mappings.Count > 10000) throw new InvalidDataException("图标包应用超过 10000 个。");
        }
        return mappings;
    }
    private static int Density(string path) => path.Contains("xxxhdpi", StringComparison.Ordinal) ? 6
        : path.Contains("xxhdpi", StringComparison.Ordinal) ? 5 : path.Contains("xhdpi", StringComparison.Ordinal) ? 4
        : path.Contains("nodpi", StringComparison.Ordinal) ? 3 : path.Contains("hdpi", StringComparison.Ordinal) ? 2 : 1;
}

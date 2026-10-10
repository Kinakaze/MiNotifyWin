using System.Text.RegularExpressions;

namespace MiPushDesk.Core;

public sealed record PaletteColors
{
    public string Background { get; init; } = "#FAFAFC";
    public string Sidebar { get; init; } = "#FFFFFF";
    public string Surface { get; init; } = "#FFFFFF";
    public string Text { get; init; } = "#282633";
    public string Muted { get; init; } = "#797487";
    public string Border { get; init; } = "#E5E4EB";
    public string Accent { get; init; } = "#7963BF";
}

public static class Palettes
{
    public static IReadOnlyDictionary<string, PaletteColors> Presets { get; } = new Dictionary<string, PaletteColors>
    {
        ["paper"] = new(),
        ["midnight"] = new() { Background = "#131318", Sidebar = "#19191F", Surface = "#202027", Text = "#F0EFF5", Muted = "#A3A0B2", Border = "#32313C", Accent = "#B8A6FF" },
        ["ocean"] = new() { Background = "#111820", Sidebar = "#161F29", Surface = "#1C2935", Text = "#E6EFF7", Muted = "#8FA6BB", Border = "#2F4050", Accent = "#8FBBF0" },
        ["forest"] = new() { Background = "#141C19", Sidebar = "#18241E", Surface = "#202E27", Text = "#E8F0E9", Muted = "#98ADA0", Border = "#314339", Accent = "#A3C9AE" }
    };

    public static PaletteColors For(DisplayProfile profile) => profile.Colors ?? Presets[profile.Theme];
    public static void Select(DisplayProfile profile, string id)
    {
        if (!Presets.ContainsKey(id)) throw new InvalidDataException("配色无效。");
        profile.Theme = id;
        profile.Colors = null;
    }
    public static bool ValidColor(string? value) => value is { Length: 7 } && Regex.IsMatch(value, "^#[A-Fa-f0-9]{6}$", RegexOptions.CultureInvariant);
    public static void Validate(PaletteColors colors)
    {
        if (new[] { colors.Background, colors.Sidebar, colors.Surface, colors.Text, colors.Muted, colors.Border, colors.Accent }.Any(value => !ValidColor(value)))
            throw new InvalidDataException("颜色需要使用 #RRGGBB 格式。");
    }
    public static bool IsDark(string color)
    {
        var red = Linear(Convert.ToByte(color.Substring(1, 2), 16));
        var green = Linear(Convert.ToByte(color.Substring(3, 2), 16));
        var blue = Linear(Convert.ToByte(color.Substring(5, 2), 16));
        return red * 0.2126 + green * 0.7152 + blue * 0.0722 < 0.179;
    }
    private static double Linear(byte component)
    {
        var value = component / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}

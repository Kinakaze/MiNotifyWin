using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiPushDesk.Core;
using Windows.UI;

namespace MiPushDesk.Services;

public static class Ui
{
    public static DisplayProfile Profile { get; set; } = new();
    public static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];
    public static Color Color(string hex) => Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
    public static TextBlock Text(string text, double size = 14, string brush = "DeskText", bool bold = false) => new()
    {
        Text = text, FontSize = size * Profile.FontSize / 14, Foreground = Brush(brush),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Wrap(string text, double size = 13, string brush = "DeskMuted")
    {
        var block = Text(text, size, brush); block.TextWrapping = TextWrapping.Wrap; return block;
    }
    public static FontIcon Icon(string glyph, double size = 16, string brush = "DeskMuted") => new() { Glyph = glyph, FontSize = size, Foreground = Brush(brush) };
    public static StackPanel Stack(double spacing = 12, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    public static StackPanel Row(double spacing = 8, params UIElement[] children)
    {
        var panel = Stack(spacing, children); panel.Orientation = Orientation.Horizontal; panel.VerticalAlignment = VerticalAlignment.Center; return panel;
    }
    public static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = width }); return grid;
    }
    public static GridLength Star(double size = 1) => new(size, GridUnitType.Star);
    public static void Add(Grid grid, UIElement child, int column = 0, int row = 0)
    {
        Grid.SetColumn((FrameworkElement)child, column); Grid.SetRow((FrameworkElement)child, row); grid.Children.Add(child);
    }
    public static Button Button(string label, Action action, string style = "DeskButton", string? glyph = null, string? id = null)
    {
        var brush = style == "DeskPrimary" ? "DeskOnAccent" : style == "DeskQuiet" ? "DeskMuted" : "DeskText";
        var button = new Button { Style = (Style)Application.Current.Resources[style], CornerRadius = new(Profile.CornerRadius),
            Content = glyph is null ? Text(label, 13, brush) : Row(7, Icon(glyph, 14, brush), Text(label, 13, brush)) };
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, label);
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        return button;
    }
    public static Border Card(UIElement child, double padding = 18) => new()
    {
        Background = Brush("DeskSurface"), BorderBrush = Brush("DeskBorder"), BorderThickness = new(1),
        CornerRadius = new(Profile.CornerRadius), Padding = new(padding), Child = child
    };
    public static Border Badge(string text) => new()
    {
        Background = Brush("DeskAccentSoft"), CornerRadius = new(5), Padding = new(8, 4, 8, 4),
        Child = Text(text, 10, "DeskAccent"), VerticalAlignment = VerticalAlignment.Center
    };
    public static Border Rule() => new() { Height = 1, Background = Brush("DeskBorder") };
    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    public static Grid Heading(string title, UIElement? actions = null)
    {
        var grid = Columns(Star(), GridLength.Auto);
        grid.ColumnSpacing = 16;
        Add(grid, Text(title, 27, bold: true));
        if (actions is not null) Add(grid, actions, 1);
        return grid;
    }
    public static string Time(DateTimeOffset time) => time.LocalDateTime.Date == DateTime.Today
        ? time.LocalDateTime.ToString("HH:mm") : time.LocalDateTime.ToString("MM-dd HH:mm");
    public static void ApplyPalette(Grid root, DisplayProfile profile)
    {
        Profile = profile;
        var colors = Palettes.For(profile);
        var palette = new[] { colors.Background, colors.Sidebar, colors.Surface, colors.Text, colors.Muted,
            colors.Border, colors.Accent, Palettes.IsDark(colors.Accent) ? "#FFFFFF" : "#17171C" };
        var names = new[] { "DeskBackground", "DeskSidebar", "DeskSurface", "DeskText", "DeskMuted", "DeskBorder", "DeskAccent", "DeskOnAccent" };
        for (var index = 0; index < names.Length; index++) Brush(names[index]).Color = Color(palette[index]);
        var accent = Color(palette[6]); accent.A = 36; Brush("DeskAccentSoft").Color = accent;
        root.RequestedTheme = Palettes.IsDark(colors.Background) ? ElementTheme.Dark : ElementTheme.Light;
    }
}

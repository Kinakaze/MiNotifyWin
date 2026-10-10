using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiPushDesk.Core;
using MiPushDesk.Services;

namespace MiPushDesk;

public sealed partial class MainWindow
{
    private readonly List<(PaletteColors Colors, Button Button)> _paletteSwatches = [];

    private UIElement ThemeSwatches(double size = 28)
    {
        var row = Ui.Row(9);
        foreach (var (id, colors) in Palettes.Presets)
        {
            var swatch = new Button
            {
                Style = (Style)Application.Current.Resources["DeskSwatch"], Width = size, Height = size,
                CornerRadius = new(size / 2), Padding = new(3), BorderThickness = new(1),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = new Border { Width = size - 8, Height = size - 8, CornerRadius = new(size / 2),
                    Background = new SolidColorBrush(Ui.Color(colors.Background)),
                    Child = new Border { Width = size / 3, Height = size / 3, CornerRadius = new(size / 2),
                        Background = new SolidColorBrush(Ui.Color(colors.Accent)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
            };
            AutomationProperties.SetAutomationId(swatch, "Theme-" + id);
            AutomationProperties.SetName(swatch, "切换配色 " + (row.Children.Count + 1));
            swatch.Click += (_, _) => { Palettes.Select(_settings.Appearance, id); SaveSettings(true); };
            _paletteSwatches.Add((colors, swatch));
            row.Children.Add(swatch);
        }
        UpdatePaletteSwatches();
        return row;
    }
    private void UpdatePaletteSwatches()
    {
        foreach (var (colors, button) in _paletteSwatches)
        {
            var selected = Palettes.For(_settings.Appearance) == colors;
            button.BorderBrush = Ui.Brush(selected ? "DeskAccent" : "DeskBorder");
            button.BorderThickness = new(selected ? 2 : 1);
        }
    }
    private UIElement BuildColorControls()
    {
        var heading = Ui.Columns(Ui.Star(), GridLength.Auto);
        Ui.Add(heading, Ui.Text("配色", 14, bold: true));
        Ui.Add(heading, ThemeSwatches(34), 1);
        var fields = Ui.Columns(Ui.Star(), Ui.Star()); fields.ColumnSpacing = 26; fields.RowSpacing = 10;
        for (var index = 0; index < 4; index++) fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var colors = Palettes.For(_settings.Appearance);
        Ui.Add(fields, ColorField("强调色", "Accent", colors.Accent, (current, value) => current with { Accent = value }));
        Ui.Add(fields, ColorField("背景", "Background", colors.Background, (current, value) => current with { Background = value }), 1);
        Ui.Add(fields, ColorField("侧栏", "Sidebar", colors.Sidebar, (current, value) => current with { Sidebar = value }), row: 1);
        Ui.Add(fields, ColorField("卡片", "Surface", colors.Surface, (current, value) => current with { Surface = value }), 1, 1);
        Ui.Add(fields, ColorField("文字", "Text", colors.Text, (current, value) => current with { Text = value }), row: 2);
        Ui.Add(fields, ColorField("次要文字", "Muted", colors.Muted, (current, value) => current with { Muted = value }), 1, 2);
        Ui.Add(fields, ColorField("边框", "Border", colors.Border, (current, value) => current with { Border = value }), row: 3);
        var editor = new Expander { Header = "调整颜色", Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(editor, "ColorEditor");
        return Ui.Card(Ui.Stack(16, heading, editor));
    }
    private UIElement ColorField(string label, string id, string value, Func<PaletteColors, string, PaletteColors> change)
    {
        var row = Ui.Columns(Ui.Star(), GridLength.Auto, new(108)); row.ColumnSpacing = 8;
        Ui.Add(row, Ui.Text(label, 12));
        var swatch = new Button { Style = (Style)Application.Current.Resources["DeskSwatch"], Width = 28, Height = 28,
            CornerRadius = new(6), BorderThickness = new(1), BorderBrush = Ui.Brush("DeskBorder"), Background = new SolidColorBrush(Ui.Color(value)) };
        AutomationProperties.SetAutomationId(swatch, "ColorSwatch-" + id);
        AutomationProperties.SetName(swatch, "修改" + label);
        var input = new TextBox { Text = value, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 12,
            Padding = new(8, 4, 8, 4), MinHeight = 34, MaxLength = 7 };
        AutomationProperties.SetAutomationId(input, "Color-" + id);
        AutomationProperties.SetName(input, label + "色值");
        var picker = new ColorPicker { Color = Ui.Color(value), IsAlphaEnabled = false, IsMoreButtonVisible = false,
            IsColorSliderVisible = true, Width = 270 };
        var lastValid = value;
        swatch.Flyout = new Flyout { Content = picker };
        input.TextChanged += (_, _) =>
        {
            if (!Palettes.ValidColor(input.Text)) return;
            lastValid = input.Text.ToUpperInvariant();
            var color = Ui.Color(input.Text);
            swatch.Background = new SolidColorBrush(color);
            if (picker.Color != color) picker.Color = color;
            var profile = _settings.Appearance;
            var updated = change(Palettes.For(profile), input.Text.ToUpperInvariant());
            if (updated == Palettes.For(profile)) return;
            profile.Colors = updated; SaveSettings(); UpdatePaletteSwatches();
        };
        input.LostFocus += (_, _) => { if (!Palettes.ValidColor(input.Text)) input.Text = lastValid; };
        picker.ColorChanged += (_, arguments) =>
        {
            var hex = $"#{arguments.NewColor.R:X2}{arguments.NewColor.G:X2}{arguments.NewColor.B:X2}";
            if (!string.Equals(input.Text, hex, StringComparison.OrdinalIgnoreCase)) input.Text = hex;
        };
        Ui.Add(row, swatch, 1); Ui.Add(row, input, 2);
        return row;
    }
}

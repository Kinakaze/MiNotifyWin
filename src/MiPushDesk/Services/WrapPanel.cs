using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MiPushDesk.Services;

public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d; var rowWidth = 0d; var rowHeight = 0d; var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var desired = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + Spacing + desired.Width > availableSize.Width)
            { width = Math.Max(width, rowWidth); height += rowHeight + Spacing; rowWidth = rowHeight = 0; }
            rowWidth += (rowWidth == 0 ? 0 : Spacing) + desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }
        return new(Math.Max(width, rowWidth), height + rowHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = 0d; var top = 0d; var rowHeight = 0d;
        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            if (left > 0 && left + desired.Width > finalSize.Width) { left = 0; top += rowHeight + Spacing; rowHeight = 0; }
            child.Arrange(new Rect(left, top, Math.Min(desired.Width, finalSize.Width), desired.Height));
            left += desired.Width + Spacing; rowHeight = Math.Max(rowHeight, desired.Height);
        }
        return finalSize;
    }
}

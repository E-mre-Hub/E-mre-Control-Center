using System.Windows;
using System.Windows.Controls;

namespace RtxWindowsUpdater.Views;

/// <summary>
/// Sabit genişlikli öğeleri satırlara dizer ve her satırı ortalar (ana sayfa: üstte 4, altta 3 kategori).
/// WrapPanel son satırı sola yaslar; bu panel eksik satırı ortada gösterir.
/// </summary>
public sealed class CenteredWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(CenteredWrapPanel),
        new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }

    private int PerRow(double width) =>
        Math.Max(1, double.IsInfinity(width) ? InternalChildren.Count : (int)Math.Floor((width + 0.5) / ItemWidth));

    protected override Size MeasureOverride(Size availableSize)
    {
        var perRow = PerRow(availableSize.Width);
        double height = 0, rowHeight = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(ItemWidth, availableSize.Height));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++count % perRow == 0)
            {
                height += rowHeight;
                rowHeight = 0;
            }
        }
        height += rowHeight;
        var columns = Math.Min(perRow, Math.Max(1, InternalChildren.Count));
        return new Size(columns * ItemWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var perRow = PerRow(finalSize.Width);
        var children = InternalChildren.Cast<UIElement>().ToList();
        double y = 0;
        for (var start = 0; start < children.Count; start += perRow)
        {
            var row = children.Skip(start).Take(perRow).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            var x = (finalSize.Width - row.Count * ItemWidth) / 2;
            foreach (var child in row)
            {
                child.Arrange(new Rect(x, y, ItemWidth, rowHeight));
                x += ItemWidth;
            }
            y += rowHeight;
        }
        return finalSize;
    }
}

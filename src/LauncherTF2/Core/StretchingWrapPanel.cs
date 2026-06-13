using System.Windows;
using System.Windows.Controls;

namespace LauncherTF2.Core;

/// <summary>
/// Wrap panel that stretches items to fill each row evenly instead of leaving
/// dead space on the right edge. The number of columns is recomputed from the
/// available width and <see cref="MinItemWidth"/> on every measure pass, so
/// the layout adapts when side panels open or close.
/// </summary>
public class StretchingWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(StretchingWrapPanel),
            new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(StretchingWrapPanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemSpacingProperty =
        DependencyProperty.Register(nameof(ItemSpacing), typeof(double), typeof(StretchingWrapPanel),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, cellWidth) = ComputeColumns(availableSize.Width);
        var cellSize = new Size(cellWidth, double.IsNaN(ItemHeight) ? double.PositiveInfinity : ItemHeight);

        double rowHeight = 0;
        double totalHeight = 0;
        int colIndex = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(cellSize);
            rowHeight = System.Math.Max(rowHeight, child.DesiredSize.Height);
            colIndex++;
            if (colIndex >= columns)
            {
                totalHeight += rowHeight + ItemSpacing;
                rowHeight = 0;
                colIndex = 0;
            }
        }

        if (colIndex > 0) totalHeight += rowHeight;

        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, cellWidth) = ComputeColumns(finalSize.Width);

        double x = 0, y = 0, rowHeight = 0;
        int colIndex = 0;

        foreach (UIElement child in InternalChildren)
        {
            var h = double.IsNaN(ItemHeight) ? child.DesiredSize.Height : ItemHeight;
            child.Arrange(new Rect(x, y, cellWidth, h));
            rowHeight = System.Math.Max(rowHeight, h);
            colIndex++;
            if (colIndex >= columns)
            {
                y += rowHeight + ItemSpacing;
                rowHeight = 0;
                colIndex = 0;
                x = 0;
            }
            else
            {
                x += cellWidth + ItemSpacing;
            }
        }

        return finalSize;
    }

    private (int columns, double cellWidth) ComputeColumns(double availableWidth)
    {
        if (availableWidth <= 0 || MinItemWidth <= 0)
            return (1, availableWidth);

        var columns = System.Math.Max(1, (int)((availableWidth + ItemSpacing) / (MinItemWidth + ItemSpacing)));
        var cellWidth = (availableWidth - (columns - 1) * ItemSpacing) / columns;
        return (columns, cellWidth);
    }
}

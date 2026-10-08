using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CineLibraryCS.Controls;

/// <summary>
/// Lays children out left to right and starts a new line when the next one
/// doesn't fit. WinUI has no built-in wrap panel for items of mixed widths.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Place(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Place(double width, bool arrange)
    {
        var line = new List<UIElement>();
        double x = 0, y = 0, lineHeight = 0, widest = 0;

        void EndLine()
        {
            if (arrange)
            {
                double lx = 0;
                foreach (var c in line)
                {
                    c.Arrange(new Rect(lx, y, c.DesiredSize.Width, lineHeight));
                    lx += c.DesiredSize.Width + HorizontalSpacing;
                }
            }
            widest = Math.Max(widest, x - HorizontalSpacing);
            y += lineHeight + VerticalSpacing;
            line.Clear();
            x = 0;
            lineHeight = 0;
        }

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (line.Count > 0 && x + size.Width > width) EndLine();
            line.Add(child);
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        if (line.Count > 0) EndLine();

        return new Size(widest, Math.Max(0, y - VerticalSpacing));
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Mirador.App.Controls;

public sealed partial class PageColumn : Panel
{
    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(
        nameof(MaxColumnWidth), typeof(double), typeof(PageColumn), new PropertyMetadata(900.0, OnLayoutChanged));

    public static readonly DependencyProperty IsCenteredProperty = DependencyProperty.Register(
        nameof(IsCentered), typeof(bool), typeof(PageColumn), new PropertyMetadata(true, OnLayoutChanged));

    public double MaxColumnWidth
    {
        get => (double)GetValue(MaxColumnWidthProperty);
        set => SetValue(MaxColumnWidthProperty, value);
    }

    public bool IsCentered
    {
        get => (bool)GetValue(IsCenteredProperty);
        set => SetValue(IsCenteredProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(availableSize.Width, MaxColumnWidth);
        var height = 0.0;
        var widest = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? widest : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(finalSize.Width, MaxColumnWidth);
        var x = IsCentered ? (finalSize.Width - width) / 2 : 0;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
        }

        return finalSize;
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageColumn)d).InvalidateMeasure();
}

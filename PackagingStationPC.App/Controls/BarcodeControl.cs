using System.Windows;
using System.Windows.Media;
using PackagingStationPC.Services;

namespace PackagingStationPC.Controls;

public sealed class BarcodeControl : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(string),
        typeof(BarcodeControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (string.IsNullOrWhiteSpace(Value) || Value.Any(character => character is < (char)32 or > (char)126))
        {
            return;
        }

        var widths = Code128Encoder.GetModuleWidths(Value);
        const int quietZone = 10;
        var moduleWidth = RenderSize.Width / (widths.Sum() + quietZone * 2);
        var x = quietZone * moduleWidth;
        var isBar = true;
        foreach (var count in widths)
        {
            var width = count * moduleWidth;
            if (isBar)
            {
                drawingContext.DrawRectangle(Brushes.Black, null, new Rect(x, 0, width, RenderSize.Height));
            }

            x += width;
            isBar = !isBar;
        }
    }
}

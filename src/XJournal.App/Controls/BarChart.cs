using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XJournal.App.Controls;

public sealed record BarPoint(string Label, double Value, string Detail);

/// <summary>A simple bar chart (e.g. words per week) with hover details, styled like <see cref="TrendChart"/>.</summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarPoint>?> BarsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<BarPoint>?>(nameof(Bars));

    private const double Left = 44, Right = 8, Top = 12, Bottom = 26;
    private int _hover = -1;

    static BarChart() => AffectsRender<BarChart>(BarsProperty);

    public BarChart()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<BarPoint>? Bars { get => GetValue(BarsProperty); set => SetValue(BarsProperty, value); }

    private Rect Plot => new(Left, Top, Math.Max(1, Bounds.Width - Left - Right), Math.Max(1, Bounds.Height - Top - Bottom));

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, weight: weight), size, brush);

    private (double X, double Width) Slot(int index, int count)
    {
        var slot = Plot.Width / Math.Max(1, count);
        var width = Math.Max(2, slot * 0.62);
        return (Plot.Left + slot * index + (slot - width) / 2, width);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < Left + Right + 40 || Bounds.Height < Top + Bottom + 20) return; // no room to draw
        var bars = Bars ?? [];
        var plot = Plot;
        var text = Brush("TextFillColorSecondaryBrush", Brushes.Gray);
        var grid = new Pen(Brush("DividerStrokeColorDefaultBrush", Brushes.LightGray), 1);
        var accent = Brush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
        var accentHover = Brush("AccentFillColorSecondaryBrush", accent);
        var max = NiceMax(bars.Count == 0 ? 0 : bars.Max(b => b.Value));
        if (bars.Count == 0 || max == 0)
        {
            context.DrawLine(grid, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            var empty = Text("Nothing written in this period yet", 14, text);
            context.DrawText(empty, new Point(plot.Center.X - empty.Width / 2, plot.Center.Y - empty.Height / 2));
            return;
        }

        for (var i = 0; i <= 2; i++)
        {
            var value = max * i / 2;
            var y = plot.Bottom - plot.Height * i / 2;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(value.ToString("N0", CultureInfo.CurrentCulture), 11, text);
            context.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(bars.Count * 60 / plot.Width));
        for (var i = 0; i < bars.Count; i++)
        {
            var (x, width) = Slot(i, bars.Count);
            var height = plot.Height * bars[i].Value / max;
            if (height > 0)
                context.DrawRectangle(i == _hover ? accentHover : accent, null,
                    new Rect(x, plot.Bottom - height, width, height), 3, 3);
            if ((bars.Count - 1 - i) % labelEvery == 0)
            {
                var label = Text(bars[i].Label, 11, text);
                context.DrawText(label, new Point(x + width / 2 - label.Width / 2, plot.Bottom + 6));
            }
        }

        if (_hover >= 0 && _hover < bars.Count)
        {
            var detail = Text(bars[_hover].Detail, 12, Brush("TextFillColorPrimaryBrush", Brushes.Black), FontWeight.SemiBold);
            var (x, width) = Slot(_hover, bars.Count);
            var boxX = Math.Clamp(x + width / 2 - detail.Width / 2 - 8, 0, Math.Max(0, Bounds.Width - detail.Width - 16));
            var box = new Rect(boxX, 0, detail.Width + 16, detail.Height + 8);
            context.DrawRectangle(Brush("SolidBackgroundFillColorQuarternaryBrush", Brushes.White),
                new Pen(Brush("CardStrokeColorDefaultBrush", Brushes.LightGray), 1), box, 6, 6);
            context.DrawText(detail, new Point(box.X + 8, box.Y + 4));
        }
    }

    /// <summary>Rounds the axis maximum up to a tidy number (1, 2 or 5 × a power of ten).</summary>
    internal static double NiceMax(double value)
    {
        if (value <= 0) return 0;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1, 2, 5, 10 })
            if (step * magnitude >= value) return step * magnitude;
        return 10 * magnitude;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var bars = Bars ?? [];
        var x = e.GetPosition(this).X;
        var index = x < Plot.Left || x > Plot.Right || bars.Count == 0
            ? -1
            : Math.Clamp((int)((x - Plot.Left) / (Plot.Width / bars.Count)), 0, bars.Count - 1);
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }
}

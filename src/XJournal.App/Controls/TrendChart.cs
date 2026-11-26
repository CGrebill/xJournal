using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XJournal.App.Controls;

/// <summary>One answer on the chart. <see cref="Reworded"/> marks the first answer to a new wording.</summary>
public sealed record TrendPoint(DateOnly Day, double Value, string ValueLabel, string Wording, bool Reworded, long? EntryId);

/// <summary>A lightweight line chart of answers over a date range, with hover details and wording-change markers.</summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<TrendPoint>?> PointsProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<TrendPoint>?>(nameof(Points));

    public static readonly StyledProperty<DateOnly> FromProperty =
        AvaloniaProperty.Register<TrendChart, DateOnly>(nameof(From));

    public static readonly StyledProperty<DateOnly> ToProperty =
        AvaloniaProperty.Register<TrendChart, DateOnly>(nameof(To));

    /// <summary>Bottom and top of the value axis.</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(Minimum), 1);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TrendChart, double>(nameof(Maximum), 5);

    private const double Left = 44, Right = 16, Top = 22, Bottom = 30, DotRadius = 4;
    private int _hover = -1;

    static TrendChart()
    {
        AffectsRender<TrendChart>(PointsProperty, FromProperty, ToProperty, MinimumProperty, MaximumProperty);
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public TrendChart()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public IReadOnlyList<TrendPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public DateOnly From { get => GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateOnly To { get => GetValue(ToProperty); set => SetValue(ToProperty, value); }

    /// <summary>Raised when a point is clicked.</summary>
    public event Action<TrendPoint>? PointActivated;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty) _hover = -1;
    }

    private Rect Plot => new(Left, Top, Math.Max(1, Bounds.Width - Left - Right), Math.Max(1, Bounds.Height - Top - Bottom));

    private double X(DateOnly day)
    {
        var span = Math.Max(1, To.DayNumber - From.DayNumber);
        return Plot.Left + Plot.Width * (day.DayNumber - From.DayNumber) / span;
    }

    private double Y(double value) => Plot.Bottom - Plot.Height * (value - Minimum) / Math.Max(1e-9, Maximum - Minimum);

    /// <summary>Distance between grid lines: every whole number for short ranges, otherwise 1, 2 or 5 × a power of ten.</summary>
    internal static double TickStep(double range)
    {
        if (range <= 10) return 1;
        var raw = range / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var step in new[] { 1, 2, 5, 10 })
            if (step * magnitude >= raw) return step * magnitude;
        return 10 * magnitude;
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < Left + Right + 40 || Bounds.Height < Top + Bottom + 20) return; // no room to draw
        var text = Brush("TextFillColorSecondaryBrush", Brushes.Gray);
        var primary = Brush("TextFillColorPrimaryBrush", Brushes.Black);
        var grid = new Pen(Brush("DividerStrokeColorDefaultBrush", Brushes.LightGray), 1);
        var accent = Brush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
        var surface = Brush("SolidBackgroundFillColorQuarternaryBrush", Brushes.White);
        var typeface = new Typeface(FontFamily.Default);
        var plot = Plot;

        // Horizontal grid lines and value labels.
        var tick = TickStep(Maximum - Minimum);
        for (var v = Minimum; v <= Maximum + tick / 1000; v += tick)
        {
            var y = Y(v);
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(v.ToString("#,0.##", CultureInfo.CurrentCulture), 12, text, typeface);
            context.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }

        // Date labels.
        var days = Math.Max(1, To.DayNumber - From.DayNumber);
        var ticks = Math.Clamp((int)(plot.Width / 110), 2, 8);
        for (var i = 0; i <= ticks; i++)
        {
            var day = DateOnly.FromDayNumber(From.DayNumber + (int)Math.Round(days * i / (double)ticks));
            var label = Text(day.ToString(days > 400 ? "MMM yyyy" : "MMM d", CultureInfo.CurrentCulture), 12, text, typeface);
            var x = Math.Clamp(X(day) - label.Width / 2, plot.Left - 8, Math.Max(plot.Left - 8, plot.Right - label.Width + 8));
            context.DrawText(label, new Point(x, plot.Bottom + 8));
        }

        var points = Points ?? [];
        if (points.Count == 0)
        {
            var empty = Text("No answers in this period", 14, text, typeface);
            context.DrawText(empty, new Point(plot.Center.X - empty.Width / 2, plot.Center.Y - empty.Height / 2));
            return;
        }

        // Wording changes.
        var dashed = new Pen(text, 1, new DashStyle([4, 4], 0));
        foreach (var p in points.Where(p => p.Reworded))
        {
            var x = X(p.Day);
            context.DrawLine(dashed, new Point(x, plot.Top - 6), new Point(x, plot.Bottom));
            var label = Text("reworded", 11, text, typeface);
            var labelX = x + 4 + label.Width > Bounds.Width ? x - 4 - label.Width : x + 4;
            context.DrawText(label, new Point(labelX, plot.Top - label.Height - 4));
        }

        // Line and points.
        var line = new Pen(accent, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        for (var i = 1; i < points.Count; i++)
            context.DrawLine(line, new Point(X(points[i - 1].Day), Y(points[i - 1].Value)), new Point(X(points[i].Day), Y(points[i].Value)));
        var ring = new Pen(surface, 2);
        for (var i = 0; i < points.Count; i++)
        {
            var r = i == _hover ? DotRadius + 2 : DotRadius;
            context.DrawEllipse(accent, ring, new Point(X(points[i].Day), Y(points[i].Value)), r, r);
        }

        if (_hover >= 0 && _hover < points.Count) DrawTooltip(context, points[_hover], primary, text, surface, typeface);
    }

    private void DrawTooltip(DrawingContext context, TrendPoint p, IBrush primary, IBrush secondary, IBrush surface, Typeface typeface)
    {
        const double pad = 10, width = 260;
        var title = Text($"{p.Day:ddd, MMM d, yyyy}", 12, secondary, typeface);
        var value = Text(p.ValueLabel, 14, primary, new Typeface(FontFamily.Default, weight: FontWeight.SemiBold));
        var wording = Text($"“{p.Wording}”", 12, secondary, typeface, width - pad * 2);
        var height = pad * 2 + title.Height + value.Height + wording.Height + 8;

        var anchor = new Point(X(p.Day), Y(p.Value));
        var x = anchor.X + 12 + width > Bounds.Width ? anchor.X - 12 - width : anchor.X + 12;
        var y = Math.Clamp(anchor.Y - height / 2, 0, Math.Max(0, Bounds.Height - height));
        var box = new Rect(x, y, width, height);
        context.DrawRectangle(surface, new Pen(Brush("CardStrokeColorDefaultBrush", Brushes.LightGray), 1), box, 6, 6,
            new BoxShadows(new BoxShadow { Blur = 12, OffsetY = 4, Color = Color.FromArgb(50, 0, 0, 0) }));
        var cursor = new Point(x + pad, y + pad);
        context.DrawText(title, cursor);
        cursor += new Point(0, title.Height + 2);
        context.DrawText(value, cursor);
        cursor += new Point(0, value.Height + 6);
        context.DrawText(wording, cursor);
    }

    private static FormattedText Text(string s, double size, IBrush brush, Typeface typeface, double maxWidth = double.PositiveInfinity)
    {
        var text = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
        if (!double.IsInfinity(maxWidth)) text.MaxTextWidth = maxWidth;
        return text;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var points = Points ?? [];
        var best = -1;
        var bestDistance = 24.0;
        for (var i = 0; i < points.Count; i++)
        {
            var distance = Math.Abs(X(points[i].Day) - position.X);
            if (distance < bestDistance || (distance == bestDistance && best >= 0 &&
                Math.Abs(Y(points[i].Value) - position.Y) < Math.Abs(Y(points[best].Value) - position.Y)))
            {
                best = i;
                bestDistance = distance;
            }
        }
        if (best == _hover) return;
        _hover = best;
        Cursor = new Cursor(best >= 0 && points[best].EntryId is not null ? StandardCursorType.Hand : StandardCursorType.Arrow);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var points = Points ?? [];
        if (_hover >= 0 && _hover < points.Count) PointActivated?.Invoke(points[_hover]);
    }
}

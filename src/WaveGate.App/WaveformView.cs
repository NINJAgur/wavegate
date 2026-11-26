using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WaveGate.Overlay;

/// <summary>A clip's waveform as bars in its colour; the played part lights up, so it doubles as the progress bar.</summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = Register(nameof(Peaks), typeof(IReadOnlyList<float>), null);
    public static readonly DependencyProperty ProgressProperty = Register(nameof(Progress), typeof(double), 0.0);
    public static readonly DependencyProperty ColorProperty = Register(nameof(Color), typeof(Color), Colors.White);
    public static readonly DependencyProperty IsPlayingProperty = Register(nameof(IsPlaying), typeof(bool), false);
    public static readonly DependencyProperty IsPausedProperty = Register(nameof(IsPaused), typeof(bool), false);
    public static readonly DependencyProperty IsHighlightedProperty = Register(nameof(IsHighlighted), typeof(bool), false);
    public static readonly DependencyProperty BarWidthProperty = Register(nameof(BarWidth), typeof(double), 3.0);
    public static readonly DependencyProperty ShowPlayheadProperty = Register(nameof(ShowPlayhead), typeof(bool), false);

    // Not float[]: the XAML compiler rejects binding array-typed properties inside templates.
    public IReadOnlyList<float>? Peaks { get => (IReadOnlyList<float>?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public bool IsPaused { get => (bool)GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }
    public bool IsHighlighted { get => (bool)GetValue(IsHighlightedProperty); set => SetValue(IsHighlightedProperty, value); }
    public double BarWidth { get => (double)GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }

    /// <summary>Draws a playhead at the current position while the pointer is over a playing waveform (for seeking).</summary>
    public bool ShowPlayhead { get => (bool)GetValue(ShowPlayheadProperty); set => SetValue(ShowPlayheadProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? fallback) =>
        DependencyProperty.Register(name, type, typeof(WaveformView),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole strip takes the mouse, not just the bars

        const double gap = 2;
        int n = Math.Max(1, (int)((w + gap) / (BarWidth + gap)));
        double bar = (w - gap * (n - 1)) / n;
        bool active = IsPlaying || IsPaused;

        var played = Brush(Color, IsPaused ? 0.6 : 1);
        var unplayed = Brush(Colors.White, IsPaused ? 0.10 : 0.13);
        var idle = Brush(Color, IsHighlighted ? 0.55 : 0.38);

        var peaks = Peaks;
        for (int i = 0; i < n; i++)
        {
            double level = peaks is { Count: > 0 } ? Level(peaks, i, n) : 0.15; // flat line until analysed
            double bh = Math.Max(2, level * h);
            var brush = !active ? idle : (i + 0.5) / n <= Progress ? played : unplayed;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (bar + gap), (h - bh) / 2, bar, bh), 1, 1);
        }

        if (ShowPlayhead && active && IsMouseOver)
        {
            double x = Math.Clamp(Progress * w, 1, w - 1);
            dc.DrawRectangle(Brushes.White, null, new Rect(x - 1, -3, 2, h + 6));
        }
    }

    private static double Level(IReadOnlyList<float> peaks, int i, int n)
    {
        int from = i * peaks.Count / n, to = Math.Max(from + 1, (i + 1) * peaks.Count / n);
        float max = 0;
        for (int j = from; j < to && j < peaks.Count; j++) max = Math.Max(max, peaks[j]);
        return max;
    }

    private static SolidColorBrush Brush(Color c, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}

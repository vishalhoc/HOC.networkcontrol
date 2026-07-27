using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace WinNetControl.Controls;

/// <summary>
/// Smooth sparkline chart drawn using a Canvas + Polyline + fill Polygon.
/// Data arrives already in chronological order (oldest→newest) from ProcessNetworkInfo.SpeedHistory.
/// The DependencyProperty fires OnSamplesChanged on every new array reference, triggering Redraw().
/// </summary>
public sealed partial class SparklineControl : UserControl
{
    // ── Dependency property ───────────────────────────────────────────────────
    public static readonly DependencyProperty SpeedSamplesProperty =
        DependencyProperty.Register(
            nameof(SpeedSamples),
            typeof(IReadOnlyList<double>),
            typeof(SparklineControl),
            new PropertyMetadata(null, OnSamplesChanged));

    public IReadOnlyList<double>? SpeedSamples
    {
        get => (IReadOnlyList<double>?)GetValue(SpeedSamplesProperty);
        set => SetValue(SpeedSamplesProperty, value);
    }

    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SparklineControl)d).Redraw();

    // ── Visuals ────────────────────────────────────────────────────────────────
    private readonly Polyline _line = new();
    private readonly Polygon  _fill = new();

    // Accent colours — updated when theme changes
    private Color _lineColor = Color.FromArgb(255, 0, 120, 212);
    private Color _fillColor = Color.FromArgb(50,  0, 120, 212);

    // Speed thresholds for colour coding (KB/s)
    private const double ThresholdHigh = 1024;   // ≥ 1 MB/s  → orange/red
    private const double ThresholdMed  = 128;    // ≥ 128 KB/s → yellow

    public SparklineControl()
    {
        this.InitializeComponent();

        _fill.Stroke          = null;
        _fill.StrokeThickness = 0;

        _line.StrokeThickness    = 1.5;
        _line.StrokeLineJoin     = PenLineJoin.Round;
        _line.StrokeStartLineCap = PenLineCap.Round;
        _line.StrokeEndLineCap   = PenLineCap.Round;

        SparkCanvas.Children.Add(_fill);
        SparkCanvas.Children.Add(_line);

        ActualThemeChanged += (_, __) => { ApplyThemeColors(0); Redraw(); };
        Loaded             += (_, __) => { ApplyThemeColors(0); Redraw(); };

        // IMP#19 / Bug#26: trigger Redraw when the canvas gets a real size for
        // the first time. Without this the graph stays blank until the next ETW
        // data tick because ActualWidth/Height are 0 at construction time.
        SizeChanged += (_, __) => Redraw();
    }

    // ── Theme / colour ─────────────────────────────────────────────────────────
    private void ApplyThemeColors(double peakKbps)
    {
        bool dark = ActualTheme == ElementTheme.Dark;

        Color accent;
        if (peakKbps >= ThresholdHigh)
        {
            accent      = Color.FromArgb(255, 232, 80,  0);   // orange-red (high)
            _fillColor  = Color.FromArgb(dark ? (byte)55 : (byte)35, 232, 80, 0);
        }
        else if (peakKbps >= ThresholdMed)
        {
            accent      = Color.FromArgb(255, 0, 180, 100);   // teal-green (medium)
            _fillColor  = Color.FromArgb(dark ? (byte)55 : (byte)35, 0, 180, 100);
        }
        else
        {
            accent      = Color.FromArgb(255, 0, 120, 212);   // blue (low/idle)
            _fillColor  = Color.FromArgb(dark ? (byte)45 : (byte)25, 0, 120, 212);
        }

        _lineColor     = accent;
        _line.Stroke   = new SolidColorBrush(_lineColor);
        _fill.Fill     = new SolidColorBrush(_fillColor);
    }

    // ── Redraw ─────────────────────────────────────────────────────────────────
    private void Redraw()
    {
        _line.Points.Clear();
        _fill.Points.Clear();

        var samples = SpeedSamples;
        if (samples == null || samples.Count == 0) return;

        double w = SparkCanvas.ActualWidth;
        double h = SparkCanvas.ActualHeight;
        if (w <= 0) w = SparkCanvas.Width;
        if (h <= 0) h = SparkCanvas.Height;
        if (w <= 0 || h <= 0) return;

        int n = samples.Count;

        // Find the peak in the visible window for scaling
        double max = samples.Max();

        // Update accent colour based on current peak (live colour coding)
        ApplyThemeColors(max);

        // If all zero — draw a flat baseline and return
        if (max <= 0)
        {
            double baseY = h - 1;
            _line.Points.Add(new Windows.Foundation.Point(0,   baseY));
            _line.Points.Add(new Windows.Foundation.Point(w,   baseY));
            _fill.Points.Add(new Windows.Foundation.Point(0,   baseY));
            _fill.Points.Add(new Windows.Foundation.Point(w,   baseY));
            _fill.Points.Add(new Windows.Foundation.Point(w,   h));
            _fill.Points.Add(new Windows.Foundation.Point(0,   h));
            return;
        }

        double step  = w / Math.Max(n - 1, 1);
        double padT  = 2.0;   // top padding so the line doesn't clip
        double drawH = h - padT - 1;

        // Build points (data is already oldest→newest from SpeedHistory)
        var pts = new List<Windows.Foundation.Point>(n);
        for (int i = 0; i < n; i++)
        {
            double x = i * step;
            double y = padT + drawH - (samples[i] / max * drawH);
            pts.Add(new Windows.Foundation.Point(x, y));
        }

        foreach (var pt in pts) _line.Points.Add(pt);

        // Fill polygon: line path + bottom-right corner + bottom-left corner
        foreach (var pt in pts) _fill.Points.Add(pt);
        _fill.Points.Add(new Windows.Foundation.Point(w, h));
        _fill.Points.Add(new Windows.Foundation.Point(0, h));
    }
}

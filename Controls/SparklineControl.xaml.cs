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
/// Minimal sparkline chart drawn using a Canvas + Polyline.
/// Bind <see cref="SpeedSamples"/> to ProcessNetworkInfo.SpeedHistory.
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

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly Polyline _line   = new();
    private readonly Polygon  _fill   = new();

    public SparklineControl()
    {
        this.InitializeComponent();

        // Fill polygon (semi-transparent)
        _fill.Stroke          = null;
        _fill.StrokeThickness = 0;

        // Line on top
        _line.StrokeThickness = 1.5;
        _line.StrokeLineJoin  = PenLineJoin.Round;
        _line.StrokeStartLineCap = PenLineCap.Round;
        _line.StrokeEndLineCap   = PenLineCap.Round;

        SparkCanvas.Children.Add(_fill);
        SparkCanvas.Children.Add(_line);

        ActualThemeChanged += (_, __) => ApplyThemeColors();
        Loaded += (_, __) => { ApplyThemeColors(); Redraw(); };
    }

    private void ApplyThemeColors()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var accent   = Color.FromArgb(255, 0, 120, 212);
        var fillClr  = Color.FromArgb(dark ? (byte)50 : (byte)30, 0, 120, 212);
        _line.Stroke = new SolidColorBrush(accent);
        _fill.Fill   = new SolidColorBrush(fillClr);
    }

    private void Redraw()
    {
        _line.Points.Clear();
        _fill.Points.Clear();

        var samples = SpeedSamples;
        if (samples == null || samples.Count == 0) return;

        double w = SparkCanvas.Width;
        double h = SparkCanvas.Height;

        double max = samples.Max();
        if (max <= 0) return;

        int n = samples.Count;
        double step = w / Math.Max(n - 1, 1);

        // Build line points (ordered by index from oldest→newest)
        var pts = new List<Windows.Foundation.Point>();
        for (int i = 0; i < n; i++)
        {
            double x = i * step;
            double y = h - (samples[i] / max * (h - 2)) - 1;
            pts.Add(new Windows.Foundation.Point(x, y));
        }

        foreach (var pt in pts)         _line.Points.Add(pt);

        // Fill: same points + bottom-right + bottom-left
        foreach (var pt in pts)         _fill.Points.Add(pt);
        _fill.Points.Add(new Windows.Foundation.Point(w, h));
        _fill.Points.Add(new Windows.Foundation.Point(0, h));
    }
}

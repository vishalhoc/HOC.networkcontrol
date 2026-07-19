using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;

namespace WinNetControl.Pages;

public class AdapterStats
{
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string UpSpeed     { get; set; } = "0 KB/s";
    public string DownSpeed   { get; set; } = "0 KB/s";
    public string TotalSent   { get; set; } = "0 B";
    public string TotalRecv   { get; set; } = "0 B";
}

public sealed partial class MonitoringPage : Page
{
    private MainViewModel? _vm;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Graph history
    private const int HistoryMax = 60;
    private readonly List<double> _upHistory   = new();
    private readonly List<double> _downHistory = new();
    private double _peakUp, _peakDown;

    // Per-adapter tracking
    private readonly Dictionary<string, (long prevSent, long prevRecv, long totalSent, long totalRecv)> _adapterSnapshots = new();
    private readonly ObservableCollection<AdapterStats> _adapterStats = new();

    public MonitoringPage()
    {
        this.InitializeComponent();
        AdapterList.ItemsSource = _adapterStats;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
        {
            _vm = vm;
            TopProcessList.ItemsSource = vm.FilteredProcesses;
        }

        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, null!);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _timer.Stop();
    }

    private void OnTick(object? sender, object e)
    {
        if (_vm == null) return;

        // Hero cards
        double up   = _vm.GlobalUploadSpeed;
        double down = _vm.GlobalDownloadSpeed;
        HeroUpload.Text   = _vm.GlobalUploadText;
        HeroDownload.Text = _vm.GlobalDownloadText;

        if (up   > _peakUp)   _peakUp   = up;
        if (down > _peakDown) _peakDown = down;

        HeroUploadPeak.Text   = $"Peak: {FormatSpeed(_peakUp)}";
        HeroDownloadPeak.Text = $"Peak: {FormatSpeed(_peakDown)}";
        HeroSent.Text = _vm.GlobalTotalText;
        HeroRecv.Text = _vm.GlobalTotalText;

        // History
        _upHistory.Add(up);
        _downHistory.Add(down);
        if (_upHistory.Count   > HistoryMax) _upHistory.RemoveAt(0);
        if (_downHistory.Count > HistoryMax) _downHistory.RemoveAt(0);

        DrawGraph();
        RefreshAdapters();
    }

    // ── Bandwidth graph ───────────────────────────────────────────────────────
    private void DrawGraph()
    {
        BandwidthCanvas.Children.Clear();
        double w = BandwidthCanvas.ActualWidth;
        double h = BandwidthCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _upHistory.Count < 2) return;

        double maxVal = Math.Max(
            Math.Max(_upHistory.Max(), _downHistory.Max()), 1);

        double stepX = w / (HistoryMax - 1);

        // Grid lines + labels
        for (int g = 0; g <= 4; g++)
        {
            double y = h * g / 4;
            var gridLine = new Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 128, 128, 128)),
                StrokeThickness = 1
            };
            BandwidthCanvas.Children.Add(gridLine);

            double val = maxVal * (4 - g) / 4;
            var lbl = new TextBlock
            {
                Text     = FormatSpeed(val),
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 128, 128, 128)),
                Margin = new Thickness(2, 0, 0, 0)
            };
            Canvas.SetLeft(lbl, 0);
            Canvas.SetTop(lbl, y + 1);
            BandwidthCanvas.Children.Add(lbl);
        }

        // Upload fill area (light blue)
        DrawFilledArea(_upHistory, w, h, maxVal, stepX,
            Windows.UI.Color.FromArgb(35, 0, 120, 212),
            Windows.UI.Color.FromArgb(200, 0, 120, 212));

        // Download fill area (light green)
        DrawFilledArea(_downHistory, w, h, maxVal, stepX,
            Windows.UI.Color.FromArgb(35, 16, 124, 16),
            Windows.UI.Color.FromArgb(200, 16, 124, 16));
    }

    private void DrawFilledArea(List<double> history, double w, double h,
        double maxVal, double stepX,
        Windows.UI.Color fillColor, Windows.UI.Color lineColor)
    {
        if (history.Count < 2) return;

        int offset = HistoryMax - history.Count;

        // Build polyline
        var poly = new Polyline
        {
            Stroke          = new SolidColorBrush(lineColor),
            StrokeThickness = 2,
            StrokeLineJoin  = PenLineJoin.Round
        };

        var fillPoly = new Polygon
        {
            Fill = new SolidColorBrush(fillColor)
        };

        // Bottom-left anchor
        double startX = offset * stepX;
        fillPoly.Points.Add(new Windows.Foundation.Point(startX, h));

        for (int i = 0; i < history.Count; i++)
        {
            double x = (offset + i) * stepX;
            double y = h - (history[i] / maxVal * h * 0.92);
            poly.Points.Add(new Windows.Foundation.Point(x, y));
            fillPoly.Points.Add(new Windows.Foundation.Point(x, y));
        }

        // Bottom-right anchor
        fillPoly.Points.Add(new Windows.Foundation.Point((offset + history.Count - 1) * stepX, h));

        BandwidthCanvas.Children.Add(fillPoly);
        BandwidthCanvas.Children.Add(poly);

        // Latest value dot
        if (history.Count > 0)
        {
            double lx = (offset + history.Count - 1) * stepX;
            double ly = h - (history[^1] / maxVal * h * 0.92);
            var dot = new Ellipse
            {
                Width = 7, Height = 7,
                Fill  = new SolidColorBrush(lineColor)
            };
            Canvas.SetLeft(dot, lx - 3.5);
            Canvas.SetTop(dot, ly - 3.5);
            BandwidthCanvas.Children.Add(dot);
        }
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    // ── Per-adapter stats ─────────────────────────────────────────────────────
    private void RefreshAdapters()
    {
        try
        {
            var ifaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.OperationalStatus == OperationalStatus.Up)
                .ToList();

            var updated = new List<AdapterStats>();
            foreach (var ni in ifaces)
            {
                try
                {
                    var stats = ni.GetIPv4Statistics();
                    long sent = stats.BytesSent;
                    long recv = stats.BytesReceived;

                    double upRate = 0, downRate = 0;
                    long totSent = 0, totRecv = 0;

                    if (_adapterSnapshots.TryGetValue(ni.Id, out var snap))
                    {
                        upRate   = Math.Max(0, sent - snap.prevSent);
                        downRate = Math.Max(0, recv - snap.prevRecv);
                        totSent  = snap.totalSent + (long)upRate;
                        totRecv  = snap.totalRecv + (long)downRate;
                    }

                    _adapterSnapshots[ni.Id] = (sent, recv, totSent, totRecv);

                    updated.Add(new AdapterStats
                    {
                        Name        = ni.Name,
                        Description = ni.Description,
                        UpSpeed     = $"↑ {FormatSpeed(upRate)}",
                        DownSpeed   = $"↓ {FormatSpeed(downRate)}",
                        TotalSent   = FormatSize(totSent),
                        TotalRecv   = FormatSize(totRecv)
                    });
                }
                catch { }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                _adapterStats.Clear();
                foreach (var a in updated) _adapterStats.Add(a);
            });
        }
        catch { }
    }

    // ── Controls ──────────────────────────────────────────────────────────────
    private void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        int secs = IntervalCombo.SelectedIndex switch
        {
            1 => 2,
            2 => 5,
            _ => 1
        };
        _timer.Interval = TimeSpan.FromSeconds(secs);
    }

    private void OnPauseToggle(object sender, RoutedEventArgs e)
    {
        if (PauseBtn.IsChecked == true)
        {
            _timer.Stop();
            PauseIcon.Glyph = "\uE768";
            PauseBtnText.Text = "Resume";
        }
        else
        {
            _timer.Start();
            PauseIcon.Glyph = "\uE769";
            PauseBtnText.Text = "Pause";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:F1} MB/s";
        if (bytesPerSec >= 1_024)    return $"{bytesPerSec / 1_024:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}

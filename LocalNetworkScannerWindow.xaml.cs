using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using WinNetControl.Core;
using WinUIEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl;

public sealed partial class LocalNetworkScannerWindow : Window
{
    private readonly LocalNetworkScannerService _scanner = new();
    private CancellationTokenSource? _cts;
    private readonly List<LocalNetworkDevice> _devices = new();
    private readonly List<(CheckBox cb, LocalNetworkDevice dev)> _rows = new();

    public LocalNetworkScannerWindow()
    {
        this.InitializeComponent();
        this.SetWindowSize(1060, 680);
        this.Title = "Local Network Scanner — WinNetControl";
        try { this.SetIcon("Assets\\AppIcon.ico"); } catch { }
    }

    // ── Scan ────────────────────────────────────────────────────────────────
    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            // Cancel running scan
            _cts.Cancel();
            _cts = null;
            ScanButtonText.Text = "Scan Network";
            ScanIcon.Glyph      = "\uE8B3";
            ScanProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = "Scan cancelled.";
            return;
        }

        _cts = new CancellationTokenSource();
        _devices.Clear();
        _rows.Clear();
        DevicesPanel.Children.Clear();
        DeviceCountBadge.Text = "Scanning…";
        ScanButtonText.Text   = "Cancel";
        ScanIcon.Glyph        = "\uE711";
        ScanProgress.Value    = 0;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Scanning local network…";

        var progress = new Progress<(int done, int total)>(p =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                double pct = p.total > 0 ? (double)p.done / p.total * 100 : 0;
                ScanProgress.Value = pct;
                StatusText.Text    = $"Scanning… {p.done}/{p.total} addresses checked";
            });
        });

        try
        {
            var found = await _scanner.ScanAsync(progress, _cts.Token);
            DispatcherQueue.TryEnqueue(() =>
            {
                _devices.AddRange(found);
                foreach (var dev in found)
                    AddDeviceRow(dev);

                UpdateStatusBar();
                DeviceCountBadge.Text   = $"{found.Count} device{(found.Count == 1 ? "" : "s")} found";
                ScanProgress.Visibility = Visibility.Collapsed;
                ScanButtonText.Text     = "Scan Again";
                ScanIcon.Glyph          = "\uE8B3";
                StatusText.Text         = $"Scan complete — {found.Count} device(s) online.";
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts = null;
        }
    }

    // ── Build device row ────────────────────────────────────────────────────
    private void AddDeviceRow(LocalNetworkDevice dev)
    {
        var border = new Border
        {
            Padding         = new Thickness(12, 8, 12, 8),
            BorderBrush     = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 128, 128, 128)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background      = dev.IsGateway
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(15, 0, 120, 212))
                : null!,
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

        // Checkbox
        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
        Grid.SetColumn(cb, 0);

        // Status dot
        var dot = new Ellipse
        {
            Width  = 8, Height = 8,
            Fill   = new SolidColorBrush(dev.IsOnline ? Color.FromArgb(255, 16, 124, 16) : Color.FromArgb(255, 150, 150, 150)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dot, 1);

        // IP
        var ipBlock = new TextBlock
        {
            Text              = dev.IpAddress,
            FontSize          = 12,
            FontWeight        = dev.IsGateway ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = dev.IsGateway
                ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))
                : (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };
        Grid.SetColumn(ipBlock, 2);

        // Hostname
        var hostBlock = new TextBlock
        {
            Text              = dev.Hostname == dev.IpAddress ? "–" : dev.Hostname,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.FromArgb(180, 150, 150, 150)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(hostBlock, 3);

        // MAC
        var macBlock = new TextBlock
        {
            Text              = string.IsNullOrEmpty(dev.MacAddress) ? "–" : dev.MacAddress,
            FontSize          = 11,
            FontFamily        = new FontFamily("Consolas"),
            Foreground        = new SolidColorBrush(Color.FromArgb(180, 150, 150, 150)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(macBlock, 4);

        // Device type / vendor
        var typeBlock = new TextBlock
        {
            Text              = dev.DeviceType,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(typeBlock, 5);

        // Latency
        var latencyBlock = new TextBlock
        {
            Text              = dev.IsOnline ? $"{dev.LatencyMs} ms" : "–",
            FontSize          = 11,
            Foreground        = LatencyColor(dev.LatencyMs),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(latencyBlock, 6);

        // Block toggle
        var blockToggle = new ToggleButton
        {
            IsChecked = dev.IsBlocked,
            Padding   = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(blockToggle, "Block/unblock this device's traffic");
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var blockIcon = new FontIcon { Glyph = dev.IsBlocked ? "\uE72E" : "\uF140", FontSize = 12 };
        var blockText = new TextBlock { Text = dev.IsBlocked ? "Blocked" : "Block", FontSize = 11 };
        toggleContent.Children.Add(blockIcon);
        toggleContent.Children.Add(blockText);
        blockToggle.Content = toggleContent;

        blockToggle.Click += (s, _) =>
        {
            bool nowBlocked = blockToggle.IsChecked == true;
            if (nowBlocked) LocalNetworkScannerService.BlockDevice(dev.IpAddress);
            else            LocalNetworkScannerService.UnblockDevice(dev.IpAddress);
            dev.IsBlocked   = nowBlocked;
            blockIcon.Glyph = nowBlocked ? "\uE72E" : "\uF140";
            blockText.Text  = nowBlocked ? "Blocked" : "Block";
            UpdateStatusBar();
        };
        Grid.SetColumn(blockToggle, 8);

        grid.Children.Add(cb);
        grid.Children.Add(dot);
        grid.Children.Add(ipBlock);
        grid.Children.Add(hostBlock);
        grid.Children.Add(macBlock);
        grid.Children.Add(typeBlock);
        grid.Children.Add(latencyBlock);
        grid.Children.Add(blockToggle);

        border.Child = grid;
        DevicesPanel.Children.Add(border);
        _rows.Add((cb, dev));
    }

    private static SolidColorBrush LatencyColor(long ms)
    {
        if (ms <= 5)   return new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));
        if (ms <= 30)  return new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        if (ms <= 100) return new SolidColorBrush(Color.FromArgb(255, 255, 165, 0));
        return             new SolidColorBrush(Color.FromArgb(255, 204, 51, 0));
    }

    // ── Bulk actions ────────────────────────────────────────────────────────
    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        bool check = SelectAllCheckBox.IsChecked == true;
        foreach (var (cb, _) in _rows) cb.IsChecked = check;
    }

    private async void OnBlockAllSelectedClicked(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.cb.IsChecked == true).Select(r => r.dev).ToList();
        if (selected.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title          = "Block Selected Devices?",
            Content        = $"This will block all network traffic to/from {selected.Count} device(s) using Windows Firewall rules.",
            PrimaryButtonText   = "Block All",
            CloseButtonText     = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await Task.Run(() =>
        {
            foreach (var dev in selected.Where(d => !d.IsBlocked))
                LocalNetworkScannerService.BlockDevice(dev.IpAddress);
        });

        // Refresh list
        OnScanClicked(sender, e);
    }

    private void OnUnblockAllClicked(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            foreach (var dev in _devices.Where(d => d.IsBlocked))
                LocalNetworkScannerService.UnblockDevice(dev.IpAddress);
        });
        StatusText.Text = "Unblocked all WNC-managed device rules.";
    }

    // ── Status bar ──────────────────────────────────────────────────────────
    private void UpdateStatusBar()
    {
        int online  = _devices.Count(d => d.IsOnline);
        int blocked = _devices.Count(d => d.IsBlocked);
        OnlineCountText.Text  = $"{online} online";
        BlockedCountText.Text = $"{blocked} blocked";
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI;
using WinNetControl.Models;
using WinUIEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using Windows.UI;

namespace WinNetControl;

/// <summary>
/// Per-app network path visualizer — draws a tree showing how a request travels
/// from the app through Firewall → AdGuard → Adapter → Gateway → DNS → Remote servers,
/// and the response path back.
/// </summary>
public sealed partial class NetworkMapWindow : Window
{
    private ProcessNetworkInfo? _process;

    // Node geometry constants
    private const double NodeW    = 160;
    private const double NodeH    = 64;
    private const double ColGap   = 80;
    private const double RowGap   = 20;
    private const double StartX   = 20;
    private const double StartY   = 30;

    public NetworkMapWindow(ProcessNetworkInfo process)
    {
        this.InitializeComponent();
        _process = process;
        this.SetWindowSize(1420, 760);
        this.Title = $"Network Map — {process.ProcessName}";
        try { this.SetIcon("Assets\\AppIcon.ico"); } catch { }

        AppNameHeader.Text = $"Network Map — {process.ProcessName}";
        AppSubtitle.Text   = $"PID {process.ProcessId}  ·  {process.CurrentConnections?.Count ?? 0} connection(s)  ·  {process.AdapterName}";

        BuildMap();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => BuildMap();

    // ── Map builder ──────────────────────────────────────────────────────────
    private void BuildMap()
    {
        MapCanvas.Children.Clear();
        if (_process == null) return;

        // ── Detect network environment ───────────────────────────────────
        string  adapterName = _process.AdapterName ?? GetAdapterName();
        string  gateway     = GetDefaultGateway();
        string  dnsServer   = GetDnsServer();
        bool    firewallOn  = IsFirewallEnabled();
        bool    adguardOn   = IsAdGuardRunning();
        bool    appBlocked  = _process.IsBlocked;

        var remoteHosts = (_process.CurrentConnections ?? new())
            .Where(c => !string.IsNullOrEmpty(c.RemoteAddress) && c.RemoteAddress != "0.0.0.0")
            .GroupBy(c => c.RemoteAddress)
            .Take(8)
            .Select(g => (ip: g.Key, blocked: g.First().IsBlocked, suspicious: g.First().IsSuspicious, port: g.First().RemotePort,
                          country: g.First().GeoCountry ?? ""))
            .ToList();

        ConnectionCountText.Text = $"{remoteHosts.Count} remote endpoint(s)";

        // ── Build column layout ───────────────────────────────────────────
        // Col 0: App
        // Col 1: Firewall
        // Col 2: AdGuard (optional)
        // Col 3: Adapter
        // Col 4: Gateway/Router
        // Col 5: DNS
        // Col 6+: Remote hosts

        double col0x = StartX;
        double col1x = col0x + NodeW + ColGap;
        double col2x = col1x + NodeW + ColGap;
        double col3x = adguardOn ? col2x + NodeW + ColGap : col2x;
        double col4x = col3x + NodeW + ColGap;
        double col5x = col4x + NodeW + ColGap;
        double col6x = col5x + NodeW + ColGap;

        double centerY = 320;

        // ── Draw REQUEST path label ───────────────────────────────────────
        DrawLabel("REQUEST →", col0x, centerY - NodeH - 20, "#0078D4");
        DrawLabel("← RESPONSE", col0x, centerY + NodeH + 30, "#107C10");

        // ── App node ─────────────────────────────────────────────────────
        var appColor = appBlocked ? "#CC3300" : "#107C10";
        DrawNode(MapCanvas, col0x, centerY - NodeH / 2,
                 icon: "\uE8EF",
                 title: _process.ProcessName,
                 subtitle: $"PID {_process.ProcessId}",
                 statusColor: appColor,
                 statusText: appBlocked ? "BLOCKED" : "ACTIVE",
                 tooltip: _process.ProcessPath ?? "");

        // ── Firewall node ─────────────────────────────────────────────────
        var fwColor = !firewallOn ? "#FF8C00" : appBlocked ? "#CC3300" : "#107C10";
        DrawNode(MapCanvas, col1x, centerY - NodeH / 2,
                 icon: "\uE7BA",
                 title: "Windows Firewall",
                 subtitle: firewallOn ? "Enabled" : "⚠ Disabled",
                 statusColor: fwColor,
                 statusText: appBlocked ? "BLOCKING" : firewallOn ? "PASS" : "OFF");

        DrawArrow(col0x + NodeW, centerY, col1x, centerY, appBlocked ? "#CC3300" : "#0078D4", label: "TCP/UDP");

        // ── AdGuard node (optional) ────────────────────────────────────────
        double nextX;
        if (adguardOn)
        {
            DrawNode(MapCanvas, col2x, centerY - NodeH / 2,
                     icon: "\uE736",
                     title: "AdGuard",
                     subtitle: "DNS Filter Active",
                     statusColor: "#107C10",
                     statusText: "ACTIVE");
            DrawArrow(col1x + NodeW, centerY, col2x, centerY, "#0078D4", label: "DNS/HTTP");
            nextX = col3x;
        }
        else
        {
            nextX = col2x;  // skip AdGuard column
        }

        // ── Adapter node ──────────────────────────────────────────────────
        DrawNode(MapCanvas, nextX, centerY - NodeH / 2,
                 icon: "\uE839",
                 title: adapterName,
                 subtitle: "Network Adapter",
                 statusColor: "#107C10",
                 statusText: "UP");
        DrawArrow((adguardOn ? col2x : col1x) + NodeW, centerY, nextX, centerY, "#0078D4", label: "Packets");
        double adapterX = nextX;

        // ── Gateway / Router node ─────────────────────────────────────────
        double gwX = adapterX + NodeW + ColGap;
        DrawNode(MapCanvas, gwX, centerY - NodeH / 2,
                 icon: "\uE968",
                 title: string.IsNullOrEmpty(gateway) ? "Router/Gateway" : gateway,
                 subtitle: "Default Gateway",
                 statusColor: "#107C10",
                 statusText: "ONLINE");
        DrawArrow(adapterX + NodeW, centerY, gwX, centerY, "#0078D4", label: "LAN");

        // ── DNS Server node ────────────────────────────────────────────────
        double dnsX = gwX + NodeW + ColGap;
        DrawNode(MapCanvas, dnsX, centerY - NodeH / 2,
                 icon: "\uE8BD",
                 title: string.IsNullOrEmpty(dnsServer) ? "DNS Server" : dnsServer,
                 subtitle: "DNS Resolution",
                 statusColor: "#107C10",
                 statusText: "OK");
        DrawArrow(gwX + NodeW, centerY, dnsX, centerY, "#0078D4", label: "DNS");

        // ── Remote host nodes ──────────────────────────────────────────────
        double remX     = dnsX + NodeW + ColGap;
        int    remCount = remoteHosts.Count;
        if (remCount == 0)
        {
            // Placeholder
            DrawNode(MapCanvas, remX, centerY - NodeH / 2,
                     icon: "\uE704",
                     title: "No connections",
                     subtitle: "No remote endpoints",
                     statusColor: "#888888",
                     statusText: "IDLE");
            DrawArrow(dnsX + NodeW, centerY, remX, centerY, "#888888", label: "");
        }
        else
        {
            double totalHeight = remCount * (NodeH + RowGap) - RowGap;
            double remStartY   = centerY - totalHeight / 2;

            for (int i = 0; i < remoteHosts.Count; i++)
            {
                var (ip, blocked, suspicious, port, country) = remoteHosts[i];
                double remY = remStartY + i * (NodeH + RowGap);
                string label = $":{port}" + (string.IsNullOrEmpty(country) ? "" : $" · {country}");
                string statusColor = blocked ? "#CC3300" : suspicious ? "#FFB900" : "#107C10";
                string statusText = blocked ? "BLOCKED" : suspicious ? "SUSPICIOUS" : "ACTIVE";

                DrawNode(MapCanvas, remX, remY,
                         icon: "\uE704",
                         title: ip,
                         subtitle: label,
                         statusColor: statusColor,
                         statusText: statusText,
                         tooltip: $"IP: {ip}\nPort: {port}\nStatus: {statusText}" + (string.IsNullOrEmpty(country) ? "" : $"\nLocation: {country}"));

                // Arrow from DNS to each remote (fan out)
                DrawArrow(dnsX + NodeW, centerY,
                          remX, remY + NodeH / 2,
                          statusColor,
                          label: i == 0 ? "Internet" : "");
            }
        }

        // ── Response path (below, lighter) ────────────────────────────────
        DrawResponsePath(col0x, col1x, adapterX, gwX, dnsX, remX, centerY, remoteHosts);
    }

    private void DrawResponsePath(double appX, double fwX, double adX, double gwX,
                                   double dnsX, double remX, double cy,
                                   List<(string ip, bool blocked, bool suspicious, int port, string country)> remotes)
    {
        double responseY = cy + NodeH + 50;
        string color     = "#107C10";

        // Simplified response line — just draw a dashed line from last node back to app
        double endX = remotes.Count > 0 ? remX + NodeW : dnsX + NodeW;
        DrawDashedArrow(endX, responseY, appX, responseY, color, label: "Response");

        // Waypoint dots
        foreach (double wx in new[] { dnsX + NodeW / 2, gwX + NodeW / 2, adX + NodeW / 2, fwX + NodeW / 2 })
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(HexColor(color)) };
            Canvas.SetLeft(dot, wx - 4);
            Canvas.SetTop(dot,  responseY - 4);
            MapCanvas.Children.Add(dot);
        }
    }

    // ── Drawing primitives ────────────────────────────────────────────────────
    private void DrawNode(Canvas canvas, double x, double y,
                          string icon, string title, string subtitle,
                          string statusColor, string statusText, string tooltip = "")
    {
        var border = new Border
        {
            Width           = NodeW,
            Height          = NodeH,
            CornerRadius    = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            BorderBrush     = new SolidColorBrush(HexColor(statusColor)),
            Background      = new SolidColorBrush(Color.FromArgb(20,
                                  HexColor(statusColor).R,
                                  HexColor(statusColor).G,
                                  HexColor(statusColor).B)),
        };
        ToolTipService.SetToolTip(border, tooltip);

        var inner = new Grid { Padding = new Thickness(8) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBlock = new FontIcon
        {
            Glyph      = icon,
            FontSize   = 20,
            Foreground = new SolidColorBrush(HexColor(statusColor)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(iconBlock, 0);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text         = title,
            FontSize     = 11,
            FontWeight   = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textStack.Children.Add(new TextBlock
        {
            Text       = subtitle,
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 140, 140, 140)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Status pill
        var pill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(4, 1, 4, 1),
            Background   = new SolidColorBrush(HexColor(statusColor)),
            Margin       = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        pill.Child = new TextBlock
        {
            Text       = statusText,
            FontSize   = 9,
            Foreground = new SolidColorBrush(Colors.White),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        textStack.Children.Add(pill);

        Grid.SetColumn(textStack, 1);
        inner.Children.Add(iconBlock);
        inner.Children.Add(textStack);
        border.Child = inner;

        Canvas.SetLeft(border, x);
        Canvas.SetTop(border,  y);
        canvas.Children.Add(border);
    }

    private void DrawArrow(double x1, double y1, double x2, double y2,
                            string color, string label = "")
    {
        // Main line
        var line = new Line
        {
            X1              = x1, Y1 = y1,
            X2              = x2, Y2 = y2,
            Stroke          = new SolidColorBrush(HexColor(color)),
            StrokeThickness = 2,
        };
        MapCanvas.Children.Add(line);

        // Arrowhead
        double angle  = Math.Atan2(y2 - y1, x2 - x1);
        double arrLen = 10;
        double arrAng = 0.4;
        DrawArrowhead(x2, y2, angle, arrLen, arrAng, color);

        // Label
        if (!string.IsNullOrEmpty(label))
        {
            double midX = (x1 + x2) / 2;
            double midY = (y1 + y2) / 2 - 14;
            var tb = new TextBlock
            {
                Text       = label,
                FontSize   = 9,
                Foreground = new SolidColorBrush(HexColor(color)),
                Opacity    = 0.7
            };
            Canvas.SetLeft(tb, midX - 20);
            Canvas.SetTop(tb,  midY);
            MapCanvas.Children.Add(tb);
        }
    }

    private void DrawDashedArrow(double x1, double y1, double x2, double y2,
                                  string color, string label = "")
    {
        var line = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke          = new SolidColorBrush(HexColor(color)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 5, 3 }
        };
        MapCanvas.Children.Add(line);

        if (!string.IsNullOrEmpty(label))
        {
            var tb = new TextBlock
            {
                Text       = label,
                FontSize   = 9,
                Foreground = new SolidColorBrush(HexColor(color)),
                Opacity    = 0.7
            };
            Canvas.SetLeft(tb, (x1 + x2) / 2 - 20);
            Canvas.SetTop(tb,  y1 - 14);
            MapCanvas.Children.Add(tb);
        }
    }

    private void DrawArrowhead(double x, double y, double angle, double len, double spread, string color)
    {
        var p1 = new Line
        {
            X1 = x, Y1 = y,
            X2 = x - len * Math.Cos(angle - spread),
            Y2 = y - len * Math.Sin(angle - spread),
            Stroke          = new SolidColorBrush(HexColor(color)),
            StrokeThickness = 2
        };
        var p2 = new Line
        {
            X1 = x, Y1 = y,
            X2 = x - len * Math.Cos(angle + spread),
            Y2 = y - len * Math.Sin(angle + spread),
            Stroke          = new SolidColorBrush(HexColor(color)),
            StrokeThickness = 2
        };
        MapCanvas.Children.Add(p1);
        MapCanvas.Children.Add(p2);
    }

    private void DrawLabel(string text, double x, double y, string color)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontSize   = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(HexColor(color))
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb,  y);
        MapCanvas.Children.Add(tb);
    }

    // ── Environment detection ─────────────────────────────────────────────────
    private static bool IsFirewallEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh",
                "advfirewall show allprofiles state")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi);
            string out_ = p?.StandardOutput.ReadToEnd() ?? "";
            return out_.Contains("ON", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static bool IsAdGuardRunning()
    {
        return Process.GetProcesses().Any(p =>
            p.ProcessName.Contains("adguard", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDefaultGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var gw in ni.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    return gw.Address.ToString();
            }
        }
        return "";
    }

    private static string GetDnsServer()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var dns = ni.GetIPProperties().DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .FirstOrDefault();
            if (!string.IsNullOrEmpty(dns)) return dns;
        }
        return "";
    }

    private static string GetAdapterName()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            return ni.Name;
        }
        return "Network Adapter";
    }

    // ── Color helper ─────────────────────────────────────────────────────────
    private static Color HexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            hex.Length == 8 ? Convert.ToByte(hex[..2], 16) : (byte)255,
            Convert.ToByte(hex.Length == 8 ? hex[2..4] : hex[0..2], 16),
            Convert.ToByte(hex.Length == 8 ? hex[4..6] : hex[2..4], 16),
            Convert.ToByte(hex.Length == 8 ? hex[6..8] : hex[4..6], 16));
    }
}

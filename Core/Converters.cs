using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinNetControl.Core;

public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class SpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double speed = value is double d ? d : (value is float f ? f : 0);
        return FormatSpeed(speed);
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();

    public static string FormatSpeed(double kbps)
    {
        if (kbps >= 1024 * 1024) return $"{kbps / (1024.0 * 1024):F2} GB/s";
        if (kbps >= 1024)        return $"{kbps / 1024.0:F1} MB/s";
        return $"{kbps:F1} KB/s";
    }
}

public class SizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        long size = value is long l ? l : (value is int i ? i : 0L);
        if (size < 1024)                  return $"{size} B";
        if (size < 1024 * 1024)           return $"{Math.Round(size / 1024.0, 1)} KB";
        if (size < 1024L * 1024 * 1024)   return $"{Math.Round(size / (1024.0 * 1024), 1)} MB";
        return $"{Math.Round(size / (1024.0 * 1024 * 1024), 2)} GB";
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Returns Visibility.Visible when the bool is true.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Returns Visibility.Collapsed when the bool is true (inverse).</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Collapsed;
}

/// <summary>Returns a colored brush for the connection direction badge.
/// true (Inbound) → orange-amber, false (Outbound) → blue.</summary>
public class BoolToBrushConverter : IValueConverter
{
    private SolidColorBrush? _inboundBrush;
    private SolidColorBrush? _outboundBrush;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b)
            return _inboundBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0x3B, 0x01)); // orange-red
        return _outboundBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)); // blue
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Maps a TCP state string to a background color brush for the state badge.</summary>
public class StateToColorConverter : IValueConverter
{
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(0xCC, r, g, b));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return Normalize(value as string) switch
        {
            "ESTABLISHED"           => Brush(16,  124, 16),   // green
            "LISTEN" or "LISTENING" => Brush(0,   99,  177),  // blue
            "CLOSE_WAIT"            => Brush(176, 124, 0),    // amber
            "TIME_WAIT"             => Brush(136, 136, 0),    // yellow-ish
            "SYN_SENT"              => Brush(0,   153, 153),  // teal
            "FIN_WAIT"              => Brush(100, 100, 100),  // grey
            "INACTIVE"              => Brush(80,  80,  80),   // dark grey
            "N/A"                   => Brush(60,  60,  60),   // UDP
            "OBSERVED"              => Brush(0,   120, 215),  // MS blue
            _                       => Brush(90,  90,  90)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();

    internal static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Numeric TCP states (MIB_TCP_STATE)
        return raw.Trim() switch
        {
            "1"  => "CLOSED",
            "2"  => "LISTEN",
            "3"  => "SYN_SENT",
            "4"  => "SYN_RCVD",
            "5"  => "ESTABLISHED",
            "6"  => "FIN_WAIT",
            "7"  => "CLOSE_WAIT",
            "8"  => "CLOSING",
            "9"  => "LAST_ACK",
            "10" => "TIME_WAIT",
            "11" => "DELETE_TCB",
            "12" => "INACTIVE",
            _    => raw.ToUpperInvariant()
        };
    }
}

/// <summary>Returns a human-readable description of a TCP/UDP state for tooltip display.</summary>
public class StateToDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string norm = StateToColorConverter.Normalize(value as string);
        return norm switch
        {
            "ESTABLISHED"  => "ESTABLISHED — Active data connection open",
            "LISTEN"
            or "LISTENING" => "LISTENING — Port open, waiting for incoming connections",
            "SYN_SENT"     => "SYN_SENT — Connection request sent, awaiting reply",
            "SYN_RCVD"     => "SYN_RCVD — Connection request received, handshake in progress",
            "FIN_WAIT"     => "FIN_WAIT — Connection closing, waiting for remote FIN",
            "CLOSE_WAIT"   => "CLOSE_WAIT — Remote end closed, local close pending",
            "CLOSING"      => "CLOSING — Both sides initiated close simultaneously",
            "LAST_ACK"     => "LAST_ACK — Waiting for final ACK before fully closed",
            "TIME_WAIT"    => "TIME_WAIT — Connection closed, waiting to ensure all packets delivered",
            "CLOSED"       => "CLOSED — Connection fully terminated",
            "DELETE_TCB"   => "DELETE_TCB — TCP control block being deleted",
            "INACTIVE"     => "INACTIVE — No recent activity (UDP / idle)",
            "N/A"          => "N/A — UDP socket (connectionless, no state)",
            "OBSERVED"     => "OBSERVED — Detected via network monitoring (read-only)",
            ""             => "Unknown state",
            _              => norm
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}


/// <summary>
/// Maps a connection count int to a background brush:
/// ≤10 → accent, ≤20 → amber, >20 → red
/// </summary>
public class CountToColorConverter : IValueConverter
{
    private SolidColorBrush? _normalBrush;
    private SolidColorBrush? _warnBrush;
    private SolidColorBrush? _highBrush;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int count = value is int i ? i : 0;
        if (count > 20)
            return _highBrush   ??= new SolidColorBrush(Color.FromArgb(0xFF, 0xD0, 0x20, 0x20));
        if (count > 10)
            return _warnBrush   ??= new SolidColorBrush(Color.FromArgb(0xFF, 0xCA, 0x50, 0x10));
        return _normalBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a 0.0–1.0 ratio to a pixel width for the mini speed bar.
/// Maximum bar width is 60px by default; pass parameter to override.
/// </summary>
public class RatioToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double ratio = value is double d ? d : 0.0;
        double maxWidth = parameter is string s && double.TryParse(s, out double p) ? p : 60.0;
        return Math.Max(2, ratio * maxWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Returns true when value is a non-empty string.</summary>
public class StringToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Returns "ON" when bool is true, "OFF" when false — used for ToggleButton labels.</summary>
public class BoolToOnOffConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? "ON" : "OFF";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is string s && s == "ON";
}

/// <summary>
/// Maps DataUsageTier (0–3) to a colored SolidColorBrush for the usage badge.
/// 0=transparent, 1=green, 2=amber, 3=red
/// </summary>
public class DataUsageTierToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int tier = value is int i ? i : 0;
        return tier switch
        {
            1 => new SolidColorBrush(Color.FromArgb(255, 52, 168, 83)),   // green
            2 => new SolidColorBrush(Color.FromArgb(255, 251, 188, 5)),   // amber
            3 => new SolidColorBrush(Color.FromArgb(255, 234, 67, 53)),   // red
            _ => new SolidColorBrush(Colors.Transparent)
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Collapses the badge when DataUsageTier == 0 (less than 10 MB).</summary>
public class DataUsageTierToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is int i && i > 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Maps IsSuspicious bool to a warning foreground color.</summary>
public class SuspiciousToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b)
            ? new SolidColorBrush(Color.FromArgb(255, 251, 188, 5))
            : new SolidColorBrush(Colors.Transparent);
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>Maps VtStatus to a foreground brush for the VT badge text.</summary>
public class VtStatusToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is WinNetControl.Core.VtStatus status)
        {
            return status switch
            {
                WinNetControl.Core.VtStatus.Clean      => new SolidColorBrush(Color.FromArgb(255, 16,  124, 16)),   // green
                WinNetControl.Core.VtStatus.Suspicious => new SolidColorBrush(Color.FromArgb(255, 220, 150, 0)),    // amber
                WinNetControl.Core.VtStatus.Malicious  => new SolidColorBrush(Color.FromArgb(255, 196, 43,  28)),   // red
                WinNetControl.Core.VtStatus.Checking   => new SolidColorBrush(Color.FromArgb(255, 0,   120, 212)),  // blue
                _                                      => new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),  // gray
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

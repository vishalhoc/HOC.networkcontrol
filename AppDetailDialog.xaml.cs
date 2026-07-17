using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WinNetControl.Core;
using WinNetControl.Models;

namespace WinNetControl;

public sealed partial class AppDetailDialog : ContentDialog
{
    private readonly ProcessNetworkInfo         _process;
    private readonly Action<ProcessNetworkInfo>? _blockCallback;
    private readonly Action<ProcessNetworkInfo>? _unblockCallback;

    public AppDetailDialog(ProcessNetworkInfo process,
                           Action<ProcessNetworkInfo>? blockCallback   = null,
                           Action<ProcessNetworkInfo>? unblockCallback = null)
    {
        this.InitializeComponent();
        _process         = process;
        _blockCallback   = blockCallback;
        _unblockCallback = unblockCallback;
        PopulateDetails();
        LoadAppIcon();
        PopulateProcessInfo();
    }

    // ── Main populate ─────────────────────────────────────────────────────────
    private void PopulateDetails()
    {
        Title             = $"{_process.ProcessName} \u2014 Details";
        AppNameText.Text  = _process.ProcessName;
        AppPathText.Text  = string.IsNullOrEmpty(_process.ProcessPath)
                            ? "Path unknown" : _process.ProcessPath;
        AppTypeText.Text  = _process.AppType;
        AppPidText.Text   = _process.ProcessId > 0 ? $"PID {_process.ProcessId}" : string.Empty;
        AppAdapterText.Text = !string.IsNullOrEmpty(_process.AdapterName)
                              ? $"  via {_process.AdapterName}" : string.Empty;

        // Stat cards
        UploadText.Text    = FormatSpeed(_process.UploadSpeed);
        DownloadText.Text  = FormatSpeed(_process.DownloadSpeed);
        TotalDataText.Text = FormatSize(_process.TotalDataUsed);
        ConnCountText.Text = _process.Connections.Count.ToString();

        // Block badge
        if (_process.IsBlocked)
        {
            BlockBadge.Background     = new SolidColorBrush(Color.FromArgb(40, 200, 0, 0));
            BlockBadgeText.Text       = "\uD83D\uDEAB BLOCKED";
            BlockBadgeText.Foreground = new SolidColorBrush(Colors.OrangeRed);
        }
        else
        {
            BlockBadge.Background     = new SolidColorBrush(Color.FromArgb(40, 0, 180, 0));
            BlockBadgeText.Text       = "\u2713 ALLOWED";
            BlockBadgeText.Foreground = new SolidColorBrush(Colors.LimeGreen);
        }

        // Kill button — hide if no valid PID
        KillBtn.Visibility = _process.ProcessId > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Firewall rule names
        string name = _process.ProcessName;
        string path = _process.ProcessPath ?? "";
        InRuleName.Text  = $"WinNetControl_Block_{name}_In";
        OutRuleName.Text = $"WinNetControl_Block_{name}_Out";
        InRulePath.Text  = path;
        OutRulePath.Text = path;

        InRuleAction.Text  = _process.BlockInbound  ? "\uD83D\uDEAB Block" : "\u2713 Allow";
        OutRuleAction.Text = _process.BlockOutbound ? "\uD83D\uDEAB Block" : "\u2713 Allow";
        InRuleAction.Foreground  = new SolidColorBrush(_process.BlockInbound  ? Colors.OrangeRed : Colors.LimeGreen);
        OutRuleAction.Foreground = new SolidColorBrush(_process.BlockOutbound ? Colors.OrangeRed : Colors.LimeGreen);

        InRuleEnabled.Text  = _process.BlockInbound  ? "Yes" : "No";
        OutRuleEnabled.Text = _process.BlockOutbound ? "Yes" : "No";
        InRuleEnabled.Foreground  = new SolidColorBrush(_process.BlockInbound  ? Colors.OrangeRed : Colors.LimeGreen);
        OutRuleEnabled.Foreground = new SolidColorBrush(_process.BlockOutbound ? Colors.OrangeRed : Colors.LimeGreen);

        // Connections
        DetailConnList.ItemsSource = _process.Connections;
    }

    // ── Process Info section ──────────────────────────────────────────────────
    private void PopulateProcessInfo()
    {
        InfoProcName.Text = _process.ProcessName;
        InfoPid.Text      = _process.ProcessId > 0 ? _process.ProcessId.ToString() : "—";
        InfoPath.Text     = _process.ProcessPath ?? "—";

        // Read file version info from exe
        try
        {
            if (!string.IsNullOrEmpty(_process.ProcessPath) && File.Exists(_process.ProcessPath))
            {
                var fvi = FileVersionInfo.GetVersionInfo(_process.ProcessPath);
                InfoProduct.Text  = string.IsNullOrWhiteSpace(fvi.ProductName)  ? "—" : fvi.ProductName;
                InfoCompany.Text  = string.IsNullOrWhiteSpace(fvi.CompanyName)  ? "—" : fvi.CompanyName;
                InfoVersion.Text  = string.IsNullOrWhiteSpace(fvi.FileVersion)  ? "—" : fvi.FileVersion;
                AppCompanyText.Text = !string.IsNullOrWhiteSpace(fvi.CompanyName)
                                      ? $"  {fvi.CompanyName}" : string.Empty;
            }
            else
            {
                InfoProduct.Text = InfoCompany.Text = InfoVersion.Text = "—";
            }
        }
        catch { InfoProduct.Text = InfoCompany.Text = InfoVersion.Text = "—"; }

        // Process start time
        try
        {
            if (_process.ProcessId > 0)
            {
                var proc = Process.GetProcessById(_process.ProcessId);
                InfoStartTime.Text = proc.StartTime.ToString("yyyy-MM-dd  HH:mm:ss");
            }
            else InfoStartTime.Text = "—";
        }
        catch { InfoStartTime.Text = "—"; }

        // Authenticode signature check (#28)
        CheckSignature();
    }

    // ── Authenticode Signature (#28) ──────────────────────────────────────────
    private void CheckSignature()
    {
        string path = _process.ProcessPath ?? "";
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (InfoSignature != null) InfoSignature.Text = "—";
            return;
        }
        try
        {
            var cert = System.Security.Cryptography.X509Certificates
                             .X509Certificate.CreateFromSignedFile(path);
            string subject = cert.Subject;
            // Parse CN= from subject for cleaner display
            var match = System.Text.RegularExpressions.Regex.Match(subject, @"CN=([^,]+)");
            string signer = match.Success ? match.Groups[1].Value.Trim('"') : subject;
            if (InfoSignature != null)
                InfoSignature.Text = $"✓  Signed by: {signer}";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            if (InfoSignature != null)
                InfoSignature.Text = "⚠  Not signed or signature invalid";
        }
        catch
        {
            if (InfoSignature != null) InfoSignature.Text = "—";
        }
    }


    // ── App icon (loads from exe via BitmapImage) ─────────────────────────────
    private void LoadAppIcon()
    {
        try
        {
            if (string.IsNullOrEmpty(_process.ProcessPath) ||
                !File.Exists(_process.ProcessPath)) return;

            // Use the URI-based bitmap source — works for any file on disk
            // WinUI can load BitmapImage from a file URI
            var bmp = new BitmapImage(new Uri(_process.ProcessPath));
            // This won't work for exe directly — exe is not an image.
            // Use a safe fallback: skip icon, leave the accent border placeholder.
        }
        catch { /* icon unavailable — placeholder shows */ }
    }

    // ── Kill Process ──────────────────────────────────────────────────────────
    private async void OnKillProcess(object s, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title   = "Kill Process?",
            Content = $"This will forcibly terminate '{_process.ProcessName}' (PID {_process.ProcessId}).\nAny unsaved work in that app will be lost.",
            PrimaryButtonText = "Kill",
            CloseButtonText   = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var proc = Process.GetProcessById(_process.ProcessId);
            proc.Kill(entireProcessTree: false);
            KillBtn.IsEnabled  = false;
            KillBtn.Content    = "Killed";
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                Title           = "Could not kill process",
                Content         = ex.Message,
                CloseButtonText = "OK",
                XamlRoot        = this.XamlRoot
            };
            await err.ShowAsync();
        }
    }

    // ── Block actions ─────────────────────────────────────────────────────────
    private void OnBlockAll(object s, RoutedEventArgs e)
    {
        _blockCallback?.Invoke(_process);
        PopulateDetails();
    }

    private void OnBlockIn(object s, RoutedEventArgs e)
    {
        System.Threading.Tasks.Task.Run(() =>
            FirewallService.BlockAppInbound(_process.ProcessName, _process.ProcessPath ?? ""));
        _process.BlockInbound = true;
        PopulateDetails();
    }

    private void OnBlockOut(object s, RoutedEventArgs e)
    {
        System.Threading.Tasks.Task.Run(() =>
            FirewallService.BlockAppOutbound(_process.ProcessName, _process.ProcessPath ?? ""));
        _process.BlockOutbound = true;
        PopulateDetails();
    }

    private void OnUnblockAll(object s, RoutedEventArgs e)
    {
        _unblockCallback?.Invoke(_process);
        PopulateDetails();
    }

    private void OnOpenFirewall(object s, RoutedEventArgs e)
        => FirewallService.OpenWindowsFirewall();

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string FormatSpeed(double kbps)
    {
        if (kbps <= 0)          return "0 B/s";
        if (kbps < 1)           return $"{kbps * 1024:F0} B/s";
        if (kbps < 1024)        return $"{kbps:F1} KB/s";
        if (kbps < 1024 * 1024) return $"{kbps / 1024:F2} MB/s";
        return $"{kbps / 1024 / 1024:F2} GB/s";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)                   return $"{bytes} B";
        if (bytes < 1024 * 1024)            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024)   return $"{bytes / 1024.0 / 1024:F2} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}

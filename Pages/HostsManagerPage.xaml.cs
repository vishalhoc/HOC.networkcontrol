using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public sealed partial class HostsManagerPage : Page
{
    // ── Public service accessor — other pages call this directly ─────────────
    /// <summary>Blocks a domain from any page in the app (e.g. ConnectionManagerPage).</summary>
    public static (bool ok, string error) BlockDomain(string hostname, string sourceApp = "")
        => HostsFileService.BlockDomain(hostname, sourceApp);

    // ── State ─────────────────────────────────────────────────────────────────
    private List<HostsEntry> _allEntries  = new();
    private readonly ObservableCollection<HostsEntry> _view = new();

    public HostsManagerPage()
    {
        this.InitializeComponent();
        EntriesList.ItemsSource = _view;
        HostsPathText.Text = HostsFileService.HostsPath;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadEntries();
    }

    // ── Load ──────────────────────────────────────────────────────────────────
    private void LoadEntries()
    {
        _allEntries = HostsFileService.ReadEntries();
        ApplyFilter();
        EntryCountText.Text = $"{_allEntries.Count(e => !e.IsComment)} active entries";
    }

    private void ApplyFilter()
    {
        // Guard: Checked/Unchecked fires during InitializeComponent before controls are created
        if (SearchBox == null || ShowCommentsCheck == null || ShowDisabledCheck == null) return;

        bool showComments = ShowCommentsCheck.IsChecked == true;
        bool showDisabled = ShowDisabledCheck.IsChecked == true;
        string q = SearchBox.Text.Trim().ToLowerInvariant();

        _view.Clear();
        foreach (var e in _allEntries)
        {
            if (e.IsComment && !showComments) continue;
            if (!e.IsEnabled && !showDisabled) continue;
            if (q.Length > 0 &&
                !e.Ip.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _view.Add(e);
        }
    }

    // ── UI events ─────────────────────────────────────────────────────────────
    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();
    private void OnReload(object sender, RoutedEventArgs e) => LoadEntries();

    private void OnOpenNotepad(object sender, RoutedEventArgs e)
        => HostsFileService.OpenInNotepad();

    private async void OnResetDefault(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title   = "Reset Hosts File?",
            Content = "This will restore the hosts file to Windows defaults. All custom entries will be lost.",
            PrimaryButtonText   = "Reset",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, err) = await Task.Run(() => HostsFileService.ResetToDefault());
        StatusText.Text = ok ? "Hosts file reset to default." : $"Error: {err}";
        LoadEntries();
    }

    private async void OnAddEntry(object sender, RoutedEventArgs e)
    {
        string ip       = AddIpBox.Text.Trim();
        string hostname = AddHostnameBox.Text.Trim();
        string comment  = AddCommentBox.Text.Trim();

        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(hostname))
        {
            StatusText.Text = "IP and Hostname are required."; return;
        }
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            StatusText.Text = "Invalid IP address format."; return;
        }

        StatusText.Text = "Saving…";
        var (ok, err) = await Task.Run(() => HostsFileService.AddEntry(ip, hostname, comment));
        StatusText.Text = ok ? $"Added: {ip} → {hostname}" : $"Error: {err}";
        if (ok)
        {
            AddIpBox.Text = AddHostnameBox.Text = AddCommentBox.Text = "";
            LoadEntries();
        }
    }

    private async void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int lineIndex) return;
        StatusText.Text = "Deleting…";
        var (ok, err) = await Task.Run(() => HostsFileService.RemoveEntry(lineIndex));
        StatusText.Text = ok ? "Entry removed." : $"Error: {err}";
        if (ok) LoadEntries();
    }

    private async void OnToggleEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not HostsEntry entry) return;
        var (ok, err) = await Task.Run(() => HostsFileService.ToggleEntry(entry.LineIndex, toggle.IsOn));
        StatusText.Text = ok
            ? $"{entry.Hostname} {(toggle.IsOn ? "enabled" : "disabled")}."
            : $"Error: {err}";
        if (ok) LoadEntries();
    }

    // ── Block presets ─────────────────────────────────────────────────────────
    private static readonly Dictionary<string, string[]> _presets = new()
    {
        ["ads"] = new[]
        {
            "googleadservices.com", "doubleclick.net", "googlesyndication.com",
            "adnxs.com", "ads.yahoo.com", "advertising.amazon.com",
            "pagead2.googlesyndication.com", "static.ads-twitter.com", "ads.linkedin.com"
        },
        ["telemetry"] = new[]
        {
            "telemetry.microsoft.com", "vortex.data.microsoft.com",
            "settings-win.data.microsoft.com", "watson.telemetry.microsoft.com",
            "oca.telemetry.microsoft.com", "sqm.telemetry.microsoft.com",
            "telecommand.telemetry.microsoft.com", "reports.wes.df.telemetry.microsoft.com"
        },
        ["social"] = new[]
        {
            "www.facebook.com", "facebook.com", "connect.facebook.net",
            "www.twitter.com", "twitter.com", "t.co",
            "www.instagram.com", "instagram.com", "www.tiktok.com", "tiktok.com"
        },
        ["gaming"] = new[]
        {
            "nexus.officeapps.live.com", "survey.medallia.com",
            "datarouter.ol.epicgames.com", "tracking.epicgames.com",
            "metrics.icloud.com", "analytics.steampowered.com"
        }
    };

    private async void OnBlockPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Tag?.ToString() ?? "";
        if (!_presets.TryGetValue(tag, out var domains)) return;

        StatusText.Text = $"Blocking {domains.Length} domains ({tag})…";
        int count = 0;
        await Task.Run(() =>
        {
            foreach (var d in domains)
            {
                var (ok, _) = HostsFileService.BlockDomain(d, "WNC Preset");
                if (ok) count++;
            }
        });
        StatusText.Text = $"Blocked {count}/{domains.Length} domains ({tag} preset). DNS flushed.";
        LoadEntries();
    }

    private async void OnRemoveAllWnc(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title   = "Remove All WNC Entries?",
            Content = "This removes all hosts entries added by WinNetControl.",
            PrimaryButtonText   = "Remove",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var toRemove = _allEntries
            .Where(e => e.Comment.Contains("WinNetControl", StringComparison.OrdinalIgnoreCase)
                     || e.SourceApp.Contains("WNC", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.LineIndex)
            .ToList();

        if (toRemove.Count == 0) { StatusText.Text = "No WNC entries found."; return; }
        var (ok, err) = await Task.Run(() => HostsFileService.RemoveEntries(toRemove));
        StatusText.Text = ok ? $"Removed {toRemove.Count} WNC entries." : $"Error: {err}";
        LoadEntries();
    }
}

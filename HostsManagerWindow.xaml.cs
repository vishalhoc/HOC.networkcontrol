using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinNetControl.Core;
using WinUIEx;

namespace WinNetControl;

public sealed partial class HostsManagerWindow : Window
{
    private List<HostsEntry>                 _allEntries  = new();
    private ObservableCollection<HostsEntry> _displayed   = new();
    private bool _hasUnsavedChanges;

    public HostsManagerWindow()
    {
        this.InitializeComponent();
        try { this.SetIcon("Assets\\AppIcon.ico"); } catch { }

        // Set title — indicate if not elevated (hosts file needs admin)
        bool isAdmin = Core.FirewallService.IsAdministrator();
        this.Title = isAdmin ? "Hosts File Manager" : "Hosts File Manager  (Run as Admin to save changes)";

        // Fixed window size
        this.SetWindowSize(920, 640);

        HostsPathText.Text    = HostsFileService.HostsPath;
        HostsList.ItemsSource = _displayed;

        LoadEntries();
    }

    // ── Load ──────────────────────────────────────────────────────────────────
    private void LoadEntries()
    {
        _allEntries = HostsFileService.ReadEntries();
        _hasUnsavedChanges = false;
        ApplyFilter();
        StatusText.Text = string.Empty;
    }

    private void ApplyFilter()
    {
        string search       = SearchBox?.Text?.ToLowerInvariant() ?? string.Empty;
        bool   showDisabled = ShowDisabledToggle?.IsChecked == true;
        bool   showComments = ShowCommentsToggle?.IsChecked == true;

        _displayed.Clear();
        foreach (var e in _allEntries)
        {
            if (e.IsComment && !showComments)    continue;
            if (!e.IsEnabled && !showDisabled)   continue;
            if (!string.IsNullOrEmpty(search))
            {
                bool match = e.Hostname.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || e.Ip.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || e.Comment.Contains(search, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
            }
            _displayed.Add(e);
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        int total   = _allEntries.Count(e => !e.IsComment);
        int blocked = _allEntries.Count(e => !e.IsComment && e.IsEnabled && e.Ip == "0.0.0.0");
        EntryCountText.Text = $"{_displayed.Count} shown · {total} entries · {blocked} blocked";
    }

    // ── Quick Block ───────────────────────────────────────────────────────────
    private void OnQuickBlock(object sender, RoutedEventArgs e)
    {
        string hostname = QuickHostnameBox.Text.Trim();
        string ip       = QuickIpBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(hostname)) { ShowStatus("Enter a hostname to block.", error: true); return; }
        if (string.IsNullOrWhiteSpace(ip)) ip = "0.0.0.0";

        var entry = new HostsEntry
        {
            Ip        = ip,
            Hostname  = hostname.ToLowerInvariant(),
            Comment   = "WinNetControl",
            IsEnabled = true,
            LineIndex = _allEntries.Count
        };
        _allEntries.Add(entry);
        _hasUnsavedChanges = true;
        QuickHostnameBox.Text = string.Empty;
        ApplyFilter();
        ShowStatus($"✓ '{hostname}' → {ip} added. Click Save to apply.", error: false);
    }

    private void OnQuickBlockKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnQuickBlock(sender, new RoutedEventArgs());
    }

    // ── Add Entry ─────────────────────────────────────────────────────────────
    private async void OnAddEntry(object sender, RoutedEventArgs e)
    {
        var ipBox       = new TextBox { PlaceholderText = "IP address (e.g. 127.0.0.1)", Text = "0.0.0.0" };
        var hostBox     = new TextBox { PlaceholderText = "Hostname (e.g. example.com)", Margin = new Thickness(0,8,0,0) };
        var commentBox  = new TextBox { PlaceholderText = "Comment (optional)",          Margin = new Thickness(0,8,0,0) };
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(ipBox);
        panel.Children.Add(hostBox);
        panel.Children.Add(commentBox);

        var dialog = new ContentDialog
        {
            Title           = "Add Hosts Entry",
            Content         = panel,
            PrimaryButtonText  = "Add",
            CloseButtonText    = "Cancel",
            XamlRoot        = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        string ip   = ipBox.Text.Trim();
        string host = hostBox.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(host))
        { ShowStatus("IP and Hostname are required.", error: true); return; }

        _allEntries.Add(new HostsEntry { Ip = ip, Hostname = host, Comment = commentBox.Text.Trim(), IsEnabled = true, LineIndex = _allEntries.Count });
        _hasUnsavedChanges = true;
        ApplyFilter();
        ShowStatus($"✓ Entry added. Click Save to apply.", false);
    }

    // ── Remove Selected ───────────────────────────────────────────────────────
    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        var selected = HostsList.SelectedItems.OfType<HostsEntry>().ToList();
        if (!selected.Any()) { ShowStatus("No entries selected.", error: true); return; }

        var confirm = new ContentDialog
        {
            Title           = "Remove Entries?",
            Content         = $"Remove {selected.Count} selected entry/entries?",
            PrimaryButtonText  = "Remove",
            CloseButtonText    = "Cancel",
            XamlRoot        = this.Content.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var idxs = selected.Select(e2 => e2.LineIndex).ToHashSet();
        _allEntries.RemoveAll(e2 => idxs.Contains(e2.LineIndex));
        for (int i = 0; i < _allEntries.Count; i++) _allEntries[i].LineIndex = i;
        _hasUnsavedChanges = true;
        ApplyFilter();
        ShowStatus($"✓ {selected.Count} entries removed. Click Save.", false);
    }

    // ── Remove single ─────────────────────────────────────────────────────────
    private async void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is HostsEntry entry)
        {
            var confirm = new ContentDialog
            {
                Title           = "Remove Entry?",
                Content         = $"Remove '{entry.Hostname}' ({entry.Ip})?",
                PrimaryButtonText  = "Remove",
                CloseButtonText    = "Cancel",
                XamlRoot        = this.Content.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            _allEntries.RemoveAll(e2 => e2.LineIndex == entry.LineIndex);
            for (int i = 0; i < _allEntries.Count; i++) _allEntries[i].LineIndex = i;
            _hasUnsavedChanges = true;
            ApplyFilter();
            ShowStatus("✓ Entry removed. Click Save.", false);
        }
    }

    // ── Toggle enable/disable ─────────────────────────────────────────────────
    private void OnToggleEntry(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is HostsEntry entry)
        {
            entry.IsEnabled    = tb.IsChecked == true;
            _hasUnsavedChanges = true;
            ShowStatus("Entry updated — click Save to write to disk.", false);
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────────
    private void OnSaveChanges(object sender, RoutedEventArgs e)
    {
        var (ok, error) = HostsFileService.WriteEntries(_allEntries);
        if (ok)
        {
            _hasUnsavedChanges = false;
            ShowStatus("✓ Hosts file saved and DNS cache flushed.", false);
            LoadEntries();
        }
        else
        {
            ShowStatus($"✗ {error}", true);
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────
    private async void OnResetDefault(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title   = "Reset Hosts File?",
            Content = "This will erase ALL custom entries and restore the Windows default hosts file. Continue?",
            PrimaryButtonText  = "Reset",
            CloseButtonText    = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, error) = HostsFileService.ResetToDefault();
        ShowStatus(ok ? "✓ Hosts file reset to Windows default." : $"✗ {error}", !ok);
        LoadEntries();
    }

    // ── Notepad ───────────────────────────────────────────────────────────────
    private void OnOpenNotepad(object sender, RoutedEventArgs e)
        => HostsFileService.OpenInNotepad();

    // ── Refresh ───────────────────────────────────────────────────────────────
    private void OnRefresh(object sender, RoutedEventArgs e)   => LoadEntries();
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnFilterChanged(object sender, RoutedEventArgs e)      => ApplyFilter();
    private void OnHostsItemClick(object sender, ItemClickEventArgs e)  { }

    // ── Backup (#31) ──────────────────────────────────────────────────────────
    private void OnBackupHosts(object sender, RoutedEventArgs e)
    {
        try
        {
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string stamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dest    = System.IO.Path.Combine(desktop, $"hosts_backup_{stamp}.txt");
            System.IO.File.Copy(HostsFileService.HostsPath, dest, overwrite: true);
            ShowStatus($"✓  Backup saved → {dest}", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus($"Backup failed: {ex.Message}", error: true);
        }
    }

    // ── Restore (#31) ─────────────────────────────────────────────────────────
    private void OnRestoreHosts(object sender, RoutedEventArgs e)
    {
        try
        {
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            // Pick the most-recent hosts backup from Desktop automatically
            var backups = System.IO.Directory.GetFiles(desktop, "hosts_backup_*.txt")
                                             .OrderByDescending(f => f).ToArray();
            if (backups.Length == 0)
            {
                ShowStatus("No backup files found on Desktop (hosts_backup_*.txt).", error: true);
                return;
            }
            string latest = backups[0];
            System.IO.File.Copy(latest, HostsFileService.HostsPath, overwrite: true);
            LoadEntries();
            ShowStatus($"✓  Restored from: {System.IO.Path.GetFileName(latest)}", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus($"Restore failed: {ex.Message}", error: true);
        }
    }

    private void ShowStatus(string msg, bool error)
    {
        StatusText.Text       = msg;
        StatusText.Foreground = error
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WinNetControl.Pages;

public sealed partial class HashcatPage : Page
{
    // ── Paths ────────────────────────────────────────────────────────────────
    private static readonly string AppDataDir   = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinNetControl", "hashcat");
    private static readonly string WordlistsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinNetControl", "wordlists");

    private string HashcatExe => Path.Combine(AppDataDir, "hashcat.exe");
    private bool   HashcatInstalled => File.Exists(HashcatExe);

    // ── Process state ────────────────────────────────────────────────────────
    private Process?               _proc;
    private CancellationTokenSource? _cts;
    private readonly object        _lock = new();
    private int                    _crackedCount;

    // ── HTTP client (shared, lazy to avoid static-init crash) ─────────────────
    private static HttpClient? _httpBacking;
    private static HttpClient Http
    {
        get
        {
            if (_httpBacking == null)
            {
                _httpBacking = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                _httpBacking.DefaultRequestHeaders.UserAgent.ParseAdd("WinNetControl/1.0");
            }
            return _httpBacking;
        }
    }

    public HashcatPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Defer init to Loaded so all XAML controls are fully rendered
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded; // run only once per navigation

        // Wire events AFTER all controls are initialized — avoids mid-XAML-init exceptions
        HashTypeCombo.SelectionChanged    += OnHashTypeChanged;
        AttackModeCombo.SelectionChanged  += OnAttackModeChanged;
        MaskPresetCombo.SelectionChanged  += OnMaskPresetChanged;
        WorkloadSlider.ValueChanged        += OnWorkloadChanged;

        InitPage();
    }

    // Use async void (event-handler pattern) so exceptions surface in the terminal
    private async void InitPage()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(WordlistsDir);
            await InitAsync();
        }
        catch (Exception ex)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                OutputBox.Text += $"\n[ERROR] Page init failed: {ex.GetType().Name}: {ex.Message}\n";
            });
        }
    }

    // ── Initialise ───────────────────────────────────────────────────────────

    private async Task InitAsync()
    {
        try
        {
            // Set welcome message via code (avoids Unicode in XAML attribute)
            DispatcherQueue?.TryEnqueue(() =>
            {
                OutputBox.Text =
                    "[Hashcat - Native Windows GPU Password Cracker]\n" +
                    "No WSL or USB adapter required.\n\n" +
                    "Steps:\n" +
                    "  1. Click 'Download Hashcat' if not installed\n" +
                    "  2. Load a .hc22000 or .cap hash file\n" +
                    "  3. Load or download a wordlist (rockyou.txt recommended)\n" +
                    "  4. Click 'Start Cracking'\n\n" +
                    new string('-', 60) + "\n";
            });

            UpdateCommandPreview();

            if (HashcatInstalled)
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    SetStatus("Hashcat ready", ok: true);
                    SetupBanner.Visibility = Visibility.Collapsed;
                    SetupStatusText.Text   = $"Hashcat installed at: {HashcatExe}";

                    if (string.IsNullOrEmpty(OutputFileBox.Text))
                        OutputFileBox.Text = Path.Combine(AppDataDir, "cracked.txt");

                    RulesDirHint.Text = Path.Combine(AppDataDir, "rules", "best64.rule");
                });

                AppendLine($"[✓] Hashcat found at: {HashcatExe}");
                AppendLine("[ℹ] Click 'GPU Info' to check your GPU devices.");
                AppendLine("");

                // Auto-detect GPU in background
                await RunHashcatAsync("-I", "Detecting GPU devices…", silent: true,
                                      onOutput: line => ParseGpuInfo(line));
            }
            else
            {
                SetStatus("Hashcat not installed", ok: false);
                AppendLine("[!] Hashcat not found.");
                AppendLine("[!] Click 'Download Hashcat' to install it automatically.");
            }
        }
        catch (Exception ex)
        {
            AppendLine($"[ERROR] Init: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Status helpers ────────────────────────────────────────────────────────

    private void SetStatus(string text, bool ok)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                HashcatStatusText.Text = text;
                var color = ok
                    ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x10)
                    : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE8, 0x11, 0x23);
                HashcatStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetStatus error: {ex.Message}");
            }
        });
    }

    private void AppendLine(string line)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                if (OutputBox == null) return;
                OutputBox.Text += line + "\n";
                OutputScrollViewer?.ChangeView(null, double.MaxValue, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppendLine error: {ex.Message}");
            }
        });
    }

    private void ClearOutput()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try { OutputBox.Text = ""; _crackedCount = 0; } catch { }
        });
    }

    // ── Download Hashcat ─────────────────────────────────────────────────────

    private async void OnDownloadHashcat(object sender, RoutedEventArgs e)
    {
        DownloadHashcatBtn.IsEnabled  = false;
        SetupDownloadBtn.IsEnabled    = false;
        DownloadProgressBar.Visibility  = Visibility.Visible;
        DownloadProgressText.Visibility = Visibility.Visible;

        try
        {
            ClearOutput();
            AppendLine("[→] Fetching latest Hashcat release from GitHub…");

            _httpBacking?.DefaultRequestHeaders.UserAgent.Clear();
            string apiUrl = "https://api.github.com/repos/hashcat/hashcat/releases/latest";
            string json   = await Http.GetStringAsync(apiUrl);

            // Parse download URL (look for windows zip)
            var match = Regex.Match(json,
                @"""browser_download_url""\s*:\s*""(https://[^""]+hashcat-[\d.]+\.7z)""");
            if (!match.Success)
            {
                // Fallback to zip
                match = Regex.Match(json,
                    @"""browser_download_url""\s*:\s*""(https://[^""]+hashcat-[\d.]+\.zip)""");
            }

            if (!match.Success)
            {
                AppendLine("[ERROR] Could not find Windows download URL.");
                AppendLine("[HINT]  Download manually from: https://hashcat.net/hashcat/");
                AppendLine($"[HINT]  Extract hashcat.exe to: {AppDataDir}");
                return;
            }

            string downloadUrl = match.Groups[1].Value;
            string fileName    = Path.GetFileName(downloadUrl);
            string localZip    = Path.Combine(Path.GetTempPath(), fileName);

            AppendLine($"[→] Downloading: {downloadUrl}");
            DownloadProgressText.Text = "Downloading…";

            // Download with progress
            using var response = await Http.GetAsync(downloadUrl,
                HttpCompletionOption.ResponseHeadersRead);
            long? total  = response.Content.Headers.ContentLength;
            long  received = 0;

            await using var fs = new FileStream(localZip, FileMode.Create, FileAccess.Write);
            await using var stream = await response.Content.ReadAsStreamAsync();

            byte[] buffer = new byte[81920];
            int    read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total.HasValue)
                {
                    double pct = (double)received / total.Value * 100;
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        DownloadProgressBar.Value = pct;
                        DownloadProgressText.Text = $"Downloading… {received / 1_048_576.0:F1} MB / {total.Value / 1_048_576.0:F1} MB";
                    });
                }
            }
            fs.Close();

            AppendLine($"[✓] Downloaded {received / 1_048_576.0:F1} MB");
            DownloadProgressText.Text = "Extracting…";

            // Extract
            AppendLine("[→] Extracting…");
            if (fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                // Use 7z if available, otherwise prompt
                await Extract7z(localZip);
            }
            else
            {
                await Task.Run(() =>
                {
                    using var zip = ZipFile.OpenRead(localZip);
                    foreach (var entry in zip.Entries)
                    {
                        string dest = Path.Combine(AppDataDir,
                            entry.FullName.Contains('/') || entry.FullName.Contains('\\')
                                ? Path.GetFileName(entry.FullName)
                                : entry.FullName);
                        if (string.IsNullOrEmpty(entry.Name)) continue; // skip dirs
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                });
            }

            // Cleanup
            File.Delete(localZip);
            AppendLine($"[✓] Hashcat installed to: {AppDataDir}");

            DispatcherQueue?.TryEnqueue(() =>
            {
                SetupBanner.Visibility = Visibility.Collapsed;
                SetupStatusText.Text   = $"Hashcat installed at: {HashcatExe}";
                OutputFileBox.Text     = Path.Combine(AppDataDir, "cracked.txt");
                RulesDirHint.Text      = Path.Combine(AppDataDir, "rules", "best64.rule");
            });
            SetStatus("Hashcat ready", ok: true);
            await RunHashcatAsync("-I", "Detecting GPU…", silent: true,
                                  onOutput: line => ParseGpuInfo(line));
        }
        catch (Exception ex)
        {
            AppendLine($"[ERROR] Download failed: {ex.Message}");
            AppendLine("[HINT]  Download manually: https://hashcat.net/hashcat/");
            AppendLine($"[HINT]  Extract hashcat.exe to: {AppDataDir}");
            SetStatus("Download failed", ok: false);
        }
        finally
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                DownloadHashcatBtn.IsEnabled    = true;
                SetupDownloadBtn.IsEnabled      = true;
                DownloadProgressBar.Visibility  = Visibility.Collapsed;
                DownloadProgressText.Visibility = Visibility.Collapsed;
            });
        }
    }

    private async Task Extract7z(string path)
    {
        // Try system 7z first
        string[] sevenZipPaths = [
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        ];
        string? sevenZip = sevenZipPaths.FirstOrDefault(File.Exists);

        if (sevenZip != null)
        {
            var psi = new ProcessStartInfo(sevenZip, $"x \"{path}\" -o\"{AppDataDir}\" -y")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
        }
        else
        {
            AppendLine("[WARN] 7-Zip not found. Please extract manually.");
            AppendLine($"[HINT]  Extract {path} to: {AppDataDir}");
            AppendLine("[HINT]  Or install 7-Zip from https://7-zip.org");
        }
    }

    // ── File pickers ──────────────────────────────────────────────────────────

    private async void OnBrowseHashFile(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(
            [".hc22000", ".cap", ".hccapx", ".txt", ".hash"],
            "Hash / Capture File");
        if (path != null)
        {
            HashFileBox.Text = path;
            // Auto-set output file next to input
            OutputFileBox.Text = Path.Combine(
                Path.GetDirectoryName(path) ?? AppDataDir,
                Path.GetFileNameWithoutExtension(path) + "_cracked.txt");
            UpdateCommandPreview();
        }
    }

    private async void OnBrowseWordlist(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync([".txt", ".dict", ".lst"], "Wordlist File");
        if (path != null) { WordlistBox.Text = path; UpdateCommandPreview(); }
    }

    private async void OnBrowseRules(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync([".rule", ".rules"], "Rules File");
        if (path != null) { RulesBox.Text = path; UpdateCommandPreview(); }
    }

    private async void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var savePicker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName      = "cracked"
        };
        savePicker.FileTypeChoices.Add("Text file", [".txt"]);
        InitializeWithWindow.Initialize(savePicker,
            WindowNative.GetWindowHandle(App.Window));
        var file = await savePicker.PickSaveFileAsync();
        if (file != null) { OutputFileBox.Text = file.Path; UpdateCommandPreview(); }
    }

    private async Task<string?> PickFileAsync(string[] extensions, string title)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.ViewMode = PickerViewMode.List;
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ── Wordlist downloads ────────────────────────────────────────────────────

    private async void OnDownloadRockyou(object sender, RoutedEventArgs e)
        => await DownloadWordlistAsync(
            "https://github.com/brannondorsey/naive-hashcat/releases/download/data/rockyou.txt",
            "rockyou.txt");

    private async void OnDownloadTop1M(object sender, RoutedEventArgs e)
        => await DownloadWordlistAsync(
            "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Passwords/Common-Credentials/10-million-password-list-top-1000000.txt",
            "top-1m.txt");

    private async void OnDownloadCommon(object sender, RoutedEventArgs e)
        => await DownloadWordlistAsync(
            "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Passwords/WiFi-WPA/probable-v2-wpa-top4800.txt",
            "common-wifi.txt");

    private async Task DownloadWordlistAsync(string url, string name)
    {
        string dest = Path.Combine(WordlistsDir, name);
        WordlistDownloadBar.Visibility  = Visibility.Visible;
        WordlistDownloadText.Visibility = Visibility.Visible;
        WordlistDownloadText.Text       = $"Downloading {name}…";
        WordlistDownloadBar.IsIndeterminate = true;

        try
        {
            AppendLine($"[→] Downloading wordlist: {name}");
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            long? total    = response.Content.Headers.ContentLength;
            long  received = 0;

            WordlistDownloadBar.IsIndeterminate = !total.HasValue;

            await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
            await using var stream = await response.Content.ReadAsStreamAsync();
            byte[] buf = new byte[81920];
            int    read;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read));
                received += read;
                if (total.HasValue)
                {
                    double pct = (double)received / total.Value * 100;
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        WordlistDownloadBar.Value   = pct;
                        WordlistDownloadText.Text   = $"{name} — {received / 1_048_576.0:F1} MB";
                    });
                }
            }

            DispatcherQueue?.TryEnqueue(() =>
            {
                WordlistBox.Text = dest;
                WordlistDownloadText.Text = $"[✓] {name} saved → {dest}";
                WordlistDownloadBar.Value = 100;
            });
            AppendLine($"[✓] Wordlist saved: {dest}");
            UpdateCommandPreview();
        }
        catch (Exception ex)
        {
            AppendLine($"[ERROR] Download failed: {ex.Message}");
        }
        finally
        {
            await Task.Delay(2000);
            DispatcherQueue?.TryEnqueue(() =>
            {
                WordlistDownloadBar.Visibility  = Visibility.Collapsed;
                WordlistDownloadText.Visibility = Visibility.Collapsed;
            });
        }
    }

    private void OnOpenWordlistFolder(object sender, RoutedEventArgs e)
        => Process.Start("explorer.exe", WordlistsDir);

    private void OnOpenHashcatFolder(object sender, RoutedEventArgs e)
        => Process.Start("explorer.exe", AppDataDir);

    // ── Convert .cap → .hc22000 ───────────────────────────────────────────────

    private async void OnConvertCap(object sender, RoutedEventArgs e)
    {
        string capFile = HashFileBox.Text.Trim();
        if (string.IsNullOrEmpty(capFile) || !File.Exists(capFile))
        {
            AppendLine("[ERROR] Select a .cap file first.");
            return;
        }
        if (!HashcatInstalled)
        {
            AppendLine("[ERROR] Hashcat not installed. Download first.");
            return;
        }

        string outFile = Path.ChangeExtension(capFile, ".hc22000");
        // hcxpcapngtool is separate; try with hashcat's built-in cap2hccapx or tell user
        string hcxPcap = Path.Combine(AppDataDir, "hcxpcapngtool.exe");
        if (File.Exists(hcxPcap))
        {
            await RunHashcatAsync($"-o \"{outFile}\" \"{capFile}\"",
                $"Converting {capFile} → {outFile}…",
                exePath: hcxPcap);
            HashFileBox.Text = outFile;
        }
        else
        {
            AppendLine("[INFO] hcxpcapngtool not found. Conversion options:");
            AppendLine("[OPT1] Use cap2hccapx (bundled with hashcat) for .hccapx format:");
            string cap2 = Path.Combine(AppDataDir, "cap2hccapx.exe");
            if (File.Exists(cap2))
            {
                string hccapxOut = Path.ChangeExtension(capFile, ".hccapx");
                await RunHashcatAsync($"\"{capFile}\" \"{hccapxOut}\"",
                    $"Converting .cap → .hccapx…", exePath: cap2);
                HashFileBox.Text = hccapxOut;
                AppendLine("[HINT] Use hash type 2500 for .hccapx files.");
            }
            else
            {
                AppendLine("[OPT2] Use hashcat-utils online: https://hashcat.net/cap2hashcat/");
                AppendLine("[OPT3] Upload .cap file to: https://hashcat.net/cap2hashcat/");
                Process.Start(new ProcessStartInfo("https://hashcat.net/cap2hashcat/") { UseShellExecute = true });
            }
        }
    }

    // ── Cracking ─────────────────────────────────────────────────────────────

    private async void OnStartCracking(object sender, RoutedEventArgs e)
    {
        if (!HashcatInstalled)
        {
            AppendLine("[ERROR] Hashcat not installed. Click 'Download Hashcat' first.");
            return;
        }

        string hashFile  = HashFileBox.Text.Trim();
        string wordlist  = WordlistBox.Text.Trim();
        string outputFile = OutputFileBox.Text.Trim();
        string session   = SessionBox.Text.Trim();

        if (string.IsNullOrEmpty(hashFile))  { AppendLine("[ERROR] Select a hash/capture file."); return; }

        // Build hashcat arguments
        string hashType = ((ComboBoxItem?)HashTypeCombo.SelectedItem)?.Tag?.ToString() ?? "22000";
        string attack   = ((ComboBoxItem?)AttackModeCombo.SelectedItem)?.Tag?.ToString() ?? "0";
        string device   = ((ComboBoxItem?)DeviceCombo.SelectedItem)?.Tag?.ToString() ?? "";
        int    workload = (int)WorkloadSlider.Value;

        string rules = RulesBox.Text.Trim();
        string mask  = GetCurrentMask();

        var args = new StringBuilder();
        args.Append($"-m {hashType} -a {attack}");
        args.Append($" \"{hashFile}\"");

        if (attack is "0" or "6")
        {
            if (string.IsNullOrEmpty(wordlist)) { AppendLine("[ERROR] Select a wordlist for dictionary attack."); return; }
            args.Append($" \"{wordlist}\"");
        }
        if (attack is "3" or "7")
        {
            if (!string.IsNullOrEmpty(mask)) args.Append($" {mask}");
        }
        if (attack is "1")
        {
            if (string.IsNullOrEmpty(wordlist)) { AppendLine("[ERROR] Select a wordlist."); return; }
            args.Append($" \"{wordlist}\" \"{wordlist}\"");  // combinator needs 2
        }

        if (!string.IsNullOrEmpty(rules) && attack == "0")
            args.Append($" -r \"{rules}\"");
        if (!string.IsNullOrEmpty(device))
            args.Append($" {device}");

        args.Append($" -w {workload}");
        args.Append($" --status --status-timer=3");  // live status every 3s
        args.Append($" --potfile-path \"{Path.Combine(AppDataDir, session + ".potfile")}\"");

        if (!string.IsNullOrEmpty(outputFile))
            args.Append($" -o \"{outputFile}\"");

        if (!string.IsNullOrEmpty(session))
            args.Append($" --session {session}");

        // GPU force-show status
        args.Append(" --force"); // remove if user wants strict mode

        ClearOutput();
        _crackedCount = 0;
        DispatcherQueue?.TryEnqueue(() =>
        {
            RunningBadge.Visibility = Visibility.Visible;
            CrackProgressBar.Value  = 0;
            SpeedText.Text   = "—";
            ProgressText.Text = "—";
            EtaText.Text     = "—";
            ElapsedText.Text = "—";
            StartBtn.IsEnabled = false;
        });

        await RunHashcatAsync(args.ToString(), "Starting crack…",
                              onOutput: ParseHashcatOutput);

        DispatcherQueue?.TryEnqueue(() =>
        {
            RunningBadge.Visibility = Visibility.Collapsed;
            StartBtn.IsEnabled = true;
        });

        // Show cracked results
        await ShowCrackedFromFileAsync(outputFile);
    }

    private void OnPauseCracking(object sender, RoutedEventArgs e)
    {
        lock (_lock)
        {
            if (_proc is { HasExited: false })
            {
                _proc.StandardInput.Write("p");
                AppendLine("[→] Pause signal sent (p)");
            }
        }
    }

    private void OnResumeCracking(object sender, RoutedEventArgs e)
    {
        lock (_lock)
        {
            if (_proc is { HasExited: false })
            {
                _proc.StandardInput.Write("r");
                AppendLine("[→] Resume signal sent (r)");
            }
            else
            {
                // Resume from session
                string session = SessionBox.Text.Trim();
                if (!string.IsNullOrEmpty(session))
                    _ = RunHashcatAsync($"--session {session} --restore",
                        "Resuming from session…", onOutput: ParseHashcatOutput);
            }
        }
    }

    private void OnStopCracking(object sender, RoutedEventArgs e)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            if (_proc is { HasExited: false })
            {
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        AppendLine("[→] Hashcat stopped.");
        DispatcherQueue?.TryEnqueue(() =>
        {
            RunningBadge.Visibility = Visibility.Collapsed;
            StartBtn.IsEnabled = true;
        });
    }

    // ── GPU info ─────────────────────────────────────────────────────────────

    private async void OnCheckGpu(object sender, RoutedEventArgs e)
    {
        if (!HashcatInstalled) { AppendLine("[ERROR] Hashcat not installed."); return; }
        ClearOutput();
        await RunHashcatAsync("-I", "Detecting GPU devices…",
                              onOutput: line => ParseGpuInfo(line));
    }

    private void ParseGpuInfo(string line)
    {
        if (line.Contains("Device #") || line.Contains("Type") || line.Contains("Name"))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                GpuBadge.Visibility = Visibility.Visible;
                if (line.Contains("Name"))
                    GpuBadgeText.Text = line.Split(':').LastOrDefault()?.Trim() ?? "GPU Ready";
            });
        }
    }

    // ── Show cracked passwords ────────────────────────────────────────────────

    private async void OnShowCracked(object sender, RoutedEventArgs e)
        => await ShowCrackedFromFileAsync(OutputFileBox.Text.Trim());

    private async Task ShowCrackedFromFileAsync(string outputFile)
    {
        if (string.IsNullOrEmpty(outputFile) || !File.Exists(outputFile)) return;

        string[] lines = await File.ReadAllLinesAsync(outputFile);
        if (lines.Length == 0) return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            CrackedPanel.Visibility = Visibility.Visible;
            CrackedBadge.Visibility = Visibility.Visible;
            CrackedCountText.Text   = $"{lines.Length} cracked";
            CrackedBox.Text         = string.Join("\n", lines);
        });

        AppendLine($"[✓] {lines.Length} password(s) cracked!");
        foreach (var l in lines)
            AppendLine($"    → {l}");
    }

    private void OnCopyCracked(object sender, RoutedEventArgs e)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(CrackedBox.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private async void OnSaveCracked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName      = "cracked_passwords"
        };
        picker.FileTypeChoices.Add("Text file", [".txt"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSaveFileAsync();
        if (file != null)
            await File.WriteAllTextAsync(file.Path, CrackedBox.Text);
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(OutputBox.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void OnClearOutput(object sender, RoutedEventArgs e)
        => ClearOutput();

    // ── Parse hashcat real-time output ───────────────────────────────────────

    private void ParseHashcatOutput(string line)
    {
        // Speed: "Speed.#1.........:   123.4 MH/s"
        var speedMatch = Regex.Match(line, @"Speed\.#\d+\.*:\s+(.+)");
        if (speedMatch.Success)
        {
            string speed = speedMatch.Groups[1].Value.Trim();
            DispatcherQueue?.TryEnqueue(() => SpeedText.Text = speed);
        }

        // Progress: "Progress.........: 1234567/14344384 (8.60%)"
        var progMatch = Regex.Match(line, @"Progress\.*:\s+[\d/]+\s+\((.+?)\)");
        if (progMatch.Success && double.TryParse(
            progMatch.Groups[1].Value.Replace("%", "").Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double pct))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                ProgressText.Text      = $"{pct:F1}%";
                CrackProgressBar.Value = pct;
            });
        }

        // ETA: "ETA.......: 0 secs"
        var etaMatch = Regex.Match(line, @"ETA\.*:\s+(.+)");
        if (etaMatch.Success)
            DispatcherQueue?.TryEnqueue(() => EtaText.Text = etaMatch.Groups[1].Value.Trim());

        // Elapsed: "Time.Estimated...: ..."  or "Time.Started"
        var elapsedMatch = Regex.Match(line, @"Time\.Started\.*:\s+.+\((.+?) passed\)");
        if (elapsedMatch.Success)
            DispatcherQueue?.TryEnqueue(() => ElapsedText.Text = elapsedMatch.Groups[1].Value.Trim());

        // Cracked password line: "HASH:password" or "ESSID:password"
        if (line.Contains(":") && !line.StartsWith("[") && !line.StartsWith("Session")
            && !line.StartsWith("Status") && !line.StartsWith("Hash")
            && !line.StartsWith("Time") && !line.StartsWith("Speed")
            && !line.StartsWith("Progress") && !line.StartsWith("Restored")
            && !line.StartsWith("ETA") && !line.StartsWith("Recovered")
            && !line.StartsWith("Guess") && !line.StartsWith("Device"))
        {
            _crackedCount++;
            DispatcherQueue?.TryEnqueue(() =>
            {
                CrackedPanel.Visibility = Visibility.Visible;
                CrackedBadge.Visibility = Visibility.Visible;
                CrackedCountText.Text   = $"{_crackedCount} cracked";
                CrackedBox.Text        += line + "\n";
            });
        }
    }

    // ── Command preview ───────────────────────────────────────────────────────

    private void UpdateCommandPreview()
    {
        // Guard: only update when all controls are ready
        if (CommandPreviewBox == null) return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                if (CommandPreviewBox == null) return;
                string hashType = ((ComboBoxItem?)HashTypeCombo?.SelectedItem)?.Tag?.ToString() ?? "22000";
                string attack   = ((ComboBoxItem?)AttackModeCombo?.SelectedItem)?.Tag?.ToString() ?? "0";
                string hashFile = HashFileBox?.Text.Trim() is { Length: > 0 } h ? $"\"{h}\"" : "<hash_file>";
                string wordlist = WordlistBox?.Text.Trim() is { Length: > 0 } w ? $"\"{w}\"" : "<wordlist>";
                int    workload = (int)(WorkloadSlider?.Value ?? 2);
                string outFile  = OutputFileBox?.Text.Trim() is { Length: > 0 } o ? $"\"{o}\"" : "cracked.txt";
                string rules    = RulesBox?.Text.Trim() is { Length: > 0 } r ? $" -r \"{r}\"" : "";

                CommandPreviewBox.Text =
                    $"hashcat -m {hashType} -a {attack} {hashFile} {wordlist}{rules} -w {workload} -o {outFile} --status";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateCommandPreview error: {ex.Message}");
            }
        });
    }

    private void OnHashTypeChanged(object s, SelectionChangedEventArgs e)    => UpdateCommandPreview();
    private void OnAttackModeChanged(object s, SelectionChangedEventArgs e)
    {
        string attack = ((ComboBoxItem?)AttackModeCombo.SelectedItem)?.Tag?.ToString() ?? "0";
        MaskPanel.Visibility = (attack == "3" || attack == "7")
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommandPreview();
    }
    private void OnWorkloadChanged(object s,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        string[] labels = ["", "1-Low", "2-Default", "3-High", "4-Nightmare"];
        int v = (int)WorkloadSlider.Value;
        WorkloadRun.Text = labels[Math.Clamp(v, 1, 4)];
        UpdateCommandPreview();
    }

    private void OnMaskPresetChanged(object s, SelectionChangedEventArgs e)
    {
        string? tag = ((ComboBoxItem?)MaskPresetCombo.SelectedItem)?.Tag?.ToString();
        CustomMaskBox.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommandPreview();
    }

    private string GetCurrentMask()
    {
        string? preset = ((ComboBoxItem?)MaskPresetCombo?.SelectedItem)?.Tag?.ToString();
        if (preset == "custom") return CustomMaskBox?.Text.Trim() ?? "";
        return preset ?? "?d?d?d?d?d?d?d?d";
    }

    // ── Core runner ───────────────────────────────────────────────────────────

    private async Task RunHashcatAsync(
        string arguments, string statusMessage,
        bool silent           = false,
        string? exePath       = null,
        Action<string>? onOutput = null)
    {
        exePath ??= HashcatExe;

        if (!File.Exists(exePath))
        {
            AppendLine($"[ERROR] Executable not found: {exePath}");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_lock) { _cts?.Cancel(); _cts = cts; }

        if (!silent)
        {
            AppendLine($"[→] {statusMessage}");
            AppendLine($"[CMD] hashcat {arguments}");
            AppendLine(new string('─', 60));
        }

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(exePath, arguments)
            {
                WorkingDirectory       = AppDataDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };

            proc = new Process { StartInfo = psi };
            lock (_lock) _proc = proc;

            proc.Start();

            var stdoutTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null)
                    {
                        string captured = line;
                        onOutput?.Invoke(captured);
                        if (!silent)
                            DispatcherQueue?.TryEnqueue(() => AppendLine(captured));
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });

            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync(cts.Token)) != null)
                    {
                        string captured = "[stderr] " + line;
                        if (!silent)
                            DispatcherQueue?.TryEnqueue(() => AppendLine(captured));
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });

            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cts.Token);

            int exit = proc.ExitCode;
            if (!silent)
            {
                AppendLine(new string('─', 60));
                if (exit == 0)
                    AppendLine("[DONE] Hashcat completed successfully.");
                else if (exit == 1)
                    AppendLine("[DONE] Exhausted — hash not found in wordlist.");
                else
                    AppendLine($"[DONE] Process exited with code {exit}.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLine("[→] Cancelled.");
        }
        catch (Exception ex)
        {
            AppendLine($"[ERROR] {ex.Message}");
        }
        finally
        {
            try { proc?.Dispose(); } catch { }
        }
    }
}

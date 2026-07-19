using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace WinNetControl.Pages;

public sealed partial class TerminalPage : Page
{
    private bool _running;
    private bool _usePowershell;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    public TerminalPage() => this.InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) { base.OnNavigatedTo(e); }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
    }

    // ── Shell toggle ──────────────────────────────────────────────────────────
    private void OnShellChanged(object sender, RoutedEventArgs e)
    {
        // Guard: Checked fires during InitializeComponent before PromptLabel is created
        if (PromptLabel == null) return;
        _usePowershell = ShellPs.IsChecked == true;
        PromptLabel.Text = _usePowershell ? "PS>" : "C:\\>";
    }

    // ── Keyboard input ────────────────────────────────────────────────────────
    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OnRunCommand(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            if (_history.Count > 0 && _historyIndex < _history.Count - 1)
            {
                _historyIndex++;
                InputBox.Text = _history[_history.Count - 1 - _historyIndex];
                InputBox.SelectionStart = InputBox.Text.Length;
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down)
        {
            if (_historyIndex > 0)
            {
                _historyIndex--;
                InputBox.Text = _history[_history.Count - 1 - _historyIndex];
            }
            else
            {
                _historyIndex = -1;
                InputBox.Text = "";
            }
            InputBox.SelectionStart = InputBox.Text.Length;
            e.Handled = true;
        }
    }

    // ── Quick commands ────────────────────────────────────────────────────────
    private void OnQuickCmd(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cmd)
        {
            InputBox.Text = cmd;
            OnRunCommand(sender, new RoutedEventArgs());
        }
    }

    // ── Run command ───────────────────────────────────────────────────────────
    private async void OnRunCommand(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        string cmd = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(cmd)) return;

        // History
        _history.Add(cmd);
        _historyIndex = -1;
        InputBox.Text = "";

        string prompt = _usePowershell ? "PS>" : "C:\\>";
        AppendLine($"\n{prompt} {cmd}", isPrompt: true);

        _running = true;
        RunIcon.Glyph   = "\uE769";
        RunBtnText.Text = "Running…";

        try
        {
            string exe  = _usePowershell ? "powershell.exe" : "cmd.exe";
            string args = _usePowershell ? $"-NoProfile -NonInteractive -Command \"{cmd.Replace("\"", "\\\"")}\"" : $"/c {cmd}";

            string output = await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding  = System.Text.Encoding.UTF8
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string o  = proc.StandardOutput.ReadToEnd();
                string er = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return o + (er.Length > 0 ? $"\n[stderr]\n{er}" : "");
            });

            AppendLine(output.TrimEnd());
        }
        catch (Exception ex)
        {
            AppendLine($"[Error] {ex.Message}");
        }
        finally
        {
            _running = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                RunIcon.Glyph   = "\uE768";
                RunBtnText.Text = "Run";
            });
        }
    }

    // ── Output helpers ────────────────────────────────────────────────────────
    private const int MaxTerminalChars = 50_000;
    private const int TrimAmount       = 10_000;

    private void AppendLine(string text, bool isPrompt = false)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string next = OutputText.Text + text + "\n";

            // UX-16: trim oldest content when buffer exceeds cap
            if (next.Length > MaxTerminalChars)
            {
                int cutAt = next.IndexOf('\n', TrimAmount);
                next = cutAt > 0
                    ? "… [older output trimmed] …\n" + next[(cutAt + 1)..]
                    : next[^MaxTerminalChars..];
            }

            OutputText.Text = next;
            _ = OutputScroller.ChangeView(null, OutputScroller.ScrollableHeight, null);
        });
    }

    private void OnClearTerminal(object sender, RoutedEventArgs e)
    {
        OutputText.Text = "Windows Network Control — Built-in Terminal\nReady.\n";
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(OutputText.Text);
        Clipboard.SetContent(dp);
    }
}

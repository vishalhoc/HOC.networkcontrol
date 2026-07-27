using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public class PortResult
{
    public int    Port      { get; set; }
    public string State     { get; set; } = "";
    public string Service   { get; set; } = "";
    public string ResponseMs{ get; set; } = "";
    public SolidColorBrush StateBrush => new(State == "Open"
        ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
        : State == "Filtered"
            ? Windows.UI.Color.FromArgb(255, 251, 188, 5)
            : Windows.UI.Color.FromArgb(255, 160, 160, 160));
}

public sealed partial class PortScannerPage : Page
{
    private bool _scanning;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<PortResult> _results = new();

    // Common service names
    private static readonly System.Collections.Generic.Dictionary<int, string> Services = new()
    {
        {21,"FTP"},{22,"SSH"},{23,"Telnet"},{25,"SMTP"},{53,"DNS"},
        {80,"HTTP"},{110,"POP3"},{143,"IMAP"},{443,"HTTPS"},{445,"SMB"},
        {3306,"MySQL"},{3389,"RDP"},{5900,"VNC"},{8080,"HTTP-Alt"},
        {8443,"HTTPS-Alt"},{27017,"MongoDB"},{5432,"PostgreSQL"},
        {1433,"MSSQL"},{6379,"Redis"},{9200,"Elasticsearch"}
    };

    public PortScannerPage()
    {
        this.InitializeComponent();
        ResultsList.ItemsSource = _results;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { base.OnNavigatedTo(e); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { base.OnNavigatedFrom(e); _cts?.Cancel(); }

    private async void OnScanPorts(object sender, RoutedEventArgs e)
    {
        if (_scanning) { _cts?.Cancel(); return; }

        string host    = TargetBox.Text.Trim();
        int    fromP   = (int)FromPort.Value;
        int    toP     = (int)ToPort.Value;
        int    timeout = (int)TimeoutMs.Value;

        if (string.IsNullOrEmpty(host) || fromP > toP) return;

        _scanning = true;
        ScanBtnText.Text = "Stop";
        _results.Clear();
        StatusText.Text = $"Scanning {host}:{fromP}-{toP}…";
        ScanProgress.IsIndeterminate = false;
        ScanProgress.Value = 0;
        ScanProgress.Visibility = Visibility.Visible;
        OpenCount.Text  = "0 open";

        // FIX Bug#24: dispose the previous CTS before allocating a new one.
        // The old IsCancellationRequested state was carried over between scans
        // and tasks that checked it would exit prematurely on the next scan.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        int total = toP - fromP + 1;
        int done  = 0;
        int open  = 0;

        using var semaphore = new SemaphoreSlim(200);

        var tasks = Enumerable.Range(fromP, total).Select(async port =>
        {
            await semaphore.WaitAsync(_cts.Token);
            try
            {
                if (_cts.Token.IsCancellationRequested) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string state;
                try
                {
                    using var tcp = new TcpClient();
                    var conn = tcp.ConnectAsync(host, port);
                    if (await Task.WhenAny(conn, Task.Delay(timeout, _cts.Token)) == conn && !conn.IsFaulted)
                        state = "Open";
                    else
                        state = ShowClosed.IsChecked == true ? "Closed" : null!;
                }
                catch { state = ShowClosed.IsChecked == true ? "Filtered" : null!; }
                sw.Stop();

                bool addResult = state != null;

                PortResult? result = null;
                if (addResult)
                {
                    result = new PortResult
                    {
                        Port       = port,
                        State      = state!,
                        Service    = Services.TryGetValue(port, out string? svc) ? svc : "—",
                        ResponseMs = state == "Open" ? $"{sw.ElapsedMilliseconds} ms" : "—"
                    };
                    if (state == "Open") Interlocked.Increment(ref open);
                }

                int d = Interlocked.Increment(ref done);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (result != null)
                    {
                        _results.Add(result);
                        OpenCount.Text = $"{open} open";
                    }
                    ScanProgress.Value = (double)d / total * 100;
                    StatusText.Text = $"{d}/{total} ports scanned — {open} open";
                });
            }
            finally { semaphore.Release(); }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { StatusText.Text = "Scan stopped."; }
        finally
        {
            _scanning = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                ScanBtnText.Text = "Scan Ports";
                ScanProgress.Visibility = Visibility.Collapsed;
                // Sort: open first, then by port number
                var sorted = _results.OrderBy(r => r.State != "Open").ThenBy(r => r.Port).ToList();
                _results.Clear();
                foreach (var r in sorted) _results.Add(r);
            });
        }
    }

    private void OnPresetRange(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var parts = btn.Tag?.ToString()?.Split('-');
            if (parts?.Length == 2)
            {
                FromPort.Value = int.Parse(parts[0]);
                ToPort.Value   = int.Parse(parts[1]);
            }
        }
    }
}

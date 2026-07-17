using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace WinNetControl.Core;

public class NetworkSpeedMonitorService : IDisposable
{
    private TraceEventSession? _session;
    private volatile bool _isRunning;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    
    // PID -> (UploadBytes, DownloadBytes)
    private readonly ConcurrentDictionary<int, (long Upload, long Download)> _currentIntervalData = new();
    
    // PID -> (TotalUpload, TotalDownload)
    private readonly ConcurrentDictionary<int, (long Upload, long Download)> _totalData = new();

    // ConnectionKey -> (UploadBytes, DownloadBytes)
    private readonly ConcurrentDictionary<string, (long Upload, long Download)> _currentConnectionIntervalData = new();
    
    // ConnectionKey -> (TotalUpload, TotalDownload)
    private readonly ConcurrentDictionary<string, (long Upload, long Download)> _totalConnectionData = new();

    public event Action<Dictionary<int, NetworkSpeedInfo>, Dictionary<string, NetworkSpeedInfo>>? OnSpeedUpdated;

    public void StartMonitoring()
    {
        if (_isRunning) return;
        _isRunning = true;

        Task.Run(() =>
        {
            try
            {
                // Kill any stale session from a previous crash (same name = Access Denied)
                try { TraceEventSession.GetActiveSession("WinNetControl_NetworkMonitor")?.Stop(noThrow: true); } catch { }

                using (_session = new TraceEventSession("WinNetControl_NetworkMonitor"))
                {
                    _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                    
                    _session.Source.Kernel.TcpIpSend += data =>
                    {
                        AddTraffic(data.ProcessID, "TCP", data.saddr.ToString(), data.sport, data.daddr.ToString(), data.dport, data.size, true);
                    };
                    
                    _session.Source.Kernel.TcpIpRecv += data =>
                    {
                        AddTraffic(data.ProcessID, "TCP", data.daddr.ToString(), data.dport, data.saddr.ToString(), data.sport, data.size, false);
                    };

                    _session.Source.Kernel.UdpIpSend += data =>
                    {
                        AddTraffic(data.ProcessID, "UDP", data.saddr.ToString(), data.sport, data.daddr.ToString(), data.dport, data.size, true);
                    };
                    
                    _session.Source.Kernel.UdpIpRecv += data =>
                    {
                        AddTraffic(data.ProcessID, "UDP", data.daddr.ToString(), data.dport, data.saddr.ToString(), data.sport, data.size, false);
                    };

                    _session.Source.Kernel.TcpIpSendIPV6 += data =>
                    {
                        AddTraffic(data.ProcessID, "TCPv6", data.saddr.ToString(), data.sport, data.daddr.ToString(), data.dport, data.size, true);
                    };
                    
                    _session.Source.Kernel.TcpIpRecvIPV6 += data =>
                    {
                        AddTraffic(data.ProcessID, "TCPv6", data.daddr.ToString(), data.dport, data.saddr.ToString(), data.sport, data.size, false);
                    };

                    _session.Source.Kernel.UdpIpSendIPV6 += data =>
                    {
                        AddTraffic(data.ProcessID, "UDPv6", data.saddr.ToString(), data.sport, data.daddr.ToString(), data.dport, data.size, true);
                    };
                    
                    _session.Source.Kernel.UdpIpRecvIPV6 += data =>
                    {
                        AddTraffic(data.ProcessID, "UDPv6", data.daddr.ToString(), data.dport, data.saddr.ToString(), data.sport, data.size, false);
                    };

                    _session.Source.Process(); // Blocking call
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("network_error.log", $"[ETW] {DateTime.Now}: {ex.Message}\n{ex.StackTrace}\n"); } catch {}
                Console.WriteLine($"ETW Session Error: {ex.Message}");
            }
        });

        // Publisher loop
        Task.Run(async () =>
        {
            var token = _cts.Token;
            while (_isRunning && !token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { break; }
                if (_isRunning) PublishSpeedInfo();
            }
        });
    }

    private void AddTraffic(int pid, string protocol, string localIp, int localPort, string remoteIp, int remotePort, int size, bool isUpload)
    {
        if (pid <= 0) return;

        _currentIntervalData.AddOrUpdate(pid,
            addValueFactory: p => isUpload ? (size, 0) : (0, size),
            updateValueFactory: (p, existing) => 
                isUpload ? (existing.Upload + size, existing.Download) : (existing.Upload, existing.Download + size));

        _totalData.AddOrUpdate(pid,
            addValueFactory: p => isUpload ? (size, 0) : (0, size),
            updateValueFactory: (p, existing) => 
                isUpload ? (existing.Upload + size, existing.Download) : (existing.Upload, existing.Download + size));

        string connKey = $"{pid}|{protocol}|{localIp}:{localPort}|{remoteIp}:{remotePort}";
        _currentConnectionIntervalData.AddOrUpdate(connKey,
            addValueFactory: k => isUpload ? (size, 0) : (0, size),
            updateValueFactory: (k, existing) =>
                isUpload ? (existing.Upload + size, existing.Download) : (existing.Upload, existing.Download + size));
                
        _totalConnectionData.AddOrUpdate(connKey,
            addValueFactory: k => isUpload ? (size, 0) : (0, size),
            updateValueFactory: (k, existing) =>
                isUpload ? (existing.Upload + size, existing.Download) : (existing.Upload, existing.Download + size));
    }

    private void PublishSpeedInfo()
    {
        var result = new Dictionary<int, NetworkSpeedInfo>();
        var connResult = new Dictionary<string, NetworkSpeedInfo>();

        foreach (var kvp in _currentIntervalData)
        {
            var pid = kvp.Key;
            var intervalTraffic = kvp.Value;
            var totalTraffic = _totalData.GetValueOrDefault(pid);

            result[pid] = new NetworkSpeedInfo
            {
                UploadSpeedKBps = intervalTraffic.Upload / 1024.0,
                DownloadSpeedKBps = intervalTraffic.Download / 1024.0,
                TotalUploadBytes = totalTraffic.Upload,
                TotalDownloadBytes = totalTraffic.Download
            };
        }

        foreach (var kvp in _currentConnectionIntervalData)
        {
            var key = kvp.Key;
            var intervalTraffic = kvp.Value;
            var totalTraffic = _totalConnectionData.GetValueOrDefault(key);

            connResult[key] = new NetworkSpeedInfo
            {
                UploadSpeedKBps = intervalTraffic.Upload / 1024.0,
                DownloadSpeedKBps = intervalTraffic.Download / 1024.0,
                TotalUploadBytes = totalTraffic.Upload,
                TotalDownloadBytes = totalTraffic.Download
            };
        }

        // Reset interval data
        _currentIntervalData.Clear();
        _currentConnectionIntervalData.Clear();

        OnSpeedUpdated?.Invoke(result, connResult);
    }

    /// <summary>
    /// Clears all accumulated totals and interval data. Call after ResetAllData in the ViewModel
    /// so that the ETW accumulator is fully flushed and speeds don't snap back to old values.
    /// </summary>
    public void ResetTotals()
    {
        _currentIntervalData.Clear();
        _totalData.Clear();
        _currentConnectionIntervalData.Clear();
        _totalConnectionData.Clear();
    }

    public void StopMonitoring()
    {
        _isRunning = false;
        _cts.Cancel();
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        StopMonitoring();
        _cts.Dispose();
    }
}


public class NetworkSpeedInfo
{
    public double UploadSpeedKBps { get; set; }
    public double DownloadSpeedKBps { get; set; }
    public long TotalUploadBytes { get; set; }
    public long TotalDownloadBytes { get; set; }
}
